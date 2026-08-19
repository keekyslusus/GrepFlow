using System.IO;

namespace GrepFlow.Search;

public sealed class RipgrepLocator
{
    private const string ExecutableName = "rg.exe";
    private const string PluginSettingsFolderName = "GrepFlow";

    private readonly string _pluginDirectory;
    private readonly string? _dataRoot;

    public RipgrepLocator(string pluginDirectory, string? dataRoot = null)
    {
        _pluginDirectory = pluginDirectory;
        _dataRoot = !string.IsNullOrWhiteSpace(dataRoot)
            ? dataRoot
            : TryGetDataRoot(pluginDirectory);
    }

    public string InstallDirectory
    {
        get
        {
            if (_dataRoot is not null)
            {
                return Path.Combine(_dataRoot, "Settings", "Plugins", PluginSettingsFolderName, "rg");
            }

            return LegacyInstallDirectory;
        }
    }

    public string InstalledExecutablePath => Path.Combine(InstallDirectory, ExecutableName);

    private string LegacyInstallDirectory => Path.Combine(_pluginDirectory, "Tools", "rg");

    private string LegacyExecutablePath => Path.Combine(LegacyInstallDirectory, ExecutableName);

    public static string? TryGetDataRoot(string pluginDirectory)
    {
        if (string.IsNullOrWhiteSpace(pluginDirectory)) return null;

        var pluginsDir = Directory.GetParent(pluginDirectory);
        if (pluginsDir is null || !pluginsDir.Name.Equals("Plugins", StringComparison.OrdinalIgnoreCase))
            return null;

        return pluginsDir.Parent?.FullName;
    }

    public string? Locate()
    {
        if (_dataRoot is not null)
        {
            var settingsPath = Path.Combine(
                _dataRoot, "Settings", "Plugins", PluginSettingsFolderName, "rg", ExecutableName);
            if (File.Exists(settingsPath)) return settingsPath;
        }

        var onPath = FindOnPath();
        if (onPath is not null) return onPath;

        if (File.Exists(LegacyExecutablePath)) return LegacyExecutablePath;

        return null;
    }

    private static string? FindOnPath()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string candidate;
            try
            {
                candidate = Path.Combine(directory, ExecutableName);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }
}
