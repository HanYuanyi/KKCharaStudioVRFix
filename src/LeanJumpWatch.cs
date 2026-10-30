#if JUMPWATCH
using System;
using System.IO;
using System.Text;
using UnityEngine;
using VRGIN.Core;

namespace KKCharaStudioVRFix
{
 /// <summary>
 /// 【精简版专用】跳变检测 —— 只做这一件事，常驻。
 ///
 /// 为什么单独写一个类：精简版里 `FixLog.*` 全被 `[Conditional("DIAG")]` 编译掉了，
 /// 一个字符都不会进 DLL。但用户需要"精简版偶发跳变时能留下证据"，
 /// 所以这里**独立写文件**，不经过 FixLog、不依赖任何配置开关。
 ///
 /// ★ 不刷屏的三道保险：
 /// ① **不走 BepInEx 控制台**（不调 Logger、不调 FixLog）→ 游戏里和 LogOutput.log 里都看不到；
 /// ② 正常时**一行都不写**，只有真的出现异常才写；
 /// ③ origin 跳变要求「**0.25 秒内位移 > 1 米**」（正常拖动世界是平滑的，0.25 秒挪不了 1 米），
 /// 而且同一类事件最多每 2 秒记一行；全文件最多 500 行，超了就自己停。
 ///
 /// 日志文件：`BepInEx/KKCharaStudioVRFix_jump.log`
 /// </summary>
 /// <remarks>
 /// ★★ 将来怎么删干净（三步，不留残渣）：
 /// 1. 删掉本文件 LeanJumpWatch.cs；
 /// 2. 在 VRRecoveryWatchdog.cs 里搜索 "REMOVABLE:JUMPWATCH"（共 2 处），把中间那段整段删除；
 /// 3. 完成。**没有任何其它地方引用它** —— 不注册 Harmony 补丁、不改全局状态、不碰 FixLog。
 /// </remarks>
 internal static class LeanJumpWatch
 {
 private static string _path;
 private static bool _inited;
 private static float _lastTick = -999f;
 private static Vector3 _lastOrigin;
 private static bool _hadOrigin;
 private static bool _lastModeOk = true;
 private static bool _everSawMode; // 只有"见过活的 Mode"之后，null 才算死亡（否则是启动阶段）
 private static int _writeCount;
 private static float _lastJumpLog = -999f;
 private static bool _stopped;

 private const float TickInterval = 0.25f; // 采样间隔
 private const float JumpThreshold = 1.0f; // 0.25 秒内位移超过 1 米才算"瞬移"
 private const float JumpLogCooldown = 2.0f; // 同一类事件最多 2 秒一行
 private const int MaxLines = 500; // 全文件上限，防意外膨胀

 internal static void Init()
 {
 if (_inited) return;
 _inited = true;
 try
 {
 _path = Path.Combine(BepInEx.Paths.BepInExRootPath, "KKCharaStudioVRFix_jump.log");
 File.WriteAllText(_path,
 "=== KKCharaStudioVRFix 跳变检测（精简版专用）===" + Environment.NewLine
 + "start: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine
 + "说明：只在出现异常时写一行；不进 BepInEx 控制台，只在这个文件里。" + Environment.NewLine
 + " origin 跳变 = 0.25 秒内位移 > 1 米（正常拖动世界是平滑的，不会触发）。" + Environment.NewLine
 + Environment.NewLine,
 Encoding.UTF8);
 }
 catch { _path = null; }
 }

 internal static void Tick()
 {
 if (!_inited) Init();
 if (_path == null || _stopped) return;
 if (Time.realtimeSinceStartup - _lastTick < TickInterval) return;
 _lastTick = Time.realtimeSinceStartup;

 try
 {
 // ① 模式是否还活着（最关键的信号）
 bool modeOk = ModeAlive();
 if (modeOk) _everSawMode = true;
 if (_everSawMode && _lastModeOk && !modeOk)
 Write("【模式死亡】Mode 变成 null（手柄会消失、只剩头显能动）｜" + Snapshot());
 _lastModeOk = modeOk;

 // ② origin 是否"瞬移"
 Transform o = VRRef.GetVROrigin();
 if (o == null)
 {
 if (_hadOrigin)
 {
 Write("【rig 丢失】origin = null｜" + Snapshot());
 _hadOrigin = false;
 }
 return;
 }

 Vector3 p = o.position;
 if (_hadOrigin)
 {
 float d = Vector3.Distance(p, _lastOrigin);
 if (d > JumpThreshold && Time.realtimeSinceStartup - _lastJumpLog > JumpLogCooldown)
 {
 _lastJumpLog = Time.realtimeSinceStartup;
 Write("【origin 跳变】0.25 秒内 Δ=" + d.ToString("0.00") + "m "
 + Fmt(_lastOrigin) + " → " + Fmt(p)
 + " modeOk=" + modeOk + " 加载中=" + IsLoading() + "｜" + Snapshot());
 }
 }
 _lastOrigin = p;
 _hadOrigin = true;

 // ③ rig 状态追踪：id 或 scene 变了就记一行（正常情况一整局只有 1~2 行）
 TrackRig();
 }
 catch (Exception e) { Write("【检测自身异常】" + e.GetType().Name + ": " + e.Message); }
 }

 /// <summary>模式被销毁/禁用时由 Harmony 钩子调用（复现时最有价值的一行）。</summary>
 internal static void OnModeGone(string how)
 {
 if (!_inited) Init();
 if (_path == null || _stopped) return;
 try { Write("【模式销毁事件】" + how + " 被调用｜" + Snapshot()); } catch { }
 }

 private static bool _ddlOkLogged;
 private static bool _ddlFailLogged;
 private static int _sceneLoadCount;

 /// <summary>
 /// 【主动确认】每次"场景加载开始前"都会调用一次 —— 这是**正向证据**：
 /// 不是"没出事所以大概没事"，而是"这一次加载前，rig 确实已经在 DontDestroyOnLoad 里"。
 /// 连点 N 张地图就有 N 行「是 ✅」，那才叫确认修好。
 /// </summary>
 internal static void OnBeforeSceneLoad(bool rigInDdl, string api)
 {
 if (!_inited) Init();
 if (_path == null || _stopped) return;
 try
 {
 _sceneLoadCount++;
 Write("【加载前检查 #" + _sceneLoadCount + "｜" + api + "】rig 在 DDL = "
 + (rigInDdl ? "是 ✅（场景卸载带不走它）" : "否 ❌（这次很可能跳变）")
 + "｜" + Snapshot());
 }
 catch { }
 }

 // 【追踪】rig 实例 + 它所在 scene 的 handle。两者任一变化就记一行。
 // 这是回答"rig 到底是什么时候离开 DontDestroyOnLoad 的"的唯一直接证据。
 private static int _trackRigId;
 private static int _trackSceneHandle = int.MinValue;
 private static string _trackSceneName;

 /// <summary>
 /// DDL（DontDestroyOnLoad）的执行结果。
 /// 只记**第一次成功**和**第一次失败**各一行 —— 不刷屏，但足以判断
 /// "到底是我们没调用 DDL，还是调用了但没生效"。
 /// </summary>
 internal static void OnDdlResult(bool ok, string where)
 {
 if (!_inited) Init();
 if (_path == null || _stopped) return;
 try
 {
 if (ok)
 {
 if (_ddlOkLogged) return;
 _ddlOkLogged = true;
 Write("【DDL 成功】" + where + "｜" + Snapshot());
 }
 else
 {
 if (_ddlFailLogged) return;
 _ddlFailLogged = true;
 Write("【DDL 失败】" + where + " → 调了 DontDestroyOnLoad 但 root 仍不在 DDL 场景｜" + Snapshot());
 }
 }
 catch { }
 }

 /// <summary>
 /// 追踪 rig 的「实例 ID + 所在 scene」。任一变化就写一行：
 /// 【rig 状态】id=A scene=X → id=B scene=Y
 /// 如果看到 scene 从 DontDestroyOnLoad 变成地图名，就说明**有人把它搬回去了**；
 /// 如果从一开始就是地图名，就说明 **DDL 从来没生效**。两者修法完全不同。
 /// </summary>
 private static void TrackRig()
 {
 try
 {
 VRCamera cam = VRRef.FindExistingVRCamera();
 if (cam == null)
 {
 if (_trackRigId != 0)
 {
 Write("【rig 状态】VRCamera 不见了（缓存失效）｜" + Snapshot());
 _trackRigId = 0;
 _trackSceneHandle = int.MinValue;
 }
 return;
 }

 int id = cam.GetInstanceID();
 Transform root = cam.transform != null ? cam.transform.root : null;
 UnityEngine.SceneManagement.Scene sc =
 root != null ? root.gameObject.scene : default(UnityEngine.SceneManagement.Scene);

 // 这个 Unity 版本没有 Scene.handle，直接比 scene 名（每秒 4 次，开销可忽略）
 string newName = sc.IsValid() ? sc.name : "<invalid>";
 if (id == _trackRigId && string.Equals(newName, _trackSceneName, StringComparison.Ordinal)) return;

 string oldName = _trackSceneName ?? "<首次>";
 _trackRigId = id;
 _trackSceneName = newName;

 // 注意：真正的"自动纠正"（解除 parent + 重新补 DDL）在核心路径 `RigIdGuardTick` 里，
 // 不放在这里 —— 诊断类将来会被整个删掉，功能不能寄生在它上面。

 // ★★★ 关键取证：rig 的 root 有没有 parent？
 // DontDestroyOnLoad 的对象**一旦被 SetParent 到普通场景里的对象**，就会跟着回到那个场景。
 // 这就是"DDL 明明成功过，后来却跑到地图场景"的唯一机制。
 string parentInfo = "";
 try
 {
 if (root != null && root.parent != null)
 {
 Transform pp = root.parent;
 parentInfo = " ★parent=" + pp.name + "(id=" + pp.gameObject.GetInstanceID()
 + "/scene=" + (pp.gameObject.scene.IsValid() ? pp.gameObject.scene.name : "?") + ")";
 }
 else parentInfo = " parent=null(是根对象)";
 }
 catch { }

 Write("【rig 状态】id=" + id + " scene: " + oldName + " → " + newName
 + (newName == "DontDestroyOnLoad" ? " ✅在DDL" : " ❌不在DDL")
 + parentInfo + "｜" + Snapshot());
 }
 catch { }
 }

 private static bool ModeAlive()
 {
 try
 {
 VRManager mgr = VR.Manager;
 if (mgr == null) return false;
 return mgr.Mode != null;
 }
 catch { return false; }
 }

 private static string IsLoading()
 {
 try { return VRRef.IsGameLoading() ? "是" : "否"; } catch { return "?"; }
 }

 private static string Snapshot()
 {
 try { return VRRef.GetRigSceneSnapshot(); } catch { return "快照失败"; }
 }

 private static string Fmt(Vector3 v)
 {
 return "(" + v.x.ToString("0.00") + "," + v.y.ToString("0.00") + "," + v.z.ToString("0.00") + ")";
 }

 private static void Write(string msg)
 {
 try
 {
 if (_path == null) return;
 if (_writeCount >= MaxLines)
 {
 _stopped = true;
 File.AppendAllText(_path, "—— 已达 " + MaxLines + " 行上限，检测停止 ——" + Environment.NewLine);
 return;
 }
 _writeCount++;
 File.AppendAllText(_path,
 DateTime.Now.ToString("HH:mm:ss.fff") + " [" + _writeCount + "] " + msg + Environment.NewLine);
 }
 catch { }
 }
 }
}
#endif
