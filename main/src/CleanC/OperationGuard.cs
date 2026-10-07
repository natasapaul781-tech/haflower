using System.Drawing;
using System.Windows.Forms;

namespace CleanC;

/// <summary>
/// 「危险操作进行中」登记器 + 退出拦截。
///
/// 为什么需要它：分区收缩 / 删除 / 扩容、以及正在运行的卸载程序，都**不能让进程中途退出**。
/// 强杀进程可能留下半完成状态——分区表条目已删而空间未并入（磁盘上多出一段"未分配"）、
/// 卷被截断、注册表与文件不一致等，且都**无法自动恢复**。而窗口右上角的 ×、
/// Alt+F4、任务栏右键关闭，都是用户随手就会做的动作。
///
/// 设计要点：
/// ① 只拦"真的危险"的操作。只读扫描、清理临时文件这类可以安全中断的操作**不登记**，
///    用户随时能取消、能关窗——否则会变成"程序卡住还不让关"的糟糕体验；
/// ② 用 <see cref="IDisposable"/> 登记，确保异常路径下也会注销（不会永久锁死窗口）；
/// ③ 拦不住"任务管理器结束进程"——那是用户明确选择承担风险，我们能做的是把后果讲清楚。
/// </summary>
public static class OperationGuard
{
    private static readonly object Sync = new();
    private static readonly Dictionary<long, string> Running = new();
    private static long _nextId;
    private static int _closeAttempts;

    /// <summary>当前是否有不可中断的危险操作在跑。</summary>
    public static bool HasCritical
    {
        get { lock (Sync) return Running.Count > 0; }
    }

    /// <summary>当前危险操作的描述（没有则为空串）。</summary>
    public static string CurrentDescription
    {
        get { lock (Sync) return Running.Count == 0 ? string.Empty : Running.Values.Last(); }
    }

    /// <summary>登记一个不可中断的危险操作；用 using 包住，结束后自动注销。</summary>
    public static IDisposable Begin(string description)
    {
        long id;
        lock (Sync)
        {
            id = ++_nextId;
            Running[id] = string.IsNullOrWhiteSpace(description) ? "危险操作" : description;
            _closeAttempts = 0;
        }
        return new Scope(id);
    }

    private sealed class Scope(long id) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            lock (Sync)
            {
                Running.Remove(id);
                if (Running.Count == 0) _closeAttempts = 0;
            }
        }
    }

    /// <summary>
    /// 测试专用：置 true 后 <see cref="ConfirmExit"/> 不弹窗，直接返回"拒绝关闭"。
    /// （自动化测试里没法点 MessageBox，但必须能验证"危险操作期间关不掉"这条行为。）
    /// </summary>
    public static bool SuppressPromptForTest { get; set; }

    /// <summary>
    /// 关闭窗口前的统一闸门：危险操作未结束时返回 false（调用方应把 FormClosingEventArgs.Cancel 置 true）。
    /// 会弹窗说明"为什么现在不能关"以及强行退出的后果。
    /// </summary>
    public static bool ConfirmExit(IWin32Window owner)
    {
        string desc;
        int attempts;
        lock (Sync)
        {
            if (Running.Count == 0) return true;
            desc = Running.Values.Last();
            attempts = ++_closeAttempts;
        }

        if (SuppressPromptForTest) return false;

        string extra = attempts >= 3
            ? $"\n\n如果长时间没有任何进展，你可以在任务管理器中结束本程序（{ExeName()}）——" +
              "但强制结束有可能导致数据损坏，请只在确认它真的卡死时这么做。"
            : string.Empty;

        MessageBox.Show(owner,
            "现在不能退出。\n\n" +
            $"正在执行：{desc}\n\n" +
            "这类操作中途被强制结束，可能造成：\n" +
            "  · 分区表与文件系统不一致（例如分区已删除、空间却没并入，磁盘上多出一段未分配空间）；\n" +
            "  · 卷被截断、文件损坏；\n" +
            "  · 卸载到一半的软件在注册表与磁盘上状态不一致。\n" +
            "这些情况都无法自动恢复。\n\n" +
            "请等待它完成（窗口中央会显示进度动画），完成后即可正常关闭。" + extra,
            "操作进行中，暂不能退出", MessageBoxButtons.OK, MessageBoxIcon.Warning);

        return false;
    }

    /// <summary>测试用：读取内部状态（不弹窗）。</summary>
    public static int RunningCount
    {
        get { lock (Sync) return Running.Count; }
    }

    /// <summary>当前可执行文件名（程序被改名后提示文案要跟着变）。</summary>
    private static string ExeName()
    {
        try
        {
            string? p = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(p)) return Path.GetFileName(p);
        }
        catch (Exception) { /* 忽略 */ }
        return "本程序";
    }
}
