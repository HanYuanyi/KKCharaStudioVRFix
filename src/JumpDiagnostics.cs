using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;
using VRGIN.Core;
using VRGIN.Modes;
using VRGIN.Visuals;

namespace KKCharaStudioVRFix
{
 /// <summary>
 /// 跳变诊断第四版：上一版 rig 保活日志只证明 `DontDestroyOnLoad`
 /// 没抛异常，不证明对象真的进入 DDL 场景；且把 DDL 调在 eye 子对象，不符合 Unity root 要求。
 /// 本版只对 `transform.root.gameObject` 调 DDL，并核对 `root.scene.name == DontDestroyOnLoad`；
 /// 诊断本身用 FindObjectOfType，不调用 `VRCamera.Instance` 惰性构造器，避免仪表自己造新 rig。
 /// 同时监听 Unity `sceneUnloaded`，Mode 死亡现场打印 eye/root/origin 的 scene 和 InstanceID。
 ///
 /// **蓝屏/Mode 死亡仍未修复**： 日志显示“保活成功”后 Mode/跳变仍发生，
 /// 所以旧版的修复宣称不成立。本版的 root-scene 校验是验证手段，不能提前宣称已根治。
 /// 另，解锁丢失已找到明确原因并改为左右手分开跟踪（日志中只有左手 unlocked，
 /// 旧版却只存右手 locked）。
 /// </summary>
 internal static class JumpDiagnostics
 {
 private static bool _installed;
 private static HarmonyLib.Harmony _harmony;
 private static bool _sceneEventHooked;

 // ---------- 环形缓冲（统一时间线） ----------
 private const int RingSize = 60;
 private static readonly string[] _ring = new string[RingSize];
 private static int _ringNext;

 // ---------- ② 卡帧记录仪 ----------
 private static float _lastFrameTime = -1f;
 private static float _lastStallLogTime = -999f;
 private static int _stallCount;
 private static float _maxFrameTime;

 // ---------- ④ origin 记录仪 ----------
 private static float _suppressUntil = -1f;
 private static Vector3 _lastPos;
 private static Quaternion _lastRot = Quaternion.identity;
 private static bool _hasLast;

 /// <summary>抑制 origin 记录 N 秒（我方自己要写 origin 之前调用）。</summary>
 internal static void Suppress(float seconds)
 {
 _suppressUntil = Time.realtimeSinceStartup + seconds;
 _hasLast = false;
 }

 /// <summary>往统一时间线里打一条标记（供看门狗的关键事件调用）。</summary>
 internal static void Mark(string text)
 {
 PushRing("◆ " + text);
 }

 internal static void Install(HarmonyLib.Harmony harmony)
 {
 _harmony = harmony;
 TryInstall();
 }

 internal static void RetryInstall()
 {
 if (_installed) return;
 if (_harmony == null) return;
 TryInstall();
 }

 private static void TryInstall()
 {
 try
 {
 // ① ControlMode.OnDestroy（`public virtual`， 误挂不存在的基类
 // ProtectedBehaviour.OnDestroy 导致完全没装上； 挂在 ControlMode 上
 // 却用 postfix —— OnDestroy 执行期间 __instance 已是 Unity fake-null，
 // postfix 首行 `__instance == null` 把日志全吞了）。
 // 正解：挂 ControlMode.OnDestroy + **prefix**（对象还活着）。
 MethodInfo onDestroy = AccessTools.Method(typeof(ControlMode), "OnDestroy");
 if (onDestroy == null)
 onDestroy = typeof(ControlMode).GetMethod("OnDestroy", AllInstanceFlags);
 if (onDestroy == null)
 {
 FixLog.Warn("【跳变诊断】找不到 ControlMode.OnDestroy，死亡现场未启用（稍后重试）。");
 return;
 }
 _harmony.Patch(onDestroy, prefix: new HarmonyMethod(
 AccessTools.Method(typeof(JumpDiagnostics), "ModeOnDestroyPrefix")));
 if (!_sceneEventHooked)
 {
 UnityEngine.SceneManagement.SceneManager.sceneUnloaded += SceneUnloaded;
 _sceneEventHooked = true;
 }
 _installed = true;
 FixLog.Key("【跳变诊断】已装齐：ControlMode.OnDestroy(prefix) 调用栈 + 蓝屏卡帧仪"
 + " + origin 记录仪 + Unity 场景卸载现场。");
 }
 catch (Exception e)
 {
 FixLog.Warn("【跳变诊断】安装失败（稍后重试）: " + e.Message);
 }
 }

 private const BindingFlags AllInstanceFlags = BindingFlags.Public | BindingFlags.NonPublic
 | BindingFlags.Instance;

 /// <summary>Unity 场景卸载完成现场：看 rig root 是否仍留在 DontDestroyOnLoad。</summary>
 private static void SceneUnloaded(UnityEngine.SceneManagement.Scene scene)
 {
 try
 {
 string record = "【Unity 场景卸载完成】name=" + scene.name
 + "；" + VRRef.GetRigSceneSnapshot();
 FixLog.Key(record);
 PushRing(record);
 }
 catch (Exception e) { FixLog.Debug("SceneUnloaded 诊断失败: " + e.Message); }
 }

 /// <summary>
 /// ① Mode 死亡现场（prefix：OnDestroy 方法体还没跑，对象还活着）。
 /// 栈只有 Unity callback 并不够定位销毁发起者；新版本同时记录这个对象所在的
 /// Unity Scene、整条 root/origin hierarchy 和 DontDestroyOnLoad 实际 scene，
 /// 并监听 sceneUnloaded。若 Mode dies 时 root.scene 已是 DDL，且期间没有场景卸载，
 /// 就排除“场景卸载回收”这条，转查显式/引擎 teardown。
 /// </summary>
 public static void ModeOnDestroyPrefix(ControlMode __instance)
 {
 try
 {
 if (__instance == null) return; // 理论上不会真（prefix 时活着）；保底
 string typeName = __instance.GetType().Name;
 string goName = __instance.gameObject != null ? __instance.gameObject.name : "?";

 string stack;
 try { stack = new StackTrace(true).ToString(); }
 catch (Exception se) { stack = "<StackTrace 失败: " + se.Message + ">"; }

 string rigInfo = VRRef.GetRigSceneSnapshot(__instance.gameObject) + " | " + DescribeActiveScene();

 string record = "【Mode 死亡】" + typeName + " (挂在 " + goName + " 上, scene="
 + SafeObjectScene(__instance.gameObject) + ", id=" + __instance.GetInstanceID()
 + ") | " + rigInfo;
 FixLog.Key(record);
 FixLog.Key(" 调用栈 >>>\n" + stack + "\n <<< 调用栈结束");
 PushRing(record + "\n " + stack.Replace("\n", "\n "));
 }
 catch { }
 }

 /// <summary>活动场景 + 场景数（区分"场景卸载致死"）。</summary>
 private static string DescribeActiveScene()
 {
 try
 {
 var sc = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
 return "activeScene=" + sc.name + " sceneCount="
 + UnityEngine.SceneManagement.SceneManager.sceneCount;
 }
 catch (Exception e) { return "activeScene=<读取失败:" + e.Message + ">"; }
 }

 private static string SafeObjectScene(GameObject go)
 {
 try { return go.scene.IsValid() ? go.scene.name : "<invalid>"; }
 catch { return "<error>"; }
 }

 /// <summary>
 /// rig 的 DDL 保活巡检（每 2 秒）。
 /// **这是 BUG 修复本身，不是诊断** —— 场景切换可能把 rig 移出 DontDestroyOnLoad 场景，
 /// 必须定期重新保活，否则 rig 死 → Mode/控制器/origin 陪葬 → 蓝屏跳变回来。
 /// 所以它**不能**被 `#if !LEAN` 排除（精简版也必须跑）。
 /// </summary>
 internal static void RigPatrol()
 {
 if (Time.realtimeSinceStartup - _lastRigCheckTime < 2f) return;
 _lastRigCheckTime = Time.realtimeSinceStartup;
 try
 {
 // 不调用 VR.Camera（该 getter 会在对象死亡时惰性创建新 rig，污染现场）。
 VRCamera cam = VRRef.FindExistingVRCamera();
 int id = cam == null ? 0 : cam.GetInstanceID();
 bool persisted = (cam != null) && VRRef.EnforceRigDDL();
#if !LEAN
 string before = VRRef.GetRigSceneSnapshot();
 string after = VRRef.GetRigSceneSnapshot();
 if (id > 0 && _lastRigId > 0 && id != _lastRigId)
 {
 string record = "【rig 实例更换】InstanceID " + _lastRigId
 + " → " + id + " | before=" + before;
 FixLog.Key(record);
 PushRing(record);
 FixLog.Key("【rig DDL 校验】更换后 root scene="
 + (persisted ? "DontDestroyOnLoad（已验证）" : "非 DDL 场景/校验失败")
 + " | after=" + after);
 }
 else if (cam != null && !persisted
 && Time.realtimeSinceStartup - _lastRigWarnTime >= 10f)
 {
 _lastRigWarnTime = Time.realtimeSinceStartup;
 string record = "【rig DDL 校验失败】DontDestroyOnLoad 调用后 root 仍非 DDL 场景 | " + after;
 FixLog.Warn(record);
 PushRing(record);
 }
#endif
 if (id > 0) _lastRigId = id;
 }
 catch (Exception e) { FixLog.Debug("rig 持久化检查异常: " + e.Message); }
 }

 /// <summary>②③④ 每帧调用（看门狗 Update，VR 守卫之内）。</summary>
 private static float _lastSecFps; // 上一秒的平均帧数，用于判断"这一秒是否掉帧"
 private static int _fpsFrames;
 private static float _fpsAccum;
 private static float _fpsWorst;
 private static float _fpsWindowStart = -999f;
 private static int _gc0Last = -1;
 private static long _heapLast = -1L;
 private static int _lastRigId = -1;
 private static float _lastRigCheckTime = -999f;
 private static float _lastRigWarnTime = -999f;
 internal static void MonitorTick()
 {
 try
 {
 // ② 卡帧记录仪（蓝屏 = 不出帧）
 // 诊断总开关关闭时，卡帧仪与 origin 记录仪都停（FPS/GC 量尺保留）
 bool diag = KKCharaStudioVRFixPlugin.DiagOn;
 float ft = Time.unscaledDeltaTime;
 if (_lastFrameTime >= 0f)
 {
 if (ft > _maxFrameTime) _maxFrameTime = ft;
 if (diag && ft > 0.1f && Time.realtimeSinceStartup - _lastStallLogTime > 1.0f)
 {
 _lastStallLogTime = Time.realtimeSinceStartup;
 _stallCount++;
 string record = "【卡帧】" + (ft * 1000f).ToString("0") + "ms（本场最长 "
 + (_maxFrameTime * 1000f).ToString("0") + "ms，第 " + _stallCount + " 次）";
 FixLog.Key(record);
 PushRing(record);
 }
 }
 _lastFrameTime = ft;

 // ②附：rig 巡检 —— 已提取为独立方法 RigPatrol()，
 // 由看门狗周期调用（**精简版也必须调用**，否则跳变会回来）。
 RigPatrol();

 // FPS 客观计量：每 10 秒报一次"平均帧率 + 最差帧"。
 // 用户反复反馈"帧数又掉回 30"，但卡帧仪只记 >100ms 的顿卡，
 // 无法证明"平均帧率"——加这个才有客观依据判断是不是插件造成的。
 _fpsFrames++;
 _fpsAccum += ft;
 if (ft > _fpsWorst) _fpsWorst = ft;
 // 改成**每秒一条**（原来是 10 秒汇总）。
 // 用户实测："把头显固定不动，盯 SteamVR 左上角帧数，动不动从 53 跳到 48"
 // —— 这是**秒级抖动**，10 秒平均值完全看不出来。
 // 现在每秒落一行：帧数 + 该秒最差帧 + 该秒 GC 次数 + 分配量，
 // 日志就是完整帧率曲线，能直接看到 53→48→53 的波动。
 // 1 秒 → **10 秒**一条。
 // 每秒一条开销过大：每一条都要 GC.GetTotalMemory()
 // （遍历 1.2GB 堆 = 几毫秒）+ 字符串拼接 + 写文件，
 // 等于每秒白烧几毫秒 —— 诊断仪表自己把 53 帧拖到 43 帧。
 // 10 秒一条足够看趋势与掉帧归因，开销降到 1/10。
 if (Time.realtimeSinceStartup - _fpsWindowStart >= 10f)
 {
 float avg = _fpsAccum > 0f ? (_fpsFrames / _fpsAccum) : 0f;

 // GC 计量：用户指出"大停顿是游戏的 GC，但本插件让它触发得更频繁"。
 // Unity 5.6 用的是**非分代、全停顿**的 Boehm GC —— 堆一涨就触发，
 // 一停就是几百毫秒。所以只要把"这 10 秒里 GC 回收了几次、堆涨了多少"
 // 和"最差单帧"放在同一行，就能直接判定大停顿是不是 GC 造成的：
 // · 若最差单帧很大且 Gen0 次数明显偏高 → GC 嫌疑成立（该去砍分配）；
 // · 若 Gen0 次数很少、堆几乎不涨 → 与 GC 无关，另找。
 int gc0 = 0; long heap = 0L;
 try { gc0 = GC.CollectionCount(0); heap = GC.GetTotalMemory(false); } catch { }
 int gcDelta = (_gc0Last >= 0) ? (gc0 - _gc0Last) : 0;
 long heapDeltaKb = (_heapLast > 0L) ? ((heap - _heapLast) / 1024L) : 0L;
 _gc0Last = gc0; _heapLast = heap;

 PerfProbe.CollectStat();
 float plugAvg = (PerfProbe.StatFrames > 0)
 ? (PerfProbe.StatTotal / PerfProbe.StatFrames) : 0f;
 // 每秒一行：帧数 + 最差帧 + GC 次数 + 分配量
 string rec = "【秒帧】" + _fpsFrames + "帧 = " + avg.ToString("0.0") + "fps（"
 + (_fpsAccum / Mathf.Max(1, _fpsFrames) * 1000f).ToString("0.0")
 + "ms/帧，最差 " + (_fpsWorst * 1000f).ToString("0") + "ms）"
 + (gcDelta > 0 ? " | ★GC×" + gcDelta : "")
 + " | 分配" + (heapDeltaKb >= 0 ? "+" : "") + heapDeltaKb + "KB"
 + "（其中本插件 " + (ColorGradingFix.TakeAllocSample() / 1024) + "KB/s）"
 // 帧数比上一秒掉超过 2 帧、或最差帧 >30ms 时，
 // 列出**该秒内每个插件任务各花了多少 ms** —— 直接点名是谁把这一秒拖垮的。
 + ((_lastSecFps > 0f && avg <= _lastSecFps - 2f) || (_fpsWorst * 1000f) > 30f
 ? " | ★掉帧归因：本秒任务 " + PerfProbe.DescribeSecondStats()
 : "")
 // 同一窗口里报"VR 相机上正在生效的效果" ——
 // 这样一次游玩就能看出帧数低时是不是有重效果（AO/色彩分级等）在算。
 + " | VR相机效果=[" + ColorGradingFix.DescribeEnabledVrEffects() + "]"
 // 同一窗口再报"本插件每帧任务耗时"的平均/峰值/占比 ——
 // 稳态 24ms 的帧从来不触发 >5ms 的耗时告警，所以以前完全看不到
 // 插件占了多少帧预算。现在三个数一起出现：1) 平均几毫秒、2) 峰值几毫秒、
 // 3) 占总帧时间百分之几。若这里显示 0.00ms/0.0% 而帧数仍低 → 与插件无关。
 // StatTotal/StatMax 单位是**毫秒**（PerfProbe 内部 *1000f），
 // 而 _fpsAccum 是**秒**（unscaledDeltaTime 累加）—— 直接相除会大 1000 倍，
 // 统一换算成秒显示。
 + " | 本插件每帧 " + plugAvg.ToString("0.00") + "ms 平均 / "
 + PerfProbe.StatMax.ToString("0.0") + "ms 峰值（占 "
 + ((_fpsAccum > 0f && _fpsFrames > 0 && PerfProbe.StatFrames > 0)
 ? ((PerfProbe.StatTotal / 1000f) / _fpsAccum * 100f).ToString("0.00") : "?") + "%）";
 // **每秒都必须清空分桶**（无条件）。
 // 旧逻辑"只有没掉帧的秒才清" → 而低帧率会话几乎每秒都在掉帧
 // → 分桶几乎从不清空 → 任务耗时无限累加，归因数字虚高且无意义
 // （日志里每秒都有 DumpControllerModels=3.7ms，其实是累计了好几分钟）。
 // DescribeSecondStats() 内部会清；这里兜"未触发归因的秒"。
 if (!((_lastSecFps > 0f && avg <= _lastSecFps - 2f) || (_fpsWorst * 1000f) > 30f))
 PerfProbe.ClearSecondStatsQuiet();
 FixLog.Key(rec);
 PushRing(rec);
 _fpsFrames = 0; _fpsAccum = 0f; _fpsWorst = 0f;
 _fpsWindowStart = Time.realtimeSinceStartup;
 _lastSecFps = avg;
 }

 // ④ origin 记录仪（收紧阈值：只留真跳变）
 if (!diag) return;
 Transform origin = VRRef.GetVROrigin();
 if (origin == null) { _hasLast = false; return; }
 if (Time.realtimeSinceStartup < _suppressUntil) { _hasLast = false; return; }

 if (!_hasLast)
 {
 _lastPos = origin.position;
 _lastRot = origin.rotation;
 _hasLast = true;
 return;
 }

 float dPos = (origin.position - _lastPos).magnitude;
 float dRot = Quaternion.Angle(origin.rotation, _lastRot);
 if (dPos > 0.25f || dRot > 10f)
 {
 string record = "【origin 跳变】Δpos=" + dPos.ToString("0.00") + "m Δrot="
 + dRot.ToString("0.0") + "° | " + _lastPos.ToString("0.00") + " → "
 + origin.position.ToString("0.00") + " | 加载中=" + VRRef.IsGameLoading()
 + " 场景卡窗口=" + VRRef.IsSceneCardLoadSticky();
 FixLog.Key(record);
 PushRing(record);
 }
 _lastPos = origin.position;
 _lastRot = origin.rotation;
 }
 catch (Exception e) { FixLog.Debug("MonitorTick 异常: " + e.Message); }
 }

 /// <summary>
 /// ③ Mode==null 尸检：分清三种死法。
 /// </summary>
 internal static void AutopsyModeLoss()
 {
 try
 {
 var sb = new StringBuilder();
 sb.Append("【尸检】VR.Manager.Mode == null。");
 try
 {
 object mgr = VR.Manager;
 sb.Append(" VRManager=").Append(mgr == null ? "null" : "id=" + ((object)mgr).GetHashCode());
 }
 catch (Exception e) { sb.Append(" VRManager=<读取失败:").Append(e.Message).Append(">"); }
 try
 {
 VRCamera existingRig = VRRef.FindExistingVRCamera();
 sb.Append(" | VRCamera(rig)=").Append(existingRig == null ? "不存在（未触发惰性创建）" : "存活 id=" + existingRig.GetInstanceID());
 }
 catch { sb.Append(" | VRCamera=<异常>"); }
 try
 {
 ControlMode[] modes = UnityEngine.Object.FindObjectsOfType<ControlMode>();
 sb.Append(" | 全场活着的 ControlMode=").Append(modes == null ? 0 : modes.Length);
 if (modes != null)
 for (int i = 0; i < modes.Length && i < 4; i++)
 sb.Append(" [").Append(modes[i] == null ? "null" : modes[i].GetType().Name)
 .Append("/id=").Append(modes[i] == null ? 0 : modes[i].GetInstanceID()).Append("]");
 }
 catch (Exception e) { sb.Append(" | ControlMode 扫描失败:").Append(e.Message); }
 FixLog.Key(sb.ToString());
 PushRing(sb.ToString());
 }
 catch { }
 }

 private static void PushRing(string record)
 {
 _ring[_ringNext] = Time.realtimeSinceStartup.ToString("0.0") + "s | " + record;
 _ringNext = (_ringNext + 1) % RingSize;
 }

 /// <summary>F7 诊断新增章节：统一时间线 + Mode/rig/origin 当前状态。</summary>
 internal static void AppendReport(StringBuilder sb)
 {
 sb.AppendLine(" 仪表: " + (_installed ? "已启用(死亡栈+卡帧仪+origin仪)" : "未启用"));
 sb.AppendLine(" 本场卡帧 " + _stallCount + " 次，最长单帧 "
 + (_maxFrameTime * 1000f).ToString("0") + "ms（>100ms 才会记录；蓝屏=不出帧）");
 sb.AppendLine(" --- 统一时间线（最近 " + RingSize + " 条：◆=事件，其余为仪器自动记录）---");
 bool any = false;
 for (int i = 0; i < RingSize; i++)
 {
 int idx = (_ringNext + i) % RingSize;
 if (string.IsNullOrEmpty(_ring[idx])) continue;
 any = true;
 sb.AppendLine(" " + _ring[idx]);
 }
 if (!any) sb.AppendLine(" （本场没有记录）");

 sb.AppendLine(" --- 当前状态 ---");
 try
 {
 sb.AppendLine(" Mode = " + DescribeMode());
 VRCamera existingRig = VRRef.FindExistingVRCamera();
 sb.AppendLine(" existing VRCamera(rig) = " + (existingRig == null
 ? "未找到（没有调用 Instance 惰性创建）"
 : "id=" + existingRig.GetInstanceID() + " | " + VRRef.GetRigSceneSnapshot()));
 Transform origin = VRRef.GetVROrigin();
 if (origin != null)
 sb.AppendLine(" origin pos=" + origin.position.ToString("0.00"));
 else sb.AppendLine(" origin = null");
 }
 catch (Exception e) { sb.AppendLine(" <状态读取失败> " + e.Message); }
 }

 private static string DescribeMode()
 {
 try
 {
 object mode = VR.Manager.Mode;
 if (mode == null) return "null（已死/引用丢失）";
 return mode.GetType().Name + "（存活, id=" + ((object)mode).GetHashCode() + "）";
 }
 catch (Exception e) { return "<读取失败: " + e.Message + ">"; }
 }
 }
}
