using System.Globalization;

namespace CleanC.Core;

/// <summary>
/// 统一的日期时间文本（**一律不变区域性 / 公历**）。
///
/// 为什么必须固定：部分系统语言的默认日历不是公历——泰语（th-TH）默认佛历、
/// 阿拉伯语（ar-SA）默认回历，`DateTime.Now:yyyyMMdd` 会写出 <c>25681231</c> 这类年份，
/// 直接破坏导出文件名、日志时间戳与文本排序；同一份日志在不同电脑上也不可比较。
/// </summary>
public static class TimeText
{
    /// <summary>标准时间戳：<c>2026-10-06 01:23:45</c>。</summary>
    public const string StampFormat = "yyyy-MM-dd HH:mm:ss";

    /// <summary>文件名用的紧凑时间戳：<c>20261006-012345</c>。</summary>
    public const string CompactFormat = "yyyyMMdd-HHmmss";

    /// <summary>日期：<c>2026-10-06</c>。</summary>
    public const string DateFormat = "yyyy-MM-dd";

    /// <summary>到分钟：<c>2026-10-06 01:23</c>。</summary>
    public const string MinuteFormat = "yyyy-MM-dd HH:mm";

    public static string StampNow() => Format(DateTime.Now, StampFormat);

    public static string CompactNow() => Format(DateTime.Now, CompactFormat);

    public static string Stamp(DateTime value) => Format(value, StampFormat);

    public static string Minute(DateTime value) => Format(value, MinuteFormat);

    /// <summary>日期文本；无值返回 <c>—</c>。</summary>
    public static string Date(DateTime? value) =>
        value is { } v ? Format(v, DateFormat) : "—";

    public static string Format(DateTime value, string format) =>
        value.ToString(format, CultureInfo.InvariantCulture);
}
