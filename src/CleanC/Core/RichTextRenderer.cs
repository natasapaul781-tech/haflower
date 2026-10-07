using System.Text;

namespace CleanC.Core;

/// <summary>
/// 把带**极简标记**的长文档渲染成 RTF，供只读 RichTextBox 显示；另提供"去掉标记"的纯文本版本。
///
/// 为什么需要它：Core 层生成的长文档（补救清单、执行报告、说明书、阻断说明）用 <c>**…**</c> 表达强调，
/// 但
/// ① 用 <c>Label</c>/<c>TextBox</c> 直接显示会把星号原样露出来（用户看到的是"**可移动的文件**"）；
/// ② 导出成 .txt 的说明书里出现星号也不合适。
/// 因此：界面用 <see cref="BuildRtf"/> 渲染成真正的粗体，纯文本出口（.txt、剪贴板）用
/// <see cref="StripMarkdown"/> 去掉标记。
///
/// 支持范围刻意保持极小：<c>**粗体**</c> + 换行 + 制表符。其余字符按 RTF 规则转义。
/// </summary>
public static class RichTextRenderer
{
    /// <summary>RTF 头：等宽字体 + 指定字号（<paramref name="fontSizePt"/> 以磅为单位）。</summary>
    private static string Header(float fontSizePt)
    {
        int halfPoints = Math.Max(12, (int)Math.Round(fontSizePt * 2));
        return @"{\rtf1\ansi\ansicpg936\deff0{\fonttbl{\f0\fmodern\fcharset134 Consolas;}}" +
               $@"\f0\fs{halfPoints} ";
    }

    /// <summary>
    /// 生成 RTF。规则：
    /// · <c>\r\n</c> / <c>\r</c> / <c>\n</c> 统一视为一次换行 → <c>\par</c>（避免出现"每行后面多一个空行"）；
    /// · <c>**文字**</c> → 真正的粗体；
    /// · 未配对的 <c>**</c> 原样转义输出（不会让右侧整段变粗）；
    /// · <c>\</c> <c>{</c> <c>}</c> 按 RTF 规范转义；非 ASCII 用 <c>\uN?</c>（RTF 要求 16 位有符号）；
    /// · 制表符 → <c>\tab</c>。
    /// </summary>
    public static string BuildRtf(string? text, float fontSizePt = 9.5f)
    {
        string normalized = NormalizeNewlines(text);
        var sb = new StringBuilder(Header(fontSizePt));

        bool bold = false;
        int i = 0;
        while (i < normalized.Length)
        {
            char c = normalized[i];

            // **粗体** 标记处理：
            //  · 已在粗体中 → 这一对一定是收尾，切回常规；
            //  · 否则找配对的后半段，找到才切换（避免未配对标记把后面整段变粗）；
            //  · 找不到配对 → 原样输出两个星号（RTF 里 * 无需转义，也不能写成 \*：
            //    \* 在 RTF 里是“可忽略目标”引导符，会让解析器跳过后续内容）。
            if (c == '*' && i + 1 < normalized.Length && normalized[i + 1] == '*')
            {
                if (bold)
                {
                    bold = false;
                    sb.Append(@"\b0 ");
                    i += 2;
                    continue;
                }

                int close = normalized.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (close > i + 2)
                {
                    bold = true;
                    sb.Append(@"\b ");
                    i += 2;
                    continue;
                }

                sb.Append("**");
                i += 2;
                continue;
            }

            switch (c)
            {
                case '\n':
                    sb.Append(@"\par").Append('\n');
                    break;
                case '\t':
                    sb.Append(@"\tab ");
                    break;
                case '\\':
                    sb.Append(@"\\");
                    break;
                case '{':
                    sb.Append(@"\{");
                    break;
                case '}':
                    sb.Append(@"\}");
                    break;
                default:
                    if (c < 0x20)
                    {
                        // 其它控制字符直接丢弃（RTF 里没有对应表示）
                    }
                    else if (c <= 0x7E)
                    {
                        sb.Append(c);
                    }
                    else
                    {
                        // RTF 的 \uN 取 16 位有符号值
                        int code = c;
                        if (code > 32767) code -= 65536;
                        sb.Append(@"\u").Append(code).Append('?');
                    }
                    break;
            }
            i++;
        }

        if (bold) sb.Append(@"\b0 ");
        sb.Append('}');
        return sb.ToString();
    }

    /// <summary>
    /// 去掉极简标记，得到可放进 .txt / 剪贴板的纯文本。
    /// 同时统一换行为 <see cref="Environment.NewLine"/>（并修掉 <c>\r\r\n</c> 这类重复换行）。
    /// </summary>
    public static string StripMarkdown(string? text)
    {
        string normalized = NormalizeNewlines(text);
        return normalized.Replace("**", string.Empty).Replace("\n", Environment.NewLine);
    }

    /// <summary>把 <c>\r\n</c>、<c>\r</c>、<c>\n</c> 统一成单个 <c>\n</c>（消除重复换行）。</summary>
    public static string NormalizeNewlines(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                sb.Append('\n');
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;   // 吃掉 \r\n 的 \n
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    /// <summary>文本里是否含粗体标记（用于自检与断言）。</summary>
    public static bool HasBoldMarkup(string? text) =>
        !string.IsNullOrEmpty(text) && text.Contains("**", StringComparison.Ordinal);
}
