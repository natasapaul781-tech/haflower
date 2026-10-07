using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace CleanC.Core;

/// <summary>卸载执行方式。</summary>
public enum UninstallMode
{
    /// <summary>静默卸载（MSI /qn、QuietUninstallString；不支持时回退交互式）。</summary>
    Silent,

    /// <summary>运行官方卸载程序（可能弹出引导向导）。</summary>
    Interactive,
}

/// <summary>卸载结果状态。</summary>
public enum UninstallStatus
{
    /// <summary>卸载成功且注册表条目已消失。</summary>
    Success,

    /// <summary>成功但要求重启后生效。</summary>
    RebootRequired,

    /// <summary>卸载程序已退出，但条目仍在（可能未卸载干净/正在运行）。</summary>
    StillRegistered,

    /// <summary>卸载失败（启动失败、退出码非 0、超时等）。</summary>
    Failed,

    /// <summary>该应用没有官方卸载程序（UninstallString 缺失）。</summary>
    NotSupported,
}

public sealed class UninstallOutcome
{
    public UninstallStatus Status { get; init; }
    public string Detail { get; init; } = string.Empty;
}

/// <summary>
/// 卸载执行引擎：MSI 走 msiexec /x，非 MSI 走 QuietUninstallString / UninstallString（官方卸载程序），
/// UWP 走 Remove-AppxPackage；卸载后轮询复验注册表条目。取消仅在步骤边界生效，绝不杀死已启动的卸载程序。
/// </summary>
public static partial class UninstallRunner
{
    private const int VerifyPollMs = 2000;
    private const int VerifyTimeoutMs = 180_000;   // 复验轮询上限（3 分钟）
    private const int MsiTimeoutMs = 3_600_000;    // MSI 静默卸载上限（1 小时）
    private const int UwpTimeoutMs = 600_000;      // 商店应用卸载上限（10 分钟）

    public static async Task<UninstallOutcome> UninstallAsync(
        InstalledApp app, UninstallMode mode, Action<string>? log, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                if (app.IsUwp) return UninstallUwp(app, log, ct);
                if (app.IsMsi && !string.IsNullOrEmpty(app.ProductCode))
                    return UninstallMsi(app, mode, log, ct);
                return UninstallNative(app, mode, log, ct);
            }
            catch (OperationCanceledException)
            {
                return new UninstallOutcome
                {
                    Status = UninstallStatus.StillRegistered,
                    Detail = "已取消等待卸载程序完成；可重新扫描确认状态。"
                };
            }
            catch (Exception ex)
            {
                return new UninstallOutcome { Status = UninstallStatus.Failed, Detail = ex.Message };
            }
        }, ct);
    }

    // ---------- MSI ----------

    private static UninstallOutcome UninstallMsi(InstalledApp app, UninstallMode mode, Action<string>? log, CancellationToken ct)
    {
        string msiexec = Path.Combine(Environment.SystemDirectory, "msiexec.exe");
        string args = mode == UninstallMode.Silent
            ? $"/x \"{app.ProductCode}\" /qn /norestart"
            : $"/x \"{app.ProductCode}\"";   // 交互：显示 MSI 卸载确认对话框

        log?.Invoke($"    执行：msiexec {args}");
        int? code = RunProcess(msiexec, args, MsiTimeoutMs, killOnCancel: false, ct);
        if (code == null)
            return new UninstallOutcome { Status = UninstallStatus.StillRegistered, Detail = "等待 msiexec 完成超时或已取消（未强制终止），请重新扫描确认状态。" };

        bool needsReboot = code == 3010;
        if (code != 0 && !needsReboot)
            return new UninstallOutcome
            {
                Status = UninstallStatus.Failed,
                Detail = code == 1618
                    ? "msiexec 返回 1618：有另一个安装正在进行，请稍后重试。"
                    : $"msiexec 返回错误码 {code}。"
            };

        bool removed = WaitForRemoval(app, ct);
        var status = needsReboot
            ? (removed ? UninstallStatus.Success : UninstallStatus.RebootRequired)
            : (removed ? UninstallStatus.Success : UninstallStatus.StillRegistered);
        string detail = needsReboot && !removed
            ? "卸载完成，需要重启后注册表条目才会消失。"
            : removed ? "注册表条目已消失。" : "注册表条目仍存在（可能未卸载干净）。";
        return new UninstallOutcome { Status = status, Detail = detail };
    }

    // ---------- 原生（非 MSI） ----------

    private static UninstallOutcome UninstallNative(InstalledApp app, UninstallMode mode, Action<string>? log, CancellationToken ct)
    {
        string? cmd = mode == UninstallMode.Silent
            ? (!string.IsNullOrWhiteSpace(app.QuietUninstallString) ? app.QuietUninstallString : app.UninstallString)
            : app.UninstallString;
        if (string.IsNullOrWhiteSpace(cmd))
            return new UninstallOutcome { Status = UninstallStatus.NotSupported, Detail = "没有官方卸载程序（UninstallString 缺失）。" };

        // msiexec /X{GUID} 形式的卸载串（WindowsInstaller 未标记的情况）
        var msiMatch = MsiArgsRegex().Match(cmd);
        if (msiMatch.Success)
        {
            string msiexec = Path.Combine(Environment.SystemDirectory, "msiexec.exe");
            string guid = msiMatch.Groups[1].Value;
            string args = mode == UninstallMode.Silent ? $"/x {{{guid}}} /qn /norestart" : $"/x {{{guid}}}";
            log?.Invoke($"    执行：msiexec {args}");
            int? code = RunProcess(msiexec, args, MsiTimeoutMs, killOnCancel: false, ct);
            if (code == null)
                return new UninstallOutcome { Status = UninstallStatus.StillRegistered, Detail = "等待 msiexec 完成超时或已取消（未强制终止），请重新扫描确认状态。" };
            if (code != 0 && code != 3010)
                return new UninstallOutcome { Status = UninstallStatus.Failed, Detail = $"msiexec 返回错误码 {code}。" };
            bool msiRemoved = WaitForRemoval(app, ct);
            return new UninstallOutcome
            {
                Status = msiRemoved ? UninstallStatus.Success : (code == 3010 ? UninstallStatus.RebootRequired : UninstallStatus.StillRegistered),
                Detail = msiRemoved ? "注册表条目已消失。" : "注册表条目仍存在（可能未卸载干净）。"
            };
        }

        string expanded = Environment.ExpandEnvironmentVariables(cmd);
        var (fileName, arguments) = ParseCommandLine(expanded);
        if (string.IsNullOrWhiteSpace(fileName))
            return new UninstallOutcome { Status = UninstallStatus.NotSupported, Detail = "无法解析卸载命令。" };

        log?.Invoke($"    执行：{fileName} {arguments}");
        int? exit = RunProcess(fileName, arguments, timeoutMs: 0, killOnCancel: false, ct);
        if (exit == null)
            return new UninstallOutcome { Status = UninstallStatus.StillRegistered, Detail = "等待卸载程序完成超时或已取消（未强制终止），请重新扫描确认状态。" };
        if (exit != 0)
        {
            log?.Invoke($"    卸载程序退出码 {exit}，继续等待注册表条目消失...");
        }

        bool removed = WaitForRemoval(app, ct);
        return new UninstallOutcome
        {
            Status = removed ? UninstallStatus.Success : UninstallStatus.StillRegistered,
            Detail = removed
                ? "注册表条目已消失。"
                : "卸载程序已退出但注册表条目仍存在（可能未卸载干净或仍在后台运行）。"
        };
    }

    // ---------- UWP ----------

    private static UninstallOutcome UninstallUwp(InstalledApp app, Action<string>? log, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(app.PackageFullName))
            return new UninstallOutcome { Status = UninstallStatus.NotSupported, Detail = "缺少包全名。" };

        string verifyScript =
            PowerShellRunner.Prelude +
            "$ErrorActionPreference='SilentlyContinue';" +
            $"$all = Get-AppxPackage{(app.IsOtherUserPackage ? " -AllUsers" : string.Empty)} | Where-Object {{ $_.PackageFullName -eq '{app.PackageFullName}' }};" +
            "if ($all) { 'True' } else { 'False' }";

        // 以包全名卸载（包名可能重复，FullName 唯一），再用同一 FullName 复验；
        // 全机（其他用户）安装的包需附加 -AllUsers
        string script =
            PowerShellRunner.Prelude +
            "$ErrorActionPreference='Continue';" +
            $"Remove-AppxPackage -Package '{app.PackageFullName}'{(app.IsOtherUserPackage ? " -AllUsers" : string.Empty)};" +
            "if ($?) { 'OK' } else { 'FAIL' }";

        log?.Invoke($"    执行：Remove-AppxPackage -Package {app.PackageFullName}{(app.IsOtherUserPackage ? " -AllUsers" : string.Empty)}");
        var (code, output, err) = SoftwareScanner.RunPowerShell(script, UwpTimeoutMs, killOnTimeout: false, ct);
        if (code != 0)
            return new UninstallOutcome
            {
                Status = UninstallStatus.Failed,
                Detail = $"Remove-AppxPackage 失败（退出码 {code}）：{Truncate(err, 300)}{Truncate(output, 200)}"
            };

        // 复验：包是否已消失
        var (vCode, vOut, _) = SoftwareScanner.RunPowerShell(verifyScript, 60_000, killOnTimeout: true, ct);
        bool stillThere = vCode == 0 && vOut.Trim().Equals("True", StringComparison.OrdinalIgnoreCase);
        return new UninstallOutcome
        {
            Status = stillThere ? UninstallStatus.StillRegistered : UninstallStatus.Success,
            Detail = stillThere
                ? "包仍存在：预装（全员）应用可能需在“设置 → 应用”中卸载，或稍后重试。"
                : "包已移除。"
        };
    }

    // ---------- 复验 ----------

    /// <summary>轮询等待注册表条目消失；返回是否已消失。</summary>
    private static bool WaitForRemoval(InstalledApp app, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < VerifyTimeoutMs)
        {
            if (ct.IsCancellationRequested) return false;
            if (!SoftwareScanner.IsEntryStillRegistered(app)) return true;
            Thread.Sleep(VerifyPollMs);
        }
        return !SoftwareScanner.IsEntryStillRegistered(app);
    }

    // ---------- 进程启动 ----------

    /// <summary>
    /// 启动进程并等待退出。timeoutMs &gt; 0 表示等待上限；取消/超时不会杀死卸载类子进程
    /// （中途终止卸载程序属于危险操作），仅放弃等待并返回 null。
    /// </summary>
    private static int? RunProcess(string fileName, string arguments, int timeoutMs, bool killOnCancel, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = true,          // 卸载程序可能触发 UAC 子进程并创建窗口
            CreateNoWindow = false,
        };
        using var p = Process.Start(psi);
        if (p == null) throw new InvalidOperationException($"无法启动卸载程序：{fileName}");

        var sw = Stopwatch.StartNew();
        while (!p.HasExited)
        {
            if (ct.IsCancellationRequested)
            {
                if (killOnCancel)
                {
                    try { p.Kill(); } catch (Exception) { }
                }
                return null;    // 不杀子进程（卸载中被打断是危险操作）
            }
            if (timeoutMs > 0 && sw.ElapsedMilliseconds > timeoutMs)
                return null;
            p.WaitForExit(300);
        }
        return p.ExitCode;
    }

    // ---------- 命令行解析（CommandLineToArgvW，避免 cmd 拼接注入） ----------

    /// <summary>解析命令行（CommandLineToArgvW 规则），返回可执行文件与其余参数；供卸载执行与残留扫描共用。</summary>
    internal static (string FileName, string Arguments) ParseCommandLine(string commandLine)
    {
        IntPtr argv = CommandLineToArgvW(commandLine, out int argc);
        if (argv == IntPtr.Zero || argc <= 0) return (string.Empty, string.Empty);

        try
        {
            var args = new string[argc];
            for (int i = 0; i < argc; i++)
            {
                IntPtr p = Marshal.ReadIntPtr(argv, i * IntPtr.Size);
                args[i] = Marshal.PtrToStringUni(p) ?? string.Empty;
            }
            string fileName = args[0].Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(fileName)) return (string.Empty, string.Empty);
            string rest = string.Join(" ", args.Skip(1).Select(QuoteArg));
            return (fileName, rest);
        }
        finally
        {
            LocalFree(argv);
        }
    }

    private static string QuoteArg(string s)
    {
        if (string.IsNullOrEmpty(s)) return "\"\"";
        if (!s.Contains(' ') && !s.Contains('\t') && !s.Contains('"')) return s;
        var sb = new System.Text.StringBuilder();
        sb.Append('"');
        int backslashes = 0;
        foreach (char c in s)
        {
            if (c == '\\') { backslashes++; }
            else if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
            }
            else
            {
                if (backslashes > 0) { sb.Append('\\', backslashes); backslashes = 0; }
                sb.Append(c);
            }
        }
        if (backslashes > 0) sb.Append('\\', backslashes * 2);
        sb.Append('"');
        return sb.ToString();
    }

    private static string Truncate(string s, int max) => string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= max ? s : s[..max] + "...");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW([MarshalAs(UnmanagedType.LPWStr)] string lpCmdLine, out int pNumArgs);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    [GeneratedRegex(@"(?i)\bmsiexec\.exe\b[^/]*/[IX]\{([0-9a-fA-F\-]{36})\}")]
    private static partial Regex MsiArgsRegex();
}
