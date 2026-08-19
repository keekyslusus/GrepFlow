using System.IO;
using Flow.Launcher.Plugin;
using GrepFlow.Search;

namespace GrepFlow.Presentation;

public sealed class ContextMenuProvider
{
    private readonly ResultActions _actions;
    private readonly ContextMenuIcons _icons;
    private readonly ITextProvider _texts;

    public ContextMenuProvider(ResultActions actions, ContextMenuIcons icons, ITextProvider texts)
    {
        _actions = actions;
        _icons = icons;
        _texts = texts;
    }

    public List<Result> LoadContextMenus(Result selectedResult)
    {
        return selectedResult?.ContextData switch
        {
            SearchFolderContext folder => CreateSearchFolderMenu(folder),
            RipgrepMatch match => CreateMatchMenu(match),
            _ => new List<Result>(),
        };
    }

    private List<Result> CreateSearchFolderMenu(SearchFolderContext folder)
    {
        if (string.IsNullOrWhiteSpace(folder.FolderPath)) return new List<Result>();

        return
        [
            Create(
                TextKeys.PluginGrepflowRevealInExplorer,
                folder.FolderPath,
                _icons.RevealInExplorer,
                () => _actions.OpenFolder(folder.FolderPath)),
            CreateTerminal(folder.FolderPath),
            Create(
                TextKeys.PluginGrepflowCopyFullPath,
                folder.FolderPath,
                _icons.CopyFullPath,
                () => _actions.CopyToClipboard(folder.FolderPath)),
        ];
    }

    private List<Result> CreateMatchMenu(RipgrepMatch match)
    {
        var results = new List<Result>
        {
            Create(TextKeys.PluginGrepflowRevealInExplorer, match.AbsolutePath, _icons.RevealInExplorer, () => _actions.RevealInExplorer(match.AbsolutePath)),
        };

        var directory = Path.GetDirectoryName(match.AbsolutePath);
        if (!string.IsNullOrEmpty(directory)) results.Add(CreateTerminal(directory));

        results.AddRange(
        [
            Create(TextKeys.PluginGrepflowCopyFullPath, match.AbsolutePath, _icons.CopyFullPath, () => _actions.CopyToClipboard(match.AbsolutePath)),
            Create(TextKeys.PluginGrepflowCopyMatchedLine, match.LineText, _icons.CopyMatchedLine, () => _actions.CopyToClipboard(match.LineText)),
        ]);
        return results;
    }

    private Result CreateTerminal(string directory) =>
        Create(TextKeys.PluginGrepflowOpenTerminalHere, directory, _icons.OpenTerminal, () => _actions.OpenTerminal(directory));

    private Result Create(string titleKey, string subTitle, string iconPath, Func<bool> action) => new()
    {
        Title = _texts.Get(titleKey),
        SubTitle = subTitle,
        IcoPath = iconPath,
        Action = _ => action(),
    };
}
