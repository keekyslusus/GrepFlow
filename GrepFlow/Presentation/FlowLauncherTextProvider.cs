using System.Globalization;

namespace GrepFlow.Presentation;

public sealed class FlowLauncherTextProvider : ITextProvider
{
    private readonly Func<string, string> _getTranslation;

    public FlowLauncherTextProvider(Func<string, string> getTranslation) => _getTranslation = getTranslation;

    public string Get(string key, params object?[] arguments)
    {
        var template = _getTranslation(key);
        return arguments.Length == 0 ? template : string.Format(CultureInfo.CurrentCulture, template, arguments);
    }
}
