using System;
using System.Reflection;
using HarmonyLib;
using VRGIN.Core;

namespace KKCharaStudioVRFix
{
 /// <summary>
 /// 【NEWFIX ★★★】掩码事件钩子 —— **功能性修复**，与诊断完全无关，两版都常开。
 ///
 /// 来历（2026-10-21 事故复盘）：
 /// 这三处补丁原本写在 `SlaveDiagnostics.Install()` 里。但 SlaveDiagnostics 是**诊断类**，
 /// 精简版为了压缩体积把它换成了空壳 → 补丁不再安装 → 掩码修复失去"关键时刻"。
 ///
 /// 为什么必须在这三个时刻纠正：
 /// VRGIN 唯一给 VR 相机写 `cullingMask` 的地方是 `VRCamera.UpdateCameraConfig()`，
 /// 而它是在**渲染前**（`CameraKiller.OnPreCull`）触发的 —— **晚于我们的 Update**。
 /// 只在 Update 里修会被它当场覆盖 → 表现为「FK/IK 引导球全部显示」。
 /// 所以在它刚写完的同一瞬间（postfix）立刻纠正，才是正确做法。
 ///
 /// 这三个 postfix 里**只有** `FixVrCameraMaskNow()` 这一句功能性调用；
 /// 原来附带的"登记从相机/注销从相机"日志是纯诊断，已随 SlaveDiagnostics 一起移除。
 /// </summary>
 internal static class MaskEventFix
 {
 private static bool _installed;
 internal static bool Installed { get { return _installed; } }

 internal static void Install(HarmonyLib.Harmony harmony)
 {
 if (_installed) return;
 try
 {
 // ① UpdateCameraConfig —— 唯一写 cullingMask 的地方，最关键
 MethodInfo cfg = AccessTools.Method(typeof(VRCamera), "UpdateCameraConfig", Type.EmptyTypes);
 if (cfg != null)
 {
 harmony.Patch(cfg, postfix: new HarmonyMethod(
 AccessTools.Method(typeof(MaskEventFix), "OnMaskRecomputed")));
 }

 // ② RegisterSlave / UnregisterSlave —— 从相机增减也会重算掩码
 Patch(harmony, "RegisterSlave");
 Patch(harmony, "UnregisterSlave");

 // 校验：UpdateCameraConfig 那条是必须装上的
 bool ok = false;
 if (cfg != null)
 {
 HarmonyLib.Patches info = HarmonyLib.Harmony.GetPatchInfo(cfg);
 if (info != null && info.Postfixes != null)
 {
 foreach (HarmonyLib.Patch p in info.Postfixes)
 {
 if (p != null && p.PatchMethod != null
 && p.PatchMethod.Name == "OnMaskRecomputed") { ok = true; break; }
 }
 }
 }
 _installed = ok;

 if (KKCharaStudioVRFixPlugin.DiagOn)
 FixLog.Key("【掩码事件·NEWFIX】VRCamera.UpdateCameraConfig / RegisterSlave / UnregisterSlave = "
 + (_installed ? "已挂上（VRGIN 重算掩码的同一瞬间就纠正）" : "未挂上（只能靠每帧修）"));
 }
 catch (Exception e) { FixLog.Debug("MaskEventFix.Install 异常: " + e.Message); }
 }

 private static void Patch(HarmonyLib.Harmony harmony, string methodName)
 {
 try
 {
 MethodInfo target = AccessTools.Method(typeof(VRCamera), methodName,
 new Type[] { typeof(CameraSlave) });
 if (target == null) return;
 harmony.Patch(target, postfix: new HarmonyMethod(
 AccessTools.Method(typeof(MaskEventFix), "OnMaskRecomputed")));
 }
 catch { }
 }

 /// <summary>VRGIN 刚重算完掩码 → 立刻按我们的规则纠正（同一帧内完成）。</summary>
 public static void OnMaskRecomputed()
 {
 try { VRRecoveryWatchdog.FixVrCameraMaskNow(); } catch { }
 }
 }
}
