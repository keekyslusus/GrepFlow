using System.IO;
using System.Text;

namespace GrepFlow.Interop;

internal sealed class JetBrainsIdeLogReader
{
    private const int DefaultTailBytes = 1024 * 1024;
    private const string FrameMarker = "Setting project frame to Project(name=";
    private const string NameDelimiter = ", containerState=";
    private const string ComponentStoreMarker = "componentStore=";

    private readonly int _tailBytes;
    private readonly PluginLog? _log;
    private readonly Dictionary<string, LogState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _warnedFingerprints = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public JetBrainsIdeLogReader(PluginLog? log = null)
        : this(DefaultTailBytes, log)
    {
    }

    internal JetBrainsIdeLogReader(int tailBytes, PluginLog? log = null)
    {
        _tailBytes = Math.Max(1, tailBytes);
        _log = log;
    }

    internal long BytesRead { get; private set; }

    internal int LargestReadBytes { get; private set; }

    public string? TryResolveProjectPath(string logPath, string windowTitle)
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(logPath)) return null;

                if (!_states.TryGetValue(logPath, out var state))
                {
                    state = new LogState();
                    _states.Add(logPath, state);
                }

                Refresh(logPath, state);
                var projectName = MatchProjectName(state, windowTitle);
                if (projectName is not null)
                {
                    EnsureCompleteProjectHistory(logPath, state, projectName);
                }
                else if (!state.UnresolvedTitleSearchLengths.TryGetValue(windowTitle, out var searchedLength) ||
                         searchedLength != state.Offset)
                {
                    var matchedNames = SearchBackward(logPath, state, windowTitle, projectName: null);
                    foreach (var matchedName in matchedNames)
                        state.ProjectHistorySearchLengths[matchedName] = state.Offset;
                    state.UnresolvedTitleSearchLengths[windowTitle] = state.Offset;
                }

                projectName = MatchProjectName(state, windowTitle);
                return projectName is null ? null : ResolveUniqueExistingPath(state, projectName);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                WarnOnce(logPath, exception);
                return null;
            }
        }
    }

    private void Refresh(string logPath, LogState state)
    {
        var creationTimeUtc = File.GetCreationTimeUtc(logPath);
        var writeTimeUtc = File.GetLastWriteTimeUtc(logPath);

        using var stream = new FileStream(
            logPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        var length = stream.Length;
        var replaced = state.Initialized && creationTimeUtc != state.CreationTimeUtc;
        var rewrittenAtSameLength = state.Initialized && length == state.Offset && writeTimeUtc != state.WriteTimeUtc;
        if (replaced || length < state.Offset || rewrittenAtSameLength)
            state.Reset();

        if (state.Initialized && length == state.Offset)
        {
            state.WriteTimeUtc = writeTimeUtc;
            return;
        }

        var previousOffset = state.Offset;
        var wasInitialized = state.Initialized;
        var startsMidFile = !wasInitialized && length > _tailBytes;
        var start = state.Initialized ? state.Offset : Math.Max(0, length - _tailBytes);
        ReadForward(stream, state, start, length, startsMidFile);
        if (wasInitialized)
            AdvanceCompletedSearches(state, previousOffset);
        state.CreationTimeUtc = creationTimeUtc;
        state.WriteTimeUtc = writeTimeUtc;
        state.Initialized = true;
    }

    private void ReadForward(
        FileStream stream,
        LogState state,
        long start,
        long end,
        bool discardLeadingPartialLine)
    {
        var position = start;
        while (position < end)
        {
            var requested = (int)Math.Min(_tailBytes, end - position);
            var bytes = new byte[requested];
            var total = ReadAt(stream, position, bytes);
            if (total == 0) break;

            RecordRead(total);
            position += total;

            ReadOnlySpan<byte> incoming = bytes.AsSpan(0, total);
            if (discardLeadingPartialLine)
            {
                var firstNewline = incoming.IndexOf((byte)'\n');
                if (firstNewline < 0) continue;
                incoming = incoming[(firstNewline + 1)..];
                discardLeadingPartialLine = false;
            }

            ConsumeLines(state, incoming);
        }

        state.Offset = position;
    }

    private void EnsureCompleteProjectHistory(string logPath, LogState state, string projectName)
    {
        if (state.ProjectHistorySearchLengths.TryGetValue(projectName, out var searchedLength) &&
            searchedLength == state.Offset)
            return;

        SearchBackward(logPath, state, windowTitle: null, projectName);
        state.ProjectHistorySearchLengths[projectName] = state.Offset;
    }

    private HashSet<string> SearchBackward(
        string logPath,
        LogState state,
        string? windowTitle,
        string? projectName)
    {
        var matchedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var stream = new FileStream(
            logPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        var position = stream.Length;
        var rightFragment = Array.Empty<byte>();
        var findingCompleteLineEnd = true;

        while (position > 0)
        {
            var requested = (int)Math.Min(_tailBytes, position);
            position -= requested;

            var bytes = new byte[requested];
            var total = ReadAt(stream, position, bytes);
            if (total == 0) break;
            RecordRead(total);

            var combined = new byte[total + rightFragment.Length];
            bytes.AsSpan(0, total).CopyTo(combined);
            rightFragment.CopyTo(combined, total);

            var processEnd = combined.Length;
            if (findingCompleteLineEnd)
            {
                var lastNewline = combined.AsSpan().LastIndexOf((byte)'\n');
                if (lastNewline < 0) continue;

                processEnd = lastNewline + 1;
                findingCompleteLineEnd = false;
            }

            var processStart = 0;
            if (position > 0)
            {
                var firstNewline = combined.AsSpan(0, processEnd).IndexOf((byte)'\n');
                if (firstNewline < 0)
                {
                    rightFragment = combined[..processEnd];
                    continue;
                }

                processStart = firstNewline + 1;
                rightFragment = combined[..processStart];
            }

            ParseCompleteLines(
                combined.AsSpan(processStart, processEnd - processStart),
                (name, path) =>
                {
                    var matches = projectName is not null
                        ? string.Equals(name, projectName, StringComparison.OrdinalIgnoreCase)
                        : JetBrainsIdeProjectTitleParser.MatchKnownProjectName(windowTitle, [name]) is not null;
                    if (!matches) return;

                    AddMapping(state, name, path);
                    matchedNames.Add(name);
                });
        }

        return matchedNames;
    }

    private static void AdvanceCompletedSearches(LogState state, long previousOffset)
    {
        foreach (var projectName in state.ProjectHistorySearchLengths.Keys.ToArray())
        {
            if (state.ProjectHistorySearchLengths[projectName] == previousOffset)
                state.ProjectHistorySearchLengths[projectName] = state.Offset;
        }

        foreach (var title in state.UnresolvedTitleSearchLengths.Keys.ToArray())
        {
            if (state.UnresolvedTitleSearchLengths[title] == previousOffset)
                state.UnresolvedTitleSearchLengths[title] = state.Offset;
        }
    }

    private static string? MatchProjectName(LogState state, string windowTitle)
        => JetBrainsIdeProjectTitleParser.MatchKnownProjectName(windowTitle, state.PathsByProject.Keys);

    private static string? ResolveUniqueExistingPath(LogState state, string projectName)
    {
        if (!state.PathsByProject.TryGetValue(projectName, out var paths)) return null;

        string? unique = null;
        foreach (var path in paths)
        {
            if (!Directory.Exists(path)) continue;
            if (unique is not null && !string.Equals(unique, path, StringComparison.OrdinalIgnoreCase))
                return null;
            unique = path;
        }

        return unique;
    }

    private static int ReadAt(FileStream stream, long position, byte[] bytes)
    {
        stream.Seek(position, SeekOrigin.Begin);
        var total = 0;
        while (total < bytes.Length)
        {
            var read = stream.Read(bytes, total, bytes.Length - total);
            if (read == 0) break;
            total += read;
        }

        return total;
    }

    private void RecordRead(int bytes)
    {
        BytesRead += bytes;
        LargestReadBytes = Math.Max(LargestReadBytes, bytes);
    }

    private static void ConsumeLines(LogState state, ReadOnlySpan<byte> incoming)
    {
        var combined = new byte[state.Pending.Length + incoming.Length];
        state.Pending.CopyTo(combined, 0);
        incoming.CopyTo(combined.AsSpan(state.Pending.Length));

        var lineStart = 0;
        for (var index = 0; index < combined.Length; index++)
        {
            if (combined[index] != (byte)'\n') continue;

            var length = index - lineStart;
            if (length > 0 && combined[index - 1] == (byte)'\r') length--;
            ParseLine(state, Encoding.UTF8.GetString(combined, lineStart, length));
            lineStart = index + 1;
        }

        state.Pending = combined[lineStart..];
    }

    private static void ParseLine(LogState state, string line)
    {
        if (!TryParseLine(line, out var projectName, out var path)) return;
        AddMapping(state, projectName, path);
    }

    private static void ParseCompleteLines(
        ReadOnlySpan<byte> bytes,
        Action<string, string> parsedLine)
    {
        var lineStart = 0;
        for (var index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] != (byte)'\n') continue;

            var length = index - lineStart;
            if (length > 0 && bytes[index - 1] == (byte)'\r') length--;
            if (TryParseLine(Encoding.UTF8.GetString(bytes.Slice(lineStart, length)), out var name, out var path))
                parsedLine(name, path);
            lineStart = index + 1;
        }
    }

    private static bool TryParseLine(string line, out string projectName, out string path)
    {
        projectName = string.Empty;
        path = string.Empty;

        var frame = line.IndexOf(FrameMarker, StringComparison.Ordinal);
        if (frame < 0) return false;

        var nameStart = frame + FrameMarker.Length;
        var nameEnd = line.IndexOf(NameDelimiter, nameStart, StringComparison.Ordinal);
        if (nameEnd < 0) return false;

        var componentStart = line.IndexOf(ComponentStoreMarker, nameEnd, StringComparison.Ordinal);
        if (componentStart < 0) return false;
        componentStart += ComponentStoreMarker.Length;

        var componentEnd = line.LastIndexOf(')');
        if (componentEnd <= componentStart) return false;

        projectName = line[nameStart..nameEnd];
        path = JetBrainsIdeProjectTitleParser.NormalizeLocalPath(line[componentStart..componentEnd]) ?? string.Empty;
        return projectName.Length > 0 && path.Length > 0;
    }

    private static void AddMapping(LogState state, string projectName, string path)
    {
        if (!state.PathsByProject.TryGetValue(projectName, out var paths))
        {
            paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            state.PathsByProject.Add(projectName, paths);
        }

        paths.Add(path);
    }

    private void WarnOnce(string logPath, Exception exception)
    {
        var fingerprint = $"{logPath}:{exception.GetType().Name}:{exception.Message}";
        if (!_warnedFingerprints.Add(fingerprint)) return;

        _log?.Warn(
            nameof(JetBrainsIdeLogReader),
            $"could not read JetBrains IDE log '{logPath}': {exception.GetType().Name}: {exception.Message}");
    }

    private sealed class LogState
    {
        public Dictionary<string, HashSet<string>> PathsByProject { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, long> ProjectHistorySearchLengths { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, long> UnresolvedTitleSearchLengths { get; } =
            new(StringComparer.Ordinal);

        public bool Initialized { get; set; }
        public long Offset { get; set; }
        public DateTime CreationTimeUtc { get; set; }
        public DateTime WriteTimeUtc { get; set; }
        public byte[] Pending { get; set; } = [];

        public void Reset()
        {
            PathsByProject.Clear();
            ProjectHistorySearchLengths.Clear();
            UnresolvedTitleSearchLengths.Clear();
            Initialized = false;
            Offset = 0;
            CreationTimeUtc = default;
            WriteTimeUtc = default;
            Pending = [];
        }
    }
}
