namespace CleanC.Core;

/// <summary>
/// 软件名 / 发布者与目录名、注册表子键名的“确定性匹配”工具。
/// 残留候选扫描（LeftoverScanner）与软件占用定位（AppFootprintResolver）共用同一套规则，
/// 只做名称规范化后的精确相等与首末 token 比较，绝不做模糊/关键字猜测。
/// </summary>
public static class AppPathMatcher
{
    /// <summary>目录名过短或过于通用时不作为匹配依据（Common、Shared、Temp、Update…）。</summary>
    private static readonly string[] GenericNames =
    {
        "common", "commonfiles", "shared", "temp", "tmp", "update", "updates", "installer",
        "system", "system32", "windows", "driver", "drivers", "bin", "lib", "libs", "data",
        "app", "apps", "application", "client", "server", "tools", "tool", "package", "packages",
        "cache", "caches", "log", "logs", "user", "users", "default", "resources", "resource",
        "microsoft", "google", "tencent", "adobe", "intel", "nvidia", "realtek",
    };

    /// <summary>按空白拆分 token（与残留扫描既有实现一致）。</summary>
    public static string[] NameTokens(string s) =>
        string.IsNullOrWhiteSpace(s)
            ? Array.Empty<string>()
            : s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    public static string FirstToken(string s)
    {
        var t = NameTokens(s);
        return t.Length > 0 ? t[0] : string.Empty;
    }

    public static string LastToken(string s)
    {
        var t = NameTokens(s);
        return t.Length > 0 ? t[^1] : string.Empty;
    }

    /// <summary>规范化：仅保留字母与数字并转小写（“Visual Studio Code” → “visualstudiocode”）。</summary>
    public static string Normalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s)
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    /// <summary>是否过于通用的名称（不作为匹配依据）。</summary>
    public static bool IsGenericName(string name)
    {
        string n = Normalize(name);
        if (n.Length < 3) return true;
        return GenericNames.Contains(n, StringComparer.Ordinal);
    }

    /// <summary>
    /// 宽松匹配键集合：显示名/发布者的整体规范化、首 token、首末 token 组合（去重、长度 ≥3）。
    /// 例：“Microsoft Visual Studio Code” → {microsoftvisualstudiocode, microsoft, microsoftcode}。
    /// </summary>
    public static IReadOnlyList<string> MatchKeys(string? displayName, string? publisher)
    {
        var keys = new List<string>(6);
        AddKeys(keys, displayName);
        AddKeys(keys, publisher);
        return keys.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static void AddKeys(List<string> keys, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var tokens = NameTokens(text);
        void Add(string candidate)
        {
            string n = Normalize(candidate);
            if (n.Length >= 3) keys.Add(n);
        }

        Add(text);
        if (tokens.Length > 0) Add(tokens[0]);
        if (tokens.Length > 1) Add(tokens[0] + tokens[^1]);
    }

    /// <summary>宽松匹配：目录名规范化后命中任一匹配键（用于“找安装/数据目录”，非删除依据）。</summary>
    public static bool MatchesLoose(string dirName, IReadOnlyList<string> keys)
    {
        if (IsGenericName(dirName) || keys.Count == 0) return false;
        string n = Normalize(dirName);
        if (n.Length < 3) return false;
        foreach (var k in keys)
            if (string.Equals(n, k, StringComparison.Ordinal))
                return true;
        return false;
    }

    // ---------- 严格匹配（残留扫描沿用，行为与既有实现一致） ----------

    /// <summary>规则 1/2：目录名 == 显示名 / 发布者（严格、忽略大小写）。命中返回匹配理由，否则 null。</summary>
    public static string? MatchDirectoryNameStrict(string dirName, string displayName, string? publisher)
    {
        if (string.Equals(dirName, displayName, StringComparison.OrdinalIgnoreCase))
            return "目录名 == 显示名";
        if (!string.IsNullOrEmpty(publisher) && string.Equals(dirName, publisher, StringComparison.OrdinalIgnoreCase))
            return "目录名 == 发布者";
        return null;
    }

    /// <summary>规则 3：末两级路径 == 显示名首末 token（如 Google\Chrome ← “Google Chrome”）。</summary>
    public static string? MatchParentChildStrict(string path, int depth, string displayName)
    {
        var tokens = NameTokens(displayName);
        if (depth < 2 || tokens.Length < 2) return null;

        var parts = path.TrimEnd('\\').Split('\\');
        if (parts.Length < 2) return null;
        if (parts[^2].Equals(tokens[0], StringComparison.OrdinalIgnoreCase) &&
            parts[^1].Equals(tokens[^1], StringComparison.OrdinalIgnoreCase))
            return "路径与显示名一致（如 Google\\Chrome）";
        return null;
    }

    /// <summary>注册表子键名 == 显示名 / 发布者。</summary>
    public static bool NameEqualsStrict(string name, string displayName, string? publisher) =>
        string.Equals(name, displayName, StringComparison.OrdinalIgnoreCase) ||
        (!string.IsNullOrEmpty(publisher) && string.Equals(name, publisher, StringComparison.OrdinalIgnoreCase));

    // ---------- 位置合理性 ----------

    private static readonly string[] BroadRelativePaths =
    {
        @"Program Files", @"Program Files (x86)", @"Program Files\Common Files",
        @"Program Files (x86)\Common Files", "ProgramData", "Users", "Windows",
        @"Windows\System32", @"Windows\SysWOW64", @"Windows\Temp",
        @"ProgramData\Microsoft", @"ProgramData\Package Cache",
    };

    /// <summary>
    /// 路径是否过宽（盘根 / Program Files / ProgramData / 用户目录 / AppData 等本身）：
    /// 这类“安装位置”若来自注册表脏数据，会把整个公共目录算给某个软件，必须拒绝。
    /// </summary>
    public static bool IsTooBroadPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;

        string full;
        try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception) { return true; }

        if (full.Length <= 3) return true; // 盘根 C:\

        string drive = Path.GetPathRoot(full) ?? string.Empty;
        if (string.Equals(full, Path.TrimEndingDirectorySeparator(drive), StringComparison.OrdinalIgnoreCase))
            return true;

        // 用户目录（C:\Users\X）与各 AppData 根本身（走 UserContext：提权后仍指当前登录用户）
        string[] profileRoots =
        {
            UserContext.Profile,
            UserContext.LocalAppData,
            UserContext.RoamingAppData,
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Path.Combine(UserContext.Profile, "AppData"),
            UserContext.Desktop,
            UserContext.Documents,
        };
        foreach (var r in profileRoots)
            if (!string.IsNullOrEmpty(r) && string.Equals(full, Path.TrimEndingDirectorySeparator(r), StringComparison.OrdinalIgnoreCase))
                return true;

        foreach (var rel in BroadRelativePaths)
        {
            string p = Path.TrimEndingDirectorySeparator(Path.Combine(drive, rel));
            if (string.Equals(full, p, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>是否为系统目录（不作为软件安装位置/数据位置候选）。</summary>
    public static bool IsSystemPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;
        // Windows 目录随系统盘变化，不能用 "C:\Windows" 兜底
        return SafeTargets.IsSameOrUnder(path, SafeTargets.WindowsDirectory());
    }
}
