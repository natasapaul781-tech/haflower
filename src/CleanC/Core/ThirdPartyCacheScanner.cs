using Microsoft.Win32;

namespace CleanC.Core;

/// <summary>
/// 第三方软件缓存扫描器：在启动时探测已安装第三方软件的缓存目录，生成可清理项。
/// 全部为“确定性规则”：已知软件表只命中已核实的缓存子目录（如浏览器各渠道的 Cache/GPUCache、
/// 微信 FileStorage\Cache、Steam shadercache），通用规则只收集**同时具备 Chromium/Electron
/// 用户数据目录佐证**（Local State / Preferences / Network 等）且名称精确等于
/// Cache/Code Cache/GPUCache/cache2 等标准缓存名的目录。绝不触碰聊天记录、书签、密码、下载文件、游戏本体等用户数据。
/// 说明：跳过名单里含 Tencent/Microsoft/Google 等厂商目录，因此这些厂商下的软件**不会**被通用规则命中，
/// 只能由已知表覆盖（未列入已知表的厂商级应用会被有意放过——宁可漏清，不可误删）。
/// </summary>
public static class ThirdPartyCacheScanner
{
    public const string CategoryName = "第三方软件缓存";

    private const int MaxGenericItems = 40;

    // 用户级目录一律走 UserContext：提权到别的账户时它仍指向**当前登录用户**
    private static readonly string LocalAppData = UserContext.LocalAppData;
    private static readonly string AppData = UserContext.RoamingAppData;
    private static readonly string Documents = UserContext.Documents;
    private static readonly string ProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    private static readonly string ProgramFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

    /// <summary>通用规则枚举时跳过的顶层目录（系统/框架/已被已知表覆盖）。</summary>
    private static readonly string[] SkipTopLevel =
    {
        "Packages", "Microsoft", "Google", "Mozilla", "Tencent", "Netease", "Apple",
        "JetBrains", "ElevatedTrust", "Windows", "Docker", "Temp",
    };

    // Chromium/Electron 的标准缓存子目录名（纯缓存，删除后自动重建）。
    private static readonly string[] ChromiumCacheSubNames = { "Cache", "Code Cache", "GPUCache", "GrShaderCache", "ShaderCache" };
    private static readonly string[] ElectronCacheSubNames = { "Cache", "Code Cache", "GPUCache" };

    /// <summary>
    /// 「这个目录确实是 Chromium/Electron 应用的**用户数据目录**」的佐证（文件或子目录名）。
    /// 通用规则**必须**据此确认布局，而不是只看到有个叫 Cache 的目录就当缓存——
    /// 在别人的电脑上，任何程序把自己的非可再生数据放在名为 Cache 的目录里都不奇怪。
    /// </summary>
    private static readonly string[] ChromiumEvidenceNames =
    {
        "Local State", "Preferences", "Local Storage", "Session Storage",
        "Network", "IndexedDB", "Web Data", "History",
    };

    /// <summary>返回本机已探测到的第三方软件缓存清理项（文件不存在的软件不会出现）。</summary>
    public static IReadOnlyList<CleanItem> CreateItems()
    {
        // claimed：已认领的缓存目录（规范化路径），已知表优先，通用规则不得重复
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<CleanItem>();

        AddKnown(items, claimed);
        AddGeneric(items, claimed);
        return items;
    }

    // ---------- 已知软件表（确定性路径，命中才生成） ----------

    private static void AddKnown(List<CleanItem> items, HashSet<string> claimed)
    {
        // 浏览器（Chromium 内核）：配置文件目录下的标准缓存子目录。
        // 注意渠道差异：稳定版在 Google\Chrome\User Data，而 Beta/Dev/Canary 分别在
        // "Chrome Beta" / "Chrome Dev" / "Chrome SxS" 目录下（Edge 同理），
        // 通用规则又会因为 SkipTopLevel 里的 Google/Microsoft 而跳过这些厂商目录，
        // 所以这里必须逐渠道显式列出，否则别的电脑上装了 Beta 版就完全清不到。
        AddItem(items, claimed, "tpc-chrome", "Chrome 浏览器缓存",
            "Chrome 各渠道（稳定版/Beta/Dev/Canary）各配置目录的网页/GPU/着色器缓存（Cache、Code Cache、GPUCache 等），删除后自动重建。",
            "仅清理缓存子目录，不影响书签、密码、历史记录；建议先退出 Chrome，被占用文件自动跳过。",
            recommended: true, elevation: false,
            ChromiumChannels(@"Google",
                "Chrome", "Chrome Beta", "Chrome Dev", "Chrome SxS"));

        AddItem(items, claimed, "tpc-edge", "Edge 浏览器缓存",
            "Edge 各渠道（稳定版/Beta/Dev/Canary）各配置目录的网页/GPU/着色器缓存（Cache、Code Cache、GPUCache 等），删除后自动重建。",
            "仅清理缓存子目录，不影响收藏夹、密码、历史记录；建议先退出 Edge，被占用文件自动跳过。",
            recommended: true, elevation: false,
            ChromiumChannels("Microsoft",
                "Edge", "Edge Beta", "Edge Dev", "Edge Canary"));

        AddItem(items, claimed, "tpc-firefox", "Firefox 浏览器缓存",
            "Firefox 各配置目录的缓存（cache2、startupCache），删除后自动重建。",
            "仅清理缓存子目录，不影响书签、密码、历史记录；建议先退出 Firefox。",
            recommended: true, elevation: false,
            FirefoxProfileCacheDirs());

        // 微信：只清纯缓存（图片/视频缩略图 FileStorage\Cache、网页内核 radium\cache、小程序框架缓存 WmpfCache），
        // 兼容 3.x（%APPDATA%\Tencent\WeChat\WeChat Files）与 4.x（radium 布局 + 文档目录）两种存储
        AddItem(items, claimed, "tpc-wechat", "微信缓存",
            "微信图片/视频缩略图缓存（FileStorage\\Cache）与网页/小程序缓存（radium\\cache、WmpfCache），清理后聊天记录与接收文件不受影响。",
            "仅清理纯缓存目录；聊天记录、收藏、接收文件不受影响；建议先退出微信。",
            recommended: true, elevation: false,
            WeChatCacheDirs());

        // 钉钉：头像/表情/资源缓存（各账号目录下的 *_cache、AvatarFiles、defEmotion 等）
        AddItem(items, claimed, "tpc-dingtalk", "钉钉缓存",
            "钉钉头像、表情、资源缓存（resource_cache 等），可自动重新下载。",
            "仅清理头像/表情/资源缓存，聊天记录、接收文件不受影响；建议先退出钉钉。",
            recommended: true, elevation: false,
            DingTalkCacheDirs());

        // Steam：注册表定位安装路径，只清 appcache 与 shadercache（不含游戏本体）
        AddItem(items, claimed, "tpc-steam", "Steam 缓存",
            "Steam 客户端缓存与着色器缓存（appcache、steamapps\\shadercache），删除后自动重建。",
            "仅清理缓存，不影响游戏本体与存档；安装于 Program Files 时需管理员权限；建议先退出 Steam。",
            recommended: true, elevation: true,
            SteamCacheDirs());

        AddItem(items, claimed, "tpc-netease", "网易云音乐缓存",
            "网易云音乐试听/封面等缓存（Netease\\CloudMusic\\Cache），清理后重新加载。",
            "仅清理缓存，歌单与已下载歌曲不受影响；建议先退出网易云音乐。",
            recommended: true, elevation: false,
            new[] { Path.Combine(LocalAppData, @"Netease\CloudMusic\Cache") });
    }

    // ---------- 通用规则（Chromium/Electron 布局） ----------

    /// <summary>
    /// 通用规则：对 %LOCALAPPDATA% / %APPDATA% 下的应用目录按两种标准布局收集缓存，
    /// 同一应用名（含不同规则命中）合并为一条。
    /// ① Chromium 风格：应用目录内 User Data（深度 ≤ 2）中的配置目录
    ///    （Default / Profile N / 含 Preferences 文件）→ 收集标准缓存子目录；
    /// ② Electron 风格：应用根目录下直接存在 Cache/Code Cache/GPUCache。
    /// 覆盖未列入已知表的 Chromium 浏览器、Electron 桌面应用（如夸克、QQ NT、Discord、飞书等）。
    /// </summary>
    private static void AddGeneric(List<CleanItem> items, HashSet<string> claimed)
    {
        if (items.Count >= MaxGenericItems) return;

        var byAppName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        // ① Chromium 风格（仅 %LOCALAPPDATA% 常见布局）
        foreach (var appDir in SafeGetDirs(LocalAppData))
        {
            string name = Path.GetFileName(appDir);
            if (IsSkipTopLevel(name) || IsReparsePoint(appDir)) continue;

            foreach (var userData in FindUserDataDirs(appDir))
            {
                if (!HasChromiumEvidence(userData)) continue;
                var dirs = ChromiumProfileCacheDirs(userData).ToArray();
                if (dirs.Length == 0) continue;
                AddToGroup(byAppName, name, dirs);
            }
        }

        // ② Electron 风格（%APPDATA% 与 %LOCALAPPDATA% 均可能）
        foreach (var root in new[] { AppData, LocalAppData })
        {
            foreach (var appDir in SafeGetDirs(root))
            {
                string name = Path.GetFileName(appDir);
                if (IsSkipTopLevel(name) || IsReparsePoint(appDir)) continue;

                // 必须先确认这是 Chromium/Electron 的用户数据目录（有 Local State / Preferences /
                // Network 等），否则「有个叫 Cache 的子目录」不足以证明它是可再生缓存。
                if (!HasChromiumEvidence(appDir)) continue;

                AddToGroup(byAppName, name, ElectronCacheSubNames
                    .Select(sub => Path.Combine(appDir, sub))
                    .Where(Directory.Exists));
            }
        }

        foreach (var kv in byAppName)
        {
            if (items.Count >= MaxGenericItems) break;
            string name = kv.Key;
            var dirs = kv.Value;

            AddItem(items, claimed, "tpc-gen-" + name,
                $"{name}（通用检测）",
                $"通用规则检测：{dirs[0]} 等 {dirs.Count} 个标准缓存目录（Cache、Code Cache、GPUCache 等），删除后自动重建。",
                "通用规则命中，仅清理标准缓存子目录（不影响书签/密码/历史记录/聊天数据）；建议先退出该程序；默认不勾选。",
                recommended: false, elevation: false, dirs);
        }
    }

    private static void AddToGroup(Dictionary<string, List<string>> byAppName, string name, IEnumerable<string> dirs)
    {
        var add = dirs
            .Where(d => !string.IsNullOrEmpty(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (add.Length == 0) return; // 空组不建键，避免后续 dirs[0] 越界

        if (!byAppName.TryGetValue(name, out var list))
        {
            list = new List<string>();
            byAppName[name] = list;
        }
        list.AddRange(add.Where(d => !list.Contains(d, StringComparer.OrdinalIgnoreCase)));
    }

    // ---------- 目录定位器 ----------

    /// <summary>
    /// 某厂商（Google / Microsoft）下各**渠道**浏览器配置目录的缓存：
    /// 稳定版与 Beta/Dev/Canary 各占一个目录，只列稳定版会让装了 Beta 的机器完全清不到。
    /// </summary>
    private static IEnumerable<string> ChromiumChannels(string vendor, params string[] channelDirs)
    {
        foreach (var channel in channelDirs)
        {
            string userData = Path.Combine(LocalAppData, vendor, channel, "User Data");
            if (!Directory.Exists(userData)) continue;
            foreach (var d in ChromiumProfileCacheDirs(userData)) yield return d;
        }
    }

    /// <summary>Chromium 配置目录（含 Preferences 文件，或名为 Default / Profile N）下的标准缓存子目录。</summary>
    private static IEnumerable<string> ChromiumProfileCacheDirs(string userDataRoot)
    {
        if (string.IsNullOrEmpty(userDataRoot) || !Directory.Exists(userDataRoot)) yield break;

        foreach (var profile in SafeGetDirs(userDataRoot))
        {
            string name = Path.GetFileName(profile);
            bool isProfile = name.Equals("Default", StringComparison.OrdinalIgnoreCase)
                             || name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase)
                             || File.Exists(Path.Combine(profile, "Preferences"));
            if (!isProfile) continue;

            foreach (var sub in ChromiumCacheSubNames)
            {
                string p = Path.Combine(profile, sub);
                if (Directory.Exists(p)) yield return p;
            }
        }
    }

    /// <summary>Firefox 各配置目录下的 cache2 / startupCache。</summary>
    private static IEnumerable<string> FirefoxProfileCacheDirs()
    {
        string root = Path.Combine(LocalAppData, @"Mozilla\Firefox\Profiles");
        if (!Directory.Exists(root)) yield break;

        foreach (var profile in SafeGetDirs(root))
            foreach (var sub in new[] { "cache2", "startupCache" })
            {
                string p = Path.Combine(profile, sub);
                if (Directory.Exists(p)) yield return p;
            }
    }

    /// <summary>
    /// 微信纯缓存目录：
    /// ① FileStorage\Cache（图片/视频缩略图）：3.x 为 %APPDATA%\Tencent\WeChat\WeChat Files，4.x 常在文档目录；
    /// ② 4.x 网页内核与小程序框架缓存：radium\cache、radium\WmpfCache。
    /// 只定位命名的缓存子目录，绝不触碰聊天记录（msg）、文件（filestorage/file）等用户数据。
    /// </summary>
    private static IEnumerable<string> WeChatCacheDirs()
    {
        foreach (var baseDir in new[]
                 {
                     Path.Combine(AppData, @"Tencent\WeChat\WeChat Files"),
                     Path.Combine(Documents, "WeChat Files"),
                 })
        {
            if (!Directory.Exists(baseDir)) continue;
            foreach (var account in SafeGetDirs(baseDir))
            {
                string cache = Path.Combine(account, @"FileStorage\Cache");
                if (Directory.Exists(cache)) yield return cache;
            }
        }

        string radium = Path.Combine(AppData, @"Tencent\WeChat\radium");
        foreach (var sub in new[] { "cache", "WmpfCache" })
        {
            string p = Path.Combine(radium, sub);
            if (Directory.Exists(p)) yield return p;
        }
    }

    /// <summary>钉钉资源/头像/表情缓存：%APPDATA%\DingTalk 下深度 ≤ 2 的 resource_cache / AvatarFiles / defEmotion。</summary>
    private static IEnumerable<string> DingTalkCacheDirs()
    {
        string root = Path.Combine(AppData, "DingTalk");
        if (!Directory.Exists(root)) yield break;

        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            var (dir, depth) = stack.Pop();
            if (depth >= 2) continue;
            foreach (var sub in SafeGetDirs(dir))
            {
                string name = Path.GetFileName(sub);
                if (name.Equals("resource_cache", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("AvatarFiles", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("defEmotion", StringComparison.OrdinalIgnoreCase))
                {
                    yield return sub;
                    continue; // 命中是清理目标本身，不再下探
                }
                stack.Push((sub, depth + 1));
            }
        }
    }

    /// <summary>Steam 安装路径（HKCU SteamPath，回退 HKLM InstallPath）下的 appcache 与 shadercache。</summary>
    private static IEnumerable<string> SteamCacheDirs()
    {
        string? install = null;
        try
        {
            using var userKey = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            install = userKey?.GetValue("SteamPath") as string;
        }
        catch (Exception) { }

        if (string.IsNullOrEmpty(install))
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
                using var key = baseKey.OpenSubKey(@"SOFTWARE\Valve\Steam");
                install = key?.GetValue("InstallPath") as string;
            }
            catch (Exception) { }
        }

        if (string.IsNullOrEmpty(install) || !Directory.Exists(install)) yield break;

        // 注册表里的路径同样是脏数据高发区：必须过安全闸门，否则 appcache 会拼到一个很宽的位置上
        if (SafeTargets.Reject(Path.Combine(install, "appcache")) is not null) yield break;

        yield return Path.Combine(install, "appcache");
        yield return Path.Combine(install, @"steamapps\shadercache");
    }

    /// <summary>在应用目录内部寻找 User Data 目录（自身一层 + 子目录一层，覆盖 厂商\浏览器\User Data 布局）。</summary>
    private static IEnumerable<string> FindUserDataDirs(string appDir)
    {
        string direct = Path.Combine(appDir, "User Data");
        if (Directory.Exists(direct)) yield return direct;

        foreach (var sub in SafeGetDirs(appDir))
        {
            if (sub.Equals(direct, StringComparison.OrdinalIgnoreCase)) continue;
            if (IsReparsePoint(sub)) continue;
            string nested = Path.Combine(sub, "User Data");
            if (Directory.Exists(nested)) yield return nested;
        }
    }

    /// <summary>
    /// 该目录是否具备 Chromium/Electron 用户数据目录的佐证（自身或下一层存在
    /// Local State / Preferences / Local Storage / Network 等）。
    /// 这是通用规则的**准入条件**：没有佐证就不把它当浏览器缓存。
    /// </summary>
    private static bool HasChromiumEvidence(string dir)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;

        foreach (var name in ChromiumEvidenceNames)
        {
            try
            {
                if (File.Exists(Path.Combine(dir, name)) || Directory.Exists(Path.Combine(dir, name)))
                    return true;
            }
            catch (Exception) { /* 单个条目失败忽略 */ }
        }

        // 再下探一层：User Data\<Profile>\Preferences 这种布局
        foreach (var sub in SafeGetDirs(dir))
        {
            if (IsReparsePoint(sub)) continue;
            foreach (var name in new[] { "Preferences", "Local Storage", "Network", "Web Data" })
            {
                try
                {
                    if (File.Exists(Path.Combine(sub, name)) || Directory.Exists(Path.Combine(sub, name)))
                        return true;
                }
                catch (Exception) { /* 忽略 */ }
            }
        }

        return false;
    }

    // ---------- 条目构造 ----------

    /// <summary>
    /// 由一组缓存目录生成清理项：剔除不存在/重解析点/已被认领的目录；
    /// 目录全部被剔除时不生成条目。多目录合并为一项（分析/清理均逐目录操作）。
    /// </summary>
    private static void AddItem(List<CleanItem> items, HashSet<string> claimed,
        string id, string name, string description, string warning,
        bool recommended, bool elevation, IEnumerable<string> candidateDirs)
    {
        var dirs = new List<string>();
        foreach (var d in candidateDirs)
        {
            if (string.IsNullOrEmpty(d) || !Directory.Exists(d)) continue;
            if (IsReparsePoint(d)) continue;

            // 安全闸门：目标来自注册表（SteamPath）或对 %APPDATA% 的目录名猜测，
            // 在别人的电脑上可能指向盘根/用户目录，一律不列为可清理项。
            if (SafeTargets.Reject(d) is { } unsafeWhy)
            {
                OperationLog.LogSystem($"第三方缓存项「{name}」已跳过：{d}（{unsafeWhy}）");
                continue;
            }

            if (!claimed.Add(Normalize(d))) continue; // 已被其他条目认领（已知表优先）
            dirs.Add(d);
        }
        if (dirs.Count == 0) return;

        bool needsElevation = elevation || dirs.Any(PathNeedsElevation);
        string[] snapshot = dirs.ToArray();

        items.Add(new CleanItem
        {
            Id = id,
            Name = name,
            Category = CategoryName,
            Description = description + (snapshot.Length > 1 ? $" 共 {snapshot.Length} 个缓存目录。" : string.Empty),
            Recommended = recommended,
            RequiresElevation = needsElevation,
            Warning = warning + (needsElevation && !elevation ? "（含 Program Files 下的目录，需管理员）" : string.Empty),
            Paths = snapshot,
            Estimate = ct => Task.Run(() =>
            {
                long total = 0;
                foreach (var d in snapshot)
                    if (Directory.Exists(d) && !IsReparsePoint(d))
                        total += DirectoryHelper.GetDirectorySize(d);
                return total;
            }, ct),
            Clean = ct => Task.Run<CleanResult>(() =>
            {
                var r = new CleanResult();
                foreach (var d in snapshot)
                {
                    if (!Directory.Exists(d)) continue;                       // 软件已卸载/目录已消失：无操作
                    if (IsReparsePoint(d))
                    {
                        r.Errors.Add($"{d}: 检测为符号链接/联接，已拒绝清理。");
                        continue;
                    }
                    var rr = DirectoryHelper.DeleteFolderContents(d);
                    r.FreedBytes += rr.FreedBytes;
                    r.Errors.AddRange(rr.Errors.Select(e => $"{d}: {e}"));
                }
                return r;
            }, ct)
        });
    }

    // ---------- 通用工具 ----------

    private static bool IsSkipTopLevel(string name) =>
        SkipTopLevel.Any(s => name.Equals(s, StringComparison.OrdinalIgnoreCase));

    private static bool PathNeedsElevation(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length < 3) return false;
        // 带分隔符边界比较：裸 StartsWith 会把 "C:\Program Files Extra\..." 也算成 Program Files
        return SafeTargets.IsSameOrUnder(path, ProgramFiles)
            || SafeTargets.IsSameOrUnder(path, ProgramFilesX86);
    }

    private static bool IsReparsePoint(string path)
    {
        try { return (new DirectoryInfo(path).Attributes & FileAttributes.ReparsePoint) != 0; }
        catch (Exception) { return false; }
    }

    private static string Normalize(string path)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception) { return path.TrimEnd('\\', '/'); }
    }

    private static string[] SafeGetDirs(string dir)
    {
        try { return Directory.GetDirectories(dir); }
        catch (Exception) { return Array.Empty<string>(); }
    }
}
