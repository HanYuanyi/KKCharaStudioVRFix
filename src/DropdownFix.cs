using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using VRGIN.Core;

namespace KKCharaStudioVRFix
{
 /// <summary>
 /// 修复「下拉菜单在 VR 里看不见」（MaterialEditor 的菜单、画面效果里的滤镜下拉等）。
 ///
 /// 原理：uGUI 的 `Dropdown.Show()` 会在**运行时动态创建**两个 GameObject：
 /// m_Dropdown —— 弹出的选项列表
 /// m_Blocker —— 点击遮罩
 /// 它们是新建的、不在 VRGIN 的 VRGUI 注册表里，所以 VRGIN 的 CatchCanvas()
 /// 不会把它们挪到 VR 的 UI 图层 → 它们留在 VR 相机不渲染的图层上 → **看不见**；
 /// 但 GraphicRaycaster 仍然工作 → **能点到**。
 ///
 /// 这与「系统菜单右侧面板看不见」是同一类问题，但那条路径（GuiCanvasFix）是按 Canvas 处理的，
 /// 而 Dropdown 的弹出列表是**嵌套 Canvas**，需要单独在 Show() 之后处理。
 ///
 /// 做法照搬官方维护版 KK_VR-0.4.0 的 DropdownFixHook：
 /// 在 Dropdown.Show() 之后把 m_Dropdown 和 m_Blocker 整个挪到 VR.Context.UILayer。
 /// </summary>
 internal static class DropdownFix
 {
 private static FieldInfo _blockerField;
 private static FieldInfo _dropdownField;

 internal static void Install(HarmonyLib.Harmony harmony)
 {
 try
 {
 MethodInfo target = AccessTools.Method(typeof(Dropdown), "Show", Type.EmptyTypes);
 if (target == null)
 {
 FixLog.Warn("未找到 Dropdown.Show()，下拉菜单修复未启用。");
 return;
 }

 _blockerField = typeof(Dropdown).GetField("m_Blocker",
 BindingFlags.NonPublic | BindingFlags.Instance);
 _dropdownField = typeof(Dropdown).GetField("m_Dropdown",
 BindingFlags.NonPublic | BindingFlags.Instance);

 harmony.Patch(target, postfix: new HarmonyMethod(
 AccessTools.Method(typeof(DropdownFix), "ShowPostfix")));

 FixLog.Info("已挂载钩子: Dropdown.Show (下拉菜单修复)");
 }
 catch (Exception e)
 {
 FixLog.Warn("挂载 Dropdown.Show 失败: " + e.Message);
 }
 }

 /// <summary>Dropdown.Show() 之后：把动态创建的弹出列表和遮罩挪到 VR 的 UI 图层。</summary>
 public static void ShowPostfix(Dropdown __instance)
 {
 try
 {
 // 平面模式（VRGIN 未启动）直接跳过：VR.Context 为 null，
 // 且平面模式本来就不需要把 UI 挪到 VR 图层。
 try { if (!VR.Active) return; }
 catch { return; }

 // 去掉配置项门控 —— BUG 修好后就是固定机制，永久生效。
 // 精简版曾出现"画面效果菜单看不见"，配置读取是嫌疑之一，直接不再依赖它。
 int uiLayer = LayerMask.NameToLayer(VR.Context.UILayer);
 if (uiLayer < 0)
 {
 FixLog.Warn("拿不到 VR 的 UI 图层名（" + VR.Context.UILayer + "），下拉菜单修复跳过。");
 return;
 }

 int fixedCount = 0;
 fixedCount += ChangeLayer(GetField(_blockerField, __instance), uiLayer, "遮罩");
 fixedCount += ChangeLayer(GetField(_dropdownField, __instance), uiLayer, "弹出列表");

 if (fixedCount > 0)
 FixLog.Info("下拉菜单已挪到 VR UI 图层（" + fixedCount + " 个对象）。");
 }
 catch (Exception e)
 {
 FixLog.Debug("DropdownFix 异常: " + e.Message);
 }
 }

 private static GameObject GetField(FieldInfo f, object instance)
 {
 if (f == null || instance == null) return null;
 try { return f.GetValue(instance) as GameObject; }
 catch { return null; }
 }

 private static int ChangeLayer(GameObject go, int layer, string label)
 {
 if (go == null) return 0;
 try
 {
 go.layer = layer;
 Transform[] children = go.GetComponentsInChildren<Transform>(true);
 for (int i = 0; i < children.Length; i++)
 children[i].gameObject.layer = layer;

 FixLog.Debug("下拉菜单" + label + "已挪到图层 " + layer + ": " + go.name);
 return 1;
 }
 catch (Exception e)
 {
 FixLog.Debug("挪下拉菜单" + label + "失败: " + e.Message);
 return 0;
 }
 }
 }
}
