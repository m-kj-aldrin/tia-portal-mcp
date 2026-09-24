using System.Collections.Concurrent;
using System.Diagnostics;

namespace TiaOpennessMcpServer.Utilities;

/// <summary>
/// Runs tasks on a dedicated STA thread. TIA Openness uses COM under the hood
/// and requires all calls to originate from the same STA thread.
/// </summary>
public sealed class StaTaskScheduler : IDisposable
{
    private readonly Thread _staThread;
    private readonly BlockingCollection<Action> _queue = new();
    private volatile bool _disposed;

    public StaTaskScheduler()
    {
        _staThread = new Thread(ThreadLoop)
        {
            Name         = "TIA-Openness-STA",
            IsBackground = true,
        };
        _staThread.SetApartmentState(ApartmentState.STA);
        _staThread.Start();
    }

    internal void VerifyAccess()
    {
        if (Thread.CurrentThread != _staThread)
            throw new InvalidOperationException("TIA Openness access must run on the shared STA worker.");
    }

    /// <summary>Runs <paramref name="action"/> on the STA thread and awaits completion.</summary>
    public Task RunAsync(Action action)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(StaTaskScheduler));
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() => Complete(tcs, action));
        return tcs.Task;
    }

    /// <summary>Runs <paramref name="func"/> on the STA thread and returns its result.</summary>
    public Task<T> RunAsync<T>(Func<T> func)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(StaTaskScheduler));
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    private static void Complete(TaskCompletionSource<bool> tcs, Action action)
    {
        try
        {
            action();
            tcs.TrySetResult(false);
        }
        catch (Exception ex) { tcs.TrySetException(ex); }
    }

    private void ThreadLoop()
    {
        foreach (var action in _queue.GetConsumingEnumerable())
        {
            try { action(); }
            // A failed action completes its own task. This keeps the worker alive if that completion itself fails.
            catch (Exception ex) { Trace.TraceError("STA worker action failed outside its completion source: " + ex.Message); }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.CompleteAdding();
        _staThread.Join(TimeSpan.FromSeconds(5));
        _queue.Dispose();
    }
}
