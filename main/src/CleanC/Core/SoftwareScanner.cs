using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace CleanC.Core;

/// <summary>
/// 已安装软件扫描器：枚举注册表 Uninstall 项（HKLM 64/32 位视图 + HKCU）与 Microsoft Store/UWP 包
/// （PowerShell Get-AppxPackage），并按“控制面板 → 程序和功能”口径过滤系统组件与更新。
/// 零第三方依赖：注册表 API 由共享框架提供，PowerShell 为系统内置。
/// </summary>
public static partial class SoftwareScanner
{
    /// <summary>一次扫描的结果。</summary>
    public sealed class Result
    {
        public required IReadOnlyList<InstalledApp> Items { get; init; }
        public required IReadOnlyList<string> Errors { get; init; }
    }

    private const string UninstallSubPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const int RegistryHashCap = 20_000; // 单视图枚举上限保护

    public static Result Scan(CancellationToken ct)
    {
        var items = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();

        ScanRegistry(RegistryHive.LocalMachine, RegistryView.Registry64, AppSource.System64, items, ct);
        ScanRegistry(RegistryHive.LocalMachine, RegistryView.Registry32, AppSource.System32, items, ct);
        ScanRegistry(RegistryHive.CurrentUser, RegistryView.Default, AppSource.User, items, ct);

        // 商店/UWP：失败只记日志，不影响传统项
        try
        {
            ScanUwp(items, ct);
        }
        catch (OperationCanceledException)
        {
            throw;   // 取消不是"失败"，必须向上传播，否则会返回一份被截断的列表当成完整结果
        }
        catch (Exception ex)
        {
            errors.Add($"商店(UWP)应用枚举失败：{ex.Message}");
        }

        var list = items.Values
            .OrderBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.DisplayVersion, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new Result { Items = list, Errors = errors };
    }

    // ---------- 注册表枚举 ----------

    private static void ScanRegistry(RegistryHive hive, RegistryView view, AppSource source,
        Dictionary<string, InstalledApp> items, CancellationToken ct)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = baseKey.OpenSubKey(UninstallSubPath);
            if (uninstall == null) return;

            int count = 0;
            foreach (var keyName in uninstall.GetSubKeyNames())
            {
                if (++count > RegistryHashCap) break;
                ct.ThrowIfCancellationRequested();

                try
                {
                    // 读值也可能因单个损坏条目抛异常：必须放进 try 里，
                    // 否则一个坏条目会顺着外层 catch 把该视图**剩下的所有键**都丢掉（列表静默截断）。
                    using var key = uninstall.OpenSubKey(keyName);
                    if (key == null) continue;
                    if (!IsVisibleEntry(key)) continue;

                    var app = BuildApp(key, keyName, hive, view, source);
                    if (app != null)
                        items.TryAdd(app.Id, app);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // 单个条目读取异常不影响整体
                    System.Diagnostics.Debug.WriteLine($"跳过注册表条目 {keyName}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception) { /* 无法打开则视为无该视图结果 */ }
    }

    /// <summary>与“程序和功能”口径一致：隐藏系统组件、热修复/更新（ReleaseType、ParentKeyName）、无名称项。</summary>
    private static bool IsVisibleEntry(RegistryKey key)
    {
        if (string.IsNullOrWhiteSpace(GetString(key, "DisplayName"))) return false;
        if (GetDWord(key, "SystemComponent") != 0) return false;
        if (!string.IsNullOrEmpty(GetString(key, "ReleaseType"))) return false;
        if (!string.IsNullOrEmpty(GetString(key, "ParentKeyName"))) return false;
        return true;
    }

    private static InstalledApp? BuildApp(RegistryKey key, string keyName,
        RegistryHive hive, RegistryView view, AppSource source)
    {
        string displayName = GetString(key, "DisplayName") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(displayName)) return null;

        int sizeKb = GetDWord(key, "EstimatedSize");
        long estimatedBytes = sizeKb > 0 ? (long)sizeKb * 1024L : 0;

        DateTime? installDate = TryParseInstallDate(GetString(key, "InstallDate"));

        string? uninstallString = TrimQuotes(GetString(key, "UninstallString"));
        string? quiet = TrimQuotes(GetString(key, "QuietUninstallString"));
        // 注册表里的路径**常量**是带 %ProgramFiles% 这类变量写进去的（本类刻意用
        // DoNotExpandEnvironmentNames 读原始值），因此使用前必须自己展开，
        // 否则 Directory.Exists 一律为 false，安装位置会被静默丢弃。
        string? installLocation = ExpandEnv(TrimQuotes(GetString(key, "InstallLocation")));
        string? displayIcon = NullIfBlank(ExpandEnv(GetString(key, "DisplayIcon")?.Trim()));

        bool isMsi = GetDWord(key, "WindowsInstaller") != 0
                     || (keyName.StartsWith('{') && keyName.EndsWith('}') && (uninstallString?.Contains("msiexec", StringComparison.OrdinalIgnoreCase) ?? false));
        string? productCode = null;
        if (isMsi)
        {
            // 键名通常是 {GUID}；否则从卸载串中提取
            if (Guid.TryParse(keyName.Trim('{', '}'), out _))
                productCode = keyName;
            else
                productCode = ExtractGuid(uninstallString);
        }

        string keyPath = $@"{UninstallSubPath}\{keyName}";

        return new InstalledApp
        {
            // hive+view+键路径唯一标识；同一 GUID 在两视图并存时视为两条真实安装（罕见）
            Id = $"{hive}|{view}|{keyPath}",
            DisplayName = displayName,
            DisplayVersion = GetString(key, "DisplayVersion"),
            Publisher = GetString(key, "Publisher"),
            InstallDate = installDate,
            EstimatedBytes = estimatedBytes,
            InstallLocation = installLocation,
            DisplayIcon = displayIcon,
            UninstallString = uninstallString,
            QuietUninstallString = quiet,
            IsMsi = isMsi,
            ProductCode = productCode,
            Source = source,
            Hive = hive,
            View = view,
            KeyPath = keyPath,
        };
    }

    private static DateTime? TryParseInstallDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length != 8) return null;
        return DateTime.TryParseExact(raw, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var d) ? d : null;
    }

    private static string? ExtractGuid(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        var m = GuidRegex().Match(s);
        return m.Success ? m.Groups[1].Value : null;
    }

    // ---------- UWP 枚举 ----------

    private static void ScanUwp(Dictionary<string, InstalledApp> items, CancellationToken ct)
    {
        // -AllUsers 需要管理员权限：非提权时它会让整段脚本失败（表现为「商店应用一个都列不出来」）。
        // 因此只在已提权时才追加全机扫描，未提权时至少把当前用户的应用列全。
        bool elevated = ElevationHelper.IsElevated();
        string allUsersPart = elevated ? " + @(Map (Get-AppxPackage -AllUsers) $true)" : string.Empty;

        string script =
            PowerShellRunner.Prelude +
            "$ErrorActionPreference='SilentlyContinue';" +
            "$start = @{};" +
            "Get-StartApps | ForEach-Object { $start[[string]$_.AppID] = $_.Name };" +
            "function Map($apps, $allUsers) {" +
            "  $apps | Where-Object { $_.IsFramework -ne $true -and $_.IsResourcePackage -ne $true } | ForEach-Object {" +
            "    $pubId = ($_.PackageFullName -split '_')[-1];" +
            "    $appId = \"$($_.Name)_$pubId!App\";" +
            "    [PSCustomObject]@{ Name=$_.Name; Friendly=$start[[string]$appId]; PackageFullName=$_.PackageFullName;" +
            "      Version=$_.Version.ToString(); Publisher=$_.Publisher; InstallLocation=$_.InstallLocation; AllUsers=$allUsers }" +
            "  }" +
            "}" +
            $"$items = @(Map (Get-AppxPackage) $false){allUsersPart};" +
            "$items | Sort-Object PackageFullName -Unique | ConvertTo-Json -Compress";

        var (code, output, err) = RunPowerShell(script, timeoutMs: 120_000, killOnTimeout: true, ct);
        if (code != 0)
            throw new InvalidOperationException($"PowerShell 退出码 {code}：{Truncate(err, 300)}");

        string json = output.Trim();
        if (string.IsNullOrEmpty(json)) return;

        // ConvertTo-Json：多包输出数组，单包输出对象，空输出为空字符串 — 统一转数组再遍历
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(json);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return;
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            ProcessUwpElement(root, items, ct);
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in root.EnumerateArray())
                ProcessUwpElement(e, items, ct);
        }
    }

    private static void ProcessUwpElement(JsonElement e, Dictionary<string, InstalledApp> items, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string name = GetProperty(e, "Name");
        string pfn = GetProperty(e, "PackageFullName");
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(pfn)) return;

        string friendly = GetProperty(e, "Friendly");
        if (string.IsNullOrWhiteSpace(friendly)) friendly = name;

        var app = new InstalledApp
        {
            Id = "uwp|" + pfn,
            DisplayName = friendly,
            DisplayVersion = GetProperty(e, "Version"),
            Publisher = TrimPublisher(GetProperty(e, "Publisher")),
            EstimatedBytes = 0,
            InstallLocation = NullIfBlank(GetProperty(e, "InstallLocation")),
            UninstallString = null,
            QuietUninstallString = null,
            IsMsi = false,
            ProductCode = null,
            Source = AppSource.Uwp,
            Hive = RegistryHive.CurrentUser,
            View = RegistryView.Default,
            KeyPath = null,
            PackageFullName = pfn,
            IsOtherUserPackage = GetBool(e, "AllUsers"),
        };
        items.TryAdd(app.Id, app);
    }

    private static bool GetBool(JsonElement e, string name)
    {
        string v = GetProperty(e, name);
        return v.Equals("True", StringComparison.OrdinalIgnoreCase) || v == "1";
    }

    private static string TrimPublisher(string? p)
    {
        if (string.IsNullOrWhiteSpace(p)) return string.Empty;
        // "CN=Microsoft Corporation, O=..." → "Microsoft Corporation"
        int comma = p.IndexOf(',');
        string first = comma > 0 ? p[..comma] : p;
        return first.StartsWith("CN=", StringComparison.Ordinal) ? first[3..] : first;
    }

    private static string GetProperty(JsonElement e, string name)
    {
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null)
            return v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : v.ToString();
        return string.Empty;
    }

    // ---------- 占用与位置解析 ----------

    /// <summary>
    /// 解析每个软件的安装/数据位置（很快），并可按 <paramref name="measureFilter"/> 选择性实测占用。
    /// 顺序：① 全部条目解析位置 → ② 位置可疑性校验（排除把父目录当安装位置的脏数据）→ ③ 测量（只读遍历）。
    /// progress 参数为（已完成数, 待测总数, 当前软件），仅在测量阶段回调。
    /// </summary>
    public static FootprintSession ResolveFootprints(IReadOnlyList<InstalledApp> apps, bool measure,
        Action<int, int, InstalledApp>? progress, CancellationToken ct,
        Func<InstalledApp, bool>? measureFilter = null)
    {
        var session = new FootprintSession();

        // 阶段 1：解析位置（不测量）
        for (int i = 0; i < apps.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            apps[i].Footprint = AppFootprintResolver.Resolve(apps[i], session, measure: false, ct);
        }

        // 阶段 2：位置可疑性校验（需要跨软件视野）
        AppFootprintResolver.MarkSuspiciousPaths(apps);

        if (!measure) return session;

        // 阶段 3：测量（跳过可疑位置）
        var queue = measureFilter == null ? apps.ToArray() : apps.Where(measureFilter).ToArray();
        for (int i = 0; i < queue.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Invoke(i, queue.Length, queue[i]);
            AppFootprintResolver.Measure(queue[i], ct);
        }
        if (queue.Length > 0)
            progress?.Invoke(queue.Length, queue.Length, queue[^1]);

        return session;
    }

    // ---------- 卸载复验 / 注册表项清理 ----------

    /// <summary>卸载后复验：注册表条目是否仍存在。</summary>
    public static bool IsEntryStillRegistered(InstalledApp app)
    {
        if (app.IsUwp || string.IsNullOrEmpty(app.KeyPath)) return false;
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(app.Hive, app.View);
            using var key = baseKey.OpenSubKey(app.KeyPath);
            return key != null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ---------- PowerShell 辅助 ----------

    /// <summary>
    /// 运行 powershell.exe（-EncodedCommand，避免引号拼接问题）。killOnTimeout=true 仅用于只读查询。
    /// 实现已抽到 <see cref="PowerShellRunner"/>（磁盘分区探测共用）；此处保留原签名以免调用方改动。
    /// </summary>
    public static (int Code, string Output, string Error) RunPowerShell(
        string script, int timeoutMs, bool killOnTimeout, CancellationToken ct)
        => PowerShellRunner.Run(script, timeoutMs, killOnTimeout, ct);

    // ---------- 通用 ----------

    private static string? GetString(RegistryKey key, string name) =>
        key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;

    /// <summary>
    /// 读 DWORD。部分软件把 SystemComponent/EstimatedSize 写成字符串或 QWORD，
    /// 只认 int 会静默当成 0——表现为「系统组件重新出现在列表里」「占用估计全部消失」。
    /// </summary>
    private static int GetDWord(RegistryKey key, string name)
    {
        object? v = key.GetValue(name);
        return v switch
        {
            int i => i,
            uint u => unchecked((int)u),
            long l => unchecked((int)Math.Clamp(l, int.MinValue, int.MaxValue)),
            short s => s,
            byte b => b,
            string str => int.TryParse(str, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int p) ? p : 0,
            _ => 0,
        };
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>展开 <c>%VAR%</c> 形式的路径常量；失败时原样返回（不抛）。</summary>
    private static string? ExpandEnv(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return s;
        try { return Environment.ExpandEnvironmentVariables(s); }
        catch (Exception) { return s; }
    }

    /// <summary>去除整串外层引号（部分软件注册的 InstallLocation/UninstallString 带引号，会导致路径判定失败）。</summary>
    private static string? TrimQuotes(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        string t = s.Trim();
        if (t.Length >= 2 && t[0] == '"' && t[^1] == '"')
            t = t[1..^1].Trim();
        return NullIfBlank(t);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    [GeneratedRegex(@"\{([0-9A-Fa-f\-]{36})\}")]
    private static partial Regex GuidRegex();
}
