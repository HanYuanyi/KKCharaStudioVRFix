using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace KKCharaStudioVRFix
{
 /// <summary>
 /// KKCharaStudioVRPlugin 0.0.3 的“地图 / 场景加载”BUG 修复插件。
 ///
 /// v1.3.0 关键修正（基于用户回传的诊断日志）：
 /// · 引导球全都在 **Studio/Col(27)** 图层，不是 Studio/Select(28)。
 /// 按原生 ReflectOption() 的规则同时处理 27 和 28。
 /// · 坏状态的 VR 相机掩码 = 全 32 层（= 某台全图层相机被登记成从相机，
 /// 触发 VRCamera.UpdateCameraConfig() 整体覆盖）。现在加了掩码守卫。
 /// · workInfo.visibleAxis 本来就是 false → F6/F9 那条路从一开始就是错的杠杆。
 /// · 配置版本迁移：旧 cfg 里的旧默认值（如 F9、AlignCameraAfterLoad=true）会被自动升级。
 /// </summary>
#if NEWFIX
 // 【NEWFIX】功能改动版：名字里带标记，方便在插件管理器里区分。
 [BepInPlugin(Guid, "KK CharaStudio VR Fix (for 0.0.3) [NEWFIX]", Version)]
#else
 [BepInPlugin(Guid, "KK CharaStudio VR Fix (for 0.0.3)", Version)]
#endif
 [BepInProcess("CharaStudio")]
 public class KKCharaStudioVRFixPlugin : BaseUnityPlugin
 {
 public const string Guid = "KKCharaStudioVRFix";
#if NEWFIX
 // 【NEWFIX】功能改动版。
 // ★ 注意：这个字符串会被 BepInEx 拿去 `new Version(...)`（反编译 BepInPlugin 构造函数第 762 行），
 // 所以**只能写纯数字点分版本**。曾经写成 "1.8.63-NEWFIX" → FormatException
 // → 整个插件被 BepInEx 静默丢弃（游戏里所有 BUG 全部回来、一条日志都没有）。
 public const string Version = "1.0.0";
#else
 public const string Version = "1.0.0";
#endif

 /// <summary>配置结构版本。改动默认值时 +1，旧配置会被自动迁移。</summary>
 private const int ConfigVersion = 11;

 internal static KKCharaStudioVRFixPlugin Instance;

 internal static ConfigEntry<bool> CfgWatchdogEnabled;
 internal static ConfigEntry<bool> CfgAdoptOnlyStudioCamera;

 internal static ConfigEntry<bool> CfgFixCullingMask;
 internal static ConfigEntry<bool> CfgStrictMaskRestore;
 internal static ConfigEntry<bool> CfgEnsureReferenceLayers;
 internal static ConfigEntry<string> CfgForbiddenLayers;
 internal static ConfigEntry<bool> CfgForceHideAxis;
 internal static ConfigEntry<bool> CfgVerboseCullingLog;

 internal static ConfigEntry<bool> CfgFixCameraBackground;
 internal static ConfigEntry<float> CfgCameraSyncIntervalSeconds;
 internal static ConfigEntry<int> CfgCameraSyncMaxPerMinute;

 internal static ConfigEntry<bool> CfgRepairControllers;
 internal static ConfigEntry<bool> CfgRebuildModeIfDead;
 internal static ConfigEntry<float> CfgControllerDeadSeconds;
 internal static ConfigEntry<bool> CfgAlignCameraAfterLoad;
 internal static ConfigEntry<bool> CfgAlignCameraPitch;
 internal static ConfigEntry<bool> CfgRaiseUIRenderQueue;
 internal static ConfigEntry<bool> CfgRaiseHandRenderQueue;
 internal static ConfigEntry<int> CfgUIRenderQueue;
 internal static ConfigEntry<bool> CfgDefaultUnlockedDrag;
 internal static ConfigEntry<bool> CfgDisableCursorConfine;
 internal static ConfigEntry<bool> CfgForceCursorVisible;
 internal static ConfigEntry<bool> CfgFixWindowHandle;
 internal static ConfigEntry<bool> CfgSyncEffectsFromSource;
 internal static ConfigEntry<bool> CfgEnableDiagnostics;

 /// <summary>诊断插桩总开关（见配置说明）。</summary>
 internal static bool DiagnosticsEnabled
 {
 get
 {
#if LEAN
 return false; // 【LEAN·第四批删除】EnableDiagnostics 整项删除
#else
 try { return CfgEnableDiagnostics == null || CfgEnableDiagnostics.Value; } catch { return true; }
#endif
 }
 }
 internal static ConfigEntry<bool> CfgMinimalMode;

 /// <summary>诊断仪表是否启用（总开关，见 EnableDiagnostics 配置说明）。</summary>
 internal static bool DiagOn
 {
 get
 {
#if LEAN
 // 精简版：诊断一律关（连判断计算都省掉）。
 // 以前 DiagOn 只看配置项、而配置默认 true → 精简版虽然不输出日志，
 // 但所有诊断**计算**照样在跑（枚举画布、算指纹、遍历字段…）—— 纯白烧。
 return false;
#else
 try { return CfgEnableDiagnostics == null || CfgEnableDiagnostics.Value; }
 catch { return true; }
#endif
 }
 }
 internal static ConfigEntry<KeyboardShortcut> CfgPullUIToFaceKey;
 internal static ConfigEntry<bool> CfgAutoPrimeMapLoad;
 internal static ConfigEntry<bool> CfgReapplyMapLight;
 internal static ConfigEntry<bool> CfgReapplyColorGrading;
 internal static ConfigEntry<bool> CfgFixOcclusion;
 internal static ConfigEntry<bool> CfgFixMissingMode;
 internal static ConfigEntry<bool> CfgBlockSpuriousModeRebuild;
 internal static ConfigEntry<bool> CfgFixHiddenGuiCanvases;
 internal static ConfigEntry<bool> CfgLevelOriginAfterLoad;
 internal static ConfigEntry<bool> CfgFixDropdowns;
 internal static ConfigEntry<bool> CfgDumpAfterLoad;

 internal static ConfigEntry<KeyboardShortcut> CfgDiagnosticKey;
 internal static ConfigEntry<KeyboardShortcut> CfgForceHideAxisKey;

 /// <summary>
 /// 退出时把日志缓冲刷盘 —— 日志改成缓冲写之后，最后不到 1 秒的内容
 /// 还躺在内存里，不刷就丢。
 /// </summary>
 private void OnDestroy()
 {
 try { FixLog.Flush(); } catch { }
 }

 private void Awake()
 {
 Instance = this;
 FixLog.Source = Logger;

 try
 {
 string logPath = Path.Combine(Paths.BepInExRootPath, "KKCharaStudioVRFix.log");
 FixLog.InitFile(logPath);
 }
 catch { }

 FixLog.Key("修复插件 v" + Version + " 启动。日志文件: " + FixLog.FilePath);

 // ================= 【2026-10-22 ★★★】平面模式：整个插件不启动 =================
 // 和 0.0.3 的做法**完全一致**（反编译 KKCharaStudioVRPlugin 构造函数）：
 // bool flag = Environment.CommandLine.Contains("--novr");
 // if (CommandLine.Contains("--studiovr") || (!flag && SteamVRDetector.IsRunning))
 // { VRLoader.Create(true); SaveLoadSceneHook.InstallHook(); LoadFixHook.InstallHook(); }
 // else { VRLoader.Create(false); }
 // 也就是：**平面模式下 0.0.3 连一个 Harmony 钩子都不装**，只把 VR 关掉。
 // （`SteamVRDetector.IsRunning` 的实现就是"有没有 vrcompositor 进程"。）
 //
 // 我们以前没做这一步 → 平面模式下照样装钩子、照样跑逻辑 →
 // 钩子里碰 `VR.Camera`（惰性 getter）凭空造出一套假 VR rig → 相机被抢：
 // 蓝背景 + UI 仍在 + 镜头无法移动。这就是 2026-10-22 用户报的平面卡死。
 //
 // 现在：平面模式**直接 return**，一个钩子都不装、一个配置项都不 Bind、看门狗也不创建
 // —— 与"根本没装这个插件"完全等价。
 if (!IsVrSession())
 {
 FixLog.Key("平面模式（未启用 VR）→ 本插件不安装任何钩子、不创建看门狗，"
 + "行为与未安装完全一致。判定依据见 IsVrSession()。");
 return;
 }

 try
 {
 BindConfig();
 MigrateConfig();
 }
 catch (Exception e)
 {
 FixLog.Error("读取/迁移配置失败: " + e);
 }

 LogEffectiveSettings();

 try
 {
 SceneTransitionHook.Install(new Harmony(Guid));
 ColorGradingFix.Install(new Harmony(Guid + ".fx"));
 DropdownFix.Install(new Harmony(Guid + ".dropdown"));
 // 窗口拖动**坐标修正** —— patch **所有**程序集的 UILib.MovableWindow
 // （以前只 patch 第一个找到的 → 打错靶子，ME 窗口从未被修正）
 // 注意：只修「VR 里拖动会瞬移」这个 VR 专属 BUG（平面模式没这问题）；
 // 拖动卡顿是 uGUI 自身开销（平面模式也一样卡），不属本插件范畴，相关测量代码已删除。
 MovableWindowFix.Install(new Harmony(Guid + ".movablewin"));
 // 【NEWFIX ★★★】掩码事件钩子必须常开 —— 它原来是 `SlaveDiagnostics.Install`，
 // 而 SlaveDiagnostics 是诊断类（精简版已被换成空壳）。
 // 但里面挂的 `VRCamera.UpdateCameraConfig` / RegisterSlave / UnregisterSlave 三个 postfix
 // 都只是调 `FixVrCameraMaskNow()` —— **这是功能性修复，不是诊断**：
 // VRGIN 在渲染前（CameraKiller.OnPreCull）会重算掩码，晚于我们的 Update，
 // 不在这三个时刻纠正，掩码就会被它覆盖 → FK/IK 轴全部显示。
 MaskEventFix.Install(new Harmony(Guid + ".maskevent"));
 // MoveToCurrent postfix（俯仰补回覆盖 10 机位快捷键）。
 // 本插件的 Awake 早于 KKCharaStudioVRPlugin 加载，这里多半装不上，
 // 由 StartupCleanupCo 的 RetryMoveToCurrentHook() 兜底重试。
 VRRef.InstallMoveToCurrentHook(new Harmony(Guid + ".moveto"));
 if (DiagnosticsEnabled)
 {
 // 跳变重装诊断：Mode.OnDestroy 调用栈 + origin 行车记录仪
 #if !LEAN
 JumpDiagnostics.Install(new Harmony(Guid + ".jumpdiag"));
 #endif
 // VR 鼠标注入诊断（窗口 rect 校验 + 贴角落报警）
 #if !LEAN
 VRInputFix.Install(new Harmony(Guid + ".vrinput"));
 #endif
 // 事件驱动：hook VRGIN 的 CopyFX/Copy，替掉后处理与掩码的周期轮询

 // 指针链路诊断（注册表/距离/射线）+ 输入模拟器调用记录
 #if !LEAN
 VRPointerDiagnostics.Install(new Harmony(Guid + ".vrpointer"));
 VRPointerDiagnostics.InstallInputSpy(new Harmony(Guid + ".vrinputspy"));
 #endif
 }
 else
 {
 FixLog.Key("【诊断】EnableDiagnostics=false → 所有诊断插桩已跳过（用于纯净 A/B 对比帧率）。");
 }

 // 【NEWFIX ★★★】这两组是**功能性事件**（不是诊断），必须常开：
 // · CopyFX → 源相机效果变化时重建后处理副本（画面效果/变灰修复）
 // · Canvas.OnEnable → 新画布一出生就接管（UI 图层保修）
 // 它们原来被写在 `if (DiagnosticsEnabled)` 里面 —— 关掉诊断就会连带失效。
 CameraEventFix.Install(new Harmony(Guid + ".cameraevent"));
 CameraEventFix.InstallCanvasHook(new Harmony(Guid + ".canvasevent"));
 // 挂在 VRGIN.Modes.StandingMode.OnStart 上，一次压过它的原点归零
 OriginGuardFix.Install(new Harmony(Guid + ".origin"));
 // 的 `MapStateDiagnostics`（地图状态打点）已**移除** ——
 // 它的使命（定案"重建↔跳变"的对应关系）已经完成，且它每次加载都往日志
 // 灌 8 行 `▸[地图状态 #N]`。删掉后日志清爽，功能不受任何影响。
 // 阻止 0.0.3 在场景卡加载期间多余地重建 StandingMode ——
 // 实机日志证实「重建」与「蓝屏跳变」一一对应。
 SpuriousModeRebuildGuard.Install(new Harmony(Guid + ".rebuildguard"));
 }
 catch (Exception e)
 {
 FixLog.Error("安装 Harmony 钩子失败: " + e);
 }

 try
 {
 GameObject go = new GameObject("KKCharaStudioVRFix");
 UnityEngine.Object.DontDestroyOnLoad(go);
 go.AddComponent<VRRecoveryWatchdog>();
 FixLog.Key("看门狗已启动。");
 }
 catch (Exception e)
 {
 FixLog.Error("启动看门狗失败: " + e);
 }
 }

 /// <summary>
 /// 【2026-10-22 ★★★】本次是不是 VR 会话 —— 判定逻辑**逐字对齐 0.0.3**
 /// （反编译 `KKCharaStudioVRPlugin` 构造函数）：
 ///
 /// bool noVr = Environment.CommandLine.Contains("--novr");
 /// if (CommandLine.Contains("--studiovr") || (!noVr && SteamVRDetector.IsRunning))
 /// → VRLoader.Create(true); SaveLoadSceneHook.InstallHook(); LoadFixHook.InstallHook();
 /// else
 /// → VRLoader.Create(false); // 平面：一个钩子都不装
 ///
 /// 其中 `SteamVRDetector.IsRunning`（反编译 VRGIN_KKCS）就是：
 /// Process.GetProcesses().Any(p => p.ProcessName == "vrcompositor")
 ///
 /// **为什么必须和它一模一样**：
 /// · 若 0.0.3 判平面、我们判 VR → 我们在平面模式里跑 VR 逻辑 → 踩 `VR.Camera` 惰性 getter
 /// 造假 rig → 相机被抢（蓝背景 + 镜头无法移动）；
 /// · 若 0.0.3 判 VR、我们判平面 → 我们所有修复都不生效。
 /// 所以这里不做任何"更聪明"的判断，直接对齐原版。
 /// </summary>
 private static bool IsVrSession()
 {
 try
 {
 string cmd = Environment.CommandLine ?? "";

 // 显式 VR 参数（--studiovr 是本插件的标准启动方式；--vr 是 MainGameVR 那套的写法）
 if (cmd.IndexOf("--studiovr", StringComparison.OrdinalIgnoreCase) >= 0
 || cmd.IndexOf("--vr", StringComparison.OrdinalIgnoreCase) >= 0)
 return true;

 // --novr：只屏蔽"自动检测 SteamVR"这条路（与 0.0.3 一致）
 if (cmd.IndexOf("--novr", StringComparison.OrdinalIgnoreCase) >= 0)
 return false;

 return SteamVrCompositorRunning();
 }
 catch (Exception e)
 {
 // 判定失败时**默认按 VR 处理**：现在每个游戏侧钩子入口都有 VrRunning 安全阀，
 // 多装钩子不会再把平面模式搞坏；反之若误判成平面，VR 修复会整场失效。
 FixLog.Warn("判定 VR 会话失败（按 VR 处理）: " + e.Message);
 return true;
 }
 }

 /// <summary>等价于 VRGIN 的 `SteamVRDetector.IsRunning`：系统里有没有 vrcompositor 进程。</summary>
 private static bool SteamVrCompositorRunning()
 {
 try
 {
 System.Diagnostics.Process[] ps = System.Diagnostics.Process.GetProcesses();
 for (int i = 0; i < ps.Length; i++)
 {
 try
 {
 if (ps[i] != null && ps[i].ProcessName == "vrcompositor") return true;
 }
 catch { } // 有的进程读不到名字（权限），跳过
 }
 }
 catch { }
 return false;
 }

 private static void BindConfig()
 {
#if !LEAN
 CfgWatchdogEnabled = Instance.Config.Bind("General", "Enabled", true,
 "总开关。关闭后本插件不做任何动作（排查用）。");
#else
 CfgWatchdogEnabled = null; // 【LEAN】总开关移除：插件恒定启用（F1 只留渲染队列）
#endif

#if !LEAN
 CfgAdoptOnlyStudioCamera = Instance.Config.Bind("Load", "AdoptOnlyStudioCamera", true,
 "只允许 VRGIN 收养 Studio.cameraCtrl.mainCmaera 作为主相机，其余带 MainCamera 标签的相机判为 Ignore。");
#else
 CfgAdoptOnlyStudioCamera = null; // 【LEAN】固定开启，F1 里不出现，描述文本也不进 DLL
#endif

#if !LEAN
 CfgFixCullingMask = Instance.Config.Bind("Fixes", "FixCullingMask", true,
 "按原生 Studio.CameraControl.ReflectOption() 的规则管理 VR 相机掩码里的\n" +
 "Studio/Col(27) 和 Studio/Select(28) 两层 —— 引导球(FK/IK 球)就画在 Studio/Col 上。\n" +
 "**这一项是修「FK/IK 球全部显示」的核心。**");
#else
 CfgFixCullingMask = null; // 【LEAN·A组】固定开启，F1 不出现
#endif

#if !LEAN
 CfgStrictMaskRestore = Instance.Config.Bind("Fixes", "StrictMaskRestore", true,
 "掩码守卫：当 VR 相机的掩码被污染（出现禁止图层 / 层数暴涨）时，\n" +
 "把它恢复成插件学到的「正常掩码」。日志里会写明剔除了哪些图层。\n" +
 "如果发现地图/角色有东西不显示，把这一项关掉试试。");
#else
 CfgStrictMaskRestore = null; // 【LEAN·A组】固定开启，F1 不出现
#endif

#if !LEAN
 CfgForbiddenLayers = Instance.Config.Bind("Fixes", "ForbiddenLayers",
 "UI,TransparentFX,Ignore Raycast",
 "永远不允许出现在 VR 相机掩码里的图层（逗号分隔）。\n" +
 "正常掩码里本来就没有这几层；一旦出现，说明掩码被污染了。");
#else
 CfgForbiddenLayers = null; // 【LEAN】固定开启，F1 里不出现，描述文本也不进 DLL
#endif

#if !LEAN
 CfgEnsureReferenceLayers = Instance.Config.Bind("Fixes", "EnsureReferenceLayers", true,
 "保证参考掩码里的图层始终存在。\n" +
 "日志显示 VRGIN 每秒会重算约 7 次掩码（CameraKiller 反复开关相机 → CameraSlave\n" +
 "反复注销/登记 → UpdateCameraConfig 反复整体覆盖），注销时掩码会掉到只剩 1 层，\n" +
 "此时地图和角色都会消失（表现为画面发灰/发空）。本项把参考图层补回去。");
#else
 CfgEnsureReferenceLayers = null; // 【LEAN·A组】固定开启，F1 不出现
#endif

#if !LEAN
 CfgFixCameraBackground = Instance.Config.Bind("Fixes", "FixCameraBackground", false,
 "【默认关闭】把 VR 相机的 clearFlags / 背景色同步到 Studio 主相机。\n" +
 "注意：Studio 主相机已经被 VRGIN 的 CameraKiller 关掉了（clearFlags=Nothing、背景=黑），\n" +
 "从它同步会把天空盒换成纯黑，反而更容易显得发灰。除非明确需要，否则别开。");
#else
 CfgFixCameraBackground = null; // 【LEAN·第二批删除】整项删除，F1 不出现
#endif

#if !LEAN
 CfgCameraSyncIntervalSeconds = Instance.Config.Bind("Fixes", "CameraSyncIntervalSeconds", 1.0f,
 "相机背景同步的最小间隔（秒）。");
#else
 CfgCameraSyncIntervalSeconds = null; // 【LEAN·第一批删除】整项删除，F1 不出现
#endif

#if !LEAN
 CfgCameraSyncMaxPerMinute = Instance.Config.Bind("Fixes", "CameraSyncMaxPerMinute", 20,
 "每分钟最多写多少次相机渲染状态，超过自动停用。");
#else
 CfgCameraSyncMaxPerMinute = null; // 【LEAN·第一批删除】整项删除，F1 不出现
#endif

#if !LEAN
 CfgVerboseCullingLog = Instance.Config.Bind("Debug", "VerboseCullingLog", false,
 "把每次掩码的实际改动写进日志（排查用，会很啰嗦）。");
#else
 CfgVerboseCullingLog = null; // 【LEAN·第一批删除】整项删除，F1 不出现
#endif

#if !LEAN
 CfgRepairControllers = Instance.Config.Bind("Fixes", "RepairControllers", false,
 "【默认关闭】手柄重绑定。会瞬间把手柄 SetActive(false) 再恢复，可能被误判成故障。");
#else
 CfgRepairControllers = null; // 【LEAN·第三批删除】整项删除，F1 不出现
#endif

#if !LEAN
 CfgRebuildModeIfDead = Instance.Config.Bind("Fixes", "RebuildModeIfDead", false,
 "【默认关闭】重建 StandingMode。会导致镜头跳变。");
#else
 CfgRebuildModeIfDead = null; // 【LEAN·第三批删除】整项删除，F1 不出现
#endif

#if !LEAN
 CfgControllerDeadSeconds = Instance.Config.Bind("Fixes", "ControllerDeadSeconds", 4.0f,
 "手柄连续丢失多少秒后才触发重建（秒）。");
#else
 CfgControllerDeadSeconds = null; // 【LEAN·第一批删除】整项删除，F1 不出现
#endif

#if !LEAN
 CfgAlignCameraAfterLoad = Instance.Config.Bind("Fixes", "AlignCameraAfterLoad", true,
 "场景卡加载完成后，把 VR 视角对齐到场景卡保存的机位"
 + "（调用原版 VRCameraMoveHelper.MoveToCurrent）。这是必需的一步 ——"
 + "原版 CameraControl.Import 只改 studio 相机数据，不会同步 VR 原点。"
 + "关闭后场景卡机位会停在原版摆放的错误位置。");
#else
 CfgAlignCameraAfterLoad = null; // 【LEAN】固定开启，F1 里不出现，描述文本也不进 DLL
#endif

 // 旧描述声称"蓝屏跳变 = UnloadUnusedAssets 与地图加载挤在一起"，
 // 该说法已被 2026-10-14 的 A/B 对比日志推翻：垫卡与直载两组**都有**数秒卡帧，
 // 但只有直载组出现跳变；真正原因是 rig（VRGIN_Camera）被场景卸载带走后惰性重建，
 // 已由 的 root-DDL 修复（日志证实 rigId 全程不变、Mode 死亡 0 次）。
 // 这个开关仍保留（无害、可关），但它不是蓝屏跳变的解药。
#if !LEAN
 CfgAutoPrimeMapLoad = Instance.Config.Bind("Fixes", "AutoPrimeMapLoad", true,
 "工作室就绪后自动预热地图加载路径（仅 VR；每场一次；"
 + "用户已自行加载过场景卡则跳过）。等价于手动「先加载一张没地图的场景卡」——"
 + "把一次性的 Resources.UnloadUnusedAssets 重活放在桌面期安静跑完，"
 + "而不是和开机后第一张有地图场景卡的地图加载/SteamVR 出图挤在一起"
 + "（那就是蓝屏跳变的来源）。");
#else
 CfgAutoPrimeMapLoad = null; // 【LEAN·第三批删除】整项删除，F1 不出现
#endif

#if !LEAN
 CfgAlignCameraPitch = Instance.Config.Bind("Fixes", "AlignCameraPitch", true,
 "对齐场景卡机位时，同时补回场景卡相机的完整旋转（含俯仰/翻滚）。\n"
 + "原版 MoveTo() 结尾的 RemoveXZRot 只保留水平偏航，导致加载后视角永远是"
 + "「变灰水平」——场景卡明明从上往下看，VR 里却看向地平线。\n"
 + "开启 = 视角与场景卡完全一致（代价：世界随俯仰一起倾斜，不适应就关）；"
 + "关闭 = 原版行为（只留水平偏航）。");
#else
 CfgAlignCameraPitch = null; // 【LEAN】固定开启，F1 里不出现，描述文本也不进 DLL
#endif

#if !LEAN
 CfgForceHideAxis = Instance.Config.Bind("Fixes", "ForceHideAxis", false,
 "强制把 workInfo.visibleAxis 置为 false。");
#else
 CfgForceHideAxis = null; // 【LEAN·第一批删除】整项删除，F1 不出现
#endif

#if !LEAN
 CfgRaiseUIRenderQueue = Instance.Config.Bind("Fixes", "RaiseUIRenderQueue", true,
 "把主 VR UI 面板材质的渲染队列抬到 4000，避免被半透明类特效盖住。\n" +
 "注意：实体几何遮挡来自深度测试，抬队列赢不了 —— UI 被挡按 PullUIToFace 拉回眼前。");
#else
 CfgRaiseUIRenderQueue = null; // 【LEAN】开关移除：恒定抬 UI 队列，只保留数值调整
#endif
#if !LEAN
 CfgRaiseHandRenderQueue = Instance.Config.Bind("Fixes", "RaiseHandRenderQueue", false,
 "是否也抬手柄（控制器）材质队列。\n" +
 "实机证实：从 第一次抬手柄材质起，手柄上的工具图标就消失了。\n" +
 "默认关闭先恢复图标；仅作诊断用。手柄渲染体清单无论如何都会每 5 秒打点。");
#else
 CfgRaiseHandRenderQueue = null; // 【LEAN·第三批删除】整项删除，F1 不出现
#endif

 // ================= UI 队列 / 输入指针 相关 =================
 // 时间线（用户实测反馈）：
 // （=7000）：用户说"帧数回来了"；
 // 起改成 9999：用户说"低帧率又回来了"，且此后一直卡。
 // 这是那两版之间**唯一**碰渲染的改动，而 7000 已经高于用户实测那张墙的 6000+，
 // 遮 UI 的问题在 7000 就已经解决了 —— 所以 9999 没有任何额外收益，只带来风险。
 // 先回退到 7000 做对照；若帧数恢复，就锁定是队列；若没恢复，继续按下面的开关二分。
 CfgUIRenderQueue = Instance.Config.Bind("Fixes", "UIRenderQueue", 7000,
 "VR UI 面板的渲染队列。VRGIN 的 GUIQuad 默认正好是 4000；\n" +
 "场景卡里 6000+ 的半透明材质（用户实测那张粉墙就是）会盖住 UI，\n" +
 "所以默认抬到 7000（7000 已足够盖过 6000+ 的墙；9999 无额外收益）。\n" +
 "注意：不透明实体几何按深度正常遮挡，抬高队列无效，那属于正常现象。");
#if !LEAN
 CfgDefaultUnlockedDrag = Instance.Config.Bind("Fixes", "DefaultUnlockedDrag", true,
 "进入工作室后自动把「拖动世界」设为解锁（lockRotXZ=false），不用每次手动按三键。\n" +
 "只在该手还没被观测到过时生效一次；之后你手动锁定/解锁的选择仍会被记住并在加载后恢复。");
#else
 CfgDefaultUnlockedDrag = null; // 【LEAN】固定开启，F1 里不出现，描述文本也不进 DLL
#endif
#if !LEAN
 CfgDisableCursorConfine = Instance.Config.Bind("Fixes", "DisableCursorConfine", true,
 "运行时把 VR context 的 ConfineMouse 置 false。\n" +
 "反编译依据：VRGUI.OnUpdate 在 ConfineMouse=true 时**每帧**执行\n" +
 "Cursor.lockState = Locked，会把光标钉在窗口中心、并隐藏光标，\n" +
 "与「把真实鼠标移到 VR 指针处」的注入逻辑互相打架（用户报的指针贴角落/点不动）。");
#else
 CfgDisableCursorConfine = null; // 【LEAN·A组】固定开启，F1 不出现
#endif
#if !LEAN
 CfgForceCursorVisible = Instance.Config.Bind("Fixes", "ForceCursorVisible", true,
 "每帧兜住 Cursor.visible = true。反编译依据：VR 里的模拟指针\n" +
 "(SimulatedCursor.OnGUI) 只在 Cursor.visible 为真时才绘制 ——\n" +
 "游戏一旦隐藏系统光标，VR 指针就一起消失（用户报的「VR 里鼠标指针没了」）。");
#else
 CfgForceCursorVisible = null; // 【LEAN·A组】固定开启，F1 不出现
#endif
#if !LEAN
 CfgFixWindowHandle = Instance.Config.Bind("Fixes", "FixWindowHandle", true,
 "定期让 VRGIN 重新解析游戏窗口句柄。反编译依据：WindowManager.Handle 是\n" +
 "**一次性缓存**，开机时窗口还没成形就会用错 rect 一整个会话，\n" +
 "导致注入的鼠标位置错 → 指针贴角落 / 拖窗口只能单方向动。");
#else
 CfgFixWindowHandle = null; // 【LEAN·A组】固定开启，F1 不出现
#endif

 // 对照用总开关：打开后**停掉本插件所有可选的运行时改动**，
 // 只保留「跳变修复（rig 保活）」这类必须项 —— 效果上接近原版 0.0.3。
 // 用途：一次重启就能回答"掉帧到底是不是插件造成的"（用户有对照组：原版 55 帧）。
#if !LEAN
 CfgEnableDiagnostics = Instance.Config.Bind("Diagnostics", "EnableDiagnostics", true,
 "诊断仪表总开关。\n" +
 "关掉后：不再安装指针链路探针（注册表/距离/射线 + 输入模拟记录）、\n" +
 "不再每帧跑卡帧仪与 origin 记录仪、不再打手柄渲染体/画面效果现场清单、\n" +
 "不再记录 VR 输入状态。**所有修复功能不受影响。**\n" +
 "用途：帧率对比实验 —— 「修复全开 + 诊断全关」若帧数回升，说明吃帧的是诊断代码；\n" +
 "注意：关掉后就没有【帧率】以外的现场信息了（【帧率】/【GC】始终保留，因为它就是量尺）。");
#else
 CfgEnableDiagnostics = null; // 【LEAN·第四批删除】整项删除（含重复那一行）
#endif

#if !LEAN
 CfgMinimalMode = Instance.Config.Bind("Fixes", "MinimalMode", false,
 "【对照用】一键停掉本插件所有可选的运行时改动（相机遮罩修复、色彩分级兜底、\n" +
 "UI 队列提升、Canvas 接管、画面效果同步、指针/手柄诊断等），只保留跳变修复。\n" +
 "打开后表现应接近原版 0.0.3。用来对照帧率：若打开后帧数恢复到 55，\n" +
 "说明掉帧是插件某个功能造成的；若仍是 26，则问题不在这些功能里。");
#else
 CfgMinimalMode = null; // 【LEAN·第一批删除】整项删除，F1 不出现
#endif

#if !LEAN
 CfgEnableDiagnostics = Instance.Config.Bind("Fixes", "EnableDiagnostics", true,
 "所有**诊断插桩**的总开关（跳变诊断栈 / 指针链路 / 输入模拟 / 耗时打点）。\n" +
 "用途：做一次干净的 A/B —— 关掉它重启，如果帧率明显回升，就说明掉帧来自这些诊断插桩\n" +
 "（本项目历史上已经因为'加检测手段'掉过 3 次帧，所以这个开关必须有）。\n" +
 "关掉不影响任何修复功能（跳变保活/画面效果同步/掩码修复等照常）。");
#else
 CfgEnableDiagnostics = null; // 【LEAN·第四批删除】整项删除（含重复那一行）
#endif

#if !LEAN
 CfgSyncEffectsFromSource = Instance.Config.Bind("Fixes", "SyncEffectsFromSource", true,
 "加载完成时把源相机(Studio 主相机)的画面效果参数同步到 VR 相机，\n" +
 "并按 VRGIN CopyFX 的本意关掉源相机上「与 VR 副本同时在算」的那一份。\n" +
 "依据：VRGIN CopyFX = 复制到 VR 相机 + disabledSourceFx 关掉源相机那份；\n" +
 "而本插件以前只在「VR 相机上组件全没了」时才重建，从不回传参数。\n" +
 "关掉它可回到旧行为（场景卡里改的画面效果不会生效）。");
#else
 CfgSyncEffectsFromSource = null; // 【LEAN·A组】固定开启，F1 不出现
#endif

#if !NEWFIX
#if !LEAN
 CfgPullUIToFaceKey = Instance.Config.Bind("Keys", "PullUIToFace",
 new KeyboardShortcut(KeyCode.F8),
 "一键把主 UI 面板拉到眼前 1.2m（0.4.0 同款 RepositionMainUI）。\n" +
 "UI 被桌子/道具挡住时按它。");
#else
 CfgPullUIToFaceKey = null; // 【LEAN】F8 功能早已删除（#if !NEWFIX），配置项一并移除
#endif
#else
 // 【NEWFIX】删掉 F8：用户实测「按了从来没反应」，而且 UI 被挡时用手柄把面板拖近更直接。
#endif

#if !LEAN
 CfgReapplyMapLight = Instance.Config.Bind("Fixes", "ReapplyMapLight", false,
 "加载完成后重新应用一次地图光照。\n" +
 "⚠️ 副作用证实：它会覆盖太阳光的颜色/角度/强度 —— 太阳角度一变，阴影方向就跟着错位，\n" +
 "光色一变，整体颜色质感就不对。而游戏自己在地图激活时已经应用过一次光照，\n" +
 "这一项只会让结果更糟。仅在前述两个问题都排除、且确认要手工复现某旧问题时才打开。");
#else
 CfgReapplyMapLight = null; // 【LEAN·第二批删除】整项删除，F1 不出现
#endif

#if !LEAN
 CfgReapplyColorGrading = Instance.Config.Bind("Fixes", "ReapplyColorGrading", true,
 "【修「画面变灰」的核心项】\n" +
 "把 VR 相机上被误禁用的色彩分级后处理（AmplifyColorEffect 等）重新打开。\n" +
 "诊断证据：正常时 enabled=True，变灰时 enabled=False，其余状态完全相同。\n" +
 "成因是 VRGIN 的 CopyFX 按「源相机启用状态」复制效果、又把源相机禁用，\n" +
 "所以第二次加载时复制到的是 disabled。");
#else
 CfgReapplyColorGrading = null; // 【LEAN·A组】固定开启，F1 不出现
#endif

#if !LEAN
 CfgFixOcclusion = Instance.Config.Bind("Fixes", "FixOcclusion", false,
 "修后处理时是否也接管 AmplifyOcclusionEffect（环境遮挡）。\n" +
 "原版行为：CopyFX 复制一次后源被禁用，第二次加载起 VR 相机上的 AO 也变成\n" +
 "disabled → AO 消失。我们旧版每 2 秒把它重新打开、缺失时重建，\n" +
 "用户实测「双眼看黑阴影角度错位」——AO 被以错误状态持续显示。\n" +
 "AO 对「发灰」修复没有贡献，默认交给原版；打开此项可恢复 及以前的处理。");
#else
 CfgFixOcclusion = null; // 【LEAN·第二批删除】整项删除，F1 不出现
#endif

#if !LEAN
 CfgFixMissingMode = Instance.Config.Bind("Fixes", "FixMissingMode", true,
 "【修「只剩头显能动、看不到手柄和 UI」】\n" +
 "当 VR.Manager.Mode 变成 null（控制器被整个销毁）时重建 StandingMode。\n" +
 "VR.Mode == null 是明确无歧义的损坏信号，不会像「手柄健康检查」那样误判。\n" +
 "重建时会保存并还原 VR 原点位置，不会把镜头弹回原点。");
#else
 CfgFixMissingMode = null; // 【LEAN·A组】固定开启，F1 不出现
#endif

#if !LEAN
 CfgBlockSpuriousModeRebuild = Instance.Config.Bind("Fixes", "BlockSpuriousModeRebuild", true,
 "修「加载场景卡时蓝屏跳变 + 手柄消失 + 镜头被甩到歪的位置」\n" +
 "0.0.3 的 LoadFixHook 在点加载按钮时会让 ForceResetVRModeCo 等 5 帧，\n" +
 "然后判定「mode 不是 GenericStandingMode 就重建」。但加载过程会打断 mode 的\n" +
 "可用性，导致这个判定在**mode 明明还好着**的时候误判成「丢了」→ 多余重建一次\n" +
 "StandingMode → 手柄消失（ControllerManager 被销毁）+ origin 被归零 + 朝向被抹平。\n" +
 "实机日志已证实：5 次加载里只有「发生重建」的那一次出现跳变，且一一对应。\n" +
 "本项在「场景卡加载期间 + mode 仍健在且就是 GenericStandingMode」时跳过这次重建。\n" +
 "mode 真的坏了时**不会**被拦，0.0.3 的自救照旧生效。");
#else
 CfgBlockSpuriousModeRebuild = null; // 【LEAN】固定开启，F1 里不出现，描述文本也不进 DLL
#endif

#if !LEAN
 CfgFixHiddenGuiCanvases = Instance.Config.Bind("Fixes", "FixHiddenGuiCanvases", true,
 "【修「系统菜单右侧面板看不见但能点到」】\n" +
 "VRGIN 的 VRGUI.CatchCanvas() 只接管它认得的 Canvas，漏掉的 Canvas 不会被渲染进\n" +
 "VR 的 UI 贴图、又留在 VR 相机不渲染的图层上 → 看不到，但 GraphicRaycaster 还能被射线点到。\n" +
 "本项按 VRGIN 自己的方式把漏掉的 Canvas 接管过来。");
#else
 CfgFixHiddenGuiCanvases = null; // 【LEAN·A组】固定开启，F1 不出现
#endif

#if !LEAN
 CfgLevelOriginAfterLoad = Instance.Config.Bind("Fixes", "LevelOriginAfterLoad", true,
 "【修「加载后镜头是歪的、拖动世界只能在歪的平面上水平移动」】\n" +
 "加载完成后把 VR 原点的俯仰/翻滚（X/Z 旋转）清零 —— 等价于 0.0.3 拖动工具里\n" +
 "ResetRotation() 做的事（也就是你手动按那三个键触发的那个）。\n" +
 "只在加载后做一次，不会周期性强制，避免和手动解锁旋转冲突。");
#else
 CfgLevelOriginAfterLoad = null; // 【LEAN】固定开启，F1 里不出现，描述文本也不进 DLL
#endif

#if !LEAN
 CfgFixDropdowns = Instance.Config.Bind("Fixes", "FixDropdowns", true,
 "【修「下拉菜单在 VR 里看不见但能点到」】\n" +
 "uGUI 的 Dropdown.Show() 会在运行时动态创建 m_Dropdown（弹出列表）和 m_Blocker（遮罩），\n" +
 "VRGIN 的 CatchCanvas 抓不到它们 → 留在 VR 相机不渲染的图层 → 看不见。\n" +
 "本项在 Show() 之后把它们挪到 VR 的 UI 图层。修 MaterialEditor 下拉、画面效果滤镜下拉等。");
#else
 CfgFixDropdowns = null; // 【LEAN】固定开启，F1 里不出现，描述文本也不进 DLL
#endif

#if !LEAN
 CfgDumpAfterLoad = Instance.Config.Bind("Debug", "DumpAfterLoad", false,
 "每次场景卡加载完成后自动导出一份诊断现场。\n" +
 "以前是无条件导出，导致日志 60% 的体积来自这里（每次几十行）。\n" +
 "关闭后日志会清爽很多；需要现场时按 F7 手动导出即可（那条路径不受影响）。");
#else
 CfgDumpAfterLoad = null; // 【LEAN·第二批删除】整项删除，F1 不出现
#endif

#if !NEWFIX
#if !LEAN
 CfgDiagnosticKey = Instance.Config.Bind("Keys", "DiagnosticDump", new KeyboardShortcut(KeyCode.F7),
 "【重要】导出诊断现场到日志文件。出 BUG 时按一下。");
#else
 CfgDiagnosticKey = null; // 【LEAN】F7 诊断导出整项删除
#endif
#else
 // 【NEWFIX】删掉 F7：它在**平面模式**下会读 `VR.Camera`，而那是惰性 getter
 // → 会凭空造出一套 VR rig，把相机抢走（镜头跳歪/卡死/UI 消失/FK-IK 轴全亮）。
 // 用户实测踩到过，所以这个快捷键直接取消（`Diagnostics.DumpAll` 也加了 VR.Active 安全阀）。
#endif

#if !NEWFIX
#if !LEAN
 CfgForceHideAxisKey = Instance.Config.Bind("Keys", "ForceHideAxis", new KeyboardShortcut(KeyCode.F6),
 "强制关闭 FK/IK 轴显示。");
#else
 CfgForceHideAxisKey = null; // 【LEAN·A组】固定开启，F1 不出现
#endif
#else
 // 【NEWFIX】删掉 F6 快捷键：切 FK/IK 轴显示是**游戏自带**的功能，
 // 插件再提供一个重复的快捷键没有意义（用户明确要求删）。
#endif
 }

 /// <summary>
 /// 配置迁移：BepInEx 会保留旧 cfg 里的旧默认值，导致新版本的默认值根本不生效
 /// （上一轮就是因此 F6 没反应、AlignCameraAfterLoad 还在跑）。
 /// 这里按版本号把有风险的项强制改成新默认值。
 /// </summary>
 private static void MigrateConfig()
 {
 // 注意：默认值必须是 0（不是 ConfigVersion）。
 // 如果默认写成 ConfigVersion，BepInEx 新建这个键时会直接填上最新版本号，
 // 于是"旧配置缺这个键"也会被当成"已迁移"，迁移就永远不执行（v1.3.0 的 bug）。
 ConfigEntry<int> version = Instance.Config.Bind("General", "ConfigVersion", 0,
 "配置结构版本，插件自动维护，不用手动改。");

 if (version.Value >= ConfigVersion)
 {
 FixLog.Info("配置版本 = " + version.Value + "，无需迁移。");
 return;
 }

 FixLog.Key("检测到旧配置（v" + version.Value + " → v" + ConfigVersion + "），正在升级默认值…");

 SetBool(CfgFixCullingMask, true);
 SetBool(CfgStrictMaskRestore, true);
 SetBool(CfgEnsureReferenceLayers, true);
 SetBool(CfgFixCameraBackground, false);
 SetBool(CfgRepairControllers, false);
 SetBool(CfgRebuildModeIfDead, false);
 // 默认改为 true —— 用户明确表示 MoveToCurrent 是需要的功能。
 SetBool(CfgAlignCameraAfterLoad, true);
 SetBool(CfgForceHideAxis, false);
 // 默认改为 false —— 它覆盖太阳光颜色/角度/强度，
 // 实测导致「加载后颜色质感不正确」+「阴影错位」。
 SetBool(CfgReapplyMapLight, false);
 SetBool(CfgReapplyColorGrading, true);
 SetBool(CfgFixMissingMode, true);
 // 新项，默认开 —— 修「蓝屏跳变 + 手柄消失」的核心。
 SetBool(CfgBlockSpuriousModeRebuild, true);
 // 默认关 —— 别再每次加载都灌几十行诊断。
 SetBool(CfgDumpAfterLoad, false);
 SetBool(CfgFixHiddenGuiCanvases, true);
 SetBool(CfgLevelOriginAfterLoad, true);
 SetBool(CfgFixDropdowns, true);

 // 旧版把强制关轴绑在 F9（和游戏截图键冲突），这里改成 F6
#if !NEWFIX
 try
 {
 if (CfgForceHideAxisKey.Value.MainKey == KeyCode.F9)
 {
 CfgForceHideAxisKey.Value = new KeyboardShortcut(KeyCode.F6);
 FixLog.Key("强制关轴按键: F9 → F6");
 }
 }
 catch { }
#endif

 version.Value = ConfigVersion;
 Instance.Config.Save();

 FixLog.Key("配置升级完成。");
 }

 /// <summary>
 /// 【精简版】读一个"固定开启"的开关。
 /// 精简版里这些 ConfigEntry 根本不绑定（=null），F1 里不出现、描述文本也不进 DLL，
 /// 但功能本身照常开启 —— 这就是「只删配置项与日志、保留功能」。
 /// </summary>
 internal static bool On(ConfigEntry<bool> entry)
 {
#if LEAN
 return true;
#else
 return entry == null || entry.Value;
#endif
 }

 /// <summary>【精简版】禁止图层名单写死在代码里，不再暴露成 F1 字符串。</summary>
 internal static string ForbiddenLayersValue()
 {
#if LEAN
 return "UI,TransparentFX,Ignore Raycast";
#else
 return CfgForbiddenLayers == null ? "UI,TransparentFX,Ignore Raycast" : CfgForbiddenLayers.Value;
#endif
 }

 private static void SetBool(ConfigEntry<bool> entry, bool value)
 {
 try
 {
 if (entry != null && entry.Value != value)
 {
 entry.Value = value;
 FixLog.Info(" " + entry.Definition.Section + "/" + entry.Definition.Key
 + " = " + value);
 }
 }
 catch { }
 }

 private static void LogEffectiveSettings()
 {
 try
 {
#if !LEAN
 // 【2026-10-22】精简版里下面这些配置项**全是 null**（为了 F1 干净已移除）。
 // 虽然 `FixLog.Key` 带 [Conditional("DIAG")]，精简版不定义 DIAG 时整段会被编译器剔除，
 // 但万一有人用 `LEAN;DIAG` 编译排障，这里就会当场空引用 —— 所以显式排除，不留隐患。
 FixLog.Key("生效设置: FixCullingMask=" + CfgFixCullingMask.Value
 + ", StrictMaskRestore=" + CfgStrictMaskRestore.Value
 + ", ForbiddenLayers=[" + CfgForbiddenLayers.Value + "]"
 + ", FixCameraBackground=" + CfgFixCameraBackground.Value);
 FixLog.Key("生效设置: RepairControllers=" + CfgRepairControllers.Value
 + ", RebuildModeIfDead=" + CfgRebuildModeIfDead.Value
 + ", AlignCameraAfterLoad=" + CfgAlignCameraAfterLoad.Value
 + ", AutoPrimeMapLoad=" + CfgAutoPrimeMapLoad.Value
 + ", AlignCameraPitch=" + CfgAlignCameraPitch.Value
 + ", RaiseUIRenderQueue=" + CfgRaiseUIRenderQueue.Value
 + ", RaiseHandRenderQueue=" + CfgRaiseHandRenderQueue.Value);
 FixLog.Key("生效设置: BlockSpuriousModeRebuild=" + CfgBlockSpuriousModeRebuild.Value
 + ", FixMissingMode=" + CfgFixMissingMode.Value
 + ", LevelOriginAfterLoad=" + CfgLevelOriginAfterLoad.Value
 + ", ReapplyMapLight=" + CfgReapplyMapLight.Value
 + "（默认关：开着会覆盖太阳光→阴影错位/色变质变）");
#if !NEWFIX
 FixLog.Key("按键: 诊断导出=" + CfgDiagnosticKey.Value
 + ", 强制关轴=" + CfgForceHideAxisKey.Value
 + ", 拉回UI=" + CfgPullUIToFaceKey.Value);
#else
 FixLog.Key("按键: 无（NEWFIX 已删除 F6/F7/F8 三个快捷键）");
#endif
#endif
 }
 catch { }
 }
 }
}
