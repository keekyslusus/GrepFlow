using System.IO;
using System.Text;
using System.Text.Json;

namespace GrepFlow.Search;

public sealed class RipgrepJsonParser
{
    private const int MaxLineLength = 1024;

    // --max-columns has no effect on --json output, so a minified file that is one multi-megabyte
    // line would be parsed in full for every match on it. Such records are dropped unparsed
    private const int MaxJsonLineLength = 64 * 1024;

    // returns null for every record that is not a usable match
    public RipgrepMatch? Parse(string jsonLine, string folder)
    {
        if (string.IsNullOrWhiteSpace(jsonLine)) return null;
        if (jsonLine.Length > MaxJsonLineLength) return null;

        try
        {
            using var document = JsonDocument.Parse(jsonLine);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "match") return null;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return null;

            // non UTF-8 paths and lines arrive as {"bytes": "<base64>"} those records are skipped
            var relativePath = ReadText(data, "path");
            var rawLine = ReadText(data, "lines");
            if (relativePath is null || rawLine is null) return null;

            var lineNumber = data.TryGetProperty("line_number", out var number) && number.ValueKind == JsonValueKind.Number
                ? number.GetInt32()
                : 0;

            var (startByte, endByte) = ReadFirstSubmatch(data);

            var lineText = Truncate(rawLine.TrimEnd('\n', '\r'));
            var matchStart = Math.Min(ByteOffsetToCharIndex(rawLine, startByte), lineText.Length);
            var matchEnd = Math.Min(ByteOffsetToCharIndex(rawLine, endByte), lineText.Length);
            var matchLength = Math.Max(0, matchEnd - matchStart);

            var normalizedRelative = Normalize(relativePath);
            var absolutePath = ToAbsolute(folder, normalizedRelative);

            return new RipgrepMatch(absolutePath, normalizedRelative, lineNumber, lineText, matchStart, matchLength);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadText(JsonElement data, string propertyName)
    {
        if (!data.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Object) return null;
        if (!property.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String) return null;
        return text.GetString();
    }

    private static (int Start, int End) ReadFirstSubmatch(JsonElement data)
    {
        if (!data.TryGetProperty("submatches", out var submatches) || submatches.ValueKind != JsonValueKind.Array) return (0, 0);

        foreach (var submatch in submatches.EnumerateArray())
        {
            if (submatch.ValueKind != JsonValueKind.Object) continue;

            var start = submatch.TryGetProperty("start", out var startValue) && startValue.ValueKind == JsonValueKind.Number
                ? startValue.GetInt32()
                : 0;
            var end = submatch.TryGetProperty("end", out var endValue) && endValue.ValueKind == JsonValueKind.Number
                ? endValue.GetInt32()
                : start;

            return (start, Math.Max(start, end));
        }

        return (0, 0);
    }

    // `submatches` offsets count bytes inside the UTF-8 encoded line, while highlight data needs
    // C# character indexes. On non-ASCII text the two differ immediately
    private static int ByteOffsetToCharIndex(string text, int byteOffset)
    {
        if (byteOffset <= 0) return 0;

        var bytes = Encoding.UTF8.GetBytes(text);
        if (byteOffset >= bytes.Length) return text.Length;

        return Encoding.UTF8.GetString(bytes, 0, byteOffset).Length;
    }

    private static string Truncate(string line)
        => line.Length <= MaxLineLength ? line : line[..MaxLineLength];

    private static string Normalize(string relativePath)
    {
        var path = relativePath.Replace('/', '\\');
        return path.StartsWith(".\\", StringComparison.Ordinal) ? path[2..] : path;
    }

    private static string ToAbsolute(string folder, string relativePath)
    {
        try
        {
            return Path.GetFullPath(Path.Combine(folder, relativePath));
        }
        catch (ArgumentException)
        {
            return relativePath;
        }
        catch (PathTooLongException)
        {
            return relativePath;
        }
    }
}
