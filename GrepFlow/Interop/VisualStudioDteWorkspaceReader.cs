using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace GrepFlow.Interop;

public sealed class VisualStudioDteWorkspaceReader
{
    private readonly Func<IntPtr, uint> _getProcessId;
    private readonly Func<uint, object?> _findDte;

    public VisualStudioDteWorkspaceReader()
        : this(GetProcessId, VisualStudioRunningObjectTable.TryGetDte)
    {
    }

    internal VisualStudioDteWorkspaceReader(
        Func<IntPtr, uint> getProcessId,
        Func<uint, object?> findDte)
    {
        _getProcessId = getProcessId;
        _findDte = findDte;
    }

    public string? TryReadWorkspace(IntPtr window)
    {
        object? dte = null;
        object? mainWindow = null;
        object? solution = null;
        try
        {
            var processId = _getProcessId(window);
            if (processId == 0) return null;

            dte = _findDte(processId);
            if (dte is null) return null;

            mainWindow = GetProperty(dte, "MainWindow");
            var dteWindow = GetProperty(mainWindow, "HWnd");
            if (dteWindow is null || !IsSameWindow(dteWindow, window))
                return null;

            solution = GetProperty(dte, "Solution");
            var fullName = GetProperty(solution, "FullName") as string;
            return ResolveWorkspacePath(fullName);
        }
        catch (COMException)
        {
            return null;
        }
        catch (InvalidComObjectException)
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
        catch (InvalidCastException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
        catch (OverflowException)
        {
            return null;
        }
        finally
        {
            Release(solution);
            Release(mainWindow);
            Release(dte);
        }
    }

    internal static string? ResolveWorkspacePath(string? fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName) || !Path.IsPathFullyQualified(fullName)) return null;
        if (Directory.Exists(fullName)) return fullName;
        if (!File.Exists(fullName)) return null;

        var directory = Path.GetDirectoryName(fullName);
        return !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory) ? directory : null;
    }

    private static uint GetProcessId(IntPtr window)
    {
        var threadId = NativeMethods.GetWindowThreadProcessId(window, out var processId);
        return threadId == 0 ? 0 : processId;
    }

    private static object? GetProperty(object? target, string name)
        => target?.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null);

    private static bool IsSameWindow(object dteWindow, IntPtr window)
    {
        var dteHandle = Convert.ToInt64(dteWindow, CultureInfo.InvariantCulture);
        return unchecked((uint)dteHandle) == unchecked((uint)window.ToInt64());
    }

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

internal static class VisualStudioRunningObjectTable
{
    private const string DteMonikerPrefix = "!VisualStudio.DTE.";

    public static object? TryGetDte(uint processId)
    {
        IRunningObjectTable? table = null;
        IEnumMoniker? enumerator = null;
        IBindCtx? bindContext = null;
        try
        {
            Marshal.ThrowExceptionForHR(NativeMethods.GetRunningObjectTable(0, out table));
            if (table is null) return null;

            table.EnumRunning(out enumerator);
            if (enumerator is null) return null;

            Marshal.ThrowExceptionForHR(NativeMethods.CreateBindCtx(0, out bindContext));
            if (bindContext is null) return null;

            var monikers = new IMoniker[1];
            while (enumerator.Next(1, monikers, IntPtr.Zero) == 0)
            {
                var moniker = monikers[0];
                try
                {
                    moniker.GetDisplayName(bindContext, null, out var displayName);
                    if (!MatchesDteMoniker(displayName, processId)) continue;

                    Marshal.ThrowExceptionForHR(table.GetObject(moniker, out var dte));
                    return dte;
                }
                catch (COMException)
                {
                }
                catch (InvalidComObjectException)
                {
                }
                finally
                {
                    Release(moniker);
                    monikers[0] = null!;
                }
            }

            return null;
        }
        catch (COMException)
        {
            return null;
        }
        catch (InvalidComObjectException)
        {
            return null;
        }
        finally
        {
            Release(bindContext);
            Release(enumerator);
            Release(table);
        }
    }

    internal static bool MatchesDteMoniker(string? displayName, uint processId)
    {
        if (string.IsNullOrEmpty(displayName) ||
            !displayName.StartsWith(DteMonikerPrefix, StringComparison.Ordinal))
            return false;

        var separator = displayName.LastIndexOf(':');
        if (separator <= DteMonikerPrefix.Length || separator == displayName.Length - 1) return false;

        var versionText = displayName[DteMonikerPrefix.Length..separator];
        var processIdText = displayName[(separator + 1)..];
        return Version.TryParse(versionText, out _) &&
            uint.TryParse(processIdText, NumberStyles.None, CultureInfo.InvariantCulture, out var candidate) &&
            candidate == processId;
    }

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
