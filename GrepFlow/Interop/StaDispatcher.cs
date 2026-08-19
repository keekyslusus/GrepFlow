using System.Windows.Threading;

namespace GrepFlow.Interop;

// dedicated STA thread with a running message pump. COM (Shell.Application) may only be used from
// such a thread, and the WinEvent hook needs the pump to deliver callbacks.
public sealed class StaDispatcher : IDisposable
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(2);

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private Dispatcher? _dispatcher;

    public StaDispatcher()
    {
        _thread = new Thread(Pump)
        {
            IsBackground = true,
            Name = "GrepFlow STA",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(ShutdownTimeout);
    }

    public async Task<T?> InvokeAsync<T>(Func<T?> callback, CancellationToken token)
    {
        var dispatcher = _dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted) return default;

        try
        {
            return await dispatcher.InvokeAsync(callback, DispatcherPriority.Normal, token).Task.ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            return default;
        }
    }

    public void Post(Action action)
    {
        var dispatcher = _dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted) return;
        dispatcher.InvokeAsync(action, DispatcherPriority.Normal);
    }

    public void Send(Action action)
    {
        var dispatcher = _dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted) return;

        try
        {
            dispatcher.Invoke(action, DispatcherPriority.Send, CancellationToken.None, ShutdownTimeout);
        }
        catch (TaskCanceledException)
        {
        }
        catch (TimeoutException)
        {
        }
    }

    private void Pump()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _ready.Set();
        Dispatcher.Run();
    }

    public void Dispose()
    {
        _dispatcher?.InvokeShutdown();
        _thread.Join(ShutdownTimeout);
        _ready.Dispose();
    }
}
