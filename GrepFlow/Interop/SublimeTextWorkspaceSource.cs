using System.IO;

namespace GrepFlow.Interop;

internal readonly record struct SublimeTextNativeWindow(IntPtr Window, uint ProcessId);

internal sealed class SublimeTextWorkspaceSource : IWorkspaceSource
{
    public const string SourceId = "sublime-text";
    internal const int MaxCachedWindows = 32;

    private readonly Func<IntPtr, SublimeTextWindowSnapshot?> _inspectWindow;
    private readonly Func<SublimeTextSession?> _readSession;
    private readonly Func<string, bool> _directoryExists;
    private readonly object _gate = new();
    private readonly Dictionary<SublimeTextNativeWindow, WindowState> _states = [];

    private SublimeTextWindowSnapshot? _lastWindow;
    private long _useSequence;

    public SublimeTextWorkspaceSource(
        SublimeTextWindowInspector inspector,
        SublimeTextSessionReader reader)
        : this(inspector.TryInspect, reader.TryReadSession, Directory.Exists)
    {
    }

    internal SublimeTextWorkspaceSource(
        Func<IntPtr, SublimeTextWindowSnapshot?> inspectWindow,
        Func<SublimeTextSession?> readSession,
        Func<string, bool>? directoryExists = null)
    {
        _inspectWindow = inspectWindow;
        _readSession = readSession;
        _directoryExists = directoryExists ?? Directory.Exists;
    }

    public string Id => SourceId;

    public string DisplayName => "Sublime Text";

    internal int CachedWindowCount
    {
        get
        {
            lock (_gate) return _states.Count;
        }
    }

    public bool MatchesForeground(IntPtr window)
    {
        var snapshot = _inspectWindow(window);
        if (snapshot is null) return false;

        var key = Key(snapshot);
        lock (_gate)
        {
            _lastWindow = snapshot;
            GetOrAddState(key).LastUsed = ++_useSequence;
            PruneCache(key);
        }

        return true;
    }

    public ValueTask<ActiveFolder?> GetActiveFolderAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        SublimeTextWindowSnapshot? cached;
        lock (_gate) cached = _lastWindow;
        if (cached is null) return ValueTask.FromResult<ActiveFolder?>(null);

        var refreshed = _inspectWindow(cached.Window);
        if (refreshed is null || refreshed.ProcessId != cached.ProcessId)
        {
            Invalidate(cached);
            return ValueTask.FromResult<ActiveFolder?>(null);
        }

        var key = Key(refreshed);
        long? cachedWindowId;
        lock (_gate)
        {
            var state = GetOrAddState(key);
            state.LastUsed = ++_useSequence;
            cachedWindowId = state.SessionWindowId;
        }

        token.ThrowIfCancellationRequested();
        var session = _readSession();
        if (session is null)
            return ValueTask.FromResult(CreateFallback(key));

        var sessionWindow = MatchSessionWindow(refreshed, session.Windows, cachedWindowId);
        if (sessionWindow is null)
        {
            if (cachedWindowId is not null && session.Windows.All(window => window.WindowId != cachedWindowId))
            {
                lock (_gate)
                {
                    if (_states.TryGetValue(key, out var state)) state.SessionWindowId = null;
                }
            }

            return ValueTask.FromResult(CreateFallback(key));
        }

        string? selected;
        lock (_gate)
        {
            var state = GetOrAddState(key);
            state.SessionWindowId = sessionWindow.WindowId;
            selected = SelectRoot(refreshed.Title, sessionWindow.Folders, state.SelectedRoot);
            state.SelectedRoot = selected;
        }

        if (selected is null || !_directoryExists(selected))
            return ValueTask.FromResult<ActiveFolder?>(null);

        return ValueTask.FromResult<ActiveFolder?>(
            new ActiveFolder(selected, DisplayName, FromNearestWindow: false));
    }

    internal static string? SelectRoot(
        string title,
        IReadOnlyList<SublimeTextSessionFolder> folders,
        string? rememberedRoot)
    {
        if (folders.Count == 0) return null;

        var roots = folders.Select(folder => folder.Path).ToArray();
        if (roots.Length == 1) return roots[0];

        var titleRoot = RootExposedByTitle(title, roots);
        if (titleRoot is not null) return titleRoot;

        var remembered = roots.FirstOrDefault(root => PathsEqual(root, rememberedRoot));
        return remembered ?? roots[0];
    }

    internal static SublimeTextSessionWindow? MatchSessionWindow(
        SublimeTextWindowSnapshot nativeWindow,
        IReadOnlyList<SublimeTextSessionWindow> sessionWindows,
        long? cachedWindowId)
    {
        if (sessionWindows.Count == 1) return sessionWindows[0];

        var byLabel = UniqueLabelMatch(nativeWindow.Title, sessionWindows);
        if (cachedWindowId is not null)
        {
            var cached = sessionWindows.FirstOrDefault(window => window.WindowId == cachedWindowId);
            if (cached is not null && (byLabel is null || byLabel.WindowId == cached.WindowId))
                return cached;
        }

        if (byLabel is not null) return byLabel;

        return UniqueGeometryMatch(nativeWindow.Bounds, sessionWindows);
    }

    private ActiveFolder? CreateFallback(SublimeTextNativeWindow key)
    {
        string? selected;
        lock (_gate)
            selected = _states.TryGetValue(key, out var state) ? state.SelectedRoot : null;

        return selected is not null && _directoryExists(selected)
            ? new ActiveFolder(selected, DisplayName, FromNearestWindow: false)
            : null;
    }

    private void Invalidate(SublimeTextWindowSnapshot cached)
    {
        var key = Key(cached);
        lock (_gate)
        {
            _states.Remove(key);
            if (_lastWindow is not null && Key(_lastWindow) == key) _lastWindow = null;
        }
    }

    private WindowState GetOrAddState(SublimeTextNativeWindow key)
    {
        if (_states.TryGetValue(key, out var state)) return state;
        state = new WindowState();
        _states.Add(key, state);
        return state;
    }

    private void PruneCache(SublimeTextNativeWindow current)
    {
        while (_states.Count > MaxCachedWindows)
        {
            var oldest = _states
                .Where(pair => pair.Key != current)
                .MinBy(pair => pair.Value.LastUsed);
            _states.Remove(oldest.Key);
        }
    }

    private static SublimeTextNativeWindow Key(SublimeTextWindowSnapshot snapshot)
        => new(snapshot.Window, snapshot.ProcessId);

    private static SublimeTextSessionWindow? UniqueGeometryMatch(
        SublimeTextWindowBounds? nativeBounds,
        IReadOnlyList<SublimeTextSessionWindow> sessionWindows)
    {
        if (nativeBounds is null) return null;

        var candidates = sessionWindows
            .Where(window => window.Bounds is not null && GeometryIsNear(nativeBounds, window.Bounds))
            .ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    private static bool GeometryIsNear(SublimeTextWindowBounds left, SublimeTextWindowBounds right)
        => Math.Abs(left.Left - right.Left) <= 32 &&
           Math.Abs(left.Top - right.Top) <= 32 &&
           Math.Abs(left.Right - right.Right) <= 32 &&
           Math.Abs(left.Bottom - right.Bottom) <= 32;

    private static SublimeTextSessionWindow? UniqueLabelMatch(
        string title,
        IReadOnlyList<SublimeTextSessionWindow> sessionWindows)
    {
        var label = ExtractWorkspaceLabel(title);
        if (label is null) return null;

        var candidates = sessionWindows.Where(window => LabelMatches(label, window)).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    private static bool LabelMatches(string label, SublimeTextSessionWindow window)
    {
        if (LabelsEqual(label, window.WorkspaceName)) return true;

        var projectLabel = string.IsNullOrWhiteSpace(window.Project)
            ? null
            : Path.GetFileNameWithoutExtension(window.Project);
        if (LabelsEqual(label, projectLabel)) return true;

        var labels = window.Folders
            .Select(folder => folder.Label)
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .ToArray();
        if (labels.Length == 0) return false;

        return labels.Any(candidate => LabelsEqual(label, candidate)) ||
               LabelsEqual(label, string.Join(", ", labels));
    }

    internal static string? ExtractWorkspaceLabel(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        var content = TrimSublimeSuffix(title.Trim());
        if (content.Length == 0) return null;

        if (content[^1] == ')')
        {
            var open = content.LastIndexOf('(');
            if (open >= 0 && open + 1 < content.Length - 1)
                return content[(open + 1)..^1].Trim();
        }

        foreach (var separator in new[] { " - ", " \u2014 ", " \u2013 " })
        {
            var index = content.LastIndexOf(separator, StringComparison.Ordinal);
            if (index >= 0 && index + separator.Length < content.Length)
                return content[(index + separator.Length)..].Trim();
        }

        return ContainsAbsoluteWindowsPath(content) ? null : content;
    }

    private static string TrimSublimeSuffix(string title)
    {
        foreach (var suffix in new[] { " - Sublime Text", " \u2014 Sublime Text", " \u2013 Sublime Text" })
        {
            var index = title.LastIndexOf(suffix, StringComparison.OrdinalIgnoreCase);
            if (index >= 0) return title[..index].TrimEnd();
        }

        return title;
    }

    private static bool ContainsAbsoluteWindowsPath(string value)
    {
        for (var index = 0; index + 2 < value.Length; index++)
        {
            if (char.IsAsciiLetter(value[index]) &&
                value[index + 1] == ':' &&
                value[index + 2] is '\\' or '/')
                return true;
        }

        return false;
    }

    private static bool LabelsEqual(string label, string? candidate)
        => candidate is not null &&
           string.Equals(label.Trim(), candidate.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string? RootExposedByTitle(string title, IEnumerable<string> roots)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        var normalizedTitle = title.Replace('/', '\\');
        return roots
            .Where(root => TitleContainsChildPath(normalizedTitle, root.Replace('/', '\\')))
            .OrderByDescending(root => root.Length)
            .FirstOrDefault();
    }

    private static bool TitleContainsChildPath(string title, string root)
    {
        var searchFrom = 0;
        while (searchFrom < title.Length)
        {
            var index = title.IndexOf(root, searchFrom, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return false;

            var beforeIsBoundary = index == 0 ||
                                   !(char.IsLetterOrDigit(title[index - 1]) || title[index - 1] is '\\' or ':');
            var childStart = index + root.Length;
            if (beforeIsBoundary &&
                childStart + 1 < title.Length &&
                title[childStart] == '\\' &&
                title[childStart + 1] != '\\')
                return true;

            searchFrom = index + 1;
        }

        return false;
    }

    private static bool PathsEqual(string path, string? other)
        => other is not null && string.Equals(path, other, StringComparison.OrdinalIgnoreCase);

    private sealed class WindowState
    {
        public long? SessionWindowId { get; set; }

        public string? SelectedRoot { get; set; }

        public long LastUsed { get; set; }
    }
}
