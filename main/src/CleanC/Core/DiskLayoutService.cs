using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace CleanC.Core;

/// <summary>BitLocker 保护状态（由 Win32_EncryptableVolume 判定，不含本地化文本）。</summary>
public enum BitLockerProtection
{
    /// <summary>无法判定（WMI 类不存在或查询失败）。</summary>
    Unknown,

    /// <summary>未加密（ConversionStatus = 全解密）。</summary>
    Off,

    /// <summary>已加密且保护已开启——调整分区前应先挂起保护。</summary>
    On,

    /// <summary>已加密但保护已挂起（调整分区相对安全，之后需恢复保护）。</summary>
    Suspended,
}

/// <summary>某个盘符的 BitLocker 状态。</summary>
public sealed class BitLockerState
{
    public required char DriveLetter { get; init; }
    public BitLockerProtection Protection { get; init; } = BitLockerProtection.Unknown;

    /// <summary>ConversionStatus 原始值（0 全解密 / 1 全加密 / 2 加密中 / 3 解密中 / 4 加密暂停 / 5 解密暂停）。</summary>
    public int ConversionStatus { get; init; }

    public string Text => Protection switch
    {
        BitLockerProtection.On => "已加密（保护开启，调整分区前需先挂起）",
        BitLockerProtection.Suspended => "已加密（保护已挂起）",
        BitLockerProtection.Off => "未加密",
        _ => "无法判定",
    };
}

/// <summary>影响分区操作的环境事实（**只读**采集）。</summary>
public sealed class DiskEnvironmentFacts
{
    /// <summary>按盘符记录的 BitLocker 状态。</summary>
    public IReadOnlyDictionary<char, BitLockerState> BitLocker { get; init; } =
        new Dictionary<char, BitLockerState>();

    /// <summary>页面文件所在路径（如 <c>C:\pagefile.sys</c>）；空表示无页面文件或无法读取。</summary>
    public IReadOnlyList<string> PageFilePaths { get; init; } = Array.Empty<string>();

    /// <summary>系统是否启用休眠（注册表 HibernateEnabled）。</summary>
    public bool HibernateEnabled { get; init; }

    /// <summary>各卷根目录下 hiberfil.sys 的实际大小之和（字节）；判定不到为 0。</summary>
    public long HiberFileBytes { get; init; }

    /// <summary>卷影副本（系统还原/备份）使用量（字节）；<see cref="VssKnown"/> 为 false 时无意义。</summary>
    public long VssUsedBytes { get; init; }

    public bool VssKnown { get; init; }

    /// <summary>
    /// 系统保护（系统还原）是否启用。启发式判定（注册表 DisableSR），**仅用于提示**，不作为阻断条件。
    /// </summary>
    public bool SystemRestoreEnabled { get; init; }

    /// <summary>系统还原允许占用的磁盘百分比（0 表示未配置）。</summary>
    public int SystemRestoreDiskPercent { get; init; }

    public BitLockerState? BitLockerOf(char letter) =>
        BitLocker.TryGetValue(char.ToUpperInvariant(letter), out var s) ? s : null;
}

/// <summary>一次完整探测的结果（磁盘布局 + 环境事实）。</summary>
public sealed class DiskProbeResult
{
    public required DiskLayoutSnapshot Layout { get; init; }
    public required DiskEnvironmentFacts Environment { get; init; }
}

/// <summary>
/// 磁盘与分区**只读**探测服务：枚举磁盘/分区/卷，按需查询可收缩/可扩展范围（GetSupportedSize），
/// 并采集影响分区操作的环境事实（BitLocker、页面文件、休眠、卷影副本、系统还原）。
///
/// 本类不含任何修改入口：不创建、不删除、不调整任何分区与文件。
/// </summary>
public static class DiskLayoutService
{
    // 注意：这里**不要**再放置「不带大括号的 GPT 类型 GUID 常量」。
    // 实测 MSFT_Partition.GptType 返回的是带大括号的形式（{ebd0a0a2-…}），直接拿不带括号的字符串比较
    // 会让所有 GPT 分区落成 PartitionKind.Other（历史故障）。GUID 判据统一走 NormalizeGuid + Guid* 常量。

    private const int ProbeTimeoutMs = 60_000;
    private const int EnvTimeoutMs = 60_000;

    /// <summary>GetSupportedSize 会启动 defragsvc（Optimize Drive）服务，大容量/碎片多的盘耗时较长。</summary>
    public const int SupportedSizeTimeoutMs = 600_000;

    // ---------- 入口 ----------

    public static Task<DiskProbeResult> ProbeAsync(CancellationToken ct, Action<string>? log = null)
        => Task.Run(() => Probe(ct, log), ct);

    public static DiskProbeResult Probe(CancellationToken ct, Action<string>? log)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var problems = new List<string>();
        bool elevated = ElevationHelper.IsElevated();

        var storage = QueryStorage(problems, log, ct, elevated);
        var env = QueryEnvironment(problems, log, ct, elevated);

        var layout = new DiskLayoutSnapshot
        {
            Disks = storage.Disks,
            Partitions = storage.Partitions,
            Volumes = storage.Volumes,
            Problems = problems,
            CapturedAt = DateTime.Now,
            ElapsedMs = (int)sw.ElapsedMilliseconds,
            SystemDriveLetter = SystemDriveLetter(),
            UsedDriveLetters = storage.Letters,
            IsElevated = elevated,
            RawJson = storage.RawJson,
        };

        log?.Invoke($"磁盘探测完成：{storage.Disks.Count} 块磁盘、{storage.Partitions.Count} 个分区、" +
                    $"{storage.Volumes.Count} 个卷，用时 {sw.ElapsedMilliseconds} ms" +
                    (elevated ? string.Empty : "（当前进程不是管理员）"));
        foreach (var p in problems) log?.Invoke("  ~ " + p);

        return new DiskProbeResult { Layout = layout, Environment = env };
    }

    /// <summary>
    /// 磁盘/分区信息受 WMI 命名空间 ACL 保护：非管理员查询一律返回“拒绝访问”，且**没有**可用的非提权回退
    /// （MSFT_* 与 Win32_DiskPartition 同样被拒）。因此这里把权限问题合并成一条可行动提示，而不是逐类刷屏。
    /// </summary>
    private const string ElevationRequiredMessage =
        "需要管理员权限：磁盘与分区信息（MSFT_Disk / MSFT_Partition / MSFT_Volume）受系统 ACL 保护，非管理员查询一律“拒绝访问”。" +
        "本程序默认以普通权限启动，请点窗口里的「以管理员身份重新启动」（会弹出 UAC 确认）。";

    private static void NoteAccessDenied(List<string> problems, bool elevated)
    {
        // 启用了管理员权限却仍然被拒，才是真正的异常，需要单独说明
        if (elevated)
            problems.Add("已具备管理员权限，但磁盘/分区查询仍被拒绝：可能是 WMI 服务（Winmgmt）异常或安全软件拦截，请检查后重试。");
        else if (problems.Count == 0 || problems[^1] != ElevationRequiredMessage)
            problems.Add(ElevationRequiredMessage);
    }


    /// <summary>Windows 目录所在卷的盘符（如 'C'）；判定失败返回 null。</summary>
    public static char? SystemDriveLetter()
    {
        try
        {
            string root = Path.GetPathRoot(Environment.SystemDirectory) ?? string.Empty;
            if (root.Length >= 1 && char.IsLetter(root[0]))
                return char.ToUpperInvariant(root[0]);
        }
        catch (Exception) { /* 忽略 */ }
        return null;
    }

    /// <summary>
    /// 实测某个卷根目录下 pagefile.sys + hiberfil.sys + swapfile.sys 的大小之和（字节）。
    /// 走文件系统读取（不依赖 WMI、不需要管理员），口径与本工具「占用分析」窗口的“根目录直属文件”一致；
    /// 这三个文件默认不能移出所在卷，因此分盘推荐值必须把它们计入“必须保留”。
    /// </summary>
    public static long MeasurePagingFiles(char driveLetter)
    {
        if (ParseLetter(driveLetter.ToString()) is not { } letter) return 0;

        long total = 0;
        foreach (string name in new[] { "pagefile.sys", "hiberfil.sys", "swapfile.sys" })
        {
            try
            {
                var fi = new FileInfo(Path.Combine($"{letter}:\\", name));
                if (fi.Exists) total += fi.Length;
            }
            catch (Exception) { /* 单个文件读取失败忽略，不中断 */ }
        }
        return total;
    }

    // ---------- 1) 磁盘 / 分区 / 卷 ----------

    private sealed record StorageQueryResult(
        List<DiskInfo> Disks, List<PartitionInfo> Partitions, List<VolumeInfo> Volumes, HashSet<char> Letters,
        string RawJson);

    private static StorageQueryResult QueryStorage(List<string> problems, Action<string>? log, CancellationToken ct,
        bool elevated)
    {
        string script = PowerShellRunner.Prelude + """
            $ErrorActionPreference='SilentlyContinue'
            $problems=New-Object System.Collections.ArrayList

            $disks=@()
            try {
              $disks=@(Get-Disk -ErrorAction Stop | Select-Object Number,FriendlyName,
                @{n='Size';e={[uint64]$_.Size}},
                @{n='PartitionStyle';e={[string]$_.PartitionStyle}},
                @{n='IsBoot';e={[bool]$_.IsBoot}},
                @{n='IsSystem';e={[bool]$_.IsSystem}},
                @{n='BusType';e={[string]$_.BusType}},
                @{n='OperationalStatus';e={[string]$_.OperationalStatus}},
                @{n='IsOffline';e={[bool]$_.IsOffline}},
                @{n='IsReadOnly';e={[bool]$_.IsReadOnly}},
                @{n='HealthStatus';e={[string]$_.HealthStatus}})
            } catch { [void]$problems.Add("枚举磁盘失败：" + $_.Exception.Message) }

            $parts=@()
            try {
              $parts=@(Get-Partition -ErrorAction Stop | Select-Object DiskNumber,PartitionNumber,
                @{n='DriveLetter';e={ if($_.DriveLetter){[string]$_.DriveLetter}else{''} }},
                @{n='Offset';e={[uint64]$_.Offset}},
                @{n='Size';e={[uint64]$_.Size}},
                @{n='TypeName';e={[string]$_.Type}},
                @{n='GptType';e={[string]$_.GptType}},
                @{n='Guid';e={[string]$_.Guid}},
                @{n='IsBoot';e={[bool]$_.IsBoot}},
                @{n='IsSystem';e={[bool]$_.IsSystem}},
                @{n='IsHidden';e={[bool]$_.IsHidden}},
                @{n='IsActive';e={[bool]$_.IsActive}},
                @{n='IsShadowCopy';e={[bool]$_.IsShadowCopy}},
                @{n='NoDefaultDriveLetter';e={[bool]$_.NoDefaultDriveLetter}},
                @{n='MbrType';e={[int]$_.MbrType}})
            } catch { [void]$problems.Add("枚举分区失败：" + $_.Exception.Message) }

            $vols=@()
            try {
              $vols=@(Get-Volume -ErrorAction Stop | Select-Object
                @{n='DriveLetter';e={ if($_.DriveLetter){[string]$_.DriveLetter}else{''} }},
                @{n='FileSystemLabel';e={[string]$_.FileSystemLabel}},
                @{n='FileSystem';e={[string]$_.FileSystem}},
                @{n='FileSystemType';e={[string]$_.FileSystemType}},
                @{n='Size';e={[uint64]$_.Size}},
                @{n='SizeRemaining';e={[uint64]$_.SizeRemaining}},
                @{n='DriveType';e={[string]$_.DriveType}},
                @{n='HealthStatus';e={[string]$_.HealthStatus}})
            } catch { [void]$problems.Add("枚举卷失败：" + $_.Exception.Message) }

            $letters=@()
            try {
              $letters=@(Get-PSDrive -PSProvider FileSystem -ErrorAction Stop |
                Where-Object { $_.Name -match '^[A-Za-z]$' } | ForEach-Object { $_.Name.ToUpper() })
            } catch { }

            [pscustomobject]@{ Ok=$true; Problems=@($problems); Disks=@($disks);
              Partitions=@($parts); Volumes=@($vols); Letters=@($letters) } |
              ConvertTo-Json -Depth 5 -Compress
            """;

        var (code, output, err) = PowerShellRunner.Run(script, ProbeTimeoutMs, killOnTimeout: true, ct);
        if (code != 0)
        {
            if (IsAccessDenied(err)) NoteAccessDenied(problems, elevated);
            else problems.Add($"磁盘/分区枚举失败（PowerShell 退出码 {code}）：" +
                              $"{Truncate(PowerShellRunner.UnwrapStderr(err), 200)}");
            log?.Invoke("磁盘/分区枚举失败，请确认 Storage 模块可用（Windows 10/11 默认具备）。");
            return new StorageQueryResult(new(), new(), new(), new(), string.Empty);
        }

        var roots = PowerShellRunner.ParseJsonAsList(output);
        if (roots.Count == 0)
        {
            problems.Add("磁盘/分区枚举未返回可解析的数据（PowerShell 输出为空或非 JSON）。");
            return new StorageQueryResult(new(), new(), new(), new(), string.Empty);
        }

        var root = roots[0];
        foreach (var p in root.ArrayOf("Problems"))
            if (p.ValueKind == JsonValueKind.String && p.GetString() is { Length: > 0 } s && !IsAccessDenied(s))
                problems.Add(s);

        var disks = new List<DiskInfo>();
        foreach (var e in root.ArrayOf("Disks"))
        {
            string hs = e.GetString("HealthStatus");
            disks.Add(new DiskInfo
            {
                Number = e.GetInt32("Number"),
                FriendlyName = e.GetString("FriendlyName"),
                SizeBytes = e.GetUInt64("Size"),
                Style = ParsePartitionStyle(e.GetString("PartitionStyle")),
                IsBoot = e.GetBool("IsBoot"),
                IsSystem = e.GetBool("IsSystem"),
                BusType = e.GetString("BusType"),
                OperationalStatus = e.GetString("OperationalStatus"),
                IsOffline = e.GetBool("IsOffline"),
                IsReadOnly = e.GetBool("IsReadOnly"),
                // HealthStatus 是 ScriptProperty 返回的英文字符串（不是数字）
                Health = ParseHealth(hs),
                HealthRaw = hs,
            });
        }

        var partitions = new List<PartitionInfo>();
        foreach (var e in root.ArrayOf("Partitions"))
            partitions.Add(ParsePartition(e));

        // 动态磁盘标记：该磁盘上存在 LDM 分区
        var ldmDisks = partitions
            .Where(p => p.Kind is PartitionKind.LdmMetadata or PartitionKind.LdmData)
            .Select(p => p.DiskNumber).ToHashSet();
        if (ldmDisks.Count > 0)
        {
            for (int i = 0; i < disks.Count; i++)
            {
                var d = disks[i];
                if (!ldmDisks.Contains(d.Number)) continue;
                disks[i] = new DiskInfo
                {
                    Number = d.Number, FriendlyName = d.FriendlyName, SizeBytes = d.SizeBytes, Style = d.Style,
                    IsBoot = d.IsBoot, IsSystem = d.IsSystem, BusType = d.BusType,
                    OperationalStatus = d.OperationalStatus, IsOffline = d.IsOffline, IsReadOnly = d.IsReadOnly,
                    Health = d.Health, HealthRaw = d.HealthRaw, HasLdmPartitions = true,
                };
            }
        }

        var volumes = new List<VolumeInfo>();
        foreach (var e in root.ArrayOf("Volumes"))
        {
            string fs = e.GetString("FileSystem");
            if (fs.Length == 0) fs = e.GetString("FileSystemType");
            char? letter = ParseLetter(e.GetString("DriveLetter"));
            string hs = e.GetString("HealthStatus");

            // 同一个盘符偶有重复条目（卷与分区并非一一对应），保留信息更完整的那一条
            var existing = letter is { } l
                ? volumes.FirstOrDefault(v => v.DriveLetter == l)
                : null;
            var info = new VolumeInfo
            {
                DriveLetter = letter,
                Label = e.GetString("FileSystemLabel"),
                FileSystem = fs,
                SizeBytes = e.GetUInt64("Size"),
                FreeBytes = e.GetUInt64("SizeRemaining"),
                DriveType = e.GetString("DriveType"),
                Health = ParseHealth(hs),
                HealthRaw = hs,
            };

            if (existing is null) volumes.Add(info);
            else if (existing.FileSystem.Length == 0 && info.FileSystem.Length > 0)
                volumes[volumes.IndexOf(existing)] = info;
        }

        // 已占用盘符 = PowerShell 报告 ∪ 卷盘符 ∪ 本地 DriveInfo（含网络盘与 subst）
        var letters = new HashSet<char>();
        foreach (var e in root.ArrayOf("Letters"))
            if (ParseLetter(e.GetString()) is { } l) letters.Add(l);
        foreach (var v in volumes)
            if (v.DriveLetter is { } vl) letters.Add(vl);
        try
        {
            foreach (var di in DriveInfo.GetDrives())
                if (di.Name.Length >= 1 && char.IsLetter(di.Name[0]))
                    letters.Add(char.ToUpperInvariant(di.Name[0]));
        }
        catch (Exception) { /* 忽略 */ }

        // 三个列表全空 = 查询实际没成功（最典型原因就是未提权）；合并成一条可行动提示
        if (disks.Count == 0 && partitions.Count == 0 && volumes.Count == 0)
            NoteAccessDenied(problems, elevated);

        return new StorageQueryResult(disks, partitions, volumes, letters, output ?? string.Empty);
    }

    /// <summary>是否为“拒绝访问”类本地化错误（各语言均含“拒绝访问”/“Access is denied”）。</summary>
    private static bool IsAccessDenied(string message)
    {
        string m = message ?? string.Empty;
        return m.Contains("拒绝访问", StringComparison.OrdinalIgnoreCase) ||
               m.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) ||
               m.Contains("Zugriff verweigert", StringComparison.OrdinalIgnoreCase) ||
               m.Contains("Accès refusé", StringComparison.OrdinalIgnoreCase);
    }

    // ---------- 2) 单个分区的轻量查询（用于执行前后逐步复验） ----------

    /// <summary>
    /// 只读查询某一个分区当前的真实状态（比全量探测快得多，用于破坏性步骤之间逐步复验）。
    /// 分区不存在时返回 null。
    /// </summary>
    public static PartitionInfo? GetPartition(int diskNumber, int partitionNumber, CancellationToken ct,
        Action<string>? log = null)
    {
        string script = PowerShellRunner.Prelude + """
            $ErrorActionPreference='Stop'
            try {
              $p = Get-Partition -__LOC__ -ErrorAction Stop
              if (-not $p) { throw '未找到该分区' }
              $sel = $p | Select-Object DiskNumber,PartitionNumber,
                @{n='DriveLetter';e={ if($_.DriveLetter){[string]$_.DriveLetter}else{''} }},
                @{n='Offset';e={[uint64]$_.Offset}},
                @{n='Size';e={[uint64]$_.Size}},
                @{n='TypeName';e={[string]$_.Type}},
                @{n='GptType';e={[string]$_.GptType}},
                @{n='Guid';e={[string]$_.Guid}},
                @{n='IsBoot';e={[bool]$_.IsBoot}},
                @{n='IsSystem';e={[bool]$_.IsSystem}},
                @{n='IsHidden';e={[bool]$_.IsHidden}},
                @{n='IsActive';e={[bool]$_.IsActive}},
                @{n='IsShadowCopy';e={[bool]$_.IsShadowCopy}},
                @{n='NoDefaultDriveLetter';e={[bool]$_.NoDefaultDriveLetter}},
                @{n='MbrType';e={[int]$_.MbrType}}
              [pscustomobject]@{ Ok=$true; Partition=$sel } | ConvertTo-Json -Depth 4 -Compress
            } catch {
              [pscustomobject]@{ Ok=$false; Error=$_.Exception.Message } | ConvertTo-Json -Compress
            }
            """
            .Replace("__LOC__", $"DiskNumber {diskNumber} -PartitionNumber {partitionNumber}");

        var (code, output, err) = PowerShellRunner.Run(script, ProbeTimeoutMs, killOnTimeout: true, ct);
        if (code != 0)
        {
            log?.Invoke($"查询磁盘 {diskNumber} 分区 {partitionNumber} 失败：" +
                        $"{Truncate(PowerShellRunner.UnwrapStderr(err), 160)}");
            return null;
        }

        var roots = PowerShellRunner.ParseJsonAsList(output);
        if (roots.Count == 0) return null;

        var root = roots[0];
        if (!root.GetBool("Ok"))
        {
            log?.Invoke($"查询磁盘 {diskNumber} 分区 {partitionNumber} 未成功：{root.GetString("Error")}");
            return null;
        }

        var arr = root.ArrayOf("Partition");
        return arr.Count == 0 ? null : ParsePartition(arr[0]);
    }

    /// <summary>
    /// 只读枚举某磁盘上的**全部**分区（按起始偏移排序）。
    /// 用于执行前复验"计划要用的未分配空间现在是否仍然空着"——不需要跑一次完整探测。
    /// </summary>
    public static IReadOnlyList<PartitionInfo> GetPartitionsOfDisk(int diskNumber, CancellationToken ct,
        Action<string>? log = null)
    {
        string script = PowerShellRunner.Prelude + """
            $ErrorActionPreference='Stop'
            try {
              $items = @(Get-Partition -DiskNumber __DISK__ -ErrorAction Stop | Sort-Object Offset |
                Select-Object DiskNumber,PartitionNumber,
                @{n='DriveLetter';e={ if($_.DriveLetter){[string]$_.DriveLetter}else{''} }},
                @{n='Offset';e={[uint64]$_.Offset}},
                @{n='Size';e={[uint64]$_.Size}},
                @{n='TypeName';e={[string]$_.Type}},
                @{n='GptType';e={[string]$_.GptType}},
                @{n='Guid';e={[string]$_.Guid}},
                @{n='IsBoot';e={[bool]$_.IsBoot}},
                @{n='IsSystem';e={[bool]$_.IsSystem}},
                @{n='IsHidden';e={[bool]$_.IsHidden}},
                @{n='IsActive';e={[bool]$_.IsActive}},
                @{n='IsShadowCopy';e={[bool]$_.IsShadowCopy}},
                @{n='NoDefaultDriveLetter';e={[bool]$_.NoDefaultDriveLetter}},
                @{n='MbrType';e={[int]$_.MbrType}})
              [pscustomobject]@{ Ok=$true; Partitions=$items } | ConvertTo-Json -Depth 4 -Compress
            } catch {
              [pscustomobject]@{ Ok=$false; Error=$_.Exception.Message } | ConvertTo-Json -Compress
            }
            """
            .Replace("__DISK__", diskNumber.ToString());

        var (code, output, err) = PowerShellRunner.Run(script, ProbeTimeoutMs, killOnTimeout: true, ct);
        if (code != 0)
        {
            log?.Invoke($"枚举磁盘 {diskNumber} 的分区失败：" + Truncate(PowerShellRunner.UnwrapStderr(err), 160));
            return Array.Empty<PartitionInfo>();
        }

        var roots = PowerShellRunner.ParseJsonAsList(output);
        if (roots.Count == 0) return Array.Empty<PartitionInfo>();

        var root = roots[0];
        if (!root.GetBool("Ok"))
        {
            log?.Invoke($"枚举磁盘 {diskNumber} 的分区未成功：{root.GetString("Error")}");
            return Array.Empty<PartitionInfo>();
        }

        return root.ArrayOf("Partitions").Select(ParsePartition).OrderBy(p => p.OffsetBytes).ToArray();
    }

    // ---------- 3) 环境事实 ----------
    private static DiskEnvironmentFacts QueryEnvironment(List<string> problems, Action<string>? log, CancellationToken ct,
        bool elevated)
    {
        string script = PowerShellRunner.Prelude + """
            $ErrorActionPreference='SilentlyContinue'
            $problems=New-Object System.Collections.ArrayList

            # BitLocker：用 WMI 类（非本地化，家庭版也可用）
            $bl=@()
            try {
              $bl=@(Get-CimInstance -Namespace 'root\cimv2\security\microsoftvolumeencryption' `
                -ClassName Win32_EncryptableVolume -ErrorAction Stop | Select-Object
                @{n='DriveLetter';e={[string]$_.DriveLetter}},
                @{n='ProtectionStatus';e={[int]$_.ProtectionStatus}},
                @{n='ConversionStatus';e={[int]$_.ConversionStatus}})
            } catch { [void]$problems.Add("BitLocker 状态查询失败：" + $_.Exception.Message) }

            # 页面文件位置
            $pf=@()
            try {
              $pf=@(Get-CimInstance -ClassName Win32_PageFileSetting -ErrorAction Stop |
                Where-Object { $_.Name } | ForEach-Object { [string]$_.Name })
            } catch { [void]$problems.Add("页面文件查询失败：" + $_.Exception.Message) }

            # 卷影副本使用量（Win32_ShadowStorage，需管理员）；无法查询时返回 -1
            $vssUsed=-1
            try {
              $st=@(Get-CimInstance -ClassName Win32_ShadowStorage -ErrorAction Stop)
              $vssUsed=[long]0
              foreach($s in $st){ $vssUsed += [long]$s.UsedSpace }
            } catch { }

            [pscustomobject]@{ Ok=$true; Problems=@($problems); BitLocker=@($bl);
              PageFiles=@($pf); VssUsed=$vssUsed } | ConvertTo-Json -Depth 5 -Compress
            """;

        var pageFiles = new List<string>();
        var bl = new Dictionary<char, BitLockerState>();
        long vssUsed = 0;
        bool vssKnown = false;

        // ---- 注册表与文件系统部分（不依赖 PowerShell） ----
        bool hibernate = false;
        try
        {
            using var power = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power");
            if (power?.GetValue("HibernateEnabled") is int h) hibernate = h != 0;
        }
        catch (Exception) { /* 忽略 */ }

        bool restoreEnabled = true;
        int restorePercent = 0;
        try
        {
            using var sr = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore");
            if (sr is null)
            {
                restoreEnabled = false;   // 键不存在通常意味着从未配置系统保护
            }
            else
            {
                if (sr.GetValue("DisableSR") is int ds && ds != 0) restoreEnabled = false;
                if (sr.GetValue("DiskPercent") is int dp) restorePercent = dp;
            }
        }
        catch (Exception) { /* 忽略 */ }

        long hiberBytes = 0;
        try
        {
            foreach (var di in DriveInfo.GetDrives())
            {
                try
                {
                    if (!di.IsReady) continue;
                    string hiber = Path.Combine(di.RootDirectory.FullName, "hiberfil.sys");
                    if (File.Exists(hiber)) hiberBytes += new FileInfo(hiber).Length;
                }
                catch (Exception) { /* 单个卷失败忽略 */ }
            }
        }
        catch (Exception) { /* 忽略 */ }

        // ---- PowerShell 部分 ----
        var (code, output, err) = PowerShellRunner.Run(script, EnvTimeoutMs, killOnTimeout: true, ct);
        if (code == 0)
        {
            var roots = PowerShellRunner.ParseJsonAsList(output);
            if (roots.Count > 0)
            {
                var root = roots[0];
                foreach (var p in root.ArrayOf("Problems"))
                    if (p.ValueKind == JsonValueKind.String && p.GetString() is { Length: > 0 } s && !IsAccessDenied(s))
                        problems.Add(s);

                foreach (var e in root.ArrayOf("BitLocker"))
                {
                    if (ParseLetter(e.GetString("DriveLetter")) is not { } letter) continue;
                    int protection = e.GetInt32("ProtectionStatus");
                    int conversion = e.GetInt32("ConversionStatus");
                    bl[letter] = new BitLockerState
                    {
                        DriveLetter = letter,
                        ConversionStatus = conversion,
                        // 0=保护关闭，1=开启，2=未知；保护关闭 + 全加密（1）= 已挂起
                        Protection = protection switch
                        {
                            1 => BitLockerProtection.On,
                            0 => conversion == 1 ? BitLockerProtection.Suspended : BitLockerProtection.Off,
                            _ => BitLockerProtection.Unknown,
                        },
                    };
                }

                foreach (var e in root.ArrayOf("PageFiles"))
                    if (e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } s)
                        pageFiles.Add(s);

                long v = root.GetInt64("VssUsed");
                if (v >= 0) { vssUsed = v; vssKnown = true; }
            }
        }
        else
        {
            if (IsAccessDenied(err)) NoteAccessDenied(problems, elevated);
            else problems.Add($"环境事实查询失败（PowerShell 退出码 {code}）：" +
                              $"{Truncate(PowerShellRunner.UnwrapStderr(err), 200)}");
        }

        log?.Invoke($"环境事实：BitLocker {bl.Count} 个卷；页面文件 {pageFiles.Count} 处；" +
                    $"休眠{(hibernate ? "已启用" : "未启用")}（hiberfil.sys {SizeFormatter.Format(hiberBytes)}）；" +
                    $"卷影副本 {(vssKnown ? SizeFormatter.Format(vssUsed) : "无法判定")}；" +
                    $"系统保护 {(restoreEnabled ? $"已启用（上限 {restorePercent}%）" : "未启用")}");

        return new DiskEnvironmentFacts
        {
            BitLocker = bl,
            PageFilePaths = pageFiles,
            HibernateEnabled = hibernate,
            HiberFileBytes = hiberBytes,
            VssUsedBytes = vssUsed,
            VssKnown = vssKnown,
            SystemRestoreEnabled = restoreEnabled,
            SystemRestoreDiskPercent = restorePercent,
        };
    }

    // ---------- 3) 可收缩 / 可扩展范围（按需，较慢） ----------

    /// <summary>
    /// 查询某个分区可收缩到的最小尺寸与可扩展到的最大尺寸（MSFT_Partition.GetSupportedSize）。
    /// **较慢**：该方法会启动磁盘碎片整理程序（defragsvc）分析不可移动文件位置，大容量盘可能需要数分钟。
    /// 脚本内会先复验分区偏移与大小，与探测结果不符即视为“分区已变化”并拒绝继续。
    /// </summary>
    public static Task<SupportedSize> QuerySupportedSizeAsync(PartitionInfo partition, CancellationToken ct,
        Action<string>? log = null)
        => Task.Run(() => QuerySupportedSize(partition, ct, log), ct);

    public static SupportedSize QuerySupportedSize(PartitionInfo partition, CancellationToken ct,
        Action<string>? log = null)
    {
        // 用「磁盘号 + 分区号」定位（分区号在本次会话内稳定），并在脚本内复验偏移与大小。
        // 关键：整体包 try/catch，失败也输出结构化 JSON——否则 stdout 为空，
        // 调用方只能拿到 stderr 的 CLIXML，用户看到「#< CLIXML」这种无意义信息。
        string script = PowerShellRunner.Prelude + """
            $ErrorActionPreference='Stop'
            try {
              __LOCATE__
              __VERIFY__
              $r = Invoke-CimMethod -InputObject $p -MethodName GetSupportedSize -ErrorAction Stop
              [pscustomobject]@{ Ok=$true; ReturnValue=[int]$r.ReturnValue; SizeMin=[uint64]$r.SizeMin;
                SizeMax=[uint64]$r.SizeMax; Extended=[string]$r.ExtendedStatus } | ConvertTo-Json -Compress
            } catch {
              [pscustomobject]@{ Ok=$false; ReturnValue=-1; SizeMin=[uint64]0; SizeMax=[uint64]0;
                Extended=$_.Exception.Message } | ConvertTo-Json -Compress
            }
            """;

        script = script
            .Replace("__LOCATE__",
                $"$p = Get-Partition -DiskNumber {partition.DiskNumber} -PartitionNumber {partition.PartitionNumber} -ErrorAction Stop; " +
                "if (-not $p) { throw '未找到该分区（可能已被删除或编号已变化）' }")
            .Replace("__VERIFY__",
                $"if ([uint64]$p.Offset -ne [uint64]{partition.OffsetBytes}) {{ throw '分区起始偏移已变化，已中止分析' }}; " +
                $"if ([uint64]$p.Size -ne [uint64]{partition.SizeBytes}) {{ throw '分区大小已变化（可能是其它程序调整过），已中止分析' }}");

        log?.Invoke($"正在分析 {partition.DisplayName} 的可收缩范围（可能需数分钟：该方法会启动磁盘碎片整理程序分析不可移动文件位置）...");
        var (code, output, err) = PowerShellRunner.Run(script, SupportedSizeTimeoutMs, killOnTimeout: true, ct);

        // 先看 stdout 的 JSON：脚本自身用 try/catch 把结果写进 Json，**即使退出码非 0 也优先用它**。
        // 否则只能拿到 stderr 的 CLIXML 包装，用户看到的就是「#< CLIXML」这种无意义信息（真机上踩过）。
        var roots = PowerShellRunner.ParseJsonAsList(output);
        if (roots.Count == 0)
        {
            string detail = code != 0 ? PowerShellRunner.UnwrapStderr(err) : "未返回可解析结果";
            if (string.IsNullOrWhiteSpace(detail)) detail = $"PowerShell 退出码 {code}";
            detail = ExplainExceptionMessage(detail);
            log?.Invoke("分析失败：" + detail);
            return new SupportedSize { ReturnValue = -1, ExtendedStatus = detail };
        }

        var e = roots[0];
        var result = new SupportedSize
        {
            ReturnValue = e.GetInt32("ReturnValue"),
            MinBytes = e.GetUInt64("SizeMin"),
            MaxBytes = e.GetUInt64("SizeMax"),
            ExtendedStatus = e.GetString("Extended"),
        };

        if (result.Ok)
        {
            log?.Invoke($"  当前 {SizeFormatter.Format((long)partition.SizeBytes)}；" +
                        $"最小可收缩到 {SizeFormatter.Format((long)result.MinBytes)}" +
                        $"（最多分出 {SizeFormatter.Format((long)result.MaxShrinkableBytes(partition.SizeBytes))}）；" +
                        $"最大可扩展到 {SizeFormatter.Format((long)result.MaxBytes)}" +
                        $"（紧邻空闲 {SizeFormatter.Format((long)result.AdjacentFreeBytes(partition.SizeBytes))}）");
        }
        else
        {
            log?.Invoke($"  分析未成功（返回码 {result.ReturnValue}）：{ExplainExceptionMessage(result.ExtendedStatus)}");
        }
        return result;
    }

    /// <summary>
    /// 把系统返回的原始异常文本映射成**可行动的中文说明**。
    ///
    /// 典型例子：非管理员调用 <c>GetSupportedSize</c> 得到的是
    /// <c>Access to a CIM resource was not available to the client.</c>——
    /// 直接显示这句英文对用户毫无帮助，必须翻译成"请以管理员身份运行"。
    /// </summary>
    public static string ExplainExceptionMessage(string? raw)
    {
        string m = (raw ?? string.Empty).Trim();
        if (m.Length == 0) return "调用失败（系统未返回错误信息）。";

        if (m.Contains("CIM resource", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("拒绝访问", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("Access denied", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("not available to the client", StringComparison.OrdinalIgnoreCase))
            return "需要管理员权限：读取可收缩/可扩展范围要调用系统存储接口，请以管理员身份运行本程序后重试。";

        if (m.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("未找到", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("No MSFT_Partition", StringComparison.OrdinalIgnoreCase))
            return "找不到该分区（可能已被删除或编号已变化），请点「重新检测」后重试。";

        if (m.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("超时", StringComparison.OrdinalIgnoreCase))
            return "分析超时：磁盘碎片较多时该分析可能很久，可先整理碎片或稍后重试。";

        return m;
    }

    /// <summary>把 GetSupportedSize / Resize 的返回码翻译成可行动的中文说明。</summary>
    public static string ExplainReturnCode(int code) => code switch
    {
        0 => "成功。",
        1 => "该操作不被支持（可能是分区类型或文件系统不支持调整）。",
        2 => "未指明的错误。",
        3 => "操作超时。",
        4 => "操作失败。",
        5 => "参数无效。",
        4097 => "目标尺寸不被支持：可能小于可收缩下限，或大于紧邻的连续空闲空间。",
        40001 => "拒绝访问：请以管理员身份运行。",
        40002 => "资源不足，无法完成操作。",
        42008 => "卷存在错误，无法收缩：请先运行 chkdsk（如 chkdsk C: /scan）修复后再试。",
        42009 => "未知的文件系统：本操作仅支持 NTFS。",
        -1 => "调用失败（未能执行到系统接口）。",
        _ => $"未识别的返回码 {code}。",
    };

    // ---------- 分类与解析 ----------

    /// <summary>
    /// 解析一个 MSFT_Partition 的 JSON 对象（<c>Get-Partition | Select-Object ...</c> 的输出形状）。
    /// 全量探测与单分区复验共用同一套解析，保证两处判定永远一致。
    /// </summary>
    public static PartitionInfo ParsePartition(JsonElement e)
    {
        string gptType = e.GetString("GptType");
        int mbrType = e.GetInt32("MbrType");
        string typeName = e.GetString("TypeName");
        bool shadow = e.GetBool("IsShadowCopy");

        // 注意：判据顺序是「Type 字符串 → 归一化 GUID → MBR」，见 Classify 的文档
        var kind = Classify(typeName, gptType, mbrType, shadow, out string reason);

        return new PartitionInfo
        {
            DiskNumber = e.GetInt32("DiskNumber"),
            PartitionNumber = e.GetInt32("PartitionNumber"),
            DriveLetter = ParseLetter(e.GetString("DriveLetter")),
            OffsetBytes = e.GetUInt64("Offset"),
            SizeBytes = e.GetUInt64("Size"),
            TypeText = typeName,
            GptType = gptType,
            Guid = e.GetString("Guid"),
            IsBoot = e.GetBool("IsBoot"),
            IsSystem = e.GetBool("IsSystem"),
            IsHidden = e.GetBool("IsHidden"),
            IsActive = e.GetBool("IsActive"),
            NoDefaultDriveLetter = e.GetBool("NoDefaultDriveLetter"),
            MbrType = mbrType,
            Kind = kind,
            ClassificationReason = reason,
        };
    }

    /// <summary>
    /// 分区类别判定（白名单的真源）。
    ///
    /// **判据优先级**（本功能最关键的一处正确性来源）：
    /// ① <paramref name="typeName"/> —— <c>MSFT_Partition.Type</c> 脚本属性的字符串。它由 Windows 自带的
    ///    <c>Storage.types.ps1xml</c> 定义，是**权威映射**：不本地化，且天然处理了 GUID 的书写形式，
    ///    未来新增类型也能自动覆盖。因此以它为主判据。
    /// ② 归一化后的 GPT 类型 GUID。⚠ 实测 <c>MSFT_Partition.GptType</c> 返回**带大括号**的形式
    ///    （Windows 自己的比较语句就是 <c>"{ebd0a0a2-…}" { "Basic" }</c>）；归一化后，
    ///    带括号 / 不带括号 / 无连字符 / 大小写混写都能识别。早先直接比较不带括号的常量，
    ///    导致**所有 GPT 分区都落成 Other**（可操作分区 0 个、下拉框为空）。
    /// ③ MBR 类型（<c>MbrType</c>）表值。
    ///
    /// 判定为不可操作时会通过 <paramref name="reason"/> 给出**中文原因**，供「磁盘明细」弹窗展示，
    /// 避免出现"下拉框空着却不知道为什么"的死胡同。
    /// </summary>
    public static PartitionKind Classify(string? typeName, string? gptType, int mbrType, bool isShadowCopy,
        out string reason)
    {
        if (isShadowCopy)
        {
            reason = "卷影副本分区（VSS 使用），不参与分区调整。";
            return PartitionKind.ShadowCopy;
        }

        // ① 主判据：Windows 自己给出的 Type 字符串
        string t = (typeName ?? string.Empty).Trim();
        switch (t.ToUpperInvariant())
        {
            case "BASIC":
                reason = "GPT 基本数据分区。";
                return PartitionKind.BasicData;
            case "IFS":
                reason = "MBR 数据分区（类型 0x07 IFS），可参与分区调整。";
                return PartitionKind.BasicData;
            case "FAT12":
            case "FAT16":
            case "FAT32":
            case "FAT32 XINT13":
            case "XINT13":
            case "LOGICAL":
                // 是数据分区但通常不是 NTFS：交给"文件系统不受支持"那条给出更可行动的提示
                reason = $"数据分区，但类型为 {t}（通常不是 NTFS）。";
                return PartitionKind.BasicData;
            case "SYSTEM":
                reason = "EFI 系统分区：存放引导文件，删除后将无法开机。";
                return PartitionKind.EfiSystem;
            case "RESERVED":
                reason = "MSR 保留分区：系统保留区域，不可删除。";
                return PartitionKind.MicrosoftReserved;
            case "RECOVERY":
                reason = "恢复分区（WinRE）：删除后无法进入恢复环境，且通常挡住 C 盘扩容。";
                return PartitionKind.Recovery;
            case "LDM METADATA":
                reason = "动态磁盘元数据分区（LDM）——本工具不操作动态磁盘。";
                return PartitionKind.LdmMetadata;
            case "LDM DATA":
                reason = "动态磁盘数据分区（LDM）——本工具不操作动态磁盘。";
                return PartitionKind.LdmData;
            case "SPACE PROTECTIVE":
                reason = "存储池保护分区（Storage Spaces），不属可调整范围。";
                return PartitionKind.StorageSpaceProtective;
            case "EXTENDED":
                reason = "MBR 扩展分区：只是逻辑驱动器的容器，不能直接调整。";
                return PartitionKind.Other;
        }

        // ② 次判据：归一化 GPT 类型 GUID
        string g = NormalizeGuid(gptType);
        if (g.Length > 0)
        {
            switch (g)
            {
                case GuidBasicData:
                    reason = string.IsNullOrWhiteSpace(t)
                        ? "GPT 基本数据分区（按类型 GUID 识别）。"
                        : $"GPT 基本数据分区（Type 原文 {t}，按类型 GUID 识别）。";
                    return PartitionKind.BasicData;
                case GuidEfiSystem:
                    reason = "EFI 系统分区（按类型 GUID 识别）：删除后将无法开机。";
                    return PartitionKind.EfiSystem;
                case GuidMsReserved:
                    reason = "MSR 保留分区（按类型 GUID 识别）。";
                    return PartitionKind.MicrosoftReserved;
                case GuidRecovery:
                    reason = "恢复分区（WinRE，按类型 GUID 识别）。";
                    return PartitionKind.Recovery;
                case GuidLdmMetadata:
                    reason = "动态磁盘元数据分区（按类型 GUID 识别）。";
                    return PartitionKind.LdmMetadata;
                case GuidLdmData:
                    reason = "动态磁盘数据分区（按类型 GUID 识别）。";
                    return PartitionKind.LdmData;
                case GuidSpaceProtective:
                    reason = "存储池保护分区（Storage Spaces，按类型 GUID 识别）。";
                    return PartitionKind.StorageSpaceProtective;
                default:
                    reason = $"未知的 GPT 分区类型 {gptType}" +
                             $"（Type 原文 {(t.Length == 0 ? "为空" : t)}）——为安全起见不做任何操作。";
                    return PartitionKind.Other;
            }
        }

        // ③ MBR 回退（GPT 磁盘上 MbrType 为 0）
        switch (mbrType)
        {
            case 7:
                reason = "MBR 数据分区（类型 7 = IFS/NTFS）。";
                return PartitionKind.BasicData;
            case 1:
            case 4:
            case 6:
            case 11:
            case 12:
            case 14:
                reason = $"MBR 数据分区（类型 {mbrType}，通常不是 NTFS）。";
                return PartitionKind.BasicData;
            case 5:
            case 0x0F:
                reason = "MBR 扩展分区：只是逻辑驱动器的容器，不能直接调整。";
                return PartitionKind.Other;
            case 231:
                reason = "存储池保护分区（Storage Spaces）。";
                return PartitionKind.StorageSpaceProtective;
            case 0:
                reason = $"无法识别分区类型（Type 原文 {(t.Length == 0 ? "为空" : t)}，" +
                         "MbrType 0，GptType 为空）——为安全起见不做任何操作。";
                return PartitionKind.Other;
            default:
                reason = $"未知的 MBR 分区类型 {mbrType}——为安全起见不做任何操作。";
                return PartitionKind.Other;
        }
    }

    /// <summary>
    /// 兼容便捷重载：参数顺序与主签名一致，只是不返回判定理由。
    /// 注意参数顺序是 <c>(Type 原文, GptType, MbrType, IsShadowCopy)</c>——
    /// 主判据 Type 在最前，避免调用方误以为 GUID 是首要判据。
    /// </summary>
    public static PartitionKind Classify(string? typeName, string? gptType, int mbrType,
        bool isShadowCopy = false) =>
        Classify(typeName, gptType, mbrType, isShadowCopy, out _);

    // GPT 分区类型 GUID 的归一化形式（无大括号、无连字符、全大写）
    private const string GuidBasicData = "EBD0A0A2B9E5443387C068B6B72699C7";
    private const string GuidEfiSystem = "C12A7328F81F11D2BA4B00A0C93EC93B";
    private const string GuidMsReserved = "E3C9E3160B5C4DB8817DF92DF00215AE";
    private const string GuidRecovery = "DE94BBA406D14D40A16ABFD50179D6AC";
    private const string GuidLdmMetadata = "5808C8AA7E8F42E085D2E1E90434CFB3";
    private const string GuidLdmData = "AF9B60A014314F62BC683311714A69AD";
    private const string GuidSpaceProtective = "E75CAF8FF6804CEEAFA3B001E56EFC2D";

    /// <summary>
    /// 归一化 GUID：只保留十六进制字符并转大写。
    /// <c>{ebd0a0a2-…}</c>、<c>ebd0a0a2-…</c>、<c>EBD0A0A2B9E5…</c> 三种写法结果一致。
    /// </summary>
    public static string NormalizeGuid(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var sb = new StringBuilder(32);
        foreach (char c in raw)
            if (Uri.IsHexDigit(c)) sb.Append(char.ToUpperInvariant(c));
        return sb.ToString();
    }

    /// <summary>
    /// 解析健康状态。
    /// <c>MSFT_Disk/Volume.HealthStatus</c> 是 ScriptProperty，返回
    /// "Healthy"/"Warning"/"Unhealthy"/"Unknown" 的**字符串**；这里同时兼容数字形式（0/1/2）。
    /// </summary>
    public static HealthState ParseHealth(string? raw)
    {
        string s = (raw ?? string.Empty).Trim().ToUpperInvariant();
        return s switch
        {
            "HEALTHY" or "0" => HealthState.Healthy,
            "WARNING" or "1" => HealthState.Warning,
            "UNHEALTHY" or "2" => HealthState.Unhealthy,
            _ => HealthState.Unknown,
        };
    }

    private static PartitionStyle ParsePartitionStyle(string raw) => (raw ?? string.Empty).Trim().ToUpperInvariant() switch
    {
        "GPT" or "2" => PartitionStyle.Gpt,
        "MBR" or "1" => PartitionStyle.Mbr,
        "RAW" or "3" => PartitionStyle.Raw,
        _ => PartitionStyle.Unknown,
    };

    private static string HealthText(int code) => code switch
    {
        0 => "健康",
        1 => "警告",
        2 => "不健康",
        _ => "未知",
    };

    /// <summary>
    /// 建议一个未被占用的盘符：从 D: 起向后找第一个空闲字母（跳过 A:/B: 与系统盘）。
    /// 找不到时返回 null。
    /// </summary>
    public static char? SuggestFreeDriveLetter(IReadOnlySet<char> usedLetters, char? systemLetter = null)
    {
        for (char c = 'D'; c <= 'Z'; c++)
        {
            if (usedLetters.Contains(c)) continue;
            if (systemLetter is { } s && char.ToUpperInvariant(s) == c) continue;
            return c;
        }
        return null;
    }

    /// <summary>解析盘符：接受 "C" / "C:" / "c"；空、null 或无效返回 null。</summary>
    public static char? ParseLetter(string? raw)
    {
        string s = (raw ?? string.Empty).Trim();
        if (s.Length == 0) return null;
        char c = char.ToUpperInvariant(s[0]);
        return c is >= 'A' and <= 'Z' ? c : null;
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= max ? s : s[..max] + "...");
}
