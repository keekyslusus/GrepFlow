using System.IO;
using System.Text.Json;

namespace GrepFlow.Interop;

internal sealed record JetBrainsIdeState(string SystemDirectory, string ConfigDirectory, string LogPath);

internal sealed class JetBrainsIdeStateLocator
{
    private readonly string _localAppData;
    private readonly string _appData;
    private readonly string _vendorDirectory;
    private readonly string _stateDirectoryPrefix;
    private readonly PluginLog? _log;
    private string? _lastResolvedPath;
    private string? _lastWarnFingerprint;

    public JetBrainsIdeStateLocator(
        string vendorDirectory,
        string stateDirectoryPrefix,
        PluginLog? log = null)
        : this(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            vendorDirectory,
            stateDirectoryPrefix,
            log)
    {
    }

    internal JetBrainsIdeStateLocator(
        string localAppData,
        string appData,
        string vendorDirectory,
        string stateDirectoryPrefix,
        PluginLog? log = null)
    {
        _localAppData = localAppData;
        _appData = appData;
        _vendorDirectory = vendorDirectory;
        _stateDirectoryPrefix = stateDirectoryPrefix;
        _log = log;
    }

    public JetBrainsIdeState? TryLocate(JetBrainsIdeProcessWindow process)
    {
        try
        {
            var dataDirectoryName = TryReadDataDirectoryNameSafely(process.ImagePath);
            if (dataDirectoryName is not null)
            {
                var defaultSystem = Path.Combine(_localAppData, _vendorDirectory, dataDirectoryName);
                if (PidMatchesSafely(defaultSystem, process.ProcessId))
                    return CreateState(defaultSystem, dataDirectoryName);
            }

            var vendorRoot = Path.Combine(_localAppData, _vendorDirectory);
            if (!Directory.Exists(vendorRoot)) return null;

            foreach (var candidate in Directory.EnumerateDirectories(
                         vendorRoot,
                         _stateDirectoryPrefix + "*",
                         SearchOption.TopDirectoryOnly))
            {
                if (!PidMatchesSafely(candidate, process.ProcessId)) continue;
                return CreateState(candidate, Path.GetFileName(candidate));
            }

            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            WarnOnce(exception);
            return null;
        }
    }

    private JetBrainsIdeState CreateState(string systemDirectory, string dataDirectoryName)
    {
        var state = new JetBrainsIdeState(
            systemDirectory,
            Path.Combine(_appData, _vendorDirectory, dataDirectoryName),
            Path.Combine(systemDirectory, "log", "idea.log"));

        if (!string.Equals(_lastResolvedPath, state.LogPath, StringComparison.OrdinalIgnoreCase))
        {
            _lastResolvedPath = state.LogPath;
            _log?.Info(nameof(JetBrainsIdeStateLocator), $"JetBrains IDE log resolved to {state.LogPath}");
        }

        return state;
    }

    private string? TryReadDataDirectoryNameSafely(string imagePath)
    {
        try
        {
            return TryReadDataDirectoryName(imagePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            WarnOnce(exception);
            return null;
        }
    }

    private static string? TryReadDataDirectoryName(string imagePath)
    {
        var binDirectory = Path.GetDirectoryName(imagePath);
        var installRoot = binDirectory is null ? null : Directory.GetParent(binDirectory)?.FullName;
        if (installRoot is null) return null;

        var productInfoPath = Path.Combine(installRoot, "product-info.json");
        if (!File.Exists(productInfoPath)) return null;

        using var stream = new FileStream(
            productInfoPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var document = JsonDocument.Parse(stream);
        if (!document.RootElement.TryGetProperty("dataDirectoryName", out var value) ||
            value.ValueKind != JsonValueKind.String)
            return null;

        var dataDirectoryName = value.GetString();
        return IsSafePathComponent(dataDirectoryName)
            ? dataDirectoryName
            : null;
    }

    private static bool IsSafePathComponent(string? value)
        => !string.IsNullOrWhiteSpace(value) &&
           value is not "." and not ".." &&
           value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
           string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal);

    private bool PidMatchesSafely(string systemDirectory, uint expectedProcessId)
    {
        try
        {
            return PidMatches(systemDirectory, expectedProcessId);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            WarnOnce(exception);
            return false;
        }
    }

    private static bool PidMatches(string systemDirectory, uint expectedProcessId)
    {
        var pidPath = Path.Combine(systemDirectory, ".pid");
        if (!File.Exists(pidPath)) return false;

        using var stream = new FileStream(
            pidPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return uint.TryParse(reader.ReadToEnd().Trim(), out var actualProcessId) &&
               actualProcessId == expectedProcessId;
    }

    private void WarnOnce(Exception exception)
    {
        var fingerprint = $"{exception.GetType().Name}:{exception.Message}";
        if (string.Equals(_lastWarnFingerprint, fingerprint, StringComparison.Ordinal)) return;

        _lastWarnFingerprint = fingerprint;
        _log?.Warn(
            nameof(JetBrainsIdeStateLocator),
            $"could not locate JetBrains IDE state: {exception.GetType().Name}: {exception.Message}");
    }
}
