using System;
using System.Collections.Generic;
using UnityEngine;

namespace KKCharaStudioVRFix
{
#if DIAG
 /// <summary>
 /// 周期任务耗时打点。
 ///
 /// 起因：日志显示**稳态 60fps**（插件平时没代价），但场景卡加载窗口里出现
 /// 9750ms / 15489ms 的巨型停顿，加载后也长期 13~26fps。
 /// 光看"卡帧"数字无法判断是谁花的 —— 所以给看门狗里**每个周期任务**套一层计时，
 /// 任何一次超过阈值（默认 5ms）就落盘：`【耗时】GuiCanvasFix.RunPeriodic = 312ms`。
 ///
 /// 实现刻意做到零分配、零 GC：只用 `Time.realtimeSinceStartup` 两个 float 相减，
 /// 不打日志时不拼字符串（拼字符串只在超阈值时发生）。
 /// 同名任务 3 秒内最多报一次，避免刷屏。
 /// </summary>
 internal static class PerfProbe
 {
 /// <summary>超过这个毫秒数才落盘（低于它的开销不值得关心）。</summary>
 internal const float ThresholdMs = 5f;

 private struct Frame
 {
 public string Name;
 public float Start;
 public float Accum; // 同一帧内同名任务累加（例如每帧多次调用的）
 public float LastLog;
 }

 private static readonly Dictionary<string, Frame> _frames = new Dictionary<string, Frame>(16);
 private static readonly List<string> _keyBuf = new List<string>(16); // 复用，避免每帧分配

 // 每 10 秒的插件每帧任务耗时统计（零分配攒字段）
 internal static float StatTotal;
 internal static int StatFrames;
 internal static float StatMax;
 private static float _statTotal;
 private static int _statFrames;
 private static float _statMax;

 // ================= 掉帧归因 =================
 // 需求：用户实测"帧数动不动从 53 掉到 48"，但 10 秒平均值看不出是哪一秒、
 // 更看不出**那一秒是谁干的**。所以按秒给每个任务的耗时单独分桶：
 // 当某秒帧数下降时，直接列出该秒所有任务的耗时 → 一眼点名凶手，不用猜。
 private static readonly Dictionary<string, float> _secAccum = new Dictionary<string, float>(16);
 private static float _secTotal;
 internal static float SecondTotal { get { return _secTotal; } }

 /// <summary>把某任务本帧耗时同时记入"本秒"分桶（在 End 里调用）。</summary>
 private static void AddSecond(string name, float ms)
 {
 float v;
 if (!_secAccum.TryGetValue(name, out v)) v = 0f;
 _secAccum[name] = v + ms;
 _secTotal += ms;
 }

 /// <summary>
 /// 取走并清零"本秒"分桶，返回形如 "VRRenderQueueFix=41.2ms, DragStateFix=3.1ms" 的明细
 /// （按耗时降序）。只在掉帧时调用，分配可接受。
 /// </summary>
 internal static string DescribeSecondStats()
 {
 try
 {
 if (_secAccum.Count == 0) return "无";
 var list = new System.Collections.Generic.List<KeyValuePair<string, float>>(_secAccum);
 list.Sort((a, b) => b.Value.CompareTo(a.Value));
 var sb = new System.Text.StringBuilder();
 for (int i = 0; i < list.Count; i++)
 {
 if (list[i].Value < 0.05f) continue; // 忽略可忽略的量
 if (sb.Length > 0) sb.Append(", ");
 sb.Append(list[i].Key).Append('=').Append(list[i].Value.ToString("0.0")).Append("ms");
 }
 _secAccum.Clear();
 float t = _secTotal; _secTotal = 0f;
 return (sb.Length == 0 ? "无" : sb.ToString()) + "（合计" + t.ToString("0.0") + "ms）";
 }
 catch { return "<读取失败>"; }
 }

 /// <summary>清零本秒分桶（未掉帧的秒也要清，否则会累积到下秒）。</summary>
 internal static void ClearSecondStatsQuiet() { ClearSecondStats(); }

 internal static void ClearSecondStats2() { ClearSecondStats(); }

 internal static void ClearSecondStats()
 {
 _secAccum.Clear();
 _secTotal = 0f;
 }

 /// <summary>由 FPS 计每 10 秒调用：取走并清零统计值。</summary>
 internal static void CollectStat()
 {
 StatTotal = _statTotal; StatFrames = _statFrames; StatMax = _statMax;
 _statTotal = 0f; _statFrames = 0; _statMax = 0f;
 }
 private static float _frameStart = -1f;
 private static float _frameWorst = -1f;
 private static int _sumFrames;
 private static float _sumTotal;
 private static float _sumMax;
 private static float _sumStart = -999f;

 [System.Diagnostics.Conditional("DIAG")]
 internal static void Begin(string name)
 {
#if LEAN
 return;
#endif
 // 诊断总开关关掉时直接短路 —— 稳态每帧零开销。
 if (!KKCharaStudioVRFixPlugin.DiagOn) return;
 try
 {
 Frame f;
 if (!_frames.TryGetValue(name, out f))
 {
 f = new Frame { Name = name };
 }
 f.Start = Time.realtimeSinceStartup;
 _frames[name] = f;
 }
 catch { }
 }

 [System.Diagnostics.Conditional("DIAG")]
 internal static void End(string name)
 {
#if LEAN
 return;
#endif
 if (!KKCharaStudioVRFixPlugin.DiagOn) return;
 try
 {
 Frame f;
 if (!_frames.TryGetValue(name, out f)) return;
 float ms = (Time.realtimeSinceStartup - f.Start) * 1000f;
 if (ms < 0f) ms = 0f;

 if (ms >= ThresholdMs && Time.realtimeSinceStartup - f.LastLog > 3f)
 {
 f.LastLog = Time.realtimeSinceStartup;
 _frames[name] = f;
 FixLog.Key("【耗时】" + name + " = " + ms.ToString("0.0") + "ms"
 + "（单次周期任务；稳态 60fps 说明平时不花时间，这里只抓加载期的尖峰）");
 return;
 }
 f.Accum += ms;
 _frames[name] = f;
 AddSecond(name, ms); // 同时记入"本秒"分桶，用于掉帧归因
 }
 catch { }
 }

 /// <summary>
 /// 每帧调用：记录"这一帧总共花在插件周期任务上的时间"。
 /// 只在超过阈值时落盘 —— 这样能直接回答"这一帧的卡顿是不是插件造成的"。
 /// </summary>
 internal static void FrameTick()
 {
#if LEAN
 return;
#endif
 if (!KKCharaStudioVRFixPlugin.DiagOn) return;
 try
 {
 float now = Time.realtimeSinceStartup;
 if (_frameStart < 0f) { _frameStart = now; return; }

 float frameMs = (now - _frameStart) * 1000f;
 _frameStart = now;

 // 累加本帧各任务耗时。
 // 注意：**不能在 foreach 里回写字典**（虽然是覆盖已有键、当前不会抛，
 // 但一旦哪天变成 Add/Remove 就会抛，而且抛了会被 catch 吞掉 →
 // Accum 永不清零 → 之后每帧都报"耗时超标"的假告警）。
 // 改成：先把键收集到复用列表，再在枚举之外清零。
 float total = 0f;
 _keyBuf.Clear();
 foreach (KeyValuePair<string, Frame> kv in _frames)
 {
 total += kv.Value.Accum;
 if (kv.Value.Accum > 0f) _keyBuf.Add(kv.Key);
 }
 for (int i = 0; i < _keyBuf.Count; i++)
 {
 Frame f;
 if (_frames.TryGetValue(_keyBuf[i], out f)) { f.Accum = 0f; _frames[_keyBuf[i]] = f; }
 }

 // 不再只在超阈值时才报 —— 用户明确要求"找出插件每帧到底花多少"，
 // 所以每 5 秒**无条件**打一条平均/最大，哪怕只有 0.05ms/帧 也要有数字。
 // 这样"插件每帧 CPU 是否就是那 20ms"就有了直接答案（而不是靠推测）。
 _sumFrames++;
 _sumTotal += total;
 if (total > _sumMax) _sumMax = total;
 if (now - _sumStart >= 5f)
 {
 float avg = _sumFrames > 0 ? (_sumTotal / _sumFrames) : 0f;
 FixLog.Key("【耗时】插件每帧合计：平均 " + avg.ToString("0.00") + " ms/帧，最大 "
 + _sumMax.ToString("0.0") + " ms（统计 " + _sumFrames + " 帧）"
 + (avg >= 5f ? " ← **偏大，很可能就是掉帧来源**" : ""));
 _sumStart = now; _sumFrames = 0; _sumTotal = 0f; _sumMax = 0f;
 }

 if (total >= 20f && frameMs > 0f)
 {
 if (now - _frameWorst > 3f)
 {
 _frameWorst = now;
 FixLog.Key("【耗时】本帧插件任务合计 " + total.ToString("0.0")
 + "ms（该帧共 " + frameMs.ToString("0.0") + "ms）");
 }
 }

 // 每帧"插件任务总耗时"的分位数统计。
 // 稳态下 total 常常不到 20ms → 不会触发上面的告警 → 就看不到插件占了多少帧预算。
 // 现在把每帧的量攒起来，每 10 秒和中位/95 分位一起打（并到 FPS 那一行）。
 _statTotal += total;
 _statFrames++;
 if (total > _statMax) _statMax = total;
 }
 catch { }
 }

 internal static string Describe()
 {
 return "耗时打点阈值 " + ThresholdMs + "ms；已跟踪 " + _frames.Count + " 个周期任务";
 }
 }
#else
 /// <summary>
 /// 【精简版】耗时打点**整体不存在**。
 ///
 /// 为什么能整类拿掉：
 /// · 16 处调用点全部是 `PerfProbe.Begin/End`，两个方法都带 `[Conditional("DIAG")]`
 /// → 精简版不定义 DIAG，编译器把**调用连同参数**一起剔除，运行时一次都不会进来；
 /// · 唯一被无条件调用的是 `FrameTick()`（每帧一次）→ 这里做成空方法；
 /// · 其余成员（StatTotal / DescribeSecondStats / CollectStat …）只有
 /// `JumpDiagnostics` 在用，而它在精简版被 `Compile Remove` 了。
 ///
 /// 省下的不只是几十行代码：原来 `Dictionary<string,Frame>` / `List<string>` /
 /// `Dictionary<string,float>` 三个集合、`Frame` 结构体和一整套分桶统计都在 DLL 里。
 /// </summary>
 internal static class PerfProbe
 {
 [System.Diagnostics.Conditional("DIAG")]
 internal static void Begin(string name) { }

 [System.Diagnostics.Conditional("DIAG")]
 internal static void End(string name) { }

 /// <summary>看门狗每帧调一次；精简版什么都不记。</summary>
 internal static void FrameTick() { }
 }
#endif
}
