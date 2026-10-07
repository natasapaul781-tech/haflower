using System.IO.Enumeration;

namespace CleanC.Core;

/// <summary>按目录聚合的接收器（占用分析用）：目录路径以 span 传入，避免每文件分配字符串。</summary>
internal interface IDirectoryAggregator
{
    void AddFile(ReadOnlySpan<char> directory, long length);

    void AddSubDirectory(ReadOnlySpan<char> parentDirectory);
}

/// <summary>
/// 快速目录遍历器（单次遍历、无 per-file 字符串分配）：
/// ① 跳过重解析点（联接/符号链接）既不计入也不下探，避免重复计数与死循环；
/// ② 无权限目录通过 ContinueOnError 计数后继续，不中断整体；
/// ③ 统计文件数、目录数与总字节数，供 MeasureDirectory / SpaceAnalyzer 共用。
/// </summary>
internal sealed class FastDirectoryWalker : FileSystemEnumerator<long>
{
    private readonly IDirectoryAggregator? _aggregator;

    public long Bytes { get; private set; }
    public long Files { get; private set; }
    public long Dirs { get; private set; }
    public int Denied { get; private set; }

    /// <summary>因是重解析点（联接/符号链接）而跳过的条目数。</summary>
    public long SkippedReparse { get; private set; }

    public FastDirectoryWalker(string root, IDirectoryAggregator? aggregator)
        : base(root, new EnumerationOptions
        {
            RecurseSubdirectories = true,
            // 自行处理不可访问目录（ContinueOnError），以便统计“无权限”数量
            IgnoreInaccessible = false,
            // 不按属性跳过：隐藏/系统文件（pagefile.sys 等）同样计入占用
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
            MatchType = MatchType.Simple,
        })
    {
        _aggregator = aggregator;
    }

    /// <summary>重解析点（联接/符号链接）不参与统计。</summary>
    protected override bool ShouldIncludeEntry(ref FileSystemEntry entry)
    {
        if ((entry.Attributes & FileAttributes.ReparsePoint) == 0) return true;
        SkippedReparse++;
        return false;
    }

    protected override bool ShouldRecurseIntoEntry(ref FileSystemEntry entry) =>
        (entry.Attributes & FileAttributes.ReparsePoint) == 0;

    protected override long TransformEntry(ref FileSystemEntry entry)
    {
        if (entry.IsDirectory)
        {
            Dirs++;
            _aggregator?.AddSubDirectory(entry.Directory);
            return 0;
        }

        long length = entry.Length;
        Files++;
        Bytes += length;
        _aggregator?.AddFile(entry.Directory, length);
        return length;
    }

    /// <summary>无权限/被占用目录：计数后继续（返回 true 不中断遍历）。</summary>
    protected override bool ContinueOnError(int error)
    {
        Denied++;
        return true;
    }
}
