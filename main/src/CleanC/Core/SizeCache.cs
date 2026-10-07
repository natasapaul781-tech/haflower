using System.Collections.Concurrent;

namespace CleanC.Core;

/// <summary>一次目录测量的结果（含访问受限与截断标记）。</summary>
public sealed class SizeEntry
{
    public long Bytes { get; init; }
    public long Files { get; init; }
    public long Dirs { get; init; }

    /// <summary>无权限而未能统计的目录数（0 表示完整）。</summary>
    public int Denied { get; init; }

    /// <summary>是否因上限（文件数/耗时）截断，此时 Bytes 为下界。</summary>
    public bool Truncated { get; init; }

    public DateTime At { get; init; } = DateTime.UtcNow;

    /// <summary>UI 文案：截断显示 “≥”，有受限目录追加 “+”。</summary>
    public string Format()
    {
        string text = SizeFormatter.Format(Bytes);
        if (Truncated) text = "≥" + text;
        if (Denied > 0) text += $"+（{Denied} 个子目录无权限）";
        return text;
    }
}

/// <summary>
/// 会话级目录大小缓存：软件窗口、开发者环境、残留清理、占用分析共用，
/// 避免同一目录（如 %LOCALAPPDATA%\Programs）被反复统计。删除/卸载完成后调用 Invalidate*。
/// </summary>
public static class SizeCache
{
    private static readonly ConcurrentDictionary<string, SizeEntry> Map = new(StringComparer.OrdinalIgnoreCase);

    private static string Key(string path)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception) { return path.TrimEnd('\\', '/'); }
    }

    public static bool TryGet(string path, out SizeEntry entry) => Map.TryGetValue(Key(path), out entry!);

    public static void Set(string path, SizeEntry entry) => Map[Key(path)] = entry;

    public static void Invalidate(string path) => Map.TryRemove(Key(path), out _);

    /// <summary>失效某路径及其全部子路径的缓存（删除目录后调用）。</summary>
    public static void InvalidateSubtree(string path)
    {
        string root = Key(path);
        Map.TryRemove(root, out _);
        string prefix = root + Path.DirectorySeparatorChar;
        foreach (var k in Map.Keys)
            if (k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                Map.TryRemove(k, out _);
    }

    /// <summary>清空全部缓存（下一次测量重新读盘）。</summary>
    public static void Clear() => Map.Clear();

    public static int Count => Map.Count;
}
