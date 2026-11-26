namespace Glance.Core.Threading;

/// <summary>
/// Runs blocking work (USB, pipes, processes, big files) on the thread pool, one item at a time, in the order it was asked
/// for (review M7). The UI thread hands work over and awaits the result; it never waits for the work itself.
/// <list type="bullet">
/// <item>Ordered: "release the display" asked before "open it again" always runs first, however long it takes.</item>
/// <item>Never on the caller's thread, even when nothing else is queued.</item>
/// <item>An item that throws faults only its own task; the next item still runs.</item>
/// </list>
/// Awaiting the returned task on the UI thread brings the continuation back to the UI thread (Avalonia's and WinForms'
/// synchronization contexts), so UI updates after the await are safe.
/// </summary>
public sealed class SerialWork
{
    private readonly object _lock = new();
    private Task _tail = Task.CompletedTask;
    private int _pending;

    /// <summary>Items asked for that have not finished yet (including the one running).</summary>
    public int Pending => Volatile.Read(ref _pending);

    /// <summary>True while any item is queued or running.</summary>
    public bool Busy => Pending > 0;

    public Task Run(Action work) => Run(() => { work(); return true; });

    public Task<T> Run<T>(Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (_lock)
        {
            Interlocked.Increment(ref _pending);
            // not ExecuteSynchronously: even when the queue is empty, the item is queued to the pool, never run inline here
            var next = _tail.ContinueWith(_ =>
            {
                try { return work(); }
                finally { Interlocked.Decrement(ref _pending); }
            }, CancellationToken.None, TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default);
            _tail = next;
            return next;
        }
    }
}

/// <summary>
/// Marks a method that blocks (USB, pipes, processes, registry, big files) and must only run off the UI thread: from
/// <c>Task.Run</c>, a <see cref="SerialWork"/> item, or another <c>[WorkerThread]</c> method. <c>UiThreadAuditTests</c>
/// checks the editor's and the tray app's source for this.
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class WorkerThreadAttribute : Attribute { }

/// <summary>
/// A value that is slow to find out (for example "is the tray app running?", which enumerates every process), read on the
/// UI thread without waiting: <see cref="Value"/> is the last result, and <see cref="Poke"/> starts at most one new check on
/// the thread pool. A fresh value found elsewhere (<see cref="Report"/>) wins over a check that started before it.
/// </summary>
public sealed class BackgroundProbe<T>
{
    private readonly Func<T> _probe;
    private readonly object _lock = new();
    private T _value;
    private int _generation;
    private int _running;
    private Task _last = Task.CompletedTask;

    public BackgroundProbe(Func<T> probe, T initial) { _probe = probe; _value = initial; }

    /// <summary>The last known value. Never blocks.</summary>
    public T Value { get { lock (_lock) return _value; } }

    /// <summary>The last check started by <see cref="Poke"/> (for tests).</summary>
    internal Task Last { get { lock (_lock) return _last; } }

    /// <summary>Starts a check on the thread pool unless one is still running. Returns at once.</summary>
    /// <returns>True when a new check was started.</returns>
    public bool Poke()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return false;
        int generation;
        lock (_lock) generation = _generation;
        var task = Task.Run(() =>
        {
            try
            {
                var v = _probe();
                lock (_lock) if (generation == _generation) _value = v;   // a Report made meanwhile is newer
            }
            catch { /* keep the last value; the next poke tries again */ }
            finally { Volatile.Write(ref _running, 0); }
        });
        lock (_lock) _last = task;
        return true;
    }

    /// <summary>A value found out just now (off the UI thread, by the caller): use it, and ignore any older check still running.</summary>
    public void Report(T value)
    {
        lock (_lock) { _value = value; _generation++; }
    }
}
