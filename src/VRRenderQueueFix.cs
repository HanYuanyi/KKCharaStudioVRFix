using System;
using System.Reflection;
using UnityEngine;
using VRGIN.Controls;
using VRGIN.Core;
using VRGIN.Visuals;

namespace KKCharaStudioVRFix
{
 /// <summary>
 /// 【v1.9.7 / 】VR UI 与手柄被场景物品挡住的问题。
 ///
 /// 用户报：UI 总是被物品挡住、手柄也是；并问"能不能把渲染队列改成最高"。
 ///
 /// 实情（反编译 VRGIN + 0.4.0 源码）：
 /// · VRGIN 的 UI 是**世界空间面板**（GUIQuad 显示 VRGUI 的 uGuiTexture），
 /// 被桌子/道具挡住是深度测试的正常结果 —— 单纯抬 renderQueue 只能改善
 /// "与半透明物体之间"的先后，赢不了实体几何的深度测试；
 /// · 0.4.0 的官方做法是 <c>RepositionMainUI(dist)</c>：把 UI 挪到头前 dist 米处
 /// （设置项 UISpawnDistance，范围 0.3~3.0m）。本类提供快捷键一键拉回眼前；
 /// · 手柄/控制器是普通渲染模型：把材质 renderQueue 抬到 4000（与 0.4.0 把 IK 标记
 /// 设 3800 同思路），对半透明类遮挡物有效；被实体完全挡住时任何队列都无效，
 /// 动一下头/手即可。
 /// </summary>
 internal static class VRRenderQueueFix
 {
 // 5s → 60s。渲染队列是"设一次就固定"的（材质是共享的），
 // 原来每 5 秒重扫一遍（实测单次峰值 46ms）纯属白烧 —— 这就是"53→48"秒级抖动来源之一。
 // 场景切换/加载后会由加载完成钩子主动跑一次（见 VRRecoveryWatchdog 的加载后处理）。
 private const float RunInterval = 60f;
 private const float DumpInterval = 30f; // 手柄清单从 5s 降到 30s（原来每次都全表扫描）
 private const int HandQueue = 4000; // 手柄用固定值（默认不启用）
 private static float _lastRun = -999f;
#if !LEAN
 // 【精简版瘦身】只有完整版用得到
 private static float _lastDump = -999f;
#endif
 private static int _lastUiState = -1;
 private static int _lastHandState = -1;

 /// <summary>
 /// UI 面板渲染队列。用户实测：VRGIN 的 GUIQuad 默认正好是 **4000**，
 /// 场景里半透明材质（如那张粉墙）默认 6000+ 时会把 UI 盖住（6000>4000，后画=盖上面）。
 /// 所以默认抬到 7000 —— 高于常见场景材质；若仍有更高的材质，可在配置里继续调大。
 /// 注意：实体不透明几何靠深度测试遮挡，抬高队列赢不了（那属于正常遮挡）。
 /// </summary>
 internal static int UiQueue
 {
 get
 {
 try
 {
 if (KKCharaStudioVRFixPlugin.CfgUIRenderQueue != null)
 return Mathf.Clamp(KKCharaStudioVRFixPlugin.CfgUIRenderQueue.Value, 3000, 4999 + 20000);
 }
 catch { }
 return 7000;
 }
 }

 // ================= 全表扫描缓存 =================
 // 实测：本任务单次最大 37.5ms、手柄模型 dump 最大 25.3ms ——
 // 元凶都是 `FindObjectsOfType<Controller>()` / `GetComponentsInChildren<Renderer>(true)`
 // 每次重扫。手柄数量几乎不变，没必要每 5 秒重扫一遍。
 // 缓存策略：30 秒重扫一次；缓存里出现被销毁的对象（Unity fake-null）立即重扫。
 private const float ScanCacheSeconds = 60f;
 private static Controller[] _ctrlCache;
 private static float _ctrlCacheTime = -999f;
 private static GUIQuad[] _quadCache;
 private static float _quadCacheTime = -999f;

 private static bool AnyNull<T>(T[] arr) where T : UnityEngine.Object
 {
 if (arr == null) return true;
 for (int i = 0; i < arr.Length; i++) if (arr[i] == null) return true;
 return false;
 }

 private static Controller[] GetControllers()
 {
 try
 {
 if (_ctrlCache == null || AnyNull(_ctrlCache)
 || Time.realtimeSinceStartup - _ctrlCacheTime > ScanCacheSeconds)
 {
 _ctrlCache = UnityEngine.Object.FindObjectsOfType<Controller>();
 _ctrlCacheTime = Time.realtimeSinceStartup;
 }
 }
 catch { _ctrlCache = null; }
 return _ctrlCache;
 }

 private static GUIQuad[] GetQuads()
 {
 try
 {
 if (_quadCache == null || AnyNull(_quadCache)
 || Time.realtimeSinceStartup - _quadCacheTime > ScanCacheSeconds)
 {
 _quadCache = UnityEngine.Object.FindObjectsOfType<GUIQuad>();
 _quadCacheTime = Time.realtimeSinceStartup;
 }
 }
 catch { _quadCache = null; }
 return _quadCache;
 }

 /// <summary>由看门狗 LateUpdate 调用（已在 VR 守卫之内）。自带 5 秒节流。</summary>
 internal static void RunPeriodic(string reason)
 {
 try
 {
 // 最小模式下一律不动队列
 if (KKCharaStudioVRFixPlugin.CfgMinimalMode != null
 && KKCharaStudioVRFixPlugin.CfgMinimalMode.Value) return;
 bool uiOn = KKCharaStudioVRFixPlugin.On(KKCharaStudioVRFixPlugin.CfgRaiseUIRenderQueue);
 bool handOn = KKCharaStudioVRFixPlugin.CfgRaiseHandRenderQueue != null
 && KKCharaStudioVRFixPlugin.CfgRaiseHandRenderQueue.Value;
 if (!uiOn && !handOn) return;
 if (Time.realtimeSinceStartup - _lastRun < RunInterval) return;
 _lastRun = Time.realtimeSinceStartup;

 int ui = uiOn ? RaiseUIQuad(UiQueue) : 0;
 // 手柄图标问题：默认不再碰手柄材质（图标就是从开始碰它之后消失的）。
 int hand = handOn ? RaiseControllerMaterials(HandQueue) : 0;

 if (ui != _lastUiState || hand != _lastHandState)
 {
 _lastUiState = ui;
 _lastHandState = hand;
 FixLog.Key("VR 渲染队列提升 [" + reason + "]: UI 面板/共享材质 "
 + (ui > 0 ? "已抬到 " + UiQueue + "（" + ui + " 处）" : "未找到(0 个)")
 + "；手柄材质 " + (handOn
 ? (hand > 0 ? "已抬到 " + HandQueue + "（" + hand + " 个）" : "未找到(0 个)")
 : "未处理（RaiseHandRenderQueue=false）")
 + "。说明：VRGIN 的 UI 默认队列是 4000，场景里 6000+ 的半透明材质会盖住它，"
 + "所以抬到 " + UiQueue + "；实体不透明几何仍按深度正常遮挡（那是正常现象）。");
 }

 // 手柄渲染体清单：降频到 30 秒（每次都是全表扫描 + 每只手取全部子 Renderer）
 // 手柄渲染体清单**纯诊断**，与真正的队列提升无关：
 // DiagOn 关掉时完全不跑；打开时也只每 30 秒一次。
 // 原来它每 5 秒混在周期任务里跑，实测单次 6~7ms 纯白烧。
#if !LEAN
 if (KKCharaStudioVRFixPlugin.DiagOn && Time.realtimeSinceStartup - _lastDump > DumpInterval)
 {
 _lastDump = Time.realtimeSinceStartup;
 DumpControllerRenderers();
 }
#endif
 }
 catch (Exception e) { FixLog.Debug("VRRenderQueueFix 异常: " + e.Message); }
 }

 /// <summary>
 /// 手柄渲染体清单打点：把每个 Controller 下的 Renderer 的
 /// 名字/图层/enabled/renderQueue/材质名打出来。数量变化才落日志。
 /// 用途：定位"手柄切换图标"到底是什么对象、在哪层、什么队列 —— 下一份日志见分晓。
 /// </summary>
#if !LEAN
 // 【精简版瘦身】只有完整版用得到
 private static int _lastDumpCount = -1;
#endif
#if !LEAN
 // 【精简版瘦身】UI 现场取证 / 手柄渲染体清单：纯诊断，精简版不编译。
 private static void DumpControllerRenderers()
 {
 try
 {
 Controller[] controllers = GetControllers();
 if (controllers == null) return;
 var sb = new System.Text.StringBuilder();
 int total = 0;
 for (int i = 0; i < controllers.Length; i++)
 {
 if (controllers[i] == null) continue;
 Renderer[] renderers = controllers[i].GetComponentsInChildren<Renderer>(true);
 for (int j = 0; j < renderers.Length; j++)
 {
 if (renderers[j] == null) continue;
 total++;
 sb.Append(" [").Append(i).Append("] ").Append(renderers[j].name)
 .Append(" | layer=").Append(LayerMask.LayerToName(renderers[j].gameObject.layer))
 .Append(" | enabled=").Append(renderers[j].enabled)
 // 用 sharedMaterial **读**：`renderer.material` 会**实例化一份材质**
 // （破坏合批 + 制造垃圾），而这里只是打日志，读共享材质就够。
 .Append(" | queue=").Append(renderers[j].sharedMaterial == null ? "-" : renderers[j].sharedMaterial.renderQueue.ToString())
 .Append(" | mat=").Append(renderers[j].sharedMaterial == null ? "-" : renderers[j].sharedMaterial.name)
 .Append('\n');
 }
 }
 if (total == _lastDumpCount) return; // 没变化不打
 _lastDumpCount = total;
 FixLog.Key("【手柄渲染体清单】共 " + total + " 个 Renderer：\n" + sb.ToString());
 }
 catch (Exception e) { FixLog.Debug("DumpControllerRenderers 异常: " + e.Message); }
 }
#else
 // 【LEAN】诊断导出整段删除（只给完整版）
#endif

 /// <summary>
 /// 把 VR UI 面板的 renderQueue 抬到 target。返回处理的材质/面板数。
 ///
 /// 之前一直报"未找到(0 个)"，原因是查错了地方：
 /// · VRGIN 的 GUIQuad 在 `UpdateGUI()` 里执行
 /// `renderer.material = VR.Context.Materials.UnlitTransparentCombined`（或 UnlitTransparent）
 /// —— 面板材质其实是**共享材质**，改这两个材质一处即可覆盖所有面板；
 /// · 面板对象本身用 `GUIQuadRegistry.Quads`（VRGIN 自己的注册表）最可靠，
 /// `FindObjectsOfType` 只作兜底。
 /// </summary>
 // ================= UI 现场（画布 + GUIQuad） =================
 // 用户报「画面效果菜单/右边面板看不见」。
 // 该窗口是 **Studio 自带 IMGUI**（KK_StudioSceneSettings 只是 hook，没有自己的 GUI），
 // 在 VR 里画在 VRGIN 的 GUIQuad 上；uGUI 窗口（ME 等）则靠 GuiCanvasFix 接管。
 // 所以看不见只有两类可能：① 画在 GUIQuad 上但被裁掉/无内容；② uGUI 画布没被接管。
 // 这段把两边**事实**都打出来（只读，DiagOn 时每 10 秒一次）。
 private static float _lastUiFact;

#if !LEAN
 // 【精简版瘦身】UI 现场取证 / 手柄渲染体清单：纯诊断，精简版不编译。
 internal static void DumpUiFacts(string reason)
 {
 if (!KKCharaStudioVRFixPlugin.DiagOn) return;
 try
 {
 System.Text.StringBuilder sb = new System.Text.StringBuilder();

 GUIQuad[] quads = GetQuads();
 sb.Append(" GUIQuad 共 ").Append(quads == null ? 0 : quads.Length).Append(" 个：\n");
 if (quads != null)
 {
 for (int i = 0; i < quads.Length; i++)
 {
 GUIQuad q = quads[i];
 if (q == null) { sb.Append(" · [已销毁]\n"); continue; }
 Renderer r = q.GetComponentInChildren<Renderer>();
 Material m = (r == null) ? null : r.sharedMaterial;
 Texture tex = (m == null) ? null : m.mainTexture;
 RenderTexture rt = tex as RenderTexture;
 sb.Append(" · ").Append(q.gameObject.name)
 .Append(" active=").Append(q.gameObject.activeInHierarchy)
 .Append(" layer=").Append(LayerMask.LayerToName(q.gameObject.layer))
 .Append(" renderer=").Append(r == null ? "无" : (r.enabled ? "启用" : "禁用"))
 .Append(" 队列=").Append(m == null ? "-" : m.renderQueue.ToString())
 .Append(" 材质=").Append(m == null ? "-" : m.name)
 .Append(" 贴图=").Append(tex == null ? "无"
 : (tex.name + (rt != null ? (" " + rt.width + "x" + rt.height) : "")))
 .Append('\n');
 }
 }

 Canvas[] cs = UnityEngine.Object.FindObjectsOfType<Canvas>();
 sb.Append(" Canvas 共 ").Append(cs == null ? 0 : cs.Length).Append(" 个（只看非 VRGIN 的）：\n");
 int shown = 0;
 if (cs != null)
 {
 for (int i = 0; i < cs.Length; i++)
 {
 Canvas c = cs[i];
 if (c == null) continue;
 string n = c.gameObject.name;
 if (n.IndexOf("VRGIN", StringComparison.OrdinalIgnoreCase) >= 0) continue;
 if (shown >= 12) { sb.Append(" · …（还有更多，已省略）\n"); break; }
 sb.Append(" · ").Append(n)
 .Append(" 模式=").Append(c.renderMode)
 .Append(" 相机=").Append(c.worldCamera == null ? "null" : c.worldCamera.name)
 .Append(" 图层=").Append(LayerMask.LayerToName(c.gameObject.layer))
 .Append(" active=").Append(c.gameObject.activeInHierarchy)
 .Append(" enabled=").Append(c.enabled)
 .Append(" 排序=").Append(c.sortingOrder)
 .Append('\n');
 shown++;
 }
 }
 sb.Append(" ↑ 判定：GUIQuad 贴图尺寸 ≠ 屏幕尺寸 → IMGUI 会被裁掉（右边看不见）；\n")
 .Append(" Canvas 模式=ScreenSpaceOverlay 或 相机=null → 没被接管 → 看不见但能点到。");

 FixLog.Key("【UI 现场】" + reason + " 屏幕=" + Screen.width + "x" + Screen.height + "\n" + sb.ToString());
 }
 catch (Exception e) { FixLog.Debug("DumpUiFacts 异常: " + e.Message); }
 }
#else
 // 【LEAN】诊断导出整段删除（只给完整版）
#endif

#if !LEAN
 // 【精简版瘦身】UI 现场取证 / 手柄渲染体清单：纯诊断，精简版不编译。
 internal static void DumpUiFactsPeriodic()
 {
 if (!KKCharaStudioVRFixPlugin.DiagOn) return;
 if (Time.realtimeSinceStartup - _lastUiFact < 10f) return;
 _lastUiFact = Time.realtimeSinceStartup;
 DumpUiFacts("周期检查");
 }
#else
 // 【LEAN】诊断导出整段删除（只给完整版）
#endif

 private static int RaiseUIQuad(int target)
 {
 int n = 0;

 // ① 共享材质（真正生效的一步）
 try
 {
 VRGIN.Core.IVRManagerContext ctx = VR.Context;
 if (ctx != null && ctx.Materials != null)
 {
 Material a = ctx.Materials.UnlitTransparentCombined;
 Material b = ctx.Materials.UnlitTransparent;
 if (a != null && a.renderQueue != target) { a.renderQueue = target; n++; }
 if (b != null && b.renderQueue != target) { b.renderQueue = target; n++; }
 }
 }
 catch (Exception e) { FixLog.Debug("抬共享 GUI 材质队列失败: " + e.Message); }

 // ② 注册表里的每个面板
 try
 {
 foreach (GUIQuad q in GUIQuadRegistry.Quads)
 n += BumpQuad(q, target);
 }
 catch (Exception e) { FixLog.Debug("GUIQuadRegistry 遍历失败: " + e.Message); }

 // ③ 兜底：全场景 GUIQuad + 0.0.3 主面板 internalGui
 try
 {
 GUIQuad[] quads = GetQuads();
 if (quads != null)
 for (int i = 0; i < quads.Length; i++) n += BumpQuad(quads[i], target);
 }
 catch (Exception e) { FixLog.Debug("FindObjectsOfType<GUIQuad> 失败: " + e.Message); }

 n += BumpQuad(GetMainUIQuad(), target);
 return n;
 }

 /// <summary>抬一个面板的 renderer 材质队列（renderer 可能在子物体上，用 InChildren 兜底）。</summary>
 private static int BumpQuad(GUIQuad quad, int target)
 {
 try
 {
 if (quad == null) return 0;
 Renderer r = quad.GetComponent<Renderer>();
 if (r == null) r = quad.GetComponentInChildren<Renderer>();
 if (r == null) return 0;
 return BumpMaterial(r, target);
 }
 catch { return 0; }
 }

 /// <summary>把所有控制器（手柄）渲染模型材质的 renderQueue 抬到目标值。</summary>
 private static int RaiseControllerMaterials(int target)
 {
 try
 {
 Controller[] controllers = GetControllers();
 if (controllers == null) return 0;
 int n = 0;
 for (int i = 0; i < controllers.Length; i++)
 {
 if (controllers[i] == null) continue;
 Renderer[] renderers = controllers[i].GetComponentsInChildren<Renderer>(true);
 for (int j = 0; j < renderers.Length; j++)
 {
 if (renderers[j] == null) continue;
 n += BumpMaterial(renderers[j], target);
 }
 }
 return n;
 }
 catch (Exception e)
 {
 FixLog.Debug("RaiseControllerMaterials 异常: " + e.Message);
 return 0;
 }
 }

 private static int BumpMaterial(Renderer r, int target)
 {
 try
 {
 // 同样改用 sharedMaterial：本插件的目的本来就是"改那份共享材质的队列"
 // （VRGIN 的 GUIQuad 用的就是 VR.Context.Materials.* 共享材质），
 // 用 `.material` 反而会为每个 renderer 复制一份，既费内存又破坏合批。
 Material m = r.sharedMaterial;
 if (m == null) return 0;
 if (m.renderQueue == target) return 0;
 m.renderQueue = target;
 return 1;
 }
 catch { return 0; }
 }

#if !LEAN
 // 【精简版瘦身】F8「把 UI 拉回眼前」早已删除，函数一起移除。
 /// <summary>
 /// 一键把主 UI 拉回眼前。0.4.0 的官方做法是 `RepositionMainUI(dist)`，
 /// 但 **0.0.3 没有这个方法**（实机日志：`找不到 RepositionMainUI(float)`），
 /// 所以这里按 0.4.0 的公式**直接写 GUIQuad 变换**（版本无关，恒可用）：
 /// pos = head.TransformPoint(0, 0, dist)
 /// rot = LookRotation(head.TransformVector(0, 0, 1))
 /// </summary>
 internal static void PullUIToFace()
 {
 try
 {
 Transform head = null;
 try { head = VR.Camera.SteamCam != null ? VR.Camera.SteamCam.head : null; }
 catch { }
 if (head == null)
 {
 FixLog.Warn("拉回 UI：拿不到 VR 头部。");
 return;
 }

 GUIQuad gui = GetMainUIQuad();
 if (gui == null)
 {
 FixLog.Warn("拉回 UI：找不到主 UI 面板（GripMoveKKCharaStudioTool.internalGui）。");
 return;
 }

 Transform t = gui.transform;
 t.position = head.TransformPoint(new Vector3(0f, 0f, 1.2f));
 t.rotation = Quaternion.LookRotation(head.TransformVector(new Vector3(0f, 0f, 1f)));
 TryCallUpdateAspect(gui);
 FixLog.Key("已把主 UI 拉回眼前 1.2m（直接写 GUIQuad 变换，0.4.0 同款公式）。"
 + "被物品挡住时按这个键即可。");
 }
 catch (Exception e)
 {
 FixLog.Warn("拉回 UI 失败: " + e.Message);
 }
 }
#else
 // 【LEAN】整段删除（F8 功能早已删除）
#endif

 /// <summary>主 UI 面板 = 0.0.3/0.4.0 的 GripMoveKKCharaStudioTool.internalGui（static）。</summary>
 private static GUIQuad GetMainUIQuad()
 {
 try
 {
 Type toolType = VRRef.FindType("KKCharaStudioVR.GripMoveKKCharaStudioTool");
 if (toolType == null) return null;
 FieldInfo f = toolType.GetField("internalGui",
 BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
 if (f == null) return null;
 object gui = f.GetValue(null);
 GUIQuad quad = gui as GUIQuad;
 return quad; // UnityEngine fake-null：销毁后 as 返回 null
 }
 catch (Exception e)
 {
 FixLog.Debug("GetMainUIQuad 异常: " + e.Message);
 return null;
 }
 }

#if !LEAN
 // 【精简版瘦身】没有任何调用点的实验性代码。
 private static void TryCallUpdateAspect(GUIQuad gui)
 {
 try
 {
 MethodInfo m = typeof(GUIQuad).GetMethod("UpdateAspect", BindingFlags.Instance
 | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
 if (m != null) m.Invoke(gui, null);
 }
 catch { }
 }
#else
 // 【LEAN】整段删除（无任何调用点）
#endif
 }
}
