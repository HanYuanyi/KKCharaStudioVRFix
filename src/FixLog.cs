using System;
using System.IO;
using System.Text;
using BepInEx.Logging;

namespace KKCharaStudioVRFix
{
#if LEAN
 /// <summary>
 /// 【精简版】日志**整体不存在**。
 ///
 /// 保留的只有"签名"：
 /// · `Source` —— `Awake` 里有一句 `FixLog.Source = Logger;`（无条件执行），删字段会编译不过；
 /// · `InitFile` / `Flush` / `FlushTick` —— 分别在 Awake / OnDestroy / 每帧被无条件调用；
 /// · 五个日志方法全部 `[Conditional("DIAG")]` → 精简版不定义 DIAG，
 /// 编译器把**调用 + 参数（含中文字面量）+ 字符串拼接**一起剔除。
 ///
 /// 因此精简版**不再生成 `KKCharaStudioVRFix.log`**，也没有任何中文日志文本进 DLL。
 /// </summary>
 internal static class FixLog
 {
 internal static BepInEx.Logging.ManualLogSource Source;

 internal static string FilePath { get { return null; } }
 internal static void InitFile(string path) { }
 internal static void Flush() { }
 internal static void FlushTick() { }

 [System.Diagnostics.Conditional("DIAG")]
 internal static void Info(string msg) { }
 [System.Diagnostics.Conditional("DIAG")]
 internal static void Warn(string msg) { }
 [System.Diagnostics.Conditional("DIAG")]
 internal static void Debug(string msg) { }
 [System.Diagnostics.Conditional("DIAG")]
 internal static void Key(string msg) { }
 [System.Diagnostics.Conditional("DIAG")]
 internal static void Error(string msg) { }
 }
#else
 /// <summary>
 /// 日志。
 ///
 /// 除了走 BepInEx 日志，还会**额外写一份独立文件** —— 因为 BepInEx 的日志级别/
 /// 控制台过滤会吞掉 Info，排查时看不到关键信息。
 /// 文件位置： BepInEx\KKCharaStudioVRFix.log
 /// </summary>
 internal static class FixLog
 {
 internal static ManualLogSource Source;

 private static string _filePath;
 private static readonly object _lock = new object();
 private static bool _fileBroken;

 // ================= 日志改为**缓冲 + 定时落盘** =================
 // 事故（2026-10-18 实测）：`FixLog.Key` 走的是 `File.AppendAllText`
 // —— **渲染线程上的同步磁盘 IO**（打开/追加/关闭文件，还带一个 lock）。
 // 拖动 ME 窗口那次测量里，「写回」桶 625.9ms / 165 次 = 3.79ms/次，
 // 而其中绝大部分其实是**每秒一条【窗口拖动】日志**的磁盘开销，不是 Unity 改位置。
 // （同一批数据里 27 次那条测得 95.1ms，也正好是一条日志的量级。）
 //
 // 结论：诊断版自己就是"每几秒抽一下"的来源之一 —— `【秒帧】`每 10 秒、
 // `【耗时】`每 5 秒、`【指针距离】`每 1~3 秒，全是同步磁盘写。
 // 现在改成：先塞进内存缓冲，由看门狗每帧调 `FlushTick()`，
 // **最多每秒落盘一次**（或缓冲满 8KB 立刻落盘）。渲染线程上不再有文件 IO。
 // 代价：崩溃时最多丢 1 秒日志（可接受）。
 private static readonly StringBuilder _buf = new StringBuilder(8192);
 private static DateTime _lastFlushUtc = DateTime.UtcNow;

 internal static string FilePath { get { return _filePath; } }

 /// <summary>把缓冲写进文件（只在 FlushTick / 缓冲满 / 退出时调用）。</summary>
 internal static void Flush()
 {
 if (_fileBroken || _filePath == null) return;
 string text = null;
 try
 {
 lock (_lock)
 {
 if (_buf.Length == 0) return;
 text = _buf.ToString();
 _buf.Length = 0;
 }
 }
 catch { return; }

 try
 {
 lock (_lock) { File.AppendAllText(_filePath, text, Encoding.UTF8); }
 }
 catch
 {
 _fileBroken = true;
 }
 }

 /// <summary>看门狗每帧调用：最多每秒落盘一次；缓冲里没东西就零开销返回。</summary>
 internal static void FlushTick()
 {
 if (_fileBroken || _filePath == null) return;
 bool has;
 lock (_lock) { has = _buf.Length > 0; }
 if (!has) return;
 DateTime now = DateTime.UtcNow;
 if ((now - _lastFlushUtc).TotalMilliseconds < 1000) return;
 _lastFlushUtc = now;
 Flush();
 }

 internal static void InitFile(string path)
 {
 try
 {
 _filePath = path;
 string dir = Path.GetDirectoryName(path);
 if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
 Directory.CreateDirectory(dir);

 File.WriteAllText(path,
 "=== KKCharaStudioVRFix log ===" + Environment.NewLine
 + "start: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine
 + Environment.NewLine,
 Encoding.UTF8);
 }
 catch
 {
 _fileBroken = true;
 }
 }

 [System.Diagnostics.Conditional("DIAG")]
 internal static void Info(string msg) {
#if LEAN
 return; // 精简版：日志不输出
#endif
 Write(LogLevel.Info, msg); }
 [System.Diagnostics.Conditional("DIAG")]
 internal static void Warn(string msg) {
#if LEAN
 return; // 精简版：日志不输出
#endif
 Write(LogLevel.Warning, msg); }
 internal static void Error(string msg) { Write(LogLevel.Error, msg); }
 [System.Diagnostics.Conditional("DIAG")]
 internal static void Debug(string msg) {
#if LEAN
 return; // 精简版：日志不输出
#endif
 Write(LogLevel.Debug, msg); }

 /// <summary>关键信息：同时以 Message 级别输出，保证在控制台里也能看到。</summary>
 [System.Diagnostics.Conditional("DIAG")]
 internal static void Key(string msg) {
#if LEAN
 return; // 精简版：日志不输出
#endif
 Write(LogLevel.Message, msg); }

 private static void Write(LogLevel level, string msg)
 {
 try
 {
 if (Source != null) Source.Log(level, "[VRFix] " + msg);
 }
 catch { }

 if (_fileBroken || _filePath == null) return;

 // 只有"真正值得留档"的级别才写文件。
 //
 // 原因：写文件是**同步磁盘 IO**（File.AppendAllText + lock），
 // 而 Info/Debug 的量非常大（每次加载几十条、周期检查还可能持续产）。
 // 稳态下这些行没人看，却要在渲染线程上做磁盘写入 —— 是加载卡顿与
 // 帧时间抖动的来源之一。
 //
 // 现在：Key / Warning / Error / Fatal 落盘（这些是"出事了要看的"），
 // Info / Debug 只走 BepInEx 控制台（BepInEx 自己有缓冲，不阻塞渲染）。
 // 需要完整日志排查时，把 BepInEx 的 Logging.Console 打上即可。
 if (level != LogLevel.Message && level != LogLevel.Warning
 && level != LogLevel.Error && level != LogLevel.Fatal)
 return;

 try
 {
 string line = DateTime.Now.ToString("HH:mm:ss.fff") + " [" + level + "] " + msg
 + Environment.NewLine;
 bool full;
 lock (_lock)
 {
 _buf.Append(line);
 full = _buf.Length >= 8192;
 }
 // 缓冲满了就立刻落盘，避免长时间不 flush 时内存无限增长
 if (full) Flush();
 }
 catch
 {
 _fileBroken = true;
 }
 }
 }
#endif
}
