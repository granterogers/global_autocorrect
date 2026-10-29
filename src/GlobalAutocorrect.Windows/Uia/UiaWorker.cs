using System.Collections.Concurrent;

namespace GlobalAutocorrect.Windows.Uia;

/// <summary>
/// Runs UI Automation calls on a dedicated STA thread with a timeout (10.3.b, D11). A hung app can block a COM call
/// indefinitely, so on timeout the thread is abandoned and a fresh one takes over; the caller gets a failure, never a hang.
/// </summary>
public sealed class UiaWorker : IDisposable
{
    private sealed record Job(Func<UiaReader, object?> Work, TaskCompletionSource<object?> Done);

    private BlockingCollection<Job> _jobs = new();
    private Thread _thread;
    private readonly int _timeoutMs;
    private bool _disposed;

    public int Replacements { get; private set; }

    public UiaWorker(int timeoutMs = 500)
    {
        _timeoutMs = timeoutMs;
        _thread = StartThread(_jobs);
    }

    public T? Invoke<T>(Func<UiaReader, T> work, T? onTimeout = default)
    {
        var job = new Job(r => work(r), new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously));
        BlockingCollection<Job> queue;
        lock (this)
        {
            if (_disposed) return onTimeout;
            queue = _jobs;
        }
        queue.Add(job);
        if (job.Done.Task.Wait(_timeoutMs)) return job.Done.Task.Result is T t ? t : onTimeout;

        // Timed out: leave the stuck thread behind (it is a background thread) and start a new one.
        lock (this)
        {
            if (ReferenceEquals(queue, _jobs) && !_disposed)
            {
                _jobs.CompleteAdding();
                _jobs = new BlockingCollection<Job>();
                _thread = StartThread(_jobs);
                Replacements++;
            }
        }
        return onTimeout;
    }

    private static Thread StartThread(BlockingCollection<Job> jobs)
    {
        var t = new Thread(() =>
        {
            var reader = new UiaReader();
            foreach (var job in jobs.GetConsumingEnumerable())
            {
                try { job.Done.TrySetResult(job.Work(reader)); }
                catch (Exception e) { job.Done.TrySetException(e); }
            }
        }) { IsBackground = true, Name = "GA UI Automation" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return t;
    }

    public void Dispose()
    {
        lock (this)
        {
            _disposed = true;
            _jobs.CompleteAdding();
        }
    }
}
