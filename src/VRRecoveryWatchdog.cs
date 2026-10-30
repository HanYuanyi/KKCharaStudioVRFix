using System;
using System.Collections;
using UnityEngine;
using Studio;
using VRGIN.Controls;
using VRGIN.Core;
using VRGIN.Modes;

namespace KKCharaStudioVRFix
{
 /// <summary>
 /// VR 看门狗（v1.3.0）。
 ///
 /// 【核心修复：掩码守卫】
 /// 诊断日志证明：BUG 状态下 VR 相机的 cullingMask 会被整体替换成"接近全 32 层"
 /// （= 某台全图层相机被 VRCamera.RegisterSlave 登记，触发 UpdateCameraConfig()
 /// 用所有从相机掩码的并集覆盖）。而 367 个引导球(FK/IK 球)全都在 Studio/Col(27) 图层，
 /// 于是它们全部显示出来。按原生规则需要同时处理 27 和 28。
 ///
 /// 现在按原生 Studio.CameraControl.ReflectOption() 的规则处理 27 和 28 两层，
 /// 并在掩码被污染时把它恢复成插件学到的"正常掩码"。
 /// </summary>
 internal class VRRecoveryWatchdog : MonoBehaviour
 {
 private static VRRecoveryWatchdog _instance;

 private static bool _transitionActive;
 private static float _transitionStartTime;
 private static bool _pendingRecovery;
 private static bool _recovering;

 /// <summary>本次场景卡加载是否已经做过"对齐到场景卡机位"。</summary>
 private static bool _sceneCardAligned;

 /// <summary>
 /// 本次场景卡加载是否**已由 `SceneTransitionHook.ImportPostfix`** 对齐过。
 /// 与 `_sceneCardAligned` 的区别：本标记**不受** `NotifyTransitionStart` 复位
 /// （一次加载会触发多次 transition start，见 NotifySceneCardAlignedByImport 的注释），
 /// 只在「加载结束」收口里被清掉。
 /// </summary>
 private static bool _sceneCardAlignedByImport;

 /// <summary>本场是否已做过「自动预热」（等价于手动先加载一张没地图的场景卡）。</summary>
 private static bool _autoPrimed;

 /// <summary>用户是否已经自己加载过场景卡（加载过就不再自动预热，避免毁档）。</summary>
#if !LEAN
 // 【精简版瘦身】只有完整版用得到
 private static bool _userLoadHappened;
#endif

 /// <summary>
 /// 过渡态的最长容忍时间。超过就强制解除 ——
 /// 否则一旦 RecoverAfterLoadCo() 卡在等待循环里，「_transitionActive 永远为 true」，
 /// SafeCheckMissingMode() 就再也不会重建已经死掉的 Mode（ 日志里
 /// 从头到尾没有一条「检测到 VR.Manager.Mode == null…重建」就是这个原因）。
 /// </summary>
 private const float TransitionMaxSeconds = 8.0f;

 /// <summary>过渡态是否仍在有效期内（带超时自动解除）。</summary>
 private static bool TransitionActive
 {
 get
 {
 if (!_transitionActive) return false;
 if (Time.realtimeSinceStartup - _transitionStartTime > TransitionMaxSeconds)
 {
 _transitionActive = false;
 FixLog.Debug("过渡态超时（" + TransitionMaxSeconds + " 秒）已自动解除，恢复看门狗常规检查。");
 return false;
 }
 return true;
 }
 }

#if !LEAN
 // 【精简版瘦身】只有完整版用得到
 private float _deadSince = -1f;
#endif
 private float _lastHealthCheck;
 private float _lastModeCheck;
 private float _lastMaskFixTime;

#if NEWFIX
 // ================= 【NEWFIX】"插一句"替代定时扫描 =================
 // 模式死亡：以前每 5 秒检查 VR.Manager.Mode 是不是 null。
 // 现在改成挂 ControlMode.OnDestroy（对象销毁那一刻），当场置标志、下一帧立刻抢救。
 private static bool _modeDestroyHooked;
 private static bool _modeDisableHooked;
 private static bool _modeDestroyPending;
 private static bool _modeHookTried;
 private static HarmonyLib.Harmony _modeHookHarmony;

 // ================= 【NEWFIX ★★★】rig 保活：从"诊断"里搬出来 =================
 // 现在改成**事件驱动 + 状态变化检测**，放在核心路径里，与诊断完全无关：
 // ① `VRCamera.OnAwake` 事件 → 新 rig 一出生就补 DDL；
 // ② 每帧比一次 rigId（缓存相机 + 一个 int 比较，不是定时器）→ 兜住事件漏掉的路径。
 private static bool _rigAwakeHooked;
 private static bool _sceneLoadHooked;
 private static HarmonyLib.Harmony _rigAwakeHarmony;

 private static void TryInstallRigAwakeHook()
 {
 if (_rigAwakeHooked) return;
 try
 {
 System.Reflection.MethodInfo oa = typeof(VRCamera).GetMethod("OnAwake",
 System.Reflection.BindingFlags.Instance
 | System.Reflection.BindingFlags.Public
 | System.Reflection.BindingFlags.NonPublic);
 if (oa == null) return;

 if (_rigAwakeHarmony == null)
 _rigAwakeHarmony = new HarmonyLib.Harmony(KKCharaStudioVRFixPlugin.Guid + ".rigawake");
 _rigAwakeHarmony.Patch(oa, postfix: new HarmonyLib.HarmonyMethod(
 typeof(VRRecoveryWatchdog).GetMethod("RigAwakePostfix")));

 HarmonyLib.Patches info = HarmonyLib.Harmony.GetPatchInfo(oa);
 if (info != null && info.Postfixes != null)
 {
 foreach (HarmonyLib.Patch p in info.Postfixes)
 {
 if (p != null && p.PatchMethod != null
 && p.PatchMethod.Name == "RigAwakePostfix") { _rigAwakeHooked = true; break; }
 }
 }
 if (KKCharaStudioVRFixPlugin.DiagOn)
 FixLog.Key("【跳变修复·NEWFIX】VRCamera.OnAwake 事件 = "
 + (_rigAwakeHooked ? "已挂上（新 rig 一出生就补 DontDestroyOnLoad）" : "未挂上（靠每帧 rigId 比对兜底）"));

 // 【NEWFIX ★★★ 2026-10-21 第二次修】再挂「场景加载开始」事件。
 // 为什么还需要它：实测 rig 在 Mode 被销毁那一刻的 scene 仍是**地图场景**（如 2FToilet），
 // 说明 DDL 要么没生效、要么被后来的重新挂接冲掉了。
 // 而这个 Unity 版本**没有** `sceneUnloading`（只有 sceneUnloaded，太晚），
 // 所以在 `SceneManager.LoadScene / LoadSceneAsync` 的**前缀**里补一次 DDL ——
 // 这是"场景即将卸载"之前的最后一个可控时刻。
#if AB_NOSCENELOAD
 // ===== A/B 测试版：**故意不挂**「场景加载前补 DDL」 =====
 // 目的：只留 VRCamera.OnAwake + rigId 变化检测，看跳变会不会回来。
 // 跳了 → 「场景加载前补 DDL」才是关键那一刀；
 // 不跳 → OnAwake 就够，这个钩子可以删。
 _sceneLoadHooked = false;
 if (KKCharaStudioVRFixPlugin.DiagOn)
 FixLog.Key("【A/B 测试】已按 AB_NOSCENELOAD 编译：**不挂**场景加载前 DDL，只留 OnAwake + rigId 检测");
#else
 try
 {
 int n = 0;
 foreach (System.Reflection.MethodInfo m in typeof(UnityEngine.SceneManagement.SceneManager).GetMethods(
 System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
 {
 // 【NEWFIX】把**所有**场景切换入口都挂上（不只是 LoadScene）：
 // LoadScene / LoadSceneAsync —— 切换场景（会先卸载旧场景）
 // UnloadScene / UnloadSceneAsync —— 单纯卸载某个场景
 // 这四个是 Unity 5.6 里全部的托管场景切换 API；只要走其中一个，
 // 我们就能在"卸载之前"把 rig 拉回 DontDestroyOnLoad —— **完全事件驱动，零轮询**。
 if (m.Name != "LoadScene" && m.Name != "LoadSceneAsync"
 && m.Name != "UnloadScene" && m.Name != "UnloadSceneAsync") continue;
 try
 {
 _rigAwakeHarmony.Patch(m, prefix: new HarmonyLib.HarmonyMethod(
 typeof(VRRecoveryWatchdog).GetMethod("BeforeSceneLoadPrefix")));
 n++;
 }
 catch { }
 }
 _sceneLoadHooked = n > 0;
 if (KKCharaStudioVRFixPlugin.DiagOn)
 FixLog.Key("【跳变修复·NEWFIX】场景加载前事件 = "
 + (_sceneLoadHooked ? ("已挂上 " + n + " 个 LoadScene/LoadSceneAsync") : "未挂上"));
 }
 catch (Exception e) { FixLog.Debug("挂 SceneManager 失败: " + e.Message); }
#endif
 }
 catch (Exception e) { FixLog.Debug("TryInstallRigAwakeHook 异常: " + e.Message); }
 }

 /// <summary>
 /// 场景加载**开始之前**：把 rig 从即将卸载的场景里拽出来（补 DontDestroyOnLoad）。
 /// 这是"场景卸载前"唯一能挂到的事件（本 Unity 版本没有 sceneUnloading）。
 /// </summary>
 public static void BeforeSceneLoadPrefix(System.Reflection.MethodBase __originalMethod)
 {
 try
 {
 VRCamera cam = VRRef.FindExistingVRCamera();
 if (cam == null) return;
 bool ok = VRRef.EnforceRigDDL(cam);
#if JUMPWATCH
 // 正向确认：记一行"是哪个 API 触发的 + rig 在不在 DDL"
 string api = __originalMethod != null ? __originalMethod.Name : "?";
 try { LeanJumpWatch.OnBeforeSceneLoad(ok, api); } catch { }
#endif
 }
 catch { }
 }

 /// <summary>由 Harmony 调用：新 rig 刚创建 → 立刻补 DontDestroyOnLoad。</summary>
 public static void RigAwakePostfix(VRCamera __instance)
 {
 try
 {
 bool ok = VRRef.EnforceRigDDL(__instance);
#if JUMPWATCH
 try { LeanJumpWatch.OnDdlResult(ok, "VRCamera.OnAwake"); } catch { }
#endif
 }
 catch { }
 }

 private static void TryInstallModeDestroyHook()
 {
 if (_modeHookTried) return;
 try
 {
 System.Reflection.MethodInfo od = typeof(ControlMode).GetMethod("OnDestroy",
 System.Reflection.BindingFlags.Instance
 | System.Reflection.BindingFlags.Public
 | System.Reflection.BindingFlags.NonPublic);
 if (od == null) return;
 _modeHookTried = true;

 if (_modeHookHarmony == null)
 _modeHookHarmony = new HarmonyLib.Harmony(KKCharaStudioVRFixPlugin.Guid + ".modedestroy");
 _modeHookHarmony.Patch(od, prefix: new HarmonyLib.HarmonyMethod(
 typeof(VRRecoveryWatchdog).GetMethod("ModeDestroyPrefix")));

 // 校验：补丁表里必须真有我们这一条
 HarmonyLib.Patches info = HarmonyLib.Harmony.GetPatchInfo(od);
 if (info != null && info.Prefixes != null)
 {
 foreach (HarmonyLib.Patch p in info.Prefixes)
 {
 if (p != null && p.PatchMethod != null
 && p.PatchMethod.Name == "ModeDestroyPrefix") { _modeDestroyHooked = true; break; }
 }
 }

 // 【NEWFIX ★】再挂一个 OnDisable —— 有些销毁路径只把组件禁用/整块 GO 关掉，
 // OnDestroy 不一定回调到。OnDisable 也是事件，不需要轮询。
 try
 {
 System.Reflection.MethodInfo odis = typeof(ControlMode).GetMethod("OnDisable",
 System.Reflection.BindingFlags.Instance
 | System.Reflection.BindingFlags.Public
 | System.Reflection.BindingFlags.NonPublic);
 if (odis != null)
 {
 _modeHookHarmony.Patch(odis, prefix: new HarmonyLib.HarmonyMethod(
 typeof(VRRecoveryWatchdog).GetMethod("ModeDestroyPrefix")));
 HarmonyLib.Patches i2 = HarmonyLib.Harmony.GetPatchInfo(odis);
 if (i2 != null && i2.Prefixes != null)
 {
 foreach (HarmonyLib.Patch p in i2.Prefixes)
 {
 if (p != null && p.PatchMethod != null
 && p.PatchMethod.Name == "ModeDestroyPrefix")
 { _modeDestroyHooked = true; _modeDisableHooked = true; break; }
 }
 }
 }
 }
 catch (Exception e) { FixLog.Debug("挂 ControlMode.OnDisable 失败: " + e.Message); }
 }
 catch (Exception e)
 {
 FixLog.Warn("【NEWFIX】挂 ControlMode.OnDestroy 失败（保留 5 秒轮询兜底）: " + e.Message);
 }

 if (KKCharaStudioVRFixPlugin.DiagOn)
 FixLog.Key("【NEWFIX】模式销毁事件 = " + (_modeDestroyHooked
 ? ("已挂上（OnDestroy" + (_modeDisableHooked ? " + OnDisable" : "") + "）→ 轮询停用，销毁那一刻立刻抢救")
 : "未挂上 → 保留 5 秒轮询"));
 }

 /// <summary>由 Harmony 调用（OnDestroy 前缀）：只置标志，不在销毁过程中做事。</summary>
 public static void ModeDestroyPrefix()
 {
#if JUMPWATCH
 // ===== [REMOVABLE:JUMPWATCH-BEGIN] 跳变检测（已默认关闭）=====
 try { LeanJumpWatch.OnModeGone("ControlMode.OnDestroy/OnDisable"); } catch { }
 // ===== [REMOVABLE:JUMPWATCH-END] =====
#endif
 try { _modeDestroyPending = true; }
 catch { }
 }
#endif
 private float _lastModeRebuild = -999f;
 private float _lastColorCheck;
 private float _lastCanvasCheck;
 private float _lastStrayCheck;

 private const float HealthCheckInterval = 5.0f;
 private const float ModeCheckInterval = 5.0f;

 /// <summary>相机掩码修正的周期。原来每帧跑，是掉帧主因之一。</summary>
 private const float MaskFixInterval = 5.0f;
 private const float ModeRebuildCooldown = 5.0f;

 /// <summary>
 /// 色彩分级兜底周期：0.5s → 2.0s。
 ///
 /// 理由：色彩分级被关掉**只发生在 CopyFX（加载那一刻）**，而那一刻已经被
 /// `ColorGradingFix.CopyFXPrefix` / `CopyFXPostfix` 两个 Harmony 钩子**当场修好**。
 /// 这个周期检查本来就只是兜底 —— 而它每跑一次就要
 /// `GetComponents&lt;MonoBehaviour&gt;()`（分配数组）+ 每个组件 `GetType().Name`（分配字符串）。
 /// 2 秒兜底足够了。
 /// </summary>
 private const float ColorCheckInterval = 30.0f;

 /// <summary>
 /// 把"回滚历史图层污染"的周期从 2 秒放宽到 10 秒。
 /// 这个检查是给**历史遗留污染**兜底用的（v1.7.0 曾把世界物件永久搬进 UI 图层），
 /// 稳态下根本不会有新污染 —— 每 2 秒扫一遍 48+ 个画布纯属浪费。
 /// 污染一旦发生也能在被发现的 10 秒内修回来，对实际观感没有区别。
 /// </summary>
 private const float StrayCheckInterval = 30.0f;

 /// <summary>相机背景同步周期（原来每帧，默认配置下本来就是关的）。</summary>
 private const float BackgroundCheckInterval = 30.0f;
 private float _lastBgCheck;

 /// <summary>
 /// `OriginGuardFix.Tick()` / `DragStateFix.Tick()` 的调用周期。
 ///
 /// 这两个函数此前在 `LateUpdate` 里**每帧**跑：
 /// · OriginGuardFix.Tick：取 origin Transform + 读 mode + 算 4 个归一化角度
 /// + 比较"是否与候选一致"（含 Quaternion.Angle）；
 /// · DragStateFix.Tick：先 `RefreshCache()`，再 `GetComponent&lt;Controller&gt;()`
 /// + 反射读 `lockRotXZ` 字段，**每次都要调**（因为要靠实例 ID 变化判定重建）。
 ///
 /// 在 VR 里一帧 60~90 次，两者叠加是稳定的 CPU 开销（与加载无关的**常态掉帧**）。
 /// 但它们要捕捉的状态变化都是"用户手动动作"或"加载后重建"级别的，
 /// **几十毫秒的检测延迟完全无感** —— 所以改成按周期调用。
 ///
 /// 注意与掩码/色彩那些不同：这两个不能放太宽，因为
 /// DragStateFix 靠"实例 ID 变化"判定工具重建，需要足够密才能及时恢复解锁状态。
 ///
 /// 0.05s → 0.20s（每 12 帧 @60fps）：
 /// · DragStateFix 的重建检测延迟变成 0.2 秒 —— 用户按下加载按钮后
 /// 0.2 秒内恢复解锁状态，人眼分辨不出来（它靠实例 ID，不靠时间）；
 /// · OriginGuardFix.Tick 只是"记录玩家姿态"，本身就带"稳定 N 帧才写入"的机制，
 /// 0.2 秒采样反而让"稳定帧"更快达标。
 /// 这一项直接砍掉 75% 的反射调用。
 /// </summary>
 private const float OriginTickInterval = 1.0f;
#if NEWFIX
 // 【NEWFIX】有 Awake 事件兜住"重建那一刻"，周期扫描从 1 秒放慢到 5 秒（只是兜底）。
 private const float DragTickInterval = 5.0f;
#else
 private const float DragTickInterval = 1.0f;
#endif

 private float _lastOriginTick;
 private float _lastDragTick;

 private float _lastCameraSyncTime;
 private float _cameraSyncWindowStart;
 private int _cameraSyncCount;
 private bool _cameraSyncDisabled;

 private static float _lastMaskLogTime;

 /// <summary>插件学到的"正常掩码"（只在掩码干净时累积学习）。</summary>
 private static int _goodMask;


 // ---------------- 外部通知 ----------------

 internal static void NotifyTransitionStart()
 {
 _transitionActive = true;
 _transitionStartTime = Time.realtimeSinceStartup;
 _pendingRecovery = false;
#if !LEAN
 _userLoadHappened = true; // 用户已自己加载过 → 自动预热永久跳过
#endif

 // 每次新的加载开始都重置"已对齐场景卡机位"标记。
 // 注意：**不清除 VRRef 的场景卡标记** —— 那个标记由
 // SceneTransitionHook 在 Studio.LoadScene* 的 prefix 里打，
 // 这里（可能是地图路径的前置钩子）不该抢先把它抹掉。
 _sceneCardAligned = false;

 // 加载开始 → 让色彩分级的存在性缓存失效
 // （VRGIN 的 CopyFX 会在这一刻 DestroyImmediate 掉 VR 相机上的后处理组件）。
 try { ColorGradingFix.InvalidateExistenceCache(); }
 catch (Exception e) { FixLog.Debug("InvalidateExistenceCache 异常: " + e.Message); }

 // 加载开始前先把 VR 原点快照下来。
 // 关键：用户手动「三键解锁 + 拖动世界」出来的状态会被完整保住，
 // 加载结束后只有在原点被弄坏时才还原，不再无条件覆盖。
 SnapshotOrigin();

 // 同时让连续守卫记一份「健康原点」——
 // 它的回写窗口不依赖恢复协程时机，能压过 StandingMode.OnStart() 的归零。
 try { OriginGuardFix.SnapshotNow("加载开始"); }
 catch (Exception e) { FixLog.Debug("OriginGuardFix.SnapshotNow 异常: " + e.Message); }

 FixLog.Info(">> 场景/地图加载开始");
 }

 internal static void NotifyTransitionQueued()
 {
 _pendingRecovery = true;
 FixLog.Info(">> 场景/地图加载结束");
 }

 /// <summary>
 /// 外部（`SceneTransitionHook.ImportPostfix`）**已经**在 Import 的 postfix
 /// 里调过一次 `MoveToCurrent()` 时，用本方法把标记置位 —— 让看门狗在「加载结束」
 /// 收口里**跳过**重复对齐。
 ///
 /// ── 为什么不能直接用 `_sceneCardAligned` ──
 /// `_sceneCardAligned` 会在**每一次** `NotifyTransitionStart()` 里被复位（第 130 行），
 /// 而一次场景卡加载会触发**多次** transition start（`LoadScene` prefix → `AddMap`
 /// prefix → `UnloadBaseScene` prefix…）。`Import` 在 `AddMap` **之后**执行，
 /// 所以在这里置位后，后面任何一次 transition start 都会把它清掉，
 /// 看门狗于是又对齐一次 → 又是一跳。
 ///
 /// 解决：另用一个**只由「加载结束」清掉**的标记 `_sceneCardAlignedByImport`，
 /// 它不受 `NotifyTransitionStart` 影响。
 /// </summary>
 internal static void NotifySceneCardAlignedByImport()
 {
 _sceneCardAlignedByImport = true;
 }

 // ---------------- MonoBehaviour ----------------

 private void Awake()
 {
 _instance = this;
 }

 /// <summary>
 /// 启动后延迟几秒做一次「历史污染回滚」。
 ///
 /// v1.7.0 把 WorldSpace 画布（手柄图标所在的层级）整棵子树永久改成了 UI(5) 图层。
 /// 这个改动**保存在场景对象上**，换插件版本也不会自动还原 —— 只要还用同一个
 /// CharaStudio 进程，污染就一直在。所以 启动时必须主动回滚一次。
 /// </summary>
 private void Start()
 {
 StartCoroutine(StartupCleanupCo());
#if !LEAN
 StartCoroutine(AutoPrimeCo());
#else
 // 【LEAN】AutoPrimeMapLoad 整项删除
#endif
 }

 /// <summary>
 /// 自动「垫卡」预热 —— 让开机后第一张有地图的场景卡不再蓝屏跳变。
 ///
 /// 背景（反编译实证）：每张场景卡加载都会走
 /// InitScene() → Map.ReleaseMap() → Manager.Scene.UnloadBaseScene()
 /// → Resources.UnloadUnusedAssets()（一次性重活）。
 /// 没地图的卡只做 ReleaseMap、不换基础场景，这次重活在桌面期安静跑完；
 /// 有地图的卡会把重活和地图加载 / SteamVR 出图挤在同一时刻 → 长卡帧 →
 /// 蓝屏 + 镜头跳。手动「先加载一张没地图的卡」能免疫后续所有加载
 /// （0.0.3 原版实测如此，与本插件无关）。本协程自动重放这个动作。
 ///
 /// 安全约束：仅 VR 模式；每场一次；用户一旦自己加载过场景卡就永远跳过；
 /// 只在工作室空闲时执行（ReleaseMap 在空工作室上是无副作用的走场）。
 /// </summary>
#if !LEAN
 private IEnumerator AutoPrimeCo()
 {
 if (!IsVrUsable()) yield break;
 if (_autoPrimed) yield break;
 if (_userLoadHappened)
 {
 FixLog.Info("自动预热跳过：用户已先加载过场景卡，本次不再预热。");
 yield break;
 }
#if LEAN
 yield break; // 【LEAN·第三批删除】AutoPrimeMapLoad 整项删除（已被 A/B 日志否证）
#else
 if (KKCharaStudioVRFixPlugin.CfgAutoPrimeMapLoad == null) yield break;
 if (!KKCharaStudioVRFixPlugin.CfgAutoPrimeMapLoad.Value) yield break;
#endif

 // 不再死等 240 帧 —— 上轮日志：用户开机 29 秒就点加载，
 // 预热还没跑（03:19:42），第一张地图卡的巨卡帧（5627ms）已发生（03:19:31）。
 // 改为：等工作室结束加载 + 稳 2 秒就触发（仍遵守"用户一加载就永久跳过"）。
 float deadline = Time.realtimeSinceStartup + 60f;
 while (VRRef.IsGameLoading() && Time.realtimeSinceStartup < deadline)
 yield return null;
 if (VRRef.IsGameLoading()) yield break;
 if (_userLoadHappened) yield break;
 for (int i = 0; i < 120; i++) yield return null; // 稳 2 秒
 if (_userLoadHappened) yield break;

 _autoPrimed = true;
 try
 {
 float t0 = Time.realtimeSinceStartup;
 FixLog.Key("auto-prime: 开始在桌面期预热（等价于手动先加载一张没地图的场景卡）。");

 // ① 没地图的卡实际走的就是这一句
 Studio.Map map = Studio.Map.Instance;
 if (map != null) map.ReleaseMap();

 // ② 直接触发一次性重活 Resources.UnloadUnusedAssets()。
 // 依据：跳变=巨卡帧（5627ms/6878ms/3131ms/1800ms），
 // 而"先垫一张没地图的卡就没事"说明垫卡把某笔一次性开销在桌面期蒸发了。
 // ReleaseMap 在空工作室上只走壳（_baseScene=null 什么也不卸），
 // 所以这里把真正的重活也提前做掉。
 try
 {
 Resources.UnloadUnusedAssets();
 FixLog.Key("auto-prime: Resources.UnloadUnusedAssets() 完成，耗时 "
 + (Time.realtimeSinceStartup - t0).ToString("0.0")
 + "s。之后直接加载有地图的场景卡不应再撞上数秒卡顿/蓝屏。");
 }
 catch (Exception e2)
 {
 FixLog.Debug("auto-prime: UnloadUnusedAssets 异常: " + e2.Message);
 }
 }
 catch (Exception e)
 {
 FixLog.Debug("auto-prime 异常: " + e.Message);
 }
 }
#else
 // 【LEAN】AutoPrimeMapLoad 整项删除：连协程本体一起不编译
#endif

 private IEnumerator StartupCleanupCo()
 {
 for (int i = 0; i < 30; i++) yield return null; // 等 VRGIN/游戏初始化完
 if (!IsVrUsable()) yield break;

 try
 {
 int n = GuiCanvasFix.UndoStrayLayerMove("启动清理");
 if (n > 0)
 FixLog.Key("启动清理：把 " + n + " 个被误搬进 UI 图层的世界物件节点还原回 Default。");
 else
 FixLog.Info("启动清理：未发现被误搬进 UI 图层的世界物件。");
 }
 catch (Exception e) { FixLog.Debug("启动清理异常: " + e.Message); }

 // Awake 时可能因程序集还没加载齐而找不到目标 → 这里重试一次。
 // 实机日志曾出现「未找到 ForceResetAsStandingMode」导致守卫静默失效、
 // 每次加载都真的重建 StandingMode（origin 归零 + 手柄消失）。
 try
 {
 SpuriousModeRebuildGuard.RetryInstall();
 FixLog.Debug("已重试多余重建守护安装。");
 }
 catch (Exception e) { FixLog.Debug("重试多余重建守护异常: " + e.Message); }

 // 同理重试 MoveToCurrent 钩子（覆盖 10 机位快捷键的俯仰补回）
 try { VRRef.RetryMoveToCurrentHook(); }
 catch (Exception e) { FixLog.Debug("RetryMoveToCurrentHook 异常: " + e.Message); }

 // 跳变诊断（Mode.OnDestroy 调用栈 + origin 记录仪）也在这里重试安装
 try { if (KKCharaStudioVRFixPlugin.DiagnosticsEnabled) JumpDiagnostics.RetryInstall(); }
 catch (Exception e) { FixLog.Debug("JumpDiagnostics.RetryInstall 异常: " + e.Message); }

 // 指针链路诊断延迟安装：GripMenuHandler 是 0.0.3 在 VR 模式起来后才创建的，
 // 插件 Awake 时还不存在 → 必须在这里（VR 就绪 + 工作室就绪之后）重试。
 try
 {
 if (KKCharaStudioVRFixPlugin.DiagnosticsEnabled)
 {
 #if !LEAN
 VRPointerDiagnostics.Install(new HarmonyLib.Harmony(KKCharaStudioVRFixPlugin.Guid + ".vrpointer.retry"));
 VRPointerDiagnostics.InstallInputSpy(new HarmonyLib.Harmony(KKCharaStudioVRFixPlugin.Guid + ".vrinputspy.retry"));
 #endif
 }
 }
 catch (Exception e) { FixLog.Debug("VRPointerDiagnostics.RetryInstall 异常: " + e.Message); }

 // 强制保住 VR rig 不被场景切换回收（rig 死=Mode/控制器/origin 全陪葬，
 // 是全部跳变的总根源 —— 日志铁证：rig 重建与 Mode 死亡成对出现）
 try
 {
 bool ok = VRRef.EnforceRigDDL();
 FixLog.Key("rig 保活校验：" + (ok ? "已验证位于 DontDestroyOnLoad 场景" : "未通过验证")
 + " | " + VRRef.GetRigSceneSnapshot());
 }
 catch (Exception e) { FixLog.Debug("EnforceRigDDL 启动异常: " + e.Message); }

 // 启动后立刻跑一次渲染队列提升（之后由 LateUpdate 每 5s 维护）
 try { VRRenderQueueFix.RunPeriodic("启动清理"); }
 catch (Exception e) { FixLog.Debug("VRRenderQueueFix 启动异常: " + e.Message); }
 }

 private void Update()
 {
 PerfProbe.Begin("Watchdog.Update");
 try { UpdateInner(); }
 catch (Exception e) { ReportTickError("Update", e); }
 finally { PerfProbe.End("Watchdog.Update"); }
 }

 /// <summary>原来的 Update 主体（为了整体计时而拆出）。</summary>
 private void UpdateInner()
 {
 HandleShortcuts();

 // 日志缓冲落盘（最多每秒一次）。放在 VR 就绪判断**之前**，
 // 这样启动期的日志也能及时写出来。
 try { FixLog.FlushTick(); } catch { }

 if (!IsVrUsable()) return;

#if !LEAN
 // UI 现场取证（DiagOn 时每 10 秒一次）
 try { VRRenderQueueFix.DumpUiFactsPeriodic(); }
 catch (Exception e) { FixLog.Debug("DumpUiFactsPeriodic 异常: " + e.Message); }
#endif

 // 窗口拖动修正的周期重试（第三方 DLL 可能后加载）
 try { MovableWindowFix.TryInstall(); }
 catch (Exception e) { FixLog.Debug("MovableWindowFix.TryInstall 异常: " + e.Message); }

 // 【2026-10-24】ME 悬浮说明窗口的定位接管（同样是第三方 DLL，可能后加载；
 // 内部 10 秒一次重试，装好后永久变成一次 bool 判断）
 try { MaterialEditorTooltipFix.TryInstall(); }
 catch (Exception e) { FixLog.Debug("MaterialEditorTooltipFix.TryInstall 异常: " + e.Message); }

#if NEWFIX
 // 【NEWFIX】模式销毁事件（挂不上就退回 5 秒轮询，见 UpdateInner）
 try { TryInstallModeDestroyHook(); }
 catch (Exception e) { FixLog.Debug("TryInstallModeDestroyHook 异常: " + e.Message); }

 // 【NEWFIX】拖动工具 Awake 事件（重建后立刻恢复解锁状态）
 try { DragStateFix.TryInstallAwakeHook(); }
 catch (Exception e) { FixLog.Debug("TryInstallAwakeHook 异常: " + e.Message); }

 // 【NEWFIX】新 rig 出生事件（跳变根因修复，必须常开）
 try { TryInstallRigAwakeHook(); }
 catch (Exception e) { FixLog.Debug("TryInstallRigAwakeHook 异常: " + e.Message); }
#endif

 // UI 图层保修（内部 0.5 秒节流、零分配复查）
 // 修「画面效果菜单看不见」：被 GUI 相机渲染的画布必须在 UI 层，被改回别的层就立刻修回来。
 try { GuiCanvasFix.LayerWatchTick(); }
 catch (Exception e) { FixLog.Debug("LayerWatchTick 异常: " + e.Message); }

 // rig DDL 保活巡检 —— 完整版里它每 2 秒兜一次底。
 // 【2026-10-22】精简版里 JumpDiagnostics 是**空壳存根**（Compile Remove），
 // 这个调用等于空转 → 用 #if 排除，避免"以为在跑、其实没跑"的误解。
 // 精简版的 rig 保活由核心路径负责：VRCamera.OnAwake + 四个场景切换 API 前缀。
#if !LEAN
 try { JumpDiagnostics.RigPatrol(); }
 catch (Exception e) { FixLog.Debug("RigPatrol 异常: " + e.Message); }
#endif

 // origin 行车记录仪：抓每一跳（非我方写入的）
 #if !LEAN
 try { JumpDiagnostics.MonitorTick(); }
 catch (Exception e) { FixLog.Debug("MonitorTick 异常: " + e.Message); }
 #endif

 if (!KKCharaStudioVRFixPlugin.On(KKCharaStudioVRFixPlugin.CfgWatchdogEnabled)) return;

 if (KKCharaStudioVRFixPlugin.CfgMinimalMode != null
 && KKCharaStudioVRFixPlugin.CfgMinimalMode.Value)
 return; // 最小模式：Update 侧的周期修复也全部跳过

 // 原来这里**每帧**调 SafeFixVrCameraMask()。
 // 它虽然已经用了缓存掩码，但仍要每帧取相机 + 读 workInfo.visibleAxis
 // + 读 optionSystem.selectedState + 比较，且 VR 里一帧 60~90 次。
 // 掩码只在"被VRGIN改坏"或"轴显隐变化"时才需要纠正，
 // 所以改成 0.5 秒一次的周期检查（对齐 UI 需求足够了）。
#if NEWFIX
 // 【NEWFIX】"轴显隐变化"这件事已经挂到游戏的 Studio.CameraControl.ReflectOption() 上了，
 // 而"被 VRGIN 改坏"由 VRCamera.Copy 钩子覆盖 → 5 秒轮询只在钩子没装上时才保留。
 if (!CameraEventFix.ReflectOptionHooked
 && Time.realtimeSinceStartup - _lastMaskFixTime >= MaskFixInterval)
#else
 if (Time.realtimeSinceStartup - _lastMaskFixTime >= MaskFixInterval)
#endif
 {
 _lastMaskFixTime = Time.realtimeSinceStartup;
 try { PerfProbe.Begin("SafeFixVrCameraMask"); SafeFixVrCameraMask(); }
 finally { PerfProbe.End("SafeFixVrCameraMask"); }
 }

#if NEWFIX
 // 【NEWFIX】模式死亡检查：**事件驱动**（OnDestroy + OnDisable 两个钩子），
 // 只有在钩子都装不上时才保留 5 秒轮询。
 // ★ 注意：这里**不要**改成常开轮询 —— 用户明确要求"用事件而不是打补丁"，
 // 跳变的根因早在 就用「rig 根对象 DontDestroyOnLoad」修掉了，
 // 轮询只是在掩盖"某个销毁路径没触发事件"这个真问题。
 if (!_modeDestroyHooked
 && Time.realtimeSinceStartup - _lastModeCheck >= ModeCheckInterval)
#else
 if (Time.realtimeSinceStartup - _lastModeCheck >= ModeCheckInterval)
#endif
 {
 _lastModeCheck = Time.realtimeSinceStartup;
 try { PerfProbe.Begin("SafeCheckMissingMode"); SafeCheckMissingMode(); }
 finally { PerfProbe.End("SafeCheckMissingMode"); }
 }

#if NEWFIX
 // 【NEWFIX】窗口位置：只在"还没确认正确"时重试，确认后永久不再查（替代原来的 10 秒轮询）。
 try { VRInputFix.VerifyHandleOnce(); } catch { }

 // 【NEWFIX】模式刚被销毁 → 立刻抢救（不等下一次轮询）。
 if (_modeDestroyPending)
 {
 _modeDestroyPending = false;
 try { SafeCheckMissingMode(); }
 catch (Exception e) { FixLog.Debug("销毁后立刻抢救异常: " + e.Message); }
 }
#endif

#if !LEAN
 if (KKCharaStudioVRFixPlugin.CfgRepairControllers != null
 && KKCharaStudioVRFixPlugin.CfgRepairControllers.Value
 && Time.realtimeSinceStartup - _lastHealthCheck >= HealthCheckInterval)
 {
 _lastHealthCheck = Time.realtimeSinceStartup;
 SafeCheckControllers();
 }
#else
 // 【LEAN·第三批删除】RepairControllers 整项删除（它会把手柄误判成故障）
#endif

 if (_pendingRecovery && !_recovering)
 {
 _pendingRecovery = false;
 StartCoroutine(RecoverAfterLoadCo());
 }
 }

 private static bool _minimalModeLogged;

 /// <summary>相机渲染状态的同步放在 LateUpdate，尽量避开 VRGIN / SteamVR 在 Update 里的接管。</summary>
 private void LateUpdate()
 {
 PerfProbe.Begin("Watchdog.LateUpdate");
 try { LateUpdateInner(); }
 catch (Exception e) { ReportTickError("LateUpdate", e); }
 finally { PerfProbe.End("Watchdog.LateUpdate"); }
 }

 /// <summary>
 /// 【2026-10-22 新增】兜住 Update/LateUpdate 里的异常。
 ///
 /// 为什么必须加：原来这两处只有 `try/finally`，没有 `catch`。
 /// 一旦某一行抛异常（2026-10-22 就是精简版里对已置 null 的配置项取 `.Value`），
 /// **后面所有修复都会被静默跳过**，而且 Unity 每帧往 LogOutput.log 刷一条异常 ——
 /// 用户只看得到"某些功能不生效"，完全查不到原因。
 /// 现在：异常被接住并上报，其余修复继续跑。
 ///
 /// ★ 上报**没有任何计时器**：只在"异常内容发生变化"时再写一条
 /// （同一个异常连发只报第一次；换位置/换原因立刻报）。
 /// 稳态（不抛异常）时这里一次都不会进来，零开销。
 /// </summary>
 private static int _tickErrCount;
 private static Type _lastErrType;
 private static string _lastErrMsg;

 private static void ReportTickError(string where, Exception e)
 {
 try
 {
 _tickErrCount++;
 Type t = e.GetType();
 string msg = e.Message;
 // 同一个异常重复发生 → 直接返回（不写盘、不分配新字符串）
 if (ReferenceEquals(t, _lastErrType)
 && string.Equals(msg, _lastErrMsg, StringComparison.Ordinal)) return;
 _lastErrType = t;
 _lastErrMsg = msg;
 FixLog.Warn("【看门狗异常】" + where + "（累计 " + _tickErrCount + " 次）: "
 + t.Name + ": " + msg);
 }
 catch { }
 }

 /// <summary>原来的 LateUpdate 主体（为了整体计时而拆出）。</summary>
 private void LateUpdateInner()
 {
#if JUMPWATCH
 // ===== [REMOVABLE:JUMPWATCH-BEGIN] 跳变检测（已默认关闭，需要时加 define JUMPWATCH）=====
 try { LeanJumpWatch.Tick(); } catch { }
 // ===== [REMOVABLE:JUMPWATCH-END] =====
#endif
 if (!IsVrUsable()) return;

 // 【2026-10-22 ★★★ 事故】这里原来写的是 `CfgWatchdogEnabled.Value`。
 // 精简版为了"F1 只留渲染队列"已经把这个配置项置 null → **每帧 NullReferenceException**，
 // 而 LateUpdate 当时只有 try/finally 没有 catch → 异常直接冲出方法，
 // **导致它后面的一整段修复全部不执行**：
 // · 画面效果每帧同步 / 画布周期接管 / VRInputFix.Tick（光标与窗口句柄）
 // · **VRControllerModelFix.Tick（手柄渲染模型救援 → "手柄只剩圆盘"）**
 // · **DragStateFix.ConsumePendingRestore（默认解锁拖动世界）**
 // · OriginGuardFix.Tick
 // 这正是用户报的两个 BUG（手柄变圆盘+指针失效、进游戏没默认解锁）的同一个根因。
 // 一律用 `On(...)`：精简版恒 true，完整版读真实配置。
 if (!KKCharaStudioVRFixPlugin.On(KKCharaStudioVRFixPlugin.CfgWatchdogEnabled)) return;

 // 最小模式（对照用）：跳过本帧所有可选修复，只保留跳变修复相关的观测。
 // 目的是让用户能用**一次重启**验证"掉帧是不是插件造成的"。
 if (KKCharaStudioVRFixPlugin.CfgMinimalMode != null
 && KKCharaStudioVRFixPlugin.CfgMinimalMode.Value)
 {
 if (!_minimalModeLogged)
 {
 _minimalModeLogged = true;
 FixLog.Key("【最小模式】已开启：本插件所有可选的运行时改动都已停用"
 + "（相机遮罩/色彩分级/UI队列/Canvas接管/画面效果同步/指针诊断），"
 + "只保留跳变修复。此时的表现应接近原版 0.0.3 —— 用来对照帧率。");
 }
 try { PerfProbe.FrameTick(); } catch { }
 return;
 }

 // 色彩分级（AmplifyColorEffect）周期兜底检查 —— 修「画面变灰」
#if NEWFIX
 // 【NEWFIX】删掉 30 秒兜底：`SyncEffectsFromSource` 每帧已经在按用户意图纠正
 // 每个效果的 enabled，并且（NEWFIX 起）每帧顺带补建"被销毁"的组件。
 // 所以这个 30 秒任务纯属重复劳动 —— 而且它本身就慢到"变灰要等 30 秒"。
#else
 if (KKCharaStudioVRFixPlugin.On(KKCharaStudioVRFixPlugin.CfgReapplyColorGrading)
 && Time.realtimeSinceStartup - _lastColorCheck >= ColorCheckInterval)
 {
 _lastColorCheck = Time.realtimeSinceStartup;
 try { PerfProbe.Begin("ColorGradingFix.ForceEnable"); ColorGradingFix.ForceEnable("周期检查"); }
 catch (Exception e) { FixLog.Debug("色彩分级周期检查异常: " + e.Message); }
 finally { PerfProbe.End("ColorGradingFix.ForceEnable"); }
 }
#endif

 // 画面效果同步**必须每帧**，之前被错放进上面那个 30 秒兜底门里！
 //
 // 事故复盘（2026-10-16 日志铁证）：
 // 起 SyncFromSource 的调用点被写在了 `_lastColorCheck >= ColorCheckInterval`
 // 这个 30 秒门**内部**，而 reason 还写着"每帧" —— 于是它实际每 30 秒才跑一次。
 // 日志里两次同步的时间戳是 21:32:16 / 21:33:16 / 21:33:46 / 21:34:16 / 21:34:46，
 // 间隔 30~60 秒；用户"改滤镜 → 等 20~25 秒才看到变化"正好等于这个门的剩余时间。
 // （用户实测原话："改了滤镜以后 是等了一段时间看到效果变了 才继续改下一次"。）
 //
 // 现在真正每帧跑：指纹只在 Texture/Material 变化时才销毁重建，标量变化直接 SetValue，
 // 所以"换滤镜"和"拖滤镜比例"都在 1 帧内生效（与平面模式一致）。
 // 关闭方式：配置项 SyncEffectsFromSource（默认开）。
 if (KKCharaStudioVRFixPlugin.CfgSyncEffectsFromSource == null
 || KKCharaStudioVRFixPlugin.On(KKCharaStudioVRFixPlugin.CfgSyncEffectsFromSource))
 {
#if NEWFIX
 // 【NEWFIX ★ 已回退】原本想用 `SunLightInfo.Set` 事件替掉每帧比对，实测失败：
 // 那个入口是**「切换时间段（白天/黄昏/夜晚）」**，不是「画面效果面板里换滤镜」。
 // 日志铁证：钩子触发时变化的字段是 `Lut_TimeSunset1 → Lut_Amount`（日落 LUT → 默认 LUT），
 // 而且只在你加载场景卡时触发；你在面板里换滤镜时**一次都没触发**。
 // 反编译确认：整个 Assembly-CSharp 里只有 `SunLightInfo.SetACE` 会写 `LutTexture`/`BlendAmount`，
 // 而 `SetACE` 只被 `Set()` 调用 —— 说明面板那条路是走反射/绑定直接改 `Info` 字段的，
 // 没有一个可挂的方法入口。
 // 结论：这一项**没有事件可挂**，必须保留每帧比对（成本 0.07ms/帧 ≈ 帧预算 0.6%）。
 // 事件钩子仍然保留 —— 它在加载场景卡那种长帧里能立刻同步，不冲突（第二次调用检测不到变化，只会做一次无害的字段拷贝）。
#endif
 {
 try
 {
 PerfProbe.Begin("ColorGradingFix.SyncFromSource");
 ColorGradingFix.SyncFromSource("每帧", true, true);
 }
 catch (Exception e) { FixLog.Debug("SyncFromSource 异常: " + e.Message); }
 finally { PerfProbe.End("ColorGradingFix.SyncFromSource"); }
 }
 }

 // 接管 VRGIN 漏掉的 UI Canvas —— 修「面板看不见但能点到」
 // 改为 RunPeriodic()：内部自带 3s 节流 + 连续空扫后休眠到 10s
 // （原来每 1 秒无条件做一次 Resources.FindObjectsOfTypeAll<Canvas>() 全表扫描）。
 // 所以这里**每帧调也无所谓**，节流在 GuiCanvasFix 内部。
 if (KKCharaStudioVRFixPlugin.On(KKCharaStudioVRFixPlugin.CfgFixHiddenGuiCanvases))
 {
 try
 {
 PerfProbe.Begin("GuiCanvasFix.RunPeriodic");
 int n = GuiCanvasFix.RunPeriodic("周期检查");
 if (n > 0) FixLog.Info("本次接管了 " + n + " 个漏掉的 UI Canvas。");
 }
 catch (Exception e) { FixLog.Debug("GuiCanvasFix 周期检查异常: " + e.Message); }
 finally { PerfProbe.End("GuiCanvasFix.RunPeriodic"); }
 }

 // VR UI 面板 / 手柄材质渲染队列提升（5s 节流，自带开关）
#if NEWFIX
 // 【NEWFIX】删掉 60 秒周期：改的共享材质队列**设一次永久生效**，不会自己变回 4000。
 // 唯一会变的情况是"加载后新建了面板/材质" → 所以改在「加载完成后」跑一次
 //（见 RecoverAfterLoadCo 里的 VRRenderQueueFix.RunPeriodic("加载完成后")）。
#else
 try { PerfProbe.Begin("VRRenderQueueFix.RunPeriodic"); VRRenderQueueFix.RunPeriodic("周期检查"); }
 catch (Exception e) { FixLog.Debug("VRRenderQueueFix.RunPeriodic 异常: " + e.Message); }
 finally { PerfProbe.End("VRRenderQueueFix.RunPeriodic"); }
#endif

 // VR 输入/指针维护：兜光标可见性、停 ConfineMouse 每帧锁光标、重解析窗口句柄
 try { PerfProbe.Begin("VRInputFix.Tick"); VRInputFix.Tick(); }
 catch (Exception e) { FixLog.Debug("VRInputFix.Tick 异常: " + e.Message); }
 finally { PerfProbe.End("VRInputFix.Tick"); }

 // 手柄渲染体清单的周期转储**已取消**。
 // 它是为了查「手柄只剩圆盘」加的取证，那个 BUG 已修好并验证（0.5 秒内自愈）。
 // 代价却很大：实测单次 27~51ms（要遍历 17 个 Renderer + 反射读 RenderModel），
 // 每 30 秒来一次 —— 在诊断版里就是肉眼可见的"抽一下"。函数保留，需要时手动调。

 // 指针诊断的周期重试：GripMenuHandler 可能在工作室就绪后才出现（内部 5 秒节流）
 #if !LEAN
 try { VRPointerDiagnostics.TryInstallPeriodic(); }
 catch (Exception e) { FixLog.Debug("TryInstallPeriodic 异常: " + e.Message); }
 #endif

 // 手柄渲染模型救援（修「只剩圆盘」；内部 3 秒节流，最多 20 次）
 try { PerfProbe.Begin("VRControllerModelFix.Tick"); VRControllerModelFix.Tick(); }
 catch (Exception e) { FixLog.Debug("VRControllerModelFix.Tick 异常: " + e.Message); }
 finally { PerfProbe.End("VRControllerModelFix.Tick"); }

 // 防止 UI 图层被污染：VRGIN 的 _VRGUICamera 只渲染 UI 图层，
 // 一旦世界物件（手柄模型/图标、Studio 的 3D 预览等）被搬进 UI 图层，
 // 它就会被画进 uGuiTexture，而 uGuiTexture 又贴在**显示在眼前的 GUIQuad** 上
 // → 表现为「手柄的图标被永久固定在了 UI 上」。
 // v1.7.0 的 GuiCanvasFix 曾经把 WorldSpace 画布的整棵子树改成 UI 图层且永不还原，
 // 这里周期性地把它回滚掉。
 // 【NEWFIX ★】原来是每 30 秒扫一次。这其实是"清理 v1.7.0 历史遗留"的一次性动作，
 // 场景里的画布在加载完成后就固定了 → 改成**只在加载完成后跑一次**（见 RecoverAfterLoadCo）。
 // 30 秒轮询已删除。

 // 本帧插件周期任务总耗时（超阈值才落盘）
 try { PerfProbe.FrameTick(); }
 catch { }

 // 原点姿态记忆 —— 只记不写。
 // 真正的"写回"发生在 OriginGuardFix.StandingModeOnStartPostfix()
 // （Harmony 挂在 VRGIN.Modes.StandingMode.OnStart 上，一次压过它的归零）。
 // 那种"连续 120 帧抢写"已被证明会误伤玩家手动摆正的姿态，已废弃。
 //
 // 从"每帧"改成 0.05 秒一次（详见 OriginTickInterval 注释）。
 if (Time.realtimeSinceStartup - _lastOriginTick >= OriginTickInterval)
 {
 _lastOriginTick = Time.realtimeSinceStartup;
 try { PerfProbe.Begin("OriginGuardFix.Tick"); OriginGuardFix.Tick(); }
 catch (Exception e) { FixLog.Debug("OriginGuardFix 异常: " + e.Message); }
 finally { PerfProbe.End("OriginGuardFix.Tick"); }
 }

 // 记住并自动恢复「解锁拖动世界」的状态 —— 修「每加载一张场景卡都要重新解锁」+「图标变回齿轮」
 // （0.0.3 的 LoadFixHook 每次加载场景卡都会重建 StandingMode，
 // 拖动工具被 AddComponent 成全新实例，lockRotXZ 回到默认的 true=锁定）
 //
 // 【NEWFIX ★】原来每 1 秒（后来放慢到 5 秒）跑一次 DragStateFix.Tick 兜底。
 // 现在改成**纯事件驱动**：只有 `GripMoveKKCharaStudioTool.Awake` 真的触发过，
 // 才在**下一帧**跑一次恢复；平时这里连时间比较都不做。
 // 如果实测发现解锁状态会丢，就说明还有不走 Awake 的重建路径 —— 那时再把周期兜底加回来。
 try
 {
 PerfProbe.Begin("DragStateFix.Tick");
 DragStateFix.ConsumePendingRestore();
 }
 catch (Exception e) { FixLog.Debug("DragStateFix 异常: " + e.Message); }
 finally { PerfProbe.End("DragStateFix.Tick"); }

 // 相机背景同步以前是**每帧**（只要配置开着）。
 // 背景色同样只在加载时变化，改成 1 秒一次兜底就够了。
 // 删除这里的周期调用：`FixCameraBackground` 与上面的
 // `SafeFixVrCameraMask`（掩码修复）职责重叠，且它自己有 needApply 短路、
 // 还有 1 秒的 CfgCameraSyncIntervalSeconds 节流 —— 属于重复劳动。
 // 背景问题由掩码修复 + 加载后一次性处理覆盖。
 }

 /// <summary>
 /// VR.Mode == null 是明确无歧义的损坏信号（控制器被整个销毁 → 只剩头显能动、
 /// 看不到手柄和 UI）。这里重建它，并保留原点位置。
 /// 与"手柄健康检查"不同，这里不存在误判风险。
 /// </summary>
 private void SafeCheckMissingMode()
 {
 try
 {
 if (!KKCharaStudioVRFixPlugin.On(KKCharaStudioVRFixPlugin.CfgFixMissingMode)) return;
 if (TransitionActive) return;
 if (VRRef.IsGameLoading()) return;

 VRManager mgr;
 try { mgr = VR.Manager; }
 catch (InvalidOperationException) { return; }

 if (mgr == null) return;
 if (mgr.Mode != null) return;

 if (Time.realtimeSinceStartup - _lastModeRebuild < ModeRebuildCooldown) return;
 _lastModeRebuild = Time.realtimeSinceStartup;

 FixLog.Warn("检测到 VR.Manager.Mode == null（控制器已被销毁），重建 StandingMode（保留原点位置）。");
 JumpDiagnostics.Mark("Mode==null 检出（准备重建）");
 JumpDiagnostics.AutopsyModeLoss();
 RebuildModePreservingOrigin();
 }
 catch (Exception e)
 {
 FixLog.Debug("CheckMissingMode 异常: " + e.Message);
 }
 }

 // ---------------- 基础 ----------------

 private static bool IsVrUsable()
 {
 try { return VR.Active; }
 catch { return false; }
 }

 private static Camera GetVrCamera()
 {
 try
 {
 // ★★ 2026-10-22 安全阀：平面模式不许碰 `VR.Camera`（惰性 getter 会造一套假 rig）。
 // 本方法会被 `FixVrCameraMaskNow()` 调用，而它的调用者里有**平面模式照样触发**的钩子
 // （`Studio.CameraControl.ReflectOption`、`SunLightInfo.Set`）。
 if (!VRRef.VrRunning) return null;
 if (VR.Camera == null || VR.Camera.SteamCam == null) return null;
 Camera c = VR.Camera.SteamCam.camera;
 if (c != null) return c;
 return VR.Camera.SteamCam.GetComponent<Camera>();
 }
 catch { return null; }
 }

 private void HandleShortcuts()
 {
#if !NEWFIX
 try
 {
 if (KKCharaStudioVRFixPlugin.CfgDiagnosticKey != null
 && KKCharaStudioVRFixPlugin.CfgDiagnosticKey.Value.IsDown())
 Diagnostics.DumpAll("手动按键");
 }
 catch (Exception e) { FixLog.Debug("诊断导出失败: " + e.Message); }
#else
 // 【NEWFIX】F7 已删除（平面模式会造出假 VR rig，见 Diagnostics.DumpAll 里的安全阀注释）。
#endif

#if !NEWFIX
 try
 {
 if (KKCharaStudioVRFixPlugin.CfgForceHideAxisKey.Value.IsDown())
 {
 VRRef.SetVisibleAxis(false);
 FixLog.Key("已强制关闭 FK/IK 轴显示 (visibleAxis = false)。");
 Diagnostics.DumpAll("强制关闭轴之后");
 }
 }
 catch (Exception e) { FixLog.Debug("强制关闭轴失败: " + e.Message); }
#else
 // 【NEWFIX】F6 已删除：切轴显示用游戏自带的快捷键即可。
#endif

#if !NEWFIX
 // F8：把主 UI 拉回眼前（UI 被物品挡住时的正解）
 try
 {
 if (KKCharaStudioVRFixPlugin.CfgPullUIToFaceKey != null
 && KKCharaStudioVRFixPlugin.CfgPullUIToFaceKey.Value.IsDown())
 {
 VRRenderQueueFix.PullUIToFace();
 }
 }
 catch (Exception e) { FixLog.Debug("拉回 UI 失败: " + e.Message); }
#else
 // 【NEWFIX】F8 已删除（用户实测按了从来没反应）。
#endif
 }

 // ---------------- 掩码守卫（核心）----------------

#if NEWFIX
#if !LEAN
 // 【精简版瘦身】图层差集取证：纯诊断。
 /// <summary>
 /// 【NEWFIX ★】「VR 相机看得见、但 Studio 主相机看不见」的层 —— 只打层名，不做任何写操作。
 ///
 /// 为什么需要它：某些地图（例如女厕所）里有**可点击的互动图标（心形）**，平面模式下看不见，
 /// 但在 VR 里会露出来。如果这些图标所在的那一层出现在下面的「多看的层」列表里，
 /// 就说明是我们的掩码把它放进了可见清单 —— 凶手当场点名，不用猜。
 ///
 /// 同时打印 `_goodMask`（我们学到的「正常掩码」）：
 /// 它目前是**只增不减**的（`_goodMask |= cur`），所以任何一次掩码被污染时看到的层
 /// 都会被**永久**加进白名单 → 这正好能解释「触发灰屏后心形图标就跟着出现」。
 /// </summary>
 private static void DumpLayerDiff(string reason)
 {
 if (!KKCharaStudioVRFixPlugin.DiagOn) return;
 try
 {
 Camera vcam = GetVrCamera();
 Camera scam = VRRef.GetStudioMainCamera();
 if (vcam == null || scam == null) return;

 int onlyVr = vcam.cullingMask & ~scam.cullingMask;
 System.Text.StringBuilder sb = new System.Text.StringBuilder();
 for (int i = 0; i < 32; i++)
 {
 if ((onlyVr & (1 << i)) == 0) continue;
 sb.Append(" [").Append(i).Append("]").Append(LayerMask.LayerToName(i));
 }
 FixLog.Key("【图层差集】" + reason + "：VR 相机比 Studio 主相机**多看的层** = "
 + (sb.Length == 0 ? "无" : sb.ToString())
 + "｜我们学到的参考掩码 = " + DescribeMaskBits(_goodMask));

 // 反差：主相机看得见、VR 相机看不见的层（会表现为"东西不见了"）
 int onlyStudio = scam.cullingMask & ~vcam.cullingMask;
 System.Text.StringBuilder sb2 = new System.Text.StringBuilder();
 for (int i = 0; i < 32; i++)
 {
 if ((onlyStudio & (1 << i)) == 0) continue;
 sb2.Append(" [").Append(i).Append("]").Append(LayerMask.LayerToName(i));
 }
 if (sb2.Length > 0)
 FixLog.Key("【图层差集】" + reason + "：Studio 主相机看得见、VR 相机**看不见**的层 = "
 + sb2.ToString());
 }
 catch { }
 }
#else
 // 【LEAN】诊断导出整段删除（只给完整版）
#endif

#if !LEAN
 // 【精简版瘦身】诊断辅助。
 private static string DescribeMaskBits(int mask)
 {
 System.Text.StringBuilder sb = new System.Text.StringBuilder();
 for (int i = 0; i < 32; i++)
 {
 if ((mask & (1 << i)) == 0) continue;
 if (sb.Length > 0) sb.Append(",");
 sb.Append(LayerMask.LayerToName(i));
 }
 return sb.Length == 0 ? "空" : sb.ToString();
 }
#else
 // 【LEAN】整段删除（诊断 / 只被已删除的控制器健康检查使用）
#endif

 /// <summary>
 /// 【NEWFIX ★】`Action/*` 这一组层：主游戏「可点击互动点」用的层。
 ///
 /// 实锤证据（2026-10-20 日志）：
 /// 【图层对象】层[14]Action/Gate 有 5 个渲染物件，例如：Icon / Icon / Icon / Icon / Icon
 /// 那 5 个 "Icon" 就是某些地图（女厕所等）里能点的**心形互动图标**。
 /// Studio 主相机不画这一层 → 平面模式看不见；我们的 VR 相机掩码里有它 → VR 里露出来。
 ///
 /// 为什么只处理 `Action/*`、不动 `HScene/*`：
 /// 同一次取证显示 `HScene/MetaballB` 里放的是**地图地板部件**（`mo_koi_map14_yuka00_t0*_m`），
 /// 屏蔽掉可能导致地板缺块 —— 所以不碰。
 /// </summary>
 private static int _actionLayerMask = -1;
 private static int CachedActionLayerMask()
 {
 if (_actionLayerMask >= 0) return _actionLayerMask;
 int m = 0;
 for (int i = 0; i < 32; i++)
 {
 string n = LayerMask.LayerToName(i);
 if (!string.IsNullOrEmpty(n) && n.StartsWith("Action/", StringComparison.Ordinal))
 m |= 1 << i;
 }
 _actionLayerMask = m;
 return m;
 }

#if !LEAN
 // 【精简版瘦身】诊断输出 + 只服务于已删除功能的工具方法。
 /// <summary>
 /// 【NEWFIX】对象级取证（当前**没有调用**，留着备用）：
 /// 把「VR 相机多看、Studio 主相机看不见」的每一层，最多列 8 个渲染物件名。
 /// 当初就是靠它一锤定音的：
 /// 【图层对象】层[14]Action/Gate 有 5 个渲染物件，例如：Icon / Icon / Icon / Icon / Icon
 /// 代价：`Resources.FindObjectsOfTypeAll<Renderer>()` 每次约 0.5~1 秒 → 平时别开。
 /// </summary>
 private static void DumpExtraLayerObjects(string reason)
 {
 if (!KKCharaStudioVRFixPlugin.DiagOn) return;
 try
 {
 Camera vcam = GetVrCamera();
 Camera scam = VRRef.GetStudioMainCamera();
 if (vcam == null || scam == null) return;

 int extra = vcam.cullingMask & ~scam.cullingMask;
 if (extra == 0) { FixLog.Key("【图层对象】" + reason + "：没有「多看的层」，跳过。"); return; }

 Renderer[] all = Resources.FindObjectsOfTypeAll<Renderer>();
 if (all == null) return;

 for (int i = 0; i < 32; i++)
 {
 int bit = 1 << i;
 if ((extra & bit) == 0) continue;

 int total = 0;
 System.Text.StringBuilder names = new System.Text.StringBuilder();
 for (int k = 0; k < all.Length; k++)
 {
 Renderer rd = all[k];
 if (rd == null) continue;
 GameObject go = rd.gameObject;
 if (go == null || !go.scene.IsValid()) continue; // 跳过 prefab 资产
 if (go.layer != i) continue;
 total++;
 if (names.Length < 260)
 {
 if (names.Length > 0) names.Append(" / ");
 names.Append(go.name);
 }
 }
 if (total == 0) continue;

 FixLog.Key("【图层对象】" + reason + "：层[" + i + "]" + LayerMask.LayerToName(i)
 + " 有 " + total + " 个渲染物件，例如：" + names);
 }
 }
 catch { }
 }
#else
 // 【LEAN】整段删除（诊断输出 / 只被已删除的控制器健康检查使用）
#endif
#endif

 private static void SafeFixVrCameraMask()
 {
 try { FixVrCameraMask(); }
 catch (Exception e) { FixLog.Debug("FixVrCameraMask 异常: " + e.Message); }
 }

 /// <summary>
 /// 供 SlaveDiagnostics 在 VRGIN 每次重算掩码之后**立刻**调用。
 /// 因为 VRGIN 的 UpdateCameraConfig() 是在渲染前（CameraKiller.OnPreCull）
 /// 触发的，晚于 Update —— 只在 Update 里修会被它覆盖掉。
 /// </summary>
 internal static void FixVrCameraMaskNow()
 {
 try { FixVrCameraMask(); }
 catch { }
 }

 /// <summary>缓存的层掩码（层名→掩码的字符串查找只在首次做一次）。</summary>
 private static int _colMaskCache = -1;
 private static int _selectMaskCache = -1;
 private static int _mapMaskCache = -1;
 private static int _overlayCache = -1;

 /// <summary>缓存的"禁止层"。仅在配置字符串变化时重算。</summary>
 private static int _forbiddenCache = -2;
 private static string _forbiddenCacheSrc;

 /// <summary>掩码没被改动、也没有新相机登记时的"跳过"计数（省日志与重复计算）。</summary>
 private static int _maskNoopFrames;
#if NEWFIX
 private static float _lastActionStripLog = -999f;
#endif

 private static int CachedColMask
 {
 get
 {
 if (_colMaskCache < 0) _colMaskCache = LayerMask.GetMask("Studio/Col");
 return _colMaskCache;
 }
 }

 private static int CachedSelectMask
 {
 get
 {
 if (_selectMaskCache < 0) _selectMaskCache = LayerMask.GetMask("Studio/Select");
 return _selectMaskCache;
 }
 }

 private static int CachedMapMask
 {
 get
 {
 if (_mapMaskCache < 0) _mapMaskCache = LayerMask.GetMask("Map");
 return _mapMaskCache;
 }
 }

 private static int CachedOverlay
 {
 get
 {
 if (_overlayCache < 0) _overlayCache = CachedColMask | CachedSelectMask;
 return _overlayCache;
 }
 }

 /// <summary>
 /// 带缓存的 ParseLayers。
 /// 原来每次调用都要 Split 字符串 + 对每个层名做 NameToLayer（字符串查表）——
 /// 而这个函数**每帧**都会通过 FixVrCameraMask 被调到。现在只在源串变化时重算。
 /// </summary>
 private static int CachedForbidden()
 {
 string src = KKCharaStudioVRFixPlugin.ForbiddenLayersValue();
 if (_forbiddenCache != -2 && src == _forbiddenCacheSrc) return _forbiddenCache;

 string old = _forbiddenCacheSrc;
 _forbiddenCacheSrc = src;
 try { _forbiddenCache = ParseLayers(src); }
 catch { _forbiddenCache = 0; }

 if (old != src)
 FixLog.Info("禁止图层配置已更新: [" + src + "] → 掩码 0x"
 + _forbiddenCache.ToString("X8"));
 return _forbiddenCache;
 }

 private static void FixVrCameraMask()
 {
 Camera cam = GetVrCamera();
 if (cam == null) return;

 int cur = cam.cullingMask;

 int colMask = CachedColMask;
 int selectMask = CachedSelectMask;
 int overlay = CachedOverlay;

 int forbidden = CachedForbidden();
 // Map 层永远保留，避免误伤地图
 int alwaysKeep = CachedMapMask;

 bool hasOverlay = (cur & overlay) != 0;
 bool hasForbidden = (cur & forbidden) != 0;

 // ---- 学习"正常掩码"：只在既没有禁止图层、也没有轴图层时累积 ----
 if (!hasForbidden && !hasOverlay)
 {
#if NEWFIX
 // 【NEWFIX】学的时候就把 `Action/*` 剔掉，别让主游戏的互动层进白名单。
 int learned = _goodMask | (cur & ~CachedActionLayerMask());
#else
 int learned = _goodMask | cur;
#endif
 if (learned != _goodMask)
 {
 _goodMask = learned;
 FixLog.Info("记录参考掩码（" + SlaveDiagnostics.CountBits(_goodMask) + " 层）: "
 + SlaveDiagnostics.DescribeMask(_goodMask));
 }
 }

 int want = cur;

 // ---- 掩码被污染 → 恢复成参考掩码 ----
 if (hasForbidden
 && KKCharaStudioVRFixPlugin.On(KKCharaStudioVRFixPlugin.CfgStrictMaskRestore)
 && _goodMask != 0)
 {
 int keep = _goodMask | overlay | alwaysKeep;
 int stripped = want & ~keep;
 if (stripped != 0)
 {
 FixLog.Warn("掩码被污染（当前 " + SlaveDiagnostics.CountBits(cur)
 + " 层），恢复为参考掩码，剔除: " + SlaveDiagnostics.DescribeMask(stripped));
 }
 want = want & keep;
 }

 // ---- 禁止图层永远不允许出现 ----
 want &= ~forbidden;

 // ---- 保证"参考掩码"里的图层一定在 ----
 // 这一步专门对付 VRGIN 的反复重算：它每注销一个从相机就会把掩码算小，
 // 小到只剩 1 层(Default) 时地图/角色都会消失。这里把参考图层补回去。
 if (KKCharaStudioVRFixPlugin.On(KKCharaStudioVRFixPlugin.CfgEnsureReferenceLayers) && _goodMask != 0)
 want |= (_goodMask & ~forbidden);

#if NEWFIX
 // ---- 【NEWFIX ★】主游戏的「可点击互动点」层（Action/*）永远不进 VR 相机 ----
 // 实锤：某些地图（女厕所等）把可点击的**心形图标**放在层 14 `Action/Gate` 上，
 // 物件名就叫 `Icon`（日志【图层对象】已证明）。Studio 主相机不画这一层 → 平面看不见；
 // 我们的掩码里有它 → VR 里全露出来。
 // 这里放在"补参考层"之后统一剔除，所以就算 _goodMask 里已经混进了 Action/* 也会被清掉。
 int actionMask = CachedActionLayerMask();
 if (actionMask != 0 && (want & actionMask) != 0)
 {
 want &= ~actionMask;
 if (KKCharaStudioVRFixPlugin.DiagOn)
 {
 if (Time.realtimeSinceStartup - _lastActionStripLog > 10f)
 {
 _lastActionStripLog = Time.realtimeSinceStartup;
 FixLog.Key("【图层屏蔽】已把主游戏互动层（Action/*）从 VR 相机掩码里剔除 —— "
 + "那些层里放的是可点击的心形图标（层14 Action/Gate 的 5 个 Icon）。");
 }
 }
 }
#endif

 // ---- 按原生 ReflectOption() 的规则决定轴/引导球图层 ----
 want &= ~overlay;

 bool? visibleAxis = VRRef.GetVisibleAxis();
 if (visibleAxis == true)
 {
 if (GetSelectedState() == 0) want |= colMask;
 want |= selectMask;
 }

 if (want == cur) return;

 cam.cullingMask = want;

 if (KKCharaStudioVRFixPlugin.CfgVerboseCullingLog != null
 && KKCharaStudioVRFixPlugin.CfgVerboseCullingLog.Value
 && Time.realtimeSinceStartup - _lastMaskLogTime > 1f)
 {
 _lastMaskLogTime = Time.realtimeSinceStartup;
 FixLog.Info("VR 相机掩码: " + SlaveDiagnostics.CountBits(cur) + " 层 -> "
 + SlaveDiagnostics.CountBits(want) + " 层"
 + " [现在: " + SlaveDiagnostics.DescribeMask(want) + "]");
 }
 }

 // ---------------- VR 原点保护----------------
 //
 // 【为什么重写】
 // v1.7.0 的 LevelVROrigin() 会在每次加载完成后**无条件写 origin.rotation**。
 // 反编译 0.0.3 之后真相是这样的：
 //
 // GripMoveKKCharaStudioTool.OnUpdate() 里有个 `target` 字段：
 // target = VR.Camera.SteamCam.origin.gameObject; // （每帧在拖动分支里赋值）
 // 而它的 ResetRotation() 是：
 // e = target.transform.rotation.eulerAngles; e.x = 0; e.z = 0;
 // target.transform.rotation = Quaternion.Euler(e);
 // 你按「菜单键 + 扳机 + 侧键」触发的那次「解锁 / 重置」用的就是这段，
 // 它改的**就是 VR 原点**。`lockRotXZ` 是工具里的私有 bool，按一次翻转一次。
 //
 // 于是 v1.7.0 的 LevelVROrigin() 等于「插件偷偷替你按了一次三键」——
 // · 它把 `origin.rotation` 换成了一个新值，而 GGripMove 的拖动世界、
 // GripMenuHandler.ResizeHandler 都建立在 **origin.rotation** 之上；
 // · VR 里所有挂在 origin 上的东西（含显示在眼前的 GUIQuad —— 那块
 // 「最大那个 UI」）都会跟着这次旋转一起被甩到别的位置；
 // · 你手动解锁出来的状态被这次写入覆盖 → 每加载完一张场景卡就要重新解锁一次。
 // 这就是 BUG #2（按三键时最大 UI 强制回到眼前）和 BUG #3（每次加载都要重新解锁）
 // 的直接成因。
 //
 // 【新做法】
 // · 加载**开始**时快照原点的 pos/rot/scale；
 // · 加载**结束**后，只有在「原点被游戏/VRGIN 弄坏」时才还原快照；
 // · 判坏标准只看**姿态是否离谱**（翻滚过大 / 位置被弹回原点附近），
 // 不再做「无条件拉平」；
 // · 还原时连 pos/rot/scale 一起还原，而不是只清 x/z 旋转 ——
 // 这样用户手动摆好的世界位置、手动解锁出来的朝向都不会丢。
 //
 // 这样既不抢玩家的手动操作，也能兜住"加载把原点弄飞"的情况。

 private static bool _hasOriginSnapshot;
 private static Vector3 _snapPos;
 private static Quaternion _snapRot;
 private static Vector3 _snapScale;

 /// <summary>加载开始时调用：快照 VR 原点。已解锁/已摆好的状态会被完整保住。</summary>
 internal static void SnapshotOrigin()
 {
 try
 {
 Transform origin = VRRef.GetVROrigin();
 if (origin == null) { _hasOriginSnapshot = false; return; }

 _snapPos = origin.position;
 _snapRot = origin.rotation;
 _snapScale = origin.localScale;
 _hasOriginSnapshot = true;

 Vector3 e = origin.rotation.eulerAngles;
 FixLog.Info("已快照 VR 原点: pos=" + _snapPos.ToString("0.00")
 + ", rot=(x=" + Normalize(e.x).ToString("0.00")
 + ", y=" + Normalize(e.y).ToString("0.00")
 + ", z=" + Normalize(e.z).ToString("0.00") + ")");
 }
 catch (Exception ex)
 {
 _hasOriginSnapshot = false;
 FixLog.Debug("SnapshotOrigin 异常: " + ex.Message);
 }
 }

 /// <summary>
 /// 
 ///
 /// 为什么这里不写：
 /// 的判据（倾斜>25° 或 位移>200m）对真实故障无效 ——
 /// 原点被 StandingMode.OnStart() 归零时只是从 (2.3,0.9,4.4) 变成 (0,0,0)，
 /// 位移才 5 米、倾斜为 0，会被判成「状态正常，保持不动」。
 /// 而 的"连续 120 帧抢写"更糟 —— 它分不清"游戏重置"和"玩家手动摆正"，
 /// 会把玩家按三键摆平的姿态又掰回去（用户实测反馈）。
 ///
 /// 的最终方案：在 VRGIN.Modes.StandingMode.OnStart 上挂 Harmony postfix，
 /// **一次**把记忆的姿态写回去压过它的归零。见 OriginGuardFix。
 /// 所以这里只打日志、不再写，避免多个系统抢写同一个 Transform。
 /// </summary>
 private static void LevelVROrigin()
 {
 try
 {
 Transform origin = VRRef.GetVROrigin();
 if (origin == null) return;

 Vector3 e = origin.rotation.eulerAngles;
 float dx = Normalize(e.x);
 float dz = Normalize(e.z);

 string snap = _hasOriginSnapshot
 ? "，位移=" + (origin.position - _snapPos).magnitude.ToString("0.00")
 : "";

 FixLog.Info("VR 原点现状（x=" + dx.ToString("0.00")
 + ", z=" + dz.ToString("0.00") + snap
 + "）；姿态记忆与恢复由 OriginGuardFix 负责 —— " + OriginGuardFix.Describe());

 _hasOriginSnapshot = false;
 }
 catch (Exception ex)
 {
 FixLog.Debug("LevelVROrigin 异常: " + ex.Message);
 }
 }

 /// <summary>把 euler 角归一化到 (-180, 180]，方便判断"歪了多少"。</summary>
 private static float Normalize(float angle)
 {
 angle %= 360f;
 if (angle > 180f) angle -= 360f;
 if (angle <= -180f) angle += 360f;
 return angle;
 }

 /// <summary>
 /// 重新应用一次地图光照。
 ///
 /// ⚠️默认已改为 **false**。
 ///
 /// 为什么必须关掉：反编译证实 `map.sunType = sunType` 会走
 /// Studio.Map.sunType setter → SunLightInfo.Info.SetLight(_targetLight)
 /// light.color = info.color; ← 覆盖光色
 /// light.transform.eulerAngles = info.angle; ← 覆盖太阳角度 = **阴影方向**
 /// light.intensity = info.intensity; ← 覆盖光强
 ///
 /// 而游戏**自己**在 `Studio.Map.Reserve()`（反编译第 162 行）已经执行过
 /// 同样的 `sunType = sunType;`——那时机才是正确的（地图激活那一刻）。
 /// 我们的调用发生在其**之后**（等加载稳定 + 10 帧才跑），于是把用户随后在
 /// 「画面效果」里调的太阳角度/颜色/强度，以及场景卡保存的光照配置
 /// **全部覆盖回地图预制体里的旧值**：
 /// · 太阳角度变了 → 阴影方向/位置跟着变 → 「阴影错位」
 /// · 光色 / 强度变了 → 「整体颜色质感不正确」
 ///
 /// 这两条正是用户实测到的新增 bug。
 ///
 /// 这一项当初是为修「第二次加载场景卡后整场景发灰」加的。事后查明那件事与
 /// 光照无关（逐字节诊断证明是相机/图层问题），所以保留开关仅供极端情况下
 /// 手动排查用，**默认关闭**。
 /// </summary>
#if !LEAN
 private static void ReapplyMapLight()
 {
 try
 {
 Studio.Map map = Studio.Map.IsInstance() ? Studio.Map.Instance : null;
 if (map == null) { FixLog.Info("重应用地图光照: Studio.Map.Instance = null，跳过。"); return; }

 if (map.sunLightInfo == null)
 {
 FixLog.Info("重应用地图光照: sunLightInfo = null（该地图没有太阳光信息），跳过。");
 return;
 }

 // 真的要做也绝不能盲目覆盖 —— 先把当前光照状态读出来存底，
 // 覆盖之后如果变得更糟至少有据可查。注意：SunLightInfo.Info 有个
 // CopyLight(Light) 正好是"读当前值"，但 _targetLight 是私有的拿不到，
 // 所以用 RenderSettings.sun 兜底（太阳即 Directional Light）。
 var beforeColor = RenderSettings.sun != null ? RenderSettings.sun.color : Color.black;
 var beforeAngle = RenderSettings.sun != null ? RenderSettings.sun.transform.eulerAngles : Vector3.zero;
 var beforeIntensity = RenderSettings.sun != null ? RenderSettings.sun.intensity : 0f;

 var before = map.sunType;
 map.sunType = before;
 var after = map.sunType;

 FixLog.Warn("重应用地图光照（高风险操作，已覆盖当前光照）: sunType " + before + " -> " + after
 + ", map.no = " + map.no
 + " | 太阳光色 " + beforeColor + " -> " + (RenderSettings.sun != null ? RenderSettings.sun.color : Color.black)
 + " | 太阳角度 " + beforeAngle.ToString("0.0") + " -> "
 + (RenderSettings.sun != null ? RenderSettings.sun.transform.eulerAngles.ToString("0.0") : "n/a")
 + " | 强度 " + beforeIntensity.ToString("0.00") + " -> "
 + (RenderSettings.sun != null ? RenderSettings.sun.intensity.ToString("0.00") : "n/a"));
 }
 catch (Exception e)
 {
 FixLog.Warn("重应用地图光照失败: " + e.Message);
 }
 }
#else
 // 【LEAN】ReapplyMapLight 整项删除：连方法本体一起不编译
#endif

 private static int GetSelectedState()
 {
 try
 {
 Studio.Studio studio = Studio.Studio.IsInstance() ? Studio.Studio.Instance : null;
 if (studio == null) return -1;
 return Studio.Studio.optionSystem.selectedState;
 }
 catch { return -1; }
 }

 private static int ParseLayers(string csv)
 {
 if (string.IsNullOrEmpty(csv)) return 0;
 string[] parts = csv.Split(',');
 int mask = 0;
 for (int i = 0; i < parts.Length; i++)
 {
 string name = parts[i].Trim();
 if (name.Length == 0) continue;
 int layer = LayerMask.NameToLayer(name);
 if (layer >= 0) mask |= 1 << layer;
 }
 return mask;
 }

 // ---------------- 相机背景同步（默认关闭）----------------

 private void SafeFixCameraBackground()
 {
 try { FixCameraBackground(); }
 catch (Exception e) { FixLog.Debug("FixCameraBackground 异常: " + e.Message); }
 }

 private void FixCameraBackground()
 {
 if (_cameraSyncDisabled) return;

 Camera vcam = GetVrCamera();
 Camera studioCam = VRRef.GetStudioMainCamera();
 if (vcam == null || studioCam == null || vcam == studioCam) return;

 // Studio 主相机已经被 VRGIN 的 CameraKiller 关掉了（clearFlags=Nothing、背景=黑）。
 // 从它同步会把天空盒换成纯黑，反而更容易发灰 —— 所以直接跳过。
 if (studioCam.clearFlags == CameraClearFlags.Nothing
 || studioCam.clearFlags == CameraClearFlags.Depth)
 return;

 CameraClearFlags wantFlags = studioCam.clearFlags == CameraClearFlags.Skybox
 ? CameraClearFlags.Skybox
 : CameraClearFlags.SolidColor;

 // 绝不把 Skybox 降级成纯色
 if (vcam.clearFlags == CameraClearFlags.Skybox
 && wantFlags != CameraClearFlags.Skybox)
 return;

 Skybox srcSky = studioCam.GetComponent<Skybox>();
 Material wantSkyMat = (srcSky != null) ? srcSky.material : null;

 Skybox dstSky = vcam.GetComponent<Skybox>();
 Material curSkyMat = (dstSky != null) ? dstSky.material : null;

 bool needApply = (vcam.clearFlags != wantFlags)
 || (wantFlags == CameraClearFlags.SolidColor && vcam.backgroundColor != studioCam.backgroundColor)
 || (wantSkyMat != null && curSkyMat != wantSkyMat);

 if (!needApply) return;

#if LEAN
 float interval = 1.0f; // 【LEAN】该配置项已删，用原默认值
#else
 float interval = Mathf.Max(0.1f, KKCharaStudioVRFixPlugin.CfgCameraSyncIntervalSeconds.Value);
#endif
 if (Time.realtimeSinceStartup - _lastCameraSyncTime < interval) return;

#if LEAN
 int maxPerMinute = 60; // 【LEAN】该配置项已删，用原默认值
#else
 int maxPerMinute = Mathf.Max(1, KKCharaStudioVRFixPlugin.CfgCameraSyncMaxPerMinute.Value);
#endif
 if (Time.realtimeSinceStartup - _cameraSyncWindowStart > 60f)
 {
 _cameraSyncWindowStart = Time.realtimeSinceStartup;
 _cameraSyncCount = 0;
 }
 if (_cameraSyncCount >= maxPerMinute)
 {
 _cameraSyncDisabled = true;
 FixLog.Warn("相机渲染状态被反复覆盖（超过 " + maxPerMinute + " 次/分钟），已自动停用。");
 return;
 }

 _lastCameraSyncTime = Time.realtimeSinceStartup;
 _cameraSyncCount++;

 if (vcam.clearFlags != wantFlags)
 vcam.clearFlags = wantFlags;

 if (wantFlags == CameraClearFlags.SolidColor
 && vcam.backgroundColor != studioCam.backgroundColor)
 vcam.backgroundColor = studioCam.backgroundColor;

 if (wantSkyMat != null && curSkyMat != wantSkyMat)
 {
 if (dstSky == null) dstSky = vcam.gameObject.AddComponent<Skybox>();
 dstSky.material = wantSkyMat;
 }
 }

 // ---------------- 控制器健康检查（默认关闭）----------------

#if !LEAN
 private void SafeCheckControllers()
 {
 try
 {
 if (TransitionActive) { _deadSince = -1f; return; }
 if (VRRef.IsGameLoading()) { _deadSince = -1f; return; }

 if (!ControllersDead()) { _deadSince = -1f; return; }
 if (!AnyControllerConnected()) { _deadSince = -1f; return; }

 if (_deadSince < 0f)
 {
 _deadSince = Time.realtimeSinceStartup;
 RefreshControllerManagers(rebind: true);
 return;
 }

 float deadFor = Time.realtimeSinceStartup - _deadSince;
#if LEAN
 if (deadFor < 10f) return; // 【LEAN】该配置项已删，用原默认值
#else
 if (deadFor < KKCharaStudioVRFixPlugin.CfgControllerDeadSeconds.Value) return;
#endif
#if LEAN
 return; // 【LEAN·第三批删除】RebuildModeIfDead 整项删除（它会自己造成跳变）
#else
 if (KKCharaStudioVRFixPlugin.CfgRebuildModeIfDead == null
 || !KKCharaStudioVRFixPlugin.CfgRebuildModeIfDead.Value) return;
#endif

 FixLog.Warn("手柄已丢失 " + deadFor.ToString("0.0") + " 秒，重建 StandingMode。");
 _deadSince = -1f;
 RebuildModePreservingOrigin();
 }
 catch (Exception e)
 {
 FixLog.Debug("CheckControllers 异常: " + e.Message);
 }
 }
#else
 // 【LEAN】RepairControllers 整项删除：连方法本体一起不编译
#endif

#if !LEAN
 // 【精简版瘦身】诊断输出 + 只服务于已删除功能的工具方法。
 private static bool ControllersDead()
 {
 try
 {
 VRManager mgr = VR.Manager;
 if (mgr == null) return true;
 ControlMode mode = mgr.Mode;
 if (mode == null) return true;
 return ControllerDead(mode.Left) || ControllerDead(mode.Right);
 }
 catch { return false; }
 }
#else
 // 【LEAN】整段删除（诊断输出 / 只被已删除的控制器健康检查使用）
#endif

#if !LEAN
 // 【精简版瘦身】诊断辅助。
 private static bool ControllerDead(Controller c)
 {
 try
 {
 if (c == null) return true;
 SteamVR_TrackedObject tracked = c.GetComponent<SteamVR_TrackedObject>();
 if (tracked == null) return false; // 判断方式不适用 → 不下"损坏"结论
 return (int)tracked.index < 0;
 }
 catch { return false; }
 }
#else
 // 【LEAN】整段删除（诊断 / 只被已删除的控制器健康检查使用）
#endif

#if !LEAN
 // 【精简版瘦身】诊断输出 + 只服务于已删除功能的工具方法。
 private static bool AnyControllerConnected()
 {
 try
 {
 Type t = VRRef.FindType("SteamVR");
 if (t == null) return true;
 bool[] connected = VRRef.GetStatic(t, "connected") as bool[];
 if (connected == null) return true;
 for (int i = 0; i < connected.Length; i++)
 if (connected[i]) return true;
 return false;
 }
 catch { return true; }
 }
#else
 // 【LEAN】整段删除（诊断输出 / 只被已删除的控制器健康检查使用）
#endif

 internal static void RefreshControllerManagers(bool rebind)
 {
 try
 {
 Transform origin = VRRef.GetVROrigin();
 if (origin == null) return;

 SteamVR_ControllerManager[] managers =
 origin.GetComponents<SteamVR_ControllerManager>();

 for (int i = 0; i < managers.Length; i++)
 {
 SteamVR_ControllerManager m = managers[i];
 if (m == null || !m.enabled) continue;
 if (rebind) { m.enabled = false; m.enabled = true; }
 else m.Refresh();
 }
 }
 catch (Exception e) { FixLog.Debug("RefreshControllerManagers 异常: " + e.Message); }
 }

 internal static void RebuildModePreservingOrigin()
 {
 // 重建会触发 StandingMode.OnStart 归零 + RestoreOriginCo 回写，
 // 都是"我方已知"的 origin 移动 → 抑制记录仪，避免自噪。
 JumpDiagnostics.Suppress(1.5f);
 Transform origin = VRRef.GetVROrigin();
 Vector3 pos = Vector3.zero;
 Quaternion rot = Quaternion.identity;
 Vector3 scale = Vector3.one;
 bool hasOrigin = origin != null;

 if (hasOrigin)
 {
 pos = origin.position;
 rot = origin.rotation;
 scale = origin.localScale;
 }

 if (!VRRef.RebuildStandingMode())
 {
 FixLog.Warn("重建 StandingMode 失败。");
 return;
 }

 if (hasOrigin && _instance != null)
 _instance.StartCoroutine(_instance.RestoreOriginCo(origin, pos, rot, scale));

 // 场景卡加载后 Mode 被销毁 → 最终落点必须是**场景卡机位**。
 // 实机日志铁证：加载结束已对齐 → 约 0.8 秒后 Mode 遭到销毁 → 本方法重建 +
 // 恢复"加载前位姿" → 镜头又跑离场景卡机位 = 用户报的「位置并不正确」。
 if (_instance != null && VRRef.IsSceneCardLoadSticky())
 _instance.StartCoroutine(_instance.AlignToCardAfterRebuildCo());
 }

 private IEnumerator AlignToCardAfterRebuildCo()
 {
 for (int i = 0; i < 10; i++) yield return null; // 等 RestoreOriginCo 先落位
 try
 {
 bool called = VRRef.CallMoveToCurrent();
 FixLog.Key("场景卡加载后 Mode 曾被销毁 → 已重建并重新对齐到场景卡机位"
 + (called ? "（1 次 MoveToCurrent）。" : "（MoveToCurrent 调用失败）。"));
 }
 catch (Exception e) { FixLog.Debug("AlignToCardAfterRebuild 异常: " + e.Message); }
 }

 private IEnumerator RestoreOriginCo(Transform origin, Vector3 pos, Quaternion rot, Vector3 scale)
 {
 for (int frame = 0; frame < 4; frame++)
 {
 yield return null;
 if (origin == null) yield break;
 try
 {
 // 我方回写 → 抑制记录仪（随写随抑制）
 JumpDiagnostics.Suppress(0.3f);
 origin.position = pos;
 origin.rotation = rot;
 origin.localScale = scale;
 }
 catch { yield break; }
 }
 }

 // ---------------- 加载后的恢复（只做零风险项）----------------

 private IEnumerator RecoverAfterLoadCo()
 {
 _recovering = true;
 _transitionActive = true;

 try
 {
 float deadline = Time.realtimeSinceStartup + 60f;
 int stableFrames = 0;
 while (Time.realtimeSinceStartup < deadline)
 {
 if (VRRef.IsGameLoading()) stableFrames = 0;
 else stableFrames++;
 if (stableFrames >= 10) break;
 yield return null;
 }

 for (int i = 0; i < 4; i++) yield return null;
 if (!IsVrUsable()) yield break;

 FixLog.Info("加载完成，执行掩码修复。");
 PerfProbe.Begin("SafeFixVrCameraMask");
 try { SafeFixVrCameraMask(); }
 finally { PerfProbe.End("SafeFixVrCameraMask"); }
 SafeFixCameraBackground();

#if NEWFIX
 // 【NEWFIX】渲染队列：加载后可能出现新面板/新材质（队列默认 4000），
 // 这里补一次 —— 这就是原来那个 60 秒轮询本该做的事。
 try
 {
 PerfProbe.Begin("VRRenderQueueFix.RunPeriodic");
 VRRenderQueueFix.RunPeriodic("加载完成后");
 }
 catch (Exception e) { FixLog.Debug("加载后渲染队列异常: " + e.Message); }
 finally { PerfProbe.End("VRRenderQueueFix.RunPeriodic"); }

 // 【NEWFIX】窗口位置：换场景后重新确认一次（正常情况下第一次就对了）。
 try { VRInputFix.ReverifyHandle(); } catch { }

 // 【NEWFIX ★】图层差集取证：回答「为什么某些地图的可点击图标（心形）在 VR 里可见」。
 // 只打层名，成本几乎为零；一旦那几层出现在「VR 比主相机多看的层」里，凶手就点名了。
#if !LEAN
 try { DumpLayerDiff("加载完成后"); } catch (Exception e) { FixLog.Debug("DumpLayerDiff 异常: " + e.Message); }
#endif
 // 【NEWFIX】对象级取证已完成使命（确认心形图标 = 层[14]Action/Gate 的 5 个 "Icon"），
 // 它的 `FindObjectsOfTypeAll` 每次加载要卡 0.5~1 秒 → **已撤掉调用**（函数保留备用）。
#endif

 // 不管配置开关，加载完成后都打一份"源相机 vs VR 相机"的效果现场，
 // 用来验证"卡片里的画面效果有没有真的传到 VR 相机"（两张不同效果的卡一对比就见分晓）。
#if !LEAN
 try { ColorGradingFix.DumpEffectState("场景卡/地图加载完成"); }
 catch (Exception e) { FixLog.Debug("DumpEffectState 异常: " + e.Message); }
#endif

 // 场景切换后丢弃相机缓存（新场景可能是新 rig/新相机）
 try { ColorGradingFix.InvalidateCameraCache(); } catch { }

 // 同步画面效果参数 + 消除双重后处理（详见 ColorGradingFix.SyncFromSource）
 if (KKCharaStudioVRFixPlugin.CfgSyncEffectsFromSource == null
 || KKCharaStudioVRFixPlugin.On(KKCharaStudioVRFixPlugin.CfgSyncEffectsFromSource))
 {
 try { ColorGradingFix.SyncFromSource("场景卡/地图加载完成", true, true); }
 catch (Exception e) { FixLog.Debug("SyncFromSource 异常: " + e.Message); }
 }

 if (KKCharaStudioVRFixPlugin.On(KKCharaStudioVRFixPlugin.CfgReapplyColorGrading))
 {
 int n = ColorGradingFix.ForceEnable("加载完成后");
 // ：这条日志以前只报"修正 N 个被禁用的后处理"，
 // 组件被销毁时 N=0 → 看起来一切正常，其实画面是灰的（假安全结论）。
 // 现在把"组件是否存在"也报出来，避免再被误导。
 FixLog.Info("色彩分级检查: 修正 " + n + " 个；VR 相机现存被监视效果组件 "
 + ColorGradingFix.CountWatchedVrCamera() + " 个"
 + "（0 个 = 组件被销毁过，见 EnsurePresent 的处理）。");
 }

#if !LEAN
 if (KKCharaStudioVRFixPlugin.CfgReapplyMapLight != null
 && KKCharaStudioVRFixPlugin.CfgReapplyMapLight.Value)
 ReapplyMapLight();
#else
 // 【LEAN·第二批删除】整项删除：它会把太阳光盖掉 → 阴影错位/色变
#endif

 // 【顺序很重要】先做原点保护，再接管 Canvas。
 // v1.7.0 是无条件 LevelVROrigin() 覆盖掉用户手动解锁的状态（BUG #2/#3）。
 if (KKCharaStudioVRFixPlugin.On(KKCharaStudioVRFixPlugin.CfgLevelOriginAfterLoad))
 LevelVROrigin();

 if (KKCharaStudioVRFixPlugin.On(KKCharaStudioVRFixPlugin.CfgFixHiddenGuiCanvases))
 {
 // 先把历史污染回滚，再接管新画布 —— 避免刚接管完又被旧的错误图层状态带偏
 try { GuiCanvasFix.UndoStrayLayerMove("加载完成后"); }
 catch (Exception e) { FixLog.Debug("UndoStrayLayerMove 加载后异常: " + e.Message); }

 GuiCanvasFix.RunOnceForce("加载完成后");
 }

#if !LEAN
 if (KKCharaStudioVRFixPlugin.CfgRepairControllers != null
 && KKCharaStudioVRFixPlugin.CfgRepairControllers.Value
 && KKCharaStudioVRFixPlugin.CfgRebuildModeIfDead != null
 && KKCharaStudioVRFixPlugin.CfgRebuildModeIfDead.Value)
 {
 for (int i = 0; i < 8; i++) yield return null;
 if (!IsVrUsable()) yield break;
 if (ControllersDead() && AnyControllerConnected())
 {
 FixLog.Warn("加载后手柄仍不可用，重建 StandingMode。");
 RebuildModePreservingOrigin();
 for (int i = 0; i < 8; i++) yield return null;
 }
 }
#else
 // 【LEAN·第三批删除】RepairControllers / RebuildModeIfDead 整项删除
#endif

 // 「通用对齐」只处理**非场景卡**加载，
 // 而且起**地图加载也不再对齐** —— 用户要求"地图加载后保持站姿"。
 // 判定改用 ConsumeSceneCardLoad()（读一次即消费），不再用 30 秒时间窗。
 // ⚠️ 注意：本处只做"判断"，不消费标记 —— 消费留给下面的场景卡主逻辑，
 // 否则会被这里先吃掉，导致场景卡分支永远进不去。
 bool isSceneCardLoad = VRRef.IsLoadingSceneCard();
 bool cfgAlign = KKCharaStudioVRFixPlugin.On(KKCharaStudioVRFixPlugin.CfgAlignCameraAfterLoad);
 if (cfgAlign && IsVrUsable() && !isSceneCardLoad)
 {
 // 地图 / 其他加载：**不动镜头**。
 // 以前这里调 MoveToCurrent → 把玩家拽到 cameraCtrl 残留机位 → 多余跳变。
 FixLog.Info("本次为地图/其他加载 → 不对齐镜头，保持玩家当前站姿"
 + "（修「多次跳变」；场景卡加载才对齐）。");
 }

 // 场景卡加载：把 VR rig 对齐到场景卡保存的机位（只写一次）。
 //
 // 背景（反编译 Studio.Studio.LoadScene / LoadSceneCoroutine 确认）：
 // if (sceneInfo.cameraSaveData != null)
 // m_CameraCtrl.Import(sceneInfo.cameraSaveData);
 // 游戏只是把机位数据导入了 studio.cameraCtrl，
 // **但 VR rig（origin）不会自己跟过去** —— 这要靠原版
 // VRCameraMoveHelper.MoveToCurrent()：它读 cameraCtrl.Export()，
 // 算 tobeHeadPos = pos + rotation * distance，再摆 origin。
 //
 // ── 历史三次失败的完整因果链（重要，别再走回头路）──
 //
 // 「等 cameraCtrl 变化」失败：
 // 本方法开头已有「等加载结束 + 稳定 10 帧」循环，
 // 走到这里时 m_CameraCtrl.Import(...) 早执行完，
 // 机位数据从第一帧起就是最终值 → 永远等不到"变化"。
 //
 // 「对齐到对为止」失败一半（119~120 次重试仍不收敛）：
 // 目标 head=(2.63,1.33,4.44)，实际 head=(2.52,1.33,4.60)，差 0.19m。
 // 当时误判为「head 变换晚一帧刷新」。
 //
 // 「写一次 → 等 2 帧 → 校验」仍失败 4/15，
 // 并且**新增了一个原版没有的小幅跳变**（用户明确反馈）：
 // 实测日志 line 5061：
 // origin = (2.99, 0.76, 4.65) rot (0.00, 84.08, 0.00)
 // head = (2.51, 1.29, 4.58)
 // 目标 = (2.63, 1.33, 4.44) → 差 0.19m，且全部差在 x
 // 重试 15 次才"勉强通过"→ 那 15 次连续 MoveToCurrent
 // 就是用户看到的「在原机位附近小幅跳变一下」。
 //
 // ── 真正的根因（本轮定位，反编译铁证）──
 // 原版 MoveTo()（_decomp/003 第 577–599 行）最后一步：
 // vROrigin.transform.parent = val;
 // vROrigin.transform.rotation =
 // GripMoveKKCharaStudioTool.RemoveXZRot(vROrigin.transform.rotation);
 // 而 RemoveXZRot（第 1791–1801 行）就是：
 // Vector3 e = q.eulerAngles; e.x = 0f; e.z = 0f; return Quaternion.Euler(e);
 // —— 把 origin 的**俯仰/翻滚直接清零，只留 yaw**。
 //
 // 但 MoveToCurrent() 传进来的 tobeHeadPos = pos + Euler(rotate) * distance
 // **是带俯仰的**（只要场景卡机位 rotate.x != 0）。
 // origin 被压平后，head 永远落不到 tobeHeadPos → 天然差 0.19m。
 //
 // 所以：
 // • 拿原始 tobeHeadPos 当判据的卡「永远判不过」→ 触发重试风暴；
 // • 重试风暴本身就是那个「多余的小幅跳变」（原版从不重试，所以原版没有）。
 //
 // ── 改法：分清「场景卡」与「地图」，且场景卡只调一次、不等待 ──
 //
 // 两个修正：
 //
 // ① 【判定】旧的 IsLoadingSceneCard() 是"30 秒时间窗"，
 // 实测导致**30 秒内连续加载时，地图加载被误判成场景卡**：
 // 42 次对齐 / 44 次加载结束；「地图/其他加载」出现 0 次。
 // → 每次加载都去调 MoveToCurrent，把 rig 拽到 cameraCtrl 的**残留机位**
 // → 用户看到的「多次跳变」。
 // 现在改用 ConsumeSceneCardLoad()：标记**读一次就消费**，绝不污染后续加载。
 //
 // ② 【等待】去掉 0.4.0 的"等 5 帧"。
 // 用户实测反馈：那 5 帧里的中间态反而**有概率把机位对错**。
 // 0.4.0 等 5 帧是因为它要修"原版加载场景卡后镜头跑到错误位置"，
 // 而那个"错误位置"我们的 OriginGuardFix 已经提前修好了 → 再等 5 帧纯属多余。
 // → 现在：加载一结束就调 1 次，落点最接近原版 0.0.3 的行为。
 //
 // ③ 【地图加载】完全不碰镜头。原版 0.0.3 对地图加载也不调 MoveToCurrent，
 // 用户想要的就是"地图加载后我人还在原地"。这也去掉了那几次多余跳变。
 bool sceneCardThisLoad = VRRef.ConsumeSceneCardLoad();

 // 补上配置守卫 —— ~ 这个分支**没有**检查
 // CfgAlignCameraAfterLoad，导致该项形同虚设（日志里 AlignCameraAfterLoad=False
 // 却仍然出现 30 次对齐）。用户 08:01 明确表示「MoveToCurrent 功能挺好、不用改」，
 // 所以默认值已改为 true（见 KKCharaStudioVRFixPlugin 的 Bind），
 // 但守卫必须补上，否则这个开关就是死的。
 //
 // 新增 `!_sceneCardAlignedByImport` 条件：
 // 若 Import 的 postfix 已经对齐过（那是更早、更"同一帧"的一次），
 // 这里就**不能再调**，否则就是同一张卡对齐两次 → 又是一跳。
 if (sceneCardThisLoad && cfgAlign && IsVrUsable()
 && !_sceneCardAligned && !_sceneCardAlignedByImport)
 {
 _sceneCardAligned = true;

 bool called = VRRef.CallMoveToCurrent();

 // 参考值（仅日志用，不参与判定）
 Vector3 expHead; Quaternion expRot;
 bool haveRef = VRRef.TryGetReachableHead(out expHead, out expRot);
 string refStr = haveRef ? expHead.ToString("0.00") : "未就绪";

 if (called)
 {
 FixLog.Key("场景卡加载完成 → 已把 VR 视角对齐到场景卡保存的机位"
 + "（加载结束后调用 1 次 MoveToCurrent；参考落点 " + refStr
 + "，修「第三个镜头位置错位」）。");
 }
 else
 {
 FixLog.Warn("场景卡加载完成，但 MoveToCurrent 调用失败（0.0.3 的 "
 + "VRCameraMoveHelper 可能未就绪）。");
 }

 VRRef.ClearSceneCardLoad();
 }
 else if (sceneCardThisLoad && _sceneCardAlignedByImport)
 {
 // Import 的 postfix 已经对齐过 —— 这里只打日志，不再动手。
 FixLog.Info("场景卡机位已由 Import 的 postfix 对齐过（更早的同一帧）→ "
 + "加载结束不再重复对齐，避免多余一跳。");
 VRRef.ClearSceneCardLoad();
 }
 else if (!sceneCardThisLoad)
 {
 // 地图 / 其他加载：**不动镜头**，玩家保持当前站姿。
 // 旧版本这里会调 MoveToCurrent → 把玩家拽到 cameraCtrl 里的残留机位
 // → 就是用户反复反馈的「多次跳变」。现在什么都不做。
 // （_sceneCardAligned 已在本次恢复流程开头复位，这里无需再动。）
 VRRef.ClearSceneCardLoad();
 }

 SafeFixVrCameraMask();
 SafeFixCameraBackground();

 // 加载已完整结束 —— 清掉「加载前位姿」快照，不让它泄漏到下一次加载。
 // （下一次加载的 prefix 会重新捕获。）
 try { VRRef.ClearLoadPose(); }
 catch (Exception e) { FixLog.Debug("ClearLoadPose 异常: " + e.Message); }

 // 原版这里无条件调 `Diagnostics.DumpAll("加载完成后")`，
 // 每次加载都往日志灌几十行 → 日志 60% 的体积来自这里。
 // 改成**只在配置开启时**导出（默认关）。需要现场时按 F7 即可（那条路径保留）。
#if !LEAN
 if (KKCharaStudioVRFixPlugin.CfgDumpAfterLoad != null
 && KKCharaStudioVRFixPlugin.CfgDumpAfterLoad.Value)
 {
 Diagnostics.DumpAll("加载完成后");
 }
#else
 // 【LEAN·第二批删除】整项删除：它是日志 60% 体积的来源，精简版不需要
#endif
 }
 finally
 {
 // 本次加载彻底结束 → 复位「已由 Import 对齐」标记，
 // 让下一次场景卡加载的 ImportPostfix 能正常置位。
 _sceneCardAlignedByImport = false;

 _transitionActive = false;
 _recovering = false;
 }
 }
 }
}
