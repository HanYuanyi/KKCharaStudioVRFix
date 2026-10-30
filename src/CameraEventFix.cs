using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using VRGIN.Core;

namespace KKCharaStudioVRFix
{
 /// <summary>
 /// 把"周期轮询兜底"改成"事件驱动"。
 ///
 /// 起因（用户的问题）："为什么非要留着每隔一段时间就检查一下的功能？
 /// BUG 修好了不就是该永久生效的部分吗？"
 ///
 /// 逐项分析后的结论 —— 大部分确实可以不再轮询：
 ///
 /// · **后处理/发灰**：VRGIN 的 `VRCamera.CopyFX()` 会 `DestroyImmediate` 掉 VR 相机上的
 /// 效果组件 → 所以必须"每次它复制后"立刻重建。以前靠 30 秒轮询兜底，
 /// 现在 hook `CopyFX` 的 postfix → **VRGIN 每次复制完，我下一行就重建**，零延迟零轮询。
 ///
 /// · **相机掩码被覆盖**：VRGIN 的 `VRCamera.Copy()`（收养相机）会触发
 /// `UpdateCameraConfig()` 用"所有从相机 cullingMask 的并集"覆盖 VR 相机掩码
 /// → 以前靠 5 秒轮询改回来，现在 hook `Copy` 的 postfix → 收养完立刻修正。
 ///
 /// · **解锁状态**：加载场景卡时工具被 `AddComponent` 重建 → 需要 hook 工具的 Awake。
 /// · **UI Canvas**：需要 hook `Canvas.OnEnable`。
 /// · **光标 visible / 窗口句柄**：Unity 与 Win32 都没有对应事件，**暂时无法纯事件化**，
 /// 只能保留极低频兜底（已降到 10 秒 / 60 秒，且不影响功能正确性）。
 ///
 /// 本类只做前两项（同一类的两个方法，一次 patch 解决两个轮询）。
 /// 轮询兜底仍然保留，但频率大幅降低（30 秒），仅作为"漏网时的最后防线"。
 /// </summary>
 internal static class CameraEventFix
 {
 private static bool _installed;

#if NEWFIX
 private static bool _reflectHooked;
 /// <summary>【NEWFIX】游戏"应用画面效果选项"的事件是否挂上了。没挂上就得保留 5 秒轮询兜底。</summary>
 internal static bool ReflectOptionHooked { get { return _reflectHooked; } }

 /// <summary>【NEWFIX】Studio 每次 ReflectOption() 之后调用：立刻按 visibleAxis 校一次清单。</summary>
 public static void ReflectOptionPostfix()
 {
 // ★ 平面模式（VR 没跑）下这个钩子**照样会触发**（切 FK/IK 轴就是它）。
 // 不过这一关就会走到 `VR.Camera` 惰性 getter → 凭空造一套假 rig → 相机被抢。
 if (!VRRef.VrRunning) return;
 try { VRRecoveryWatchdog.FixVrCameraMaskNow(); }
 catch { }
 }

 private static bool _sunLightHooked;
 private static int _sunLightCalls;
 private static float _lastSunLightLog = -999f;
 internal static bool SunLightHooked { get { return _sunLightHooked; } }


 /// <summary>
 /// 【NEWFIX】游戏"应用画面效果"那一刻的钩子。
 ///
 /// 反编译依据（Assembly-CSharp / SunLightInfo.cs）：
 /// public bool Set(Info.Type type, Camera cam) {
 /// info.SetLight(_targetLight);
 /// info.SetFog();
 /// info.SetSunShafts(cam.GetComponent&lt;SunShafts&gt;());
 /// info.SetACE(cam.GetComponent&lt;AmplifyColorEffect&gt;()); // ← 写 LutTexture / BlendAmount
 /// info.SetBackTexture(_bgParam);
 /// }
 /// 这就是"你在画面效果面板里改了什么"的唯一提交入口 —— 以前我们不知道它存在，
 /// 只能每帧反射比对参数；现在改成它一跑我们就同步一次，稳态零开销。
 /// </summary>
 public static void SunLightApplyPostfix()
 {
 // ★★ 2026-10-22 平面模式卡死真凶之一：`SunLightInfo.Set` / `Info.SetACE`
 // 是**游戏自己**提交画面效果的入口，平面模式加载场景卡、改画面效果时**照样触发**。
 // 之前这里没有任何 VR 判断 → 直接进 `SyncFromSource` → `GetVrCamera()` → `VR.Camera`
 // 惰性 getter 当场 `new GameObject("VRGIN_Camera")` → 相机被抢（蓝背景 + 镜头无法移动）。
 if (!VRRef.VrRunning) return;
 try
 {
 _sunLightCalls++;
 if (KKCharaStudioVRFixPlugin.DiagOn
 && Time.realtimeSinceStartup - _lastSunLightLog > 1f)
 {
 _lastSunLightLog = Time.realtimeSinceStartup;
 FixLog.Key("【画面效果·NEWFIX】游戏提交了画面效果（累计 " + _sunLightCalls
 + " 次）→ 立刻同步到 VR 相机。");
 }
 if (KKCharaStudioVRFixPlugin.CfgSyncEffectsFromSource == null
 || KKCharaStudioVRFixPlugin.On(KKCharaStudioVRFixPlugin.CfgSyncEffectsFromSource))
 {
 ColorGradingFix.SyncFromSource("游戏应用画面效果", true, true);
 }
 }
 catch { }
 }
#endif

 internal static void Install(HarmonyLib.Harmony harmony)
 {
 if (_installed) return;
 try
 {
 int n = 0;

 // ① VRGIN 每次复制后处理（CopyFX）之后 —— 立刻重建 VR 相机上的效果
 MethodInfo copyFx = AccessTools.Method(typeof(VRCamera), "CopyFX", new Type[] { typeof(Camera) });
 if (copyFx != null)
 {
 harmony.Patch(copyFx, postfix: new HarmonyMethod(
 AccessTools.Method(typeof(CameraEventFix), "CopyFXPostfix")));
 n++;
 }

 // ② VRGIN 每次收养相机（Copy）之后 —— 立刻修 VR 相机掩码
 MethodInfo copy = AccessTools.Method(typeof(VRCamera), "Copy",
 new Type[] { typeof(Camera), typeof(bool), typeof(bool) });
 if (copy != null)
 {
 harmony.Patch(copy, postfix: new HarmonyMethod(
 AccessTools.Method(typeof(CameraEventFix), "CopyPostfix")));
 n++;
 }

#if NEWFIX
 // ③ 【NEWFIX】游戏每次"应用画面效果选项"之后 —— 立刻校一次 VR 相机清单。
 //
 // 反编译依据（Assembly-CSharp / Studio.CameraControl.cs）：
 // public void ReflectOption() {
 // if (Studio.workInfo.visibleAxis) { ... list.Add("Studio/Col"); list.Add("Studio/Select"); }
 // m_SubCamera.cullingMask = LayerMask.GetMask(list.ToArray());
 // }
 // 也就是说"你按快捷键切 FK/IK 轴显示"这件事，游戏自己有一个明确的入口方法。
 // 以前我们是每 5 秒去看一眼 visibleAxis（因为不知道有这个方法）；
 // 现在直接挂在这个方法后面 → 你切完轴的**同一帧**清单就是对的，5 秒轮询可以删。
 Type ccType = VRRef.FindType("Studio.CameraControl");
 MethodInfo reflect = ccType == null ? null : ccType.GetMethod("ReflectOption",
 BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
 null, Type.EmptyTypes, null);
 if (reflect != null)
 {
 try
 {
 harmony.Patch(reflect, postfix: new HarmonyMethod(
 AccessTools.Method(typeof(CameraEventFix), "ReflectOptionPostfix")));
 // 校验：补丁表里必须真的有我们这一条（不许把"没抛异常"当成功）
 HarmonyLib.Patches info = HarmonyLib.Harmony.GetPatchInfo(reflect);
 if (info != null && info.Postfixes != null)
 {
 foreach (HarmonyLib.Patch p in info.Postfixes)
 {
 if (p != null && p.PatchMethod != null
 && p.PatchMethod.Name == "ReflectOptionPostfix")
 { _reflectHooked = true; break; }
 }
 }
 }
 catch (Exception e) { FixLog.Warn("【事件驱动】挂 ReflectOption 失败: " + e.Message); }
 }
 else
 {
 FixLog.Warn("【事件驱动】找不到 Studio.CameraControl.ReflectOption —— "
 + "轴图层只能继续靠 5 秒轮询兜底。");
 }

 // ④ 【NEWFIX】游戏"提交画面效果"的入口（SunLightInfo.Set / Info.SetACE）
 // → 替掉每帧参数比对。两个都挂上，覆盖"换滤镜"和"只拖滤镜比例"两条路。
 Type sli = VRRef.FindType("SunLightInfo");
 if (sli != null)
 {
 MethodInfo applySet = null;
 foreach (MethodInfo m in sli.GetMethods(
 BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
 {
 if (m.Name == "Set" && m.GetParameters().Length == 2) { applySet = m; break; }
 }
 if (applySet != null)
 {
 try
 {
 harmony.Patch(applySet, postfix: new HarmonyMethod(
 AccessTools.Method(typeof(CameraEventFix), "SunLightApplyPostfix")));
 HarmonyLib.Patches pi = HarmonyLib.Harmony.GetPatchInfo(applySet);
 if (pi != null && pi.Postfixes != null)
 {
 foreach (HarmonyLib.Patch p in pi.Postfixes)
 {
 if (p != null && p.PatchMethod != null
 && p.PatchMethod.Name == "SunLightApplyPostfix")
 { _sunLightHooked = true; break; }
 }
 }
 }
 catch (Exception e) { FixLog.Warn("【NEWFIX】挂 SunLightInfo.Set 失败: " + e.Message); }
 }

 // 嵌套类 SunLightInfo+Info 的 SetACE（只改滤镜/比例时可能走这条）
 try
 {
 Type infoType = sli.GetNestedType("Info",
 BindingFlags.Public | BindingFlags.NonPublic);
 MethodInfo setAce = infoType == null ? null : infoType.GetMethod("SetACE",
 BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
 if (setAce != null)
 {
 harmony.Patch(setAce, postfix: new HarmonyMethod(
 AccessTools.Method(typeof(CameraEventFix), "SunLightApplyPostfix")));
 _sunLightHooked = true;
 }
 }
 catch (Exception e) { FixLog.Warn("【NEWFIX】挂 SunLightInfo.Info.SetACE 失败: " + e.Message); }
 }
 else
 {
 FixLog.Warn("【NEWFIX】找不到 SunLightInfo —— 画面效果只能继续每帧比对。");
 }

#endif

 if (n > 0)
 {
 _installed = true;
 FixLog.Key("【事件驱动】已挂上 VRGIN 相机事件 " + n + " 个"
 + "（CopyFX→重建后处理、Copy→修掩码）—— 替掉 30s/5s 轮询兜底。");
 }
#if NEWFIX
 if (KKCharaStudioVRFixPlugin.DiagOn)
 {
 FixLog.Key("【事件驱动·NEWFIX】轴图层事件（ReflectOption）= "
 + (_reflectHooked ? "已挂上，5 秒轮询将停用" : "未挂上，保留 5 秒轮询兜底"));
 FixLog.Key("【事件驱动·NEWFIX】画面效果事件（SunLightInfo.Set/SetACE）= "
 + (_sunLightHooked
 ? "已挂上（注意：它只覆盖「切换时间段/加载场景卡」，面板里换滤镜仍靠每帧比对）"
 : "未挂上"));
 }
#endif
 }
 catch (Exception e)
 {
 FixLog.Warn("【事件驱动】安装失败: " + e.Message);
 }
 }

 /// <summary>VRGIN 刚把源相机的效果复制到 VR 相机（并销毁了旧的副本）之后。</summary>
 public static void CopyFXPostfix()
 {
 try
 {
 // VRGIN 刚刚 DestroyImmediate 了 VR 相机上的效果、并按蓝本重建了副本。
 // 这里立刻做两件事（取代 30 秒轮询）：
 // ① 确保 VR 相机上确实有被监视的效果（万一 CopyFX 因 EffectBlacklist 漏了某个）；
 // ② 立刻从源相机同步一次参数（让刚加载的场景卡效果马上生效）。
 ColorGradingFix.ForceEnable("VRGIN.CopyFX 事件");
 ColorGradingFix.SyncFromSource("VRGIN.CopyFX 事件", true, true);
 }
 catch (Exception e) { FixLog.Debug("CopyFXPostfix 异常: " + e.Message); }
 }

 /// <summary>VRGIN 刚收养了一个相机（Copy）之后 —— 它可能刚覆盖了 VR 相机的掩码。</summary>
 public static void CopyPostfix()
 {
 try
 {
 // 立刻修一次掩码（取代 5 秒轮询）：
 // VRGIN 的 Copy() → UpdateCameraConfig() 会用"所有从相机 cullingMask 的并集"
 // 覆盖 VR 相机掩码，把 UI 图层加回去 → 手柄图标被画进 UI。
 VRRecoveryWatchdog.FixVrCameraMaskNow();
 }
 catch (Exception e) { FixLog.Debug("CopyPostfix 异常: " + e.Message); }
 }

 /// <summary>
 /// hook `Canvas.OnEnable` —— 新 UI 画布出现的**那一刻**就接管，
 /// 取代 GuiCanvasFix 的 30 秒全表扫描（`Resources.FindObjectsOfTypeAll&lt;Canvas&gt;()`）。
 /// Unity 的 `Canvas.OnEnable` 不是虚方法，但 Harmony 可以直接 patch 它。
 /// </summary>
 internal static void InstallCanvasHook(HarmonyLib.Harmony harmony)
 {
 try
 {
 // 以前用 `AccessTools.Method(typeof(Canvas), "OnEnable")` —— 而
 // Canvas.OnEnable 是 **protected**，取不到就 `return`（**静默**），
 // 于是日志里从来没有「已挂 Canvas.OnEnable」这一行（用户日志已证实）。
 // 现在显式带 NonPublic 绑定，并且**失败也要落盘**，不再静默。
 MethodInfo onEnable = null;
 try
 {
 onEnable = typeof(Canvas).GetMethod("OnEnable",
 BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
 }
 catch { }
 if (onEnable == null) onEnable = AccessTools.Method(typeof(Canvas), "OnEnable");
 if (onEnable == null)
 {
 FixLog.Warn("【事件驱动】找不到 Canvas.OnEnable（protected）—— 新画布只能靠 30s 周期扫描接管。");
 return;
 }
 harmony.Patch(onEnable, postfix: new HarmonyMethod(
 AccessTools.Method(typeof(CameraEventFix), "CanvasOnEnablePostfix")));
 FixLog.Key("【事件驱动】已挂 Canvas.OnEnable（" + onEnable.Attributes
 + "）—— 新 UI 画布出现即接管，替掉 30s 扫描。");
 }
 catch (Exception e)
 {
 FixLog.Warn("【事件驱动】Canvas.OnEnable hook 失败: " + e.Message
 + "（新画布只能靠 30s 周期扫描接管）");
 }
 }

 /// <summary>
 /// `Canvas.OnEnable` 是 Unity 里**最频繁的事件之一** ——
 /// 游戏里任何 UI 元素启用都会触发，而且**我的回调不在 PerfProbe 计量范围内**
 /// （Harmony postfix 是独立调用路径），所以它的成本是日志里的盲区。
 /// 这里先做**零分配的前置过滤**，绝大多数情况直接返回：
 /// · `renderMode == WorldSpace` → 立刻跳过（不读名字、不递归）
 /// · 已接管过（InstanceID 在 HashSet 里）→ 立刻跳过
 /// 只有真正可能是"漏掉的画布"才进 AdoptCanvasNow 的完整判断。
 /// </summary>
 public static void CanvasOnEnablePostfix(Canvas __instance)
 {
 try
 {
 if (__instance == null) return;
 // ★ 平面模式：`AdoptCanvasNow` 里要拿 VRGIN 的 GUI 相机，那条链会去碰 `VRGIN_GUI`，
 // 平面模式下没有意义（而且 VRGUI.Instance 本身也会 new 一个 GameObject）→ 直接跳过。
 if (!VRRef.VrRunning) return;
 if (__instance.renderMode == RenderMode.WorldSpace) return; // 世界物件，绝不碰
 GuiCanvasFix.AdoptCanvasNow(__instance);
 }
 catch { }
 }

#if !LEAN
 // 【精简版瘦身】诊断输出。
 internal static string Describe()
 {
 return "事件驱动 hook=" + (_installed ? "已装" : "未装");
 }
#else
 // 【LEAN】诊断输出整段删除（只给完整版）
#endif
 }
}
