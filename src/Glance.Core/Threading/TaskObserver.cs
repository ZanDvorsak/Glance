namespace Glance.Core.Threading;

/// <summary>
/// For work the UI starts and does not await (review M7 L1): a failure must not vanish. <see cref="Observe"/> calls the handler
/// with the exception, on the synchronization context that was current when it was called (the UI thread, so the handler may
/// reset the UI), or on the thread pool when there was none.
/// </summary>
public static class TaskObserver
{
    public static void Observe(Task task, Action<Exception> onError)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(onError);
        var context = SynchronizationContext.Current;
        task.ContinueWith(t =>
        {
            var ex = t.Exception!.GetBaseException();
            if (context is null) Run(onError, ex);
            else context.Post(_ => Run(onError, ex), null);
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static void Run(Action<Exception> onError, Exception ex)
    {
        try { onError(ex); }
        catch { /* the handler itself failed: nothing more can be done here */ }
    }
}

/// <summary>
/// One piece of work that is asked for again and again (the tray's display check every 2 s, "Reload theme" clicked several
/// times) and must not pile up behind a slow item on a <see cref="SerialWork"/>: <see cref="Request"/> queues it unless it is
/// already queued and has not started yet. A request made while it runs queues one more run.
/// </summary>
public sealed class CoalescedWork
{
    private readonly SerialWork _queue;
    private readonly Action _work;
    private int _queued;

    public CoalescedWork(SerialWork queue, Action work) { _queue = queue; _work = work; }

    /// <returns>The queued run, or null when one was already waiting (that one covers this request).</returns>
    public Task? Request()
    {
        if (Interlocked.Exchange(ref _queued, 1) == 1) return null;
        return _queue.Run(() => { Volatile.Write(ref _queued, 0); _work(); });
    }
}
