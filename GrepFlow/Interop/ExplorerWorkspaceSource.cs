using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace GrepFlow.Interop;

// late binding is deliberate: keeps a shell interop assembly out of the plugin
public sealed class ExplorerWorkspaceSource : IWorkspaceSource
{
    public const string SourceId = "explorer";

    private const string ProgId = "Shell.Application";

    private readonly StaDispatcher _dispatcher;
    private readonly IExplorerHwndProvider _hwnd;

    private string? _lastKnownFolder;

    public ExplorerWorkspaceSource(StaDispatcher dispatcher, IExplorerHwndProvider hwnd)
    {
        _dispatcher = dispatcher;
        _hwnd = hwnd;
    }

    public string Id => SourceId;

    public string DisplayName => "File Explorer";

    public bool MatchesForeground(IntPtr window) => ExplorerWindow.IsFolderWindow(window);

    public async ValueTask<ActiveFolder?> GetActiveFolderAsync(CancellationToken token)
    {
        var window = _hwnd.CurrentExplorerWindow;
        if (window != IntPtr.Zero && NativeMethods.IsWindow(window))
        {
            var folder = await _dispatcher.InvokeAsync(() => ResolveFolder(window), token).ConfigureAwait(false);
            if (IsUsable(folder)) return Remember(folder!, fromNearestWindow: false);
        }

        // the window went away, or it is a virtual folder such as "This PC" whose Self.Path is a
        // CLSID string rather than a real directory
        if (IsUsable(_lastKnownFolder))
            return new ActiveFolder(_lastKnownFolder!, DisplayName, FromNearestWindow: false);

        // last resort, mainly right after a plugin reload: no foreground event has been seen yet,
        // so any open Explorer window beats telling the user there is none
        var fallback = await _dispatcher.InvokeAsync(ResolveAnyFolder, token).ConfigureAwait(false);
        return IsUsable(fallback) ? Remember(fallback!, fromNearestWindow: true) : null;
    }

    private ActiveFolder Remember(string folder, bool fromNearestWindow)
    {
        _lastKnownFolder = folder;
        return new ActiveFolder(folder, DisplayName, fromNearestWindow);
    }

    private static bool IsUsable(string? folder)
        => !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder);

    private static string? ResolveFolder(IntPtr window) => Enumerate(candidate => HandleOf(candidate) == window);

    private static string? ResolveAnyFolder() => Enumerate(_ => true);

    private static string? Enumerate(Func<object, bool> predicate)
    {
        object? shell = null;
        object? windows = null;
        try
        {
            var shellType = Type.GetTypeFromProgID(ProgId);
            if (shellType is null) return null;

            shell = Activator.CreateInstance(shellType);
            if (shell is null) return null;

            windows = Invoke(shell, "Windows");
            if (windows is null) return null;

            var count = Convert.ToInt32(GetProperty(windows, "Count") ?? 0);
            for (var index = 0; index < count; index++)
            {
                object? window = null;
                try
                {
                    window = Invoke(windows, "Item", index);
                    if (window is null || !predicate(window)) continue;

                    var path = ReadPath(window);
                    if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path)) return path;
                }
                catch (COMException)
                {
                }
                catch (TargetInvocationException)
                {
                }
                finally
                {
                    Release(window);
                }
            }

            return null;
        }
        catch (COMException)
        {
            return null;
        }
        catch (TargetInvocationException)
        {
            return null;
        }
        catch (MissingMemberException)
        {
            return null;
        }
        finally
        {
            // leaked references keep explorer.exe alive
            Release(windows);
            Release(shell);
        }
    }

    private static IntPtr HandleOf(object window)
    {
        var handle = GetProperty(window, "HWND");
        return handle is null ? IntPtr.Zero : new IntPtr(Convert.ToInt64(handle));
    }

    private static string? ReadPath(object window)
    {
        object? document = null;
        object? folder = null;
        object? self = null;
        try
        {
            document = GetProperty(window, "Document");
            if (document is null) return null;

            folder = GetProperty(document, "Folder");
            if (folder is null) return null;

            self = GetProperty(folder, "Self");
            if (self is null) return null;

            return GetProperty(self, "Path") as string;
        }
        catch (COMException)
        {
            return null;
        }
        catch (TargetInvocationException)
        {
            return null;
        }
        catch (MissingMemberException)
        {
            return null;
        }
        finally
        {
            Release(self);
            Release(folder);
            Release(document);
        }
    }

    private static object? GetProperty(object target, string name)
        => target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null);

    private static object? Invoke(object target, string name, params object[] arguments)
        => target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, arguments);

    private static void Release(object? comObject)
    {
        if (comObject is null || !Marshal.IsComObject(comObject)) return;

        try
        {
            Marshal.ReleaseComObject(comObject);
        }
        catch (ArgumentException)
        {
        }
    }
}
