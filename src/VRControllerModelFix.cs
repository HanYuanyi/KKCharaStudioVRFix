using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace KKCharaStudioVRFix
{
 /// <summary>
 /// 修「手柄只剩圆盘」= SteamVR 控制器渲染模型没加载。
 ///
 /// 证据（2026-10-15 11:23 日志）：
 /// · 渲染体清单只有 3 个（Sphere / Arc Renderer / pointer）—— 全是 VRGIN 自建指示物；
 /// · `【手柄模型】` 显示 SteamVR_RenderModel 组件**存在、激活、enabled**，
 /// 但模型没挂上（GameObject 上没有 MeshRenderer）。
 ///
 /// 机制（反编译 VRGIN_KKCS 里的 SteamVR_RenderModel，非猜测）：
 /// · `SetDeviceIndex(index)` → `if (enabled) UpdateModel();`
 /// · `UpdateModel()` 末尾：`if (renderModelName != text) { renderModelName = text;
 /// StartCoroutine(SetModelAsync(text)); }`
 /// —— **只要名字没变就永不重试**；而 `SetModelAsync` 里若
 /// `OpenVR.RenderModels == null`（开机时 SteamVR 还没就绪，正好是"先开手柄再启动"
 /// 那种时序）会**静默 yield break**，名字却已经被记下 → 永久卡在"没有模型"。
 /// · 引擎自己遇到皮肤设置变更时是这么刷新的：
 /// `if (!string.IsNullOrEmpty(renderModelName)) { renderModelName = ""; UpdateModel(); }`
 ///
 /// 所以本类照抄引擎自己的刷新写法：发现某个 RenderModel 迟迟没有 MeshRenderer，
 /// 就**清空 renderModelName 再调 UpdateModel()** 强制重载（间隔 3 秒，最多 60 次）。
 ///
 /// 重试间隔从 30 秒改成 3 秒（注释一直写的是 3 秒，代码却是 30f）。
 /// 实测：启动时模型没加载 → 用户看到"只剩圆盘"，等 30 秒重试一次才好；
 /// 用户还以为"加载个场景卡就正常了"，其实只是场景卡加载那几秒刚好等到了重试窗口。
 /// </summary>
 internal static class VRControllerModelFix
 {
 private static Type _modelType;
 private static bool _typeSearched;
 private static FieldInfo _nameBacking;
 private static MethodInfo _updateModel;
 private static FieldInfo _indexField;
 private static float _lastAttempt = -999f;
 private static float _lastScan = -999f;
 private static bool _allLoaded;
 private static int _attempts;
 private const int MaxAttempts = 120;
 private static int _lastMissingCount = -1;

 // ================= 改成事件登记，不再全表扫描 =================
 // 用户要求："3 秒能恢复，是不是可以改成立即恢复、也不用重扫了"。
 // 之前每轮都要靠 `Resources.FindObjectsOfTypeAll` 找 RenderModel 组件（实测单次 35.5ms，
 // 是真实尖峰）。其实这个组件只有两个，而且它们**每次更新模型都会走 `UpdateModel()`**
 // —— 直接 hook 住这个方法登记实例，就再也不用扫描了。
 // 扫描只留作兜底：万一组件是在我们挂钩之前就建好的（注册表为空时才扫一次）。
 private static readonly List<Component> _known = new List<Component>(8);
 private static bool _hookInstalled;
 private static bool _hookTried;

 /// <summary>
 /// 事件标志：有人调用过 `UpdateModel()`（换手柄 / 改皮肤 / 设备索引变化）→ 下一帧复检。
 /// 这是我们自己调 `UpdateModel` 时**不**置位的（否则会变成每帧自我触发）。
 /// </summary>
 private static bool _dirty;
 private static bool _selfCalling;

 /// <summary>重试间隔（秒）。</summary>
 /// <remarks>
 /// 原来这里是 30 秒（而注释写着"3 秒节流"，注释和代码不一致）。
 /// 发现 3 秒被"空数组当有效缓存"废掉（首次救援在启动后 56 秒）。
 /// 改成 0.5 秒 —— 用户实测 3 秒确实能恢复，但既然组件已经登记在册、
 /// 检查只是几次 `GetComponentInChildren`，没有理由再等 3 秒。
 /// 真正的瓶颈是 SteamVR 自己（`OpenVR.RenderModels` 还没就绪时异步加载会静默失败），
 /// 所以"立刻恢复"做不到；0.5 秒轮询 = SteamVR 一就绪就当场装上，体感就是立即。
 /// </remarks>
 private const float RetryInterval = 0.5f;

 private static void Refresh()
 {
 if (_typeSearched) return;
 try
 {
 // 以前这里一进来就 `_typeSearched = true`，于是"第一次找不到类型"
 // 会被永久记住 → 救援彻底失效。SteamVR_RenderModel 所在的程序集是**后加载**的，
 // 插件 Awake 时经常还拿不到，所以必须"拿不到就下次再试"。
 Type t = VRRef.FindType("SteamVR_RenderModel");
 if (t == null) return; // 还没加载：不记"已搜过"，下次再来
 _typeSearched = true;
 _modelType = t;
 _nameBacking = _modelType.GetField("<renderModelName>k__BackingField",
 BindingFlags.Instance | BindingFlags.NonPublic);
 if (_nameBacking == null)
 {
 // 万一不是自动属性，退回普通字段名
 _nameBacking = _modelType.GetField("renderModelName",
 BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
 }
 _updateModel = _modelType.GetMethod("UpdateModel",
 BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
 Type.EmptyTypes, null);
 _indexField = _modelType.GetField("index",
 BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
 }
 catch (Exception e) { FixLog.Debug("RenderModel 反射准备失败: " + e.Message); }
 }

 /// <summary>由看门狗每帧调用。</summary>
 internal static void Tick()
 {
 Refresh();
 if (_modelType == null || _updateModel == null || _nameBacking == null) return;

 // 挂钩也在这里补：SteamVR_RenderModel 所在的程序集是后加载的，Awake 时常常还拿不到。
 if (!_hookTried) Install();

 // 全部加载完 + 没人动过设备 → **立刻返回**。
 // 以前这里每 0.5 秒都要遍历一遍登记表做 GetComponentInChildren，一直做到关游戏。
 // 现在改成：只要模型都在，就只花一次 bool 判断；
 // 真有人换了手柄/改了皮肤（会走 UpdateModel）→ 我们的挂钩会把 _dirty 打开 → 下一帧复检。
 // 这就是"从更一开始就解决"：不靠周期扫描发现变化，靠**事件**叫醒。
 if (_allLoaded && !_dirty) return;

 try
 {
 // 先确保"登记表"里有东西。只有登记表为空时才做一次全表扫描兜底
 // （组件可能在我们挂钩之前就建好了）。一旦登记上，就再也不用扫了。
 if (_known.Count == 0)
 {
 if (Time.realtimeSinceStartup - _lastScan < 3f) return;
 _lastScan = Time.realtimeSinceStartup;
 UnityEngine.Object[] all = Resources.FindObjectsOfTypeAll(_modelType);
 if (all != null)
 {
 for (int i = 0; i < all.Length; i++) Register(all[i] as Component);
 }
 if (_known.Count == 0) return;
 }

 // 事件叫醒的复检：**下一帧就查**（不等 0.5 秒）；纯周期兜底才按 RetryInterval 走。
 if (!_dirty && Time.realtimeSinceStartup - _lastAttempt < RetryInterval) return;
 if (_attempts >= MaxAttempts) return;
 _lastAttempt = Time.realtimeSinceStartup;
 _dirty = false;

 int missing = 0, fixedNow = 0;
 for (int i = _known.Count - 1; i >= 0; i--)
 {
 Component comp = _known[i];
 if (comp == null) { _known.RemoveAt(i); continue; }
 GameObject go = comp.gameObject;
 if (go == null || !go.scene.IsValid()) continue; // 跳过 prefab
 if (!go.activeInHierarchy) continue;

 // 真凭据：模型加载成功时 SetModel() 会给它挂 MeshFilter + MeshRenderer
 // 用 GetComponentInChildren（单个、不分配数组）—— 每 0.5 秒跑一次也不能分配。
 if (go.GetComponentInChildren<MeshRenderer>(true) != null) continue; // 已加载，正常

 missing++;
 // 照抄引擎自己的刷新写法：清空名字 → UpdateModel() 会重新走 SetModelAsync
 try
 {
 object idx = _indexField != null ? _indexField.GetValue(comp) : null;
 _nameBacking.SetValue(comp, "");
 _selfCalling = true;
 try { _updateModel.Invoke(comp, null); }
 finally { _selfCalling = false; }
 fixedNow++;
 if (_attempts < 3 || fixedNow == 1)
 {
 FixLog.Key("【手柄模型救援】渲染模型未加载 → 已强制重载：go="
 + go.name + " index=" + (idx == null ? "?" : idx.ToString())
 + "（第 " + (_attempts + 1) + " 次尝试）。");
 }
 }
 catch (Exception e) { FixLog.Debug("强制重载失败: " + e.Message); }
 }

 if (missing > 0)
 {
 _attempts++;
 if (missing != _lastMissingCount)
 {
 _lastMissingCount = missing;
 FixLog.Key("【手柄模型救援】当前仍有 " + missing + " 个渲染模型未加载（本轮重载 "
 + fixedNow + " 个，累计尝试 " + _attempts + "/" + MaxAttempts + "）。");
 }
 if (_attempts >= MaxAttempts)
 {
 FixLog.Warn("【手柄模型救援】已尝试 " + MaxAttempts + " 次仍未加载出模型 —— "
 + "说明 SteamVR 侧的 render model 接口/资源不可用（不是能靠重试解决的），"
 + "需要换一种启动顺序（先把工作室打开、再开手柄）或检查 SteamVR 的渲染模型文件。");
 }
 }
 else if (_lastMissingCount > 0 || !_allLoaded)
 {
 _allLoaded = true;
 _lastMissingCount = 0;
 FixLog.Key("【手柄模型救援】所有控制器渲染模型都已加载完成（共重试 " + _attempts + " 次）。");
 }
 }
 catch (Exception e) { FixLog.Debug("VRControllerModelFix.Tick 异常: " + e.Message); }
 }

 private static HarmonyLib.Harmony _harmony;

 /// <summary>
 /// 挂钩 `SteamVR_RenderModel.UpdateModel()` —— 每次它跑都顺手登记实例。
 /// 这样"有哪些 RenderModel 组件"就不用靠 `Resources.FindObjectsOfTypeAll` 全表扫描了
 /// （那一次实测 35.5ms，是真实尖峰）。
 /// 由 Tick 反复调用直到装上：SteamVR 的程序集是后加载的，早调必然拿不到类型。
 /// </summary>
 internal static void Install()
 {
 if (_hookTried) return;
 Refresh();
 if (_modelType == null || _updateModel == null) return; // 还没就绪，下次再试
 _hookTried = true;
 try
 {
 if (_harmony == null)
 _harmony = new HarmonyLib.Harmony(KKCharaStudioVRFixPlugin.Guid + ".ctrlmodel");

 _harmony.Patch(_updateModel, postfix: new HarmonyLib.HarmonyMethod(
 AccessTools.Method(typeof(VRControllerModelFix), "UpdateModelPostfix")));

 // 校验：不能把"没抛异常"当成功（本项目硬规矩）
 HarmonyLib.Patches info = HarmonyLib.Harmony.GetPatchInfo(_updateModel);
 if (info != null && info.Postfixes != null)
 {
 foreach (HarmonyLib.Patch p in info.Postfixes)
 {
 if (p != null && p.PatchMethod != null
 && p.PatchMethod.Name == "UpdateModelPostfix") { _hookInstalled = true; break; }
 }
 }
 }
 catch (Exception e)
 {
 FixLog.Warn("【手柄模型救援】挂钩 UpdateModel 失败（将退回扫描模式）: " + e.Message);
 }

 if (KKCharaStudioVRFixPlugin.DiagOn)
 {
 if (_hookInstalled)
 FixLog.Key("【手柄模型救援】已挂钩 SteamVR_RenderModel.UpdateModel —— "
 + "渲染模型改为**事件登记**，不再靠全表扫描（省掉每次 35ms 的尖峰）。");
 else
 FixLog.Warn("【手柄模型救援】UpdateModel 挂钩未生效（补丁表里没查到）—— "
 + "退回全表扫描兜底，功能不受影响。");
 }
 }

 /// <summary>由 Harmony 调用：登记这个 RenderModel 实例，并置"需要复检"。</summary>
 public static void UpdateModelPostfix(object __instance)
 {
 try
 {
 Register(__instance as Component);
 // 我们自己的重试也会走这里 —— 那种情况不能置位，否则下一帧又触发，变成每帧重试。
 if (!_selfCalling) _dirty = true;
 }
 catch { }
 }

 private static void Register(Component comp)
 {
 if (comp == null) return;
 for (int i = 0; i < _known.Count; i++)
 if (ReferenceEquals(_known[i], comp)) return;
 if (_known.Count > 16) return; // 正常只有 2 个，防止异常情况下无限增长
 _known.Add(comp);
 }

#if !LEAN
 // 【精简版瘦身】诊断输出。
 internal static string Describe()
 {
 return "渲染模型救援：登记 " + _known.Count + " 个（事件登记=" + _hookInstalled + "），"
 + "尝试 " + _attempts + "/" + MaxAttempts + " 次；最近未加载数=" + _lastMissingCount;
 }
#else
 // 【LEAN】诊断输出整段删除（只给完整版）
#endif
 }
}
