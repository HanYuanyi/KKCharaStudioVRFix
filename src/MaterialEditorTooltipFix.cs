using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace KKCharaStudioVRFix
{
 /// <summary>
 /// 【2026-10-24】修「ME（KK_MaterialEditor）的悬浮说明窗口飘在场景里 / 不跟着窗口走」。
 ///
 /// 机制（反编译 KK_MaterialEditor → MaterialEditorAPI.TooltipManager，**原文**）：
 /// private void Update() {
 /// if (MaterialEditorPluginBase.Showtooltips.Value) {
 /// Vector3 v = Input.mousePosition / panelTransform.localScale.x;
 /// panelTransform.position = new Vector3(v.x + 5f, v.y + 5f, v.z);
 /// }
 /// }
 ///
 /// 它把**真实鼠标的屏幕坐标**直接当**世界坐标**写进 `transform.position`。
 /// 这个做法只在 ScreenSpaceOverlay 画布上"碰巧成立"（那时屏幕坐标≈画布坐标）。
 /// VR 里 ME 窗口是 **ScreenSpaceCamera** 画布（VRGIN 把它渲染成贴图给头显看），
 /// 于是这个面板被摆到世界坐标 (鼠标x, 鼠标y) 上 —— 离窗口十万八千里，飘在场景里；
 /// 而且它每帧都被**写死在世界坐标**，所以窗口怎么拖它都不会跟。
 /// （用户 2026-10-24 截图：窗口左下的黑色 "Renderable elem…" 框就是它。）
 ///
 /// 修法（比原版更正确，且不依赖鼠标）：
 /// · `Tooltip.OnPointerEnter/OnPointerExit` → 记住/清掉**被悬停的那个控件**；
 /// · VR 下**接管** `TooltipManager.Update` 的定位 → 贴着该控件左下角外侧摆，
 /// 并把结果**夹在 ME 窗口矩形内**（不会跑到窗口外面去）；
 /// · 平面模式一律放行原逻辑（那里 Input.mousePosition 是对的）。
 ///
 /// 成本：面板隐藏时 `_hovered == null` → 一次判空返回；只有真的悬停时才算一次位置。
 /// 没有任何每帧分配（四角缓冲复用），也没有新增定时器。
 /// </summary>
 internal static class MaterialEditorTooltipFix
 {
 private const string ManagerTypeName = "MaterialEditorAPI.TooltipManager";
 private const string TooltipTypeName = "MaterialEditorAPI.Tooltip";

 private static Type _managerType;
 private static Type _tooltipType;
 private static FieldInfo _panelField;
 private static bool _installed;
 private static bool _failed;

 /// <summary>当前被悬停的控件（由 Tooltip.OnPointerEnter/Exit 写入）。</summary>
 private static RectTransform _hovered;

 /// <summary>复用缓冲，避免每帧 new Vector3[4]（Unity 5.6 非分代 GC，本项目红线）。</summary>
 private static readonly Vector3[] _corners = new Vector3[4];

 /// <summary>已经改成"不吃射线"的那个面板实例（按实例记录，面板被重建时会重新处理一次）。</summary>
 private static RectTransform _raycastPanel;

 /// <summary>
 /// 面板引用缓存。
 /// 【为什么要缓存】`TooltipManager.Update` 是**每帧**都跑的，而取 `panelTransform` 要走反射
 /// （`FieldInfo.GetValue`）。ME 是单例（`Instance`），面板也只有一个 → 缓存一次就够，
 /// 稳态下这里**一次反射都不做**。只有面板被销毁（Unity fake-null）时才重读一次。
 /// </summary>
 private static object _mgrCache;
 private static RectTransform _panelCache;

 private static RectTransform PanelOf(object mgr)
 {
 if (ReferenceEquals(mgr, _mgrCache) && _panelCache != null) return _panelCache;
 _mgrCache = mgr;
 try
 {
 _panelCache = _panelField != null
 ? _panelField.GetValue(mgr) as RectTransform : null;
 }
 catch { _panelCache = null; }
 return _panelCache;
 }

 /// <summary>
 /// 第三方 DLL 可能比我们晚加载 → 与窗口拖动修复同一套"低频重试"节奏。
 /// 装上后 `_installed = true`，此方法立刻变成一次 bool 判断。
 /// </summary>
 private static float _lastTry = -999f;

 internal static void TryInstall()
 {
 if (_installed || _failed) return;
 if (Time.realtimeSinceStartup - _lastTry < 10f) return;
 _lastTry = Time.realtimeSinceStartup;

 try
 {
 if (_managerType == null)
 {
 _managerType = VRRef.FindType(ManagerTypeName);
 _tooltipType = VRRef.FindType(TooltipTypeName);
 if (_managerType == null || _tooltipType == null) return; // 还没加载 → 下次再来

 _panelField = _managerType.GetField("panelTransform",
 BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
 if (_panelField == null)
 {
 _failed = true;
 FixLog.Warn("【ME 说明窗口】找不到 TooltipManager.panelTransform —— 修复不可用。");
 return;
 }
 }

 MethodInfo upd = _managerType.GetMethod("Update",
 BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
 null, Type.EmptyTypes, null);
 MethodInfo enter = _tooltipType.GetMethod("OnPointerEnter",
 BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
 MethodInfo exit = _tooltipType.GetMethod("OnPointerExit",
 BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

 if (upd == null || enter == null || exit == null)
 {
 _failed = true;
 FixLog.Warn("【ME 说明窗口】找不到 Update/OnPointerEnter/OnPointerExit —— 修复不可用。");
 return;
 }

 HarmonyLib.Harmony h = new HarmonyLib.Harmony(KKCharaStudioVRFixPlugin.Guid + ".metooltip");
 h.Patch(upd, prefix: new HarmonyMethod(
 AccessTools.Method(typeof(MaterialEditorTooltipFix), "UpdatePrefix")));
 h.Patch(enter, postfix: new HarmonyMethod(
 AccessTools.Method(typeof(MaterialEditorTooltipFix), "EnterPostfix")));
 h.Patch(exit, postfix: new HarmonyMethod(
 AccessTools.Method(typeof(MaterialEditorTooltipFix), "ExitPostfix")));

 // 校验：不许把"没抛异常"当成功（本项目硬规矩）
 _installed = Patched(upd, "UpdatePrefix")
 && Patched(enter, "EnterPostfix")
 && Patched(exit, "ExitPostfix");

 if (KKCharaStudioVRFixPlugin.DiagOn)
 FixLog.Key("【ME 说明窗口】定位接管 = "
 + (_installed ? "已挂上（改为贴着被悬停的控件摆，不再用真实鼠标坐标）" : "未挂上"));
 }
 catch (Exception e) { FixLog.Debug("MaterialEditorTooltipFix.TryInstall 异常: " + e.Message); }
 }

 private static bool Patched(MethodInfo m, string name)
 {
 try
 {
 HarmonyLib.Patches info = HarmonyLib.Harmony.GetPatchInfo(m);
 if (info == null) return false;
 if (info.Prefixes != null)
 {
 foreach (HarmonyLib.Patch p in info.Prefixes)
 if (p != null && p.PatchMethod != null && p.PatchMethod.Name == name) return true;
 }
 if (info.Postfixes != null)
 {
 foreach (HarmonyLib.Patch p in info.Postfixes)
 if (p != null && p.PatchMethod != null && p.PatchMethod.Name == name) return true;
 }
 }
 catch { }
 return false;
 }

 // ======================= Harmony 回调 =======================

 /// <summary>悬停开始：记住控件（后面每帧拿它当锚点）。</summary>
 public static void EnterPostfix(object __instance)
 {
 try
 {
 Component c = __instance as Component;
 _hovered = c != null ? c.transform as RectTransform : null;
 }
 catch { _hovered = null; }
 }

 /// <summary>悬停结束：只在"退出的就是当前锚点"时清掉，避免两只手/两个控件互相打架。</summary>
 public static void ExitPostfix(object __instance)
 {
 try
 {
 Component c = __instance as Component;
 if (c != null && ReferenceEquals(c.transform, _hovered)) _hovered = null;
 }
 catch { }
 }

 /// <summary>
 /// 接管 `TooltipManager.Update`。
 /// 返回 false = 不执行原方法（原方法会把面板写到鼠标的世界坐标上）。
 /// </summary>
 public static bool UpdatePrefix(object __instance)
 {
 // 平面模式：屏幕坐标≈画布坐标，原逻辑是对的 → 放行，绝不改动平面行为
 if (!VRRef.VrRunning) return true;

 try
 {
 RectTransform panel = PanelOf(__instance);
 if (panel == null) return false;

 // ★★ 2026-10-28 修「闪烁」：把说明面板改成**不吃射线**（只做一次）。
 //
 // 闪烁的真正机制（不是位置算错）：
 // 面板是 Image + Text，`raycastTarget` 默认 true，所以它会**挡住指针**。
 // 一旦面板被摆到指针下面，指针就从"控件"变成"进了面板" →
 // Tooltip.OnPointerExit → SetActive(false) → 面板隐藏 →
 // 指针又落回控件 → OnPointerEnter → SetActive(true) → 面板出现 →
 // 指针又进了面板 …… 无限循环 = 用户看到的"不停闪"。
 // 这也解释了为什么"只有部分控件闪、最左侧的大分类标题永远不闪"：
 // 分类标题在列表顶部，说明框能老老实实摆在下方；
 // 靠下的控件下方没空间 → 位置被夹回窗口内 → 正好盖在指针/控件身上 → 闪。
 // 修法：面板只用来显示，永远不该接收点击 → raycastTarget = false，循环直接断掉。
 // 按**实例**记录（而不是一个全局 bool）：万一 ME 重建了窗口/面板，新面板会被重新处理一次。
 if (!ReferenceEquals(panel, _raycastPanel))
 {
 if (DisableRaycast(panel)) _raycastPanel = panel;
 }

 RectTransform target = _hovered;
 if (target == null || !target.gameObject.activeInHierarchy) return false;

 float pw = panel.rect.width * Mathf.Abs(panel.lossyScale.x);
 float ph = panel.rect.height * Mathf.Abs(panel.lossyScale.y);

 float sy = Mathf.Abs(target.lossyScale.y);
 if (sy <= 0f) sy = 1f;
 float half = target.rect.height * 0.5f * sy;
 float bottomOfTarget = target.position.y - half;
 float topOfTarget = target.position.y + half;

 float x = target.position.x;
 float y = bottomOfTarget - 2f - ph; // 面板**底边**（pivot 在左下角）→ 摆在控件下方

 RectTransform win = panel.parent as RectTransform;
 if (win != null && pw > 0f && ph > 0f)
 {
 win.GetWorldCorners(_corners); // 0=左下 1=左上 2=右上 3=右下
 float minX = _corners[0].x, maxX = _corners[2].x;
 float minY = _corners[0].y, maxY = _corners[2].y;

 // 下方放不下 → **改摆到控件上方**（绝不夹到控件身上，那会盖住指针）
 if (y < minY) y = topOfTarget + 2f;
 // 上方也放不下 → 最后才夹在窗口内
 if (y + ph > maxY) y = maxY - ph;
 if (y < minY) y = minY;

 if (x + pw > maxX) x = maxX - pw;
 if (x < minX) x = minX;
 }

 panel.position = new Vector3(x, y, target.position.z);
 }
 catch { }

 return false;
 }

 /// <summary>
 /// 把说明面板整棵子树改成"不吃射线"。面板只负责显示，永远不该抢指针事件。
 /// 一次性调用（之后靠 `_raycastOff` 短路），所以这里的数组分配可以接受。
 /// </summary>
 private static bool DisableRaycast(RectTransform panel)
 {
 try
 {
 Graphic[] gs = panel.GetComponentsInChildren<Graphic>(true);
 if (gs == null || gs.Length == 0) return false;
 for (int i = 0; i < gs.Length; i++)
 if (gs[i] != null) gs[i].raycastTarget = false;
 return true;
 }
 catch { return false; }
 }
 }
}
