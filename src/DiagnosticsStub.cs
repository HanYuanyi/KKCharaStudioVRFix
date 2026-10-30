#if LEAN
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;
using VRGIN.Controls;
using VRGIN.Core;
using VRGIN.Modes;
using VRGIN.Visuals;

namespace KKCharaStudioVRFix
{
 // ==================================================================================
 // 【精简版专用】4 个诊断类的「空壳存根」
 //
 // 为什么需要它：
 // 精简版里这 4 个类（Diagnostics / VRPointerDiagnostics / JumpDiagnostics / SlaveDiagnostics）
 // 永远不会执行（DiagOn / DiagnosticsEnabled 恒 false），但别处有 65 处调用，
 // 直接删掉类会编译不过。所以这里提供**类名、方法名、参数完全一致**的空壳，
 // csproj 在 LEAN 下把 4 个真实文件 `Compile Remove` 掉 →
 // 真正的诊断代码（约 83KB 源码）**完全不进 DLL**，而所有调用点一行都不用改。
 //
 // 注意：这些方法的**返回值**（CountBits / DescribeMask / Describe）在精简版里只被
 // `FixLog.*` 的字符串拼接使用，而那些调用点已被 `[Conditional("DIAG")]` 整段剔除，
 // 所以返回 0 / "" / null 不会影响任何真实逻辑。
 // ==================================================================================

 internal static class Diagnostics
 {
 internal static void DumpAll(string reason) { }
 }

 internal static class VRPointerDiagnostics
 {
 internal static void Install(Harmony harmony) { }
 internal static string Describe() { return ""; }

 // Harmony 回调（精简版不会挂这些补丁，签名保留只为兼容）
 }

 internal static class JumpDiagnostics
 {
 internal static void Install(Harmony harmony) { }
 internal static void RetryInstall() { }
 internal static void Suppress(float seconds) { }
 internal static void Mark(string text) { }
 internal static void RigPatrol() { }
 internal static void AutopsyModeLoss() { }
 }

 internal static class SlaveDiagnostics
 {
 internal static void Install(Harmony harmony) { }
 internal static int CountBits(int mask) { return 0; }
 internal static string DescribeMask(int mask) { return ""; }
 }
}
#endif
