using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CleanC.Core;

/// <summary>
/// powershell.exe 调用工具：脚本以 <c>-EncodedCommand</c>（Base64/UTF-16LE）传入，避免引号与中文拼接问题；
/// 另附 JSON 输出解析辅助（ConvertTo-Json 在单元素时会输出对象、多元素时输出数组，需统一归一化）。
/// 零第三方依赖：PowerShell 为系统内置。
/// </summary>
public static class PowerShellRunner
{
    /// <summary>
    /// 所有生成脚本的统一前缀：① 输出编码固定 UTF-8（否则中文按系统 ANSI 码页输出，读取端会乱码）；
    /// ② 关掉进度流——**首次运行 PowerShell 时它会把「正在准备首次使用模块」写进 stderr**，
    ///    与真正的错误 CLIXML 混在一起，令错误提示变成一堆无法解析的 XML；
    /// ③ 错误偏好由各脚本自行覆盖。
    /// </summary>
    public const string Prelude =
        "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8\n" +
        "$ProgressPreference='SilentlyContinue'\n";

    private static readonly string? CachedExePath = ResolvePowerShellPath();

    /// <summary>
    /// PowerShell 可执行文件的解析结果（null = 本机没有可用的 PowerShell）。
    /// 刻意走**绝对路径**：<c>Process.Start(UseShellExecute=false)</c> 的查找顺序是
    /// 「exe 所在目录 → 当前目录 → 系统目录 → PATH」，用裸 "powershell.exe" 会让
    /// 与本程序同目录下的同名文件被优先执行（劫持风险），也会在 PATH 异常时找不到。
    /// </summary>
    public static string? ExePath => CachedExePath;

    private static string? ResolvePowerShellPath()
    {
        var candidates = new List<string>();
        try
        {
            // Windows PowerShell 5.1：Win10/11 必装，且带 Storage 模块（磁盘分区功能依赖）
            candidates.Add(Path.Combine(SafeTargets.WindowsDirectory(),
                @"System32\WindowsPowerShell\v1.0\powershell.exe"));
            // 32 位进程（理论上不会出现）下 System32 会被重定向，补一个 SysWOW64
            candidates.Add(Path.Combine(SafeTargets.WindowsDirectory(),
                @"SysWOW64\WindowsPowerShell\v1.0\powershell.exe"));
            // PowerShell 7（若用户装了，也可用：本工具只用两版都有的命令）
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe"));
        }
        catch (Exception) { /* 忽略 */ }

        foreach (var c in candidates)
        {
            try { if (File.Exists(c)) return c; }
            catch (Exception) { /* 忽略 */ }
        }

        // 最后才回退到 PATH（有些精简/定制系统把 WindowsPowerShell 装到了别处）
        foreach (var name in new[] { "powershell.exe", "pwsh.exe" })
        {
            try
            {
                string? pathEnv = Environment.GetEnvironmentVariable("PATH");
                if (string.IsNullOrEmpty(pathEnv)) continue;
                foreach (var dir in pathEnv.Split(Path.PathSeparator))
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    string full = Path.Combine(dir.Trim().Trim('"'), name);
                    if (File.Exists(full)) return full;
                }
            }
            catch (Exception) { /* 忽略 */ }
        }

        return null;
    }

    /// <summary>
    /// 运行 powershell.exe 并等待完成。<paramref name="killOnTimeout"/> = true 仅用于**只读**查询
    /// （超时/取消时杀死子进程是安全的）；卸载、分区等破坏性操作必须传 false（绝不中途终止）。
    /// </summary>
    public static (int Code, string Output, string Error) Run(
        string script, int timeoutMs, bool killOnTimeout, CancellationToken ct)
    {
        string? exe = CachedExePath;
        if (exe is null)
            return (-1, string.Empty,
                "本机找不到 PowerShell（既没有 Windows PowerShell 5.1，也没有 PowerShell 7）。" +
                "磁盘分区、软件卸载等功能不可用；如需这些功能，请安装 PowerShell 或改用说明书中的手动步骤。");

        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        Process? p;
        try
        {
            p = Process.Start(psi);
        }
        catch (Exception ex)
        {
            // 被组策略/安全软件拦截时 Start 本身会抛异常，不能让它冒泡到 UI 线程
            return (-1, string.Empty, $"无法启动 PowerShell（{ex.Message}）。若系统限制脚本执行，请改用说明书中的手动步骤。");
        }

        using (p)
        {
            if (p == null) return (-1, string.Empty, "无法启动 PowerShell。");

            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            var sw = Stopwatch.StartNew();
            bool timedOut = false;

            // 轮询等待；超时按 killOnTimeout 决定杀死或退出等待（不杀死卸载/分区类进程）
            while (!p.HasExited)
            {
                if (ct.IsCancellationRequested || sw.ElapsedMilliseconds > timeoutMs)
                {
                    if (killOnTimeout) { try { p.Kill(); } catch (Exception) { } }
                    timedOut = true;
                    break;
                }
                try { p.WaitForExit(200); }
                catch (Exception) { break; }
            }
            if (!timedOut)
            {
                try { p.WaitForExit(); } catch (Exception) { }
            }

            string stdout = outTask.IsCompleted ? outTask.Result : string.Empty;
            string stderr = errTask.IsCompleted ? errTask.Result : string.Empty;
            int code;
            try { code = p.HasExited ? p.ExitCode : -1; }
            catch (Exception) { code = -1; }

            if (ct.IsCancellationRequested)
                return (-1, stdout, "已取消等待 PowerShell 完成");
            if (!killOnTimeout && sw.ElapsedMilliseconds > timeoutMs)
                return (-1, stdout, $"PowerShell 运行超过 {timeoutMs / 1000}s，已放弃等待");
            return (code, stdout, stderr);
        }
    }

    /// <summary>
    /// 把 powershell.exe 的 stderr 变成可读文本。
    ///
    /// 脚本里发生错误时，Windows PowerShell 会把**错误记录序列化成 CLIXML** 写到 stderr，
    /// 形如 <c>#&lt; CLIXML&lt;Objs ...&gt;...&lt;/Objs&gt;</c>。直接显示给用户毫无意义
    /// （屏幕上就只有「#&lt; CLIXML」几个字符）。这里把其中的 &lt;S&gt; 文本节点抽出来拼成一行。
    /// </summary>
    public static string UnwrapStderr(string? stderr)
    {
        string s = (stderr ?? string.Empty).Trim();
        if (s.Length == 0) return string.Empty;
        if (!s.StartsWith("#< CLIXML", StringComparison.Ordinal)) return s;

        string xml = s["#< CLIXML".Length..].Trim();
        var parts = new List<string>();
        try
        {
            var doc = System.Xml.Linq.XDocument.Parse(xml);
            foreach (var node in doc.Descendants("S"))
            {
                string text = node.Value?.Trim() ?? string.Empty;
                if (text.Length > 0) parts.Add(System.Net.WebUtility.HtmlDecode(text));
            }

            // 没有 <S>（错误文本）时，退一步取 <AV>（进度/摘要文本）：PowerShell 首次运行会把
            // 「正在准备首次使用模块」这类进度记录也写成 CLIXML，此时给出它比「无法解析」有用。
            if (parts.Count == 0)
            {
                foreach (var node in doc.Descendants("AV"))
                {
                    string text = node.Value?.Trim() ?? string.Empty;
                    if (text.Length > 0) parts.Add(System.Net.WebUtility.HtmlDecode(text));
                }
            }
        }
        catch (Exception)
        {
            return xml.Length <= 300 ? xml : xml[..300] + "...";
        }

        if (parts.Count == 0) return "PowerShell 报错（无法解析详细信息）";
        var distinct = parts.Distinct().ToArray();
        string joined = string.Join("；", distinct);
        return joined.Length <= 300 ? joined : joined[..300] + "...";
    }

    /// <summary>
    /// 解析 PowerShell 的 <c>ConvertTo-Json</c> 输出为元素序列：
    /// 单元素时 ConvertTo-Json 输出裸对象、多元素时输出数组，统一按数组处理；空输出返回空序列。
    /// 解析失败返回空序列（调用方按“无数据”处理，不抛出）。
    /// </summary>
    public static IReadOnlyList<JsonElement> ParseJsonAsList(string output)
    {
        string json = (output ?? string.Empty).Trim();
        if (json.Length == 0) return Array.Empty<JsonElement>();

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(json);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Array.Empty<JsonElement>();
        }

        // PS 5.1 对单元素集合写出的仍可能是数组；对单对象则写出对象，两种都要兼容
        if (root.ValueKind == JsonValueKind.Array)
            return root.EnumerateArray().Select(e => e.Clone()).ToArray();
        if (root.ValueKind == JsonValueKind.Object)
            return new[] { root };
        return Array.Empty<JsonElement>();
    }

    /// <summary>
    /// 取某个数组属性的原始 JSON 文本，再交给 <see cref="ParseJsonAsList"/> 归一化；
    /// 属性缺失、为 null 或不是数组/对象时返回空序列（不抛出）。
    /// </summary>
    public static IReadOnlyList<JsonElement> ArrayOf(this JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return Array.Empty<JsonElement>();
        if (v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return Array.Empty<JsonElement>();
        if (v.ValueKind == JsonValueKind.Array) return ParseJsonAsList(v.GetRawText());
        if (v.ValueKind == JsonValueKind.Object) return new[] { v.Clone() };
        return Array.Empty<JsonElement>();
    }

    // ---------- JSON 取值辅助（字段缺失/类型不符一律回退默认值，不抛出） ----------

    public static string GetString(this JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return string.Empty;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? string.Empty,
            JsonValueKind.Null => string.Empty,
            JsonValueKind.Undefined => string.Empty,
            _ => v.ToString()
        };
    }

    public static string? GetStringOrNull(this JsonElement e, string name)
    {
        string s = e.GetString(name);
        return s.Length == 0 ? null : s;
    }

    public static long GetInt64(this JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt64(out long l) ? l : (long)v.GetDouble(),
            JsonValueKind.String => long.TryParse(v.GetString(), out long p) ? p : 0,
            _ => 0
        };
    }

    public static int GetInt32(this JsonElement e, string name)
    {
        long v = e.GetInt64(name);
        if (v > int.MaxValue) return int.MaxValue;
        if (v < int.MinValue) return int.MinValue;
        return (int)v;
    }

    /// <summary>取布尔值：兼容 PS 的 true/false 与 "True"/"False" 字符串。</summary>
    public static bool GetBool(this JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return false;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(v.GetString(), out bool b) && b,
            JsonValueKind.Number => v.TryGetInt64(out long l) && l != 0,
            _ => false
        };
    }

    /// <summary>取以 \" 分隔的字节数为标识的 UInt64（分区尺寸超出 Int64 时按 long.MaxValue 截断）。</summary>
    public static ulong GetUInt64(this JsonElement e, string name)
    {
        long v = e.GetInt64(name);
        return v < 0 ? 0UL : (ulong)v;
    }
}
