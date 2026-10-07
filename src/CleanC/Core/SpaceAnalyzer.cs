namespace CleanC.Core;

/// <summary>占用分析参数。</summary>
public sealed class AnalyzeOptions
{
    /// <summary>逐层展示的最大深度（更深的目录会合并进该层祖先，避免节点爆炸）。</summary>
    public int MaxDepth { get; init; } = 4;

    /// <summary>文件数上限（超出即截断，结果为下界）。</summary>
    public long MaxFiles { get; init; } = 5_000_000;

    /// <summary>耗时上限（毫秒）。</summary>
    public int MaxMilliseconds { get; init; } = 600_000;

    /// <summary>节点数上限（超出后不再新建节点，占用并入根节点）。</summary>
    public int MaxNodes { get; init; } = 200_000;

    public static readonly AnalyzeOptions Default = new();
}

/// <summary>一次占用分析中的进度。</summary>
public readonly record struct SpaceProgress(long Files, long Bytes, int ElapsedMs);

/// <summary>分析结果中的一个目录节点（大小含子孙）。</summary>
public sealed class DirNode
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required int Depth { get; init; }
    public string? ParentPath { get; init; }

    /// <summary>该目录直属文件的字节数。</summary>
    public long OwnBytes { get; set; }

    /// <summary>含全部子孙的字节数。</summary>
    public long TotalBytes { get; set; }

    public long Files { get; set; }
    public long Dirs { get; set; }

    /// <summary>该节点是否因深度上限被折叠（其内部还有更细的层级）。</summary>
    public bool IsFolded { get; set; }

    public List<DirNode> Children { get; } = new();

    public bool IsRoot => ParentPath == null;

    public double RatioOf(long total) => total > 0 ? (double)TotalBytes / total : 0;
}

/// <summary>一次占用分析的完整快照（只读统计，不含任何删除入口）。</summary>
public sealed class SpaceSnapshot
{
    public required string Root { get; init; }
    public required IReadOnlyDictionary<string, DirNode> Nodes { get; init; }

    /// <summary>根目录直属文件的字节数（pagefile.sys、hiberfil.sys 等）。</summary>
    public long RootOwnBytes { get; init; }

    public long TotalBytes { get; init; }
    public long FileCount { get; init; }
    public long DirCount { get; init; }

    /// <summary>无权限而未能统计的目录数。</summary>
    public int Denied { get; init; }

    /// <summary>跳过的重解析点（联接/符号链接）条目数。</summary>
    public long SkippedReparse { get; init; }

    /// <summary>是否因上限截断（此时 TotalBytes 为下界）。</summary>
    public bool Truncated { get; init; }

    public int ElapsedMs { get; init; }

    public long VolumeTotalBytes { get; init; }
    public long VolumeFreeBytes { get; init; }

    /// <summary>
    /// “卷已用 − 统计到”的差额：为正表示有未统计到的空间（无权限、卷元数据、系统保留）；
    /// 为负表示统计值反超卷已用（硬链接、NTFS 压缩、去重存储按逻辑大小累加导致）。
    /// </summary>
    public long UnaccountedBytes => (VolumeTotalBytes - VolumeFreeBytes) - TotalBytes;

    /// <summary>统计值是否反超卷已用（逻辑大小 &gt; 物理占用）。</summary>
    public bool OverCounted => VolumeTotalBytes > 0 && UnaccountedBytes < 0;

    public DirNode? RootNode => Nodes.TryGetValue(Root, out var n) ? n : null;

    /// <summary>某目录的直接子节点（按大小降序）。</summary>
    public IReadOnlyList<DirNode> ChildrenOf(string path)
    {
        if (!Nodes.TryGetValue(path, out var node)) return Array.Empty<DirNode>();
        return node.Children;
    }

    /// <summary>顶层目录（按大小降序）。</summary>
    public IReadOnlyList<DirNode> TopLevel => ChildrenOf(Root);
}

/// <summary>
/// 占用分析引擎：单遍遍历（不跟随联接/符号链接、无权限继续）统计每个目录的大小，
/// 按 MaxDepth 折叠深层目录并自底向上汇总；用于“找出占用空间的位置”。
/// 纯只读：不移动、不删除任何文件。
/// </summary>
public static class SpaceAnalyzer
{
    private const int ProgressInterval = 20_000;

    public static Task<SpaceSnapshot> AnalyzeAsync(string root, AnalyzeOptions? options = null,
        Action<SpaceProgress>? progress = null, CancellationToken ct = default)
        => Task.Run(() => Analyze(root, options ?? AnalyzeOptions.Default, progress, ct), ct);

    public static SpaceSnapshot Analyze(string root, AnalyzeOptions options,
        Action<SpaceProgress>? progress, CancellationToken ct)
    {
        string full = NormalizeKey(Path.GetFullPath(root));
        if (!Directory.Exists(full))
            return Empty(full);

        int rootSeps = CountSeparators(full);
        var aggregator = new Aggregator(full, rootSeps, options);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        FastDirectoryWalker walker;
        try
        {
            walker = new FastDirectoryWalker(full, aggregator);
        }
        catch (Exception)
        {
            return Empty(full);
        }

        bool truncated = false;
        try
        {
            while (walker.MoveNext())
            {
                if (walker.Files > 0 && walker.Files % ProgressInterval == 0)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Invoke(new SpaceProgress(walker.Files, walker.Bytes, (int)sw.ElapsedMilliseconds));
                    if (walker.Files >= options.MaxFiles ||
                        sw.ElapsedMilliseconds > options.MaxMilliseconds)
                    {
                        truncated = true;
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { /* 遍历中途异常：保留已统计部分 */ }
        finally
        {
            walker.Dispose();
        }

        aggregator.Flush();
        bool anyTruncated = truncated || aggregator.Truncated;

        var nodes = BuildTree(aggregator.Nodes, full, rootSeps);

        long volumeTotal = 0, volumeFree = 0;
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(full) ?? full);
            if (drive.IsReady)
            {
                volumeTotal = drive.TotalSize;
                volumeFree = drive.AvailableFreeSpace;
            }
        }
        catch (Exception) { /* 非卷根或不可读：忽略 */ }

        var rootNode = nodes.TryGetValue(full, out var rn) ? rn : null;
        progress?.Invoke(new SpaceProgress(walker.Files, walker.Bytes, (int)sw.ElapsedMilliseconds));

        return new SpaceSnapshot
        {
            Root = full,
            Nodes = nodes,
            RootOwnBytes = rootNode?.OwnBytes ?? 0,
            TotalBytes = rootNode?.TotalBytes ?? walker.Bytes,
            FileCount = rootNode?.Files ?? walker.Files,
            DirCount = rootNode?.Dirs ?? walker.Dirs,
            Denied = walker.Denied,
            SkippedReparse = walker.SkippedReparse,
            Truncated = anyTruncated,
            ElapsedMs = (int)sw.ElapsedMilliseconds,
            VolumeTotalBytes = volumeTotal,
            VolumeFreeBytes = volumeFree,
        };
    }

    private static SpaceSnapshot Empty(string root)
    {
        var nodes = new Dictionary<string, DirNode>(StringComparer.OrdinalIgnoreCase)
        {
            [root] = new DirNode { Path = root, Name = DisplayName(root), Depth = 0, ParentPath = null },
        };
        return new SpaceSnapshot
        {
            Root = root,
            Nodes = nodes,
            Denied = 1,
        };
    }

    // ---------- 树构建 ----------

    private static Dictionary<string, DirNode> BuildTree(Dictionary<string, DirNode> raw, string root, int rootSeps)
    {
        var nodes = new Dictionary<string, DirNode>(raw, StringComparer.OrdinalIgnoreCase);

        // 1) 补全缺失的中间目录（某层目录没有直属文件时不会出现节点）
        foreach (var key in nodes.Keys.ToArray())
        {
            string? parent = ParentOf(key, root);
            while (parent != null && !nodes.ContainsKey(parent))
            {
                nodes[parent] = new DirNode
                {
                    Path = parent,
                    Name = DisplayName(parent),
                    Depth = CountSeparators(parent) - rootSeps,
                    ParentPath = ParentOf(parent, root),
                };
                parent = ParentOf(parent, root);
            }
        }

        // 2) 挂接父子关系
        foreach (var node in nodes.Values)
            if (node.ParentPath != null && nodes.TryGetValue(node.ParentPath, out var parent))
            {
                parent.Children.Add(node);
                node.TotalBytes = node.OwnBytes;
            }

        if (nodes.TryGetValue(root, out var rootNode)) rootNode.TotalBytes = rootNode.OwnBytes;

        // 3) 自底向上汇总（按深度降序，保证子节点先算完）
        foreach (var node in nodes.Values.OrderByDescending(n => n.Depth))
        {
            if (node.ParentPath == null || !nodes.TryGetValue(node.ParentPath, out var parent)) continue;
            parent.TotalBytes += node.TotalBytes;
            parent.Files += node.Files;
            parent.Dirs += node.Dirs;
        }

        // 4) 子节点按大小降序
        foreach (var node in nodes.Values)
            node.Children.Sort((a, b) => b.TotalBytes.CompareTo(a.TotalBytes));

        return nodes;
    }

    private static string? ParentOf(string key, string root)
    {
        if (string.Equals(key, root, StringComparison.OrdinalIgnoreCase)) return null;
        string? parent = Path.GetDirectoryName(key);
        if (string.IsNullOrEmpty(parent)) return null;
        parent = NormalizeKey(parent);
        return string.Equals(parent, key, StringComparison.OrdinalIgnoreCase) ? null : parent;
    }

    // ---------- 路径工具 ----------

    internal static string NormalizeKey(string path)
    {
        string p = path;
        while (p.Length > 3 && (p[^1] == '\\' || p[^1] == '/')) p = p[..^1];
        if (p.Length == 2 && p[1] == ':') p += '\\';
        return p;
    }

    private static string DisplayName(string path)
    {
        string name = Path.GetFileName(path);
        return string.IsNullOrEmpty(name) ? path : name;
    }

    private static int CountSeparators(string path)
    {
        int count = 0;
        foreach (char c in path)
            if (c == '\\' || c == '/') count++;
        // 结尾分隔符不计（"C:\" 视为 0 个）
        return path.Length > 0 && (path[^1] == '\\' || path[^1] == '/') ? count - 1 : count;
    }

    // ---------- 聚合器 ----------

    /// <summary>按目录聚合文件字节/文件数/子目录数；深层目录折叠到 MaxDepth 层。</summary>
    private sealed class Aggregator : IDirectoryAggregator
    {
        private readonly string _root;
        private readonly int _rootSeps;
        private readonly int _maxDepth;
        private readonly int _maxNodes;

        private string _currentKey = string.Empty;
        private int _currentDepth;
        private long _own;
        private long _files;
        private long _dirs;
        private bool _pending;

        public Dictionary<string, DirNode> Nodes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool Truncated { get; private set; }

        public Aggregator(string root, int rootSeps, AnalyzeOptions options)
        {
            _root = root;
            _rootSeps = rootSeps;
            _maxDepth = Math.Max(1, options.MaxDepth);
            _maxNodes = Math.Max(1000, options.MaxNodes);
        }

        public void AddFile(ReadOnlySpan<char> directory, long length)
        {
            SwitchIfNeeded(directory);
            _own += length;
            _files++;
        }

        public void AddSubDirectory(ReadOnlySpan<char> parentDirectory)
        {
            SwitchIfNeeded(parentDirectory);
            _dirs++;
        }

        private void SwitchIfNeeded(ReadOnlySpan<char> directory)
        {
            if (_pending && directory.SequenceEqual(_currentKey.AsSpan())) return;
            Flush();
            _currentKey = KeyFor(directory);
            _currentDepth = Math.Max(0, CountSeparators(_currentKey) - _rootSeps);
            _pending = true;
        }

        /// <summary>把累计值写入节点表（同一目录可能被多次切换访问，因此累加而非覆盖）。</summary>
        public void Flush()
        {
            if (!_pending) return;
            if (_own != 0 || _files != 0 || _dirs != 0)
            {
                if (Nodes.TryGetValue(_currentKey, out var node))
                {
                    node.OwnBytes += _own;
                    node.Files += _files;
                    node.Dirs += _dirs;
                    node.TotalBytes = node.OwnBytes;
                }
                else if (Nodes.Count < _maxNodes || string.Equals(_currentKey, _root, StringComparison.OrdinalIgnoreCase))
                {
                    Nodes[_currentKey] = new DirNode
                    {
                        Path = _currentKey,
                        Name = DisplayName(_currentKey),
                        Depth = _currentDepth,
                        ParentPath = ParentOf(_currentKey, _root),
                        OwnBytes = _own,
                        TotalBytes = _own,
                        Files = _files,
                        Dirs = _dirs,
                        IsFolded = _currentDepth >= _maxDepth,
                    };
                }
                else
                {
                    // 节点数达上限：并入根节点，保证总量仍然正确
                    Truncated = true;
                    if (Nodes.TryGetValue(_root, out var rootNode))
                    {
                        rootNode.OwnBytes += _own;
                        rootNode.Files += _files;
                        rootNode.Dirs += _dirs;
                        rootNode.TotalBytes = rootNode.OwnBytes;
                    }
                }
            }
            _own = 0;
            _files = 0;
            _dirs = 0;
        }

        /// <summary>把目录路径折叠到 MaxDepth 层（避免为每个深层目录建节点）。</summary>
        private string KeyFor(ReadOnlySpan<char> directory)
        {
            int end = directory.Length;
            while (end > 3 && (directory[end - 1] == '\\' || directory[end - 1] == '/')) end--;
            var span = directory[..end];
            if (span.Length == 2 && span[1] == ':') { /* 盘根："C:" → "C:\" */ }

            int seps = 0;
            for (int i = 0; i < span.Length; i++)
                if (span[i] == '\\' || span[i] == '/') seps++;

            int depth = seps - _rootSeps;
            if (depth > _maxDepth)
            {
                // 截断到 (_rootSeps + _maxDepth) 个分隔符处
                int target = _rootSeps + _maxDepth;
                int seen = 0, cut = span.Length;
                for (int i = 0; i < span.Length; i++)
                {
                    if (span[i] != '\\' && span[i] != '/') continue;
                    if (++seen > target) { cut = i; break; }
                }
                span = span[..cut];
            }

            string key = new(span);
            if (span.Length == 2 && span[1] == ':') key += "\\";
            return key;
        }
    }
}
