namespace GrepFlow.Presentation;

// IMPORTANT: WHEN ADDING HINTS/TIPS, ENFORCE "HINT XOR NEAREST" AT THE CALL SITE
// (OR IN HintPicker). DO NOT FOLD THAT POLICY INTO Join — Join STAYS A DUMB JOINER.
public static class StatusSubtitle
{
    public static string Join(ITextProvider texts, params string?[] parts)
    {
        var separator = texts.Get(TextKeys.PluginGrepflowSubtitleSeparator);
        return string.Join(separator, parts.Where(static p => !string.IsNullOrWhiteSpace(p)));
    }
}
