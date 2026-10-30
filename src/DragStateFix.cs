using System;
using System.Reflection;
using UnityEngine;
using VRGIN.Controls;

namespace KKCharaStudioVRFix
{
 /// <summary>
 /// 
 /// 
 /// 
 /// 
 /// 
 ///
 /// ───────────────── 问题本质 ─────────────────
 /// 0.0.3 每次加载场景卡都会重建 VR 模式：
 /// LoadFixHook.LoadScenePreHook → ForceResetVRMode()
 /// → ForceResetAsStandingMode() → VR.Manager.SetMode&lt;GenericStandingMode&gt;()
 /// → DestroyImmediate(旧 Mode) → ControlMode.OnDestroy() 销毁 ControllerManager/Left/Right
 /// → 新 Mode 的 CreateControllers() 用 AddComponent 造出全新的 Controller 与 Tool。
 /// 结果：
 /// ① GripMoveKKCharaStudioTool.lockRotXZ 回到默认 true →「解锁的世界被重置 / 只能水平移动」
 /// ② Controller.ToolIndex 回到 0 → ActiveTool = MenuTool →「手柄图标变回齿轮」
 ///
 /// ───────────────── ：根据实机日志改为左右手独立记忆 ─────────────────
 /// 日志证据：同一时刻 Right Controller 的 lockRotXZ=true（锁定），而 Left Controller
 /// lockRotXZ=false（解锁）；旧版只跟踪右手，因而每次 Mode/工具重建都按"右手锁定"
 /// 恢复，左手实际解锁状态被丢掉。`lockRotXZ` 反编译确认是**实例字段**，可以安全
 /// 分手记忆，不会共享/互相覆盖。
 ///
 /// 两只手分别按其 Controller 引用锁定工具实例；两边各自记录 lockRotXZ 与 ToolIndex，
 /// 重建后独立恢复。未能识别左右时不猜、不把状态跨手复制。
 ///
 /// 另外：**只写 lockRotXZ 字段，不调 ResetRotation()** —— 避免改原点旋转把眼前的大 UI 甩走。
 /// </summary>
 internal static class DragStateFix
 {
 private const string ToolTypeName = "KKCharaStudioVR.GripMoveKKCharaStudioTool";
 private const string LockFieldName = "lockRotXZ";

 private static Type _toolType;
 private static FieldInfo _lockField;

 // ---------------- 记忆（右手和左手各自独立；lockRotXZ 是实例字段） ----------------

 /// <summary>右手的解锁状态。null = 还没观测到过。</summary>
 private static bool? _rememberedUnlocked;
 /// <summary>左手的解锁状态。：日志证实用户操作的是左手，不能只记右手。</summary>
 private static bool? _rememberedLeftUnlocked;

 /// <summary>右手最后一次选择的工具序号。</summary>
 private static int _rememberedToolIndex = -1;
 /// <summary>左手最后一次选择的工具序号。</summary>
 private static int _rememberedLeftToolIndex = -1;

 /// <summary>上一次观测到的右手目标工具实例 ID（0 = 还没见过）。</summary>
 private static int _lastToolId;
 /// <summary>上一次观测到的左手目标工具实例 ID。</summary>
 private static int _lastLeftToolId;

 private static int _restoreCount;
 private static int _leftRestoreCount;
 private static int _iconRestoreCount;
 private static int _leftIconRestoreCount;
 private static int _observedFrames;

 /// <summary>工具重建后静默 120 帧：该手不更新记忆，避免新实例默认值污染。</summary>
 private static int _quietFramesAfterRebuild;
 private static int _quietLeftFramesAfterRebuild;

 private static string _lastError = "";

 // ---------------- 工具缓存（性能关键） ----------------
 //
 // 旧版 Tick() 每帧都跑 Resources.FindObjectsOfTypeAll(_toolType)，
 // 这是**引擎级全表扫描**：要遍历内存里所有 UnityEngine.Object，
 // 还会分配一个 Object[] 数组。VR 里一帧 60~90 次，GC 压力极大
 // → 实测把 55 帧拉到 30 帧，是本次掉帧的头号元凶。
 //
 // 现在的策略：
 // · 平时**复用**上一次解析到的目标工具（零查找、零分配）；
 // · 只在「缓存失效」时才重新扫描：
 // - 缓存对象被销毁（Unity 的 fake-null）
 // - 距上次扫描超过 RescanInterval 帧（兜底，防漏掉静默重建）
 // · 静默重建（对象被换掉但旧的还没销毁）则由 HandleTool 里的
 // 实例 ID 变化检测兜住，不依赖每帧扫描。
 private static MonoBehaviour _cachedTarget;
 private static Controller _cachedRight;
 private static MonoBehaviour _cachedLeftTarget;
 private static Controller _cachedLeft;
 private static int _framesSinceScan;
 private const int RescanInterval = 900; // 约 15 秒 @60fps。
 // 原来 60 帧（1 秒）一次，实测单次峰值 26.6ms —— 这是"抽帧"的直接来源之一。
 // 内部是 Resources.FindObjectsOfTypeAll（引擎级全表扫描，本项目性能红线点名禁写）。
 // 它只是兜底：缓存失效时会立刻触发扫描（CachedTargetAlive()==false），
 // 常态下没必要每秒扫一遍。

 private static bool CachedTargetAlive()
 {
 // Unity 重载了 == ：被销毁的对象会 "== null" 为真
 return _cachedTarget != null;
 }

 // ---------------- 诊断用（每只手各一份， 新增） ----------------

#if !LEAN
 // 【精简版瘦身】每手诊断数据（只被 Describe/RecordHandDiagnostics 用）
 private struct HandInfo
 {
 public int Count;
 public int LastInstanceId;
 public string LastName;
 public int LastToolIndex;
 public bool LastUnlocked;
 public bool LastEnabled;
 public bool HasData;
 }

 private static HandInfo _leftInfo;
 private static HandInfo _rightInfo;

#endif
 private static void RefreshCache()
 {
 // 【2026-10-22】原来的写法有一个致命"永久失败缓存"：
 // if (_toolTypeSearched && _toolType == null) return; ← 第一次找不到就再也不试
 // 而 KKCharaStudioVR 程序集是**后加载**的，插件启动早期常常还拿不到类型
 // → 拖拽记忆会整局静默失效（和 VRControllerModelFix 踩过的是同一个坑）。
 // 现在：拿不到就"下次再来"，只有真正拿到类型+字段后才走快速返回。
 if (_toolType != null && _lockField != null) return;

 try
 {
 if (_toolType == null)
 {
 _toolType = VRRef.FindType(ToolTypeName);
 if (_toolType == null)
 {
#if !LEAN
 _lastError = "找不到类型 " + ToolTypeName + "（程序集可能还没加载，下次再试）";
#endif
 return;
 }
 }

 if (_lockField == null)
 {
 _lockField = _toolType.GetField(LockFieldName,
 BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

 if (_lockField == null)
 {
#if !LEAN
 _lastError = "找不到字段 " + ToolTypeName + "." + LockFieldName;
#endif
 FixLog.Warn(_lastError + "（0.0.3 版本可能变了），拖拽状态记忆不可用。");
 }
 else
 {
 FixLog.Info("拖拽状态记忆已启用（" + ToolTypeName + "." + LockFieldName
 + "，右手和左手状态分别独立记忆/恢复）。");
 }
 }
 }
 catch (Exception e)
 {
#if !LEAN
 _lastError = "准备失败: " + e.Message;
#endif
 FixLog.Warn("准备拖拽状态记忆失败: " + e.Message);
 }
 }

 /// <summary>
 /// 直接找出场景里所有 GripMoveKKCharaStudioTool 实例。
 /// 这是唯一"不依赖 Mode / ControllerManager / 控制器有效性"的查找方式。
 /// </summary>
 private static MonoBehaviour[] FindAllTools()
 {
 try
 {
 if (_toolType == null) return new MonoBehaviour[0];
 UnityEngine.Object[] all = Resources.FindObjectsOfTypeAll(_toolType);
 if (all == null || all.Length == 0) return new MonoBehaviour[0];

 // 过滤掉 prefab / 未实例化的资源
 int count = 0;
 for (int i = 0; i < all.Length; i++)
 {
 MonoBehaviour m = all[i] as MonoBehaviour;
 if (m == null) continue;
 if (m.gameObject.scene.IsValid()) count++;
 }

 MonoBehaviour[] result = new MonoBehaviour[count];
 int n = 0;
 for (int i = 0; i < all.Length; i++)
 {
 MonoBehaviour m = all[i] as MonoBehaviour;
 if (m == null) continue;
 if (m.gameObject.scene.IsValid()) result[n++] = m;
 }
 return result;
 }
#if LEAN
 catch (Exception)
#else
 catch (Exception e)
#endif
 {
#if !LEAN
 _lastError = "FindAllTools 异常: " + e.Message;
#endif
 return new MonoBehaviour[0];
 }
 }

 /// <summary>取 VR.Mode.Right（右手控制器）。取不到返回 null。</summary>
 private static Controller GetRightController()
 {
 try
 {
 VRGIN.Core.VRManager mgr = VRGIN.Core.VR.Manager;
 if (mgr == null) return null;
 VRGIN.Modes.ControlMode mode = mgr.Mode;
 if (mode == null) return null;
 return mode.Right;
 }
 catch { return null; }
 }

 private static Controller GetLeftController()
 {
 try
 {
 VRGIN.Core.VRManager mgr = VRGIN.Core.VR.Manager;
 if (mgr == null) return null;
 VRGIN.Modes.ControlMode mode = mgr.Mode;
 if (mode == null) return null;
 return mode.Left;
 }
 catch { return null; }
 }

 /// <summary>判断一个工具是不是挂在右手控制器上。</summary>
 private static bool IsRightHand(MonoBehaviour tool, Controller right)
 {
 try
 {
 // 判据 a：工具和控制器在同一个 GameObject 上（Controller.AddTool 就是这么加的）
 Controller owner = tool.GetComponent<Controller>();
 if (right != null && owner != null && ReferenceEquals(owner, right))
 return true;

 // 判据 b：GameObject 名字（RightController.Create() 里写死 "Right Controller"）
 string n = tool.gameObject.name;
 if (!string.IsNullOrEmpty(n) && n.IndexOf("Right", StringComparison.OrdinalIgnoreCase) >= 0)
 return true;

 // 判据 c：owner 的 GameObject 名字
 if (owner != null)
 {
 string on = owner.gameObject.name;
 if (!string.IsNullOrEmpty(on) && on.IndexOf("Right", StringComparison.OrdinalIgnoreCase) >= 0)
 return true;
 }
 }
 catch { }
 return false;
 }

 /// <summary>
 /// 从所有工具里挑出**唯一**要跟踪的那一只。
 /// 优先：识别为"右手"的那一只；
 /// 其次：识别为"左手"的排除掉之后，若只剩一个就用它；
 /// 再次：实在分不出左右时，用实例 ID 最小的那一个（保证**稳定**，绝不每帧换人）。
 /// </summary>
 private static MonoBehaviour PickTargetTool(MonoBehaviour[] tools, out Controller rightOut)
 {
 rightOut = GetRightController();

 if (tools == null || tools.Length == 0) return null;

 // 1. 明确的右手
 for (int i = 0; i < tools.Length; i++)
 if (IsRightHand(tools[i], rightOut)) return tools[i];

 // 2. 排除掉"左手"，剩下的唯一一个
 if (tools.Length == 2)
 {
 MonoBehaviour a = tools[0], b = tools[1];
 bool aLeft = IsLeftHand(a, rightOut);
 bool bLeft = IsLeftHand(b, rightOut);
 if (aLeft && !bLeft) return b;
 if (bLeft && !aLeft) return a;
 }

 // 3. 兜底：实例 ID 最小 —— 关键是要**稳定**，绝不能每帧在两只手之间跳
 MonoBehaviour best = tools[0];
 int bestId = best.GetInstanceID();
 for (int i = 1; i < tools.Length; i++)
 {
 int id = tools[i].GetInstanceID();
 if (id < bestId) { bestId = id; best = tools[i]; }
 }
 return best;
 }

 private static bool IsLeftHand(MonoBehaviour tool, Controller right)
 {
 try
 {
 Controller owner = tool.GetComponent<Controller>();
 if (right != null && owner != null && ReferenceEquals(owner, right)) return false;

 string n = tool.gameObject.name;
 if (!string.IsNullOrEmpty(n) && n.IndexOf("Left", StringComparison.OrdinalIgnoreCase) >= 0)
 return true;

 if (owner != null)
 {
 string on = owner.gameObject.name;
 if (!string.IsNullOrEmpty(on) && on.IndexOf("Left", StringComparison.OrdinalIgnoreCase) >= 0)
 return true;
 }
 }
 catch { }
 return false;
 }

 /// <summary>选择左手实例；先按 ControlMode.Left 引用精确匹配，名字判定只作兜底。</summary>
 private static MonoBehaviour PickLeftTool(MonoBehaviour[] tools, Controller right, out Controller leftOut)
 {
 leftOut = GetLeftController();
 if (tools == null || tools.Length == 0) return null;

 if (leftOut != null)
 {
 for (int i = 0; i < tools.Length; i++)
 {
 try
 {
 Controller owner = tools[i].GetComponent<Controller>();
 if (owner != null && ReferenceEquals(owner, leftOut)) return tools[i];
 }
 catch { }
 }
 }

 for (int i = 0; i < tools.Length; i++)
 if (IsLeftHand(tools[i], right)) return tools[i];

 if (tools.Length == 2)
 for (int i = 0; i < tools.Length; i++)
 if (!IsRightHand(tools[i], right)) return tools[i];

 return null;
 }

 /// <summary>
 /// 把"选实例的依据"落盘：每个工具实例、mode.Right、选中的目标。
 /// 只在内容变化时打（Key 级），避免刷屏。
 /// </summary>
#if !LEAN
 // 【精简版瘦身】只有完整版用得到
 private static string _lastSelectionLog = "";
#endif
#if !LEAN
 // 【精简版瘦身】下面这些都是"只看不改"的诊断输出，精简版一律不编译。
 private static void LogToolSelection(MonoBehaviour[] tools)
 {
 try
 {
 Controller right = GetRightController();
 var sb = new System.Text.StringBuilder();
 sb.Append("mode.Right=").Append(right == null ? "null" :
 right.gameObject.name + "/id=" + right.GetInstanceID()).Append(" | ");
 for (int i = 0; i < tools.Length; i++)
 {
 if (tools[i] == null) continue;
 Controller owner = tools[i].GetComponent<Controller>();
 bool lockVal = true;
 try { lockVal = (bool)_lockField.GetValue(tools[i]); } catch { }
 sb.Append("[id=").Append(tools[i].GetInstanceID())
 .Append(" go=").Append(tools[i].gameObject.name)
 .Append(" owner=").Append(owner == null ? "null" : owner.gameObject.name)
 .Append(" lockRotXZ=").Append(lockVal ? "true(锁定)" : "false(解锁)")
 .Append(IsRightHand(tools[i], right) ? " ←判为右手" : "")
 .Append("] ");
 }
 MonoBehaviour target = PickTargetTool(tools, out right);
 Controller left;
 MonoBehaviour leftTarget = PickLeftTool(tools, right, out left);
 sb.Append("=> 右手目标 id=").Append(target == null ? 0 : target.GetInstanceID())
 .Append("；左手目标 id=").Append(leftTarget == null ? 0 : leftTarget.GetInstanceID());
 string s = sb.ToString();
 if (s == _lastSelectionLog) return;
 _lastSelectionLog = s;
 FixLog.Key("【拖动目标选择】" + s);
 }
 catch (Exception e) { FixLog.Debug("LogToolSelection 异常: " + e.Message); }
 }
#else
 // 【LEAN】诊断输出整段删除（只给完整版）
#endif

 /// <summary>每帧调用（从看门狗 LateUpdate）。</summary>
#if NEWFIX
 // ================= 【NEWFIX】事件：工具被重建的那一刻 =================
 // 0.0.3 每次重建 StandingMode 都会 AddComponent 出**全新的** GripMoveKKCharaStudioTool，
 // 而新实例的 `lockRotXZ` 默认是 true（锁定）→ 玩家刚解锁的世界又被锁回去。
 // 以前只能靠 Tick 周期扫描发现；现在挂 `Awake`，新工具一出生就把它标记成"待恢复"，
 // 下一次 Tick 立刻扫描（不用等 900 帧的兜底间隔）。
 private static bool _awakeHooked;
 private static bool _startupArmed;
 private static HarmonyLib.Harmony _awakeHarmony;

 internal static void TryInstallAwakeHook()
 {
 if (_awakeHooked) return;

 try
 {
 Type t = VRRef.FindType("KKCharaStudioVR.GripMoveKKCharaStudioTool");
 if (t == null) return; // 程序集还没加载 → 下次再来（不是定时器，是"类型一出现就装"）

 // 【2026-10-22】只做一次的"启动对账"，放在**类型已解析成功之后**：
 // 拖动工具通常在插件装钩子之前就已创建 → `Awake` 事件永远不会补发，
 // 所以这里主动排一次队，让下一帧的 ConsumePendingRestore 把"默认解锁"补上。
 // 放在这里而不是函数开头，是为了保证排队时 `_lockField` 一定能解析出来（一次成功，不重试）。
 if (!_startupArmed) { _startupArmed = true; ArmRestore(); }
 System.Reflection.MethodInfo aw = t.GetMethod("Awake",
 System.Reflection.BindingFlags.Instance
 | System.Reflection.BindingFlags.Public
 | System.Reflection.BindingFlags.NonPublic);
 if (aw == null) return;

 if (_awakeHarmony == null)
 _awakeHarmony = new HarmonyLib.Harmony(KKCharaStudioVRFixPlugin.Guid + ".dragawake");
 _awakeHarmony.Patch(aw, postfix: new HarmonyLib.HarmonyMethod(
 typeof(DragStateFix).GetMethod("OnToolAwake")));

 HarmonyLib.Patches info = HarmonyLib.Harmony.GetPatchInfo(aw);
 if (info != null && info.Postfixes != null)
 {
 foreach (HarmonyLib.Patch p in info.Postfixes)
 {
 if (p != null && p.PatchMethod != null
 && p.PatchMethod.Name == "OnToolAwake") { _awakeHooked = true; break; }
 }
 }
 if (KKCharaStudioVRFixPlugin.DiagOn)
 FixLog.Key("【事件驱动·NEWFIX】拖动工具 Awake 事件 = "
 + (_awakeHooked ? "已挂上（重建后不用等周期扫描）" : "未挂上"));
 }
 catch (Exception e) { FixLog.Debug("DragStateFix.TryInstallAwakeHook 异常: " + e.Message); }
 }

 private static bool _restorePending;

 /// <summary>
 /// 【2026-10-22 新增】"立刻做一次状态对账" —— 只由**事件**触发，没有任何轮询：
 /// ① `GripMoveKKCharaStudioTool.Awake`（工具重建的那一刻）
 /// ② `StandingMode.OnStart` postfix（模式重建 → 控制器与工具会被重建）
 /// ③ 本类 Awake 钩子**安装的那一刻**（补"工具在我们挂钩之前就已经存在"的启动场景）
 ///
 /// ③ 就是「进游戏第一次不默认解锁拖动世界」的正解：
 /// 0.0.3 的拖动工具是在插件装钩子**之前**就 AddComponent 完的 → `Awake` 事件再也不会触发
 /// → 首帧没有任何人给 `HandleTool` 机会 → `firstEver` 的"默认解锁"永远不执行。
 /// </summary>
 internal static void ArmRestore()
 {
 try { _framesSinceScan = RescanInterval; _restorePending = true; }
 catch { }
 }

 /// <summary>
 /// 工具实例刚被创建 → 只置一个"待恢复"标志。
 /// **不在 Awake 里扫描**（FindAllTools 单次峰值 26ms，放在 Awake 里就是加载期的额外卡顿）；
 /// 由看门狗在**下一帧**调用 `ConsumePendingRestore()` 一次性完成。
 /// </summary>
 public static void OnToolAwake()
 {
 ArmRestore();
 }

 /// <summary>看门狗每帧调；只有真的发生过"工具重建/启动对账"才干活，否则立即返回。</summary>
 internal static bool ConsumePendingRestore()
 {
 if (!_restorePending) return false;
 _restorePending = false;

 // ★ 这里**不做"隔 N 帧再试"**（那也是一种轮询）。
 // 排队只由事件触发，而三个触发点（工具 Awake / 模式 OnStart / 装钩子那一刻）
 // 都保证"类型已经能解析出来"，所以走到这里 `_lockField` 必然是好的。
 // 万一真的没有（0.0.3 改了字段名）→ 直接放弃，等下一个事件再来。
 RefreshCache();
 if (_lockField == null) return false;

 try { Tick(); } catch { }
 return true;
 }
#endif

 internal static void Tick()
 {
 RefreshCache();
 if (_lockField == null) return;

 try
 {
 _observedFrames++;
 _framesSinceScan++;

 // 右手和左手都是独立组件实例（`lockRotXZ` 为非 static 字段），必须分别缓存/恢复。
 // 
 // 旧逻辑：`!CachedTargetAlive() || _cachedLeftTarget == null || 超时`
 // → 如果左手工具**从来没找到过**（或该手没有工具），`_cachedLeftTarget == null`
 // 会**每 0.2 秒（每秒 5 次）绕过节流做全表扫描**
 // （Resources.FindObjectsOfTypeAll，实测单次峰值 26.6ms）—— 这就是
 // "帧数动不动从 53 掉到 48"的直接来源。
 // 新逻辑：只有"**曾经**有过缓存、现在被销毁了"才立刻重扫（场景切换的正当需求）；
 // 从来没有过的，老老实实按 RescanInterval 节流。
 bool wasEverCached = _cachedTarget != null || _cachedLeftTarget != null
 || _framesSinceScan > 0;
 bool needScan = _framesSinceScan >= RescanInterval
 || (wasEverCached && !CachedTargetAlive());
 if (needScan)
 {
 _framesSinceScan = 0;

 MonoBehaviour[] tools = FindAllTools();
 if (tools.Length == 0)
 {
 _cachedTarget = null;
 _cachedRight = null;
 _cachedLeftTarget = null;
 _cachedLeft = null;
#if !LEAN
 _lastError = "场景里没找到 GripMoveKKCharaStudioTool";
#endif
 return;
 }

#if !LEAN
 // 【精简版瘦身】诊断记录整段删除（只给完整版）
 RecordHandDiagnostics(tools);
 LogToolSelection(tools);
#endif

 // 两只手分别缓存：日志已证实右手仍 locked 时，左手可能已 unlocked。
 Controller right;
 MonoBehaviour target = PickTargetTool(tools, out right);
 Controller left;
 MonoBehaviour leftTarget = PickLeftTool(tools, right, out left);

 _cachedTarget = target;
 _cachedRight = right;
 _cachedLeftTarget = leftTarget;
 _cachedLeft = left;
 }

 // 两只手是独立组件实例，必须分别检测重建、记忆锁定状态和恢复工具序号。
 if (CachedTargetAlive() && _cachedTarget != null)
 HandleTool(_cachedTarget, _cachedRight); // 右手原有恢复路径
 else
#if !LEAN
 _lastError = "没能从拖动工具里挑出右手目标";
#endif

 if (_cachedLeftTarget != null)
 HandleLeftTool(_cachedLeftTarget, _cachedLeft);
 }
 catch (Exception e)
 {
#if !LEAN
 _lastError = "Tick 异常: " + e.Message;
#endif
 FixLog.Debug("DragStateFix.Tick 异常: " + e.Message);
 }
 }

#if !LEAN
 // 【精简版瘦身】下面这些都是"只看不改"的诊断输出，精简版一律不编译。
 /// <summary>
 /// 新增：把每只手的实时状态记下来（只读、只供 F7 诊断显示），
 /// **不参与**记忆 / 恢复，因此不会像 那样污染 `_lastToolId`。
 /// </summary>
 private static void RecordHandDiagnostics(MonoBehaviour[] tools)
 {
 Controller right = GetRightController();

 int leftCount = 0, rightCount = 0;
 HandInfo li = _leftInfo, ri = _rightInfo;
 li.Count = 0; ri.Count = 0;

 for (int i = 0; i < tools.Length; i++)
 {
 MonoBehaviour t = tools[i];
 bool isRight = IsRightHand(t, right);
 bool isLeft = !isRight && IsLeftHand(t, right);

 bool unlocked = false;
 bool lockOk = true;
 try { unlocked = !(bool)_lockField.GetValue(t); }
 catch { lockOk = false; }

 int idx = -1;
 try
 {
 Controller c = t.GetComponent<Controller>();
 if (c != null) idx = c.ToolIndex;
 }
 catch { }

 string name = "";
 try { name = t.gameObject.name; } catch { }

 HandInfo info = default(HandInfo);
 info.LastInstanceId = t.GetInstanceID();
 info.LastName = name;
 info.LastToolIndex = idx;
 info.LastUnlocked = unlocked;
 info.LastEnabled = t.enabled;
 info.HasData = lockOk;

 if (isRight) { ri = info; rightCount++; }
 else if (isLeft) { li = info; leftCount++; }
 else
 {
 // 分不出左右：算进总数，但标记名字便于人工核对
 info.LastName = "[未识别] " + name;
 if (ri.Count == 0) { ri = info; rightCount++; }
 else { li = info; leftCount++; }
 }
 }

 ri.Count = rightCount;
 li.Count = leftCount;
 _rightInfo = ri;
 _leftInfo = li;
 }
#else
 // 【LEAN】诊断输出整段删除（只给完整版）
#endif

 /// <summary>DefaultUnlockedDrag 开关（默认 true）。</summary>
 private static bool DefaultUnlockedEnabled()
 {
 try
 {
 return KKCharaStudioVRFixPlugin.On(KKCharaStudioVRFixPlugin.CfgDefaultUnlockedDrag);
 }
 catch { return true; }
 }

 /// <summary>处理右手工具的锁定状态和工具序号（左手由 HandleLeftTool 独立处理）。</summary>
 private static void HandleTool(MonoBehaviour tool, Controller right)
 {
 int toolId = tool.GetInstanceID();

 Controller ctrl = null;
 try { ctrl = tool.GetComponent<Controller>(); }
 catch { }

 bool firstEver = (_lastToolId == 0 && !_rememberedUnlocked.HasValue);
 bool rebuilt = (_lastToolId != 0 && toolId != _lastToolId);

 // rebuilt 之后静默 2 秒再记录。实机日志（09:53）铁证：
 // 用户解锁后加载场景卡 → 缓存目标在场景切换中瞬间失效几帧 → 工具重建 →
 // rebuilt 帧不记录（已有），但**紧接着的稳定帧把新工具的默认 true(锁定)
 // 记进了记忆** → 恢复时按"已锁定"办 → 用户解锁丢失。
 // 解法：重建后 120 帧内不更新记忆（恢复动作照常执行）。
 if (rebuilt) _quietFramesAfterRebuild = 120;
 else if (_quietFramesAfterRebuild > 0) _quietFramesAfterRebuild--;

 // ================= 1. 读取当前 lockRotXZ =================
 bool unlocked = false;
 bool lockReadOk = true;
 try { unlocked = !(bool)_lockField.GetValue(tool); }
#if LEAN
 catch (Exception)
#else
 catch (Exception e)
#endif
 {
 lockReadOk = false;
#if !LEAN
 _lastError = "读取 lockRotXZ 失败: " + e.Message;
#endif
 }

 // ================= 1.5 首次见到这只手时默认解锁 =================
 // 用户要求：进游戏就是解锁，不用每次手动按三键。
 // 只在该手**从未被观测过**时写一次，之后用户的锁定/解锁选择仍会被记忆并在重建后恢复。
 if (firstEver && lockReadOk && DefaultUnlockedEnabled())
 {
 try
 {
 if ((bool)_lockField.GetValue(tool))
 {
 _lockField.SetValue(tool, false);
 FixLog.Key("【默认解锁】右手「拖动世界」已按 DefaultUnlockedDrag 设为解锁"
 + "（世界可自由旋转），以后不用每次手动解锁。");
 }
 _rememberedUnlocked = false; // 记忆=解锁 → 之后重建也恢复解锁
 }
 catch (Exception e) { _lastError = "默认解锁失败: " + e.Message; }
 }

 // ================= 2. 只在"同一只工具"上记录状态 =================
 // `rebuilt == true` 时**不记录**！
 // 因为那说明这是刚刚 AddComponent 出来的全新实例，它的状态是默认值，
 // 拿它当"用户的选择"会把新工具的默认 true(锁定) 当成用户的意图记住。
 // 就是因为每个帧都在"两只手之间"触发 rebuilt，把记忆搅成了硬币。
 // firstEver 帧不记录：那可能是刚 AddComponent 出来的新工具，
 // 它的 lockRotXZ 是默认 true，不是用户的选择（防"记忆被默认值污染"）。
 if (lockReadOk && !rebuilt && !firstEver && _quietFramesAfterRebuild <= 0)
 {
 if (!_rememberedUnlocked.HasValue || _rememberedUnlocked.Value != unlocked)
 {
 _rememberedUnlocked = unlocked;
 FixLog.Key("记录拖拽锁定状态: "
 + (unlocked ? "已解锁（世界可自由旋转）" : "已锁定（只水平平移）"));
 }

 if (ctrl != null)
 {
 try
 {
 int idx = ctrl.ToolIndex;
 if (_rememberedToolIndex != idx)
 {
 _rememberedToolIndex = idx;
 FixLog.Info("记录当前工具序号: " + idx
 + "（右手）");
 }
 }
 catch (Exception e) { _lastError = "读取 ToolIndex 失败: " + e.Message; }
 }
 }

 _lastToolId = toolId;

 if (firstEver || !rebuilt) return;

 // ================= 3. 重建时恢复 =================
 FixLog.Key("检测到拖动工具被重建（场景卡加载）：实例 ID " + _lastToolId
 + " 已刷新，开始恢复用户状态（记忆: "
 + (_rememberedUnlocked.HasValue
 ? (_rememberedUnlocked.Value ? "已解锁" : "已锁定") : "无")
 + "，工具序号 " + _rememberedToolIndex + "）。");

 // --- 恢复解锁状态 ---
 // 只在"用户上次是解锁"时才写 false。
 // 若用户上次是锁定，不必写（新工具默认就是 true=锁定），避免无意义改动。
 bool wantUnlocked = _rememberedUnlocked ?? false;
 if (wantUnlocked)
 {
 try
 {
 bool freshLocked = (bool)_lockField.GetValue(tool);
 if (freshLocked)
 {
 _lockField.SetValue(tool, false);
 _restoreCount++;
 FixLog.Key("拖动工具被重建（场景卡加载），已自动恢复「解锁世界」状态（第 "
 + _restoreCount + " 次）。不用再手动按三键了。");
 }
 }
 catch (Exception e) { _lastError = "恢复解锁失败: " + e.Message; }
 }
 else
 {
 FixLog.Info("用户上次是「锁定（只水平平移）」，新工具默认即为锁定，无需改动。");
 }

 // --- 恢复 ToolIndex（修「图标变回齿轮」） ---
 try
 {
 if (ctrl != null
 && _rememberedToolIndex >= 0
 && ctrl.Tools != null
 && _rememberedToolIndex < ctrl.Tools.Count
 && ctrl.ToolIndex != _rememberedToolIndex)
 {
 int old = ctrl.ToolIndex;

 VRGIN.Controls.Tools.Tool oldActive = null;
 try { oldActive = ctrl.ActiveTool; } catch { }
 if (oldActive != null) oldActive.enabled = false;

 ctrl.ToolIndex = _rememberedToolIndex;

 VRGIN.Controls.Tools.Tool newActive = null;
 try { newActive = ctrl.ActiveTool; } catch { }
 if (newActive != null) newActive.enabled = true;

 _iconRestoreCount++;
 FixLog.Key("控制器工具序号已恢复: " + old + " -> " + _rememberedToolIndex
 + "（修「加载后手柄图标变回齿轮」，第 " + _iconRestoreCount + " 次）。");
 }
 }
 catch (Exception e)
 {
 FixLog.Debug("恢复工具序号失败: " + e.Message);
 }
 }

 /// <summary>
 /// 左手独立状态跟踪/恢复。
 /// 用户日志铁证：右手 lockRotXZ=true（锁定），同时左手=false（解锁），
 /// 但旧版只记右手，故每次重建都把已解锁的左手丢掉。
 /// </summary>
 private static void HandleLeftTool(MonoBehaviour tool, Controller ctrl)
 {
 if (tool == null) return;
 int toolId = tool.GetInstanceID();
 bool firstEver = (_lastLeftToolId == 0 && !_rememberedLeftUnlocked.HasValue);
 bool rebuilt = (_lastLeftToolId != 0 && toolId != _lastLeftToolId);

 if (rebuilt) _quietLeftFramesAfterRebuild = 120;
 else if (_quietLeftFramesAfterRebuild > 0) _quietLeftFramesAfterRebuild--;

 bool unlocked = false;
 bool lockReadOk = true;
 try { unlocked = !(bool)_lockField.GetValue(tool); }
#if LEAN
 catch (Exception)
#else
 catch (Exception e)
#endif
 {
 lockReadOk = false;
#if !LEAN
 _lastError = "读取左手 lockRotXZ 失败: " + e.Message;
#endif
 }

 if (firstEver && lockReadOk && DefaultUnlockedEnabled())
 {
 try
 {
 if ((bool)_lockField.GetValue(tool))
 {
 _lockField.SetValue(tool, false);
 FixLog.Key("【默认解锁】左手「拖动世界」已按 DefaultUnlockedDrag 设为解锁。");
 }
 _rememberedLeftUnlocked = false;
 }
 catch (Exception e) { _lastError = "左手默认解锁失败: " + e.Message; }
 }

 if (lockReadOk && !rebuilt && !firstEver && _quietLeftFramesAfterRebuild <= 0)
 {
 if (!_rememberedLeftUnlocked.HasValue || _rememberedLeftUnlocked.Value != unlocked)
 {
 _rememberedLeftUnlocked = unlocked;
 FixLog.Key("记录左手拖拽锁定状态: "
 + (unlocked ? "已解锁（世界可自由旋转）" : "已锁定（只水平平移）"));
 }
 if (ctrl != null)
 {
 try
 {
 int idx = ctrl.ToolIndex;
 if (_rememberedLeftToolIndex != idx)
 {
 _rememberedLeftToolIndex = idx;
 FixLog.Key("记录左手当前工具序号: " + idx);
 }
 }
 catch (Exception e) { _lastError = "读取左手 ToolIndex 失败: " + e.Message; }
 }
 }

 _lastLeftToolId = toolId;
 if (firstEver || !rebuilt) return;

 FixLog.Key("检测到左手拖动工具被重建：实例 ID " + toolId
 + "，记忆解锁=" + (_rememberedLeftUnlocked.HasValue
 ? (_rememberedLeftUnlocked.Value ? "是" : "否") : "未知")
 + "，左手工具序号=" + _rememberedLeftToolIndex + "。");

 if (_rememberedLeftUnlocked == true)
 {
 try
 {
 if ((bool)_lockField.GetValue(tool))
 {
 _lockField.SetValue(tool, false);
 _leftRestoreCount++;
 FixLog.Key("左手已恢复「解锁世界」状态（第 " + _leftRestoreCount + " 次）。");
 }
 }
 catch (Exception e) { _lastError = "恢复左手解锁失败: " + e.Message; }
 }

 try
 {
 if (ctrl != null && _rememberedLeftToolIndex >= 0
 && ctrl.Tools != null && _rememberedLeftToolIndex < ctrl.Tools.Count
 && ctrl.ToolIndex != _rememberedLeftToolIndex)
 {
 VRGIN.Controls.Tools.Tool oldActive = null;
 try { oldActive = ctrl.ActiveTool; } catch { }
 if (oldActive != null) oldActive.enabled = false;
 int old = ctrl.ToolIndex;
 ctrl.ToolIndex = _rememberedLeftToolIndex;
 VRGIN.Controls.Tools.Tool newActive = null;
 try { newActive = ctrl.ActiveTool; } catch { }
 if (newActive != null) newActive.enabled = true;
 _leftIconRestoreCount++;
 FixLog.Key("左手控制器工具序号已恢复: " + old + " -> " + _rememberedLeftToolIndex
 + "（第 " + _leftIconRestoreCount + " 次）。");
 }
 }
 catch (Exception e) { FixLog.Debug("恢复左手工具序号失败: " + e.Message); }
 }

#if !LEAN
 // 【精简版瘦身】下面这些都是"只看不改"的诊断输出，精简版一律不编译。
 /// <summary>诊断/日志用。</summary>
 internal static string Describe()
 {
 if (_lockField == null)
 return "不可用（未找到字段）" + (_lastError.Length > 0 ? "；原因: " + _lastError : "");

 string rightState = _rememberedUnlocked.HasValue
 ? (_rememberedUnlocked.Value ? "已解锁" : "已锁定")
 : "未观测";
 string leftState = _rememberedLeftUnlocked.HasValue
 ? (_rememberedLeftUnlocked.Value ? "已解锁" : "已锁定")
 : "未观测";

 MonoBehaviour[] tools;
 try { tools = FindAllTools(); }
 catch { tools = new MonoBehaviour[0]; }

 System.Text.StringBuilder sb = new System.Text.StringBuilder();
 sb.Append("右手 ").Append(rightState).Append("（记忆）；左手 ").Append(leftState).Append("（记忆）");
 sb.Append("；工具序号记忆 R=").Append(_rememberedToolIndex)
 .Append(" L=").Append(_rememberedLeftToolIndex);
 sb.Append("；场景中拖动工具数 = ").Append(tools.Length);
 sb.Append("；已恢复解锁 R=").Append(_restoreCount).Append(" L=").Append(_leftRestoreCount).Append(" 次");
 sb.Append("；已恢复图标 R=").Append(_iconRestoreCount).Append(" L=").Append(_leftIconRestoreCount).Append(" 次");
 sb.Append("；已观测 ").Append(_observedFrames).Append(" 帧");
 sb.Append("；目标实例ID R=").Append(_lastToolId).Append(" L=").Append(_lastLeftToolId);

 // 逐手明细 —— 回应用户"实在不行F7里加更多检测方式"
 sb.Append("\n【右手】").Append(DescribeHand(_rightInfo));
 sb.Append("\n【左手】").Append(DescribeHand(_leftInfo));

 // 逐个工具列出（最可靠的核对方式：看两只是不是各有一个控制器）
 for (int i = 0; i < tools.Length; i++)
 {
 sb.Append("\n · 工具#").Append(i)
 .Append(" ID=").Append(tools[i].GetInstanceID())
 .Append(" 物体=\"").Append(SafeName(tools[i])).Append("\"")
 .Append(" enabled=").Append(tools[i].enabled)
 .Append(" 控制器=").Append(DescribeOwner(tools[i]))
 .Append(" ToolIndex=").Append(SafeToolIndex(tools[i]))
 .Append(" lockRotXZ=").Append(SafeLock(tools[i]));
 }

 if (_lastError.Length > 0) sb.Append("；最近状态: ").Append(_lastError);
 return sb.ToString();
 }
#else
 // 【LEAN】诊断输出整段删除（只给完整版）
#endif

#if !LEAN
 // 【精简版瘦身】下面这些都是"只看不改"的诊断输出，精简版一律不编译。
 private static string DescribeHand(HandInfo h)
 {
 if (!h.HasData || h.LastInstanceId == 0) return "（未发现工具）";
 return "工具数=" + h.Count
 + " ID=" + h.LastInstanceId
 + " 物体=\"" + h.LastName + "\""
 + " ToolIndex=" + h.LastToolIndex
 + " lockRotXZ=" + (h.LastUnlocked ? "false(已解锁)" : "true(已锁定)")
 + " enabled=" + h.LastEnabled;
 }
#else
 // 【LEAN】诊断输出整段删除（只给完整版）
#endif

#if !LEAN
 // 【精简版瘦身】下面这些都是"只看不改"的诊断输出，精简版一律不编译。
 private static string SafeName(MonoBehaviour t)
 {
 try { return t.gameObject.name; } catch { return "?"; }
 }
#else
 // 【LEAN】诊断输出整段删除（只给完整版）
#endif

#if !LEAN
 // 【精简版瘦身】下面这些都是"只看不改"的诊断输出，精简版一律不编译。
 private static string DescribeOwner(MonoBehaviour t)
 {
 try
 {
 Controller c = t.GetComponent<Controller>();
 if (c == null) return "无";
 return "\"" + c.gameObject.name + "\"(" + c.GetType().Name + ")";
 }
 catch { return "读失败"; }
 }
#else
 // 【LEAN】诊断输出整段删除（只给完整版）
#endif

#if !LEAN
 // 【精简版瘦身】下面这些都是"只看不改"的诊断输出，精简版一律不编译。
 private static string SafeToolIndex(MonoBehaviour t)
 {
 try
 {
 Controller c = t.GetComponent<Controller>();
 if (c == null) return "?";
 return c.ToolIndex + "/" + (c.Tools != null ? c.Tools.Count : 0);
 }
 catch { return "?"; }
 }
#else
 // 【LEAN】诊断输出整段删除（只给完整版）
#endif

#if !LEAN
 // 【精简版瘦身】下面这些都是"只看不改"的诊断输出，精简版一律不编译。
 private static string SafeLock(MonoBehaviour t)
 {
 try { return ((bool)_lockField.GetValue(t)) ? "true(锁定)" : "false(已解锁)"; }
 catch { return "读失败"; }
 }
#else
 // 【LEAN】诊断输出整段删除（只给完整版）
#endif
 }
}
