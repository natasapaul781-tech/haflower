namespace CleanC.Core;

/// <summary>字节数格式化。</summary>
public static class SizeFormatter
{
    /// <summary>
    /// 把字节数格式化成 “1.5 GB”。
    ///
    /// **必须用不变区域性**：这个字符串会被界面反解析回数值（<c>MainForm.ParseSize</c>）、
    /// 写进 CSV 与说明书。若跟随系统区域，德语/法语机器上会输出 <c>1,5 GB</c>，
    /// 既与中文界面混排，也会让 CSV 多出一个分隔逗号、并让反解析在区域性不一致时静默归零。
    /// </summary>
    public static string Format(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < units.Length - 1)
        {
            v /= 1024;
            i++;
        }
        return v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " " + units[i];
    }
}
