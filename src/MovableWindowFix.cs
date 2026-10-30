using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;

namespace KKCharaStudioVRFix
{
 /// <summary>
 /// 修复第三方 uGUI 窗口（MaterialEditor 等）在 VR 里拖动时「瞬移到固定位置、只能单方向拖」。
 ///
 /// 以前两版都**打错了靶子**：探针/patch 只找「第一个含
 /// UILib.MovableWindow 的程序集」。而实测**至少 5 个程序集各自定义了这个类**
 /// （KK_MaterialEditor、KKPE、KKUS、KK_CoordinateLoadOption、Timeline …），
 /// AppDomain 里的顺序决定谁被选中 —— 结果 patch 到了别的插件那份，ME 的窗口**从未被修正**，
 /// 探针也一条日志都没打（这正是「装了探针但零输出」的原因，是硬证据）。
 /// 本版改为**遍历所有程序集，逐个 patch**。
 ///
 /// 反编译事实（KK_MaterialEditor.dll / UILib.UIUtility.MakeObjectDraggable）：
 /// MovableWindow m = clickableDragZone.gameObject.AddComponent&lt;MovableWindow&gt;();
 /// m.ToDrag = draggableObject; m.PreventDragout = ...;
 /// 而 UILib.MovableWindow 的公式是：
 /// OnPointerDown: CachedDragPosition = ToDrag.position（**世界坐标**）
 /// CachedMousePosition = Input.mousePosition（**屏幕坐标**）
 /// OnDrag: ToDrag.position = CachedDragPosition + (Input.mousePosition - CachedMousePosition)
 /// if (PreventDragout) Clamp(val.x, 0, Screen.width) / Clamp(val.y, ...)
 ///
 /// 平面模式（ScreenSpaceOverlay）下 RectTransform.position **就是屏幕坐标**，所以原公式成立。
 /// 但在 VR 里，本插件的 GuiCanvasFix 会把这些画布接管成 **ScreenSpaceCamera**
 /// （世界坐标）→ 「世界坐标 + 屏幕像素增量」= 无意义的混合坐标 → 窗口瞬移；
 /// 再用 Screen.width 去夹世界坐标 → 数值被钉在左/下边界 → 只能往右、往上拖。
 ///
 /// 修法（**只改 ScreenSpaceCamera 的情况**，Overlay 一律不碰，避免破坏平面模式）：
 /// 按下时记下「窗口的屏幕坐标」和「鼠标的屏幕坐标」，以及拖动期间不会变的东西
 /// （Canvas / CanvasRect / 相机 / PreventDragout / 窗口像素宽高）；
 /// 拖动时用纯屏幕坐标算新位置（并按原语义在**屏幕空间**做 PreventDragout 夹取），
 /// 最后用 ScreenPointToWorldPointInRectangle 转回世界坐标写回。
 ///
 /// 按用户要求，**不再做任何拖动耗时测量/日志**。
 /// 实测 `rt.position = world` 本身就要 3.8ms/次（ToDrag 的子树有 3560 个节点），
 /// 而且平面模式（不启 VR）拖同样的窗口也一样卡 —— 属 Unity/uGUI 侧开销，
 /// 不属于本插件的修复范畴。所以这里只保留"把窗口拖到对的位置"这一件事。
 /// </summary>
 internal static class MovableWindowFix
 {
 private static bool _installed;
 private static readonly List<string> _patched = new List<string>();
 private static readonly Dictionary<int, DragState> _states = new Dictionary<int, DragState>();
 private static readonly Dictionary<Type, FieldInfo> _toDragCache = new Dictionary<Type, FieldInfo>();
 private static readonly Dictionary<Type, FieldInfo> _preventCache = new Dictionary<Type, FieldInfo>();
 private static readonly Vector3[] _cornersBuf = new Vector3[4]; // 复用，避免每次按下分配数组

 private class DragState
 {
 public Vector2 WindowScreen; // 按下时：窗口的屏幕坐标
 public Vector2 MouseScreen; // 按下时：鼠标的屏幕坐标
 public Canvas Canvas;
 public RectTransform CanvasRect;
 public Camera Cam;
 public bool PreventDragout;
 public float WinW, WinH; // 窗口像素宽高（拖动中不变）
 }

 internal static void Install(HarmonyLib.Harmony harmony)
 {
 if (_installed) return;
 try
 {
 foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
 {
 if (asm == null) continue;
 Type t = null;
 try { t = asm.GetType("UILib.MovableWindow", false); } catch { }
 if (t == null) continue;

 MethodInfo down = t.GetMethod("OnPointerDown",
 BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
 MethodInfo drag = t.GetMethod("OnDrag",
 BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
 if (down == null || drag == null) continue;

 try
 {
 harmony.Patch(down, postfix: new HarmonyLib.HarmonyMethod(
 AccessTools.Method(typeof(MovableWindowFix), "DownPostfix")));
 harmony.Patch(drag, postfix: new HarmonyLib.HarmonyMethod(
 AccessTools.Method(typeof(MovableWindowFix), "DragPostfix")));
 _patched.Add(asm.GetName().Name);
 }
 catch (Exception e) { FixLog.Debug("patch " + asm.GetName().Name + " 失败: " + e.Message); }
 }

 _installed = _patched.Count > 0;
 if (KKCharaStudioVRFixPlugin.DiagOn)
 {
 if (_installed)
 FixLog.Key("【窗口拖动修复】已对 " + _patched.Count
 + " 个程序集的 UILib.MovableWindow 挂上坐标修正：" + Join(_patched));
 else
 FixLog.Warn("【窗口拖动修复】没有找到任何 UILib.MovableWindow —— 无法修正。");
 }
 }
 catch (Exception e) { FixLog.Debug("MovableWindowFix 安装失败: " + e.Message); }
 }

 /// <summary>周期重试：第三方 DLL（KK_MaterialEditor 等）可能在插件 Awake 时还没加载。</summary>
 private static float _lastTry = -999f;
 internal static void TryInstall()
 {
 if (_installed) return;
 if (Time.realtimeSinceStartup - _lastTry < 10f) return;
 _lastTry = Time.realtimeSinceStartup;
 Install(new HarmonyLib.Harmony(KKCharaStudioVRFixPlugin.Guid + ".movablewin.auto"));
 }

 private static string Join(List<string> l)
 {
 System.Text.StringBuilder sb = new System.Text.StringBuilder();
 for (int i = 0; i < l.Count; i++) { if (sb.Length > 0) sb.Append(", "); sb.Append(l[i]); }
 return sb.ToString();
 }

 /// <summary>按类型缓存字段（避免每次按下都 GetField）。</summary>
 private static FieldInfo CachedField(Dictionary<Type, FieldInfo> cache, Type t, string name)
 {
 FieldInfo f;
 if (cache.TryGetValue(t, out f)) return f;
 try { f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public); }
 catch { f = null; }
 cache[t] = f;
 return f;
 }

 private static RectTransform GetToDrag(object instance)
 {
 try
 {
 if (instance == null) return null;
 FieldInfo f = CachedField(_toDragCache, instance.GetType(), "ToDrag");
 return f == null ? null : f.GetValue(instance) as RectTransform;
 }
 catch { return null; }
 }

 private static bool ReadPreventDragout(object instance)
 {
 try
 {
 if (instance == null) return false;
 FieldInfo f = CachedField(_preventCache, instance.GetType(), "PreventDragout");
 return f != null && (bool)f.GetValue(instance);
 }
 catch { return false; }
 }

 /// <summary>窗口在屏幕上的像素宽高（用于 PreventDragout 的屏幕空间夹取）。</summary>
 private static void GetWindowScreenSize(RectTransform rt, Camera cam, out float w, out float h)
 {
 w = 0f; h = 0f;
 try
 {
 rt.GetWorldCorners(_cornersBuf); // 0=左下 1=左上 2=右上 3=右下
 Vector2 bl = RectTransformUtility.WorldToScreenPoint(cam, _cornersBuf[0]);
 Vector2 tr = RectTransformUtility.WorldToScreenPoint(cam, _cornersBuf[2]);
 w = Mathf.Abs(tr.x - bl.x);
 h = Mathf.Abs(tr.y - bl.y);
 }
 catch { }
 }

 /// <summary>按下：记下窗口/鼠标屏幕坐标，并把拖动期间不变的东西一次算好。</summary>
 public static void DownPostfix(object __instance, PointerEventData eventData)
 {
 try
 {
 if (__instance == null) return;
 RectTransform rt = GetToDrag(__instance);
 if (rt == null) return;
 Canvas canvas = rt.GetComponentInParent<Canvas>();
 if (canvas == null || canvas.renderMode != RenderMode.ScreenSpaceCamera) return;
 Camera cam = canvas.worldCamera;
 if (cam == null) return;

 DragState st = new DragState();
 st.WindowScreen = RectTransformUtility.WorldToScreenPoint(cam, rt.position);
 st.MouseScreen = (eventData != null) ? eventData.position : (Vector2)Input.mousePosition;
 st.Canvas = canvas;
 st.CanvasRect = canvas.transform as RectTransform;
 st.Cam = cam;
 st.PreventDragout = ReadPreventDragout(__instance);
 GetWindowScreenSize(rt, cam, out st.WinW, out st.WinH);

 if (_states.Count > 32) _states.Clear();
 _states[__instance.GetHashCode()] = st;
 }
 catch { }
 }

 /// <summary>拖动：用纯屏幕坐标重算位置，再转回世界坐标写回。</summary>
 public static void DragPostfix(object __instance, PointerEventData eventData)
 {
 try
 {
 if (__instance == null) return;

 DragState st;
 if (!_states.TryGetValue(__instance.GetHashCode(), out st)) return;
 if (st == null || st.Canvas == null || st.Cam == null) return;

 RectTransform rt = GetToDrag(__instance);
 if (rt == null) return;

 Vector2 mouseNow = (eventData != null) ? eventData.position : (Vector2)Input.mousePosition;
 Vector2 newScreen = st.WindowScreen + (mouseNow - st.MouseScreen);

 // 与原语义一致：PreventDragout 时把窗口限制在屏幕内 —— 但必须在**屏幕空间**夹取
 //（原代码拿 Screen.width 去夹世界坐标，才会出现「只能往右/往上拖」）。
 if (st.PreventDragout)
 {
 float w = st.WinW, h = st.WinH;
 if (w > 1f && w < Screen.width)
 newScreen.x = Mathf.Clamp(newScreen.x, w * 0.5f, Screen.width - w * 0.5f);
 if (h > 1f && h < Screen.height)
 newScreen.y = Mathf.Clamp(newScreen.y, h * 0.5f, Screen.height - h * 0.5f);
 }

 Vector3 world;
 if (RectTransformUtility.ScreenPointToWorldPointInRectangle(
 st.CanvasRect, newScreen, st.Cam, out world))
 {
 rt.position = world;
 }
 }
 catch { }
 }
 }
}
