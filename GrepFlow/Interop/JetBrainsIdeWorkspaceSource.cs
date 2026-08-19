using System.IO;
using System.Text;

namespace GrepFlow.Interop;

internal sealed record JetBrainsIdeProcessWindow(IntPtr Window, uint ProcessId, string ImagePath);

internal sealed class JetBrainsIdeWorkspaceSource : IWorkspaceSource
{
    private readonly JetBrainsIdeProfile _profile;
    private readonly Func<IntPtr, JetBrainsIdeProcessWindow?> _inspectWindow;
    private readonly Func<JetBrainsIdeProcessWindow, string?> _readProjectFolder;
    private IntPtr _window;

    public JetBrainsIdeWorkspaceSource(JetBrainsIdeProfile profile, JetBrainsIdeWorkspaceReader reader)
        : this(profile, window => TryInspectIdeWindow(window, profile), reader.TryReadProjectFolder)
    {
    }

    internal JetBrainsIdeWorkspaceSource(
        JetBrainsIdeProfile profile,
        Func<IntPtr, JetBrainsIdeProcessWindow?> inspectWindow,
        Func<JetBrainsIdeProcessWindow, string?> readProjectFolder)
    {
        _profile = profile;
        _inspectWindow = inspectWindow;
        _readProjectFolder = readProjectFolder;
    }

    public string Id => _profile.SourceId;

    public string DisplayName => _profile.DisplayName;

    public bool MatchesForeground(IntPtr window)
    {
        if (_inspectWindow(window) is null) return false;

        Volatile.Write(ref _window, window);
        return true;
    }

    public ValueTask<ActiveFolder?> GetActiveFolderAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        var window = Volatile.Read(ref _window);
        if (window == IntPtr.Zero) return ValueTask.FromResult<ActiveFolder?>(null);

        var processWindow = _inspectWindow(window);
        if (processWindow is null)
        {
            Interlocked.CompareExchange(ref _window, IntPtr.Zero, window);
            return ValueTask.FromResult<ActiveFolder?>(null);
        }

        var path = _readProjectFolder(processWindow);
        if (path is null || !Directory.Exists(path))
            return ValueTask.FromResult<ActiveFolder?>(null);

        return ValueTask.FromResult<ActiveFolder?>(
            new ActiveFolder(path, DisplayName, FromNearestWindow: false));
    }

    private static JetBrainsIdeProcessWindow? TryInspectIdeWindow(IntPtr window, JetBrainsIdeProfile profile)
    {
        if (!IsProjectFrame(window)) return null;

        var imagePath = ForegroundProcess.TryGetImagePath(window);
        var imageName = imagePath is null ? null : Path.GetFileName(imagePath);
        if (!MatchesImageName(profile, imageName))
            return null;

        NativeMethods.GetWindowThreadProcessId(window, out var processId);
        return processId == 0 ? null : new JetBrainsIdeProcessWindow(window, processId, imagePath!);
    }

    internal static bool MatchesImageName(JetBrainsIdeProfile profile, string? imageName)
        => imageName is not null && profile.ExecutableFileNames.Contains(imageName);

    internal static bool IsProjectFrame(string? className, IntPtr owner)
        => string.Equals(className, "SunAwtFrame", StringComparison.Ordinal) &&
           owner == IntPtr.Zero;

    private static bool IsProjectFrame(IntPtr window)
    {
        var className = new StringBuilder(64);
        return NativeMethods.GetClassName(window, className, className.Capacity) > 0 &&
               IsProjectFrame(
                   className.ToString(),
                   NativeMethods.GetWindow(window, NativeMethods.GW_OWNER));
    }
}
