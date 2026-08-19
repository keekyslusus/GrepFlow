namespace GrepFlow.Presentation;

public sealed record ResultActionHost(
    Action<string, string?> OpenDirectory,
    Action<string> CopyToClipboard,
    Action<string, string> ShowError);
