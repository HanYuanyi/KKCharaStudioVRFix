using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using VRGIN.Core;
using VRGIN.Modes;

namespace KKCharaStudioVRFix
{
 /// <summary>
 /// 
 /// 
 ///
 /// ───────────────── 真凶（反编译 VRGIN，已逐行核实） ─────────────────
 ///
 /// 0.0.3 每次加载场景卡 → LoadFixHook.LoadScenePreHook → ForceResetVRMode()
 /// → ForceResetAsStandingMode() → VR.Manager.SetMode&lt;GenericStandingMode&gt;()
 ///
 /// 而 VRGIN 新建出来的 standing mode 的 OnStart() 里有一句写死的：
 ///
 /// // VRGIN.Modes.StandingMode
 /// protected override void OnStart()
 /// {
 /// base.OnStart();
 /// VR.Camera.SteamCam.origin.position = Vector3.zero; // ← 摄像机被拉回原点
 /// VR.Camera.SteamCam.origin.rotation = Quaternion.identity; // ← 朝向被拉平
 /// }
 ///
 /// 这就是「加载完场景卡后镜头回到一个位置」的原因。
 ///
 /// ───────────────── 为什么反而把问题搞得更糟 ─────────────────
 /// 用了个"连续 120 帧把记住的原点写回去"的看门狗。两个致命缺陷：
 /// ① 它把 VRGIN 正常状态下的 (0,0,0) 当成"健康位置"，重置后"恢复"了个零；
 /// ② 它分不清"游戏重置"和"玩家自己动的" —— 玩家按三键摆正也会被判成"重置"
 /// → 插件把刚摆正的姿态又掰回去。
 ///
 /// ───────────────── 的做法：一次性、不抢、不打架 ─────────────────
 /// 在 `StandingMode.OnStart` 上挂 Harmony **postfix**，
 /// 在原方法体（含那句归零）之后把记住的姿态一次写回，压过归零。
 /// 姿态记忆只在"原点明显偏离默认姿态"时才记 ——
 /// (0,0,0)/(identity) 永远不会被记成"用户姿态"，就不会再出现"回写零"。
 ///
 /// ───────────────── ：姿态记忆为什么还是会被污染 ─────────────────
 /// 的 `Tick()` 是"只要这一帧的值偏离默认就立刻记"。问题是模式重建的
 /// **中间过程**会短暂产生偏离默认的垃圾值。日志铁证：
 ///
 /// 02:23:12 用户按 F7，此时原点 = pos=(2.29, 1.29, 4.76), tilt=(x=3.99, z=24.03)
 /// 02:23:14 StandingMode 重建 → 插件"恢复"成了 pos=(0.06, -0.03, 1.01) ← 完全错误！
 ///
 /// 说明在 02:23:12~14 这段"正在加载 / 模式正在销毁重建"的空窗里，
 /// `Tick()` 从**半销毁状态**的 origin 上读到了 `(0.06,-0.03,1.01)` 这种垃圾值，
 /// 因为它偏离默认（> 0.35m）就被当成"玩家姿态"覆盖了真值。
 ///
 /// 的加固（三重门）：
 /// ① **加载中 / 模式为 null 时不记**（原有的 `IsGameLoading()` + `GetMode()==null` 判断）；
 /// ② **稳定帧计数**：同一个姿态连续 `StableFramesNeeded` 帧完全一致才接受 ——
 /// 半销毁状态下的垃圾值活不过 5 帧；
 /// ③ **重置拒绝窗口**：`StandingMode.OnStart` 执行后的 `RejectFramesAfterReset` 帧内
 /// 一概不记（那段正是 origin 被归零/半初始化的时间）。
 /// </summary>
 internal static class OriginGuardFix
 {
 /// <summary>位置偏离超过这个距离才认为"原点被玩家动过"。</summary>
 private const float PoseThresholdPos = 0.35f;

 /// <summary>俯仰/翻滚超过这个角度才认为"原点被玩家动过"。</summary>
 private const float PoseThresholdTilt = 3.0f;

 /// <summary>
 /// 同一个姿态必须连续这么多帧"几乎不变"才被接受为玩家姿态。
 /// 半销毁/半初始化状态下读到的垃圾值撑不过这么多帧。
 /// </summary>
 private const int StableFramesNeeded = 8;

 /// <summary>
 /// StandingMode.OnStart 执行后这么多帧内一概不记姿态
 /// （那段正在归零/重建，读到什么都不可信）。
 /// </summary>
 private const int RejectFramesAfterReset = 15;

 // ---------------- 记住的"玩家姿态" ----------------
 private static bool _hasPose;
 private static Vector3 _posePos;
 private static Quaternion _poseRot;

 // ---------------- 加载前位姿（允许默认姿态）----------------

 /// <summary>
 /// 加载开始瞬间的 origin 位姿 —— **无条件记录，默认姿态也记**。
 ///
 /// ── 为什么需要它──
 /// 的 `RestoreOriginAfterInitScene()` 第一句是 `if (!_hasPose) return;`，
 /// 而 `_hasPose` 只由「明显偏离默认姿态」的路径写入：
 /// · `Tick()` 要求 `posMoved || tilted`；
 /// · `SnapshotNow()` 同样要求，否则打日志「加载开始时原点为默认姿态，不记」。
 ///
 /// 日志铁证：两次加载的起始快照都是
 /// `已快照 VR 原点: pos=(0.00, 0.00, 0.00), rot=(0.00, 0.00, 0.00)`
 /// `VR 原点现状…最近: 加载开始时原点为默认姿态，不记`
 /// → `_hasPose` 始终为假 → 恢复函数**静默返回**，等于没写。
 ///
 /// 但"默认姿态"**恰恰也是需要恢复的**！因为 StandingMode.OnStart() 会把
 /// origin 归零到 (0,0,0)/identity —— 如果加载前它本来就是零，那"恢复成零"
 /// 正好抵消这次归零造成的**跳变**（归零 → 再归零 = 视觉上不动）。
 ///
 /// 语义区别（两者都必须保留）：
 /// · `_hasPose` = "玩家摆过一个值得记住的站姿"（用于 **StandingModeOnStartPostfix**
 /// 的**地图加载**分支，只在真有玩家姿态时才写回）；
 /// · `_hasLoadPose` = "加载开始那一刻 origin 在哪"（用于**抵消归零**，
 /// 不管它是不是默认姿态）。
 /// </summary>
 private static bool _hasLoadPose;
 private static Vector3 _loadPosePos;
 private static Quaternion _loadPoseRot;

 /// <summary>上次捕获「加载前位姿」的时间戳（秒）。用于超时重置。</summary>
 private static float _loadPoseTime = -999f;

 /// <summary>
 /// 超过这个秒数没有新捕获，就认为"上一次加载已彻底结束"，
 /// 允许下一次捕获无条件重置（不依赖收口里的 `ClearLoadPose()`）。
 ///
 /// 为什么需要：日志显示「加载开始」出现 12 次，但收口（诊断导出）只跑了 8 次 ——
 /// 有 4 次加载没走到收口，`_hasLoadPose` 就会残留上一次的值。
 /// 那会让"已有值就不覆盖"（非权威路径）保住**上一次**的位姿，比清掉更糟。
 /// </summary>
 private const float LoadPoseStaleSeconds = 20f;

 private static int _applyCount;
 private static int _rememberCount;
 private static string _lastEvent = "";

 // ---------------- 稳定帧机制 ----------------

 /// <summary>候选姿态（正在累计"稳定帧"的那一个）。</summary>
 private static bool _hasCandidate;
 private static Vector3 _candPos;
 private static Quaternion _candRot;
 private static int _candFrames;

 /// <summary>StandingMode 重建后的"拒绝记姿态"倒计时（帧）。</summary>
 private static int _rejectFrames;

 // ---------------- 安装 ----------------

 internal static void Install(HarmonyLib.Harmony harmony)
 {
 try
 {
 MethodInfo target = AccessTools.Method(
 typeof(VRGIN.Modes.StandingMode), "OnStart", Type.EmptyTypes);
 if (target == null)
 {
 FixLog.Warn("找不到 VRGIN.Modes.StandingMode.OnStart，原点姿态恢复不可用。");
 return;
 }

 harmony.Patch(target,
 postfix: new HarmonyMethod(AccessTools.Method(
 typeof(OriginGuardFix), "StandingModeOnStartPostfix")));

 // 同时挂 **prefix** —— 在 VRGIN 归零**之前**直接存一份 origin。
 // 这是最可靠的"归零前位姿"来源：它不受任何外部钩子时序影响，
 // 就在归零的同一个函数前后取值。
 harmony.Patch(target,
 prefix: new HarmonyMethod(AccessTools.Method(
 typeof(OriginGuardFix), "StandingModeOnStartPrefix")));

 FixLog.Info("已挂载钩子: VRGIN.Modes.StandingMode.OnStart [原点姿态恢复]");
 }
 catch (Exception e)
 {
 FixLog.Warn("挂载 StandingMode.OnStart 钩子失败: " + e.Message);
 }
 }

 // ---------------- 归零前位姿（最可靠来源）----------------

 /// <summary>
 /// `StandingMode.OnStart` 的 **prefix** 里存下的"归零前 origin"。
 ///
 /// 为什么比 `_loadPose*` 更可靠：`_loadPose*` 依赖"某个外部钩子在归零前触发"，
 /// 而实测证明外部钩子的时序不可控（`AddMap` 链路的钩子跑在归零之后，
 /// 会把零值写进去 → 失败）。
 /// 本组字段在**归零的同一函数、紧邻其前**取值，不可能被污染。
 /// </summary>
 private static bool _hasPreReset;
 private static Vector3 _preResetPos;
 private static Quaternion _preResetRot;

 /// <summary>
 /// "用户点加载按钮那一刻"的 origin —— 抵消归零的**首选目标**。
 ///
 /// 与 `_loadPose*` 同源（都在 `TransitionPrefixSceneCard` 捕获），
 /// 但独立命名便于诊断时一眼看清"哪个值才是用户站姿"。
 /// 由 `CapturePoseAtLoadEntry()` 写入，`ClearPoseAtLoadEntry()` 清除。
 /// </summary>
 private static bool _hasPoseAtLoadEntry;
 private static Vector3 _poseAtLoadEntryPos;
 private static Quaternion _poseAtLoadEntryRot;

 /// <summary>前缀：把 origin 归零前的值存下来。</summary>
 public static void StandingModeOnStartPrefix()
 {
 try
 {
 Transform origin = VRRef.GetVROrigin();
 if (origin == null) { _hasPreReset = false; return; }

 _preResetPos = origin.position;
 _preResetRot = origin.rotation;
 _hasPreReset = true;
 }
 catch (Exception e)
 {
 _hasPreReset = false;
 FixLog.Debug("StandingModeOnStartPrefix 异常: " + e.Message);
 }
 }

 /// <summary>
 /// 在**场景卡加载入口**（`TransitionPrefixSceneCard`）记录 origin。
 ///
 /// 这是"用户点加载按钮那一刻"的值，**早于 `InitScene`、早于 `StandingMode` 重建**，
 /// 因此是抵消归零最正确的目标（见 `UndoStandingModeReset` 的注释）。
 /// </summary>
 internal static void CapturePoseAtLoadEntry()
 {
 try
 {
 Transform origin = VRRef.GetVROrigin();
 if (origin == null) { _hasPoseAtLoadEntry = false; return; }

 _poseAtLoadEntryPos = origin.position;
 _poseAtLoadEntryRot = origin.rotation;
 _hasPoseAtLoadEntry = true;

 Vector3 e = _poseAtLoadEntryRot.eulerAngles;
 FixLog.Debug("已记录加载入口原点: pos=" + _poseAtLoadEntryPos.ToString("0.00")
 + ", rot=(x=" + Normalize(e.x).ToString("0.00")
 + ", y=" + Normalize(e.y).ToString("0.00")
 + ", z=" + Normalize(e.z).ToString("0.00") + ")");
 }
 catch (Exception e)
 {
 _hasPoseAtLoadEntry = false;
 FixLog.Debug("CapturePoseAtLoadEntry 异常: " + e.Message);
 }
 }

 /// <summary>
 /// StandingMode.OnStart 的 postfix —— 在它把 origin 归零**之后**执行。
 ///
 /// 这里**只负责"记住加载前站姿"的时间点**，
 /// **不再无条件写回**。原因见下面 "为什么要区分场景卡/地图加载"。
 /// </summary>
 public static void StandingModeOnStartPostfix()
 {
 try
 {
 // 【2026-10-22】模式重建 = 控制器与拖动工具会被重建 →
 // 让 DragStateFix 在下一帧做一次状态对账（恢复"解锁拖动世界"与工具序号）。
 // 这是事件，不是轮询；同时覆盖"工具 Awake 钩子没装上"的兜底。
 try { DragStateFix.ArmRestore(); } catch { }

 // 进入"拒绝记姿态"窗口 —— 下面这 15 帧里 origin 正在被
 // VRGIN 重建，读到的值一律不算数。
 _rejectFrames = RejectFramesAfterReset;
 // 同时把"候选"清掉，避免旧的候选帧数被延续
 _hasCandidate = false;
 _candFrames = 0;

 Transform origin = VRRef.GetVROrigin();
 if (origin == null)
 {
#if !LEAN
 _lastEvent = "postfix: origin 为 null";
#endif
 return;
 }

 // ═══════════════════════════════════════════════════════════
 // 区分「场景卡加载」与「地图加载」
 //
 // · 加载**场景卡**：场景卡里自带相机数据 studio.cameraCtrl，
 // 原版 Studio 会把镜头摆到存档里的位置。~ 的做法是
 // 这里**直接 return，什么都不做** —— 那是错的，因为
 // `StandingMode.OnStart()` 刚刚把 origin 归零到 (0,0,0)/identity
 // （见本类顶部注释），这一归零**无人对抗**就成了用户看到的
 // 「镜头被甩到错误位置、手柄消失」。
 //
 // 先**抵消归零**（恢复加载前位姿），
 // 再让原版 `MoveToCurrent` 摆到场景卡机位 —— 两者不冲突：
 // · 抵消归零：让"归零"这一跳不进入渲染帧；
 // · MoveToCurrent：在抵消后的位姿基础上摆到存档机位。
 // 注意这里**不是**写回 `_posePos`（玩家旧站姿），而是写回
 // `_loadPosePos`（加载前那一刻的原点），语义上只做"抵消"。
 //
 // · 加载**地图**：地图不带相机数据，原版也不会摆镜头，
 // 这时才需要写回玩家姿态 `_posePos` 兜底。
 //
 // 判据：`VRRef.IsSceneCardLoadSticky()` —— **粘性、只读、不消费**
 // （见 VRRef 里 的注释：/15 就是栽在消费型标记上）。
 // ═══════════════════════════════════════════════════════════
 bool sceneCard = VRRef.IsSceneCardLoadSticky();

 if (sceneCard)
 {
 // 抵消本次 StandingMode 归零。
 UndoStandingModeReset("场景卡加载");

#if !LEAN
 _lastEvent = "场景卡加载：已抵消 StandingMode 归零，交原版摆机位";
#endif
 FixLog.Key("StandingMode 重建（场景卡加载）：已**抵消归零**（回到加载前位姿）"
 + "—— 之后由原版 MoveToCurrent 摆到场景卡机位（修「跳2 镜头被甩到错误位置」）。");
 return;
 }

 if (!_hasPose)
 {
 // 还没记到任何"玩家姿态"（例如第一次进 VR，本来就在默认姿态）
 // → 保持 StandingMode 归零后的默认状态即可，不动它。
 FixLog.Info("StandingMode 重建：尚未记录玩家姿态，保持默认原点 (0,0,0)。");
 return;
 }

 origin.position = _posePos;
 origin.rotation = _poseRot;

 _applyCount++;
#if !LEAN
 _lastEvent = "已恢复玩家姿态（非场景卡加载）";
#endif
 FixLog.Key("StandingMode 重建（地图加载）→ 已恢复加载前记住的原点姿态 pos="
 + _posePos.ToString("0.00") + "（修「镜头回到固定位置」，第 "
 + _applyCount + " 次）。");
 }
 catch (Exception e)
 {
 FixLog.Debug("StandingModeOnStartPostfix 异常: " + e.Message);
 }
 }

 /// <summary>
 /// 每帧调用（从看门狗 LateUpdate）。只做一件事：记录"玩家姿态"。
 /// **绝不写原点** —— 写入只发生在 StandingModeOnStartPostfix 里。
 /// </summary>
 internal static void Tick()
 {
 try
 {
 // 拒绝窗口（模式刚重建）
 if (_rejectFrames > 0) { _rejectFrames--; return; }

 if (VRRef.IsGameLoading()) return;

 Transform origin = VRRef.GetVROrigin();
 if (origin == null) return;

 ControlMode mode = GetMode();
 if (mode == null) return; // 模式重建期间不记，避免记到中间态

 Vector3 pos = origin.position;
 Quaternion rot = origin.rotation;
 Vector3 e = rot.eulerAngles;
 float dx = Normalize(e.x);
 float dz = Normalize(e.z);

 bool posMoved = pos.magnitude > PoseThresholdPos;
 bool tilted = Mathf.Abs(dx) > PoseThresholdTilt || Mathf.Abs(dz) > PoseThresholdTilt;

 // 只有"明显偏离默认姿态"才认为这是玩家手动摆出来的状态，才值得记住。
 // 这样 (0,0,0)/(identity) 永远不会被误记成"玩家姿态"。
 if (!posMoved && !tilted)
 {
 _hasCandidate = false;
 _candFrames = 0;
 return;
 }

 // ---------- ：稳定帧累计 ----------
 bool sameAsCandidate = _hasCandidate
 && (pos - _candPos).sqrMagnitude < 0.0004f // < 2cm
 && Quaternion.Angle(_candRot, rot) < 0.5f;

 if (!sameAsCandidate)
 {
 _hasCandidate = true;
 _candPos = pos;
 _candRot = rot;
 _candFrames = 1;
 return; // 本帧只开始累计，不写记忆
 }

 if (_candFrames < StableFramesNeeded)
 {
 _candFrames++;
 return; // 还没稳定够，继续累计
 }

 // ---------- 稳定够久 → 才真正写入记忆 ----------
 if (_hasPose)
 {
 bool samePos = (pos - _posePos).sqrMagnitude < 0.0004f;
 bool sameRot = Quaternion.Angle(_poseRot, rot) < 0.5f;
 if (samePos && sameRot) return; // 与已记住的一致，不用刷
 }

 _posePos = pos;
 _poseRot = rot;
 _hasPose = true;
 _rememberCount++;

 if (_rememberCount <= 3 || _rememberCount % 50 == 0)
 {
 FixLog.Info("已记住玩家原点姿态: pos=" + pos.ToString("0.00")
 + ", tilt=(x=" + dx.ToString("0.00")
 + ", z=" + dz.ToString("0.00") + ")（第 " + _rememberCount
 + " 次刷新，已稳定 " + _candFrames + " 帧）");
 }
 }
 catch (Exception e)
 {
 FixLog.Debug("OriginGuardFix.Tick 异常: " + e.Message);
 }
 }

 /// <summary>加载开始时调用：把当前姿态立刻钉一份（防止加载过程中被改）。</summary>
 internal static void SnapshotNow(string reason)
 {
 try
 {
 Transform origin = VRRef.GetVROrigin();
 if (origin == null) return;

 Vector3 pos = origin.position;
 Quaternion rot = origin.rotation;
 Vector3 e = rot.eulerAngles;
 float dx = Normalize(e.x);
 float dz = Normalize(e.z);

 bool posMoved = pos.magnitude > PoseThresholdPos;
 bool tilted = Mathf.Abs(dx) > PoseThresholdTilt || Mathf.Abs(dz) > PoseThresholdTilt;

 if (!posMoved && !tilted)
 {
#if !LEAN
 _lastEvent = "加载开始时原点为默认姿态，不记";
#endif
 return;
 }

 _posePos = pos;
 _poseRot = rot;
 _hasPose = true;
#if !LEAN
 _lastEvent = "加载开始时已钉住姿态(" + reason + ")";
#endif

 FixLog.Info("加载开始：已钉住玩家原点姿态 pos=" + pos.ToString("0.00")
 + (reason.Length > 0 ? " [" + reason + "]" : ""));
 }
 catch (Exception e)
 {
 FixLog.Debug("OriginGuardFix.SnapshotNow 异常: " + e.Message);
 }
 }

 // ------------------------------------------------------------------
 // 加载前位姿：无条件快照 + 抵消 StandingMode 归零
 // ------------------------------------------------------------------

 /// <summary>
 /// 记下"加载前那一刻 origin 在哪"。
 ///
 /// ── 的致命缺陷（本轮日志铁证）──
 /// 的实现是**无条件覆盖**，但 `TransitionPrefix` 被**多个钩子**共用
 /// （`LoadScene` / `LoadSceneCoroutine` / `ImportScene` / `AddMap` /
 /// `UnloadBaseScene` / `LoadMapCoroutine`），而它们的执行时机横跨整个加载过程。
 ///
 /// 第二次场景卡加载的实测时序（`08:30:30` → `08:30:36`）：
 /// 30.816 捕获 pos=(2.92, 1.13, 4.68) ← 用户点按钮那一刻 ✅
 /// 30.909 捕获 pos=(2.92, 1.13, 4.68) ← 同一加载的第二个钩子，还是对的
 /// 33.342 捕获 pos=(0.00, 0.00, 0.00) ← ★ origin 已被 StandingMode 归零！
 /// 36.867 抵消：(0,0,0) → (0,0,0) ← 白干
 ///
 /// 于是「加载前位姿」被**后到的零值**覆盖，抵消变成了原地踏步。
 /// 第一次加载为什么"看起来没事"？因为它开始时原点本来就是 (0,0,0)，
 /// 三次捕获都是零，覆盖无影响 —— **这正是用户"第一次正常、第二三次还那样"的原因**。
 ///
 /// ── 修法：分级写入 + 零值拒绝 ──
 /// · `authoritative = true`（来自场景卡/地图加载入口的 prefix，即用户动作那一刻）
 /// → **允许覆盖**，但**拒绝零值**（加载入口的 prefix 一定发生在任何归零之前，
 /// 此时读到零说明这个 origin 本来就在原点，那记零也是对的 —— 所以只拒绝
 /// "已有非零值却被零覆盖"这一种情况）。
 /// · `authoritative = false`（来自 `AddMap` / `UnloadBaseScene` / `LoadMapCoroutine`
 /// 这些**加载过程中**的钩子）→ **已有值就不写**，绝不覆盖。
 ///
 /// 另外，`ClearLoadPose()` 在每次加载完整结束时清掉快照，保证下一次加载能重新捕获。
 /// </summary>
 internal static void CaptureLoadPose(string reason, bool authoritative = true)
 {
 try
 {
 Transform origin = VRRef.GetVROrigin();
 if (origin == null) { _hasLoadPose = false; return; }

 Vector3 pos = origin.position;
 Quaternion rot = origin.rotation;

 // 超时重置：距上次捕获太久 → 视为"新的一次加载"，先把旧值清掉。
 // 这样即使上一次加载没走到收口的 ClearLoadPose()，也不会用到过期的位姿。
 if (_hasLoadPose && (Time.realtimeSinceStartup - _loadPoseTime) > LoadPoseStaleSeconds)
 {
 FixLog.Debug("加载前位姿已过期（"
 + (Time.realtimeSinceStartup - _loadPoseTime).ToString("0.0")
 + " 秒未更新）→ 重置后重新捕获。");
 _hasLoadPose = false;
 }

 // 判定"这是不是一个可疑的归零值"
 bool looksZeroed = pos.sqrMagnitude < 0.0001f
 && Quaternion.Angle(rot, Quaternion.identity) < 0.5f;

 if (_hasLoadPose)
 {
 // ① 非权威调用：一律不覆盖（加载过程中的钩子可能读到位姿已被改坏的值）
 if (!authoritative)
 {
 FixLog.Debug("加载前位姿已存在，忽略非权威捕获(" + reason + ")。");
 return;
 }

 // ② 权威调用，但读到的是可疑零值，而已记的是非零值 → 拒绝覆盖
 // （说明这次"加载入口"其实是在归零之后才触发的，不是用户动作那一刻）
 // ★ 这就是 失败的根因：第二次加载的 3 次捕获里，
 // 第 3 次读到 (0,0,0) 把前面正确的 (2.92,1.13,4.68) 覆盖了。
 if (looksZeroed && _loadPosePos.sqrMagnitude >= 0.0001f)
 {
 FixLog.Debug("拒绝用可疑零值覆盖已记的加载前位姿(" + reason
 + ")：已记 pos=" + _loadPosePos.ToString("0.00")
 + "，本次读到 pos=" + pos.ToString("0.00"));
 return;
 }
 }

 // ③ 首次捕获，或权威调用的合理更新
 _loadPosePos = pos;
 _loadPoseRot = rot;
 _hasLoadPose = true;
 _loadPoseTime = Time.realtimeSinceStartup;

 Vector3 e = _loadPoseRot.eulerAngles;
 FixLog.Debug("加载前位姿已捕获(" + reason + (authoritative ? "" : "·非权威")
 + "): pos=" + _loadPosePos.ToString("0.00") + ", rot=(x="
 + Normalize(e.x).ToString("0.00") + ", y=" + Normalize(e.y).ToString("0.00")
 + ", z=" + Normalize(e.z).ToString("0.00") + ")");
 }
 catch (Exception e)
 {
 FixLog.Debug("CaptureLoadPose 异常: " + e.Message);
 }
 }

 /// <summary>清掉「加载前位姿」快照（加载完整结束后调用，避免泄漏到下一次）。</summary>
 internal static void ClearLoadPose()
 {
 _hasLoadPose = false;
 _loadPoseTime = -999f; // 时间戳一并清掉
 _hasPoseAtLoadEntry = false; // 加载入口原点同步清掉
 }

 /// <summary>
 /// 把 origin 从"被 `InitScene` 推歪的状态"
 /// **恢复成用户加载前的真实站姿** —— 用来**抵消**
 /// `VRGIN.Modes.StandingMode.OnStart()` 里那两句写死的归零。
 ///
 /// ── 为什么要抵消 ──
 /// `StandingMode.OnStart()`（反编译 VRGIN_KKCS.dll 确认）：
 /// base.OnStart();
 /// VR.Camera.SteamCam.origin.position = Vector3.zero; // ← 拽到世界原点
 /// VR.Camera.SteamCam.origin.rotation = Quaternion.identity; // ← 朝向被抹平
 ///
 /// 0.0.3 在**点击场景卡加载按钮**时会走 `LoadFixHook.LoadScenePreHook`
 /// → `ForceResetVRMode()` → 5 帧后若 mode 不是 GenericStandingMode 就
 /// `ForceResetAsStandingMode()` → `VR.Manager.SetMode&lt;GenericStandingMode&gt;()`
 /// → 新建的 mode 执行 `OnStart()` → **归零**。
 ///
 /// ── 为什么 用了 prefix 快照还是不对 ──
 /// 用户本轮关键情报：「**加载带地图的场景卡就会跳；不加载带地图的就不会**」。
 ///
 /// 反编译 `Studio.Map.LoadMapCoroutine`（Studio.Map.cs 68–101 行）：
 /// else if (no != _no) { // ★ 只有地图号变了才走
 /// isLoading = true; no = _no;
 /// Scene.Instance.LoadBaseScene(...); // 卸载旧 Unity 场景
 /// yield return new WaitWhile(() => isLoading);
 /// }
 /// 换地图 → `Manager.Scene.UnloadBaseSceneCoroutine` 里 `ActiveScene = scene`
 /// （Manager.Scene.cs 519 行）→ 触发 `activeSceneChanged`
 /// → `Studio.Map.Reserve()` → `isLoading = false`。
 ///
 /// **这一整条"卸场景→重建"的链是原版固有的**，用户自己也说"就是原版那几跳"。
 /// 我们只修其中 0.0.3 引入的那部分：`StandingMode.OnStart()` 的归零。
 ///
 /// ── 目标值为什么必须换 ──
 /// 用 `StandingMode.OnStart` 的 prefix 取 `_preReset*`，看似最可靠，
 /// 但**在 `StandingMode` 重建之前，`InitScene` 早就动过 origin 了**：
 ///
 /// LoadSceneCoroutine(path)
 /// InitScene(_close:false) ← ★ 先跑
 /// ReleaseMap(); // no = -1 + UnloadBaseScene()
 /// cameraCtrl.Reset(0); // cameraData.Copy(cameraReset)
 /// AddMapCoroutine → LoadBaseScene → activeSceneChanged → Reserve
 /// ...
 /// ForceResetVRMode 的 5 帧 → StandingMode 重建 → 归零 ← 晚于 InitScene
 ///
 /// 日志铁证：
 /// 加载前(用户) = (2.91, 1.10, 4.88)
 /// 归零前快照 = (-0.44, 0.12, 1.14) ← ★ 已被 InitScene 推歪
 /// 抵消去 `(-0.44,0.12,1.14)` → 落点错 → 用户仍看到"被甩到错误位置"。
 ///
 /// **正确目标 = 加载入口那一刻（用户点按钮时）的 origin**，见 `_poseAtLoadEntry*`。
 /// </summary>
 internal static void UndoStandingModeReset(string reason)
 {
 try
 {
 Transform origin = VRRef.GetVROrigin();
 if (origin == null) return;

 // 优先级调整：
 // ① `_poseAtLoadEntry` —— 用户点按钮那一刻 ★ 最正确
 // ② `_loadPose` —— 同一来源的冗余备份
 // ③ `_preReset` —— 归零前一刻，但可能已被 InitScene 推歪（末选）
 Vector3 targetPos;
 Quaternion targetRot;
 string source;

 if (_hasPoseAtLoadEntry && _poseAtLoadEntryPos.sqrMagnitude >= 0.0001f)
 {
 targetPos = _poseAtLoadEntryPos;
 targetRot = _poseAtLoadEntryRot;
 source = "加载入口原点";
 }
 else if (_hasLoadPose && _loadPosePos.sqrMagnitude >= 0.0001f)
 {
 targetPos = _loadPosePos;
 targetRot = _loadPoseRot;
 source = "加载前位姿";
 }
 else if (_hasPreReset && _preResetPos.sqrMagnitude >= 0.0001f)
 {
 targetPos = _preResetPos;
 targetRot = _preResetRot;
 source = "prefix 快照(末选)";
 }
 else
 {
 FixLog.Warn("StandingMode 归零抵消跳过（" + reason
 + "）：无可用目标（加载入口 / 加载前位姿 / prefix 快照 均为空或零）。");
 return;
 }

 Vector3 before = origin.position;
 origin.position = targetPos;
 origin.rotation = targetRot;

 FixLog.Key("StandingMode 归零已被抵消（" + reason + "·" + source + "）：origin "
 + before.ToString("0.00") + " → " + targetPos.ToString("0.00")
 + "，修「跳2 镜头被甩到错误位置、手柄消失」。");
 }
 catch (Exception e)
 {
 FixLog.Debug("UndoStandingModeReset 异常: " + e.Message);
 }
 }

 /// <summary>
 /// `Studio.Studio.InitScene` 执行完之后调用 —— 抵消
 /// `cameraCtrl.Reset(0)` 造成的位移（画面"跳2"的另一半）。
 ///
 /// `InitScene` 里两句（反编译 Studio.Studio.cs 628/635 行）：
 /// Singleton&lt;Map&gt;.Instance.ReleaseMap(); // 卸地图 → 蓝色（跳1，原版固有，保留）
 /// cameraCtrl.Reset(0); // cameraData.Copy(cameraReset)（跳2）
 ///
 /// `cameraReset` **不是出厂默认值**，而是"上一次
 /// `TargetSet` / `FrontTarget` / `SetCamera` 快照下来的相机位姿"
 /// （Studio.CameraControl.cs 701 / 731 / 743 行）。
 /// 在 VR 里 studio 相机变化会带着 vROrigin 一起跑 → 又是一次可见跳变。
 ///
 /// 这里把 origin 拉回加载前位姿，只是**不让这次 Reset 的位移进入渲染帧**；
 /// 原版流程照常走 `AddMap` → `Import`，随后由 `MoveToCurrent` 摆到场景卡机位。
 ///
 /// ── 与 的区别 ──
 /// 用 `_hasPose` 把关（只在"玩家摆过站姿"时才恢复）→ 首次/默认姿态下静默返回。
 /// 改用 `_hasLoadPose`（无条件快照）→ 任何情况下都有值可恢复。
 /// </summary>
 internal static void RestoreOriginAfterInitScene()
 {
 try
 {
 if (!_hasLoadPose) return;

 Transform origin = VRRef.GetVROrigin();
 if (origin == null) return;

 // 注意：`InitScene` 是**加载的第一步**（早于 StandingMode 重建），
 // 所以这里用加载入口捕获的 `_loadPose*` 是恰当的。
 // 若它本身是零（加载前玩家就在原点），抵消成零也是正确行为，不告警。
 Vector3 before = origin.position;
 origin.position = _loadPosePos;
 origin.rotation = _loadPoseRot;

 FixLog.Key("InitScene 后已把原点拉回加载前位姿（抵消 cameraCtrl.Reset(0) 的位移）："
 + before.ToString("0.00") + " → " + _loadPosePos.ToString("0.00")
 + "，修「跳1 之后镜头被甩到错误位置、手柄消失」。");
 }
 catch (Exception e)
 {
 FixLog.Debug("RestoreOriginAfterInitScene 异常: " + e.Message);
 }
 }

 private static ControlMode GetMode()
 {
 try
 {
 VRManager mgr = VR.Manager;
 if (mgr == null) return null;
 return mgr.Mode;
 }
 catch { return null; }
 }

 private static float Normalize(float angle)
 {
 angle %= 360f;
 if (angle > 180f) angle -= 360f;
 if (angle <= -180f) angle += 360f;
 return angle;
 }

 internal static string Describe()
 {
 string pose = _hasPose
 ? "pos=" + _posePos.ToString("0.00")
 + ", tilt=(x=" + Normalize(_poseRot.eulerAngles.x).ToString("0.00")
 + ", z=" + Normalize(_poseRot.eulerAngles.z).ToString("0.00") + ")"
 : "未记录（当前为默认姿态）";

 string cand = _hasCandidate
 ? "，候选 pos=" + _candPos.ToString("0.00")
 + "（已稳定 " + _candFrames + "/" + StableFramesNeeded + " 帧）"
 : "";

 return "玩家姿态: " + pose
 + (_hasLoadPose ? "；加载前位姿: pos=" + _loadPosePos.ToString("0.00") : "")
 + (_hasPoseAtLoadEntry ? "；加载入口原点: pos=" + _poseAtLoadEntryPos.ToString("0.00") : "")
 + (_hasPreReset ? "；归零前快照: pos=" + _preResetPos.ToString("0.00") : "")
 + "；已恢复 " + _applyCount + " 次"
 + "；已刷新记忆 " + _rememberCount + " 次"
 + (_rejectFrames > 0 ? "；★模式刚重建，拒绝记姿态窗口剩余 " + _rejectFrames + " 帧" : "")
 + cand
 + (_lastEvent.Length > 0 ? "；最近: " + _lastEvent : "");
 }
 }
}
