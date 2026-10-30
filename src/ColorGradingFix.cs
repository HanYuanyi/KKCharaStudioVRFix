using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using VRGIN.Core;

namespace KKCharaStudioVRFix
{
 /// <summary>
 /// 色彩分级（后处理）修复 —— **这是「画面变灰」的根因**。
 ///
 /// 诊断证据（同一份日志里"正常"与"变灰"两份导出的唯一实质差异）：
 /// 正常: VR 相机上的 AmplifyColorEffect enabled=True
 /// 变灰: VR 相机上的 AmplifyColorEffect enabled=False
 /// （Studio 主相机上始终是 False，两种状态都一样）
 ///
 /// 机制（VRGIN_KKCS/VRGIN.Core/VRCamera.cs 第 184-214 行）：
 /// private void CopyFX(GameObject source, GameObject target, bool disabledSourceFx)
 /// {
 /// foreach (MonoBehaviour e in target.GetCameraEffects())
 /// Object.DestroyImmediate(e); // 先销毁 VR 相机上的效果
 /// foreach (MonoBehaviour e in source.GetCameraEffects()) {
 /// MonoBehaviour copy = target.CopyComponentFrom(e);
 /// copy.enabled = e.enabled; // ← 按"源相机的启用状态"复制
 /// if (disabledSourceFx) e.enabled = false; // ← 然后禁用源相机的效果
 /// }
 /// }
 ///
 /// 而 VRManager.OnUpdate() 每次收养到主相机都会调用 ApplyEffects() → CopyFX(Blueprint)。
 /// 于是：
 /// 第 1 次 CopyFX：源(Studio 主相机)还没被禁用 → VR 相机副本 enabled=True → 正常
 /// 第 2 次 CopyFX：源已经被上一次禁用了 → VR 相机副本 enabled=False → 画面变灰
 /// 这就是"第二次加载场景卡才变灰"的原因。
 ///
 /// 官方 IllusionMods/KKS_VR 早就在 ForceResetAsStandingMode() 结尾做了这件事：
 /// var c = VR.Camera.gameObject.GetComponent&lt;AmplifyColorEffect&gt;();
 /// if ((bool)c) c.enabled = true;
 /// 本类把这一步补上（不引用类型，按名字匹配，避免编译期依赖 AmplifyColor.dll）。
 ///
 /// ========================= 关键补充 =========================
 /// 上一轮用户"趁发灰期间按 F7"的导出终于抓到了现场，证明还有一种更狠的情况：
 ///
 /// 正常时: VR 相机组件（6 个）: AmplifyOcclusionEffect enabled=True
 /// AmplifyColorEffect enabled=True
 /// 发灰时: VR 相机组件（2 个）: （**根本没有这两个组件**）
 ///
 /// 也就是说：组件不是被"禁用"，而是被 **DestroyImmediate 销毁** 了。
 ///
 /// 回看 CopyFX 的实现就能对上：
 /// foreach (var e in target.GetCameraEffects())
 /// Object.DestroyImmediate(e); // ← 先无条件销毁目标上的效果
 /// foreach (var e in source.GetCameraEffects()) {
 /// if (!VR.Interpreter.IsAllowedEffect(e)) { continue; } // ← 跳过时不补回来
 /// var copy = target.CopyComponentFrom(e);
 /// copy.enabled = e.enabled;
 /// }
 ///
 /// 于是只要这一次调用里 `source.GetCameraEffects()` 拿到空列表（或全被
 /// IsAllowedEffect 挡掉），**VR 相机就会以"零后处理"的状态落地**，而且
 /// 不会有任何东西再把它补回来 → 一直灰到下次 CopyFX。
 ///
 /// 这也是为什么旧版 ForceEnable() 报"修正 0 个"却画面仍然是灰的：
 /// 它只检查 `m.enabled`，组件都不存在时自然"没有需要修正的项"，
 /// 于是给出一个**假的安全结论**。这个日志误导了排查方向很久。
 ///
 /// 现在补上 EnsurePresent()：发现组件**缺失**（不是禁用）时，
 /// 走 `VR.Camera.Refresh()` 重新从源相机复制一次；复制前把源相机打开，
 /// 保证这次复制出来的是 enabled=true。
 /// </summary>
 internal static class ColorGradingFix
 {
 /// <summary>
 /// 要强制重新启用的后处理组件名（精确匹配）。
 /// 注意：**故意不含 GlobalFog** —— 雾效被误打开会让画面更糟，
 /// 而且诊断里 VR 相机上本来就没有它。
 ///
 /// **AmplifyOcclusionEffect（环境遮挡）默认不再打理**，
 /// 由配置 `Fixes/FixOcclusion`（默认 false）控制，原因：
 /// · 原版的行为是「CopyFX 复制一次后源被禁用，第二次加载起 VR 相机上的 AO
 /// 也被复制成 disabled → AO 消失」；AO 对「发灰」修复没有贡献；
 /// · 我们旧版每 2 秒 EnableOn 重新打开它、缺失时 EnsurePresent 重建
 /// （AddComponent + 只复制 public 字段，缺 AO 的私有序列化字段），
 /// 用户实测「双眼看黑阴影角度错位」——AO 被以错误状态持续显示。
 /// 现在默认放过 AO（= 原版行为）；把 FixOcclusion 打开可恢复 及以前的处理。
 /// </summary>
 private static readonly string[] EffectNames =
 {
 "AmplifyColorEffect",
 "AmplifyColor"
 };

 /// <summary>是否打理 AmplifyOcclusionEffect（环境遮挡）。默认 false = 交给原版。</summary>
 private static bool _fixOcclusion;

 // ---------------- "用户本来想要它开还是关"的记忆 ----------------
 //
 // 【为什么需要】VRGIN 的 CopyFX 复制完后会做 `source.enabled = false`
 // （`disabledSourceFx: true`），所以从第二次加载起，**源相机上所有后处理的
 // enabled 都是 VRGIN 关的 false，那不是用户的选择**。
 //
 // 而旧版 `CopyFXPrefix` / `EnableOn` 是无条件 `enabled = true`：
 // 用户在「画面效果」里亲手关掉的效果（比如 AmplifyOcclusionEffect 环境光遮蔽），
 // 会被我们强行打开并复制到 VR 相机 → **画面质感和原版不一样**（用户实测）。
 //
 // 现在的规则（按效果类型记忆"期望值"）：
 // · 第一次看到它时，源上的 enabled 就是真相 → 记为期望值；
 // · 之后再看到"源=enabled"只可能是用户打开的（VRGIN 只会关不会开）
 // → 把期望值升级为 true；
 // · 之后看到"源=disabled" 是 VRGIN 关的，**不改变期望值**。
 // 这样既保住「第二次加载后发灰」的修复，又不再擅自打开用户关掉的效果。
 private static readonly Dictionary<Type, bool> _desiredEnabled = new Dictionary<Type, bool>();

 /// <summary>
 /// 源相机效果在被 CopyFX 复制前调用：按"用户期望值"设置 enabled，并返回期望值。
 /// </summary>
 private static bool PrepareSourceEffect(MonoBehaviour e)
 {
 Type t = e.GetType();
 bool raw = e.enabled;

 bool desired;
 if (!_desiredEnabled.TryGetValue(t, out desired))
 {
 // 首次所见即真相（此时 VRGIN 还没关过它）
 desired = raw;
 }
 else if (raw)
 {
 // 之后 source.enabled==true 只可能是用户打开的 → 升级
 desired = true;
 }
 // raw==false == VRGIN 上一轮关的 → 期望值不变

 _desiredEnabled[t] = desired;
 if (e.enabled != desired) e.enabled = desired;
 return desired;
 }

 /// <summary>取某个效果类型的"期望启用状态"；没记住过则按 true 兜底（兼容发灰修复）。</summary>
 private static bool DesiredEnabled(Type t)
 {
 bool v;
 return _desiredEnabled.TryGetValue(t, out v) ? v : true;
 }

 internal static void Install(HarmonyLib.Harmony harmony)
 {
 // AO（环境遮挡）默认不搭理：原版第二次加载后它本来就消失，
 // 而我们重新打开/重建会让它以错误状态持续显示（用户实测双眼黑阴影角度错位）。
 try { _fixOcclusion = KKCharaStudioVRFixPlugin.CfgFixOcclusion != null
 && KKCharaStudioVRFixPlugin.CfgFixOcclusion.Value; }
 catch { _fixOcclusion = false; }
 FixLog.Info("环境遮挡(AmplifyOcclusionEffect)处理: "
 + (_fixOcclusion ? "启用(本插件接管)" : "关闭(交给原版)"));

 // ① 根因修复：在 CopyFX 真正复制之前，先把【源相机】上的色彩分级打开。
 // 这样 VRGIN 复制过去的副本天然就是 enabled=true，不需要事后再补救。
 try
 {
 System.Reflection.MethodInfo privateCopyFx = AccessTools.Method(
 typeof(VRCamera), "CopyFX",
 new Type[] { typeof(GameObject), typeof(GameObject), typeof(bool) });
 if (privateCopyFx != null)
 {
 harmony.Patch(privateCopyFx, prefix: new HarmonyMethod(
 AccessTools.Method(typeof(ColorGradingFix), "CopyFXPrefix")));
 FixLog.Info("已挂载钩子: VRCamera.CopyFX(GameObject,GameObject,bool) [根因修复]");
 }
 else
 {
 FixLog.Debug("未找到 VRCamera.CopyFX 私有重载，只能依赖事后补救。");
 }
 }
 catch (Exception e)
 {
 FixLog.Warn("挂载 CopyFX 私有重载失败: " + e.Message);
 }

 // ② 兜底：复制完成后立刻检查一次（防止还有别的路径把效果关掉）
 try
 {
 System.Reflection.MethodInfo publicCopyFx = AccessTools.Method(
 typeof(VRCamera), "CopyFX", new Type[] { typeof(Camera) });
 if (publicCopyFx != null)
 {
 harmony.Patch(publicCopyFx, postfix: new HarmonyMethod(
 AccessTools.Method(typeof(ColorGradingFix), "CopyFXPostfix")));
 FixLog.Info("已挂载钩子: VRCamera.CopyFX(Camera) [兜底]");
 }
 }
 catch (Exception e)
 {
 FixLog.Warn("挂载 VRCamera.CopyFX(Camera) 失败: " + e.Message);
 }
 }

 /// <summary>
 /// 【根因修复】CopyFX 复制前，把源相机上的色彩分级效果打开。
 ///
 /// 为什么这样就能根治：
 /// CopyFX 的逻辑是 `copy.enabled = source.enabled`，然后再把 source 禁用。
 /// 第 1 次调用时 source 还是 enabled=true → 副本正常；
 /// 第 2 次调用时 source 已经被上一次禁成 false → 副本被复制成 false → 画面变灰。
 /// 我们在复制前把 source 打开，副本就永远拿到 true；
 /// 随后 VRGIN 自己会把 source 再关掉（它的设计意图），所以不会双重叠加效果。
 /// </summary>
 public static void CopyFXPrefix(GameObject source)
 {
 try
 {
 if (KKCharaStudioVRFixPlugin.CfgReapplyColorGrading == null
 || !KKCharaStudioVRFixPlugin.On(KKCharaStudioVRFixPlugin.CfgReapplyColorGrading))
 return;
 if (source == null) return;

 // 按"用户期望值"设置，而不是无条件打开 ——
 // 无条件打开会让用户在画面效果里关掉的效果又冒出来。
 int n = 0;
 MonoBehaviour[] comps = source.GetComponents<MonoBehaviour>();
 for (int i = 0; i < comps.Length; i++)
 {
 MonoBehaviour e = comps[i];
 if (e == null) continue;
 if (!IsWatchedEffectName(e.GetType().Name)) continue;
 if (PrepareSourceEffect(e)) n++;
 }
 if (n > 0)
 FixLog.Info("CopyFX 复制前设置源相机(" + source.name + ")的后处理 enabled="
 + n + " 个 —— 避免 VR 相机副本被复制成 disabled（根治变灰， 起按用户选择，不再强行全开）。");
 }
 catch (Exception e)
 {
 FixLog.Debug("CopyFXPrefix 异常: " + e.Message);
 }
 }

 /// <summary>CopyFX 刚把 VR 相机的效果复制/禁用完 → 立刻纠正（兜底）。</summary>
 public static void CopyFXPostfix()
 {
 try
 {
 if (KKCharaStudioVRFixPlugin.CfgReapplyColorGrading == null
 || !KKCharaStudioVRFixPlugin.On(KKCharaStudioVRFixPlugin.CfgReapplyColorGrading))
 return;

 ForceEnable("VRCamera.CopyFX 之后");
 }
 catch (Exception e)
 {
 FixLog.Debug("CopyFXPostfix 异常: " + e.Message);
 }
 }

 /// <summary>把 VR 相机上被误禁用的后处理重新打开。返回修正数量。</summary>
 /// <summary>
 /// 加载开始时调用：强制下一次 ForceEnable 重新做存在性检查。
 /// 因为 CopyFX 的 DestroyImmediate 正是在这个时间点销毁组件。
 /// </summary>
 internal static void InvalidateExistenceCache()
 {
 _lastHadComponents = false;
 _lastExistenceCheckTime = -999f;
 }

 internal static int ForceEnable(string reason)
 {
 try
 {
 Camera vcam = GetVrCamera();
 if (vcam == null) return 0;

 int n = EnableOn(vcam.gameObject);
 if (n > 0)
 FixLog.Info("重新启用后处理 [" + reason + "]: 共 " + n + " 个。");

 // ★ ：不只是"启用"，还要检查**是否存在**。
 // 组件被 CopyFX 的 DestroyImmediate 销毁后，EnableOn() 会返回 0，
 // 旧版就此收工 → 画面持续发灰。这里补一次重建。
 if (EnsurePresent(reason, vcam))
 n += 1;

 return n;
 }
 catch (Exception e)
 {
 FixLog.Debug("ForceEnable 异常: " + e.Message);
 return 0;
 }
 }

 /// <summary>
 /// 确保 VR 相机上**存在**被监视的后处理组件。
 ///
 /// 触发条件：VR 相机上一个被监视的效果组件都没有，但源相机（Studio 主相机）上有。
 /// 处理方式：**自己** AddComponent + 复制公开字段（等价于 VRGIN 的 CopyComponentFrom）。
 ///
 /// 
 /// 这里调用了 `VR.Camera.Refresh()`，结果稳定报
 /// EnsurePresent 异常: Object reference not set to an instance of an object
 /// 因为 Refresh() → CopyFX(Blueprint)，而 CopyFX 内部要访问
 /// `VR.Camera.SteamCam` / `Blueprint` 等成员，在加载过程中这些引用会瞬时为 null。
 /// 也就是说：**在"正在加载"这个时间点调用 VRGIN 自己的重活是不安全的**。
 /// 现在改成自己复制组件，不依赖 VRGIN 的任何中间状态。
 ///
 /// 复制的字段口径与 VRGIN 保持一致（只复制 public 实例字段），
 /// 但**跳过 enabled**：我们显式把副本设成 enabled=true，
 /// 因为源相机上的效果此刻正是 disabled（CopyFX 的 disabledSourceFx 干的），
 /// 照抄过来就还是灰的。
 ///
 /// 返回是否执行了重建。
 /// </summary>
 /// <summary>
 /// 最近一次"组件存在性"检查的结果与时间。
 ///
 /// CountWatched() 会调用 GetComponents&lt;MonoBehaviour&gt;() —— 每次都分配数组。
 /// 而 ForceEnable() 每 0.5 秒被调一次，等于每 0.5 秒分配一次数组 + 遍历。
 /// 更重要的是：**组件被销毁只发生在场景/场景卡加载的那一刻**
 /// （VRGIN 的 CopyFX 用 DestroyImmediate），平时根本不会变。
 ///
 /// 所以稳态下没必要每 0.5 秒都去数一遍：只要上一次数出来是"有"，
 /// 且距上次检查不到 ExistenceRecheckSeconds 秒，就先跳过存在性检查，
 /// 只做 `enabled` 的启用（那是极廉价的属性写入）。
 /// </summary>
 private const float ExistenceRecheckSeconds = 3.0f;
 private static float _lastExistenceCheckTime = -999f;
 private static bool _lastHadComponents;

 private static bool EnsurePresent(string reason, Camera vcam)
 {
 try
 {
 if (vcam == null) return false;
 GameObject target = vcam.gameObject;
 if (target == null) return false;

 // 稳态短路：上次确认存在，且刚检查过不久 → 没必要重新数。
 if (_lastHadComponents
 && Time.realtimeSinceStartup - _lastExistenceCheckTime < ExistenceRecheckSeconds)
 return false;

 _lastExistenceCheckTime = Time.realtimeSinceStartup;

 int have = CountWatched(target);
 _lastHadComponents = have > 0;
 if (have > 0) return false; // 组件还在，交给 EnableOn 处理即可

 // VR 相机上一个都没有 → 去源相机找
 Camera source = GetSourceCamera(vcam);
 if (source == null) return false;
 GameObject srcGo = source.gameObject;
 if (srcGo == null || srcGo == target) return false;

 int srcHave = CountWatched(srcGo);
 if (srcHave <= 0) return false; // 源相机也没有，说明这张图本来就没色彩分级

 FixLog.Warn("VR 相机上的后处理组件**已被销毁**（当前 0 个，源相机 "
 + source.name + " 有 " + srcHave + " 个）→ 自行重建。[" + reason + "]");

 int rebuilt = 0;
 MonoBehaviour[] srcComps = srcGo.GetComponents<MonoBehaviour>();
 for (int i = 0; i < srcComps.Length; i++)
 {
 MonoBehaviour src = srcComps[i];
 if (src == null) continue;

 Type t = src.GetType();
 if (!IsWatchedEffect(t.Name)) continue;

 try
 {
 // AddComponent(type) —— 与 VRGIN 的 CopyComponentFrom 同一手法
 Component added = target.AddComponent(t);
 if (added == null) continue;

 CopyPublicFields(src, added, t);

 // 【关键】按用户期望值启用。源相机上的那个此刻是 disabled（被 CopyFX 关的），
 // 照抄 enabled 会让副本也变灰 —— 必须按源相机的实际状态重建。
 // 但如果用户本来就要它关着（首见时为 false），
 // 就按 disabled 补回来，不再强行打开。
 Behaviour b = added as Behaviour;
 if (b != null) b.enabled = DesiredEnabled(t);

 rebuilt++;
 FixLog.Debug(" 重建后处理组件: " + t.Name + " @ " + target.name
 + " (enabled=True)");
 }
 catch (Exception e)
 {
 FixLog.Debug(" 重建组件 " + t.Name + " 失败: " + e.Message);
 }
 }

 if (rebuilt > 0)
 FixLog.Key("已重建 " + rebuilt + " 个后处理组件（发灰修复），当前 VR 相机上 "
 + CountWatched(target) + " 个。[" + reason + "]");

 return rebuilt > 0;
 }
 catch (Exception e)
 {
 FixLog.Debug("EnsurePresent 异常: " + e.Message);
 return false;
 }
 }

#if !LEAN
 // 【精简版瘦身】诊断输出 / 只被诊断调用的工具方法。
 /// <summary>
 /// 复制组件的 public 实例字段（与 VRGIN GameObjectExtensions.CopyComponentFrom 同口径）。
 /// **跳过 enabled / name** —— enabled 由调用方显式设定，name 不属于效果参数。
 /// </summary>
 /// <summary>
 /// 把源组件的效果参数拷到 VR 相机的副本上。
 ///
 /// 
 /// 平面模式改完画面效果**立刻生效**，因为组件就在正在渲染的相机上；
 /// VR 里 VRGIN 把效果**复制**到 VR 相机（副本），副本不会自动跟着源变 ——
 /// 以前只能靠"每 1 秒反射算 hash 比对"来同步（有延迟、且 GetFields 每次分配数组）。
 ///
 /// 现在改成**每帧直接拷字段**：
 /// · 字段列表走缓存（不再每次 GetFields 分配数组）；
 /// · 只同步"用户能改的参数"（值类型 + Texture 引用），
 /// 跳过 Unity 内部状态字段（如 m_CullTransparentMesh、hideFlags 等），
 /// 避免覆盖副本运行期的内部值；
 /// · 每帧成本 = 5~10 次 GetValue/SetValue（约 3µs），比 1 秒反射算 hash 更省。
 ///
 /// 结果：**0 延迟**，与平面模式完全一致。
 /// </summary>

 /// <summary>按名字读组件的 Texture 字段（反射，字段不存在返回 null）。</summary>
 private static Texture ReadTextureField(Component c, string fieldName)
 {
 try
 {
 FieldInfo f = c.GetType().GetField(fieldName,
 BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
 if (f == null) return null;
 return f.GetValue(c) as Texture;
 }
 catch { return null; }
 }
#else
 // 【LEAN】诊断输出整段删除（只给完整版）
#endif

 /// <summary>
 /// 每帧同步用的**双指纹**。
 ///
 /// 必须把"要重建的"和"改值就行的"分开 —— 这是实测日志给出的硬证据：
 /// `变化的字段：BlendAmount(Single) 0.2 → 1; LutTexture(Texture) Lut_Amount → Lut_Illust2`
 /// 用户拖「滤镜比例」滑块时 BlendAmount **每帧都在变**，而 的逻辑是
 /// "任何参数变化都销毁重建" → 会**每帧 DestroyImmediate + AddComponent**。
 ///
 /// · Texture / Material 引用变化（= 换滤镜换 LUT）→ **必须重建**
 /// （AmplifyColorEffect 的 lut3D 内部缓存只在 Awake 时初始化）；
 /// · 值类型标量变化（BlendAmount / 颜色 / 向量）→ **直接 SetValue 即时生效**，
 /// 重建纯属浪费（一次重建会打断后处理好几帧）。
 ///
 /// 另外：指纹只统计"用户可调"的这两类字段，**不再把私有运行期字段算进去** ——
 /// 以前把全部字段（含内部缓存）纳入指纹，只要有一个自变就会持续白重建。
 /// </summary>
 private static readonly System.Collections.Generic.Dictionary<Type, int> _srcTexHash
 = new System.Collections.Generic.Dictionary<Type, int>(4);
 private static readonly System.Collections.Generic.Dictionary<Type, int> _srcScalarHash
 = new System.Collections.Generic.Dictionary<Type, int>(4);

 /// <summary>标量同步日志的节流时间戳（拖滑块时每帧都在变，不能每帧拼字符串）。</summary>
 private static float _lastScalarLog = -999f;

 private static void RebuildEffect(GameObject vrGo, Component srcComp, Type t)
 {
 try
 {
 Component old = vrGo.GetComponent(t);
 if (old != null)
 {
 try { UnityEngine.Object.DestroyImmediate(old); } catch { }
 }
 Component fresh = vrGo.AddComponent(t);
 if (fresh == null) return;
 CopyPublicFields(srcComp, fresh, t);
 // 再补拷「**私有** Texture 字段」——
 // AmplifyColorEffect 的 LUT 引用很可能是私有字段（DLL 混淆，名字也认不出），
 // 只拷 public 会让新建的组件拿默认 LUT → 滤镜看起来没变。
 // 只拷 Texture 类型：这类字段就是 LUT/贴图，不涉及内部运行状态，安全。
 CopyPrivateTextures(srcComp, fresh, t);
 Behaviour b = fresh as Behaviour;
 if (b != null) b.enabled = DesiredEnabled(t);
 }
 catch (Exception e) { FixLog.Debug("RebuildEffect 异常: " + e.Message); }
 }

 // ================= 字段快照 + 差异 =================
 // 目的：用户改「滤镜」后如果迟迟不生效，日志必须能回答两个问题：
 // ① 我们的指纹有没有跟着变（有没有测到）？
 // ② 如果测到了，变的是**哪个字段**（是不是 LUT）？
 private static readonly System.Collections.Generic.Dictionary<Type,
 System.Collections.Generic.Dictionary<string, object>> _lastVals
 = new System.Collections.Generic.Dictionary<Type,
 System.Collections.Generic.Dictionary<string, object>>();

 private static int _rebuildCount;
 private static float _rebuildWindowStart = -999f;

 private static string FmtVal(object v)
 {
 if (v == null) return "null";
 UnityEngine.Object uo = v as UnityEngine.Object;
 if (uo != null) return uo.name + "#" + uo.GetInstanceID();
 string s2 = v.ToString();
 return (s2.Length > 24) ? s2.Substring(0, 24) + "…" : s2;
 }

 /// <summary>列出相对上次快照发生变化的字段（并刷新快照）。只在诊断开启时调用。</summary>
 private static string DiffFields(Component src, Type t)
 {
 System.Text.StringBuilder sb = new System.Text.StringBuilder();
 try
 {
 System.Collections.Generic.Dictionary<string, object> prev;
 if (!_lastVals.TryGetValue(t, out prev)) prev = null;
 System.Collections.Generic.Dictionary<string, object> cur
 = new System.Collections.Generic.Dictionary<string, object>();

 FieldInfo[] fs = CachedAllFields(t);
 for (int i = 0; i < fs.Length; i++)
 {
 FieldInfo f = fs[i];
 if (f == null || f.IsStatic) continue;
 object v = null;
 try { v = f.GetValue(src); } catch { continue; }
 cur[f.Name] = v;
 if (prev == null) continue;
 object pv;
 if (!prev.TryGetValue(f.Name, out pv)) continue;
 bool changed;
 UnityEngine.Object pu = pv as UnityEngine.Object, cu = v as UnityEngine.Object;
 if (pu != null || cu != null) changed = !ReferenceEquals(pu, cu);
 else changed = !Equals(pv, v);
 if (changed)
 {
 if (sb.Length > 0) sb.Append("; ");
 sb.Append(f.Name).Append('(').Append(f.FieldType.Name).Append(") ")
 .Append(FmtVal(pv)).Append(" → ").Append(FmtVal(v));
 }
 }
 _lastVals[t] = cur;
 if (sb.Length > 400) sb.Length = 400;
 }
 catch { }
 return sb.ToString();
 }

 /// <summary>统计重建频率：若 10 秒内重建超过 20 次，说明有字段在自变（要按日志排除）。</summary>
 private static void NoteRebuild()
 {
 try
 {
 if (Time.realtimeSinceStartup - _rebuildWindowStart > 10f)
 {
 _rebuildWindowStart = Time.realtimeSinceStartup;
 _rebuildCount = 0;
 }
 _rebuildCount++;
 if (_rebuildCount == 20 && KKCharaStudioVRFixPlugin.DiagOn)
 FixLog.Warn("【画面效果同步】10 秒内重建已达 20 次 —— 说明被指纹的某个字段在自变，"
 + "请看上面「变化的字段」列表把它排除（否则会持续销毁重建、白烧性能）。");
 }
 catch { }
 }

 /// <summary>把源组件上「非 public 的 Texture 字段」也拷过去（只拷 Texture 类型，避免污染内部状态）。</summary>
 private static int CopyPrivateTextures(Component from, Component to, Type t)
 {
 int n = 0;
 try
 {
 FieldInfo[] fs = CachedAllFields(t);
 for (int i = 0; i < fs.Length; i++)
 {
 FieldInfo f = fs[i];
 if (f == null || f.IsPublic || f.IsStatic) continue;
 if (!typeof(Texture).IsAssignableFrom(f.FieldType)) continue;
 try
 {
 object v = f.GetValue(from);
 if (v == null) continue;
 f.SetValue(to, v);
 n++;
 if (KKCharaStudioVRFixPlugin.DiagOn)
 FixLog.Key("【画面效果同步】已补拷私有贴图字段 " + t.Name + "." + f.Name
 + " = " + ((Texture)v).name + "（只拷 public 会丢 LUT）。");
 }
 catch { }
 }
 }
 catch { }
 return n;
 }

 private static void CopyPublicFields(Component from, Component to, Type type)
 {
 try
 {
 FieldInfo[] fields = CachedFields(type);
 for (int i = 0; i < fields.Length; i++)
 {
 FieldInfo f = fields[i];
 if (f == null) continue;
 if (f.Name == "enabled" || f.Name == "name") continue;
 // 只同步用户可调参数：基础值类型 / Color / Vector / Texture。
 // 其它（Unity 内部对象、只读、委托等）一律跳过 —— 覆盖它们会破坏副本运行状态。
 Type ft = f.FieldType;
 if (!(ft.IsPrimitive || ft.IsEnum
 || ft == typeof(Color) || ft == typeof(Vector2)
 || ft == typeof(Vector3) || ft == typeof(Vector4)
 || ft == typeof(Quaternion)
 || typeof(Texture).IsAssignableFrom(ft)
 || typeof(Material).IsAssignableFrom(ft)))
 continue;
 try { f.SetValue(to, f.GetValue(from)); }
 catch { /* 个别字段（如只读/受限类型）复制失败不影响主体 */ }
 }
 }
 catch (Exception e)
 {
 FixLog.Debug("CopyPublicFields 异常: " + e.Message);
 }
 }

 /// <summary>数一下这个 GameObject 上有几个被监视的效果组件。</summary>
 private static int CountWatched(GameObject go)
 {
 if (go == null) return 0;
 MonoBehaviour[] comps = go.GetComponents<MonoBehaviour>();
 int n = 0;
 for (int i = 0; i < comps.Length; i++)
 {
 if (comps[i] == null) continue;
 if (IsWatchedEffect(comps[i].GetType().Name)) n++;
 }
 return n;
 }

 /// <summary>
 /// 把源相机（VRGIN Blueprint / Studio 主相机）上的效果**参数**同步到 VR 相机，
 /// 并恢复 VRGIN「只有一份在算」的不变量。两个用户问题都由这里解释（反编译 VRGIN_KKCS 的
 /// `VRCamera.CopyFX` 得到，非猜测）：
 ///
 /// · `CopyFX(blueprint)` 的语义 = 把源相机的效果复制到 VR 相机，然后
 /// **`disabledSourceFx: true` → 把源相机上的那些效果 `enabled = false`**
 /// （源码：`if (disabledSourceFx) ((Behaviour)cameraEffect2).enabled = false;`）
 /// —— 引擎的本意就是「只有 VR 相机那一份在算」。
 /// · 而本项目的 `EnsurePresent()` 在「VR 相机上已有组件」时**直接 return**
 /// （`if (have > 0) return false;`）→ **没有任何路径把源相机的新参数复制过来**，
 /// 所以场景卡里改过的画面效果传不到 VR 相机。
 /// · 修好 rig 死亡后，原来「rig 重建 → 相机重新收养 → CopyFX → 源相机那份被关掉」
 /// 这个**意外清理动作消失了**；Studio 加载场景卡/改画面效果时会把源相机那份重新打开
 /// → **两份同时在算**：又慢（后处理跑两遍）又不对。
 ///
 /// 本方法（可关，配置 SyncEffectsFromSource）：
 /// ① `copyParams=true` 时：源相机 → VR 相机同步 public 实例字段（跳过 enabled）；
 /// ② `disableSourceCopy=true` 时：VR 相机上已有**启用中的**同类型副本 → 把源相机那份关掉
 /// （就是 VRGIN CopyFX 的行为，恢复它的不变量）。
 /// 全程打印 before/after 参数指纹，便于验证到底有没有真的同步过去。
 /// </summary>
 private static MonoBehaviour[] _cachedSrcComps;
 private static GameObject _cachedSrcGo;
 private static float _cachedSrcCompsTime = -999f;

 /// <summary>源相机的组件列表缓存（见 SyncFromSource 里的性能注释）。</summary>
 private static MonoBehaviour[] GetCachedSourceComps(GameObject srcGo)
 {
 try
 {
 bool need = _cachedSrcComps == null
 || _cachedSrcGo != srcGo
 || Time.realtimeSinceStartup - _cachedSrcCompsTime > 10f;
 if (!need)
 {
 // 缓存里若有组件被销毁（Unity fake-null）→ 立刻重扫
 for (int i = 0; i < _cachedSrcComps.Length; i++)
 {
 if (_cachedSrcComps[i] == null) { need = true; break; }
 }
 }
 if (need)
 {
 _cachedSrcComps = srcGo.GetComponents<MonoBehaviour>();
 _cachedSrcGo = srcGo;
 _cachedSrcCompsTime = Time.realtimeSinceStartup;
 }
 return _cachedSrcComps;
 }
 catch { return null; }
 }

 // ================= 分配量测量 =================
 // 用户假设（需数据裁决）："插件导致 GC 频繁回收，回收停顿造成 53→48 抖动；
 // 关掉 GC 就流畅 = 变相解决"。
 // 要验证必须知道**本插件的每帧同步制造了多少垃圾**。
 // `GC.GetTotalMemory(false)` 很贵（遍历堆），所以**每秒只采样一次**
 // （在 SyncFromSource 被每帧调用时，累计帧数，到 1 秒边界测一次增量）。
 private static long _allocSampleBefore;
 private static long _lastReportedAlloc; // 最近一次采样到的分配量（字节）

#if !LEAN
 // 【精简版瘦身】诊断输出 / 只被诊断调用的工具方法。
 /// <summary>由 FPS 计每秒调用：取走上秒的分配量（字节）。</summary>
 internal static long TakeAllocSample()
 {
 long v = _lastReportedAlloc;
 _lastReportedAlloc = 0;
 return v;
 }
#else
 // 【LEAN】诊断输出整段删除（只给完整版）
#endif

 /// <summary>
 /// 分配量采样。
 /// `GC.GetTotalMemory(false)` 要**遍历整个堆** ——
 /// 我们的堆有 1.2GB，每次调用就要几毫秒。我第一版"每 60 帧采样两次"
 /// 等于**每秒制造一次几毫秒的尖峰**，那本身就是"53→48"抽帧的来源之一。
 /// 现在：① 只在诊断总开关（DiagOn）开启时才采样；② 频率降到 300 帧（约 5 秒）一次，
 /// 让测量本身的开销可以忽略。
 /// </summary>
#if !LEAN
 // 【精简版瘦身】只有完整版用得到
 private static int _sampleCountdown = 300;
#endif
 private static bool _pendingSample;
 private static bool _sampling;

#if !LEAN
 // 【精简版瘦身】效果现场导出 / 分配量采样：纯诊断。
 private static void SampleAlloc(bool begin)
 {
 if (!KKCharaStudioVRFixPlugin.DiagOn) return; // 诊断关 → 完全零开销
 try
 {
 if (begin)
 {
 if (_pendingSample)
 {
 _allocSampleBefore = GC.GetTotalMemory(false);
 _pendingSample = false;
 _sampling = true;
 }
 return;
 }
 if (_sampling)
 {
 _lastReportedAlloc = GC.GetTotalMemory(false) - _allocSampleBefore;
 _sampling = false;
 _sampleCountdown = 300; // 下一轮再等 300 帧（约 5 秒）
 }
 else if (--_sampleCountdown <= 0)
 {
 _pendingSample = true;
 }
 }
 catch { }
 }
#else
 // 【LEAN】诊断导出整段删除（只给完整版）
#endif

 internal static int SyncFromSource(string reason, bool copyParams, bool disableSourceCopy)
 {
#if !LEAN
 SampleAlloc(true); // 分配量采样起点
#endif
 try
 {
 return SyncFromSourceInner(reason, copyParams, disableSourceCopy);
 }
 finally
 {
#if !LEAN
 SampleAlloc(false); // 无论走哪条 return 都上报
#endif
 }
 }

 private static int SyncFromSourceInner(string reason, bool copyParams, bool disableSourceCopy)
 {
 try
 {
 Camera vcam = GetVrCamera();
 if (vcam == null) return 0;
 GameObject vrGo = vcam.gameObject;
 if (vrGo == null) return 0;

 Camera src = GetSourceCamera(vcam);
 if (src == null) return 0;
 GameObject srcGo = src.gameObject;
 if (srcGo == null || srcGo == vrGo) return 0;

 // `GetComponents<MonoBehaviour>()` 每秒调一次会每秒分配一个数组。
 // 组件列表只在加载/切换时才会变，所以缓存它：
 // · 相机 GameObject 变了 → 重扫；
 // · 缓存里有被销毁的组件（Unity fake-null）→ 重扫；
 // · 兜底每 10 秒重扫一次。
 MonoBehaviour[] srcComps = GetCachedSourceComps(srcGo);
 if (srcComps == null || srcComps.Length == 0) return 0;

 int synced = 0, disabled = 0;
 for (int i = 0; i < srcComps.Length; i++)
 {
 MonoBehaviour srcComp = srcComps[i];
 if (srcComp == null) continue;
 Type t = srcComp.GetType();
 if (!IsWatchedEffect(t.Name)) continue;

 Component vrComp = null;
 try { vrComp = vrGo.GetComponent(t); } catch { }
#if NEWFIX
 if (vrComp == null)
 {
 // 【NEWFIX】组件被 VRGIN 的 CopyFX 销毁了 → **当场补建**。
 // 以前这里只 continue，靠看门狗那个 30 秒兜底任务重建 →
 // 于是"画面变灰"最长要等 30 秒才恢复（等于没修好）。
 try { EnsurePresent("每帧同步：组件缺失", vcam); } catch { }
 try { vrComp = vrGo.GetComponent(t); } catch { }
 if (vrComp == null) continue;
 }
#else
 if (vrComp == null) continue; // 不存在 → 交给 EnsurePresent 重建
#endif

 // ① 参数同步（这就是「卡片效果传不过来」的修法）
 //
 // 改成"只在**源相机**参数变过时才复制"：
 // · 用户想调的画面效果下拉（滤镜/阴影表现）改的是**组件上的参数**，
 // 而渲染的是 VR 相机上的副本 → 不同步就永远看不到变化；
 // · 但每秒无条件复制会跟效果自己运行期的字段变化打架（互相覆盖）。
 // 所以用源相机的参数指纹做判据：源变了才复制（= 用户在 Studio 里动了设置），
 // 源没变就什么都不做（零开销、不打架）。
 if (copyParams)
 {
 // 区分两类字段，解决"换滤镜/LUT 有延迟"：
 //
 // · **标量**（BlendAmount、颜色等）→ 直接 SetValue，立即生效；
 // · **Texture 引用**（LutTexture 等）→ 必须**销毁重建**整个组件！
 // 原因：AmplifyColorEffect 内部有私有缓存（lut3D 3D 查找纹理、
 // lut3DName 等），只在 Awake/OnEnable 时才初始化。
 // 单纯 SetValue 换掉 LutTexture，内部缓存不会重建 → 画面不更新。
 // VRGIN 自己的 CopyFX 之所以有效，也是因为"销毁旧的 + 新建组件"。
 //
 // ~58 用"全字段指纹"，只要有任何参数变化
 // 就销毁重建 —— 而用户拖「滤镜比例」滑块时 BlendAmount 是**每帧都在变**的
 // （日志硬证据：`变化的字段：BlendAmount(Single) 0.2 → 1`），
 // 那会导致**每帧 DestroyImmediate + AddComponent**。
 // 现在按字段类型分开：
 // Texture/Material 变 → 重建（LUT 内部缓存必须重建）
 // 只有标量变 → 直接 SetValue（0 延迟，零重建）
 int texNow, scalarNow;
 ParamHashes(srcComp, t, out texNow, out scalarNow);

 int prevTex, prevScalar;
 bool knownTex = _srcTexHash.TryGetValue(t, out prevTex);
 bool knownScalar = _srcScalarHash.TryGetValue(t, out prevScalar);

 if (!knownTex || prevTex != texNow)
 {
 _srcTexHash[t] = texNow;
 _srcScalarHash[t] = scalarNow;
 // 先把「哪些字段变了」落盘 —— 这是判断
 // 「改滤镜到底改了哪个字段、我们有没有测到」的唯一硬证据。
 string diff = (KKCharaStudioVRFixPlugin.DiagOn
 && knownTex) ? DiffFields(srcComp, t) : "";
 RebuildEffect(vrGo, srcComp, t);
 synced++;
 if (KKCharaStudioVRFixPlugin.DiagOn)
 FixLog.Key("【画面效果同步】" + reason + "：" + t.Name
 + " 贴图/材质变化（指纹 " + prevTex + " → " + texNow + "）→ 已销毁重建"
 + (diff.Length > 0 ? "\n 变化的字段：" + diff : "")
 + (knownTex ? "" : "（首次见到，建立基线指纹）"));
 NoteRebuild();
 }
 else
 {
 _srcScalarHash[t] = scalarNow;
 if (!knownScalar || prevScalar != scalarNow)
 {
 // 标量变化（拖「滤镜比例」/改颜色）→ 直接改值，不重建。
 CopyPublicFields(srcComp, vrComp, t);
 // 拖滑块时每帧都在变，日志按 1 秒节流（拼字符串本身也有成本）。
 if (KKCharaStudioVRFixPlugin.DiagOn
 && Time.realtimeSinceStartup - _lastScalarLog > 1f)
 {
 _lastScalarLog = Time.realtimeSinceStartup;
 FixLog.Key("【画面效果同步】" + reason + "：" + t.Name
 + " 标量参数变化 → 已直接改值（不重建，0 延迟）"
 + "\n 变化的字段：" + DiffFields(srcComp, t));
 }
 }
 }
 }

 // 保证 VR 副本按用户意图启用（尊重「画面效果里亲手关掉」的选择）
 Behaviour vrB = vrComp as Behaviour;
 if (vrB != null && !vrB.enabled && DesiredEnabled(t))
 vrB.enabled = true;

 // ② 消除双重后处理：VR 副本已在算，源相机那份就不该再算
 if (disableSourceCopy)
 {
 Behaviour srcB = srcComp as Behaviour;
 if (srcB != null && srcB.enabled && vrB != null && vrB.enabled)
 {
 srcB.enabled = false;
 disabled++;
 }
 }
 }

 if (disabled > 0)
 {
 FixLog.Key("【画面效果同步】" + reason + "：发现源相机(" + src.name
 + ")上有 " + disabled + " 个效果与 VR 相机的副本**同时在算**（后处理跑两遍，"
 + "既费性能又会看起来不对）→ 已按 VRGIN CopyFX 的本意关掉源相机那份。");
 }
 return synced + disabled;
 }
 catch (Exception e)
 {
 FixLog.Debug("SyncFromSource 异常: " + e.Message);
 return 0;
 }
 }

#if !LEAN
 // 【精简版瘦身】效果现场导出 / 分配量采样：纯诊断。
 /// <summary>诊断/日志用：VR 相机上现存几个被监视的效果组件。</summary>
 /// <summary>
 /// 把"源相机 vs VR 相机"上**全部**效果组件列出来，并各带一个参数指纹。
 ///
 /// 目的：用户报"加载场景卡后画面效果不像前几版那样加载"。
 /// 机制（反编译 VRGIN_KKCS 的 VRCamera.CopyFX）：VR 相机上的效果是**副本**，
 /// 只在 VRGIN"收养相机"（VRManager.OnUpdate → ApplyEffects → CopyFX）时才重新复制；
 /// 而本项目的 CameraAdoptionFilter 只承认 Studio 主相机，收养很少再触发 →
 /// **卡片里新改的效果设置可能永远传不到 VR 相机**（VR 相机上留着上一张卡的旧参数）。
 /// 这个假设必须用数据验证：两张不同效果的卡各加载一次，比较下面的指纹是否跟着变。
 /// 指纹 = 各效果组件 public 实例字段值的 hash 之和（只读、不修改任何东西）。
 /// </summary>
 internal static void DumpEffectState(string reason)
 {
 if (!KKCharaStudioVRFixPlugin.DiagOn) return; // 诊断总开关
 try
 {
 Camera vcam = GetVrCamera();
 Camera src = (vcam != null) ? GetSourceCamera(vcam) : null;
 string vrText = (vcam == null) ? "<无 VR 相机>" : DescribeEffects(vcam.gameObject);
 string srcText = (src == null) ? "<无源相机>" : DescribeEffects(src.gameObject);
 // 关键判据：源相机到底还在不在渲染？
 // 日志显示源相机与 VR 相机的 AmplifyOcclusionEffect **都是 en=True 且参数相同** ——
 // 若源相机仍在渲染，就是"最贵的后处理（AO）算了两遍"，属于**每帧固定 GPU 开销**，
 // 能直接解释"原版 55 帧 → 现在 26 帧"。Camera.allCameras 只包含**启用中**的相机，
 // 用它 + enabled/targetTexture 三个字段就能定性。
 string srcRender = "<无源相机>";
 if (src != null)
 {
 bool inAll = false;
 try
 {
 Camera[] all = Camera.allCameras;
 for (int i = 0; i < all.Length; i++) if (all[i] == src) { inAll = true; break; }
 }
 catch { }
 srcRender = "enabled=" + src.enabled + " activeInHierarchy=" + src.gameObject.activeInHierarchy
 + " targetTexture=" + (src.targetTexture == null ? "null(直接上屏)" : src.targetTexture.name)
 + " 在Camera.allCameras=" + inAll + " 深度=" + src.depth
 + (inAll ? " ← **源相机仍在渲染**" : " （未参与渲染）");
 }

 FixLog.Key("【画面效果】" + reason + "\n 源相机("
 + (src == null ? "?" : src.name) + ") " + srcRender
 + "\n 组件: " + srcText
 + "\n VR相机(" + (vcam == null ? "?" : vcam.name) + ") = " + vrText);
 }
 catch (Exception e) { FixLog.Debug("DumpEffectState 异常: " + e.Message); }
 }
#else
 // 【LEAN】诊断导出整段删除（只给完整版）
#endif

#if !LEAN
 // 【精简版瘦身】诊断输出。
 private static string DescribeEffects(GameObject go)
 {
 try
 {
 MonoBehaviour[] comps = go.GetComponents<MonoBehaviour>();
 if (comps == null || comps.Length == 0) return "（无组件）";
 System.Text.StringBuilder sb = new System.Text.StringBuilder();
 for (int i = 0; i < comps.Length; i++)
 {
 MonoBehaviour m = comps[i];
 if (m == null) continue;
 string tn = m.GetType().Name;
 // 只列"可能是画面效果"的组件：排除 Unity 自带的相机/音频/脚本胶水
 if (tn == "SteamVR_Camera" || tn == "VRCamera" || tn == "Camera"
 || tn == "AudioListener" || tn == "SteamVR_GameView") continue;
 sb.Append(tn).Append("(en=").Append(m.enabled).Append(",h=")
 .Append(ParamHash(m)).Append(") ");
 }
 return sb.Length == 0 ? "（无效果组件）" : sb.ToString();
 }
 catch (Exception e) { return "<读取失败:" + e.Message + ">"; }
 }
#else
 // 【LEAN】诊断输出整段删除（只给完整版）
#endif

#if !LEAN
 // 【精简版瘦身】诊断输出 / 只被诊断调用的工具方法。
 /// <summary>
 /// 只列出 VR 相机上**启用中**的效果组件类型名（不做反射、不算指纹）。
 /// 用途：和帧率打在同一个 10 秒窗口里，一眼看出"帧数低的时候是不是有重效果在算"。
 /// 每 10 秒才调一次，成本可忽略。
 /// </summary>
 internal static string DescribeEnabledVrEffects()
 {
 try
 {
 Camera vcam = GetVrCamera();
 if (vcam == null) return "<无 VR 相机>";
 MonoBehaviour[] comps = vcam.gameObject.GetComponents<MonoBehaviour>();
 if (comps == null || comps.Length == 0) return "无";
 System.Text.StringBuilder sb = new System.Text.StringBuilder();
 for (int i = 0; i < comps.Length; i++)
 {
 MonoBehaviour m = comps[i];
 if (m == null) continue;
 Behaviour b = m as Behaviour;
 if (b != null && !b.enabled) continue;
 string tn = m.GetType().Name;
 if (tn == "Camera" || tn == "AudioListener" || tn == "SteamVR_Camera"
 || tn == "VRCamera" || tn == "SteamVR_GameView") continue;
 if (sb.Length > 0) sb.Append(',');
 sb.Append(tn);
 }
 return sb.Length == 0 ? "无" : sb.ToString();
 }
 catch (Exception e) { return "<读取失败:" + e.Message + ">"; }
 }
#else
 // 【LEAN】诊断输出整段删除（只给完整版）
#endif

 /// <summary>public 实例字段值的 hash 之和 —— 用于判断"参数是否变过"（只读）。</summary>
 /// <summary>
 /// 按类型缓存 FieldInfo[]。
 /// `Type.GetFields()` **每次调用都分配一个新数组**（约 15 个元素），
 /// 而 ParamHash 每 1 秒被调一次 → 每秒白分配一个数组 + 触发 Boehm GC 扫描。
 /// 缓存后只剩 GetValue 的反射开销。
 /// </summary>
 private static readonly System.Collections.Generic.Dictionary<Type, FieldInfo[]> _fieldCache
 = new System.Collections.Generic.Dictionary<Type, FieldInfo[]>();

 private static FieldInfo[] CachedFields(Type t)
 {
 FieldInfo[] fs;
 if (!_fieldCache.TryGetValue(t, out fs))
 {
 fs = t.GetFields(BindingFlags.Public | BindingFlags.Instance);
 _fieldCache[t] = fs;
 }
 return fs;
 }

 /// <summary>
 /// 含**私有/受保护**字段的列表缓存。
 ///
 /// 为什么要它：用户实测「改滤镜后要等 20~25 秒才生效」。
 /// 而 `AmplifyColorEffect`（实际在 Assembly-CSharp.dll 里）内部有私有缓存字段
 /// （lut3D / lut3DName 等，只在 Awake 时初始化）。所以指纹必须**把私有字段也算进去**，
 /// 才可能覆盖「滤镜选择」这种参数。
 ///
 /// 安全性：被指纹的是**源相机**上的组件，而源相机被 VRGIN 挂了 CameraKiller
 /// （OnPreCull 里 enabled=false）→ 它**不渲染** → 其内部缓存不会自己变化 → 指纹稳定。
 /// 若日志出现「重建过于频繁」告警，说明某个私有字段在自变，再按日志把它排除即可。
 /// </summary>
 private static readonly System.Collections.Generic.Dictionary<Type, FieldInfo[]> _fieldCacheAll
 = new System.Collections.Generic.Dictionary<Type, FieldInfo[]>();

 private static FieldInfo[] CachedAllFields(Type t)
 {
 FieldInfo[] fs;
 if (!_fieldCacheAll.TryGetValue(t, out fs))
 {
 fs = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
 _fieldCacheAll[t] = fs;
 }
 return fs;
 }

 /// <summary>参数指纹（含 public + 私有字段）—— 用于判断「源组件是否变过」。</summary>
 private static int ParamHash(object o)
 {
 int h = 0;
 try
 {
 FieldInfo[] fs = CachedAllFields(o.GetType());
 for (int i = 0; i < fs.Length; i++)
 {
 try
 {
 object v = fs[i].GetValue(o);
 h = h * 31 + (v == null ? 0 : v.GetHashCode());
 }
 catch { }
 }
 }
 catch { }
 return h;
 }

 /// <summary>
 /// 双指纹（每帧调用的就是它）：
 /// · texHash —— 只看 Texture / Material 引用 → 变了必须**销毁重建**；
 /// · scalarHash —— 只看值类型标量 → 变了只需**直接 SetValue**。
 ///
 /// 只统计这两类"用户可调"字段，私有运行期字段一律不算 —— 这样指纹不可能被
 /// 内部缓存自变带偏（旧版把全部字段算进去，才会出现"没人动它却每隔 30 秒重建一次"）。
 /// 非 Texture / 非标量的字段直接 continue，连 GetValue 都不做 → 每帧成本更低。
 /// </summary>
 private static void ParamHashes(Component c, Type t, out int texHash, out int scalarHash)
 {
 texHash = 0; scalarHash = 0;
 try
 {
 FieldInfo[] fs = CachedAllFields(t);
 for (int i = 0; i < fs.Length; i++)
 {
 FieldInfo f = fs[i];
 if (f == null || f.IsStatic) continue;
 Type ft = f.FieldType;
 bool isTex = typeof(Texture).IsAssignableFrom(ft)
 || typeof(Material).IsAssignableFrom(ft);
 bool isScalar = ft.IsPrimitive || ft.IsEnum
 || ft == typeof(Color) || ft == typeof(Vector2)
 || ft == typeof(Vector3) || ft == typeof(Vector4)
 || ft == typeof(Quaternion);
 if (!isTex && !isScalar) continue;
 object v;
 try { v = f.GetValue(c); } catch { continue; }
 int h = (v == null) ? 0 : v.GetHashCode();
 if (isTex) texHash = texHash * 31 + h;
 if (isScalar) scalarHash = scalarHash * 31 + h;
 }
 }
 catch { }
 }

 internal static int CountWatchedVrCamera()
 {
 try
 {
 Camera vcam = GetVrCamera();
 if (vcam == null) return -1;
 return CountWatched(vcam.gameObject);
 }
 catch { return -1; }
 }

#if !LEAN
 // 【精简版瘦身】诊断输出 / 只被诊断调用的工具方法。
 /// <summary>诊断/日志用：源相机（Studio 主相机）上现存几个被监视的效果组件。</summary>
 internal static int CountWatchedSource()
 {
 try
 {
 Camera vcam = GetVrCamera();
 Camera src = GetSourceCamera(vcam);
 if (src == null) return -1;
 return CountWatched(src.gameObject);
 }
 catch { return -1; }
 }
#else
 // 【LEAN】诊断输出整段删除（只给完整版）
#endif

 /// <summary>取 Studio 主相机（VRCamera.Blueprint），拿不到就退化成 Studio.cameraCtrl.mainCmaera。</summary>
 private static Camera GetSourceCamera(Camera vcam)
 {
 try
 {
 // 同上：平面模式不许碰 `VR.Camera`（惰性 getter 会造假 rig）
 if (VRRef.VrRunning && VR.Camera != null && VR.Camera.Blueprint != null)
 return VR.Camera.Blueprint;
 }
 catch { }

 try { return VRRef.GetStudioMainCamera(); }
 catch { return null; }
 }

 /// <summary>
 /// 把指定 GameObject 上"应该开着但被关了"的后处理打开。返回修正数量。
 ///
 /// 只打开"期望值为 true"的效果 —— 用户在画面效果里亲手关掉的
 /// （AmplifyOcclusionEffect 等）保持关闭，不再每 2 秒被强行打开。
 /// </summary>
 private static int EnableOn(GameObject go)
 {
 if (go == null) return 0;

 MonoBehaviour[] comps = go.GetComponents<MonoBehaviour>();
 int fixedCount = 0;

 for (int i = 0; i < comps.Length; i++)
 {
 MonoBehaviour m = comps[i];
 if (m == null) continue;

 Type t = m.GetType();
 if (!IsWatchedEffect(t.Name)) continue;
 if (m.enabled) continue;
 if (!DesiredEnabled(t)) continue; // 用户本来就要它关着

 m.enabled = true;
 fixedCount++;
 FixLog.Debug(" 启用: " + t.Name + " @ " + go.name);
 }

 return fixedCount;
 }

 private static bool IsWatchedEffect(string typeName)
 {
 for (int i = 0; i < EffectNames.Length; i++)
 if (string.Equals(typeName, EffectNames[i], StringComparison.Ordinal))
 return true;
 // 环境遮挡默认不接管（原版行为：第二次加载后消失）。
 if (string.Equals(typeName, "AmplifyOcclusionEffect", StringComparison.Ordinal))
 return _fixOcclusion;
 return false;
 }

 /// <summary>与 <see cref="IsWatchedEffect(string)"/> 同义，给按 Type 调用的地方用。</summary>
 private static bool IsWatchedEffectName(string typeName)
 {
 return IsWatchedEffect(typeName);
 }

#if !LEAN
 // 【精简版瘦身】诊断输出 / 只被诊断调用的工具方法。
 /// <summary>
 /// 【诊断/热键用】当前记住的各效果"期望启用状态"。
 /// </summary>
 internal static string DescribeDesired()
 {
 if (_desiredEnabled.Count == 0) return "(未观测到)";
 System.Text.StringBuilder sb = new System.Text.StringBuilder();
 foreach (var kv in _desiredEnabled)
 sb.Append("\n · ").Append(kv.Key.Name).Append(" = ").Append(kv.Value);
 return sb.ToString();
 }
#else
 // 【LEAN】诊断输出整段删除（只给完整版）
#endif

 private static Camera _vrCamCache;

 /// <summary>
 /// 取 VR 相机。
 /// **缓存引用**，不要每帧调 `VR.Camera`：
 /// · `VR.Camera` 是**惰性 getter**，`_Instance` 为 null 时会当场 `new GameObject` 造新 rig
 /// —— 那是巨大分配，而且会污染现场（本项目 踩过这个坑）；
 /// · Unity fake-null 判定保证"相机被销毁后缓存自动失效并重取"，所以缓存是安全的；
 /// · 每帧省掉一次属性链访问 + 潜在的对象创建。
 /// </summary>
 internal static Camera GetVrCamera()
 {
 try
 {
 // ★★ 2026-10-22 安全阀：平面模式下**绝对不许**碰 `VR.Camera`。
 // 它是惰性 getter，会当场 `new GameObject("VRGIN_Camera")` 造一套假 rig
 // → 相机被抢（蓝背景 + 镜头无法移动 + UI 仍在）。详见 VRRef.VrRunning 的注释。
 if (!VRRef.VrRunning) return null;
 if (_vrCamCache != null) return _vrCamCache;
 if (VR.Camera == null || VR.Camera.SteamCam == null) return null;
 Camera c = VR.Camera.SteamCam.camera;
 if (c == null) c = VR.Camera.SteamCam.GetComponent<Camera>();
 _vrCamCache = c;
 return c;
 }
 catch { return null; }
 }

 /// <summary>场景切换/加载后调用：丢弃相机缓存，让下一帧重新取。</summary>
 internal static void InvalidateCameraCache()
 {
 _vrCamCache = null;
 }
 }
}
