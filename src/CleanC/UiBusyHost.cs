using System.Drawing;
using System.Windows.Forms;

namespace CleanC;

/// <summary>
/// 把「处理中」遮罩与「任务进行中不许/不宜关闭」这两件事绑定到一个窗体上。
///
/// 用法（每个有长操作的窗体三步）：
/// <code>
///   private UiBusyHost _busyHost = null!;                    // ① 字段
///   _busyHost = new UiBusyHost(this);                        // ② InitializeUi 末尾
///   _busyHost.Begin(msg, BusyRisk.Critical, () =&gt; Cancel()); // ③ 长任务开始处
///   _busyHost.End();                                         //    任务结束处（finally 里）
/// </code>
///
/// 关闭闸门也由本类统一挂载，按风险分档处理：
/// <list type="bullet">
///   <item><see cref="BusyRisk.Critical"/>：FormClosing 直接拒绝，并弹窗说明后果（<see cref="OperationGuard"/>）；</item>
///   <item><see cref="BusyRisk.Interrupt"/>：先确认一次"现在关会中断操作"，用户坚持才放行；</item>
///   <item><see cref="BusyRisk.Safe"/> / 空闲：不拦。</item>
/// </list>
/// </summary>
public sealed class UiBusyHost
{
    private readonly Form _form;
    private readonly BusyOverlay _overlay;
    private IDisposable? _criticalScope;

    public UiBusyHost(Form form)
    {
        _form = form;

        // 遮罩不用 Dock（Dock=Fill 会参与停靠计算、把已有布局挤乱），
        // 手动铺满客户区 + Anchor 跟随缩放，显示时 BringToFront 盖在最上层。
        _overlay = new BusyOverlay();
        form.Controls.Add(_overlay);
        _overlay.Bounds = form.ClientRectangle;
        _overlay.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

        _form.FormClosing += OnFormClosing;
    }

    /// <summary>遮罩控件（测试/布局检查可直接访问）。</summary>
    public BusyOverlay Overlay => _overlay;

    /// <summary>当前是否正在显示处理中遮罩。</summary>
    public bool IsBusy => _overlay.IsBusy;

    /// <summary>当前是否处于"不可中断的危险操作"期间。</summary>
    public bool IsCritical => _criticalScope is not null;

    /// <summary>开始显示处理中动画。</summary>
    /// <param name="message">主文案（正在做什么）。</param>
    /// <param name="risk">
    /// 风险等级，见 <see cref="BusyRisk"/>。<see cref="BusyRisk.Critical"/> 时登记到
    /// <see cref="OperationGuard"/>（拒绝关闭窗口）、不提供取消按钮、显示红色警示。
    /// </param>
    /// <param name="onCancel">可取消操作被点「取消」时的回调。</param>
    public void Begin(string message, BusyRisk risk, Action? onCancel)
    {
        bool critical = risk == BusyRisk.Critical;

        // 无论本次是否危险操作，都先注销上一次的登记：否则「Critical → (未 End) → Safe」
        // 会让 OperationGuard 里的登记永久残留，表现为**窗口再也关不掉**。
        _criticalScope?.Dispose();
        _criticalScope = null;

        _overlay.CancelAction = critical ? null : onCancel;
        _overlay.Begin(message, risk, allowCancel: !critical);

        if (!critical) return;

        _criticalScope = OperationGuard.Begin(message.TrimEnd('.', '…', '。'));
    }

    /// <summary>更新动画上的文案（进度变化时调用）。</summary>
    public void Update(string message) => _overlay.Update(message);

    /// <summary>结束显示并注销危险操作登记。异常路径也必须调用（放在 finally 里）。</summary>
    public void End()
    {
        _overlay.End();
        _criticalScope?.Dispose();
        _criticalScope = null;
        _overlay.CancelAction = null;
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        // 关机/注销/任务管理器结束进程时不要把窗口挡住：这类关闭没人能点「确定」，
        // 强行拦住只会让系统等超时后强杀进程——那正是本闸门想避免的"半完成"状态。
        if (e.CloseReason is CloseReason.WindowsShutDown
            or CloseReason.TaskManagerClosing
            or CloseReason.ApplicationExitCall)
            return;

        // ① 危险操作：一律拒绝关闭，弹窗讲清后果
        if (!OperationGuard.ConfirmExit(_form))
        {
            e.Cancel = true;
            return;
        }

        // ② 会改文件但可安全中断：提醒一次，用户坚持就放行
        if (IsBusy && _overlay.Risk == BusyRisk.Interrupt)
        {
            DialogResult answer;
            try
            {
                answer = MessageBox.Show(_form,
                    "任务还在执行中。\n\n" +
                    "现在关闭会中断它：已经处理完的部分会保留，没轮到的部分保持原样，\n" +
                    "下次运行本程序时可以重新执行——不会造成损坏，但会停在半途。\n\n" +
                    "确定要中断并退出吗？",
                    "任务进行中", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2);
            }
            catch (Exception)
            {
                return;   // 弹窗本身失败（会话注销等）时放行，不要卡住关闭流程
            }
            if (answer != DialogResult.Yes) e.Cancel = true;
        }
    }
}
