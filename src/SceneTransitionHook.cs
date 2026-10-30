using System;
using System.Collections;
using HarmonyLib;
using Manager;
using Studio;
using VRGIN.Core;

namespace KKCharaStudioVRFix
{
 /// <summary>
 /// 场景 / 地图加载的生命周期钩子（强类型，已按游戏真实 API 校对）。
 ///
 /// 反编译 Assembly-CSharp.dll 后确认的真实调用链：
 ///
 /// ADD → 地图
 /// Studio.Studio.AddMap(int, bool, bool, bool)
 /// → StartCoroutine(AddMapCoroutine)
 /// → Studio.Map.LoadMapCoroutine(int, bool) ← 所有地图加载的公共收口
 /// → Manager.Scene.LoadBaseScene(Data)
 /// → LoadBaseSceneCoroutine
 /// → UnloadBaseSceneCoroutine(旧场景)
 /// SceneManager.UnloadSceneAsync(...)
 /// ActiveScene = 新场景
 /// Resources.UnloadUnusedAssets() ← 会销毁无引用对象
 /// → Studio.Studio.SetupMap
 ///
 /// 加载场景卡
 /// Studio.Studio.LoadScene(path) ← 注意：它调 AddMap 时是 StartCoroutine，**不等待**
 /// Studio.Studio.LoadSceneCoroutine(path) ← 正确顺序版，会 await AddMapCoroutine
 ///
 /// 卸载地图
 /// Studio.Map.ReleaseMap() → Manager.Scene.UnloadBaseScene()
 ///
 /// 所以：地图加载 = 一次真正的 Unity 场景切换 + 资源卸载。0.0.3 对此毫无准备。
 /// </summary>
 internal static class SceneTransitionHook
 {
 internal static void Install(HarmonyLib.Harmony harmony)
 {
 // ================= 地图路径 =================

 // ADD → 地图 的入口；Studio.LoadScene / LoadSceneCoroutine 内部也会调它
 Patch(harmony, AccessTools.Method(typeof(Studio.Studio), "AddMap",
 new Type[] { typeof(int), typeof(bool), typeof(bool), typeof(bool) }),
 "Studio.AddMap", prefix: true, postfix: true);

 // 地图真正加载完成 —— 所有地图加载路径的公共收口，包一层协程拿到精确完成点
 PatchCoroutine(harmony, AccessTools.Method(typeof(Studio.Map), "LoadMapCoroutine",
 new Type[] { typeof(int), typeof(bool) }),
 "Studio.Map.LoadMapCoroutine");

 // 卸载地图（换地图 / 清空地图）→ 又是一次场景切换
 Patch(harmony, AccessTools.Method(typeof(Manager.Scene), "UnloadBaseScene", Type.EmptyTypes),
 "Manager.Scene.UnloadBaseScene", prefix: true, postfix: true);

 // ================= 场景卡路径 =================
 // 这三条路径都**自带相机数据**，必须标记出来 ——
 // OriginGuardFix 在 StandingMode 重建时会据此**放弃写回站姿**，
 // 把机位让给原版（否则会盖掉场景卡存档的镜头位置）。

 PatchCoroutine(harmony, AccessTools.Method(typeof(Studio.Studio), "LoadSceneCoroutine",
 new Type[] { typeof(string) }),
 "Studio.LoadSceneCoroutine", sceneCard: true);

 Patch(harmony, AccessTools.Method(typeof(Studio.Studio), "LoadScene",
 new Type[] { typeof(string) }),
 "Studio.LoadScene", prefix: true, postfix: true, sceneCard: true);

 // 追加导入场景卡（只加对象，不动地图）
 Patch(harmony, AccessTools.Method(typeof(Studio.Studio), "ImportScene",
 new Type[] { typeof(string) }),
 "Studio.ImportScene", prefix: true, postfix: true, sceneCard: true);

 // ================= 相机机位导入 =================
 // Studio.CameraControl.Import(CameraData) 是原版加载流程的**最后一步**：
 // if (sceneInfo.cameraSaveData != null)
 // m_CameraCtrl.Import(sceneInfo.cameraSaveData);
 // 它只把场景卡机位写进 cameraCtrl.cameraData，**不会**同步 vROrigin。
 Patch(harmony, AccessTools.Method(typeof(Studio.CameraControl), "Import",
 new Type[] { typeof(Studio.CameraControl.CameraData) }),
 "Studio.CameraControl.Import [机位对齐]", prefix: false, postfix: true);

 // ================= 场景初始化 =================
 // Studio.Studio.InitScene(bool) 是每次加载的**第一步**，里面有两句要命的：
 // Singleton<Map>.Instance.ReleaseMap(); // 628 行 → 地图卸载 → 画面变蓝（跳1）
 // cameraCtrl.Reset(0); // 635 行 → cameraData.Copy(cameraReset)（跳2）
 //
 // 其中 cameraReset 不是"出厂默认位姿"，而是**上一次 TargetSet/FrontTarget/SetCamera
 // 快照下来的相机位姿**（Studio.CameraControl.cs 701/731/743 行）。
 // 所以在 VR 里，Reset(0) 会把 studio 相机推回"它记住的某个位置"，
 // 而 VRGIN 的 vROrigin 会跟着这个变化 → 玩家看到「镜头被甩到一个错误位置、手柄消失」。
 //
 // 归零型加载（`StandingMode` 恰好在此时重建）之所以"没有前两跳"，正是因为
 // StandingMode.OnStart() 会把 origin 归零，覆盖掉 Reset(0) 灌进来的值。
 //
 // 修法：**不给 Reset(0) 的不动原版逻辑**，只在它执行完之后，
 // 把 origin 恢复成"加载前的位置" —— 让这次位移不进入渲染帧。
 // 这样跳1（蓝色，原版固有，保留）之后直接就是场景卡机位，不再有"错误位置"中间态。
 Patch(harmony, AccessTools.Method(typeof(Studio.Studio), "InitScene",
 new Type[] { typeof(bool) }),
 "Studio.Studio.InitScene [原点守护]", prefix: false, postfix: true);

 // ================= 相机收养过滤 =================
 CameraAdoptionFilter.Install(harmony);
 }

 // ------------------------------------------------------------------

 private static void Patch(HarmonyLib.Harmony harmony, System.Reflection.MethodBase target,
 string label, bool prefix, bool postfix, bool sceneCard = false)
 {
 if (target == null)
 {
 FixLog.Debug("跳过钩子（目标不存在）: " + label);
 return;
 }

 try
 {
 System.Reflection.MethodInfo pre = null;
 if (prefix)
 {
 pre = sceneCard
 ? AccessTools.Method(typeof(SceneTransitionHook), "TransitionPrefixSceneCard")
 : AccessTools.Method(typeof(SceneTransitionHook), "TransitionPrefix");
 }
 System.Reflection.MethodInfo post = postfix
 ? AccessTools.Method(typeof(SceneTransitionHook), "TransitionPostfix") : null;

 harmony.Patch(target,
 prefix: pre != null ? new HarmonyMethod(pre) : null,
 postfix: post != null ? new HarmonyMethod(post) : null);

 FixLog.Info("已挂载钩子: " + label + (sceneCard ? " [场景卡]" : ""));
 }
 catch (Exception e)
 {
 FixLog.Warn("挂载钩子失败 " + label + ": " + e.Message);
 }
 }

 private static void PatchCoroutine(HarmonyLib.Harmony harmony,
 System.Reflection.MethodBase target, string label, bool sceneCard = false)
 {
 if (target == null)
 {
 FixLog.Debug("跳过钩子（目标不存在）: " + label);
 return;
 }

 try
 {
 System.Reflection.MethodInfo pre = sceneCard
 ? AccessTools.Method(typeof(SceneTransitionHook), "TransitionPrefixSceneCard")
 : AccessTools.Method(typeof(SceneTransitionHook), "TransitionPrefix");

 harmony.Patch(target,
 prefix: new HarmonyMethod(pre),
 postfix: new HarmonyMethod(AccessTools.Method(
 typeof(SceneTransitionHook), "CoroutinePostfix")));

 FixLog.Info("已挂载钩子: " + label + (sceneCard ? " [场景卡]" : ""));
 }
 catch (Exception e)
 {
 FixLog.Warn("挂载钩子失败 " + label + ": " + e.Message);
 }
 }

 // ------------------------------------------------------------------
 // Patch 方法体
 // ------------------------------------------------------------------

 /// <summary>平面模式（VRGIN 未启动）完全静默：VR.Active 为 false 时什么都不做。</summary>
 private static bool SkipInFlatMode()
 {
 try { return !VR.Active; }
 catch { return true; }
 }

 /// <summary>加载开始（**加载过程中**的钩子：AddMap / UnloadBaseScene / LoadMapCoroutine）。</summary>
 public static void TransitionPrefix()
 {
 if (SkipInFlatMode()) return;
 // 非权威捕获：只在还没有"加载前位姿"时才写，绝不覆盖。
 // 这些钩子横跨整个加载过程（见 OriginGuardFix.CaptureLoadPose 的注释），
 // 把它们读到的值当成"加载前位姿"就是 失败的根因。
 try
 {
 VRRef.CaptureLoadPose("加载中钩子", authoritative: false);
 }
 catch (Exception e) { FixLog.Debug("CaptureLoadPose 异常: " + e.Message); }

 try { VRRecoveryWatchdog.NotifyTransitionStart(); }
 catch (Exception e) { FixLog.Debug("TransitionPrefix 异常: " + e.Message); }
 }

 /// <summary>
 /// 加载开始（**加载入口**：`LoadScene` / `LoadSceneCoroutine` / `ImportScene`）
 /// —— 额外打上「场景卡」标记，并以**最高优先级**捕获"加载前位姿"。
 /// </summary>
 public static void TransitionPrefixSceneCard()
 {
 if (SkipInFlatMode()) return;
 // 先打标记，再捕获位姿 —— 保证 CaptureLoadPose 时粘性标记已置位，
 // 后续 StandingMode / InitScene 的 postfix 才能正确识别"这是场景卡加载"。
 try { VRRef.MarkSceneCardLoad(); }
 catch (Exception e) { FixLog.Debug("MarkSceneCardLoad 异常: " + e.Message); }

 // 这里是**权威**捕获点：它就是"用户点了场景卡加载按钮"那一刻，
 // 一定发生在任何 StandingMode 归零之前。
 try { VRRef.CaptureLoadPose("场景卡加载入口", authoritative: true); }
 catch (Exception e) { FixLog.Debug("CaptureLoadPose(权威) 异常: " + e.Message); }

 // 同一时刻再存一份"加载入口原点"，作为抵消归零的**首选目标**。
 // 为什么不能用 `StandingMode.OnStart` 的 prefix 快照：
 // 那个 prefix 跑到时，`InitScene` 早就把 origin 推歪了
 // （日志铁证：加载前 (2.91,1.10,4.88) → 归零前 (-0.44,0.12,1.14)）。
 try { VRRef.CapturePoseAtLoadEntry(); }
 catch (Exception e) { FixLog.Debug("CapturePoseAtLoadEntry 异常: " + e.Message); }

 try { VRRecoveryWatchdog.NotifyTransitionStart(); }
 catch (Exception e) { FixLog.Debug("TransitionPrefix 异常: " + e.Message); }
 }

 /// <summary>加载结束（普通方法）。</summary>
 public static void TransitionPostfix()
 {
 if (SkipInFlatMode()) return;
 try { VRRecoveryWatchdog.NotifyTransitionQueued(); }
 catch (Exception e) { FixLog.Debug("TransitionPostfix 异常: " + e.Message); }
 }

 /// <summary>
 /// 加载结束（协程方法）。把原协程包一层，等它真正跑完再通知看门狗；
 /// 中途抛异常也要通知，否则 VR 会卡死在半损坏状态。
 /// </summary>
 public static void CoroutinePostfix(ref IEnumerator __result)
 {
 if (SkipInFlatMode()) return;
 try
 {
 if (__result == null)
 {
 VRRecoveryWatchdog.NotifyTransitionQueued();
 return;
 }
 __result = Observe(__result);
 }
 catch (Exception e)
 {
 FixLog.Debug("CoroutinePostfix 异常: " + e.Message);
 VRRecoveryWatchdog.NotifyTransitionQueued();
 }
 }

 private static IEnumerator Observe(IEnumerator inner)
 {
 while (true)
 {
 bool moved;
 object current = null;
 try
 {
 moved = inner.MoveNext();
 if (moved) current = inner.Current;
 }
 catch
 {
 VRRecoveryWatchdog.NotifyTransitionQueued();
 throw;
 }

 if (!moved) break;
 yield return current;
 }

 VRRecoveryWatchdog.NotifyTransitionQueued();
 }

 // ------------------------------------------------------------------
 // ：相机机位导入后立刻同步 VR 原点
 // ------------------------------------------------------------------

 /// <summary>
 /// Studio.CameraControl.Import(CameraData) 的 postfix。
 ///
 /// ── 为什么必须在这里对齐，而不是等「加载结束」──
 /// 原版加载流程（反编译 Studio.Studio.LoadSceneCoroutine 545–579 行）：
 /// InitScene(_close:false) ← 卸地图(蓝色) + 相机 Reset(0)
 /// yield return null
 /// yield return AddMapCoroutine(...) ← 装地图
 /// ...
 /// if (sceneInfo.cameraSaveData != null)
 /// m_CameraCtrl.Import(cameraSaveData); ← ★ 原版终点
 ///
 /// 而 CameraControl.Import（反编译 295–302 行）只做：
 /// cameraData.Copy(_src); fieldOfView = cameraData.parse;
 /// —— **完全没碰 vROrigin**。VR 里决定玩家看到什么的是 vROrigin，
 /// 所以原版 Import 之后 VR 视角不会跟过去（停在 Reset(0) 的位置）。
 ///
 /// 0.4.0 的 VRCameraMoveHelper.MoveToCurrent() 补的正是这一步，但它被挂在
 /// VRRecoveryWatchdog 的「加载结束」事件上 —— 那时 Import 早已执行完、
 /// GenericStandingMode 都开始重建了，于是这一对齐变成玩家可见的**第 3 跳**。
 ///
 /// 挂在 Import 的 postfix 上，就能在 **cameraData 落地的那一帧**立刻摆好 origin，
 /// 让「Import 摆错位」和「MoveToCurrent 摆对位」合并成同一个呈现帧 → 少一跳。
 ///
 /// ── 守卫 ──
 /// Import 不只被场景卡加载调用，所以必须用 IsLoadingSceneCard() 把关；
 /// 本处**只判断不消费**，消费权仍留给 VRRecoveryWatchdog 的场景卡主逻辑
 /// —— 万一这里提前调用失败（协程时序、MoveHelper 未就绪），看门狗还能兜底。
 /// </summary>
 public static void ImportPostfix()
 {
 try
 {
 // 守卫改用**粘性**标记（只读、不消费）。
 // 用 IsLoadingSceneCard() → 首次加载标记未置位 / 已被看门狗消费
 // → 这个 postfix 从未执行过一次（日志 0 次「场景卡机位已导入」）。
 if (!VRRef.IsSceneCardLoadSticky()) return; // 非场景卡加载：不碰镜头
 if (!VRRef.IsVrReady()) return; // VR 未就绪：交给看门狗

 VRRef.CallMoveToCurrent();

 // 通知看门狗「本次已对齐」—— 免得加载结束时再调一次造成多余跳变。
 try { VRRecoveryWatchdog.NotifySceneCardAlignedByImport(); }
 catch (Exception e) { FixLog.Debug("NotifySceneCardAlignedByImport 异常: " + e.Message); }

 FixLog.Key("场景卡机位已导入 → 同一帧内对齐 VR 视角"
 + "（把「Import 摆错位」与「MoveToCurrent 摆对位」合并，去掉多出来的一跳）。");
 }
 catch (Exception e)
 {
 // 失败静默：VRRecoveryWatchdog 的「加载结束」路径仍会兜底对齐一次。
 FixLog.Debug("ImportPostfix 异常（已交由看门狗兜底）: " + e.Message);
 }
 }

 // ------------------------------------------------------------------
 // ：InitScene 之后守护原点 —— 消掉「跳1 之后镜头被甩到错误位置」
 // ------------------------------------------------------------------

 /// <summary>
 /// Studio.Studio.InitScene(bool) 的 postfix。
 ///
 /// ── 为什么需要它 ──
 /// 原版每次加载的第一步是 `InitScene(_close:false)`（反编译 Studio.Studio.cs 597–653）：
 /// Singleton<Map>.Instance.ReleaseMap(); // 628 行 → 卸载地图 → 画面变蓝（跳1）
 /// cameraCtrl.Reset(0); // 635 行 → cameraData.Copy(cameraReset)（跳2）
 ///
 /// `cameraReset` 的真相（Studio.CameraControl.cs）：
 /// protected CameraData cameraReset = new CameraData(); // 152 行，初始零值
 /// public void TargetSet(Transform t, bool isReset) {
 /// cameraData.rotate = transform.rotation.eulerAngles;
 /// if (isReset) cameraReset.Copy(cameraData); // 701 行 ★ 快照当前相机
 /// }
 /// 即 `Reset(0)` 恢复的是**上一次记住的相机位姿**，不是"出厂默认"。
 ///
 /// 在 VR 里 vROrigin 会随这次 Reset 变化 → 玩家看到「镜头跳到一个错误位置、手柄消失」。
 /// （`StandingMode` 恰好在此时重建时会 OnStart 归零 origin，从而"看起来没事" ——
 /// 这正是用户观察到的「原版非 ADD 地图后就再没前两跳」。）
 ///
 /// ── 修法 ──
 /// **不改原版逻辑**，只在 `InitScene` 返回后把 origin 立即恢复到加载前的位姿，
 /// 让这次 Reset 的位移不进入渲染帧。之后原版照常走 `AddMap` → `Import`，
 /// 我们再用 `MoveToCurrent`（Import postfix / 看门狗）把镜头摆到场景卡机位。
 ///
 /// 结果：跳1（蓝色，原版固有）→ 直接落地到场景卡机位，**不再有中间的错误位置**。
 /// </summary>
 public static void InitScenePostfix()
 {
 try
 {
 // 只处理场景卡加载：地图加载不该被动镜头（用户要求"地图加载后人还在原地"）。
 // 守卫改用**粘性**标记（只读、不消费）—— 用
 // IsLoadingSceneCard() 导致这里从未执行过（日志 0 次「InitScene 后已把原点拉回」）。
 if (!VRRef.IsSceneCardLoadSticky()) return;
 if (!VRRef.IsVrReady()) return;

 VRRef.RestoreOriginAfterInitScene();
 }
 catch (Exception e)
 {
 FixLog.Debug("InitScenePostfix 异常（不影响加载）: " + e.Message);
 }
 }
 }
}
