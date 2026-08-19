using System.Windows;
using System.Windows.Controls;
using GrepFlow.Presentation;

namespace GrepFlow.Settings;

public sealed class SettingsPanel : UserControl
{
    private const string PanelMarginResource = "SettingPanelMargin";
    private const string ItemMarginResource = "SettingPanelItemTopBottomMargin";

    public SettingsPanel(PluginSettings settings, ITextProvider texts, Action save)
    {
        var showHints = CreateCheckBox(
            texts.Get(TextKeys.PluginGrepflowSettingsShowHints),
            settings.ShowHints);
        showHints.Click += (_, _) =>
        {
            settings.ShowHints = showHints.IsChecked == true;
            save();
        };

        var searchIgnoredFiles = CreateCheckBox(
            texts.Get(TextKeys.PluginGrepflowSettingsSearchIgnoredFiles),
            settings.SearchIgnoredFiles);
        searchIgnoredFiles.Click += (_, _) =>
        {
            settings.SearchIgnoredFiles = searchIgnoredFiles.IsChecked == true;
            save();
        };

        var searchHiddenFiles = CreateCheckBox(
            texts.Get(TextKeys.PluginGrepflowSettingsSearchHiddenFiles),
            settings.SearchHiddenFiles);
        searchHiddenFiles.Click += (_, _) =>
        {
            settings.SearchHiddenFiles = searchHiddenFiles.IsChecked == true;
            save();
        };

        var panel = new StackPanel
        {
            Children =
            {
                showHints,
                searchIgnoredFiles,
                searchHiddenFiles,
            },
        };
        panel.SetResourceReference(MarginProperty, PanelMarginResource);
        Content = panel;
    }

    private static CheckBox CreateCheckBox(string text, bool isChecked)
    {
        var checkBox = new CheckBox
        {
            Content = text,
            IsChecked = isChecked,
        };
        checkBox.SetResourceReference(MarginProperty, ItemMarginResource);
        return checkBox;
    }
}
