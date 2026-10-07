using System.Text;

namespace CleanC.Core;

/// <summary>
/// 操作日志：把破坏性/关键操作写入 %LOCALAPPDATA%\CleanC\operations.log
/// （带时间戳、线程安全、超过 2MB 自动轮转保留一份 .old）。
/// 尽力而为策略：任何 IO 失败静默吞掉，绝不影响主流程；本地取证用途，不联网不上传。
/// </summary>
public static class OperationLog
{
    private static readonly object Sync = new();
    private const long MaxBytes = 2 * 1024 * 1024;      // 2MB 轮转阈值
    private const int DefaultTailLines = 2000;
    private const int DefaultTailBytes = 1024 * 1024;   // 查看器单次读取上限 1MB

    private static string? _overridePath;

    /// <summary>
    /// 日志文件路径。配置目录不可用时返回空串——**绝不能**退化成相对路径，
    /// 否则会把日志写到进程的当前目录（可能是 System32 或任意临时目录）。
    /// </summary>
    public static string CurrentPath
    {
        get
        {
            if (_overridePath is not null) return _overridePath;
            string? baseDir = UserContext.WritableDirectory(UserContext.LocalAppData);
            return baseDir is null ? string.Empty : Path.Combine(baseDir, "CleanC", "operations.log");
        }
    }

    /// <summary>测试用：注入自定义日志路径（空串恢复默认路径）。</summary>
    public static void SetPathForTest(string path) => _overridePath = string.IsNullOrEmpty(path) ? null : path;

    /// <summary>写一条记录（自动带时间戳；失败静默）。</summary>
    public static void Log(string category, string message)
    {
        try
        {
            string path = CurrentPath;
            if (path.Length == 0) return;   // 没有可用目录：宁可少记日志，也不写相对路径

            string line = $"{TimeText.StampNow()} [{category}] {SanitizeLine(message)}";
            lock (Sync)
            {
                string dir = Path.GetDirectoryName(path) ?? string.Empty;
                if (dir.Length == 0) return;
                Directory.CreateDirectory(dir);
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > MaxBytes)
                {
                    string old = path + ".old";
                    try { if (File.Exists(old)) File.Delete(old); } catch { /* 忽略，继续 */ }
                    try { File.Move(path, old, overwrite: true); } catch { /* 忽略，继续 */ }
                }
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch { /* 尽力而为：日志失败不影响主流程 */ }
    }

    /// <summary>读取最近日志（按行数与字节数截取；文件不存在/不可读返回空字符串）。</summary>
    public static string ReadTail(int maxLines = DefaultTailLines, int maxBytes = DefaultTailBytes)
    {
        try
        {
            lock (Sync)
            {
                if (!File.Exists(CurrentPath)) return string.Empty;
                if (new FileInfo(CurrentPath).Length == 0) return string.Empty;

                using var fs = new FileStream(CurrentPath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                long start = Math.Max(0, fs.Length - maxBytes);
                fs.Seek(start, SeekOrigin.Begin);
                using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var lines = sr.ReadToEnd().Split('\n');
                // 末尾换行产生的空元素不计入；Windows 下换行为 \r\n，去掉行尾 \r
                if (lines.Length > 0 && lines[^1].Length == 0)
                    lines = lines[..^1];
                lines = lines.Select(l => l.TrimEnd('\r')).ToArray();
                if (lines.Length > maxLines)
                    lines = lines[^maxLines..];
                return string.Join('\n', lines);
            }
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>清空日志（需调用方先确认）。成功返回 true。</summary>
    public static bool Clear()
    {
        try
        {
            string path = CurrentPath;
            if (path.Length == 0) return false;

            lock (Sync)
            {
                File.WriteAllText(path, string.Empty, Encoding.UTF8);
                return true;
            }
        }
        catch
        {
            return false;
        }
    }

    // ---------- 语义化记录（减少各窗口拼装差异） ----------

    /// <summary>主界面清理完成。</summary>
    public static void LogClean(int count, long freedBytes, int errors, string? extra = null)
    {
        string msg = $"清理完成：共 {count} 项，释放 {SizeFormatter.Format(freedBytes)}，{(errors > 0 ? $"{errors} 项错误" : "无错误")}";
        if (!string.IsNullOrEmpty(extra)) msg += "；" + extra;
        Log("清理", msg);
    }

    /// <summary>开发者环境删除完成。</summary>
    public static void LogDevDelete(int count, long freedBytes, int errors) =>
        Log("开发环境", $"删除完成：共 {count} 项，释放 {SizeFormatter.Format(freedBytes)}，{(errors > 0 ? $"{errors} 项提示" : "无错误")}");

    /// <summary>单个软件卸载结果。</summary>
    public static void LogUninstall(string appName, string status, string detail) =>
        Log("卸载", $"{appName}：{status}（{detail}）");

    /// <summary>残留清理删除结果。</summary>
    public static void LogLeftoverDelete(string appName, int count, long freedBytes, int errors) =>
        Log("残留清理", $"{appName}：删除 {count} 项，释放 {SizeFormatter.Format(freedBytes)}，{(errors > 0 ? $"{errors} 项提示" : "无错误")}");

    /// <summary>系统级提示（如启动、入口路径）。</summary>
    public static void LogSystem(string message) => Log("系统", message);

    // ---------- 磁盘分区（分盘 / 合盘） ----------

    /// <summary>磁盘与分区探测摘要（只读）。</summary>
    public static void LogPartitionProbe(string summary) => Log("分区-探测", summary);

    /// <summary>完整计划（可复现；含每步的等价命令）。</summary>
    public static void LogPartitionPlan(string planText) => Log("分区-计划", planText);

    /// <summary>单个执行步骤的前后状态。</summary>
    public static void LogPartitionStep(string step, string before, string after, bool ok, string detail) =>
        Log("分区-步骤",
            $"{step}：{(ok ? "成功" : "失败")}；前[{SanitizeLine(before)}] → 后[{SanitizeLine(after)}]" +
            (string.IsNullOrWhiteSpace(detail) ? string.Empty : $"；{detail}"));

    /// <summary>一次分区操作的最终结果。</summary>
    public static void LogPartitionResult(bool ok, string detail) =>
        Log("分区-结果", $"{(ok ? "成功" : "未完成")}：{detail}");

    /// <summary>
    /// 合盘删除前的"影响记录"：删除之后这就是唯一的可追溯线索，所以必须在删除**之前**写入。
    /// </summary>
    public static void LogMergeImpact(string sourceLetter, long files, long bytes, int denied, bool truncated) =>
        Log("分区-删除", $"即将删除 {sourceLetter}：其中 {files} 个文件 / {SizeFormatter.Format(bytes)}" +
                        (truncated ? "（统计已截断，实际更多）" : string.Empty) +
                        (denied > 0 ? $"；{denied} 个目录无权限未统计" : string.Empty) +
                        "；本工具不备份，数据将永久丢失");

    /// <summary>导出说明书。</summary>
    public static void LogPartitionManual(string path) => Log("分区-说明书", "已生成：" + path);

    // ---------- 内部 ----------

    private static string SanitizeLine(string message) =>
        (message ?? string.Empty).Replace("\r", "␍").Replace("\n", "␤");
}
