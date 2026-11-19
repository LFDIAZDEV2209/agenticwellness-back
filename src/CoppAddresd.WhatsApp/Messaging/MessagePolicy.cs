using System.Globalization;
using System.Text.RegularExpressions;
using CoppAddresd.Application.WhatsApp.Messaging;

namespace CoppAddresd.WhatsApp.Messaging;

public static class MessagePolicy
{
    public const int MaxTextLength = 4096;

    public static WhatsAppFailureKind Classify(int httpStatus, int? metaCode) =>
        metaCode == 190 ? WhatsAppFailureKind.Permanent
        : httpStatus == 429 || httpStatus >= 500 || metaCode is 1 or 2 or 4 or 80007 or 130429
            ? WhatsAppFailureKind.Transient
        : WhatsAppFailureKind.Permanent;

    public static string MaskPhone(string phone)
    {
        var digits = new string(phone.Where(char.IsAsciiDigit).ToArray());
        return digits.Length >= 7 ? digits[..3] + "***" + digits[^3..] : "***";
    }

    public static string[] Split(string text)
    {
        if (text.Length <= MaxTextLength)
            return text.Length == 0 ? [] : [text];

        var boundaries = StringInfo.ParseCombiningCharacters(text);
        List<string> parts = [];
        for (var offset = 0; offset < text.Length;)
        {
            if (text.Length - offset <= MaxTextLength)
            {
                parts.Add(text[offset..]);
                break;
            }

            var limit = offset + MaxTextLength;
            var index = Array.BinarySearch(boundaries, limit);
            var end = index >= 0 ? limit : boundaries[~index - 1];
            if (end <= offset)
            {
                // Un solo grafema puede superar el límite. En ese caso se conservan los escalares.
                end = ScalarBoundary(text, limit);
            }
            else
            {
                end = PreferNaturalBreak(text, boundaries, offset, end);
            }
            parts.Add(text[offset..end]);
            offset = end;
        }
        return parts.ToArray();
    }

    private static int PreferNaturalBreak(string text, int[] boundaries, int offset, int end)
    {
        var paragraph = -1;
        var line = -1;
        var word = -1;
        for (var i = end; i > offset + MaxTextLength / 2; i--)
        {
            if (!char.IsWhiteSpace(text[i - 1]) || Array.BinarySearch(boundaries, i) < 0)
                continue;
            if (text[i - 1] == '\n')
            {
                if (text[i - 2] == '\n')
                {
                    paragraph = i;
                    break;
                }
                if (line < 0)
                    line = i;
            }
            if (word < 0)
                word = i;
        }
        return paragraph >= 0 ? paragraph : line >= 0 ? line : word >= 0 ? word : end;
    }

    private static int ScalarBoundary(string text, int end) =>
        char.IsHighSurrogate(text[end - 1]) && char.IsLowSurrogate(text[end]) ? end - 1 : end;

    internal static string[] ResumeLegacyReply(string text)
    {
        text = NormalizeLegacy(text);
        List<string> parts = [];
        for (var offset = 0; offset < text.Length; )
        {
            var length = Math.Min(MaxTextLength, text.Length - offset);
            if (offset + length < text.Length && char.IsHighSurrogate(text[offset + length - 1]))
                length--;
            parts.Add(text.Substring(offset, length));
            offset += length;
        }
        return parts.ToArray();
    }

    public static string NormalizeForWhatsApp(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        char? fence = null;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var marker = Fence.Match(line);
            if (marker.Success && (fence is null || marker.Value.TrimStart()[0] == fence))
            {
                fence = fence is null ? marker.Value.TrimStart()[0] : null;
                lines[i] = "";
                continue;
            }
            if (fence is not null)
                continue;
            if (TableSeparator.IsMatch(line))
            {
                lines[i] = "";
                continue;
            }
            var trimmed = line.Trim();
            if (trimmed.Length >= 2 && trimmed.StartsWith('|') && trimmed.EndsWith('|'))
                line = string.Join(" · ", trimmed[1..^1].Split('|', StringSplitOptions.TrimEntries));
            line = MarkdownLink.Replace(line, "$1 ($2)");
            line = Regex.Replace(line, @"__(.+?)__", "*$1*");
            line = Regex.Replace(line, @"~~(.+?)~~", "~$1~");
            lines[i] = FormatLine(line, legacy: false);
        }
        return string.Join("\n", lines);
    }

    private static readonly Regex Fence = new(@"^[ \t]*(?:`{3,}|~{3,})", RegexOptions.NonBacktracking);
    private static readonly Regex TableSeparator = new(@"^\s*\|?[ \t]*:?-{3,}:?[ \t]*(?:\|[ \t]*:?-{3,}:?[ \t]*)+\|?\s*$", RegexOptions.NonBacktracking);
    private static readonly Regex MarkdownLink = new(@"!?\[([^\[\]]+)\]\(([^\s()]*(?:\([^()\s]*\)[^\s()]*)*)\)", RegexOptions.NonBacktracking);

    private static string NormalizeLegacy(string text) =>
        string.Join("\n", text.Replace("\r\n", "\n").Split('\n').Select(line => FormatLine(line, legacy: true)));

    private static string FormatLine(string line, bool legacy)
    {
        if (Regex.IsMatch(line, @"^\s*([-_*]\s*){3,}$"))
            return string.Empty;
        var header = Regex.Match(line, @"^\s*#{1,6}\s+(.+?)\s*$");
        if (header.Success)
        {
            var title = header.Groups[1].Value;
            if (!legacy)
                title = Regex.Replace(title, @"\*\*(.+?)\*\*", "$1");
            return "*" + title + "*";
        }
        return Regex.Replace(line, @"\*\*(.+?)\*\*", "*$1*");
    }
}
