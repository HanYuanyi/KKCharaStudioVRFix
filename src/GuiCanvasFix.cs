using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using VRGIN.Core;

namespace KKCharaStudioVRFix
{
 /// <summary>
 /// 修复「系统菜单右侧面板在 VR 里看不见、但能用指针点到」。
 ///
 /// 机制（VRGIN_KKCS/VRGIN.Core/VRGUI.cs）：
 /// VRGUI 把所有 UI Canvas 渲染到一张 RenderTexture(uGuiTexture)，再由 VR 里的
 /// GUIQuad 显示出来。它靠 CatchCanvas() 逐个把 Canvas 转成
 /// ScreenSpaceCamera + worldCamera = _VRGUICamera，并把整个 Canvas 挪到
 /// VR.Context.UILayer 图层（只有这一层会被 _VRGUICamera 渲染）。
 ///
 /// 而 CatchCanvas() 的筛选条件是 IsUnprocessed(c)：
 /// ScreenSpaceOverlay → 处理
 /// ScreenSpaceCamera && worldCamera != 本相机 && 该相机无 targetTexture → 处理
 /// 其它（含 WorldSpace） → 不处理
 ///
 /// 一旦某个 Canvas 没被处理：
 /// · 它不会被渲染进 uGuiTexture → VR 里看不到；
 /// · 它留在原图层，而 VR 相机不渲染那个图层 → 还是看不到；
 /// · 但它的 GraphicRaycaster 仍然工作 → 指针能点到。
 ///
 /// ========================= 重大修正 =========================
 /// v1.7.0 的写法有三个致命副作用，正是这一轮新 BUG 的根源（已由反编译证实）：
 ///
 /// ① 它把 **WorldSpace** 的 Canvas 也「接管」了。
 /// 但 WorldSpace Canvas 是**世界里的 3D 物件**（不是 UI）。VRGIN 自己
 /// CatchCanvas 明确不碰它，Studio 的物件放置预览（`New Game Object`）就属于
 /// 这一类。把它改成 ScreenSpaceCamera+UI 图层后，游戏下一帧又把它改回
 /// WorldSpace → 下一轮检查再次接管 … 无限循环（日志里
 /// `接管漏掉的 UI Canvas [周期检查]: New Game Object (renderMode=WorldSpace)` 
 /// 每 4 秒重复一次，共 8 次，就是这个循环）。
 ///
 /// ② 它把 Canvas 的**整棵子物体树**改成 UI(5) 图层，而且**永不还原**。
 /// 手柄模型 / 拖动工具 / 物件预览的整个层级被永久搬到 UI 图层后，
 /// VRGIN 的 _VRGUICamera（cullingMask = UI）就会把它们一起渲染进
 /// uGuiTexture —— 而 uGuiTexture 又贴在**显示在眼前的 GUIQuad** 上。
 /// → 于是「**手柄的图标被永久固定在了 UI 上**」。这是 BUG #1 的直接成因。
 ///
 /// ③ 它每 1 秒无条件重跑，且没有"记住已经接管过的东西"。
 ///
 /// 现在改成：
 /// · **只处理 ScreenSpaceOverlay / ScreenSpaceCamera(别人的相机)**，绝不碰 WorldSpace；
 /// · **只改 Canvas 自身 GameObject 的图层**（VRGIN 的 CatchCanvas 就是这么做的，
 /// 它改整个子树只在"自己是 UI 图层"的前提下才做，而这次的元凶正是那个子树遍历）；
 /// 子树图层只在**明确是纯 UI 画布**（名字/父级能判定）时才动；
 /// · 记录"已接管"的实例，已接管的不再重复处理（避免和游戏抢 Canvas 状态）；
 /// · 黑名单：手柄 / 控制器 / 拖动工具 / 光标 / 准星等绝对不碰。
 /// </summary>
 internal static class GuiCanvasFix
 {
 private static Camera _guiCamera;
 private static bool _guiCameraSearched;

 private static RenderTexture _guiTexture;
 // 这两个是"VR 的 UI 层信息"。原来的哨兵值是 -1 / -2，而 **-1 本身是合法的
 // LayerMask 值（= 全部层）** → 用它当"未初始化"标记会出人命，见 RefreshLayerInfo 注释。
 // 现在改成：0 / -1 都表示"还没拿到"，且只在真的拿到时才写。
 private static int _uiLayerMask = 0;
 private static int _uiLayer = -1;
 private static bool _layerWarned;

 /// <summary>最近一次 UndoStrayLayerMove 的扫描统计（诊断用）。</summary>
#if !LEAN
 private static string _lastStrayScanStats = "";
#endif

 /// <summary>累计回滚的错层节点数（诊断用）。</summary>
 private static int _strayFixedTotal = 0;

 // ================= UI 图层保修 =================
 // 事故（2026-10-18 日志，硬证据）：
 // 坏会话：【UI 现场】里 `01_Screen Effect / 00_Config / 02_Option / 03_Light / 05_Logo`
 // 全都是「模式=ScreenSpaceCamera 相机=VRGIN_GUICamera **图层=Default**」
 // → 画面效果菜单看不见（但能点到）。
 // 好会话（10-16 21:34 日志）：同样这几个画布是「…相机=VRGIN_GUICamera **图层=UI**」
 // → 菜单正常显示。
 // 两次日志唯一的差别就是这一列，所以「图层」就是「看不见」的直接原因：
 // VRGIN 的 GUI 相机只渲染 UI 层，Default 层的东西根本不会被画进 uGuiTexture。
 //
 // 为什么我们的接管逻辑没救回来：RunOnce / AdoptCanvasNow 里有一条
 // `if (renderMode == ScreenSpaceCamera && worldCamera == _guiCamera) continue;`
 // —— 认为「模式+相机已经是 GUI 相机 = 已被 VRGIN 接管 = 不用管」。
 // 但实测存在「VRGIN 改了模式/相机、**没改图层**」的状态 → 被这条判据整段跳过 → 永久不可见。
 //
 // 修法：把「图层」当成**独立不变量**来保修，与「模式/相机」解耦：
 // · 扫描时遇到「GUI 相机的画布」→ 不管有没有接管，都先校一次图层，并登记进保修表；
 // · 保修表每 0.5 秒做一次**零分配**复查（只读 gameObject.layer），
 // 万一之后又被别人改回 Default，0.5 秒内就修回来。
 // 只改 Canvas 根节点（除非判定为纯 UI 画布才搬子树）—— 与既有接管口径一致，
 // 避免重犯「把手柄图标搬进 UI 层」的老 BUG。
 private static readonly List<Canvas> _layerWatch = new List<Canvas>(32);
 private static float _lastLayerCheck = -999f;
 private const float LayerWatchInterval = 0.5f;
 private static int _layerFixTotal;
#if !NEWFIX
 private static float _vrReadyTime = -1f; // GUI 相机第一次拿到的时刻（决定"快速发现"窗口）
#endif

 /// <summary>这个图层是否会被 VRGIN 的 GUI 相机渲染（UI 层）。</summary>
 private static bool IsOnUiLayer(int layer)
 {
 return _uiLayerMask > 0 && ((1 << layer) & _uiLayerMask) != 0;
 }

 private static void AddToLayerWatch(Canvas c)
 {
 try
 {
 if (c == null) return;
 for (int i = 0; i < _layerWatch.Count; i++)
 if (_layerWatch[i] == c) return;
 _layerWatch.Add(c);
 }
 catch { }
 }

 /// <summary>
 /// 保证「被 VRGIN GUI 相机渲染的画布」在 UI 图层。返回修好的节点数（0 = 本来就对）。
 /// 这是**不变量保修**，不是猜：GUI 相机 cullingMask 只含 UI 层，别的层一定画不出来。
 /// </summary>
 internal static int EnsureUiLayer(Canvas c, string reason)
 {
 try
 {
 if (c == null) return 0;
 if (_uiLayer < 0 || _uiLayerMask <= 0)
 {
 // 以前这里静默 return —— 保修整个失效却一个字都不打，
 // 于是"菜单看不见"看起来像玄学。现在至少喊一次。
 if (!_layerWarned)
 {
 _layerWarned = true;
 FixLog.Warn("【UI 图层保修】还没拿到 VR 的 UI 层信息（VR.Context 未就绪），"
 + "保修暂不生效 —— 会持续重试，拿到后自动补上。");
 }
 return 0;
 }
 int before = c.gameObject.layer;
 if (IsOnUiLayer(before)) return 0;

 bool subtree = IsPureUiCanvas(c);
 c.gameObject.layer = _uiLayer;
 int moved = 1;
 if (subtree)
 {
 Transform[] all = c.gameObject.GetComponentsInChildren<Transform>(true);
 for (int k = 0; k < all.Length; k++)
 {
 if (all[k] == null) continue;
 if (all[k].gameObject.layer == _uiLayer) continue;
 all[k].gameObject.layer = _uiLayer;
 moved++;
 }
 }

 _layerFixTotal += moved;
 if (KKCharaStudioVRFixPlugin.DiagOn)
 FixLog.Key("【UI 图层保修】" + c.gameObject.name + " 原来在 "
 + LayerMask.LayerToName(before) + " 层（GUI 相机不渲染它 → 看不见但能点到）"
 + " → 已搬到 " + LayerMask.LayerToName(_uiLayer) + " 层（" + moved
 + " 个节点，搬子树=" + subtree + "）。触发：" + reason);
 return moved;
 }
 catch (Exception e)
 {
 FixLog.Debug("EnsureUiLayer 异常: " + e.Message);
 return 0;
 }
 }

 /// <summary>
 /// 每帧调用（内部 0.5 秒节流）：对保修表里的画布做一次零分配图层复查。
 /// 成本 = 几十次 `gameObject.layer` 读取，可忽略；发现被改坏就立刻修回来。
 /// </summary>
 internal static void LayerWatchTick()
 {
 try
 {
 if (Time.realtimeSinceStartup - _lastLayerCheck < LayerWatchInterval) return;
 _lastLayerCheck = Time.realtimeSinceStartup;

#if NEWFIX
 // 【NEWFIX】改用**引擎自己的画布表**（VRGIN 的 CatchCanvas 读的就是它）。
 // 好处：既能发现"运行时新建的画布"，成本又只有几十次字典读取 ——
 // 所以 3 秒/30 秒/60 秒那套全表扫描可以彻底不要了。
 RefreshCache();
 if (_guiCamera != null)
 {
 System.Collections.IDictionary dict = GetEngineCanvasDict();
 if (dict != null)
 {
 foreach (object k in dict.Keys)
 {
 Canvas ec = k as Canvas;
 if (ec == null) continue;
 if (ec.renderMode != RenderMode.ScreenSpaceCamera) continue;
 if (ec.worldCamera != _guiCamera) continue;
 if (IsOnUiLayer(ec.gameObject.layer)) continue;
 EnsureUiLayer(ec, "引擎画布表复查");
 }
 return;
 }
 }
#endif

 if (_layerWatch.Count == 0) return;

 for (int i = _layerWatch.Count - 1; i >= 0; i--)
 {
 Canvas c = _layerWatch[i];
 if (c == null) { _layerWatch.RemoveAt(i); continue; }
 if (IsOnUiLayer(c.gameObject.layer)) continue;
 EnsureUiLayer(c, "周期复查（被改回非 UI 层）");
 }
 }
 catch { }
 }

#if NEWFIX
 private static bool _graphicsResolved;
 private static System.Reflection.PropertyInfo _graphicsInstanceProp;
 private static System.Reflection.FieldInfo _graphicsField;

 /// <summary>
 /// 【NEWFIX】拿 Unity 引擎自己的画布表（`GraphicRegistry` 内部的字典）。
 /// VRGIN 的 `CatchCanvas` 就是 `_Graphics.GetValue(GraphicRegistry.instance)` 拿的同一张表，
 /// 所以这条反射路径在本体上是可用的；拿不到就返回 null（退回保修表方案）。
 /// </summary>
 private static System.Collections.IDictionary GetEngineCanvasDict()
 {
 try
 {
 if (!_graphicsResolved)
 {
 _graphicsResolved = true;
 Type gr = VRRef.FindType("UnityEngine.UI.GraphicRegistry");
 if (gr != null)
 {
 _graphicsInstanceProp = gr.GetProperty("instance",
 BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
 _graphicsField = gr.GetField("m_Graphics",
 BindingFlags.Instance | BindingFlags.NonPublic);
 }
 if (KKCharaStudioVRFixPlugin.DiagOn)
 {
 FixLog.Key("【NEWFIX】引擎画布表 = "
 + ((_graphicsInstanceProp != null && _graphicsField != null)
 ? "可用 → 不再做定时全表扫描" : "不可用 → 退回保修表方案"));
 }
 }
 if (_graphicsInstanceProp == null || _graphicsField == null) return null;
 object inst = _graphicsInstanceProp.GetValue(null, null);
 if (inst == null) return null;
 return _graphicsField.GetValue(inst) as System.Collections.IDictionary;
 }
 catch { return null; }
 }
#endif

#if !LEAN
 // 【精简版瘦身】诊断输出。
 internal static string DescribeLayerWatch()
 {
 return "UI 图层保修：登记 " + _layerWatch.Count + " 个画布，累计修正 " + _layerFixTotal + " 个节点";
 }
#else
 // 【LEAN】诊断输出整段删除（只给完整版）
#endif

 /// <summary>
 /// 零分配地检查一个 Transform 的**直接子节点**（递归）里
 /// 是否有人在 UI 图层。用于在 GetComponentsInChildren 之前快速排除干净子树。
 /// 注意：这里也用了 childCount/GetChild，但**不分配数组**，比
 /// GetComponentsInChildren 便宜得多；而且一旦发现就立即返回。
 /// </summary>
 private static bool HasAnyChildOnUILayer(Transform t)
 {
 int n = t.childCount;
 for (int i = 0; i < n; i++)
 {
 Transform child = t.GetChild(i);
 if (child == null) continue;
 if (child.gameObject.layer == _uiLayer) return true;
 if (HasAnyChildOnUILayer(child)) return true;
 }
 return false;
 }

 /// <summary>已经接管过的 Canvas（按实例）。避免重复处理、避免和游戏抢状态。</summary>
 private static readonly HashSet<int> _taken = new HashSet<int>();

 // ---------------- 扫描节流 ----------------
 //
 // RunOnce() 每 1 秒做一次 Resources.FindObjectsOfTypeAll<Canvas>()。
 // 这是**引擎级全表扫描**：遍历内存里所有 UnityEngine.Object 并分配一个数组。
 // 稳态下 Canvas 的数量/名字/renderMode 几乎不变，扫出来的结论永远是
 // "全部已接管，0 个新增" —— 纯粹白烧 CPU + 制造 GC 压力。
 //
 // 现在的策略：
 // · 基础周期 1.0s → 3.0s（接管新 Canvas 的延迟 1s → 3s，人眼无感）；
 // · **连续 EmptyStreakToIdle 次扫描都是 0 新增 → 进入休眠**，
 // 周期放大到 IdleIntervalSeconds（10s）；
 // · 一旦重新接管到任何 Canvas，立刻退回基础周期。
 //
 // 为什么这样安全：需要接管的 Canvas 只在"打开菜单/切面板"时出现，
 // 且 VRGIN 自己会接管它认得的那些 —— 我们只兜"VRGIN 漏掉的"，
 // 晚 3~10 秒接管对观感没有影响。
 private const float CanvasScanIntervalSeconds = 30.0f;
 private const float CanvasScanIdleSeconds = 60.0f;
 private const int EmptyStreakToIdle = 5;

 private static int _emptyScanStreak;
 private static float _lastCanvasScanTime = -999f;

 /// <summary>
 /// 周期性调用（从看门狗）：内部自带节流 + 休眠，调用方可以放心每帧调。
 /// 返回本次接管数量（被节流跳过时返回 0）。
 /// </summary>
 internal static int RunPeriodic(string reason)
 {
#if NEWFIX
 // 【NEWFIX】不再做"定时全表扫描"。
 // 新画布的发现改由 LayerWatchTick 每 0.5 秒读**引擎自己的画布表**完成
 //（VRGIN 的 CatchCanvas 用的就是同一张表 → 这就是"免费的事件源"）。
 return 0;
#else
 // GUI 相机还没解析出来时**不要吃节流、也不要记"空扫描"** ——
 // 否则第一次调用白白烧掉一个 30 秒窗口，用户刚进工作室就打开系统菜单时
 // 画布还没被修（正是"一进去点画面效果看不到"的观感）。
 RefreshCache();
 if (_guiCamera == null) return 0;

 // VR 就绪后的前 60 秒用 3 秒一次的"快速发现"，把窗口压到 3 秒内。
 // 原因：修复完全依赖"先发现画布"这一步。以前要等 30~60 秒的扫描，
 // 而用户可能在扫描之前就打开菜单 → 看起来"有时能看见有时看不见"。
 // 注意：两份构建（完整/精简）在这段逻辑上**完全相同**，差别只是帧率不同
 // → 30 秒扫描的落点相对"你打开菜单的时刻"不同 → 才显得像构建差异。
 if (_vrReadyTime < 0f) _vrReadyTime = Time.realtimeSinceStartup;
 float interval;
 if (Time.realtimeSinceStartup - _vrReadyTime < 60f) interval = 3f;
 else if (_emptyScanStreak >= EmptyStreakToIdle) interval = CanvasScanIdleSeconds;
 else interval = CanvasScanIntervalSeconds;
 if (Time.realtimeSinceStartup - _lastCanvasScanTime < interval) return 0;
 _lastCanvasScanTime = Time.realtimeSinceStartup;

 int n = RunOnce(reason);

 if (n == 0) _emptyScanStreak++;
 else _emptyScanStreak = 0;
 return n;
#endif
 }

 /// <summary>加载结束后强制立刻扫一次（不吃节流），并重置休眠。</summary>
 internal static int RunOnceForce(string reason)
 {
 _lastCanvasScanTime = Time.realtimeSinceStartup;
 int n = RunOnce(reason);
 _emptyScanStreak = (n == 0) ? _emptyScanStreak + 1 : 0;
 return n;
 }

 /// <summary>
 /// 绝对不能接管的名称片段（不区分大小写）。
 /// 这些要么是手柄/控制器自身的渲染物，要么是 0.0.3 和 Studio 自己管理的 3D 物件。
 /// </summary>
 private static readonly string[] _denyNames =
 {
 "controller", "hand", "pointer", "cursor", "laser", "crosshair",
 "aim", "reticle", "grip", "marker", "handle", "grab",
 "guide", "axis", "moveable", "movecanvas", "ik", "fk",
 "new game object", "load work", "frame cap", "autosave",
 "guiquad", "subguiquad", "vrgui", "vrgincamera"
 };

#if !LEAN
 // 【精简版瘦身】诊断输出。
 internal static void ResetCache()
 {
 _guiCamera = null;
 _guiCameraSearched = false;
 _guiTexture = null;
 _uiLayerMask = 0;
 _uiLayer = -1;
 _taken.Clear();
 }
#else
 // 【LEAN】诊断输出整段删除（只给完整版）
#endif

 private static void RefreshCache()
 {
 // 图层信息必须在**这里**先初始化，不能跟在下面那段
 // "相机缓存命中就早退"的后面。
 //
 // 事故链（2026-10-18 两份日志对照）：
 // · 第一次 RefreshCache 时 `VRGUI.Instance` 已经有了、`_VRGUICamera` 也读到了，
 // 但 `VR.Context` 还没就绪 → 设置图层的那段 try 抛异常被 catch 吞掉
 // → `_uiLayerMask` 留在 -1、`_uiLayer` 留在 -2；
 // · 之后每次进来都被 `if (_guiCameraSearched && _guiCamera != null) return;` **早退**
 // → 图层信息**永远**停在未初始化；
 // · 于是 `EnsureUiLayer` 里 `if (_uiLayer < 0 || _uiLayerMask <= 0) return 0;`
 // 每次静默返回 0 → 图层保修**完全没生效**，而且一个字都不打。
 // 两份日志的差别正好是这次"抢跑"：会话 1（13:45）保修零输出、菜单始终看不见；
 // 会话 2（13:49）保修正常输出、菜单修好。所以这不是"玄学"，是初始化竞态。
 if (_uiLayer < 0 || _uiLayerMask <= 0) RefreshLayerInfo();

 if (_guiCameraSearched && _guiCamera != null) return;

 _guiCameraSearched = true;
 _guiCamera = null;
 _guiTexture = null;

 try
 {
 VRGUI gui = VRGUI.Instance; // public static
 if (gui == null) return;

 FieldInfo f = typeof(VRGUI).GetField("_VRGUICamera",
 BindingFlags.Instance | BindingFlags.NonPublic);
 if (f != null) _guiCamera = f.GetValue(gui) as Camera;

 _guiTexture = gui.uGuiTexture; // public property
 }
 catch (Exception e)
 {
 FixLog.Debug("读取 VRGUI 内部状态失败: " + e.Message);
 }
 }

 /// <summary>
 /// 单独拿 VR 的 UI 层信息（层号 + 掩码）。拿到才算成功；没拿到就每次 RefreshCache 都再试一次
 /// —— 绝不把"没拿到"当成"拿到全部层"。
 /// </summary>
 private static void RefreshLayerInfo()
 {
 try
 {
 if (VR.Context == null) return; // VR 还没起来，下次再试
 string layerName = VR.Context.UILayer;
 int layer = LayerMask.NameToLayer(layerName);
 int mask = VR.Context.UILayerMask;
 if (layer < 0 || mask <= 0) return;
 if (layer == _uiLayer && mask == _uiLayerMask) return;

 _uiLayer = layer;
 _uiLayerMask = mask;
 _layerWarned = false;
 if (KKCharaStudioVRFixPlugin.DiagOn)
 FixLog.Key("【UI 图层】VR 的 UI 层 = " + layerName + "（层号 " + layer
 + "，掩码 " + mask + "）—— 图层保修据此生效。");
 }
 catch { }
 }

 /// <summary>名称是否命中黑名单。</summary>
 private static bool IsDenied(string name)
 {
 if (string.IsNullOrEmpty(name)) return false;
 for (int i = 0; i < _denyNames.Length; i++)
 if (name.IndexOf(_denyNames[i], StringComparison.OrdinalIgnoreCase) >= 0)
 return true;
 return false;
 }

 /// <summary>
 /// 这个 Canvas 是否"确定是纯 UI"（可以安全地把整棵子树搬到 UI 图层）。
 ///
 /// 判定条件（全部满足）：
 /// · 自身已经是 UI 图层，或者名字/父级表明是游戏 UI（Canvas/Cvs/Menu/Panel 等）；
 /// · 不是黑名单里的东西；
 /// · 相机为 null 或就是 VRGIN 的 GUI 相机（说明它本来就服务于屏幕空间 UI）。
 /// 只要有一项不满足，就**只改 Canvas 自身**，绝不动子树 ——
 /// 这就是 BUG #1「手柄图标被固定到 UI 上」的根治办法。
 /// </summary>
 private static bool IsPureUiCanvas(Canvas c)
 {
 string n = c.gameObject.name;

 // ScreenSpaceCamera + 自己的相机 → 基本就是 UI 相机（不是世界物件）
 if (c.renderMode == RenderMode.ScreenSpaceCamera
 && c.worldCamera != null
 && c.worldCamera != _guiCamera)
 {
 // 别人的 UI 相机，判定为纯 UI
 return true;
 }

 // 父级链里如果有一层是 VRGIN 自己创建的，别动（调用方已过滤，这里是双保险）
 if (IsUnderVrgin(c.transform)) return false;

 // 名字像 UI 的才做子树搬运
 string[] uiHints = { "canvas", "cvs", "menu", "panel", "window", "dialog",
 "hud", "overlay", "ui" }; 
 for (int i = 0; i < uiHints.Length; i++)
 if (n.IndexOf(uiHints[i], StringComparison.OrdinalIgnoreCase) >= 0)
 return true;

 return false;
 }

 private static bool IsUnderVrgin(Transform t)
 {
 int guard = 0;
 while (t != null && guard++ < 64)
 {
 if (t.gameObject.name.IndexOf("VRGIN", StringComparison.OrdinalIgnoreCase) >= 0)
 return true;
 t = t.parent;
 }
 return false;
 }

 /// <summary>把漏掉的 Canvas 交给 VRGIN。返回本次接管数量。</summary>
 /// <summary>
 /// 接管**单个**画布（由 Canvas.OnEnable 钩子在画布出现那一刻调用）。
 /// 取代"每 30 秒全表扫描所有 Canvas"的轮询 —— 新画布一出现就处理，零延迟零扫描。
 /// 返回是否真的接管了它。
 /// </summary>
 /// <summary>
 /// 接管**单个**画布（由 Canvas.OnEnable 钩子在画布出现那一刻调用）。
 /// 取代"每 30 秒全表扫描所有 Canvas"的轮询 —— 新画布一出现就处理，零延迟、零扫描。
 /// 判断口径与 RunOnce 完全一致（逐条复制），只把"遍历所有画布"换成"只看传入的这个"。
 /// </summary>
 internal static bool AdoptCanvasNow(Canvas c)
 {
 try
 {
 if (c == null) return false;
 if (!c.gameObject.activeInHierarchy) return false;
 RefreshCache();
 if (_guiCamera == null) return false;

 string n = c.gameObject.name;
 if (n.IndexOf("VRGIN", StringComparison.OrdinalIgnoreCase) >= 0) return false;
 if (c.renderMode == RenderMode.WorldSpace) return false; // 世界物件，绝不碰
 if (IsDenied(n)) return false;
 if (IsUnderVrgin(c.transform)) return false;
 // 模式/相机已对 → 仍要校一次图层（见【UI 图层保修】）
 if (c.renderMode == RenderMode.ScreenSpaceCamera && c.worldCamera == _guiCamera)
 {
 EnsureUiLayer(c, "OnEnable：已是 GUI 相机的画布");
 AddToLayerWatch(c);
 return false;
 }

 int id = c.gameObject.GetInstanceID();
 if (_taken.Contains(id)) return false;

 bool shouldTake =
 c.renderMode == RenderMode.ScreenSpaceOverlay
 || (c.renderMode == RenderMode.ScreenSpaceCamera && c.worldCamera != _guiCamera);
 if (!shouldTake) return false;

 try
 {
 GameInterpreter interpreter = VR.Interpreter;
 if (interpreter != null && interpreter.IsIgnoredCanvas(c)) return false;
 }
 catch { }

 bool subtree = IsPureUiCanvas(c);
 FixLog.Info("接管漏掉的 UI Canvas [Canvas.OnEnable 事件]: " + n
 + " (renderMode=" + c.renderMode + ", 搬子树图层=" + subtree + ")");

 try
 {
 c.renderMode = RenderMode.ScreenSpaceCamera;
 c.worldCamera = _guiCamera;

 if (_uiLayerMask > 0 && _uiLayer >= 0
 && ((1 << c.gameObject.layer) & _uiLayerMask) == 0)
 {
 c.gameObject.layer = _uiLayer;
 if (subtree)
 {
 Transform[] all = c.gameObject.GetComponentsInChildren<Transform>(true);
 for (int k = 0; k < all.Length; k++)
 {
 if (all[k] == null) continue;
 all[k].gameObject.layer = _uiLayer;
 }
 }
 }
 }
 catch (Exception e) { FixLog.Debug("接管 Canvas " + n + " 失败: " + e.Message); }

 _taken.Add(id);
 return true;
 }
 catch (Exception e) { FixLog.Debug("AdoptCanvasNow 异常: " + e.Message); return false; }
 }

 internal static int RunOnce(string reason)
 {
 try
 {
 RefreshCache();
 if (_guiCamera == null) return 0;

 Canvas[] canvases = Resources.FindObjectsOfTypeAll<Canvas>();
 int taken = 0;
 int repaired = 0; // 图层保修修好的节点数（也算"这一轮有收获"）

 for (int i = 0; i < canvases.Length; i++)
 {
 Canvas c = canvases[i];
 if (c == null) continue;

 // 这里不再因为"没激活"就整条跳过。
 // `Resources.FindObjectsOfTypeAll` 会连**预制体资产**一起返回，所以用 scene 有效性挡掉；
 // 真正属于当前场景的画布，哪怕现在关着（比如系统菜单的 01_Screen Effect 子面板），
 // 也要先把图层校好、并登记进保修表 —— 否则用户打开它的那一刻没人管，
 // 只能干等下一次 30 秒扫描（这正是"有时能看见有时看不见"的来源）。
 if (!c.gameObject.scene.IsValid()) continue; // 跳过 prefab 资产

 string n = c.gameObject.name;

 // VRGIN 自己创建的 GUI 不要动
 if (n.IndexOf("VRGIN", StringComparison.OrdinalIgnoreCase) >= 0) continue;

 // 【关键修正 ①】WorldSpace 是**世界物件**，不是 UI —— 绝不碰。
 // v1.7.0 在这里会把 Studio 的物件预览、手柄相关画布一起收走并永久改层，
 // 造成「手柄图标被固定到 UI 上」+ 每 4 秒一次的重收循环。
 if (c.renderMode == RenderMode.WorldSpace) continue;

 // 【关键修正 ②】黑名单：手柄 / 控制器 / 拖动工具 / 3D 辅助物，绝不碰
 if (IsDenied(n)) continue;
 if (IsUnderVrgin(c.transform)) continue;

 // 已经被 VRGIN 接管的：模式/相机对了，但**图层不一定对**（见文件上方【UI 图层保修】）。
 // 以前这里直接 continue → 那些「模式对了图层错了」的画布永远不会被修 → 菜单永久看不见。
 if (c.renderMode == RenderMode.ScreenSpaceCamera && c.worldCamera == _guiCamera)
 {
 repaired += EnsureUiLayer(c, "扫描：已是 GUI 相机的画布");
 AddToLayerWatch(c);
 continue;
 }

 // 「需要接管」的那条路必须画布是激活的才能判断（解释器忽略名单等）——
 // 没激活的先只保修图层（上面那条分支已经处理过），接管留给它被打开后的扫描。
 if (!c.gameObject.activeInHierarchy) continue;

 // 【关键修正 ③】已经接管过的不再重复处理
 int id = c.gameObject.GetInstanceID();
 if (_taken.Contains(id)) continue;

 // 只处理 VRGIN 会漏掉的那两类（WorldSpace 已在上面排除）
 bool shouldTake =
 c.renderMode == RenderMode.ScreenSpaceOverlay
 || (c.renderMode == RenderMode.ScreenSpaceCamera && c.worldCamera != _guiCamera);
 if (!shouldTake) continue;

 // 解释器明确要求忽略的，别动
 try
 {
 GameInterpreter interpreter = VR.Interpreter;
 if (interpreter != null && interpreter.IsIgnoredCanvas(c)) continue;
 }
 catch { }

 bool subtree = IsPureUiCanvas(c);

 FixLog.Info("接管漏掉的 UI Canvas [" + reason + "]: " + n
 + " (renderMode=" + c.renderMode
 + ", 原图层=" + LayerMask.LayerToName(c.gameObject.layer)
 + ", 搬子树图层=" + subtree + ")");

 try
 {
 c.renderMode = RenderMode.ScreenSpaceCamera;
 c.worldCamera = _guiCamera;

 if (_uiLayerMask > 0 && _uiLayer >= 0
 && ((1 << c.gameObject.layer) & _uiLayerMask) == 0)
 {
 // 【关键修正 ④】默认只改 Canvas 自身。
 // 只有确定是纯 UI 画布时才搬整棵子树 —— 这正是 v1.7.0 闯祸的地方。
 c.gameObject.layer = _uiLayer;

 if (subtree)
 {
 Transform[] all = c.gameObject.GetComponentsInChildren<Transform>(true);
 for (int k = 0; k < all.Length; k++)
 {
 if (all[k] == null) continue;
 all[k].gameObject.layer = _uiLayer;
 }
 }
 }
 }
 catch (Exception e)
 {
 FixLog.Debug("接管 Canvas " + n + " 失败: " + e.Message);
 continue;
 }

 _taken.Add(id);
 AddToLayerWatch(c); // 登记图层保修
 taken++;
 }

 return taken + repaired;
 }
 catch (Exception e)
 {
 FixLog.Debug("GuiCanvasFix 异常: " + e.Message);
 return 0;
 }
 }

 /// <summary>
 /// 把 v1.7.0 误搬到 UI 图层的世界物件还原回 Default(0)。
 ///
 /// v1.7.0 会把 WorldSpace Canvas 的整棵子树改成 UI(5)，且永不还原 ——
 /// 结果手柄上的图标被永久画进了 VR 的 UI 贴图。升级到 后，
 /// 这些残留的层级迁移必须在**本次启动时**修回来，否则老存档/老进程里的
 /// 污染会一直留着。
 ///
 /// 只对"确认是 3D 物件"的东西回滚：Canvas 自身 renderMode == WorldSpace，
 /// 且不在 VRGIN 名下。UI 图层里原本就有的 UI 元素不动。
 /// </summary>
 internal static int UndoStrayLayerMove(string reason)
 {
 int fixedCount = 0;
 try
 {
 RefreshCache();
 if (_uiLayer < 0) return 0;

 Canvas[] canvases = Resources.FindObjectsOfTypeAll<Canvas>();
 int scanned = 0, walked = 0;
 for (int i = 0; i < canvases.Length; i++)
 {
 Canvas c = canvases[i];
 if (c == null) continue;
 if (!c.gameObject.activeInHierarchy) continue;

 // 只回滚世界空间画布（UI 画布本来就该在 UI 图层）
 if (c.renderMode != RenderMode.WorldSpace) continue;

 string n = c.gameObject.name;
 if (n.IndexOf("VRGIN", StringComparison.OrdinalIgnoreCase) >= 0) continue;
 if (IsUnderVrgin(c.transform)) continue;

 scanned++;

 // 先做一次**零分配**的廉价检查：如果画布根节点自己
 // 不在 UI 图层，也**没有任何子物体**在 UI 图层，就不用走整棵子树。
 // 旧版对每个 WorldSpace 画布无条件 GetComponentsInChildren<Transform>(true)，
 // 每次都分配一个新数组并遍历整棵子树 —— 这是每 2 秒一次的主要开销。
 if (c.gameObject.layer != _uiLayer && !HasAnyChildOnUILayer(c.transform))
 continue;

 walked++;
 Transform[] all = c.gameObject.GetComponentsInChildren<Transform>(true);
 int moved = 0;
 for (int k = 0; k < all.Length; k++)
 {
 if (all[k] == null) continue;
 if (all[k].gameObject.layer == _uiLayer)
 {
 all[k].gameObject.layer = 0; // Default
 moved++;
 }
 }

 if (moved > 0)
 {
 FixLog.Info("回滚误搬的世界物件图层 [" + reason + "]: " + n
 + " —— " + moved + " 个节点 UI→Default");
 fixedCount += moved;
 }
 }

#if !LEAN
 _lastStrayScanStats = "扫描 " + scanned + " 个世界画布，其中 " + walked + " 个需要深查";
#endif
 _strayFixedTotal += fixedCount;
 }
 catch (Exception e)
 {
 FixLog.Debug("UndoStrayLayerMove 异常: " + e.Message);
 }
 return fixedCount;
 }

#if !LEAN
 // 【精简版瘦身】诊断输出。
 /// <summary>诊断用：列出所有 Canvas 的接管状态。</summary>
 internal static void DumpCanvases(System.Text.StringBuilder sb)
 {
 RefreshCache();
 sb.AppendLine(" VRGUI._VRGUICamera = " + (_guiCamera == null ? "null" : _guiCamera.name)
 + ", uGuiTexture = " + (_guiTexture == null ? "null" : _guiTexture.name));
 sb.AppendLine(" 本插件已接管 " + _taken.Count + " 个 Canvas。");
 sb.AppendLine(" 上次错层扫描: " + (_lastStrayScanStats == "" ? "(尚未扫描)" : _lastStrayScanStats)
 + "，已修正 " + _strayFixedTotal + " 次");

 Canvas[] canvases = Resources.FindObjectsOfTypeAll<Canvas>();
 sb.AppendLine(" 共 " + canvases.Length + " 个 Canvas：");
 for (int i = 0; i < canvases.Length; i++)
 {
 Canvas c = canvases[i];
 if (c == null) continue;
 bool owned = (c.renderMode == RenderMode.ScreenSpaceCamera && c.worldCamera == _guiCamera);
 sb.AppendLine(" [" + i + "] " + c.gameObject.name
 + " | active=" + c.gameObject.activeInHierarchy
 + " renderMode=" + c.renderMode
 + " worldCamera=" + (c.worldCamera == null ? "null" : c.worldCamera.name)
 + " layer=" + c.gameObject.layer + ":" + LayerMask.LayerToName(c.gameObject.layer)
 + " sortingOrder=" + c.sortingOrder
 + " | VRGIN已接管=" + owned);
 }
 }
#else
 // 【LEAN】诊断输出整段删除（只给完整版）
#endif
 }
}
