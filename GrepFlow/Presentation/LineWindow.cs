using System.Text;

namespace GrepFlow.Presentation;

public sealed record LineWindowResult(string Text, int MatchStart, int MatchLength);

public sealed class LineWindow
{
    private const int MaxLength = 110;
    private const int LeadingContext = 20;
    private const char Ellipsis = '…';

    public LineWindowResult Create(string line, int matchStart, int matchLength)
    {
        var text = line.Replace('\t', ' ');

        var indent = CountLeadingWhitespace(text);
        text = text[indent..];

        var start = Math.Clamp(matchStart - indent, 0, text.Length);
        var length = Math.Clamp(matchLength, 0, text.Length - start);

        if (text.Length <= MaxLength) return new LineWindowResult(text, start, length);

        var windowStart = Math.Max(0, start - LeadingContext);
        if (windowStart + MaxLength > text.Length) windowStart = Math.Max(0, text.Length - MaxLength);
        var windowEnd = Math.Min(text.Length, windowStart + MaxLength);

        var builder = new StringBuilder(MaxLength + 2);
        var shift = -windowStart;

        if (windowStart > 0)
        {
            builder.Append(Ellipsis);
            shift++;
        }

        builder.Append(text, windowStart, windowEnd - windowStart);
        if (windowEnd < text.Length) builder.Append(Ellipsis);

        var cropped = builder.ToString();
        var croppedStart = Math.Clamp(start + shift, 0, cropped.Length);
        var croppedLength = Math.Clamp(length, 0, cropped.Length - croppedStart);
        return new LineWindowResult(cropped, croppedStart, croppedLength);
    }

    private static int CountLeadingWhitespace(string text)
    {
        var index = 0;
        while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
        return index;
    }
}
