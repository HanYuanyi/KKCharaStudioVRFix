using System;
using HarmonyLib;
using UnityEngine;
using VRGIN.Core;

namespace KKCharaStudioVRFix
{
 /// <summary>
 /// 相机收养过滤。
 ///
 /// VRGIN 的 VRManager.OnUpdate() 每帧会扫描 Camera.allCameras，对没见过的相机调用
 /// JudgeCamera()；只要判定为 MainCamera 就 VR.Camera.Copy(cam, master:true) 把它当成新蓝图。
 ///
 /// 而 Koikatu Studio 在加载地图 / 过场 / 预览时会临时创建带 MainCamera 标签的相机。
 /// 一旦被收养：
 /// · VR 相机的 clearFlags / backgroundColor / Skybox 会被临时相机的值覆盖 → 画面发灰；
 /// · 该相机被挂上 CameraSlave → 触发 VRCamera.UpdateCameraConfig()，
 /// 用"所有从相机 cullingMask 的并集"整个覆盖 VR 相机遮罩 → FK/IK 球卡住；
 /// · 该相机被挂上 CameraKiller → 被强杀。
 ///
 /// 这里只做一件事：如果判定结果是 MainCamera，但相机不是 Studio.cameraCtrl.mainCmaera，
 /// 就降级为 Ignore —— 也就是"只承认 Studio 主相机"。
 ///
 /// 挂在 JudgeCamera 上（而不是 JudgeCameraInternal）是因为 JudgeCamera 是 public 非虚方法，
 /// 无论解释器有没有重写内部判定，它都一定会被调用。
 /// </summary>
 internal static class CameraAdoptionFilter
 {
 private static int _deduped;
 internal static void Install(HarmonyLib.Harmony harmony)
 {
 try
 {
 System.Reflection.MethodInfo target = AccessTools.Method(
 typeof(GameInterpreter), "JudgeCamera", new Type[] { typeof(Camera) });

 if (target == null)
 {
 FixLog.Warn("未找到 GameInterpreter.JudgeCamera，相机收养过滤未启用。");
 return;
 }

 harmony.Patch(target,
 postfix: new HarmonyMethod(AccessTools.Method(
 typeof(CameraAdoptionFilter), "JudgeCameraPostfix")));

 FixLog.Info("已挂载相机收养过滤: GameInterpreter.JudgeCamera");
 }
 catch (Exception e)
 {
 FixLog.Warn("挂载相机收养过滤失败: " + e.Message);
 }
 }

 public static void JudgeCameraPostfix(Camera camera, ref CameraJudgement __result)
 {
 try
 {
 if (!KKCharaStudioVRFixPlugin.On(KKCharaStudioVRFixPlugin.CfgAdoptOnlyStudioCamera))
 return;

 if (__result != CameraJudgement.MainCamera) return;

 Camera studioMain = VRRef.GetStudioMainCamera();

 // Studio 还没准备好时不要过滤，否则会一个蓝图都没有（黑屏）。
 if (studioMain == null) return;

 if (camera == studioMain) return;

 FixLog.Info("忽略非 Studio 主相机: " + camera.name);
 __result = CameraJudgement.Ignore;

 // 只降级是不够的 —— 必须同时让它别再渲染。
 //
 // 反编译 VRGIN_KKCS 的 VRManager.OnUpdate：收养 switch 里
 // MainCamera / SubCamera → VR.Camera.Copy(item)
 // GUI / GUIAndCamera → VR.GUI.AddCamera(item)
 // **没有任何 Ignore 分支** ← 被降级的相机既不被收养、也不会被停掉
 //
 // 而正常收养的相机会走到 VRCamera.Copy() 里这一段：
 // if (!hasOtherConsumers && blueprint.targetTexture == null
 // && VR.Interpreter.IsIrrelevantCamera(blueprint))
 // blueprint.gameObject.AddComponent<CameraKiller>();
 // CameraKiller.OnStart() → cullingMask=0、depth=-9999；
 // CameraKiller.OnPreCull() → _Camera.enabled=false（每帧都不渲染）。
 //
 // 也就是说：**原版会让这些相机彻底不渲染**。我的过滤把它们挡在收养之外，
 // 却漏了"停止渲染"这一步 → 于是这些临时相机每帧把整个场景**再渲染一遍**
 // （VR 相机也在渲染同一场景）→ 双倍渲染 → 实测 **55fps 掉到 26fps**。
 // 这就是"卡回 30 帧"的真凶：不是 CPU、不是 GC，是**多了一遍整屏渲染**。
 //
 // 这里补上原版同样的处理（只对直接上屏的相机；渲染到贴图的预览相机不碰，
 // 那是正当工作）。不收养（保留原来的目的：不被改 clearFlags/遮罩 → 不发灰），
 // 也不让它白渲染。
 try
 {
 if (camera.targetTexture == null)
 {
 if (camera.gameObject.GetComponent<CameraKiller>() == null)
 {
 camera.gameObject.AddComponent<CameraKiller>();
 _deduped++;
 FixLog.Warn("【相机去重】非 Studio 主相机「" + camera.name
 + "」(tag=MainCamera、直接上屏、无 renderTexture) 已按原版做法停掉渲染"
 + " —— 否则它每帧多渲染一遍整个场景，与 VR 相机重复计算"
 + "（双倍渲染：实测 55fps → 26fps）。累计已去重 " + _deduped + " 个。");
 }
 }
 }
 catch (Exception e) { FixLog.Debug("相机去重失败: " + e.Message); }
 }
 catch (Exception e)
 {
 FixLog.Debug("JudgeCameraPostfix 异常: " + e.Message);
 }
 }
 }
}
