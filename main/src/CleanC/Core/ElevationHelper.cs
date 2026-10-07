using System.Diagnostics;
using System.Security.Principal;

namespace CleanC.Core;

/// <summary>管理员权限检测与以管理员身份重启自身。</summary>
public static class ElevationHelper
{
    public static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 以管理员身份重启当前程序。**不抛异常**：用户取消 UAC、没有管理员凭据、exe 在网络盘上
    /// 都是常见情况，调用方需要能继续走"跳过需要管理员的项"这条路，而不是让异常冒到 UI 线程崩掉。
    /// </summary>
    /// <param name="extraArgs">附加命令行参数（如 <c>--open=disk</c>），会原样传给新实例。</param>
    /// <param name="error">失败原因（成功时为空）。</param>
    public static bool TryRestartElevated(string? extraArgs, out string error)
    {
        error = string.Empty;

        string exe;
        try
        {
            exe = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定进程路径。");
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        // runas 无法从网络路径启动：这类环境必须先复制到本地磁盘
        if (exe.StartsWith(@"\\", StringComparison.Ordinal))
        {
            error = "程序位于网络路径上，Windows 无法从网络路径提权启动。" +
                    "请先把 CleanC.exe 复制到本地磁盘（如桌面）再运行。";
            return false;
        }

        var args = new List<string>();
        if (!string.IsNullOrWhiteSpace(extraArgs)) args.Add(extraArgs.Trim());
        // 关键：提权后进程属于**另一个账户**，必须把"当前登录用户"的配置目录显式带过去，
        // 否则新实例会去清管理员账户的缓存、把说明书写进管理员的桌面。
        args.Add($"--user-profile=\"{UserContext.Profile}\"");

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = string.Join(' ', args),
            Verb = "runas",
            UseShellExecute = true,
        };
        try
        {
            string? dir = Path.GetDirectoryName(exe);
            if (!string.IsNullOrEmpty(dir)) psi.WorkingDirectory = dir;
        }
        catch (Exception) { /* 忽略 */ }

        try
        {
            Process.Start(psi);
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // 1223 = ERROR_CANCELLED：用户在 UAC 对话框上点了「否」
            error = ex.NativeErrorCode == 1223
                ? "已取消管理员授权（UAC），未做任何改动。"
                : $"无法提权启动：{ex.Message}";
            return false;
        }
        catch (Exception ex)
        {
            error = $"无法提权启动：{ex.Message}";
            return false;
        }
    }

    /// <summary>停止/启动 Windows 服务（用于 SoftwareDistribution 清理前后）。</summary>
    public static void ControlWindowsService(string action, string serviceName)
    {
        try
        {
            // 用绝对路径：CreateProcess 的查找顺序里「本程序所在目录」排在系统目录之前，
            // 裸 "sc.exe" 会被同目录下的同名文件截胡。
            string sc = Path.Combine(Environment.SystemDirectory, "sc.exe");
            var psi = new ProcessStartInfo
            {
                FileName = File.Exists(sc) ? sc : "sc.exe",
                Arguments = $"{action} {serviceName}",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p != null && !p.WaitForExit(20000))
            {
                try { p.Kill(); } catch (Exception) { /* 忽略 */ }
            }
        }
        catch (Exception) { /* 服务操作失败时忽略，继续删除 */ }
    }
}
