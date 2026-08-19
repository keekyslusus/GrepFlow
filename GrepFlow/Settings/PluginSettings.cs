namespace GrepFlow.Settings;

public sealed class PluginSettings
{
    public bool ShowHints { get; set; } = true;

    public bool SearchIgnoredFiles { get; set; }

    public bool SearchHiddenFiles { get; set; }
}
