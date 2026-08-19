using GrepFlow.Search;

namespace GrepFlow.Interop;

public interface IMatchOpener
{
    MatchOpenOutcome Open(RipgrepMatch match);
}

public enum MatchOpenOutcome
{
    Opened,
    OpenWithShown,
    Blocked,
}

public sealed class MatchOpener : IMatchOpener
{
    private readonly IFileAssociationResolver _associations;
    private readonly IReadOnlyList<IAssociatedApplicationLauncher> _associatedLaunchers;
    private readonly FileOpenSafetyPolicy _safetyPolicy;
    private readonly IExecutableFileOpener _genericExecutableOpener;
    private readonly IFileOpener _textScriptFallback;
    private readonly IFileOpener _fallback;

    public MatchOpener(
        IFileAssociationResolver associations,
        IReadOnlyList<IAssociatedApplicationLauncher> associatedLaunchers,
        FileOpenSafetyPolicy safetyPolicy,
        IExecutableFileOpener genericExecutableOpener,
        IFileOpener textScriptFallback,
        IFileOpener fallback)
    {
        _associations = associations;
        _associatedLaunchers = associatedLaunchers;
        _safetyPolicy = safetyPolicy;
        _genericExecutableOpener = genericExecutableOpener;
        _textScriptFallback = textScriptFallback;
        _fallback = fallback;
    }

    public MatchOpenOutcome Open(RipgrepMatch match)
    {
        if (_safetyPolicy.RequiresReveal(match.AbsolutePath))
            return MatchOpenOutcome.Blocked;

        if (TryOpenWithAssociatedEditor(match.AbsolutePath, match))
            return MatchOpenOutcome.Opened;

        if (_safetyPolicy.IsTextScript(match.AbsolutePath))
        {
            var textExecutable = _associations.ResolveDefaultExecutable("fallback.txt");
            if (textExecutable is not null)
            {
                if (TryOpenWithEditor(textExecutable, match)
                    || _genericExecutableOpener.TryOpen(textExecutable, match.AbsolutePath))
                    return MatchOpenOutcome.Opened;
            }

            _textScriptFallback.Open(match.AbsolutePath);
            return MatchOpenOutcome.Opened;
        }

        _fallback.Open(match.AbsolutePath);
        return MatchOpenOutcome.OpenWithShown;
    }

    private bool TryOpenWithAssociatedEditor(string associationPath, RipgrepMatch match)
    {
        var executable = _associations.ResolveDefaultExecutable(associationPath);
        return executable is not null && TryOpenWithEditor(executable, match);
    }

    private bool TryOpenWithEditor(string executable, RipgrepMatch match)
    {
        foreach (var launcher in _associatedLaunchers)
        {
            if (launcher.Recognizes(executable) && launcher.TryLaunch(executable, match))
                return true;
        }

        return false;
    }
}
