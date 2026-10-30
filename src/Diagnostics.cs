using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;
using VRGIN.Core;

namespace KKCharaStudioVRFix
{
 /// <summary>
 /// 诊断导出。按配置的按键（默认 F7）触发，把当前 VR / 相机 / 图层 / 引导球 的
 /// 完整状态写进日志文件。目的：用真实数据定位「灰屏 + FK/IK 球」的根因，
 /// 而不是继续猜。
 /// </summary>
 internal static class Diagnostics
 {
 internal static void DumpAll(string reason)
 {
 // 平面模式（没开 VR）**绝对不许**调这个。
 //
 // 原因：下面 DumpVr 会读 `VR.Camera`，而 VRGIN 的 `VRCamera.Instance` 是**惰性 getter**
 // —— rig 不存在时它会**当场造一个**。于是平面模式下按 F7 会凭空生成一套 VR rig：
 // 相机被抢走（镜头跳到诡异位置、卡死、UI 消失）、掩码被重算（FK/IK 轴全部显示）。
 // 用户实测正是这个现象，而且重启后 Studio 的 UI 可见性还被带坏了。
 try { if (!VR.Active) return; } catch { return; }

 StringBuilder sb = new StringBuilder();
 sb.AppendLine();
 sb.AppendLine("=================== 诊断导出 (" + reason + ") ===================");
 sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
 sb.AppendLine("⚠ 本次扫描会全表枚举对象（Resources.FindObjectsOfTypeAll），约卡帧 0.5~1s ——");
 sb.AppendLine(" ⚠ 复现跳变期间**不要**按 F7，否则两跳之间的间隔会被拉长（上份日志已证实）。");

 Section(sb, "1. VR 基础状态", DumpVr);
 Section(sb, "2. 图层", DumpLayers);
 Section(sb, "3. Studio 相机", DumpStudioCameras);
 Section(sb, "4. 场景中所有相机", DumpAllCameras);
 Section(sb, "5. 加载状态", DumpLoading);
 Section(sb, "6. 引导球 (FK/IK 球) 统计", DumpGuideObjects);
 Section(sb, "7. 控制器 / SteamVR", DumpControllers);
 Section(sb, "8. 渲染 / 光照 / 后处理（灰屏嫌疑区）", DumpRendering);
 Section(sb, "9. 地图光照 (SunLightInfo)", DumpMapLight);
 Section(sb, "10. UI Canvas 接管状态", GuiCanvasFix.DumpCanvases);
 Section(sb, "11. 拖拽世界 (解锁状态记忆)", DumpDragState);
 Section(sb, "12. VR 原点守卫 (摄像机被拉回固定位置)", DumpOriginGuard);
 Section(sb, "13. 跳变记录仪 (origin 每一跳 + Mode 死亡现场)", JumpDiagnostics.AppendReport);

 sb.AppendLine("=================== 诊断导出结束 ===================");
 sb.AppendLine();

 FixLog.Key("诊断已导出（详见 KKCharaStudioVRFix.log）: " + reason);
 FixLog.Info(sb.ToString());
 }

 private static void Section(StringBuilder sb, string title, Action<StringBuilder> body)
 {
 sb.AppendLine();
 sb.AppendLine("--- " + title + " ---");
 try { body(sb); }
 catch (Exception e) { sb.AppendLine(" <导出失败> " + e.Message); }
 }

 /// <summary>
 /// 导出拖动工具的解锁状态。
 /// 0.0.3 每次加载场景卡都会重建 StandingMode → 拖动工具被重新 AddComponent，
 /// lockRotXZ 回到默认的 true（锁定）→ 用户必须重新按三键解锁。
 /// 这里把"插件记住的状态 + 已恢复次数"报出来，便于确认自动恢复有没有生效。
 /// </summary>
 private static void DumpDragState(StringBuilder sb)
 {
 // Describe() 现在会返回多行（逐手 / 逐个工具的明细），
 // 统一按行缩进，避免第二行之后顶到最左边看不清层级。
 AppendIndented(sb, "记忆的解锁状态 = ", DragStateFix.Describe());

 sb.AppendLine();
 sb.AppendLine(" ");
 sb.AppendLine(" · 场景中拖动工具数 应为 2（GenericStandingMode.Tools 只有一份列表，");
 sb.AppendLine(" 但 InitializeTools 对左右手各跑一次，所以左右手各有一个工具实例）。");
 sb.AppendLine(" · 【右手】/【左手】应各报一只，且 ToolIndex / lockRotXZ 各自独立 ——");
 sb.AppendLine(" 把两只手混在一起处理，导致「记录当前工具序号」每帧刷一条。");
 sb.AppendLine(" · 若【右手】显示「未发现工具」＝ 右手控制器当时不可用，");
 sb.AppendLine(" 此时插件会退回到「唯一/实例ID最小」的那一只继续跟踪。");
 sb.AppendLine(" · 加载场景卡后应出现「拖动工具被重建（场景卡加载）：实例 ID ... 已刷新」");
 sb.AppendLine(" 紧接着「已自动恢复「解锁世界」状态（第 N 次）」。");
 }

 /// <summary>把可能含换行的多行文本按统一缩进写出。</summary>
 private static void AppendIndented(StringBuilder sb, string prefix, string text)
 {
 if (text == null) { sb.AppendLine(prefix); return; }
 string[] lines = text.Replace("\r\n", "\n").Split('\n');
 for (int i = 0; i < lines.Length; i++)
 {
 if (i == 0) sb.AppendLine(prefix + lines[i]);
 else sb.AppendLine(" " + lines[i]);
 }
 }

 /// <summary>
 /// VR 原点守卫。
 /// 根因：VRGIN.Modes.StandingMode.OnStart() 里有一句
 /// origin.position = Vector3.zero; origin.rotation = Quaternion.identity;
 /// 而 0.0.3 每次加载场景卡都会 SetMode&lt;GenericStandingMode&gt;() 重建模式
 /// → 每次加载都把原点拉回 (0,0,0) →「摄像机回到那个位置」。
 /// OriginGuardFix 连续守卫并在检测到重置后连续写回，这里把它的状态报出来。
 /// </summary>
 private static void DumpOriginGuard(StringBuilder sb)
 {
 AppendIndented(sb, "原点姿态记忆 = ", OriginGuardFix.Describe());
 sb.AppendLine();
 // 改用粘性标记 —— 消费型 IsLoadingSceneCard() 在加载结束后必然为假，
 // 会让这份诊断永远显示 "False"，无法判断刚发生的那次到底是不是场景卡加载。
 sb.AppendLine(" 本次是否场景卡加载 = " + VRRef.IsSceneCardLoadSticky()
 + "（粘性标记；场景卡加载已改为「抵消 StandingMode 归零」，再由原版摆机位）");
 sb.AppendLine();
 sb.AppendLine(" ");
 sb.AppendLine(" · 「已恢复 N 次」指的是**地图加载**时写回站姿的次数。");
 sb.AppendLine(" 场景卡加载**不应该**出现在这个计数里 —— 那会盖掉场景卡存档的机位。");
 sb.AppendLine(" · 场景卡加载应看到「StandingMode 归零已被抵消（场景卡加载）」");
 sb.AppendLine(" 以及「InitScene 后已把原点拉回加载前位姿」");
 sb.AppendLine(" 以及加载完成后的「场景卡机位已导入 → 同一帧内对齐 VR 视角」。");
 sb.AppendLine(" · 若位置仍然错位，请按 F7 后把上面这几行一起发来。");
 }

 // ---------------------------------------------------------------

 private static void DumpVr(StringBuilder sb)
 {
 bool active = false;
 try { active = VR.Active; } catch { }
 sb.AppendLine(" VR.Active = " + active);

 try
 {
 VRManager mgr = VR.Manager;
 sb.AppendLine(" VR.Manager = " + (mgr == null ? "null" : "ok"));
 if (mgr != null)
 {
 VRGIN.Modes.ControlMode mode = mgr.Mode;
 sb.AppendLine(" VR.Manager.Mode = " + (mode == null ? "null" : mode.GetType().FullName));
 if (mode != null)
 {
 sb.AppendLine(" Mode.enabled = " + mode.enabled
 + ", activeInHierarchy = " + mode.gameObject.activeInHierarchy);
 DumpController(sb, "Left", mode.Left);
 DumpController(sb, "Right", mode.Right);
 }
 sb.AppendLine(" Interpreter = " + (mgr.Interpreter == null ? "null" : mgr.Interpreter.GetType().FullName));
 }
 }
 catch (Exception e) { sb.AppendLine(" VR.Manager 读取失败: " + e.Message); }

 try
 {
 if (VR.Camera == null) { sb.AppendLine(" VR.Camera = null"); return; }
 sb.AppendLine(" VR.Camera ok, SteamCam = " + (VR.Camera.SteamCam == null ? "null" : "ok"));
 if (VR.Camera.SteamCam == null) return;

 Transform origin = VR.Camera.SteamCam.origin;
 Transform head = VR.Camera.SteamCam.head;
 sb.AppendLine(" origin = " + (origin == null ? "null" : Vec(origin.position) + " rot " + Vec(origin.eulerAngles) + " scale " + Vec(origin.localScale)));
 sb.AppendLine(" head = " + (head == null ? "null" : Vec(head.position)));

 Camera c = VR.Camera.SteamCam.camera;
 if (c == null) c = VR.Camera.SteamCam.GetComponent<Camera>();
 sb.AppendLine(" SteamCam.camera = " + (c == null ? "null" : DescribeCamera(c)));
 }
 catch (Exception e) { sb.AppendLine(" VR.Camera 读取失败: " + e.Message); }
 }

 private static void DumpController(StringBuilder sb, string label, VRGIN.Controls.Controller c)
 {
 if (c == null) { sb.AppendLine(" " + label + " = null"); return; }
 sb.AppendLine(" " + label + " = " + c.gameObject.name
 + ", enabled = " + c.enabled
 + ", activeInHierarchy = " + c.gameObject.activeInHierarchy);
 try
 {
 SteamVR_TrackedObject t = c.GetComponent<SteamVR_TrackedObject>();
 if (t == null) sb.AppendLine(" SteamVR_TrackedObject = 缺失");
 else sb.AppendLine(" Tracking.index = " + (int)t.index + " (None = -1), isValid = " + t.isValid);
 }
 catch (Exception e) { sb.AppendLine(" Tracking 读取失败: " + e.Message); }
 }

 private static void DumpLayers(StringBuilder sb)
 {
 string[] names = { "Studio/Col", "Studio/Select", "Studio/Route", "Default", "Studio", "Map" };
 for (int i = 0; i < names.Length; i++)
 {
 int layer = LayerMask.NameToLayer(names[i]);
 sb.AppendLine(" " + names[i] + " -> NameToLayer = " + layer
 + ", GetMask = 0x" + LayerMask.GetMask(names[i]).ToString("X8"));
 }
 }

 private static void DumpStudioCameras(StringBuilder sb)
 {
 Studio.Studio studio = Studio.Studio.IsInstance() ? Studio.Studio.Instance : null;
 sb.AppendLine(" Studio.Studio.Instance = " + (studio == null ? "null" : "ok"));
 if (studio == null) return;

 sb.AppendLine(" workInfo.visibleAxis = " + studio.workInfo.visibleAxis);
 sb.AppendLine(" workInfo.visibleAxisTranslation = " + studio.workInfo.visibleAxisTranslation);
 sb.AppendLine(" workInfo.visibleAxisCenter = " + studio.workInfo.visibleAxisCenter);
 sb.AppendLine(" workInfo.visibleCenter = " + studio.workInfo.visibleCenter);
 try
 {
 sb.AppendLine(" optionSystem.selectedState = " + Studio.Studio.optionSystem.selectedState);
 }
 catch { }

 if (studio.cameraCtrl == null) { sb.AppendLine(" cameraCtrl = null"); return; }

 Camera main = studio.cameraCtrl.mainCmaera;
 sb.AppendLine(" mainCmaera = " + (main == null ? "null" : DescribeCamera(main)));

 Camera sub = studio.cameraCtrl.subCamera;
 sb.AppendLine(" subCamera = " + (sub == null ? "null" : DescribeCamera(sub)));
 }

 private static void DumpAllCameras(StringBuilder sb)
 {
 Camera[] cams = Resources.FindObjectsOfTypeAll<Camera>();
 sb.AppendLine(" 共 " + cams.Length + " 个 Camera 对象：");
 for (int i = 0; i < cams.Length; i++)
 {
 Camera c = cams[i];
 if (c == null) continue;
 sb.AppendLine(" [" + i + "] " + DescribeCamera(c));
 }
 }

 private static string DescribeCamera(Camera c)
 {
 try
 {
 string slaves = "";
 try
 {
 bool hasSlave = c.GetComponent("CameraSlave") != null;
 bool hasKiller = c.GetComponent("CameraKiller") != null;
 slaves = " CameraSlave=" + hasSlave + " CameraKiller=" + hasKiller;
 }
 catch { }

 return c.gameObject.name
 + " | enabled=" + c.enabled + " activeInHierarchy=" + c.gameObject.activeInHierarchy
 + " depth=" + c.depth
 + " clearFlags=" + c.clearFlags
 + " bg=" + c.backgroundColor
 + " targetTexture=" + (c.targetTexture == null ? "null" : c.targetTexture.name)
 + " fov=" + c.fieldOfView
 + " tag=" + c.tag
 + slaves
 + " cullingMask=[" + MaskToNames(c.cullingMask) + "]";
 }
 catch (Exception e)
 {
 return "<读取失败 " + e.Message + ">";
 }
 }

 private static string MaskToNames(int mask)
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

 private static void DumpLoading(StringBuilder sb)
 {
 try
 {
 Manager.Scene scene = Manager.Scene.IsInstance() ? Manager.Scene.Instance : null;
 if (scene == null) sb.AppendLine(" Manager.Scene.Instance = null");
 else
 {
 sb.AppendLine(" Manager.Scene.IsNowLoading = " + scene.IsNowLoading);
 sb.AppendLine(" Manager.Scene.IsNowLoadingFade = " + scene.IsNowLoadingFade);
 sb.AppendLine(" Manager.Scene.LoadSceneName = " + scene.LoadSceneName);
 }
 }
 catch (Exception e) { sb.AppendLine(" Manager.Scene 读取失败: " + e.Message); }

 try
 {
 Studio.Map map = Studio.Map.IsInstance() ? Studio.Map.Instance : null;
 if (map == null) sb.AppendLine(" Studio.Map.Instance = null");
 else sb.AppendLine(" Studio.Map.isLoading = " + map.isLoading + ", no = " + map.no);
 }
 catch (Exception e) { sb.AppendLine(" Studio.Map 读取失败: " + e.Message); }

 sb.AppendLine(" VRRef.IsGameLoading() = " + VRRef.IsGameLoading());
 }

 private static void DumpGuideObjects(StringBuilder sb)
 {
 Studio.Studio studio = Studio.Studio.IsInstance() ? Studio.Studio.Instance : null;
 if (studio == null) { sb.AppendLine(" Studio 未就绪"); return; }

 // 场景对象里的 guideObject
 int total = 0, active = 0;
 try
 {
 foreach (KeyValuePair<int, Studio.ObjectCtrlInfo> kv in studio.dicObjectCtrl)
 {
 Studio.ObjectCtrlInfo info = kv.Value;
 if (info == null || info.guideObject == null) continue;
 total++;
 if (info.guideObject.gameObject.activeInHierarchy) active++;
 }
 }
 catch (Exception e) { sb.AppendLine(" dicObjectCtrl 读取失败: " + e.Message); }
 sb.AppendLine(" 场景对象 guideObject: 共 " + total + "，其中 activeInHierarchy = " + active);

 // GuideObjectManager.dicGuideObject
 try
 {
 Studio.GuideObjectManager mgr = Studio.GuideObjectManager.IsInstance()
 ? Studio.GuideObjectManager.Instance : null;
 if (mgr == null) { sb.AppendLine(" GuideObjectManager = null"); return; }

 FieldInfo f = typeof(Studio.GuideObjectManager).GetField("dicGuideObject",
 BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
 if (f == null) { sb.AppendLine(" dicGuideObject 字段未找到"); return; }

 IDictionary dict = f.GetValue(mgr) as IDictionary;
 if (dict == null) { sb.AppendLine(" dicGuideObject = null"); return; }

 int n = 0, vis = 0, act = 0;
 Dictionary<string, int> layerCount = new Dictionary<string, int>();
 foreach (DictionaryEntry e in dict)
 {
 n++;
 Studio.GuideObject go = e.Value as Studio.GuideObject;
 if (go == null) continue;
 if (go.visible) vis++;
 if (go.gameObject.activeInHierarchy) act++;
 string ln = LayerMask.LayerToName(go.gameObject.layer);
 string key = go.gameObject.layer + ":" + (string.IsNullOrEmpty(ln) ? "<unnamed>" : ln);
 if (layerCount.ContainsKey(key)) layerCount[key]++;
 else layerCount[key] = 1;
 }

 sb.AppendLine(" GuideObjectManager.dicGuideObject: 共 " + n
 + "，visible = " + vis + "，activeInHierarchy = " + act);
 foreach (KeyValuePair<string, int> kv in layerCount)
 sb.AppendLine(" 图层 " + kv.Key + " -> " + kv.Value + " 个");
 }
 catch (Exception e)
 {
 sb.AppendLine(" dicGuideObject 读取失败: " + e.Message);
 }
 }

 private static void DumpControllers(StringBuilder sb)
 {
 try
 {
 Transform origin = VRRef.GetVROrigin();
 if (origin == null) { sb.AppendLine(" origin = null"); return; }

 SteamVR_ControllerManager[] managers =
 origin.GetComponents<SteamVR_ControllerManager>();
 sb.AppendLine(" origin 上 SteamVR_ControllerManager 数量 = " + managers.Length);
 for (int i = 0; i < managers.Length; i++)
 {
 SteamVR_ControllerManager m = managers[i];
 if (m == null) { sb.AppendLine(" [" + i + "] null"); continue; }
 sb.AppendLine(" [" + i + "] enabled = " + m.enabled
 + ", left = " + (m.left == null ? "null" : m.left.name + "/" + m.left.activeInHierarchy)
 + ", right = " + (m.right == null ? "null" : m.right.name + "/" + m.right.activeInHierarchy));
 }
 }
 catch (Exception e) { sb.AppendLine(" ControllerManager 读取失败: " + e.Message); }

 try
 {
 Type t = VRRef.FindType("SteamVR");
 bool[] connected = t != null ? VRRef.GetStatic(t, "connected") as bool[] : null;
 if (connected == null) sb.AppendLine(" SteamVR.connected 读取失败");
 else
 {
 StringBuilder ids = new StringBuilder();
 for (int i = 0; i < connected.Length; i++)
 if (connected[i]) { if (ids.Length > 0) ids.Append(", "); ids.Append(i); }
 sb.AppendLine(" SteamVR.connected 为 true 的设备: "
 + (ids.Length == 0 ? "<无>" : ids.ToString()));
 }
 }
 catch (Exception e) { sb.AppendLine(" SteamVR.connected 读取失败: " + e.Message); }
 }

 private static string Vec(Vector3 v)
 {
 return "(" + v.x.ToString("0.00") + ", " + v.y.ToString("0.00") + ", " + v.z.ToString("0.00") + ")";
 }

 // ---------------------------------------------------------------
 // 渲染 / 光照 / 后处理 —— 这是「变灰」最可能藏身的地方
 // ---------------------------------------------------------------

 private static void DumpRendering(StringBuilder sb)
 {
 // RenderSettings
 try
 {
 sb.AppendLine(" RenderSettings.ambientMode = " + RenderSettings.ambientMode);
 sb.AppendLine(" RenderSettings.ambientLight = " + RenderSettings.ambientLight);
 sb.AppendLine(" RenderSettings.ambientIntensity = " + RenderSettings.ambientIntensity);
 sb.AppendLine(" RenderSettings.ambientSkyColor = " + RenderSettings.ambientSkyColor);
 sb.AppendLine(" RenderSettings.ambientEquatorColor = " + RenderSettings.ambientEquatorColor);
 sb.AppendLine(" RenderSettings.ambientGroundColor = " + RenderSettings.ambientGroundColor);
 sb.AppendLine(" RenderSettings.fog = " + RenderSettings.fog);
 sb.AppendLine(" RenderSettings.fogMode = " + RenderSettings.fogMode);
 sb.AppendLine(" RenderSettings.fogColor = " + RenderSettings.fogColor);
 sb.AppendLine(" RenderSettings.fogDensity = " + RenderSettings.fogDensity);
 sb.AppendLine(" RenderSettings.fogStartDistance = " + RenderSettings.fogStartDistance);
 sb.AppendLine(" RenderSettings.skybox = "
 + (RenderSettings.skybox == null ? "null" : RenderSettings.skybox.name));
 sb.AppendLine(" RenderSettings.defaultReflectionMode = " + RenderSettings.defaultReflectionMode);
 sb.AppendLine(" RenderSettings.reflectionIntensity = " + RenderSettings.reflectionIntensity);
 sb.AppendLine(" RenderSettings.customReflection = "
 + (RenderSettings.customReflection == null ? "null" : RenderSettings.customReflection.name));
 }
 catch (Exception e) { sb.AppendLine(" RenderSettings 读取失败: " + e.Message); }

 // 烘焙光照贴图数量 —— Resources.UnloadUnusedAssets() 把它清空就会整场景发灰
 try
 {
 LightmapData[] lms = LightmapSettings.lightmaps;
 sb.AppendLine(" LightmapSettings.lightmaps.Length = " + (lms == null ? -1 : lms.Length));
 sb.AppendLine(" LightmapSettings.lightmapsMode = " + LightmapSettings.lightmapsMode);
 }
 catch (Exception e) { sb.AppendLine(" LightmapSettings 读取失败: " + e.Message); }

 // 场景灯光
 try
 {
 Light[] lights = Resources.FindObjectsOfTypeAll<Light>();
 int enabled = 0;
 string main = "<无>";
 for (int i = 0; i < lights.Length; i++)
 {
 Light l = lights[i];
 if (l == null) continue;
 if (l.enabled) enabled++;
 if (main == "<无>" && l.type == LightType.Directional)
 main = l.gameObject.name + " intensity=" + l.intensity
 + " color=" + l.color + " enabled=" + l.enabled
 + " shadows=" + l.shadows;
 }
 sb.AppendLine(" Light 对象: 共 " + lights.Length + "，enabled = " + enabled);
 sb.AppendLine(" 主平行光: " + main);
 }
 catch (Exception e) { sb.AppendLine(" Light 读取失败: " + e.Message); }

 // VR 相机上的后处理 / 颜色分级
 try
 {
 Camera vcam = GetVrCamera();
 if (vcam == null) { sb.AppendLine(" VR 相机 = null"); return; }

 MonoBehaviour[] comps = vcam.gameObject.GetComponents<MonoBehaviour>();
 sb.AppendLine(" VR 相机组件（" + comps.Length + " 个）:");
 for (int i = 0; i < comps.Length; i++)
 {
 MonoBehaviour m = comps[i];
 if (m == null) continue;
 string n = m.GetType().Name;
 bool interesting = n.IndexOf("Effect", StringComparison.OrdinalIgnoreCase) >= 0
 || n.IndexOf("Fog", StringComparison.OrdinalIgnoreCase) >= 0
 || n.IndexOf("Bloom", StringComparison.OrdinalIgnoreCase) >= 0
 || n.IndexOf("Color", StringComparison.OrdinalIgnoreCase) >= 0
 || n.IndexOf("Amplify", StringComparison.OrdinalIgnoreCase) >= 0
 || n.IndexOf("PostProcess", StringComparison.OrdinalIgnoreCase) >= 0
 || n.IndexOf("Skybox", StringComparison.OrdinalIgnoreCase) >= 0
 || n.IndexOf("Tone", StringComparison.OrdinalIgnoreCase) >= 0;
 if (interesting)
 sb.AppendLine(" * " + n + " enabled=" + m.enabled);
 }

 // ★ ：明确报出「后处理组件是否存在」。
 // 上一轮能定位到"发灰"就是因为 F7 抓到了这里只有 2 个组件、
 // 一个 Amplify 都没有 —— 组件被 CopyFX 的 DestroyImmediate 销毁了。
 int vrWatched = ColorGradingFix.CountWatchedVrCamera();
 int srcWatched = ColorGradingFix.CountWatchedSource();
 sb.AppendLine(" >>> 后处理组件存在性: VR 相机 " + vrWatched + " 个 / 源相机 "
 + srcWatched + " 个"
 + (vrWatched <= 0 && srcWatched > 0
 ? " ← 【异常】VR 相机上组件被销毁了，这就是发灰的原因"
 : ""));

 // 主相机（已被 CameraKiller 关掉）身上的后处理，对比用
 Camera studioCam = VRRef.GetStudioMainCamera();
 if (studioCam != null)
 {
 MonoBehaviour[] sc = studioCam.gameObject.GetComponents<MonoBehaviour>();
 sb.AppendLine(" Studio 主相机组件（" + sc.Length + " 个，用于对比）:");
 for (int i = 0; i < sc.Length; i++)
 {
 MonoBehaviour m = sc[i];
 if (m == null) continue;
 string n = m.GetType().Name;
 if (n.IndexOf("Effect", StringComparison.OrdinalIgnoreCase) >= 0
 || n.IndexOf("Fog", StringComparison.OrdinalIgnoreCase) >= 0
 || n.IndexOf("Color", StringComparison.OrdinalIgnoreCase) >= 0
 || n.IndexOf("Amplify", StringComparison.OrdinalIgnoreCase) >= 0
 || n.IndexOf("Skybox", StringComparison.OrdinalIgnoreCase) >= 0)
 sb.AppendLine(" * " + n + " enabled=" + m.enabled);
 }
 }
 }
 catch (Exception e) { sb.AppendLine(" VR 相机组件读取失败: " + e.Message); }
 }

 private static Camera GetVrCamera()
 {
 try
 {
 if (VR.Camera == null || VR.Camera.SteamCam == null) return null;
 Camera c = VR.Camera.SteamCam.camera;
 if (c != null) return c;
 return VR.Camera.SteamCam.GetComponent<Camera>();
 }
 catch { return null; }
 }

 private static void DumpMapLight(StringBuilder sb)
 {
 try
 {
 Studio.Map map = Studio.Map.IsInstance() ? Studio.Map.Instance : null;
 if (map == null) { sb.AppendLine(" Studio.Map.Instance = null"); return; }

 sb.AppendLine(" map.no = " + map.no);
 sb.AppendLine(" map.isLoading = " + map.isLoading);
 sb.AppendLine(" map.mapRoot = " + (map.mapRoot == null ? "null" : map.mapRoot.name));
 sb.AppendLine(" map.sunType = " + map.sunType);
 sb.AppendLine(" map.sunLightInfo = " + (map.sunLightInfo == null ? "null" : "ok"));

 Studio.Studio studio = Studio.Studio.IsInstance() ? Studio.Studio.Instance : null;
 if (studio != null && studio.sceneInfo != null)
 sb.AppendLine(" sceneInfo.sunLightType = " + studio.sceneInfo.sunLightType);
 }
 catch (Exception e) { sb.AppendLine(" Studio.Map 光照读取失败: " + e.Message); }
 }
 }
}
