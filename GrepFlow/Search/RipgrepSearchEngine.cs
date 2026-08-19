using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using GrepFlow.Settings;

namespace GrepFlow.Search;

public sealed class RipgrepSearchEngine : ISearchEngine
{
    // ripgrep exits with 0 for matches, 1 for no matches and 2 or more for a real failure
    private const int FailureExitCode = 2;
    private const string ErrorPrefix = "error:";
    private const string ToolPrefix = "rg: ";

    private readonly PluginSettings _settings;
    private readonly RipgrepExecutable _executable;
    private readonly RipgrepJsonParser _parser;

    public RipgrepSearchEngine(
        PluginSettings settings,
        RipgrepExecutable executable,
        RipgrepJsonParser parser)
    {
        _settings = settings;
        _executable = executable;
        _parser = parser;
    }

    public async IAsyncEnumerable<RipgrepMatch> SearchAsync(
        SearchRequest request,
        [EnumeratorCancellation] CancellationToken token)
    {
        using var process = new Process();
        Configure(process.StartInfo, request);

        process.Start();
        process.StandardInput.Close();

        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

        try
        {
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(token).ConfigureAwait(false);
                if (line is null) break;

                var match = _parser.Parse(line, request.Folder);
                if (match is not null) yield return match;
            }

            await process.WaitForExitAsync(token).ConfigureAwait(false);
            if (process.ExitCode >= FailureExitCode)
                throw SearchFailure.Reported(Describe(await stderr.ConfigureAwait(false), process.ExitCode));
        }
        finally
        {
            Kill(process);
            _ = stderr.ContinueWith(static task => _ = task.Exception, TaskScheduler.Default);
        }
    }

    internal void Configure(ProcessStartInfo startInfo, SearchRequest request)
    {
        startInfo.FileName = _executable.Path
            ?? throw new InvalidOperationException("ripgrep executable path is not set");
        startInfo.WorkingDirectory = request.Folder;
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.StandardOutputEncoding = Encoding.UTF8;
        startInfo.StandardErrorEncoding = Encoding.UTF8;

        var arguments = startInfo.ArgumentList;
        arguments.Add("--json");

        // A user-level RIPGREP_CONFIG_PATH must not change the output protocol or safety limits.
        arguments.Add("--no-config");
        arguments.Add("--max-filesize");
        arguments.Add("10M");

        var options = request.UserOptions;
        arguments.Add(options.CaseMode switch
        {
            SearchCaseMode.Ignore => "--ignore-case",
            SearchCaseMode.Sensitive => "--case-sensitive",
            _ => "--smart-case",
        });

        if (_settings.SearchHiddenFiles || options.IncludeHidden) arguments.Add("--hidden");
        if (_settings.SearchIgnoredFiles || options.IncludeIgnored) arguments.Add("--no-ignore");

        if (options.FixedStrings) arguments.Add("--fixed-strings");
        if (options.WordRegexp) arguments.Add("--word-regexp");
        if (options.LineRegexp) arguments.Add("--line-regexp");

        AddValues(arguments, "--glob", options.Globs);
        AddValues(arguments, "--iglob", options.CaseInsensitiveGlobs);
        AddValues(arguments, "--type", options.Types);
        AddValues(arguments, "--type-not", options.ExcludedTypes);

        // These keep leading dashes in the pattern and the single search root from becoming options.
        arguments.Add("-e");
        arguments.Add(request.Pattern);
        arguments.Add("--");
        arguments.Add("./");
    }

    private static void AddValues(
        Collection<string> arguments,
        string option,
        IReadOnlyList<string> values)
    {
        foreach (var value in values)
        {
            arguments.Add(option);
            arguments.Add(value);
        }
    }

    private static string Describe(string standardError, int exitCode)
    {
        string? firstLine = null;

        foreach (var rawLine in standardError.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            // for a bad pattern the useful part is "error: unclosed group"; the lines above it are
            // the echoed pattern and a caret pointing at the offending character
            if (line.StartsWith(ErrorPrefix, StringComparison.OrdinalIgnoreCase))
                return line[ErrorPrefix.Length..].Trim();

            firstLine ??= line.StartsWith(ToolPrefix, StringComparison.Ordinal) ? line[ToolPrefix.Length..].Trim() : line;
        }

        return firstLine ?? $"ripgrep exited with code {exitCode}";
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
        catch (NotSupportedException)
        {
        }
    }
}
