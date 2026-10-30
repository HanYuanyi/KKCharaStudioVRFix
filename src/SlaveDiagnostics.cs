using System;
using System.Text;
using HarmonyLib;
using UnityEngine;
using VRGIN.Core;

namespace KKCharaStudioVRFix
{
 /// <summary>
 /// 监视 VRCamera.RegisterSlave / UnregisterSlave。
 ///
 /// VRGIN 的 VRCamera.UpdateCameraConfig() 是这么算 VR 相机掩码的：
 /// mask = 所有从相机掩码的并集 | Default
 /// 只要有任何一台"掩码是全图层"的相机被登记成从相机，VR 相机的掩码就会被
 /// **整体替换成接近全图层**——这就是本次诊断里看到的：
 /// 正常: 6 层；坏: 30 层（= 全 32 层 - 被我清掉的 Studio/Select）
 /// 引导球全都在 Studio/Col(27) 图层，一旦这一层被打开，球就全出来了。
 ///
 /// 这个钩子只做记录，用来在下一次测试时直接点名"是谁污染了掩码"。
 /// </summary>
 internal static class SlaveDiagnostics
 {
 internal static void Install(HarmonyLib.Harmony harmony)
 {
 Patch(harmony, "RegisterSlave", "RegisterPostfix");
 Patch(harmony, "UnregisterSlave", "UnregisterPostfix");

 // UpdateCameraConfig 是 VRGIN 里**唯一**给 VR 相机写 cullingMask 的地方
 // （VRCamera.cs:165 `GetComponent<Camera>().cullingMask = num;`）。
 // 挂它的 postfix，就能在它刚写完的同一瞬间把掩码纠正回来，不用等下一帧。
 try
 {
 System.Reflection.MethodInfo target = AccessTools.Method(
 typeof(VRCamera), "UpdateCameraConfig", Type.EmptyTypes);
 if (target != null)
 {
 harmony.Patch(target, postfix: new HarmonyMethod(
 AccessTools.Method(typeof(SlaveDiagnostics), "UpdateCameraConfigPostfix")));
 FixLog.Info("已挂载钩子: VRCamera.UpdateCameraConfig");
 }
 else
 {
 FixLog.Debug("未找到 VRCamera.UpdateCameraConfig（跳过）。");
 }
 }
 catch (Exception e)
 {
 FixLog.Warn("挂载 VRCamera.UpdateCameraConfig 失败: " + e.Message);
 }
 }

 /// <summary>VRGIN 刚重算完掩码 → 立刻按我们的规则纠正（同一帧内完成）。</summary>
 public static void UpdateCameraConfigPostfix()
 {
 VRRecoveryWatchdog.FixVrCameraMaskNow();
 }

 private static void Patch(HarmonyLib.Harmony harmony, string methodName, string postfixName)
 {
 try
 {
 System.Reflection.MethodInfo target = AccessTools.Method(
 typeof(VRCamera), methodName, new Type[] { typeof(CameraSlave) });
 if (target == null)
 {
 FixLog.Debug("未找到 VRCamera." + methodName + "（跳过）");
 return;
 }

 harmony.Patch(target, postfix: new HarmonyMethod(
 AccessTools.Method(typeof(SlaveDiagnostics), postfixName)));

 FixLog.Info("已挂载钩子: VRCamera." + methodName);
 }
 catch (Exception e)
 {
 FixLog.Warn("挂载 VRCamera." + methodName + " 失败: " + e.Message);
 }
 }

 public static void RegisterPostfix(CameraSlave slave)
 {
 Log("登记从相机", slave);
 VRRecoveryWatchdog.FixVrCameraMaskNow();
 }

 public static void UnregisterPostfix(CameraSlave slave)
 {
 Log("注销从相机", slave);
 VRRecoveryWatchdog.FixVrCameraMaskNow();
 }

 /// <summary>
 /// VerboseCullingLog 关闭时**直接返回，连字符串都不拼**。
 ///
 /// 这段日志是纯诊断（点名"是谁污染了掩码"），但它在 VRGIN **每次重算掩码**时
 /// 都会被调到（RegisterSlave / UnregisterSlave），而里面要：
 /// · `CountBits` ×2（32 次循环）
 /// · `DescribeMask`（**构造 StringBuilder** + 32 次循环 + 查层名 + 拼字符串）
 /// · 再拼一整条日志字符串写盘（FixLog 是同步文件 IO）
 /// 稳态下这些字符串没人看，纯属白烧 CPU + GC 压力。
 /// 需要现场时把 `VerboseCullingLog` 打开即可（默认就是关的）。
 /// </summary>
 private static void Log(string op, CameraSlave slave)
 {
 try
 {
 if (KKCharaStudioVRFixPlugin.CfgVerboseCullingLog == null
 || !KKCharaStudioVRFixPlugin.CfgVerboseCullingLog.Value)
 return;
 string camName = (slave == null) ? "null" : slave.gameObject.name;
 int captured = (slave == null) ? 0 : slave.cullingMask;

 string after = "<读取失败>";
 try
 {
 Camera c = VR.Camera != null && VR.Camera.SteamCam != null
 ? VR.Camera.SteamCam.camera : null;
 if (c != null) after = "层数=" + CountBits(c.cullingMask);
 }
 catch { }

 FixLog.Info("VRCamera." + op + ": " + camName
 + " | 该相机捕获的掩码层数=" + CountBits(captured)
 + " [" + DescribeMask(captured) + "]"
 + " | 之后 VR 相机 " + after);
 }
 catch (Exception e)
 {
 FixLog.Debug("slave 记录失败: " + e.Message);
 }
 }

 internal static int CountBits(int mask)
 {
 int n = 0;
 for (int i = 0; i < 32; i++) if ((mask & (1 << i)) != 0) n++;
 return n;
 }

 internal static string DescribeMask(int mask)
 {
 StringBuilder sb = new StringBuilder();
 for (int i = 0; i < 32; i++)
 {
 if ((mask & (1 << i)) == 0) continue;
 string n = LayerMask.LayerToName(i);
 if (sb.Length > 0) sb.Append(", ");
 sb.Append(i).Append(':').Append(string.IsNullOrEmpty(n) ? "<unnamed>" : n);
 }
 return sb.Length == 0 ? "<空>" : sb.ToString();
 }
 }
}
