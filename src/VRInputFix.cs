using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using VRGIN.Core;
using VRGIN.Native;

namespace KKCharaStudioVRFix
{
 /// <summary>
 /// VR 鼠标指针 / 窗口输入修复 —— Bug「手柄只剩圆盘 + VR 指针消失」与
 /// Bug「按扳机后指针跳到 UI 左上角、拖 ME 窗口只能水平动」。
 ///
 /// 以下机制全部来自反编译（VRGIN_KKCS + KKCharaStudioVRPlugin 0.0.3），不是猜测：
 ///
 /// ① **VR 指针本体**：`SimulatedCursor.OnGUI()` 只在 `Cursor.visible == true` 时才画，
 /// 位置直接取 `Input.mousePosition`。→ **游戏把系统光标藏起来时，VR 指针会一起消失**
 /// （用户报"VR 里鼠标指针也没了"）。
 /// ② **指针位置怎么来的**：0.0.3 `GripMenuHandler.UpdateLaser()` 用激光打到 GUIQuad，
 /// 把 `hit.textureCoord` 换算成屏幕坐标，再调
 /// `MouseOperations.SetClientCursorPosition(x, y)` **去移动真实鼠标**。
 /// ③ **那个函数的坐标基准**：`WindowManager.GetClientRect()`，而 `WindowManager.Handle`
 /// 是**一次性缓存**（`private static IntPtr? _Handle`，只在第一次访问时枚举本进程窗口）。
 /// 开机时若游戏窗口还没成形/句柄还没定，**整个会话都用错的 rect** →
 /// `SetCursorPos(x + Left, y + Top)` 把鼠标放到错误的绝对位置 →
 /// 指针贴到角落、IMGUI 拖窗口只能单方向动（用户实测"只能水平拖动"）。
 /// ④ **每帧抢光标**：`VRGUI.OnUpdate()` 在 `ConfineMouse=true`（我们的 XML 就是 true）
 /// 时每帧执行 `Cursor.lockState = Locked`。Locked 会隐藏光标并把光标钉在窗口中心，
 /// 与 ② 的注入互相打架。
 ///
 /// 本类做三件事（都有开关，默认开）：
 /// · 每帧兜住 `Cursor.visible = true`，让 ① 的指针能画出来；
 /// · 运行时把 context 的 `ConfineMouse` 置 false，停掉 ④ 的每帧抢光标；
 /// · 定期把 `WindowManager._Handle` 置空让它**重新解析**，并在
 /// `SetClientCursorPosition` 前校验 rect，无效就先重解析 —— 直接治 ③。
 /// 另外把每次注入的坐标/rect/光标状态落日志（节流），下次日志可判定 ② 到底打到了哪个面板。
 /// </summary>
 internal static class VRInputFix
 {
 private static bool _installed;
 private static bool _confineDisabled;
 private static bool _cursorForcedLogged;
 private static float _lastTick = -999f;
 private static float _lastInjectLog = -999f;
 private static float _lastStateLog = -999f;
 private static int _injectCount;
 private static int _cornerInjectCount;
#if !NEWFIX
 private static float _lastHandleResolve = -999f;
#endif

 internal static void Install(HarmonyLib.Harmony harmony)
 {
 if (_installed) return;
 try
 {
 MethodInfo prefix = AccessTools.Method(typeof(VRInputFix), "SetCursorPrefix");

 // 首选：SetClientCursorPosition —— 0.0.3 每次把真实鼠标移到 VR 指针处都走它。
 _installed = TryPatch(harmony, "SetClientCursorPosition",
 new Type[] { typeof(int), typeof(int) }, prefix);

 // 备选：0.0.3 里还有 SetCursorPosition(int,int)（同一条注入链路的上游写法）。
 if (!_installed)
 {
 FixLog.Warn("【VR 输入】SetClientCursorPosition 打不上补丁，改用备选挂钩 "
 + "MouseOperations.SetCursorPosition(int,int)。");
 _installed = TryPatch(harmony, "SetCursorPosition",
 new Type[] { typeof(int), typeof(int) }, prefix);
 }

 if (_installed)
 {
 FixLog.Key("【VR 输入】已挂 VR 鼠标注入诊断（每次注入会校验窗口 rect；"
 + "注入到 (0,0) 角落会单独报警）。");
 }
 else
 {
 // 必须把"仪器坏了"喊出来 ——
 // 以前这里只 Warn 一句就过去了，而日志里 `【VR 输入状态】…注入累计=0`
 // 会被误读成"SetClientCursorPosition 整场 0 次调用（激光链路没跑通）"，
 // 其实**计数器根本没装上去**，那个 0 是假证据（2026-10-16 日志已证实）。
 FixLog.Warn("【VR 输入】★ 两个挂钩点都打不上补丁 —— 鼠标注入诊断**不可用**："
 + "日志里的「注入累计」会恒为 0，**不能**当作「0 次调用」的证据。"
 + "（指针位置修正仍由 Tick 里的窗口句柄重解析兜底。）");
 }
 }
 catch (Exception e)
 {
 FixLog.Warn("【VR 输入】安装失败: " + e.Message);
 }
 }

 /// <summary>
 /// 打补丁并**校验真的挂上了** —— 本项目硬规矩：不能把"没抛异常"当成功。
 /// 校验方式是查 Harmony 自己的补丁表，而不是相信 Patch() 没有抛异常。
 /// </summary>
 private static bool TryPatch(HarmonyLib.Harmony harmony, string methodName,
 Type[] args, MethodInfo prefix)
 {
 try
 {
 MethodInfo target = AccessTools.Method(typeof(MouseOperations), methodName, args);
 if (target == null) return false;

 try { harmony.Patch(target, prefix: new HarmonyMethod(prefix)); }
 catch (Exception e) { FixLog.Warn("【VR 输入】patch " + methodName + " 失败: " + e.Message); }

 // 校验：补丁表里必须真的出现我们的前缀
 HarmonyLib.Patches info = HarmonyLib.Harmony.GetPatchInfo(target);
 if (info != null && info.Prefixes != null)
 {
 foreach (HarmonyLib.Patch p in info.Prefixes)
 {
 if (p != null && p.PatchMethod == prefix) return true;
 }
 }
 return false;
 }
 catch (Exception e)
 {
 FixLog.Debug("TryPatch 校验异常: " + e.Message);
 return false;
 }
 }

 /// <summary>
 /// 0.0.3 每次要把真实鼠标移到 VR 指针处都会走这里。
 /// 职责：① 校验窗口 rect（坏就先重解析句柄）② 记录注入坐标（含"贴角落"报警）。
 /// </summary>
 public static void SetCursorPrefix(int X, int Y)
 {
 try
 {
 _injectCount++;
 bool corner = X < 3 && Y < 3;
 if (corner) _cornerInjectCount++;

 // rect 无效（宽或高 <= 0）说明句柄缓存是坏的 → 立刻让它重新解析
 bool rectBad = false;
 try
 {
 WindowsInterop.RECT r = WindowManager.GetClientRect();
 rectBad = (r.Right - r.Left) <= 0 || (r.Bottom - r.Top) <= 0;
 if (rectBad)
 {
 ClearWindowHandleCache();
 r = WindowManager.GetClientRect();
 FixLog.Warn("【VR 输入】窗口 rect 无效，已强制重解析句柄 → 新 rect L="
 + r.Left + " T=" + r.Top + " R=" + r.Right + " B=" + r.Bottom);
 }
 }
 catch (Exception e) { FixLog.Debug("rect 校验失败: " + e.Message); }

 // 只在"贴角落"或明显异常时限速落盘（正常移动不刷屏）
 if ((corner || rectBad) && Time.realtimeSinceStartup - _lastInjectLog > 1f)
 {
 _lastInjectLog = Time.realtimeSinceStartup;
 string rectText = "?";
 try
 {
 WindowsInterop.RECT r2 = WindowManager.GetClientRect();
 rectText = "L=" + r2.Left + " T=" + r2.Top + " R=" + r2.Right + " B=" + r2.Bottom;
 }
 catch { }
 FixLog.Key("【VR 指针注入】目标=(" + X + "," + Y + ")"
 + (corner ? " ← 贴左上角！" : "") + " | 窗口 rect " + rectText
 + " | 累计注入 " + _injectCount + " 次，其中贴角落 " + _cornerInjectCount + " 次"
 + " | Cursor.visible=" + Cursor.visible + " lockState=" + Cursor.lockState);
 }
 }
 catch { }
 }

 /// <summary>每秒一次：兜光标可见性 / 停掉 ConfineMouse / 定期重解析句柄 / 状态落盘。</summary>
 internal static void Tick()
 {
 try
 {
 if (Time.realtimeSinceStartup - _lastTick < 10f) return;
 _lastTick = Time.realtimeSinceStartup;

 bool vrOn = false;
 try { vrOn = VR.Active; } catch { }
 if (!vrOn) return;

 // ① 停掉 VRGUI 每帧的 Cursor.lockState = Locked（ConfineMouse 置 false）
 if (!_confineDisabled
 && (KKCharaStudioVRFixPlugin.CfgDisableCursorConfine == null
 || KKCharaStudioVRFixPlugin.On(KKCharaStudioVRFixPlugin.CfgDisableCursorConfine)))
 {
 try
 {
 object ctx = VR.Context;
 if (ctx != null)
 {
 PropertyInfo pi = ctx.GetType().GetProperty("ConfineMouse",
 BindingFlags.Public | BindingFlags.Instance);
 if (pi != null && pi.CanWrite)
 {
 bool before = (bool)pi.GetValue(ctx, null);
 pi.SetValue(ctx, false, null);
 _confineDisabled = true;
 FixLog.Key("【VR 输入】已把 context.ConfineMouse " + before
 + " → false（停掉 VRGUI 每帧把光标钉在窗口中心）。");
 }
 }
 }
 catch (Exception e) { FixLog.Debug("ConfineMouse 覆盖失败: " + e.Message); }
 }

 // ② 兜住 Cursor.visible：VRGIN 的模拟指针只在 Cursor.visible=true 时绘制
 if (KKCharaStudioVRFixPlugin.CfgForceCursorVisible == null
 || KKCharaStudioVRFixPlugin.On(KKCharaStudioVRFixPlugin.CfgForceCursorVisible))
 {
 try
 {
 if (!Cursor.visible)
 {
 Cursor.visible = true;
 if (!_cursorForcedLogged)
 {
 _cursorForcedLogged = true;
 FixLog.Key("【VR 输入】检测到系统光标被隐藏（VR 指针会因此消失），"
 + "已强制 Cursor.visible = true。");
 }
 }
 }
 catch { }
 }

 // ③ 定期让 WindowManager 重新解析窗口句柄（治"句柄一次性缓存"）
#if NEWFIX
 // 【NEWFIX】改成"确认一次、永久不再查"（见 VerifyHandleOnce）。
 // 原来每 10 秒清一次缓存：如果刚查完 2 秒就坏了，要等 8 秒 —— 等于没修好。
#else
 if ((KKCharaStudioVRFixPlugin.CfgFixWindowHandle == null
 || KKCharaStudioVRFixPlugin.On(KKCharaStudioVRFixPlugin.CfgFixWindowHandle))
 && Time.realtimeSinceStartup - _lastHandleResolve > 10f)
 {
 _lastHandleResolve = Time.realtimeSinceStartup;
 ClearWindowHandleCache();
 }
#endif

 // ④ 状态变化落盘：只在"句柄 / rect / 光标状态"真正变化时打。
 // 改成**零分配**比较（原来每秒拼一个状态字符串，
 // 虽然不大但属于"没必要花的钱"）。字符串只在真要落盘时才拼。
 if (KKCharaStudioVRFixPlugin.DiagOn
 && StateChanged() && Time.realtimeSinceStartup - _lastStateLog > 1f)
 {
 _lastStateLog = Time.realtimeSinceStartup;
 FixLog.Key("【VR 输入状态】" + DescribeState());
 }
 }
 catch (Exception e) { FixLog.Debug("VRInputFix.Tick 异常: " + e.Message); }
 }

 /// <summary>把 WindowManager 的句柄缓存清掉，下次访问会重新枚举本进程窗口。</summary>
 private static void ClearWindowHandleCache() {
 try
 {
 FieldInfo f = typeof(WindowManager).GetField("_Handle",
 BindingFlags.NonPublic | BindingFlags.Static);
 if (f == null) return;
 f.SetValue(null, null);
 }
 catch (Exception e) { FixLog.Debug("清窗口句柄缓存失败: " + e.Message); }
 }

#if NEWFIX
 // ================= 【NEWFIX】窗口位置：确认一次，之后永久不再查 =================
 // 反编译依据：`SetClientCursorPosition` 内部每次都调 `WindowManager.GetClientRect()`，
 // 而 GetClientRect 用的是**永久缓存的窗口句柄**（`WindowManager._Handle`）。
 // 所以"窗口位置"本身每次都会重新问，唯一会错的就是那个句柄。
 // → 只要启动时确认句柄正确，就再也不需要任何定时查询。
 private static bool _handleVerified;
 private static float _lastHandleTry = -999f;

 /// <summary>看门狗每帧调（内部 0.5 秒节流）：直到窗口位置看起来正常为止，之后转入"按症状发现"。</summary>
 internal static void VerifyHandleOnce()
 {
 if (KKCharaStudioVRFixPlugin.CfgFixWindowHandle != null
 && !KKCharaStudioVRFixPlugin.On(KKCharaStudioVRFixPlugin.CfgFixWindowHandle))
 {
 _handleVerified = true; // 用户关掉了这项修复
 return;
 }

 // 【NEWFIX ★★】"确认一次就永久不管"是**错的**，日志实证：
 // 05:35:39 窗口位置已确认正常（0,0,1920,1080）
 // 05:36:02 rect 又变回 (-32000,-32000,-32000,-32000) ← 窗口被重建，旧句柄失效
 // 但也不能退回"每 10 秒查一次"（那要等 10 秒）。
 // 正解是**按症状发现**：鼠标坐标跑到离谱范围，就说明 rect 坏了 → 当场重解析。
 // 每帧只多一次 Input.mousePosition 读取 + 两次比较，没有定时器。
 try
 {
 Vector3 mp = Input.mousePosition;
 bool garbage = mp.x < -5000f || mp.x > 20000f || mp.y < -5000f || mp.y > 20000f;
 if (garbage && _handleVerified)
 {
 _handleVerified = false;
 _lastHandleTry = -999f;
 ClearWindowHandleCache();
 if (KKCharaStudioVRFixPlugin.DiagOn)
 FixLog.Key("【VR 输入·NEWFIX】鼠标坐标异常(" + mp.x.ToString("0") + "," + mp.y.ToString("0")
 + ") → 判定窗口句柄失效，立刻重解析（不等定时器）。");
 return;
 }
 }
 catch { }

 if (_handleVerified) return;
 if (Time.realtimeSinceStartup - _lastHandleTry < 0.5f) return;
 _lastHandleTry = Time.realtimeSinceStartup;

 try
 {
 WindowsInterop.RECT r = WindowManager.GetClientRect();
 int w = r.Right - r.Left;
 int h = r.Bottom - r.Top;
 // "看起来正常" = 有宽高、坐标不是 -32000 那类垃圾值
 bool sane = w > 0 && h > 0 && r.Left > -10000 && r.Top > -10000;
 if (sane)
 {
 _handleVerified = true;
 if (KKCharaStudioVRFixPlugin.DiagOn)
 FixLog.Key("【VR 输入·NEWFIX】窗口位置已确认正常（L=" + r.Left + " T=" + r.Top
 + " R=" + r.Right + " B=" + r.Bottom + "）→ 以后不再查询窗口位置。");
 FixVrGuiSizeIfBroken(r);
 return;
 }

 // 不正常 → 清掉句柄缓存，下次（0.5 秒后）重新解析
 ClearWindowHandleCache();
 }
 catch { }
 }

 /// <summary>换场景后重新确认一次（正常情况下第一次就对了，这里只是保险）。</summary>
 internal static void ReverifyHandle()
 {
 _handleVerified = false;
 _lastHandleTry = -999f;
 }

 /// <summary>
 /// 【NEWFIX ★】修「一按扳机，鼠标跳到左上角」。
 ///
 /// 反编译依据（VRGIN_KKCS / VRGUI.cs 初始化那段）：
 /// Width = clientRect.Right - clientRect.Left;
 /// Height = clientRect.Bottom - clientRect.Top;
 /// —— `VRGUI.Width/Height` 是**开机只取一次**的窗口客户区尺寸。
 /// 如果 VRGIN 初始化那一刻窗口还没成形（日志里出现过 rect = -32000,-32000,-32000,-32000），
 /// 这两个值就永久是 **0**。
 ///
 /// 而 0.0.3 的激光算屏幕坐标是：
 /// val = (hit.textureCoord.x * VRGUI.Width, (1 - hit.textureCoord.y) * VRGUI.Height)
 /// 宽高是 0 → 算出来永远是 **(0,0)** → 一按扳机就把真实鼠标挪到左上角。
 /// 这就是用户报的「扳机一按鼠标回左上角」。
 ///
 /// 修法：等窗口位置确认正常之后，把这两个值按真实客户区大小纠正一次（只改一次）。
 /// </summary>
 private static void FixVrGuiSizeIfBroken(WindowsInterop.RECT r)
 {
 try
 {
 int w = r.Right - r.Left;
 int h = r.Bottom - r.Top;
 if (w <= 0 || h <= 0) return;
 if (VRGUI.Width == w && VRGUI.Height == h) return;

 int oldW = VRGUI.Width, oldH = VRGUI.Height;
 // `public static int Width { get; private set; }` → 只能改 backing field
 Type t = typeof(VRGUI);
 System.Reflection.FieldInfo fw = t.GetField("<Width>k__BackingField",
 System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
 System.Reflection.FieldInfo fh = t.GetField("<Height>k__BackingField",
 System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
 if (fw == null || fh == null)
 {
 FixLog.Warn("【VR 输入·NEWFIX】VRGUI 宽高字段找不到，无法修正界面尺寸（"
 + oldW + "x" + oldH + "）—— 扳机鼠标跳左上角的问题会继续。");
 return;
 }
 fw.SetValue(null, w);
 fh.SetValue(null, h);
 FixLog.Key("【VR 输入·NEWFIX】VR 界面尺寸原本是 " + oldW + "x" + oldH
 + "（VRGIN 初始化时窗口还没成形，取到了 -32000 那种无效区域）→ 已修正为 "
 + w + "x" + h + "。不改这个，激光算出的屏幕坐标永远是 (0,0)，一按扳机鼠标就跳到左上角。");
 }
 catch (Exception e) { FixLog.Debug("FixVrGuiSizeIfBroken 异常: " + e.Message); }
 }
#endif

 // 上一次的"关键状态"（句柄/rect/光标），用于零分配比较
 private static bool _haveCore;
 private static IntPtr _coreHandle;
 private static int _coreL, _coreT, _coreR, _coreB;
 private static bool _coreVisible;
 private static int _coreLock;

 /// <summary>关键状态是否变化（零分配：只比较值，不拼字符串）。</summary>
 private static bool StateChanged()
 {
 try
 {
 IntPtr h = WindowManager.Handle;
 WindowsInterop.RECT r = WindowManager.GetClientRect();
 bool vis = Cursor.visible;
 int lk = (int)Cursor.lockState;
 if (_haveCore && h == _coreHandle && r.Left == _coreL && r.Top == _coreT
 && r.Right == _coreR && r.Bottom == _coreB && vis == _coreVisible && lk == _coreLock)
 return false;
 _haveCore = true;
 _coreHandle = h; _coreL = r.Left; _coreT = r.Top; _coreR = r.Right; _coreB = r.Bottom;
 _coreVisible = vis; _coreLock = lk;
 return true;
 }
 catch { return false; }
 }

 private static string DescribeState()
 {
 try
 {
 IntPtr h = WindowManager.Handle;
 WindowsInterop.RECT r = WindowManager.GetClientRect();
 Vector3 mp = Input.mousePosition;
 return "窗口句柄=" + h + " rect(L=" + r.Left + ",T=" + r.Top + ",R=" + r.Right
 + ",B=" + r.Bottom + ") 光标(visible=" + Cursor.visible
 + ",lock=" + Cursor.lockState + ") mousePos=("
 + mp.x.ToString("0") + "," + mp.y.ToString("0") + ") 注入累计="
 + (_installed ? _injectCount.ToString() + "（贴角落 " + _cornerInjectCount + "）"
 : "仪器未装上，本字段无效");
 }
 catch (Exception e) { return "<读取失败: " + e.Message + ">"; }
 }

 internal static string Describe()
 {
 return DescribeState() + "；ConfineMouse 已停=" + _confineDisabled
 + "；窗口句柄修复=" + (KKCharaStudioVRFixPlugin.CfgFixWindowHandle == null
 ? "?" : KKCharaStudioVRFixPlugin.On(KKCharaStudioVRFixPlugin.CfgFixWindowHandle).ToString());
 }
 }
}
