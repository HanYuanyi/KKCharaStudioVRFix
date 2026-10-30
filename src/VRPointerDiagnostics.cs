using System;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;
using VRGIN.Core;
using VRGIN.Visuals;

namespace KKCharaStudioVRFix
{
 /// <summary>
 /// VR 指针「为什么不动」与「手柄模型为什么没加载」的诊断。
 ///
 /// 背景（2026-10-15 08:33 日志）：整场 `注入累计=0` —— 0.0.3 的
 /// `MouseOperations.SetClientCursorPosition` **一次都没被调用过**，
 /// 也就是激光→UI 那条链路完全没跑通（用户表现为「VR 光标卡住」）。
 /// 而那条链路的开关在 `GripMenuHandler` 里：
 ///
 /// CheckForNearMenu(): _Target = GUIQuadRegistry.Quads.FirstOrDefault(IsLaserable);
 /// UpdateLaser(): if (_Target != null && IsWithinRange(_Target) && Raycast(_Target, out hit))
 /// → SetClientCursorPosition(hit.textureCoord 换算出的屏幕坐标)
 ///
 /// 所以只要 `_Target` 为 null（注册表空 / 距离不够 / 射线打不中），
 /// 指针就永远不动。本类把这三个环节的结果全部落盘：
 /// · 注册表里有几个面板、每个面板的距离是否在范围内、射线有没有打中、命中点的 textureCoord；
 /// · `_Target` / `LaserVisible` / `mouseDownPosition` / `_ScaleVector` 的实时值；
 /// · 第三方输入模拟器（VR.Input.Mouse）的每次调用（点/移），用来抓「扳机后光标贴左上角」。
 /// 另外把每个 Controller 的渲染模型状态打出来（修「手柄只剩圆盘」）。
 /// </summary>
 internal static class VRPointerDiagnostics
 {
 private static bool _installed;
 private static bool _inputInstalled;
 private static string _lastState = "";
 private static float _lastLogTime = -999f;
 private static float _lastModelLogTime = -999f;
 private static VRGIN.Controls.Controller[] _ctrlCache;
 private static float _ctrlCacheTime = -999f;
 private static string _lastModelState = "";
 private static float _lastInputLogTime = -999f;
 private static int _rayFailCount;
 private static int _rangeFailCount;

 private static Type _handlerType;
 private static FieldInfo _targetField;
 private static FieldInfo _mouseDownField;
 private static FieldInfo _scaleVectorField;
 private static PropertyInfo _laserVisibleProp;

 internal static void Install(HarmonyLib.Harmony harmony)
 {
 if (_installed) return;
 if (!KKCharaStudioVRFixPlugin.DiagOn) return; // 诊断总开关
 try
 {
 _handlerType = VRRef.FindType("KKCharaStudioVR.GripMenuHandler");
 if (_handlerType == null)
 {
 FixLog.Warn("【指针诊断】找不到 KKCharaStudioVR.GripMenuHandler，暂不启用（稍后重试）。");
 return;
 }

 _targetField = _handlerType.GetField("_Target",
 BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
 _mouseDownField = _handlerType.GetField("mouseDownPosition",
 BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
 _scaleVectorField = _handlerType.GetField("_ScaleVector",
 BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
 _laserVisibleProp = _handlerType.GetProperty("LaserVisible",
 BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

 int n = 0;
 n += Patch(harmony, "CheckForNearMenu", null, "NearMenuPostfix");
 n += Patch(harmony, "IsWithinRange", new Type[] { typeof(GUIQuad) }, "WithinRangePostfix");
 n += Patch(harmony, "Raycast", new Type[] { typeof(GUIQuad), typeof(RaycastHit).MakeByRefType() },
 "RaycastPostfix");

 if (n == 0)
 {
 FixLog.Warn("【指针诊断】GripMenuHandler 的方法一个都没挂上，稍后重试。");
 return;
 }
 _installed = true;
 FixLog.Key("【指针诊断】已挂上 " + n + " 个环节（注册表/距离/射线）+ 输入模拟器调用记录"
 + " —— 下次日志会说明「为什么 VR 指针不动」。");
 }
 catch (Exception e)
 {
 FixLog.Warn("【指针诊断】安装失败: " + e.Message);
 }
 }

 /// <summary>
 /// 周期重试安装（由看门狗调用，内部 5 秒节流）。
 /// `GripMenuHandler` 是 0.0.3 在 VR 模式起来后才创建的，插件 Awake 时还不存在，
 /// 所以必须反复重试；已装上后本方法立即返回（不创建 Harmony 实例）。
 /// </summary>
 private static float _lastInstallTry = -999f;
 internal static void TryInstallPeriodic()
 {
 if (!KKCharaStudioVRFixPlugin.DiagOn) return; // 诊断总开关
 if (_installed && _inputInstalled) return;
 if (Time.realtimeSinceStartup - _lastInstallTry < 5f) return;
 _lastInstallTry = Time.realtimeSinceStartup;
 Install(new HarmonyLib.Harmony(KKCharaStudioVRFixPlugin.Guid + ".vrpointer.auto"));
 InstallInputSpy(new HarmonyLib.Harmony(KKCharaStudioVRFixPlugin.Guid + ".vrinputspy.auto"));
 }

 private static int Patch(HarmonyLib.Harmony harmony, string methodName, Type[] args, string patchName)
 {
 try
 {
 MethodInfo target = args == null
 ? AccessTools.Method(_handlerType, methodName)
 : AccessTools.Method(_handlerType, methodName, args);
 if (target == null) { FixLog.Debug("找不到 " + methodName); return 0; }
 harmony.Patch(target, postfix: new HarmonyMethod(
 AccessTools.Method(typeof(VRPointerDiagnostics), patchName)));
 return 1;
 }
 catch (Exception e) { FixLog.Debug("挂 " + methodName + " 失败: " + e.Message); return 0; }
 }

 // ==================== 钩子 ====================

 /// <summary>CheckForNearMenu 之后：把「注册表里有几个面板 + 挑中的目标」打出来。</summary>
 public static void NearMenuPostfix(object __instance)
 {
 if (!KKCharaStudioVRFixPlugin.DiagOn) return;

 if (!KKCharaStudioVRFixPlugin.DiagOn) return; // 诊断关 → 补丁零开销
 PerfProbe.Begin("补丁:GripMenuHandler.CheckForNearMenu");
 try
 {
 // **先限速再干活**。
 // 原来顺序反了：每次都遍历注册表 + 反射读属性 + 拼状态字符串，
 // 最后才判断"要不要打日志" —— 而这个 postfix 是每帧都会跑的
 // （GripMenuHandler.OnUpdate 里 LaserVisible==false 时每帧调 CheckForNearMenu）
 // → 每帧几个字符串 + 一个装箱枚举器 = 白送的 GC 压力。
 if (Time.realtimeSinceStartup - _lastLogTime < 0.5f) return;

 int quads = 0;
 try { foreach (GUIQuad q in GUIQuadRegistry.Quads) quads++; } catch { }

 object target = _targetField != null ? _targetField.GetValue(__instance) : null;
 UnityEngine.Object t = target as UnityEngine.Object; // Unity fake-null 判定
 bool hasTarget = t != null;
 bool laser = false;
 try { laser = _laserVisibleProp != null && (bool)_laserVisibleProp.GetValue(__instance, null); } catch { }

 string state = "quads=" + quads + " target=" + (hasTarget ? t.name + "#" + t.GetInstanceID() : "null")
 + " laserVisible=" + laser;
 if (state == _lastState) return;
 _lastLogTime = Time.realtimeSinceStartup;
 _lastState = state;

 FixLog.Key("【指针目标】注册表面板数=" + quads
 + "；选中目标=" + (hasTarget ? t.name + "（id=" + t.GetInstanceID() + "）" : "**null（没有可打的面板 → 指针永远不会动）**")
 + "；LaserVisible=" + laser
 + "；累计 距离不达标=" + _rangeFailCount + " 次，射线打不中=" + _rayFailCount + " 次"
 + "；VRGUI=" + SafeVrGuiSize());
 }
 catch (Exception e) { FixLog.Debug("NearMenuPostfix 异常: " + e.Message); }
 finally { PerfProbe.End("补丁:GripMenuHandler.CheckForNearMenu"); }
 }

 /// <summary>IsWithinRange 之后：记录"距离不够"的次数（激光打不到面板的头号原因）。</summary>
 public static void WithinRangePostfix(bool __result, GUIQuad quad)
 {
 if (!KKCharaStudioVRFixPlugin.DiagOn) return;

 if (!KKCharaStudioVRFixPlugin.DiagOn) return; // 诊断关 → 补丁零开销
 PerfProbe.Begin("补丁:GripMenuHandler.IsWithinRange");
 try
 {
 if (__result) return;
 _rangeFailCount++;
 if (_rangeFailCount % 300 != 1) return; // 限速
 float d = -1f, range = -1f;
 try
 {
 Transform origin = null;
 try { origin = VR.Camera != null && VR.Camera.SteamCam != null ? VR.Camera.SteamCam.head : null; } catch { }
 if (origin != null && quad != null)
 d = Vector3.Distance(origin.position, quad.transform.position);
 if (quad != null)
 {
 Vector3 s = quad.transform.localScale;
 range = Mathf.Clamp(s.magnitude * 0.25f, 0.25f, 1.25f);
 }
 }
 catch { }
 FixLog.Key("【指针距离】面板 " + (quad == null ? "?" : quad.name) + " 不在激光范围内："
 + "头显到面板 " + d.ToString("0.00") + "m，允许范围 " + range.ToString("0.00")
 + "m（累计 " + _rangeFailCount + " 次）。把手伸近一点或用 F8 把 UI 拉回眼前即可命中。");
 }
 catch { }
 finally { PerfProbe.End("补丁:GripMenuHandler.IsWithinRange"); }
 }

 /// <summary>Raycast 之后：记录"射线打不中"和命中点的 textureCoord。</summary>
 public static void RaycastPostfix(bool __result, GUIQuad quad, ref RaycastHit hit)
 {
 if (!KKCharaStudioVRFixPlugin.DiagOn) return;

 if (!KKCharaStudioVRFixPlugin.DiagOn) return; // 诊断关 → 补丁零开销
 try
 {
 if (__result) return;
 _rayFailCount++;
 if (_rayFailCount % 300 != 1) return;
 FixLog.Key("【指针射线】打不中面板 " + (quad == null ? "?" : quad.name)
 + "（累计 " + _rayFailCount + " 次）。可能原因：面板没有 Collider，或激光方向/起点不对。");
 }
 catch { }
 }

 // ==================== 输入模拟器（扳机点击/触控板移鼠标） ====================

 /// <summary>
 /// `VR.Input` 是第三方输入模拟库（InputSimulator）。它才是"扳机=鼠标左键"的真正执行者，
 /// 也是"触控板推鼠标"（MenuTool 的 MoveMouseBy）的执行者 —— 记录每次调用，
 /// 用来定位「一按扳机光标就跑到左上角」。
 /// </summary>
 internal static void InstallInputSpy(HarmonyLib.Harmony harmony)
 {
 if (_inputInstalled) return;
 if (!KKCharaStudioVRFixPlugin.DiagOn) return; // 诊断总开关
 try
 {
 // 【注意】`VR.Input` 的类型 InputSimulator 属于第三方 WindowsInput 程序集，
 // 不能直接引用（会引入编译期依赖）→ 用反射取实例，运行时再挂它。
 object input = null;
 try
 {
 PropertyInfo p = typeof(VRGIN.Core.VR).GetProperty("Input",
 BindingFlags.Public | BindingFlags.Static);
 if (p != null) input = p.GetValue(null, null);
 }
 catch (Exception e) { FixLog.Debug("取 VR.Input 失败: " + e.Message); }
 if (input == null) return;
 Type simType = input.GetType();
 PropertyInfo mouseProp = simType.GetProperty("Mouse",
 BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
 if (mouseProp == null) return;
 Type mouseType = mouseProp.PropertyType;

 int n = 0;
 n += PatchInput(harmony, mouseType, "LeftButtonDown", Type.EmptyTypes);
 n += PatchInput(harmony, mouseType, "LeftButtonUp", Type.EmptyTypes);
 n += PatchInput(harmony, mouseType, "MoveMouseBy", new Type[] { typeof(int), typeof(int) });
 if (n == 0) return;
 _inputInstalled = true;
 FixLog.Key("【输入模拟】已挂 " + n + " 个调用记录（" + mouseType.FullName + "）"
 + " —— 扳机点击与触控板推鼠标都会留痕。");
 }
 catch (Exception e) { FixLog.Debug("输入模拟安装失败: " + e.Message); }
 }

 private static int PatchInput(HarmonyLib.Harmony harmony, Type mouseType, string name, Type[] args)
 {
 try
 {
 MethodInfo m = args.Length == 0
 ? mouseType.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null)
 : mouseType.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, args, null);
 if (m == null) return 0;
 harmony.Patch(m, prefix: new HarmonyMethod(
 AccessTools.Method(typeof(VRPointerDiagnostics), "InputPrefix")));
 return 1;
 }
 catch { return 0; }
 }

 /// <summary>输入模拟调用记录（限速 0.2 秒；MoveMouseBy 的位移也会记）。</summary>
 public static void InputPrefix(MethodBase __originalMethod, object[] __args)
 {
 if (!KKCharaStudioVRFixPlugin.DiagOn) return;

 if (!KKCharaStudioVRFixPlugin.DiagOn) return; // 诊断关 → 补丁零开销
 try
 {
 if (Time.realtimeSinceStartup - _lastInputLogTime < 0.2f) return;
 _lastInputLogTime = Time.realtimeSinceStartup;
 string extra = "";
 if (__args != null && __args.Length == 2)
 extra = " 参数=(" + __args[0] + "," + __args[1] + ")";
 Vector3 mp = Input.mousePosition;
 FixLog.Key("【输入模拟】" + (__originalMethod == null ? "?" : __originalMethod.Name) + extra
 + " | 当前真实鼠标 mousePos=(" + mp.x.ToString("0") + "," + mp.y.ToString("0")
 + ") 屏幕=" + Screen.width + "x" + Screen.height
 + " | 光标 visible=" + Cursor.visible + " lock=" + Cursor.lockState);
 }
 catch { }
 }

 // ==================== 手柄渲染模型（Bug：只剩圆盘） ====================

 /// <summary>
 /// 每个 Controller 的渲染模型现场：是否存在 SteamVR_RenderModel、名字、是否激活、
 /// renderModelName、以及子渲染体数量。用来判定「手柄模型是没创建、没加载、还是被禁用」。
 /// </summary>
 internal static void DumpControllerModels(string reason)
 {
 if (!KKCharaStudioVRFixPlugin.DiagOn) return; // 诊断总开关
 try
 {
 if (Time.realtimeSinceStartup - _lastModelLogTime < 30f) return; // 10s→30s：实测单次最大 25ms，不值得这么勤
 // 缓存 Controller 列表（30 秒重扫），避免每次 FindObjectsOfType 全表扫描
 VRGIN.Controls.Controller[] controllers = _ctrlCache;
 bool needScan = controllers == null || Time.realtimeSinceStartup - _ctrlCacheTime > 30f;
 if (!needScan)
 {
 for (int i = 0; i < controllers.Length; i++)
 if (controllers[i] == null) { needScan = true; break; }
 }
 if (needScan)
 {
 controllers = UnityEngine.Object.FindObjectsOfType<VRGIN.Controls.Controller>();
 _ctrlCache = controllers;
 _ctrlCacheTime = Time.realtimeSinceStartup;
 }
 if (controllers == null || controllers.Length == 0) return;

 StringBuilder sb = new StringBuilder();
 for (int i = 0; i < controllers.Length; i++)
 {
 var c = controllers[i];
 if (c == null) continue;
 sb.Append(" · ").Append(c.gameObject.name)
 .Append(" active=").Append(c.gameObject.activeInHierarchy);

 // 追踪索引（SteamVR_TrackedObject / SteamVR_RenderModel 都在子物体上）
 Component[] all = c.GetComponentsInChildren<Component>(true);
 int renderers = 0, models = 0;
 for (int j = 0; j < all.Length; j++)
 {
 Component comp = all[j];
 if (comp == null) continue;
 if (comp is Renderer) renderers++;
 string tn = comp.GetType().Name;
 if (tn == "SteamVR_RenderModel")
 {
 models++;
 string rmn = ReadString(comp, "renderModelName");
 // 真凭据：SetModel() 成功时会挂 MeshFilter+MeshRenderer，
 // 所以"有没有 MeshRenderer"才是"模型是否加载"的硬判据
 // （旧版读的 `model` 字段在 SteamVR_RenderModel 里根本不存在，
 // 会把"字段缺失"误报成"未加载"——已修正）。
 MeshRenderer[] mrs = comp.gameObject.GetComponentsInChildren<MeshRenderer>(true);
 int mrCount = mrs == null ? 0 : mrs.Length;
 sb.Append(" | RenderModel[").Append(j).Append("] go=")
 .Append(comp.gameObject.name)
 .Append(" active=").Append(comp.gameObject.activeInHierarchy)
 .Append(" enabled=").Append(comp is Behaviour b ? b.enabled.ToString() : "?")
 .Append(" renderModelName=\"").Append(rmn).Append("\"")
 .Append(" 模型MeshRenderer=").Append(mrCount).Append(" 个")
 .Append(mrCount > 0 ? "（已加载）" : "**（未加载）**");
 }
 else if (tn == "SteamVR_TrackedObject")
 {
 object idx = ReadField(comp, "index");
 sb.Append(" | TrackedObject=").Append(idx)
 .Append(" enabled=").Append(comp is Behaviour b2 ? b2.enabled.ToString() : "?");
 }
 }
 sb.Append(" | 子渲染体=").Append(renderers).Append(" 个，RenderModel 组件=").Append(models).Append(" 个");
 sb.Append('\n');
 }
 string state = sb.ToString();
 if (state == _lastModelState) return;
 _lastModelState = state;
 _lastModelLogTime = Time.realtimeSinceStartup;
 FixLog.Key("【手柄模型】" + reason + "：\n" + state);
 }
 catch (Exception e) { FixLog.Debug("DumpControllerModels 异常: " + e.Message); }
 }

 private static string ReadString(object o, string field)
 {
 try
 {
 FieldInfo f = o.GetType().GetField(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
 if (f == null) return "?";
 object v = f.GetValue(o);
 return v == null ? "null" : v.ToString();
 }
 catch { return "?"; }
 }

 private static object ReadField(object o, string field)
 {
 try
 {
 FieldInfo f = o.GetType().GetField(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
 return f == null ? null : f.GetValue(o);
 }
 catch { return null; }
 }

 private static string SafeVrGuiSize()
 {
 try { return VRGUI.Width + "x" + VRGUI.Height; }
 catch { return "?"; }
 }

 internal static string Describe()
 {
 return "指针诊断=" + (_installed ? "已装" : "未装")
 + "；输入模拟=" + (_inputInstalled ? "已装" : "未装")
 + "；距离不达标=" + _rangeFailCount + " 次；射线打不中=" + _rayFailCount + " 次"
 + "；最近目标=" + (_lastState.Length == 0 ? "无" : _lastState);
 }
 }
}
