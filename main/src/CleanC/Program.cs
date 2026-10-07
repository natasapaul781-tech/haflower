namespace CleanC;

using CleanC.Core;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        OperationLog.LogSystem(UserContext.Describe());
        OperationLog.LogSystem($"权限：{(ElevationHelper.IsElevated() ? "管理员" : "普通用户")}（按需提权）");
        Application.Run(new MainForm(args));
    }
}
