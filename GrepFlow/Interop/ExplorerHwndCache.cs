namespace GrepFlow.Interop;

public sealed class ExplorerHwndCache : IExplorerHwndProvider
{
    private IntPtr _window;

    public IntPtr CurrentExplorerWindow => Volatile.Read(ref _window);

    public void Capture(IntPtr window) => Volatile.Write(ref _window, window);
}
