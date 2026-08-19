using System.Collections.Generic;
using System.Windows.Controls;
using Flow.Launcher.Plugin;
using GrepFlow.Presentation;

namespace GrepFlow;

public sealed class Main : IAsyncPlugin, IPluginI18n, IContextMenu, ISettingProvider, IDisposable
{
    private PluginRuntime? _runtime;
    private ITextProvider? _texts;

    public Task InitAsync(PluginInitContext context)
    {
        _texts = CompositionRoot.CreateTextProvider(context);
        _runtime = CompositionRoot.Create(context, _texts);
        return Task.CompletedTask;
    }

    public Task<List<Result>> QueryAsync(Query query, CancellationToken token)
        => _runtime is null
            ? Task.FromResult(new List<Result>())
            : _runtime.Coordinator.QueryAsync(query.Search ?? string.Empty, token);

    public List<Result> LoadContextMenus(Result selectedResult)
        => _runtime?.ContextMenus.LoadContextMenus(selectedResult) ?? new List<Result>();

    public Control CreateSettingPanel()
        => _runtime?.CreateSettingPanel() ?? new UserControl();

    public string GetTranslatedPluginTitle() => _texts?.Get(TextKeys.PluginGrepflowPluginName) ?? string.Empty;

    public string GetTranslatedPluginDescription() => _texts?.Get(TextKeys.PluginGrepflowPluginDescription) ?? string.Empty;

    public void Dispose()
    {
        _runtime?.Dispose();
        _runtime = null;
    }
}
