using GrepFlow.Search;
using GrepFlow.Settings;

namespace GrepFlow.Presentation;

public sealed record HintContext(
    int MatchCount,
    bool LimitReached,
    bool FromNearestWindow,
    RipgrepUserOptions UserOptions,
    string Pattern,
    bool RipgrepReportedFailure);

public sealed class HintPicker
{
    private const int EvergreenEveryNSearches = 8;

    private const string GlobCs = "glob_cs";
    private const string TypeCs = "type_cs";
    private const string Word = "word";
    private const string Fixed = "fixed";
    private const string NoIgnore = "no_ignore";
    private const string Hidden = "hidden";

    private static readonly string[] LimitTips = [GlobCs, TypeCs];
    private static readonly string[] ZeroMatchRotateTips = [Hidden, NoIgnore];
    private static readonly string[] EvergreenTips = [GlobCs, TypeCs, Word, Fixed, NoIgnore, Hidden];

    private readonly PluginSettings _settings;

    private int _limitIndex;
    private int _zeroMatchIndex;
    private int _evergreenIndex;
    private int _eligibleSinceEvergreen;

    public HintPicker(PluginSettings settings) => _settings = settings;

    public string? Pick(HintContext context, ITextProvider texts)
    {
        if (!_settings.ShowHints) return null;
        if (context.FromNearestWindow) return null;

        if (context.RipgrepReportedFailure)
            return TryResolve([Fixed], 0, context.UserOptions, texts, out _);

        if (context.LimitReached)
        {
            var tip = TryResolve(LimitTips, _limitIndex, context.UserOptions, texts, out var next);
            if (tip is not null) _limitIndex = next;
            return tip;
        }

        if (context.MatchCount == 0)
            return PickZeroMatches(context, texts);

        if (context.MatchCount > 0
            && !context.LimitReached
            && !context.UserOptions.HasAnyOption)
        {
            _eligibleSinceEvergreen++;
            if (_eligibleSinceEvergreen < EvergreenEveryNSearches) return null;

            var tip = TryResolve(EvergreenTips, _evergreenIndex, context.UserOptions, texts, out var next);
            if (tip is not null)
            {
                _evergreenIndex = next;
                _eligibleSinceEvergreen = 0;
            }

            return tip;
        }

        return null;
    }

    private string? PickZeroMatches(HintContext context, ITextProvider texts)
    {
        if (LooksRegexMeta(context.Pattern))
        {
            var fixedTip = TryResolve([Fixed], 0, context.UserOptions, texts, out _);
            if (fixedTip is not null) return fixedTip;
        }

        var tip = TryResolve(ZeroMatchRotateTips, _zeroMatchIndex, context.UserOptions, texts, out var next);
        if (tip is not null) _zeroMatchIndex = next;
        return tip;
    }

    private string? TryResolve(
        string[] tips,
        int startIndex,
        RipgrepUserOptions options,
        ITextProvider texts,
        out int nextIndex)
    {
        nextIndex = startIndex;
        if (tips.Length == 0) return null;

        var start = Mod(startIndex, tips.Length);
        for (var i = 0; i < tips.Length; i++)
        {
            var tipId = tips[(start + i) % tips.Length];
            if (IsAlreadyUsed(tipId, options)) continue;

            nextIndex = (start + i + 1) % tips.Length;
            return texts.Get(TextKey(tipId));
        }

        return null;
    }

    private static int Mod(int value, int modulus)
    {
        var result = value % modulus;
        return result < 0 ? result + modulus : result;
    }

    private static string TextKey(string tipId) => tipId switch
    {
        GlobCs => TextKeys.PluginGrepflowHintGlobCs,
        TypeCs => TextKeys.PluginGrepflowHintTypeCs,
        Word => TextKeys.PluginGrepflowHintWord,
        Fixed => TextKeys.PluginGrepflowHintFixed,
        NoIgnore => TextKeys.PluginGrepflowHintNoIgnore,
        Hidden => TextKeys.PluginGrepflowHintHidden,
        _ => throw new ArgumentOutOfRangeException(nameof(tipId), tipId, null),
    };

    private static bool LooksRegexMeta(string pattern)
    {
        foreach (var c in pattern)
        {
            if (c is '.' or '*' or '+' or '?' or '[' or ']' or '(' or ')' or '{' or '}' or '|' or '^' or '$' or '\\')
                return true;
        }

        return false;
    }

    private bool IsAlreadyUsed(string tipId, RipgrepUserOptions options) => tipId switch
    {
        GlobCs => options.Globs.Count > 0 || options.CaseInsensitiveGlobs.Count > 0,
        TypeCs => options.Types.Count > 0,
        Word => options.WordRegexp,
        Fixed => options.FixedStrings,
        NoIgnore => _settings.SearchIgnoredFiles || options.IncludeIgnored,
        Hidden => _settings.SearchHiddenFiles || options.IncludeHidden,
        _ => false,
    };
}
