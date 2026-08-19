namespace GrepFlow.Presentation;

public interface ITextProvider
{
    string Get(string key, params object?[] arguments);
}

public static class TextKeys
{
    public const string PluginGrepflowPluginName = "plugin_grepflow_plugin_name";
    public const string PluginGrepflowPluginDescription = "plugin_grepflow_plugin_description";
    public const string PluginGrepflowTypeAtLeastCharacters = "plugin_grepflow_type_at_least_characters";
    public const string PluginGrepflowExplorerNotFound = "plugin_grepflow_explorer_not_found";
    public const string PluginGrepflowRipgrepNotFound = "plugin_grepflow_ripgrep_not_found";
    public const string PluginGrepflowRipgrepInstallHint = "plugin_grepflow_ripgrep_install_hint";
    public const string PluginGrepflowRipgrepInstallConfirmTitle = "plugin_grepflow_ripgrep_install_confirm_title";
    public const string PluginGrepflowRipgrepInstallConfirmBody = "plugin_grepflow_ripgrep_install_confirm_body";
    public const string PluginGrepflowRipgrepInstalled = "plugin_grepflow_ripgrep_installed";
    public const string PluginGrepflowRipgrepInstallFailed = "plugin_grepflow_ripgrep_install_failed";
    public const string PluginGrepflowRipgrepUnsupportedArch = "plugin_grepflow_ripgrep_unsupported_arch";
    public const string PluginGrepflowRipgrepInstalling = "plugin_grepflow_ripgrep_installing";
    public const string PluginGrepflowMatches = "plugin_grepflow_matches";
    public const string PluginGrepflowDuration = "plugin_grepflow_duration";
    public const string PluginGrepflowNearestWindow = "plugin_grepflow_nearest_window";
    public const string PluginGrepflowSubtitleSeparator = "plugin_grepflow_subtitle_separator";
    public const string PluginGrepflowSearchFailed = "plugin_grepflow_search_failed";
    public const string PluginGrepflowUnsupportedSearchOption = "plugin_grepflow_unsupported_search_option";
    public const string PluginGrepflowMissingSearchOptionValue = "plugin_grepflow_missing_search_option_value";
    public const string PluginGrepflowUnexpectedSearchPath = "plugin_grepflow_unexpected_search_path";
    public const string PluginGrepflowUnterminatedSearchQuote = "plugin_grepflow_unterminated_search_quote";
    public const string PluginGrepflowLimitNotice = "plugin_grepflow_limit_notice";
    public const string PluginGrepflowLimitNoticeHint = "plugin_grepflow_limit_notice_hint";
    public const string PluginGrepflowMatchSubtitle = "plugin_grepflow_match_subtitle";
    public const string PluginGrepflowRevealInExplorer = "plugin_grepflow_reveal_in_explorer";
    public const string PluginGrepflowOpenTerminalHere = "plugin_grepflow_open_terminal_here";
    public const string PluginGrepflowOpenTerminalFailed = "plugin_grepflow_open_terminal_failed";
    public const string PluginGrepflowBlockedFileOpen = "plugin_grepflow_blocked_file_open";
    public const string PluginGrepflowCopyFullPath = "plugin_grepflow_copy_full_path";
    public const string PluginGrepflowCopyMatchedLine = "plugin_grepflow_copy_matched_line";
    public const string PluginGrepflowHintGlobCs = "plugin_grepflow_hint_glob_cs";
    public const string PluginGrepflowHintTypeCs = "plugin_grepflow_hint_type_cs";
    public const string PluginGrepflowHintWord = "plugin_grepflow_hint_word";
    public const string PluginGrepflowHintFixed = "plugin_grepflow_hint_fixed";
    public const string PluginGrepflowHintNoIgnore = "plugin_grepflow_hint_no_ignore";
    public const string PluginGrepflowHintHidden = "plugin_grepflow_hint_hidden";
    public const string PluginGrepflowSettingsShowHints = "plugin_grepflow_settings_show_hints";
    public const string PluginGrepflowSettingsSearchIgnoredFiles = "plugin_grepflow_settings_search_ignored_files";
    public const string PluginGrepflowSettingsSearchHiddenFiles = "plugin_grepflow_settings_search_hidden_files";
}
