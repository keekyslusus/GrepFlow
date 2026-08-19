using System.IO;

namespace GrepFlow.Interop;

public sealed class FileOpenSafetyPolicy
{
    private static readonly HashSet<string> TextScriptExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cmd", ".bat", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh",
        ".hta", ".reg", ".scf",
    };

    private static readonly HashSet<string> RevealOnlyExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".msi", ".msp", ".mst", ".scr", ".cpl", ".lnk", ".url",
        ".application", ".jar",
    };

    public bool IsTextScript(string path) =>
        TextScriptExtensions.Contains(Path.GetExtension(path));

    public bool RequiresReveal(string path) =>
        RevealOnlyExtensions.Contains(Path.GetExtension(path));
}
