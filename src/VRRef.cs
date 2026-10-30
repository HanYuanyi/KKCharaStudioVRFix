using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using VRGIN.Core;

namespace KKCharaStudioVRFix
{
 /// <summary>
 /// 反射工具。
 ///
 /// 游戏本体（Assembly-CSharp）里的类型都是 public，可以直接强类型引用；
 /// 但 KKCharaStudioVRPlugin 0.0.3 里的 KKCharaStudioInterpreter / GenericStandingMode
 /// 是 internal，所以只有这几个地方需要用反射。
 /// </summary>
 internal static class VRRef
 {
 private const BindingFlags AllInstance =
 BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
 private const BindingFlags AllStatic =
 BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

 // ---------- 类型查找 ----------

 internal static Type FindType(string fullName)
 {
 if (string.IsNullOrEmpty(fullName)) return null;
 Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
 for (int i = 0; i < asms.Length; i++)
 {
 try
 {
 Type t = asms[i].GetType(fullName, false);
 if (t != null) return t;
 }
 catch { }
 }
 return null;
 }

 private static Type _interpreterType;
 private static bool _interpreterTypeSearched;

 /// <summary>KKCharaStudioVR.KKCharaStudioInterpreter（0.0.3 里是 internal）。</summary>
 internal static Type InterpreterType
 {
 get
 {
 // 同 MoveHelperType：只缓存成功查找，失败允许重试
 // （Awake 时目标程序集可能还没加载）。
 if (_interpreterType == null)
 _interpreterType = FindType("KKCharaStudioVR.KKCharaStudioInterpreter");
 return _interpreterType;
 }
 }

 private static Type _moveHelperType;

 /// <summary>KKCharaStudioVR.VRCameraMoveHelper（public）。</summary>
 internal static Type MoveHelperType
 {
 get
 {
 // 只缓存**成功**的查找：Awake 时 KKCharaStudioVRPlugin.dll
 // 可能还没加载（本插件的 Awake 早于它），失败的查找必须允许下次重试，
 // 否则 null 被永久缓存 → 之后所有 CallMoveToCurrent 全部失败
 // 。
 if (_moveHelperType == null)
 _moveHelperType = FindType("KKCharaStudioVR.VRCameraMoveHelper");
 return _moveHelperType;
 }
 }

 // ---------- 成员读写 ----------

 internal static object GetStatic(Type type, string name)
 {
 if (type == null) return null;
 try
 {
 PropertyInfo p = type.GetProperty(name, AllStatic);
 if (p != null && p.CanRead && p.GetIndexParameters().Length == 0)
 return p.GetValue(null, null);
 FieldInfo f = type.GetField(name, AllStatic);
 if (f != null) return f.GetValue(null);
 }
 catch (Exception e)
 {
 FixLog.Debug("GetStatic " + name + " 失败: " + e.Message);
 }
 return null;
 }

 internal static object Call(object obj, string method, params object[] args)
 {
 if (obj == null) return null;
 try
 {
 MethodInfo m = obj.GetType().GetMethod(method, AllInstance);
 if (m == null) return null;
 return m.Invoke(obj, args);
 }
 catch (Exception e)
 {
 FixLog.Debug("Call " + method + " 失败: " + e.Message);
 return null;
 }
 }

 // ---------- 游戏对象（强类型） ----------

 /// <summary>Studio 主相机（cameraCtrl.mainCmaera）。</summary>
 internal static Camera GetStudioMainCamera()
 {
 try
 {
 Studio.Studio studio = Studio.Studio.IsInstance() ? Studio.Studio.Instance : null;
 if (studio == null || studio.cameraCtrl == null) return null;
 return studio.cameraCtrl.mainCmaera;
 }
 catch (Exception e)
 {
 FixLog.Debug("GetStudioMainCamera 失败: " + e.Message);
 return null;
 }
 }

 /// <summary>Studio.workInfo.visibleAxis —— 决定 FK/IK 球（Studio/Select 图层）是否显示。</summary>
 internal static bool? GetVisibleAxis()
 {
 try
 {
 Studio.Studio studio = Studio.Studio.IsInstance() ? Studio.Studio.Instance : null;
 if (studio == null || studio.workInfo == null) return null;
 return studio.workInfo.visibleAxis;
 }
 catch
 {
 return null;
 }
 }

#if !LEAN
 // 【精简版瘦身】诊断辅助 + 已删除的 F6 功能入口。
 internal static void SetVisibleAxis(bool value)
 {
 try
 {
 Studio.Studio studio = Studio.Studio.IsInstance() ? Studio.Studio.Instance : null;
 if (studio == null || studio.workInfo == null) return;
 studio.workInfo.visibleAxis = value;
 }
 catch { }
 }
#else
 // 【LEAN】整段删除（诊断辅助 / 已删除的 F6 强制关轴功能）
#endif

 /// <summary>
 /// 同一帧内的缓存。
 ///
 /// 本方法被 `OriginGuardFix.Tick`、`SafeCheckMissingMode`、`SafeCheckControllers`
 /// 三处调用，而它们分散在 Update / LateUpdate 的不同分支里。
 /// 每次调用都要做两次泛型单例查找（`Manager.Scene.IsInstance()` +
 /// `Studio.Map.IsInstance()`，内部是 `Object.FindObjectOfType` 级别的开销）。
 /// 同一帧内结果不可能变化 —— 用 `Time.frameCount` 做一次缓存。
 /// </summary>
 private static int _loadingCachedFrame = -1;
 private static bool _loadingCachedValue;

 internal static bool IsGameLoading()
 {
 if (Time.frameCount == _loadingCachedFrame) return _loadingCachedValue;

 bool result = false;
 try
 {
 Manager.Scene scene = Manager.Scene.IsInstance() ? Manager.Scene.Instance : null;
 if (scene != null && (scene.IsNowLoading || scene.IsNowLoadingFade))
 result = true;
 else
 {
 Studio.Map map = Studio.Map.IsInstance() ? Studio.Map.Instance : null;
 if (map != null && map.isLoading) result = true;
 }
 }
 catch
 {
 result = false;
 }

 _loadingCachedFrame = Time.frameCount;
 _loadingCachedValue = result;
 return result;
 }

 /// <summary>
 /// VR 是否已就绪（可安全调用原版 MoveToCurrent）。
 /// 与 VRRecoveryWatchdog.IsVrUsable 同义，抽出来供 SceneTransitionHook 复用。
 /// </summary>
 internal static bool IsVrReady()
 {
 try { return VR.Active; }
 catch { return false; }
 }

 /// <summary>
 /// `Studio.Studio.InitScene` 之后把原点拉回加载前位姿
 /// —— 抵消 `cameraCtrl.Reset(0)` 的位移（修「跳1 之后镜头被甩到错误位置」）。
 /// 实现在 OriginGuardFix（原点姿态记忆的持有者）。
 /// </summary>
 internal static void RestoreOriginAfterInitScene()
 {
 try { OriginGuardFix.RestoreOriginAfterInitScene(); }
 catch (Exception e) { FixLog.Debug("RestoreOriginAfterInitScene 转发异常: " + e.Message); }
 }

 /// <summary>
 /// 在加载钩子的 prefix 里捕获 origin 当前位姿 ——
 /// 供 `SceneTransitionHook` 调用。实现在 OriginGuardFix（位姿记忆的持有者）。
 ///
 /// `authoritative`：
 /// · `true` —— 来自**加载入口**的 prefix（LoadScene / LoadSceneCoroutine /
 /// ImportScene），即"用户点按钮那一刻"，优先级最高；
 /// · `false` —— 来自**加载过程中**的钩子（AddMap / UnloadBaseScene /
 /// LoadMapCoroutine），只在还没有值时写入，绝不覆盖已有值。
 ///
 /// 详见 `OriginGuardFix.CaptureLoadPose` 的注释。
 /// </summary>
 internal static void CaptureLoadPose(string reason, bool authoritative = true)
 {
 try { OriginGuardFix.CaptureLoadPose(reason, authoritative); }
 catch (Exception e) { FixLog.Debug("CaptureLoadPose 转发异常: " + e.Message); }
 }

 /// <summary>加载完整结束后清掉「加载前位姿」快照。</summary>
 internal static void ClearLoadPose()
 {
 try { OriginGuardFix.ClearLoadPose(); }
 catch (Exception e) { FixLog.Debug("ClearLoadPose 转发异常: " + e.Message); }
 }

 /// <summary>
 /// 记录"加载入口那一刻"（用户点按钮时）的 origin ——
 /// 抵消 `StandingMode` 归零的**首选目标**。
 /// 实现在 OriginGuardFix（位姿记忆的持有者）。
 /// </summary>
 internal static void CapturePoseAtLoadEntry()
 {
 try { OriginGuardFix.CapturePoseAtLoadEntry(); }
 catch (Exception e) { FixLog.Debug("CapturePoseAtLoadEntry 转发异常: " + e.Message); }
 }

 /// <summary>
 /// 取当前 `VR.Manager.Mode` 实例（可能是 null）。
 ///
 /// 供 `SpuriousModeRebuildGuard` 判定"mode 是否仍然健在" ——
 /// 这是"是否阻止 0.0.3 多余重建"的关键依据。
 /// 用 `object` 作返回类型是为了不在此处引入 `VRGIN.Modes` 依赖。
 /// </summary>
 internal static object GetCurrentMode()
 {
 try
 {
 VRGIN.Core.VRManager mgr = VR.Manager;
 if (mgr == null) return null;
 return mgr.Mode;
 }
 catch { return null; }
 }

 /// <summary>
 /// 查找**已存在**的 VRCamera（绝不触发 VRCamera.Instance 的惰性创建）。
 ///
 /// 这里直接调 FindObjectOfType，而 GetVROrigin()
 /// 会被每帧调用（origin 记录仪）→ 变成**每帧一次全场景扫描** → 帧数 55 → 30。
 /// 现在缓存引用：Unity 的 fake-null 判定在对象被销毁后会返回 false，
 /// 所以"缓存失效 → 重新扫描"是可靠的，且平时只花一次字段读 + null 判定。
 /// </summary>
 private static VRCamera _camCache;

 internal static VRCamera FindExistingVRCamera()
 {
 if (_camCache != null) return _camCache; // Unity fake-null：销毁后自动为 false
 try { _camCache = UnityEngine.Object.FindObjectOfType<VRCamera>(); }
 catch { _camCache = null; }
 return _camCache;
 }

 /// <summary>强制下次重新扫描（例如刚检测到 rig 变化时）。</summary>
 internal static void InvalidateCameraCache()
 {
 _camCache = null;
 }

 /// <summary>
 /// 【2026-10-22 ★★★ 平面模式安全阀】VR 到底有没有在跑。
 ///
 /// 为什么必须有它：`VR.Camera` 就是 `VRCamera.Instance`，**是惰性 getter** ——
 /// 只要 `_Instance` 为 null，它会**当场 `new GameObject("VRGIN_Camera")` + AudioListener + VRCamera**。
 /// 平面模式（没启 VR，`VRManager` 根本没创建、`VR.Active == false`）下碰一下，就会凭空造出一套假 rig：
 /// · 你看到的是这台假相机（位于原点）的画面 → **只有天空盒/蓝背景**；
 /// · Studio 自己的 UI 是 ScreenSpaceOverlay，不受相机影响 → **UI 照旧可见**；
 /// · 拖鼠标仍然在动 Studio 的主相机，但你看的是假相机 → **"镜头无法移动"**；
 /// · 换地图后假相机能看到地图了 → **"蓝背景消失、只剩地图，镜头依然不动"**。
 /// 这就是用户 2026-10-22 报的"平面进工作室卡在蓝背景"，也是 10-12 那次的同一个坑。
 ///
 /// 谁踩过：① 已删的 F7 诊断；② `Studio.CameraControl.ReflectOption` 钩子；
 /// ③ `SunLightInfo.Set` / `Info.SetACE` 钩子（这两个**平面模式下照样触发**）。
 /// 规矩：**任何"从游戏侧事件进来"的入口，先过这一关**；`GetVrCamera()` 类取值函数也要先过。
 /// </summary>
 internal static bool VrRunning
 {
 get { try { return VR.Active; } catch { return false; } }
 }

 internal static Transform GetVROrigin()
 {
 try
 {
 VRCamera cam = FindExistingVRCamera();
 if (cam == null || cam.SteamCam == null) return null;
 return cam.SteamCam.origin;
 }
 catch
 {
 return null;
 }
 }
 /// <summary>
 /// 【v1.9.13】对 rig 的**层级根对象**执行 DontDestroyOnLoad，并验证其真正进入
 /// Unity 的 DontDestroyOnLoad 场景。旧版 v1.9.12 对 eye 子对象调用 DDL（Unity 要求目标是 root），
 /// 也对 origin 调用但没验证 hierarchy root 是否真的进入 DDL scene；只因调用没抛异常就报
 /// "成功"，随后日志仍抓到 rig/Mode 死亡。现在改为对层级 root 调用并检查
 /// `root.scene.name == "DontDestroyOnLoad"`；否则返回 false 并记出层级/scene。
 /// </summary>
 internal static bool EnforceRigDDL()
 {
 return EnforceRigDDL(FindExistingVRCamera());
 }

 /// <summary>
 /// 【NEWFIX ★★★】指定相机版本 —— 供 `VRCamera.OnAwake` 事件直接调用。
 /// 为什么必须有这个重载：新 rig 刚被创建时，`FindExistingVRCamera()` 的缓存还指向旧的/null，
 /// 用它就会漏掉"新 rig 没进 DontDestroyOnLoad"这件事 —— 而那正是跳变的根因。
 /// </summary>
 internal static bool EnforceRigDDL(VRCamera cam)
 {
 try
 {
 if (cam == null) return false;
 GameObject eye = cam.gameObject;
 if (eye == null) return false;

 Transform root = eye.transform.root;
 if (root == null) return false;
 GameObject rootGo = root.gameObject;
 if (rootGo == null) return false;

 // DDL 只能对层级根对象调用。不要再对 VRGIN_Camera (eye) 子对象调用。
 UnityEngine.Object.DontDestroyOnLoad(rootGo);

 // 确保 origin 也归入 DDL 场景（通常它就是该层级根）。
 try
 {
 Transform origin = (cam.SteamCam != null) ? cam.SteamCam.origin : null;
 if (origin != null)
 {
 Transform originRoot = origin.root;
 if (originRoot != null && originRoot.gameObject != null)
 UnityEngine.Object.DontDestroyOnLoad(originRoot.gameObject);
 }
 }
 catch { }

 string rootScene = rootGo.scene.IsValid() ? rootGo.scene.name : "<invalid>";
 string eyeScene = eye.scene.IsValid() ? eye.scene.name : "<invalid>";
 bool persisted = rootScene == "DontDestroyOnLoad";
 if (!persisted)
 {
 string path = root.name + "(id=" + rootGo.GetInstanceID() + ",scene=" + rootScene + ")";
 FixLog.Warn("【rig DDL 校验失败】调用 DontDestroyOnLoad 后 root 仍不在 DDL 场景："
 + path + "；eye=" + eye.name + "(scene=" + eyeScene + ")。不能把" +
 "API 未抛异常当作保活成功。");
 }
 return persisted;
 }
 catch (Exception e)
 {
 FixLog.Debug("EnforceRigDDL err: " + e.Message);
 return false;
 }
 }

 /// <summary>不销毁对象时记录 rig 的 hierarchy 与 Unity scene 现场。</summary>
 internal static string GetRigSceneSnapshot()
 {
 return GetRigSceneSnapshot((GameObject)null);
 }

 internal static string GetRigSceneSnapshot(GameObject knownEye)
 {
 try
 {
 VRCamera cam = (knownEye != null) ? knownEye.GetComponent<VRCamera>() : FindExistingVRCamera();
 if (cam == null) return "VRCamera=<not found (no lazy creation)>";
 GameObject eye = knownEye != null ? knownEye : cam.gameObject;
 Transform root = (eye != null) ? eye.transform.root : null;
 Transform origin = (cam.SteamCam != null) ? cam.SteamCam.origin : null;
 return "rigId=" + cam.GetInstanceID()
 + " eye=" + (eye == null ? "<null>" : eye.name + "/scene=" + SafeSceneName(eye))
 + " root=" + (root == null ? "<null>" : root.name + "/id=" + root.gameObject.GetInstanceID()
 + "/scene=" + SafeSceneName(root.gameObject))
 + " origin=" + (origin == null ? "<null>" : origin.name + "/id=" + origin.gameObject.GetInstanceID()
 + "/scene=" + SafeSceneName(origin.gameObject));
 }
 catch (Exception e) { return "rig snapshot failed: " + e.Message; }
 }

 private static string SafeSceneName(GameObject go)
 {
 try { return go.scene.IsValid() ? go.scene.name : "<invalid>"; }
 catch { return "<error>"; }
 }

 internal static void FlattenOriginRotation()
 {
 try
 {
 Transform origin = GetVROrigin();
 if (origin == null) return;

 Vector3 e = origin.rotation.eulerAngles;
 if (Mathf.Abs(e.x) < 0.01f && Mathf.Abs(e.z) < 0.01f) return; // 已经是平的

 Quaternion flat = Quaternion.Euler(0f, e.y, 0f);
 FixLog.Debug("对齐前预压平 origin 旋转: ("
 + e.x.ToString("0.00") + ", " + e.y.ToString("0.00") + ", "
 + e.z.ToString("0.00") + ") → (0, " + e.y.ToString("0.00")
 + ", 0)，避免对齐时视角扭动。");
 origin.rotation = flat;
 }
 catch (Exception e)
 {
 FixLog.Debug("FlattenOriginRotation 异常: " + e.Message);
 }
 }

 // ---------- 场景卡 / 地图 加载区分 ----------

 /// <summary>
 /// 本次加载是不是「加载场景卡」。
 ///
 /// 为什么要区分：场景卡里**自带相机数据**（`studio.sceneInfo.cameraData` →
 /// `studio.cameraCtrl`），原版 Studio 会把镜头摆到存档里的机位；
 /// 而地图不带相机数据。两者的"要不要兜底写回站姿 / 要不要对齐镜头"完全不同 ——
 /// 场景卡**绝不能**被插件写回，否则就盖掉存档机位。
 ///
 /// 置位：SceneTransitionHook 在 Studio.LoadSceneCoroutine / LoadScene /
 /// ImportScene 的 prefix 里调 <see cref="MarkSceneCardLoad"/>。
 ///
 /// ── 关键修正：从「30 秒时间窗」改成「精确的一次性标记」 ──
 /// 旧实现是"标记时间戳 + 30 秒内都算场景卡"。实测日志暴露出致命问题：
 /// **30 秒内连续加载时，前一次的标记还没过期，后面的地图加载也被误判成场景卡**
 /// （证据：42 次对齐 / 44 次加载结束；「地图/其他加载，调用 1 次」出现 0 次）。
 /// 于是每次加载（含纯地图加载）都会去调 MoveToCurrent →
 /// 把 rig 拽到 cameraCtrl 里的**上一次残留机位** → 用户看到的「多次跳变」。
 ///
 /// 新实现：
 /// · <see cref="MarkSceneCardLoad"/> 记下「本次加载从哪个入口进来的」；
 /// · <see cref="IsLoadingSceneCard"/> **只读一次**，读到就返回真，
 /// 且**读完立刻置为已消费**（见 <see cref="ConsumeSceneCardLoad"/>），
 /// 避免同一标记被后续无关加载复用；
 /// · 另外用一个**帧窗口**兜底：标记只在 N 帧内有效，防止"场景卡加载失败、
 /// 再也没人来读标记"时它一直挂着导致后续全部误判。
 /// </summary>
 private static float _sceneCardMarkTime = -999f;

 /// <summary>标记已消费（避免同一次场景卡加载被重复用于后续加载判定）。</summary>
 private static bool _sceneCardConsumed;

 /// <summary>场景卡标记的兜底有效期（秒）。到点自动失效，防止标记泄漏。</summary>
 private const float SceneCardMarkSeconds = 60f;

 // ---------- 粘性「场景卡加载进行中」标记 ----------

 /// <summary>
 /// 粘性标记：本次加载从场景卡入口进来，且**整个加载周期内都有效**。
 ///
 /// ── 为什么不能复用 IsLoadingSceneCard() ──
 /// `IsLoadingSceneCard()` 是「读一次即消费」的瞬时标记（ 为了修
 /// 「30 秒窗口误判」而故意做成这样的）。但 的 `ImportPostfix` 与
 /// 的 `InitScenePostfix` 都想在**加载过程中**判断"这是不是场景卡加载" ——
 /// 两者都撞上了同一个墙：
 ///
 /// · 首次加载时 `_sceneCardMarkTime == -999f`（一个场景卡都还没读过），
 /// `InitScene` 阶段标记根本还没打上 → 守卫为假；
 /// · 即便打上了，看门狗在「加载结束」里 `ConsumeSceneCardLoad()` 一读，
 /// `_sceneCardConsumed = true` → 之后所有 `IsLoadingSceneCard()` 全为假。
 ///
 /// 铁证：
 /// `已挂载钩子: Studio.Studio.InitScene [原点守护]` ✅ 挂上了
 /// `InitScene 后已把原点拉回加载前位姿` ❌ 0 次
 /// `场景卡机位已导入` ❌ 0 次
 /// → 两个"修复"从未执行过一条语句。
 ///
 /// 本标记与消费型标记**并存**：消费型继续服务看门狗主逻辑（它需要"一次只判一次"），
 /// 粘性标记服务加载过程中的**只读查询**（它需要"整个加载期间都为真"）。
 /// 生命周期：`MarkSceneCardLoad` 置位 → `ClearSceneCardLoad` 清除，
 /// 另有 `StickySceneCardSeconds` 兜底超时防止标记泄漏。
 /// </summary>
 private static float _stickySceneCardTime = -999f;

 /// <summary>粘性场景卡标记的兜底有效期（秒）。</summary>
 private const float StickySceneCardSeconds = 120f;

 internal static void MarkSceneCardLoad()
 {
 _sceneCardMarkTime = Time.realtimeSinceStartup;
 _sceneCardConsumed = false;
 _stickySceneCardTime = Time.realtimeSinceStartup; // 同时置粘性标记
 }

 internal static void ClearSceneCardLoad()
 {
 _sceneCardMarkTime = -999f;
 _sceneCardConsumed = false;
 _stickySceneCardTime = -999f; // 同时清粘性标记
 }

 /// <summary>
 /// 只读、**不消费**的「本次加载是场景卡吗」——
 /// 供加载过程中的 Harmony postfix 使用（Import / InitScene）。
 ///
 /// 与 <see cref="IsLoadingSceneCard"/> 的区别：本方法**永不**置 `_sceneCardConsumed`，
 /// 所以同一次加载里可以被调用任意多次都得到同样的结果。
 /// </summary>
 internal static bool IsSceneCardLoadSticky()
 {
 if (_stickySceneCardTime < 0f) return false;
 return (Time.realtimeSinceStartup - _stickySceneCardTime) <= StickySceneCardSeconds;
 }

 /// <summary>
 /// 置位/读取统一入口：读一次就消费掉。
 /// 这样"一次场景卡加载"只可能让**一次**后续判定为真，
 /// 不会像 30 秒时间窗那样污染后面 30 秒内的所有加载。
 /// </summary>
 internal static bool ConsumeSceneCardLoad()
 {
 bool v = IsLoadingSceneCard();
 if (v) _sceneCardConsumed = true;
 return v;
 }

 internal static bool IsLoadingSceneCard()
 {
 if (_sceneCardMarkTime < 0f) return false;
 if (_sceneCardConsumed) return false;
 return (Time.realtimeSinceStartup - _sceneCardMarkTime) <= SceneCardMarkSeconds;
 }

 // ---------- 0.0.3 内部类型（反射） ----------

 /// <summary>调用 0.0.3 自带的静态恢复方法 ForceResetAsStandingMode()。</summary>
 internal static bool CallForceResetAsStandingMode()
 {
 Type t = InterpreterType;
 if (t == null) return false;
 try
 {
 MethodInfo m = t.GetMethod("ForceResetAsStandingMode", AllStatic);
 if (m == null) return false;
 m.Invoke(null, null);
 return true;
 }
 catch (Exception e)
 {
 FixLog.Warn("调用 ForceResetAsStandingMode 失败: " + e.Message);
 return false;
 }
 }

 /// <summary>
 /// 调用 0.0.3 的 VRCameraMoveHelper.Instance.MoveToCurrent()。
 ///
 /// ── 为什么要先「预压平」origin 的 x/z 旋转 ──
 /// 原版 MoveTo() 的最后一步是：
 /// vROrigin.transform.rotation = RemoveXZRot(vROrigin.transform.rotation);
 /// 即**先摆位、再压平**。如果 origin 当前带俯仰/翻滚（例如用户按了「解锁拖动世界」
 /// 并转动了世界，实测出现过 z=22.26°、x=15.25°/z=48.27° 等），
 /// 这一压平就会在**摆位完成之后**突然扭转视角 → 用户看到「跳/扭一下」。
 ///
 /// 解决办法：**在调原版之前先把 x/z 归零**（自己做一次等价的 RemoveXZRot）。
 /// 这样原版那一行就成了无操作，「摆位」和「压平」不再产生可见的相对位移
 /// —— 效果完全一致，但没有中间那一扭。
 ///
 /// ⚠️ 注意：这里**只动旋转的 x/z 分量，绝不碰 `lockRotXZ` 开关**，
 /// 也绝不关闭「解锁拖动世界」功能。用户之后照样能自由转动世界。
 /// 被归零的只是"调用对齐这一刻，原点恰好是歪的"这个中间状态。
 /// </summary>
 internal static bool CallMoveToCurrent()
 {
 Type t = MoveHelperType;
 if (t == null) return false;
 try
 {
 object instance = GetStatic(t, "Instance");
 if (instance == null) return false;
 MethodInfo m = t.GetMethod("MoveToCurrent", AllInstance);
 if (m == null) return false;

 // 以下都是我方的已知 origin 写入 → 抑制跳变记录仪，避免自噪
 JumpDiagnostics.Suppress(1.5f);

 // 预压平：避免原版内部"先摆位再压平"造成可见的一扭。
 FlattenOriginRotation();

 m.Invoke(instance, null);

 // 原版 MoveTo 最后一步 RemoveXZRot 会把场景卡相机的俯仰/翻滚抹掉，
 // 只剩水平偏航 → 视角永远"变灰水平"。这里把场景卡的完整旋转补回 origin。
 // 若 MoveToCurrent 的 postfix 钩子已装上（覆盖 10 机位快捷键等
 // 所有调用方），俯仰由它统一补，这里跳过避免重复。
 if (!_moveHookInstalled && CfgAlignCameraPitchOn()) ApplyCardPitch();
 return true;
 }
 catch (Exception e)
 {
 FixLog.Debug("调用 MoveToCurrent 失败: " + e.Message);
 return false;
 }
 }

 /// <summary>是否开启「场景卡相机俯仰补偿」（默认 true，配置缺失时按 true）。</summary>
 internal static bool CfgAlignCameraPitchOn()
 {
 try
 {
 return KKCharaStudioVRFixPlugin.On(KKCharaStudioVRFixPlugin.CfgAlignCameraPitch);
 }
 catch { return true; }
 }

 /// <summary>
 /// 把场景卡相机的完整旋转（含俯仰）补回 VR 原点。
 ///
 /// 原版 `MoveTo()` 结尾 `origin.rotation = RemoveXZRot(origin.rotation)` 只留 yaw，
 /// 于是加载后视角是**水平**的 —— 场景卡明明是从上往下看，VR 里却看向灰蒙蒙的地平线。
 ///
 /// 做法：保持头部世界位置不变，令头部世界朝向 = 场景卡相机朝向：
 /// origin.rotation = cardRot * Inverse(headLocal)
 /// origin.position = headPos - origin.rotation * headLocalPos
 /// ⚠️ 代价：origin 带上俯仰后整个世界随之一同倾斜（原版抹平正是为了地平线水平）。
 /// 晕动症敏感者把 Fixes/AlignCameraPitch 关掉即可回到原版行为。
 /// </summary>
 /// <summary>
 /// 给原版 `VRCameraMoveHelper.MoveToCurrent` 挂 postfix。
 /// 目的：让**所有**调用方都获得俯仰补回 —— 不只是本插件的对齐路径，
 /// 还包括「10 个机位快捷键」（`MoveToCamera(slot)` → `Import(slot)` + `MoveToCurrent()`，
 /// 原版一样会抹掉俯仰，用户实测 10 个机位按过去全是水平）。
 /// 需要在工作室就绪后调用（此时 KKCharaStudioVRPlugin.dll 一定已加载）。
 /// </summary>
 private static HarmonyLib.Harmony _moveHookHarmony;
 private static bool _moveHookInstalled;

 internal static void InstallMoveToCurrentHook(HarmonyLib.Harmony harmony)
 {
 _moveHookHarmony = harmony;
 TryInstallMoveToCurrentHook();
 }

 internal static void RetryMoveToCurrentHook()
 {
 if (_moveHookInstalled) return;
 if (_moveHookHarmony == null) return;
 TryInstallMoveToCurrentHook();
 }

 private static void TryInstallMoveToCurrentHook()
 {
 try
 {
 Type t = MoveHelperType;
 if (t == null)
 {
 FixLog.Debug("MoveToCurrent 钩子：拿不到 VRCameraMoveHelper 类型（稍后重试）。");
 return;
 }
 MethodInfo m = t.GetMethod("MoveToCurrent", AllInstance);
 if (m == null)
 {
 FixLog.Debug("MoveToCurrent 钩子：拿不到方法（稍后重试）。");
 return;
 }
 _moveHookHarmony.Patch(m, postfix: new HarmonyMethod(
 AccessTools.Method(typeof(VRRef), "MoveToCurrentPostfix")));
 _moveHookInstalled = true;
 FixLog.Info("已挂载钩子: VRCameraMoveHelper.MoveToCurrent [俯仰补回·覆盖 10 机位快捷键]");
 }
 catch (Exception e)
 {
 FixLog.Debug("挂载 MoveToCurrent 钩子失败（稍后重试）: " + e.Message);
 }
 }

 /// <summary>MoveToCurrent 的 postfix：原版刚把俯仰抹掉，这里补回场景卡的完整旋转。</summary>
 public static void MoveToCurrentPostfix()
 {
 try
 {
 if (!CfgAlignCameraPitchOn()) return;
 ApplyCardPitch();
 }
 catch (Exception e) { FixLog.Debug("MoveToCurrentPostfix 异常: " + e.Message); }
 }

 internal static bool ApplyCardPitch()
 {
 try
 {
 Vector3 pos, rotate, distance; float fov;
 if (!TryGetStudioCamera(out pos, out rotate, out distance, out fov))
 return false;
 Transform origin = GetVROrigin();
 if (origin == null) return false;
 Transform head = GetVRHead();
 if (head == null) return false;

 // 一步到位算理论目标，不再"接着原版的残局修正"。
 // 旧实现保留原版压平后的 head 位置 —— 而原版 MoveTo 的 RemoveXZRot 对带俯仰的卡
 // 本身就差十几厘米（用户实测"位置总是靠下一点"）→ 这里直接用与原版
 // MoveToCurrent **同一个公式**（压平前）算目标落点，位置旋转双双精确：
 // targetHead = pos + Euler(rotate) * distance
 Vector3 targetHead = pos + Quaternion.Euler(rotate) * distance;
 Vector3 localPos = origin.InverseTransformPoint(head.position);
 Quaternion localRot = Quaternion.Inverse(origin.rotation) * head.rotation;

 Quaternion target = Quaternion.Euler(rotate);
 origin.rotation = target * Quaternion.Inverse(localRot);
 origin.position = targetHead - origin.rotation * localPos;

 FixLog.Key("场景卡相机旋转已补回（含俯仰）: rotate=" + rotate.ToString("0.0")
 + "，视角与场景卡一致（世界会随俯仰倾斜，不适应可关 AlignCameraPitch）。");
 return true;
 }
 catch (Exception e)
 {
 FixLog.Debug("ApplyCardPitch err: " + e.Message);
 return false;
 }
 }

 /// <summary>
 /// 读游戏 studio.cameraCtrl 当前的机位数据（pos / rotate / distance / fov）。
 ///
 /// 用途：加载场景卡时，游戏会调 m_CameraCtrl.Import(sceneInfo.cameraSaveData) 把
 /// 场景卡保存的机位灌进 cameraCtrl。我们要"等这份数据真的落地"之后再调 MoveToCurrent，
 /// 否则读到的还是上一张卡的旧值 → 机位有概率对不上。
 ///
 /// 成功返回 true，并把 pos/rotate/distance/fov 填进 out 参数。
 /// </summary>
 internal static bool TryGetStudioCamera(out Vector3 pos, out Vector3 rotate,
 out Vector3 distance, out float fov)
 {
 pos = Vector3.zero; rotate = Vector3.zero; distance = Vector3.zero; fov = 0f;
 try
 {
 Studio.Studio studio = Studio.Studio.IsInstance() ? Studio.Studio.Instance : null;
 if (studio == null || studio.cameraCtrl == null) return false;

 Studio.CameraControl.CameraData d = studio.cameraCtrl.Export();
 if (d == null) return false;

 pos = d.pos;
 rotate = d.rotate;
 distance = d.distance;
 fov = d.parse;
 return true;
 }
 catch (Exception e)
 {
 FixLog.Debug("TryGetStudioCamera 异常: " + e.Message);
 return false;
 }
 }

#if !LEAN
 // 【精简版瘦身】诊断辅助 + 已删除的 F6 功能入口。
 /// <summary>
 /// 用原版公式算出「VR rig 应该摆到哪里」，但不真的去摆。
 ///
 /// 复刻 0.0.3 VRCameraMoveHelper.MoveToCurrent() 的数学：
 /// tobeHeadPos = cameraCtrl.pos + Quaternion.Euler(rotate) * distance
 /// tobeHeadRot = Quaternion.Euler(rotate)
 /// 然后反推「head 落在 tobeHeadPos 时，origin 应该在哪」。
 ///
 /// 因为 MoveTo() 内部把 origin 先挂到 moveDummy 再摆到 tobeHeadPos，
 /// 所以最终 head.position ≈ tobeHeadPos，而 origin.position =
 /// tobeHeadPos - (head.position - origin.position)（即保持头显相对原点的偏移）。
 /// 我们只需要一个稳定的"是否已对齐"判据，用 head 与目标点比较即可。
 /// </summary>
 internal static bool TryGetExpectedHead(out Vector3 headPos, out Quaternion headRot)
 {
 headPos = Vector3.zero;
 headRot = Quaternion.identity;

 Vector3 pos, rotate, distance; float fov;
 if (!TryGetStudioCamera(out pos, out rotate, out distance, out fov))
 return false;

 headPos = pos + Quaternion.Euler(rotate) * distance;
 headRot = Quaternion.Euler(rotate);
 return true;
 }
#else
 // 【LEAN】整段删除（诊断辅助 / 已删除的 F6 强制关轴功能）
#endif

 /// <summary>
 /// 用原版公式算出「VR rig 应该摆到哪里」，并把结果**按 MoveTo() 的真实行为压平**。
 ///
 /// ── 为什么需要这个函数──
 /// 原版 VRCameraMoveHelper.MoveTo()（反编译 577–599 行，0.4.0 源码 353–380 行**完全一致**）：
 /// moveDummy.transform.position = VR.Camera.Head.position;
 /// moveDummy.transform.rotation = RemoveXZRot(VR.Camera.Head.rotation);
 /// vROrigin.transform.parent = moveDummy.transform; // origin 挂到 dummy 下
 /// moveDummy.transform.position = tobeHeadPos; // ← dummy 移到目标
 /// moveDummy.transform.rotation = tobeHeadRot;
 /// vROrigin.transform.parent = parent; // 摘回来
 /// vROrigin.transform.rotation = RemoveXZRot(vROrigin.transform.rotation); // ← 压平！
 ///
 /// ── 精确推演「MoveTo 之后 head 会落在哪」──
 /// 记：调用前 head 相对 origin 的**局部**偏移为 L（localOffset = origin.InverseTransformPoint(head.position)），
 /// 调用前 origin 的旋转为 R0。
 /// 步骤 1：dummy 摆到 head 当前位置、朝向 = R0 压平后（RemoveXZRot(R0)）。
 /// origin 挂上去 → origin 的世界变换 = dummy 的世界变换（因为挂 parent 时 Unity 保持世界姿态）。
 /// 此时 origin 的旋转 = RemoveXZRot(R0)。head 仍跟着 origin → head 世界位置不变。
 /// 步骤 2：dummy 移到 tobeHeadPos / tobeHeadRot → origin 跟着走，
 /// 于是 **head 的世界位置 = tobeHeadPos**（这一段是精确的！）。
 /// 步骤 3：origin 摘回原 parent（Unity 保持 origin 世界姿态，位置/旋转不变）。
 /// 步骤 4：**origin.rotation 被压平** = RemoveXZRot(tobeHeadRot) = 只留 yaw 的 flatRot。
 /// ⚠️ 但 origin 的**位置没变**，而 origin 绕自身轴旋转会带动子物体 head 移动：
 /// head.worldPos = origin.pos + flatRot * L
 /// 其中 L 是步骤 1 里"origin 挂到 dummy 之后、head 相对 origin 的局部偏移"。
 ///
 /// 关键：步骤 1 里 origin 挂到 dummy 时**旋转被换成了 RemoveXZRot(R0)**，
 /// 所以 L 是相对于「压平后的原点坐标系」的偏移。但 R0 的 x/z 本来就是 0 的时候
 /// （StandingMode 下 origin 通常只有 yaw），RemoveXZRot(R0) == R0，L 不变。
 ///
 /// ── 判据为什么这样取 ──
 /// 若直接用「当前 origin」反推，本函数是**在调用 MoveTo 之前**被调用的，
 /// 此刻 origin 还是旧的；而且循环里每轮 MoveTo 都改 origin，
 /// 会让"可达目标"每轮漂移 → 判据自身不稳定。
 /// 所以只依赖 cameraCtrl 数据 + 一次性的头显局部偏移，目标点在整个对齐过程中恒定。
 /// 本版改成**只依赖 cameraCtrl 数据 + 一次性的头显局部偏移**，
 /// 目标点在整个对齐过程中**恒定**。
 /// </summary>
 internal static bool TryGetReachableHead(out Vector3 headPos, out Quaternion headRot)
 {
 headPos = Vector3.zero;
 headRot = Quaternion.identity;

 Vector3 pos, rotate, distance; float fov;
 if (!TryGetStudioCamera(out pos, out rotate, out distance, out fov))
 return false;

 Quaternion rawRot = Quaternion.Euler(rotate);
 Vector3 rawHead = pos + rawRot * distance; // MoveTo 步骤 2 摆出的 head 位置

 // 复刻 RemoveXZRot：只保留 yaw
 Vector3 e = rawRot.eulerAngles;
 e.x = 0f;
 e.z = 0f;
 Quaternion flatRot = Quaternion.Euler(e);

 // 步骤 4：origin 位置不动、旋转被压平 → head 被"甩"到 origin.pos + flatRot * L。
 // 我们需要 L = head 相对 origin 的局部偏移（在压平前的坐标系里）。
 // 数值上 L = origin.InverseTransformPoint(head.position) 即可 —— 这是**当前**值，
 // 而当前 origin 恰恰就是上一次 MoveTo 后的状态，L 在 MoveTo 前后**恒定**
 // （MoveTo 只整体搬运 origin，不改变它的子物体局部关系）。
 Transform head = GetVRHead();
 Transform origin = GetVROrigin();
 if (head != null && origin != null)
 {
 Vector3 L = origin.InverseTransformPoint(head.position);
 headPos = rawHead + flatRot * L; // = (origin 将停的位置) + flatRot * L
 // 注：origin 将停在 rawHead 处（步骤 2/3 后位置不变）
 }
 else
 {
 headPos = rawHead;
 }
 headRot = flatRot;
 return true;
 }

#if !LEAN
 // 【精简版瘦身】诊断辅助 + 已删除的 F6 功能入口。
 /// <summary>
 /// rig 是否已经「足够对齐」。判据 = head 到**可达目标**的距离，
 /// 而不是到原始理论目标的距离 —— 后者对带俯仰的机位天然差 0.2m 以上。
 /// </summary>
 internal static bool IsHeadAlignedToCamera(float tolerance)
 {
 Vector3 want; Quaternion wantRot;
 if (!TryGetReachableHead(out want, out wantRot)) return false;
 Transform head = GetVRHead();
 if (head == null) return false;
 return (head.position - want).sqrMagnitude < (tolerance * tolerance);
 }
#else
 // 【LEAN】整段删除（诊断辅助 / 已删除的 F6 强制关轴功能）
#endif

 /// <summary>取 VR 头显 Transform（VR.Camera.SteamCam.head），用于校验对齐结果。</summary>
 internal static Transform GetVRHead()
 {
 try
 {
 if (!VR.Active || VR.Camera == null || VR.Camera.SteamCam == null) return null;
 return VR.Camera.SteamCam.head;
 }
 catch (Exception e)
 {
 FixLog.Debug("GetVRHead 异常: " + e.Message);
 return null;
 }
 }

 /// <summary>记住见过的 Mode 类型，这样 Mode 变成 null 之后还能按原类型重建。</summary>
 private static Type _lastModeType;

 /// <summary>强制重建 GenericStandingMode：销毁旧 Mode 后按它自己的类型重新 SetMode。</summary>
 internal static bool RebuildStandingMode()
 {
 try
 {
 VRManager mgr = VR.Manager;
 if (mgr == null) return false;

 VRGIN.Modes.ControlMode mode = mgr.Mode;
 if (mode != null)
 _lastModeType = mode.GetType();

 Type modeType = (mode != null) ? mode.GetType() : _lastModeType;

 if (mode == null && modeType == null)
 {
 // 从来没见过 Mode，退回用 0.0.3 自带的恢复方法。
 return CallForceResetAsStandingMode();
 }

 if (mode != null)
 UnityEngine.Object.DestroyImmediate(mode);

 MethodInfo setMode = typeof(VRManager).GetMethod("SetMode", AllInstance);
 if (setMode == null) return false;
 setMode.MakeGenericMethod(modeType).Invoke(mgr, null);
 return true;
 }
 catch (Exception e)
 {
 FixLog.Warn("重建 StandingMode 失败: " + e.Message);
 return false;
 }
 }
 }
}
