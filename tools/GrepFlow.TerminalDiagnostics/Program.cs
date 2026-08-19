using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Automation;
using GrepFlow.Interop;

namespace GrepFlow.TerminalDiagnostics;

internal static class Program
{
    private const int MaxVisibleTextLength = 32 * 1024;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var delay = ReadDelay(args);
            var outputPath = ReadOutputPath(args);

            Console.WriteLine("GrepFlow terminal-agent diagnostic snapshot");
            Console.WriteLine("WARNING: the JSON can contain paths and visible terminal conversation text.");
            if (delay > 0)
            {
                Console.WriteLine("Switch to the failing Codex terminal window now.");
                for (var remaining = delay; remaining > 0; remaining--)
                {
                    Console.Write($"Capturing in {remaining}...   \r");
                    Thread.Sleep(1000);
                }
                Console.WriteLine();
            }

            var snapshot = Capture();
            var directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(
                outputPath,
                JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Converters = { new JsonStringEnumConverter() },
                }),
                new UTF8Encoding(false));

            Console.WriteLine($"Snapshot written to: {outputPath}");
            Console.WriteLine($"Foreground: {snapshot.TerminalWindow?.ImageFileName ?? "unrecognized"} | {snapshot.TerminalWindow?.Title ?? "<no title>"}");
            Console.WriteLine($"UIA visible text: {(snapshot.VisibleText is null ? "unavailable" : $"{snapshot.VisibleText.Length} chars")}");
            Console.WriteLine($"Active Codex sessions: {snapshot.CodexSessions.Count}");
            Console.WriteLine($"Matches foreground: {snapshot.MatchesForeground}");
            Console.WriteLine($"Resolver: {snapshot.Resolution?.Kind.ToString() ?? "unresolved"} | {snapshot.Resolution?.WorkingDirectory ?? "<none>"}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static TerminalDiagnosticSnapshot Capture()
    {
        var hwnd = Native.GetForegroundWindow();
        var windowInspector = new TerminalWindowInspector();
        var processTree = new WindowsProcessTree();
        var processLocator = new TerminalAgentProcessLocator();
        var cwdReader = new WindowsProcessWorkingDirectoryReader();
        var accessibleTextReader = new TerminalAccessibleTextReader();
        var codexSessions = new CodexCliSessionReader();
        var claudeSessions = new ClaudeCodeSessionReader();
        var terminal = windowInspector.TryInspect(hwnd);
        var processes = processTree.Capture();
        var association = terminal is null ? null : processLocator.Locate(terminal, processes);
        var processDetails = ReadProcessDetails(terminal, association, processes, cwdReader);
        var visibleText = accessibleTextReader.TryReadVisibleText(hwnd);
        var automation = ReadAutomationSnapshot(hwnd);
        var activeCodexSessions = codexSessions.ReadActiveSessions();
        var activeClaudeSessions = claudeSessions.ReadLiveSessions();

        TerminalAgentForegroundResolver CreateResolver()
            => new(
                windowInspector,
                processTree,
                processLocator,
                cwdReader,
                codexSessions,
                claudeSessions,
                accessibleTextReader,
                new CodexCodeWindowMatcher(),
                new ClaudeCodeWindowMatcher());
        var matchesForeground = CreateResolver().TryMatchForeground(hwnd);
        var resolution = CreateResolver().TryResolve(hwnd);

        return new TerminalDiagnosticSnapshot(
            DateTimeOffset.Now,
            Environment.OSVersion.VersionString,
            RuntimeInformation.ProcessArchitecture.ToString(),
            $"0x{hwnd.ToInt64():X}",
            terminal,
            association,
            processDetails,
            visibleText,
            automation,
            Environment.GetEnvironmentVariable("CODEX_HOME"),
            codexSessions.CodexHome,
            ReadRelevantCodexConfig(codexSessions.CodexHome),
            activeCodexSessions,
            claudeSessions.SessionDirectory,
            activeClaudeSessions,
            matchesForeground,
            resolution);
    }

    private static IReadOnlyList<ProcessDiagnostic> ReadProcessDetails(
        TerminalWindow? terminal,
        TerminalAgentAssociation? association,
        WindowsProcessSnapshot snapshot,
        WindowsProcessWorkingDirectoryReader cwdReader)
    {
        var processIds = new HashSet<uint>();
        if (terminal is not null) processIds.Add(terminal.ProcessId);
        if (association is not null)
        {
            foreach (var agent in association.Processes)
            {
                var current = agent.ProcessId;
                var visited = new HashSet<uint>();
                while (current != 0 && visited.Add(current))
                {
                    processIds.Add(current);
                    if (terminal is not null && current == terminal.ProcessId) break;
                    if (!snapshot.TryGetParentProcessId(current, out current)) break;
                }
            }
        }

        var result = new List<ProcessDiagnostic>();
        foreach (var processId in processIds.Order())
        {
            snapshot.TryGetProcess(processId, out var process);
            var kind = association?.Processes
                .FirstOrDefault(agent => agent.ProcessId == processId)?.Kind;
            result.Add(new ProcessDiagnostic(
                processId,
                process?.ParentProcessId,
                process?.ImageFileName,
                kind,
                cwdReader.TryRead(processId)));
        }
        return result;
    }

    private static AutomationSnapshot ReadAutomationSnapshot(IntPtr hwnd)
    {
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root is null) return new AutomationSnapshot(null, [], "AutomationElement.FromHandle returned null");

            var focused = TryGetFocusedElement();
            var candidates = root.FindAll(
                TreeScope.Element | TreeScope.Descendants,
                new PropertyCondition(AutomationElement.IsTextPatternAvailableProperty, true));
            var result = new List<AutomationTextCandidate>();
            foreach (AutomationElement candidate in candidates)
            {
                result.Add(new AutomationTextCandidate(
                    SafeProperty(candidate, AutomationElement.AutomationIdProperty),
                    SafeProperty(candidate, AutomationElement.ClassNameProperty),
                    SafeProperty(candidate, AutomationElement.NameProperty),
                    SafeProperty(candidate, AutomationElement.HelpTextProperty),
                    SafeProperty(candidate, AutomationElement.ControlTypeProperty),
                    SafeBool(candidate, AutomationElement.HasKeyboardFocusProperty),
                    SafeBool(candidate, AutomationElement.IsOffscreenProperty),
                    ReadVisibleText(candidate)));
            }

            return new AutomationSnapshot(
                focused is null ? null : new AutomationElementSummary(
                    SafeProperty(focused, AutomationElement.AutomationIdProperty),
                    SafeProperty(focused, AutomationElement.ClassNameProperty),
                    SafeProperty(focused, AutomationElement.NameProperty)),
                result,
                null);
        }
        catch (Exception exception) when (IsAutomationFailure(exception))
        {
            return new AutomationSnapshot(null, [], $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private static AutomationElement? TryGetFocusedElement()
    {
        try
        {
            return AutomationElement.FocusedElement;
        }
        catch (Exception exception) when (IsAutomationFailure(exception))
        {
            return null;
        }
    }

    private static string? ReadVisibleText(AutomationElement element)
    {
        try
        {
            if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var value) || value is not TextPattern pattern)
                return null;

            var text = new StringBuilder();
            var remaining = MaxVisibleTextLength;
            foreach (var range in pattern.GetVisibleRanges())
            {
                if (remaining <= 0) break;
                var fragment = range.GetText(remaining);
                if (string.IsNullOrEmpty(fragment)) continue;
                text.Append(fragment);
                remaining -= fragment.Length;
            }
            return text.Length == 0 ? null : text.ToString();
        }
        catch (Exception exception) when (IsAutomationFailure(exception))
        {
            return $"<UIA read failed: {exception.GetType().Name}: {exception.Message}>";
        }
    }

    private static string? SafeProperty(AutomationElement element, AutomationProperty property)
    {
        try
        {
            var value = element.GetCurrentPropertyValue(property, true);
            return value == AutomationElement.NotSupported ? null : value?.ToString();
        }
        catch (Exception exception) when (IsAutomationFailure(exception))
        {
            return $"<unavailable: {exception.GetType().Name}>";
        }
    }

    private static bool? SafeBool(AutomationElement element, AutomationProperty property)
    {
        try
        {
            var value = element.GetCurrentPropertyValue(property, true);
            return value is bool result ? result : null;
        }
        catch (Exception exception) when (IsAutomationFailure(exception))
        {
            return null;
        }
    }

    private static IReadOnlyList<string> ReadRelevantCodexConfig(string codexHome)
    {
        var path = Path.Combine(codexHome, "config.toml");
        try
        {
            if (!File.Exists(path)) return [];
            return File.ReadLines(path)
                .Select((line, index) => (Line: line.Trim(), Number: index + 1))
                .Where(item =>
                    item.Line.StartsWith("[", StringComparison.Ordinal) ||
                    item.Line.StartsWith("status_line", StringComparison.Ordinal) ||
                    item.Line.StartsWith("terminal_title", StringComparison.Ordinal) ||
                    item.Line.StartsWith("resume_cwd", StringComparison.Ordinal))
                .Take(100)
                .Select(item => $"{item.Number}: {item.Line}")
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [$"<config read failed: {exception.GetType().Name}: {exception.Message}>"];
        }
    }

    private static int ReadDelay(string[] args)
    {
        var index = Array.IndexOf(args, "--delay");
        if (index < 0) return 8;
        if (index + 1 >= args.Length || !int.TryParse(args[index + 1], out var delay) || delay is < 0 or > 60)
            throw new ArgumentException("--delay must be an integer from 0 to 60");
        return delay;
    }

    private static string ReadOutputPath(string[] args)
    {
        var index = Array.IndexOf(args, "--output");
        if (index >= 0)
        {
            if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
                throw new ArgumentException("--output requires a file path");
            return Path.GetFullPath(args[index + 1]);
        }

        var directory = Path.Combine(Path.GetTempPath(), "GrepFlow", "Diagnostics");
        return Path.Combine(directory, $"terminal-agent-{DateTime.Now:yyyyMMdd-HHmmss}.json");
    }

    private static bool IsAutomationFailure(Exception exception)
        => exception is ElementNotAvailableException or InvalidOperationException or ArgumentException or
            NotSupportedException or UnauthorizedAccessException or COMException or System.Security.SecurityException;

    private static class Native
    {
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();
    }
}

internal sealed record TerminalDiagnosticSnapshot(
    DateTimeOffset CapturedAt,
    string OperatingSystem,
    string ProcessArchitecture,
    string ForegroundHwnd,
    TerminalWindow? TerminalWindow,
    TerminalAgentAssociation? Association,
    IReadOnlyList<ProcessDiagnostic> Processes,
    string? VisibleText,
    AutomationSnapshot Automation,
    string? CodexHomeEnvironment,
    string ResolvedCodexHome,
    IReadOnlyList<string> RelevantCodexConfigLines,
    IReadOnlyList<CodexCliSession> CodexSessions,
    string ClaudeSessionDirectory,
    IReadOnlyList<ClaudeCodeSession> ClaudeSessions,
    bool MatchesForeground,
    TerminalAgentWorkspace? Resolution);

internal sealed record ProcessDiagnostic(
    uint ProcessId,
    uint? ParentProcessId,
    string? ImageFileName,
    TerminalAgentKind? AgentKind,
    string? WorkingDirectory);

internal sealed record AutomationSnapshot(
    AutomationElementSummary? FocusedElement,
    IReadOnlyList<AutomationTextCandidate> TextCandidates,
    string? Error);

internal sealed record AutomationElementSummary(
    string? AutomationId,
    string? ClassName,
    string? Name);

internal sealed record AutomationTextCandidate(
    string? AutomationId,
    string? ClassName,
    string? Name,
    string? HelpText,
    string? ControlType,
    bool? HasKeyboardFocus,
    bool? IsOffscreen,
    string? VisibleText);
