using Flow.Launcher.Plugin;
using GrepFlow.Search;

namespace GrepFlow.Presentation;

public sealed class ResultFactory
{
    private readonly LineWindow _lineWindow;
    private readonly ResultActions _actions;
    private readonly ITextProvider _texts;
    private readonly RipgrepInstaller _installer;
    private readonly string _iconPath;
    private readonly string _ripgrepNotFoundIconPath;

    public ResultFactory(
        LineWindow lineWindow,
        ResultActions actions,
        ITextProvider texts,
        RipgrepInstaller installer,
        string iconPath,
        string ripgrepNotFoundIconPath)
    {
        _lineWindow = lineWindow;
        _actions = actions;
        _texts = texts;
        _installer = installer;
        _iconPath = iconPath;
        _ripgrepNotFoundIconPath = ripgrepNotFoundIconPath;
    }

    public Result CreateStatus(string title, string subTitle, string? folder, bool noMatches = false) => new()
    {
        Title = title,
        SubTitle = subTitle,
        IcoPath = noMatches ? _ripgrepNotFoundIconPath : _iconPath,
        Score = int.MaxValue,
        ContextData = folder is null ? null : new SearchFolderContext(folder),
        Action = _ => folder is not null && _actions.OpenFolder(folder),
    };

    public Result CreateInstallRipgrepResult() => new()
    {
        Title = _texts.Get(TextKeys.PluginGrepflowRipgrepNotFound),
        SubTitle = _texts.Get(TextKeys.PluginGrepflowRipgrepInstallHint),
        IcoPath = _ripgrepNotFoundIconPath,
        Score = int.MaxValue,
        AsyncAction = async _ => await _installer.PromptAndInstallAsync().ConfigureAwait(false),
    };

    public Result CreateMatch(RipgrepMatch match, int score)
    {
        var window = _lineWindow.Create(match.LineText, match.MatchStart, match.MatchLength);

        return new Result
        {
            Title = window.Text,
            TitleHighlightData = BuildHighlight(window),
            SubTitle = _texts.Get(TextKeys.PluginGrepflowMatchSubtitle, match.RelativePath, match.LineNumber),
            IcoPath = ResolveMatchIcon(match),
            Score = score,
            CopyText = match.AbsolutePath,
            ContextData = match,
            Action = _ => _actions.OpenMatch(match),
        };
    }

    public Result CreateLimitNotice(int limit) => new()
    {
        Title = _texts.Get(TextKeys.PluginGrepflowLimitNotice, limit),
        SubTitle = _texts.Get(TextKeys.PluginGrepflowLimitNoticeHint),
        IcoPath = _iconPath,
        Score = 0,
        Action = _ => false,
    };

    private string ResolveMatchIcon(RipgrepMatch match) =>
        string.IsNullOrWhiteSpace(match.AbsolutePath) ? _iconPath : match.AbsolutePath;

    private static List<int> BuildHighlight(LineWindowResult window)
    {
        var highlight = new List<int>(window.MatchLength);
        for (var offset = 0; offset < window.MatchLength; offset++) highlight.Add(window.MatchStart + offset);
        return highlight;
    }
}
