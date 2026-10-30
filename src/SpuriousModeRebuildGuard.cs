using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace KKCharaStudioVRFix
{
 /// <summary>
 /// 阻止 0.0.3 在场景卡加载期间**多余地重建 StandingMode** ——
 /// 这才是"蓝屏跳变 + 手柄消失 + 镜头跑到歪的地方"的**唯一根因**。
 ///
 /// ══════════════════════════════════════════════════════════════════════
 /// 一、实机 log 定案
 /// ══════════════════════════════════════════════════════════════════════
 /// 用户实测：5 次加载里**只有第 2 次**出现「蓝屏 → 镜头歪 → 再跳 → 才到位」。
 /// 日志精确对上：
 ///
 /// 加载#1 StandingMode 重建: 无 → 正常
 /// 加载#2 StandingMode 重建: ★有★ → 蓝屏 + 跳 ❌
 /// 加载#3 StandingMode 重建: 无 → 正常
 /// 加载#4 StandingMode 重建: 无 → 正常
 /// 加载#5 StandingMode 重建: 无 → 正常
 ///
 /// 全日志 **`StandingMode 归零已被抵消` 只出现 1 次**（09:34:31.465，第 2 次）。
 /// → **"重建"与"跳变"一一对应，且只发生在重建的那一次。**
 ///
 /// ══════════════════════════════════════════════════════════════════════
 /// 二、0.0.3 为什么会重建（反编译 3155–3230 行）
 /// ══════════════════════════════════════════════════════════════════════
 /// `SceneLoadScene.OnClickLoad` 的 prefix（LoadFixHook，2520 行）：
 /// if (VRManager.Instance.Mode is GenericStandingMode)
 /// ForceResetVRMode(); // ← 前提：此刻 mode 已是 StandingMode
 ///
 /// `ForceResetVRModeCo`（3173 行）：
 /// yield return null; × 5
 /// if (!VRManager.Instance.Mode || !(VRManager.Instance.Mode is GenericStandingMode))
 /// ForceResetAsStandingMode(); // ← ★ 5 帧后若 mode 不是 StandingMode 就重建
 /// else
 /// Logger.Log(..., "Is Standing Mode. Skip to setting force.");
 ///
 /// `ForceResetAsStandingMode`（3192 行）→ `VR.Manager.SetMode&lt;GenericStandingMode&gt;()`
 /// → `VRGIN.Core.VRManager.SetMode`（79 行）：
 /// DestroyImmediate(Mode); // ★ 销毁旧 mode（含 ControllerManager / Left / Right）
 /// Mode = AddComponent&lt;GenericStandingMode&gt;();
 /// → 新建组件的 `OnStart()` 在**下一帧**才跑
 /// → `VRGIN.Modes.StandingMode.OnStart()`：
 /// VR.Camera.SteamCam.origin.position = Vector3.zero; // ★ 归零
 /// VR.Camera.SteamCam.origin.rotation = Quaternion.identity; // ★ 抹平朝向
 ///
 /// **→ 一次"重建"= 手柄消失（ControllerManager 被 Destroy）+ origin 归零 + 朝向被抹平。**
 /// 这就是用户看到的全部症状。
 ///
 /// ══════════════════════════════════════════════════════════════════════
 /// 三、0.0.3 的**本意**是自愈，但会被加载过程误判
 /// ══════════════════════════════════════════════════════════════════════
 /// 它的意图是：「**如果现在已经是 StandingMode**，5 帧后检查一下万一被弄坏了就修回来」。
 /// 但 `LoadSceneCoroutine` / `InitScene` / `ReleaseMap` / Unity 卸载重建场景
 /// 这几步会打断 `VR.Manager.Mode` 的可用性 —— 于是那 5 帧后的判定
 /// **误以为 mode 丢了**，就重建了一次**本来不需要**的 StandingMode。
 ///
 /// 这解释了为什么**只有第 2 次命中**：判定依赖"5 帧窗口内 mode 的瞬时状态"，
 /// 而那个状态取决于两次加载的间隔、以及上一次加载刚做完什么 —— 具有时序偶然性。
 ///
 /// ══════════════════════════════════════════════════════════════════════
 /// 四、修法：不抵消、不重建 —— 直接让这次多余的重建**不发生**
 /// ══════════════════════════════════════════════════════════════════════
 /// 之前 ~ 一直在做「重建之后把 origin 拉回来」（`UndoStandingModeReset`）。
 /// **那是治标**：手柄已经被销毁过一次、`SetMode` 的 DestroyImmediate→AddComponent
 /// 空窗帧已经产生、`StandingMode.OnStart` 的归零已经写进渲染帧。
 ///
 /// **治本 = 拦 `ForceResetAsStandingMode` 的 prefix，在"场景卡加载期间 + mode 仍然健在"
 /// 时直接跳过整个重建。** 这样：
 /// · 手柄不会消失（ControllerManager 不被 Destroy）
 /// · `origin` 不会被归零（`StandingMode.OnStart` 不执行）
 /// · `SetMode` 的空窗帧不存在
 /// · 原版后续的 `Import` → `MoveToCurrent` 正常摆到场景卡机位（一条直线到位）
 ///
 /// 判定条件（全部满足才跳过）：
 /// ① 配置开关开着；
 /// ② 正处于"场景卡加载"窗口（`IsSceneCardLoadSticky`，粘性只读）；
 /// ③ `VR.Manager.Mode` **存在**且**就是** `GenericStandingMode` ——
 /// 即 mode 根本没坏，这次重建纯属误判。
 ///
 /// ⚠️ 注意第 ③ 条：只在"mode 确实还活着"时才拦。若 mode 真的丢了/坏了，
 /// 绝不拦 —— 那种情况下 0.0.3 的重建是**必要的**自救。
 /// </summary>
 internal static class SpuriousModeRebuildGuard
 {
 private static bool _installed;
 private static HarmonyLib.Harmony _harmony;

 /// <summary>已拦截次数（仅统计，便于日志核对）。</summary>
 private static int _blocked;

 internal static void Install(HarmonyLib.Harmony harmony)
 {
 _harmony = harmony;
 TryInstall();
 }

 /// <summary>
 /// 供看门狗在若干帧后重试安装。
 /// 背景：Awake 时 `AppDomain.CurrentDomain.GetAssemblies()` 可能还没齐
 /// （BepInEx 逐个加载/实例化插件），导致 FindTarget 找不到目标而静默失效。
 /// </summary>
 internal static void RetryInstall()
 {
 if (_installed) return;
 if (_harmony == null) return;
 TryInstall();
 }

 private static void TryInstall()
 {
 try
 {
 // 0.0.3 的 `KKCharaStudioInterpreter` 是 **internal 类**
 // （命名空间 `KKCharaStudioVR`），我们的插件是**另一个程序集**，
 // 所以**不能**用 `typeof(...)` —— 只能用字符串反射定位。
 MethodInfo target = FindTarget();

 if (target == null)
 {
 FixLog.Warn("未找到 KKCharaStudioInterpreter.ForceResetAsStandingMode —— "
 + "多余重建守护未启用（将在稍后重试；不影响其它修复）。");
 return; // 注意：不置 _installed，允许重试
 }

 _harmony.Patch(target,
 prefix: new HarmonyMethod(AccessTools.Method(
 typeof(SpuriousModeRebuildGuard), "Prefix")));

 _installed = true;
 FixLog.Info("已挂载钩子: " + target.DeclaringType.FullName + ".ForceResetAsStandingMode "
 + "[多余重建守护]");
 }
 catch (Exception e)
 {
 FixLog.Warn("多余重建守护挂载失败: " + e.Message);
 }
 }

 /// <summary>
 /// 在所有已加载程序集里找 `KKCharaStudioInterpreter.ForceResetAsStandingMode()`。
 /// 因为目标类是 internal，必须走字符串查找。
 ///
 /// 两个稳健性修复（旧版在实机上静默失败）：
 /// ① 先按程序集名 `KKCharaStudioVRPlugin` 精确定位，再全量兜底遍历；
 /// ② 不用 `GetMethod(name, flags, binder, types, mods)` 重载 —— Mono 对它有已知的坑，
 /// 改成 `GetMethods(flags)` + 手工过滤（名字/静态/无参）。
 /// </summary>
 private static MethodInfo FindTarget()
 {
 Assembly[] all;
 try { all = AppDomain.CurrentDomain.GetAssemblies(); }
 catch (Exception e)
 {
 FixLog.Debug("FindTarget: GetAssemblies 失败: " + e.Message);
 return null;
 }

 // ① 按程序集名直接定位
 foreach (Assembly asm in all)
 {
 string name;
 try { name = asm.GetName().Name; }
 catch { continue; }
 if (name == "KKCharaStudioVRPlugin")
 {
 MethodInfo m = FindInAssembly(asm);
 if (m != null) return m;
 FixLog.Debug("FindTarget: 已定位 KKCharaStudioVRPlugin 程序集，但方法解析失败。");
 }
 }

 // ② 全量兜底
 foreach (Assembly asm in all)
 {
 MethodInfo m = FindInAssembly(asm);
 if (m != null) return m;
 }
 return null;
 }

 private static MethodInfo FindInAssembly(Assembly asm)
 {
 try
 {
 Type t = asm.GetType("KKCharaStudioVR.KKCharaStudioInterpreter", throwOnError: false);
 if (t == null) return null;
 foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
 | BindingFlags.Static | BindingFlags.Instance))
 {
 if (m.Name == "ForceResetAsStandingMode" && m.IsStatic
 && m.GetParameters().Length == 0)
 return m;
 }
 }
 catch (Exception e)
 {
 FixLog.Debug("FindInAssembly 异常(" + asm.FullName + "): " + e.Message);
 }
 return null;
 }

 /// <summary>
 /// 返回 false = 跳过原方法（不重建）。
 /// </summary>
 public static bool Prefix()
 {
 try
 {
 if (!KKCharaStudioVRFixPlugin.On(KKCharaStudioVRFixPlugin.CfgBlockSpuriousModeRebuild))
 return true; // 开关关闭 → 保持 0.0.3 原行为

 // 只处理"场景卡加载"窗口；地图加载/其它情况的 0.0.3 自愈照旧
 if (!VRRef.IsSceneCardLoadSticky()) return true;

 // ★ 核心判据：mode 还活着，而且就是 GenericStandingMode
 // → 说明 0.0.3 的 5 帧判定误判了，这次重建纯属多余。
 object mode = VRRef.GetCurrentMode();
 if (mode == null) return true; // mode 真的没了 → 让它重建（自救）
 if (mode.GetType().Name != "GenericStandingMode") return true; // 是别的 mode → 不拦

 _blocked++;
 JumpDiagnostics.Mark("守卫拦下多余重建 StandingMode（第 " + _blocked + " 次）");
 FixLog.Key("已阻止多余重建 StandingMode（第 " + _blocked + " 次）—— "
 + "mode 仍健在且就是 GenericStandingMode，0.0.3 的 5 帧判定误判了。"
 + "跳过重建可避免「手柄消失 + origin 归零 + 镜头被甩到歪的位置」。");

 return false; // 跳过 ForceResetAsStandingMode
 }
 catch (Exception e)
 {
 FixLog.Debug("多余重建守护 Prefix 异常（放行原方法）: " + e.Message);
 return true;
 }
 }
 }
}
