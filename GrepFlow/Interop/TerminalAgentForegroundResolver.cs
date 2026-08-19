using System.IO;

namespace GrepFlow.Interop;

public sealed class TerminalAgentForegroundResolver
{
    private const int MaxConfirmedWindows = 16;

    private readonly Func<IntPtr, TerminalWindow?> _inspectWindow;
    private readonly Func<WindowsProcessSnapshot> _captureProcesses;
    private readonly Func<TerminalWindow, WindowsProcessSnapshot, TerminalAgentAssociation?> _locateProcesses;
    private readonly Func<uint, string?> _readProcessWorkingDirectory;
    private readonly Func<IReadOnlyList<CodexCliSession>> _readCodexSessions;
    private readonly Func<string, CodexCliSession?> _readCodexSession;
    private readonly Func<IReadOnlyList<ClaudeCodeSession>> _readClaudeSessions;
    private readonly Func<IntPtr, string?> _readAccessibleText;
    private readonly CodexCodeWindowMatcher _codexWindowMatcher;
    private readonly Func<string?, bool> _hasClaudeHeaderEvidence;
    private readonly Func<string?, IEnumerable<string>, string?> _matchClaudeWindow;
    private readonly PluginLog? _log;
    private readonly Dictionary<IntPtr, ConfirmedCodexWindow> _confirmedCodexWindows = [];
    private readonly Lock _confirmedGate = new();
    private int _warnedConflict;

    public TerminalAgentForegroundResolver(
        TerminalWindowInspector windows,
        WindowsProcessTree processes,
        TerminalAgentProcessLocator processLocator,
        WindowsProcessWorkingDirectoryReader workingDirectories,
        CodexCliSessionReader codexSessions,
        ClaudeCodeSessionReader claudeSessions,
        TerminalAccessibleTextReader accessibleText,
        CodexCodeWindowMatcher codexWindowMatcher,
        ClaudeCodeWindowMatcher claudeWindowMatcher,
        PluginLog? log = null)
        : this(
            windows.TryInspect,
            processes.Capture,
            processLocator.Locate,
            workingDirectories.TryRead,
            codexSessions.ReadActiveSessions,
            codexSessions.ReadActiveSession,
            claudeSessions.ReadLiveSessions,
            accessibleText.TryReadVisibleText,
            codexWindowMatcher,
            claudeWindowMatcher.HasHeaderEvidence,
            claudeWindowMatcher.Match,
            log)
    {
    }

    public TerminalAgentForegroundResolver(
        Func<IntPtr, TerminalWindow?> inspectWindow,
        Func<WindowsProcessSnapshot> captureProcesses,
        Func<TerminalWindow, WindowsProcessSnapshot, TerminalAgentAssociation?> locateProcesses,
        Func<uint, string?> readProcessWorkingDirectory,
        Func<IReadOnlyList<CodexCliSession>> readCodexSessions,
        Func<string, CodexCliSession?> readCodexSession,
        Func<IReadOnlyList<ClaudeCodeSession>> readClaudeSessions,
        Func<IntPtr, string?> readAccessibleText,
        CodexCodeWindowMatcher codexWindowMatcher,
        Func<string?, bool> hasClaudeHeaderEvidence,
        Func<string?, IEnumerable<string>, string?> matchClaudeWindow,
        PluginLog? log = null)
    {
        _inspectWindow = inspectWindow;
        _captureProcesses = captureProcesses;
        _locateProcesses = locateProcesses;
        _readProcessWorkingDirectory = readProcessWorkingDirectory;
        _readCodexSessions = readCodexSessions;
        _readCodexSession = readCodexSession;
        _readClaudeSessions = readClaudeSessions;
        _readAccessibleText = readAccessibleText;
        _codexWindowMatcher = codexWindowMatcher;
        _hasClaudeHeaderEvidence = hasClaudeHeaderEvidence;
        _matchClaudeWindow = matchClaudeWindow;
        _log = log;
    }

    public TerminalAgentWorkspace? TryResolve(IntPtr window)
    {
        try
        {
            var terminal = _inspectWindow(window);
            if (terminal is null)
            {
                ForgetConfirmedWindow(window);
                return null;
            }

            var association = _locateProcesses(terminal, _captureProcesses());
            if (association is null || association.Processes.Count == 0)
            {
                ForgetConfirmedWindow(window);
                return null;
            }

            var processPaths = ReadProcessPaths(association.Processes);
            var codexCandidates = PathsForKind(processPaths, TerminalAgentKind.Codex);
            var claudeCandidates = BuildClaudeCandidates(association.Processes, processPaths);

            if (association.HostKind == TerminalHostKind.DedicatedShell &&
                association.Processes.Select(process => process.Kind).Distinct().Count() == 1)
            {
                if (association.Processes.Count(process => process.Kind == TerminalAgentKind.Codex) == 1 &&
                    codexCandidates.Count == 1)
                {
                    return ResolveDedicatedCodex(
                        window,
                        terminal,
                        codexCandidates.Single());
                }

                var direct = UniqueWorkspace(codexCandidates, claudeCandidates);
                if (direct is not null) return direct;
            }

            string? accessibleText = null;
            var accessibleTextRead = false;
            string? ReadAccessibleText()
            {
                if (!accessibleTextRead)
                {
                    accessibleText = _readAccessibleText(window);
                    accessibleTextRead = true;
                }

                return accessibleText;
            }

            var claude = ResolveClaude(claudeCandidates, ReadAccessibleText);
            if (claude is not null)
            {
                var preliminaryCodex = AnalyzeCodex(
                    ReadAccessibleText(), terminal.Title, codexCandidates, []);
                var conflictingCodex = HasStrongCodexEvidence(preliminaryCodex);
                if (!conflictingCodex && CodexCodeWindowMatcher.HasUuidCandidate(terminal.Title))
                {
                    conflictingCodex = ResolveCodex(
                        window,
                        terminal,
                        association,
                        codexCandidates,
                        ReadAccessibleText()) is not null;
                }

                if (conflictingCodex)
                {
                    WarnConflict();
                    ForgetConfirmedWindow(window);
                    return null;
                }

                ForgetConfirmedWindow(window);
                return claude;
            }

            var codex = ResolveCodex(
                window,
                terminal,
                association,
                codexCandidates,
                ReadAccessibleText());
            return codex?.Workspace;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return null;
        }
    }

    private TerminalAgentWorkspace ResolveDedicatedCodex(
        IntPtr window,
        TerminalWindow terminal,
        string liveWorkingDirectory)
    {
        var accessibleText = _readAccessibleText(window);
        var visibleEvidence = AnalyzeCodex(
            accessibleText,
            terminal.Title,
            [liveWorkingDirectory],
            [],
            includeProjectAliases: false);
        var freshEvidenceRequiresScan = false;
        if (TryGetConfirmedWindow(window, terminal.ProcessId, out var confirmed) && confirmed is not null)
        {
            var cachedSession = NormalizeSession(_readCodexSession(confirmed.SessionId));
            freshEvidenceRequiresScan = CodexCodeWindowMatcher.HasUuidCandidate(terminal.Title) ||
                                        (cachedSession is not null &&
                                         visibleEvidence.VisiblePathHint is not null &&
                                         !PathsEqual(
                                             visibleEvidence.VisiblePathHint,
                                             cachedSession.WorkingDirectory));
            if (!freshEvidenceRequiresScan &&
                cachedSession is not null &&
                (string.Equals(terminal.Title, confirmed.VerifiedTitle, StringComparison.Ordinal) ||
                 _codexWindowMatcher.TitleMatchesWorkingDirectory(
                     terminal.Title,
                     cachedSession.WorkingDirectory,
                     includeProjectAliases: true) ||
                 string.Equals(
                     CodexCodeWindowMatcher.FindActiveThreadId(
                         terminal.Title,
                         [cachedSession.SessionId]),
                     cachedSession.SessionId,
                     StringComparison.OrdinalIgnoreCase)))
            {
                return new TerminalAgentWorkspace(
                    TerminalAgentKind.Codex,
                    cachedSession.WorkingDirectory);
            }

            ForgetConfirmedWindow(window);
        }

        if (!freshEvidenceRequiresScan &&
            !ShouldScanDedicatedSessions(
                terminal.Title,
                liveWorkingDirectory,
                visibleEvidence))
            return new TerminalAgentWorkspace(TerminalAgentKind.Codex, liveWorkingDirectory);

        var sessions = ReadNormalizedCodexSessions();
        if (sessions.Length == 0)
            return new TerminalAgentWorkspace(TerminalAgentKind.Codex, liveWorkingDirectory);

        var evidence = AnalyzeCodex(
            accessibleText,
            terminal.Title,
            sessions.Select(session => session.WorkingDirectory),
            sessions.Select(session => session.SessionId),
            includeProjectAliases: true);
        CodexCliSession? selected = null;
        if (evidence.ThreadId is not null)
        {
            selected = sessions.Single(session => string.Equals(
                session.SessionId,
                evidence.ThreadId,
                StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            var matchingPaths = evidence.VisibleWorkingDirectory is not null &&
                                (IsGenericShellTitle(terminal.Title) ||
                                 IsGenericCodexTitle(terminal.Title) ||
                                 evidence.TitleMatches.Contains(
                                     evidence.VisibleWorkingDirectory,
                                     StringComparer.OrdinalIgnoreCase))
                ? [evidence.VisibleWorkingDirectory]
                : evidence.TitleMatches
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(2)
                .ToArray();
            if (matchingPaths.Length == 1)
            {
                var matchingSessions = sessions.Where(session => PathsEqual(
                    session.WorkingDirectory,
                    matchingPaths[0])).Take(2).ToArray();
                if (matchingSessions.Length == 1) selected = matchingSessions[0];
                else if (matchingSessions.Length > 1)
                    return new TerminalAgentWorkspace(TerminalAgentKind.Codex, matchingPaths[0]);
            }
        }

        if (selected is null)
            return new TerminalAgentWorkspace(TerminalAgentKind.Codex, liveWorkingDirectory);

        RememberConfirmedWindow(window, terminal.ProcessId, terminal.Title, selected.SessionId);
        return new TerminalAgentWorkspace(TerminalAgentKind.Codex, selected.WorkingDirectory);
    }

    private bool ShouldScanDedicatedSessions(
        string title,
        string liveWorkingDirectory,
        CodexWindowEvidence? visibleEvidence)
    {
        if (CodexCodeWindowMatcher.HasUuidCandidate(title)) return true;
        if (visibleEvidence?.VisiblePathHint is not null &&
            !PathsEqual(visibleEvidence.VisiblePathHint, liveWorkingDirectory))
        {
            if (IsGenericShellTitle(title) ||
                IsGenericCodexTitle(title) ||
                PathHintAndTitleAgree(
                    visibleEvidence,
                    title,
                    includeProjectAliases: true))
                return true;
        }
        if (IsGenericShellTitle(title)) return false;
        if (IsGenericCodexTitle(title)) return false;

        return !_codexWindowMatcher.TitleMatchesWorkingDirectory(
            title,
            liveWorkingDirectory,
            includeProjectAliases: true);
    }

    private static bool IsGenericShellTitle(string title)
    {
        var value = title.Trim();
        return value.Length == 0 ||
               string.Equals(value, "Command Prompt", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGenericCodexTitle(string title)
        => string.Equals(title.Trim(), "Codex", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(title.Trim(), "OpenAI Codex", StringComparison.OrdinalIgnoreCase);

    public bool TryMatchForeground(IntPtr window)
    {
        try
        {
            var terminal = _inspectWindow(window);
            if (terminal is null)
            {
                ForgetConfirmedWindow(window);
                return false;
            }

            var association = _locateProcesses(terminal, _captureProcesses());
            if (association is null || association.Processes.Count == 0)
            {
                ForgetConfirmedWindow(window);
                return false;
            }
            if (association.HostKind == TerminalHostKind.DedicatedShell) return true;

            var accessibleText = _readAccessibleText(window);
            var claudeProcesses = association.Processes
                .Where(process => process.Kind == TerminalAgentKind.ClaudeCode)
                .ToArray();
            if (claudeProcesses.Length > 0)
            {
                var claudeProcessPaths = ReadProcessPaths(claudeProcesses);
                var claudeCandidates = PathsForKind(claudeProcessPaths, TerminalAgentKind.ClaudeCode);
                var hasUnreadableClaudeCwd = claudeProcessPaths.Values.Any(path => path is null);
                var claudeMatches = _matchClaudeWindow(accessibleText, claudeCandidates) is not null ||
                                    hasUnreadableClaudeCwd && _hasClaudeHeaderEvidence(accessibleText);
                if (claudeMatches)
                {
                    var codexProcesses = association.Processes
                        .Where(process => process.Kind == TerminalAgentKind.Codex)
                        .ToArray();
                    if (codexProcesses.Length == 0) return true;

                    var codexPaths = PathsForKind(ReadProcessPaths(codexProcesses), TerminalAgentKind.Codex);
                    var codexEvidence = AnalyzeCodex(accessibleText, terminal.Title, codexPaths, []);
                    return !HasStrongCodexEvidence(codexEvidence);
                }
            }

            var processes = association.Processes
                .Where(process => process.Kind == TerminalAgentKind.Codex)
                .ToArray();
            if (processes.Length == 0) return false;

            var livePaths = PathsForKind(ReadProcessPaths(processes), TerminalAgentKind.Codex);
            var evidence = AnalyzeCodex(accessibleText, terminal.Title, livePaths, []);
            if (!evidence.HasProductMarker && HasObviousShellEvidence(accessibleText))
            {
                ForgetConfirmedWindow(window);
                return false;
            }

            if (HasStrongCodexEvidence(evidence)) return true;

            var hasConfirmed = TryGetConfirmedWindow(window, terminal.ProcessId, out _);
            var canExpandAliases = evidence.HasProductMarker ||
                                   CodexCodeWindowMatcher.HasUuidCandidate(terminal.Title) ||
                                   evidence.VisibleWorkingDirectory is not null ||
                                   evidence.VisiblePathHint is not null ||
                                   hasConfirmed;
            if (canExpandAliases)
            {
                evidence = AnalyzeCodex(
                    accessibleText,
                    terminal.Title,
                    livePaths,
                    [],
                    includeProjectAliases: true);
                if (HasStrongCodexEvidence(evidence)) return true;
            }

            var canScanSessions = evidence.HasProductMarker ||
                                  CodexCodeWindowMatcher.HasUuidCandidate(terminal.Title) ||
                                  FooterAndTitleAgree(evidence) ||
                                  PathHintAndTitleAgree(
                                      evidence,
                                      terminal.Title,
                                      includeProjectAliases: true) ||
                                  hasConfirmed;
            if (!canScanSessions) return false;
            var resolved = ResolveCodex(window, terminal, association, livePaths, accessibleText);
            return resolved is not null;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return false;
        }
    }

    private TerminalAgentWorkspace? ResolveClaude(
        HashSet<string> candidates,
        Func<string?> readAccessibleText)
    {
        if (candidates.Count == 0) return null;

        var selected = _matchClaudeWindow(readAccessibleText(), candidates);
        var normalized = WindowsProcessWorkingDirectoryReader.NormalizeLocalDirectory(selected);
        return normalized is not null && candidates.Contains(normalized)
            ? new TerminalAgentWorkspace(TerminalAgentKind.ClaudeCode, normalized)
            : null;
    }

    private CodexResolution? ResolveCodex(
        IntPtr window,
        TerminalWindow terminal,
        TerminalAgentAssociation association,
        HashSet<string> livePaths,
        string? accessibleText)
    {
        if (!association.Processes.Any(process => process.Kind == TerminalAgentKind.Codex))
        {
            ForgetConfirmedWindow(window);
            return null;
        }

        var preliminary = AnalyzeCodex(accessibleText, terminal.Title, livePaths, []);
        var hasConfirmed = TryGetConfirmedWindow(window, terminal.ProcessId, out var confirmed);
        if (!preliminary.HasProductMarker && HasObviousShellEvidence(accessibleText))
        {
            ForgetConfirmedWindow(window);
            return null;
        }
        var canExpandAliases = preliminary.HasProductMarker ||
                               CodexCodeWindowMatcher.HasUuidCandidate(terminal.Title) ||
                               preliminary.VisibleWorkingDirectory is not null ||
                               preliminary.VisiblePathHint is not null ||
                               hasConfirmed;
        if (canExpandAliases)
        {
            preliminary = AnalyzeCodex(
                accessibleText,
                terminal.Title,
                livePaths,
                [],
                includeProjectAliases: true);
        }
        var mayReadSessions = CodexCodeWindowMatcher.HasUuidCandidate(terminal.Title) ||
                              preliminary.HasProductMarker ||
                              FooterAndTitleAgree(preliminary) ||
                              PathHintAndTitleAgree(
                                  preliminary,
                                  terminal.Title,
                                  includeProjectAliases: true) ||
                              hasConfirmed;
        if (!mayReadSessions) return null;

        var sessions = ReadNormalizedCodexSessions();
        if (hasConfirmed && confirmed is not null && !sessions.Any(session =>
                string.Equals(session.SessionId, confirmed.SessionId, StringComparison.OrdinalIgnoreCase)))
        {
            ForgetConfirmedWindow(window);
            hasConfirmed = false;
            confirmed = null;
        }
        var allPaths = livePaths
            .Concat(sessions.Select(session => session.WorkingDirectory))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var evidence = AnalyzeCodex(
            accessibleText,
            terminal.Title,
            allPaths,
            sessions.Select(session => session.SessionId),
            includeProjectAliases: true);

        var selectedSignals = new List<CodexResolution>();
        if (evidence.ThreadId is not null)
        {
            var session = sessions.SingleOrDefault(item =>
                string.Equals(item.SessionId, evidence.ThreadId, StringComparison.OrdinalIgnoreCase));
            if (session is not null)
                selectedSignals.Add(FromSession(session));
        }

        if (evidence.VisibleWorkingDirectory is not null && FooterAndTitleAgree(evidence))
        {
            var pathResolution = ResolveSelectedCodexPath(
                evidence.VisibleWorkingDirectory,
                sessions,
                livePaths);
            if (pathResolution is not null) selectedSignals.Add(pathResolution);
        }

        if (selectedSignals.Count == 0 && evidence.HasProductMarker)
        {
            var titlePaths = evidence.TitleMatches.Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
            if (titlePaths.Length == 1)
            {
                var pathResolution = ResolveSelectedCodexPath(titlePaths[0], sessions, livePaths);
                if (pathResolution is not null) selectedSignals.Add(pathResolution);
            }
        }

        if (selectedSignals.Select(signal => signal.Workspace.WorkingDirectory)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(2).Count() > 1)
        {
            WarnConflict();
            ForgetConfirmedWindow(window);
            return null;
        }

        var selected = selectedSignals.FirstOrDefault();
        if (selected is null && hasConfirmed && confirmed is not null &&
            !HasObviousShellEvidence(accessibleText) &&
            (string.Equals(terminal.Title, confirmed.VerifiedTitle, StringComparison.Ordinal) ||
             sessions.Any(session =>
                 string.Equals(session.SessionId, confirmed.SessionId, StringComparison.OrdinalIgnoreCase) &&
                 _codexWindowMatcher.TitleMatchesWorkingDirectory(
                     terminal.Title,
                     session.WorkingDirectory,
                     includeProjectAliases: true))))
        {
            var session = sessions.SingleOrDefault(item =>
                string.Equals(item.SessionId, confirmed.SessionId, StringComparison.OrdinalIgnoreCase));
            if (session is not null) selected = FromSession(session);
        }

        if (selected is null) return null;

        if (selected.SessionId is not null)
            RememberConfirmedWindow(window, terminal.ProcessId, terminal.Title, selected.SessionId);
        else
            ForgetConfirmedWindow(window);
        return selected;
    }

    private static CodexResolution? ResolveSelectedCodexPath(
        string selectedPath,
        IReadOnlyList<CodexCliSession> sessions,
        IReadOnlySet<string> livePaths)
    {
        var matchingSessions = sessions.Where(session => PathsEqual(
            session.WorkingDirectory,
            selectedPath)).Take(2).ToArray();
        if (matchingSessions.Length == 1) return FromSession(matchingSessions[0]);
        if (matchingSessions.Length > 1) return FromPath(selectedPath);

        var matchingLivePaths = livePaths.Where(path => PathsEqual(path, selectedPath)).Take(2).ToArray();
        return matchingLivePaths.Length == 1 ? FromPath(matchingLivePaths[0]) : null;
    }

    private CodexWindowEvidence AnalyzeCodex(
        string? accessibleText,
        string title,
        IEnumerable<string> paths,
        IEnumerable<string> activeSessionIds,
        bool includeProjectAliases = false)
        => _codexWindowMatcher.Analyze(
            accessibleText,
            title,
            paths,
            activeSessionIds,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            includeProjectAliases);

    private CodexCliSession[] ReadNormalizedCodexSessions()
        => _readCodexSessions()
            .Select(NormalizeSession)
            .Where(session => session is not null)
            .Cast<CodexCliSession>()
            .ToArray();

    private static CodexCliSession? NormalizeSession(CodexCliSession? session)
    {
        if (session is null) return null;

        var effective = WindowsProcessWorkingDirectoryReader.NormalizeLocalDirectory(
            session.WorkingDirectory);
        if (effective is null) return null;

        var initial = WindowsProcessWorkingDirectoryReader.NormalizeLocalDirectory(
            session.InitialWorkingDirectory);
        return session with
        {
            WorkingDirectory = effective,
            InitialWorkingDirectory = initial,
        };
    }

    private Dictionary<TerminalAgentProcess, string?> ReadProcessPaths(
        IEnumerable<TerminalAgentProcess> processes)
    {
        var result = new Dictionary<TerminalAgentProcess, string?>();
        foreach (var process in processes.Distinct())
        {
            var path = WindowsProcessWorkingDirectoryReader.NormalizeLocalDirectory(
                _readProcessWorkingDirectory(process.ProcessId));
            result[process] = path;
        }

        return result;
    }

    private HashSet<string> BuildClaudeCandidates(
        IReadOnlyList<TerminalAgentProcess> processes,
        IReadOnlyDictionary<TerminalAgentProcess, string?> processPaths)
    {
        var claudeProcesses = processes
            .Where(process => process.Kind == TerminalAgentKind.ClaudeCode)
            .Distinct()
            .ToArray();
        var candidates = PathsForKind(processPaths, TerminalAgentKind.ClaudeCode);
        var missingProcessIds = claudeProcesses
            .Where(process => processPaths.GetValueOrDefault(process) is null)
            .Select(process => process.ProcessId)
            .ToHashSet();
        if (missingProcessIds.Count == 0) return candidates;

        foreach (var session in _readClaudeSessions())
        {
            if (!missingProcessIds.Contains(session.ProcessId)) continue;
            AddCandidate(candidates, session.WorkingDirectory);
        }

        return candidates;
    }

    private void RememberConfirmedWindow(IntPtr window, uint processId, string title, string sessionId)
    {
        lock (_confirmedGate)
        {
            if (!_confirmedCodexWindows.ContainsKey(window) && _confirmedCodexWindows.Count >= MaxConfirmedWindows)
                _confirmedCodexWindows.Remove(_confirmedCodexWindows.Keys.First());
            _confirmedCodexWindows[window] = new ConfirmedCodexWindow(processId, title, sessionId);
        }
    }

    private bool TryGetConfirmedWindow(IntPtr window, uint processId, out ConfirmedCodexWindow? confirmed)
    {
        lock (_confirmedGate)
        {
            if (_confirmedCodexWindows.TryGetValue(window, out confirmed) &&
                confirmed.TerminalProcessId == processId)
                return true;

            _confirmedCodexWindows.Remove(window);
            confirmed = null;
            return false;
        }
    }

    private void ForgetConfirmedWindow(IntPtr window)
    {
        lock (_confirmedGate) _confirmedCodexWindows.Remove(window);
    }

    private void WarnConflict()
    {
        if (Interlocked.Exchange(ref _warnedConflict, 1) == 0)
            _log?.Warn(nameof(TerminalAgentForegroundResolver), "conflicting terminal-agent window evidence");
    }

    private static bool HasStrongCodexEvidence(CodexWindowEvidence evidence)
        => evidence.ThreadId is not null ||
           evidence.HasProductMarker && evidence.TitleMatches.Count == 1 ||
           FooterAndTitleAgree(evidence);

    private static bool FooterAndTitleAgree(CodexWindowEvidence evidence)
        => evidence.VisibleWorkingDirectory is not null &&
           evidence.TitleMatches.Contains(evidence.VisibleWorkingDirectory, StringComparer.OrdinalIgnoreCase);

    private bool PathHintAndTitleAgree(
        CodexWindowEvidence evidence,
        string title,
        bool includeProjectAliases)
        => evidence.VisiblePathHint is not null &&
           _codexWindowMatcher.TitleMatchesWorkingDirectory(
               title,
               evidence.VisiblePathHint,
               includeProjectAliases);

    private static bool HasObviousShellEvidence(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var lastSignificantLine = text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(line => !string.IsNullOrWhiteSpace(line));
        return lastSignificantLine is not null && IsExistingDirectoryPrompt(lastSignificantLine);
    }

    private static bool IsExistingDirectoryPrompt(string line)
    {
        var value = line.Trim();
        if (!value.EndsWith('>')) return false;

        value = value[..^1].TrimEnd();
        return WindowsProcessWorkingDirectoryReader.NormalizeLocalDirectory(value) is not null;
    }

    private static CodexResolution FromSession(CodexCliSession session)
        => new(
            new TerminalAgentWorkspace(TerminalAgentKind.Codex, session.WorkingDirectory),
            session.SessionId);

    private static CodexResolution FromPath(string path)
        => new(new TerminalAgentWorkspace(TerminalAgentKind.Codex, path), null);

    private static bool PathsEqual(string first, string second)
        => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    private static TerminalAgentWorkspace? UniqueWorkspace(
        IEnumerable<string> codexCandidates,
        IEnumerable<string> claudeCandidates)
    {
        var workspaces = codexCandidates
            .Select(path => new TerminalAgentWorkspace(TerminalAgentKind.Codex, path))
            .Concat(claudeCandidates.Select(path =>
                new TerminalAgentWorkspace(TerminalAgentKind.ClaudeCode, path)))
            .Distinct()
            .Take(2)
            .ToArray();
        return workspaces.Length == 1 ? workspaces[0] : null;
    }

    private static HashSet<string> PathsForKind(
        IReadOnlyDictionary<TerminalAgentProcess, string?> processPaths,
        TerminalAgentKind kind)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in processPaths)
        {
            if (pair.Key.Kind == kind) AddCandidate(result, pair.Value);
        }

        return result;
    }

    private static void AddCandidate(HashSet<string> candidates, string? path)
    {
        var normalized = WindowsProcessWorkingDirectoryReader.NormalizeLocalDirectory(path);
        if (normalized is not null) candidates.Add(normalized);
    }

    public static IReadOnlyList<string> FindCodexTitleMatches(
        string title,
        IEnumerable<string> candidates)
        => new CodexCodeWindowMatcher().FindTitleMatches(
            title,
            candidates,
            includeProjectAliases: true);

    private static bool IsRecoverable(Exception exception)
        => exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or
            NotSupportedException or System.Runtime.InteropServices.COMException or
            System.Security.SecurityException;

    private sealed record ConfirmedCodexWindow(uint TerminalProcessId, string VerifiedTitle, string SessionId);

    private sealed record CodexResolution(TerminalAgentWorkspace Workspace, string? SessionId);
}
