namespace GlobalAutocorrect.Windows.Input;

/// <summary>
/// D170 (release build on GitHub: KeyHold's 3 s safety release fired late while the thread pool was busy, and keys stayed
/// held): one deadline kept on a thread of its own, so timing that keeps the user's keys from being held too long never
/// waits for a pool thread. <see cref="Schedule"/> replaces the deadline; <see cref="Cancel"/> drops it.
/// </summary>
internal sealed class ClockThread
{
    private readonly string _name;
    private readonly Action _fire;
    private readonly AutoResetEvent _wake = new(false);
    private long _dueAt = long.MaxValue;
    private Thread? _thread;
    private readonly Lock _start = new();

    public ClockThread(string name, Action fire)
    {
        _name = name;
        _fire = fire;
    }

    public void Schedule(int ms)
    {
        Volatile.Write(ref _dueAt, Environment.TickCount64 + Math.Max(1, ms));
        lock (_start)
        {
            if (_thread is null)
            {
                _thread = new Thread(Loop) { IsBackground = true, Name = _name, Priority = ThreadPriority.AboveNormal };
                _thread.Start();
            }
        }
        _wake.Set();
    }

    public void Cancel() => Volatile.Write(ref _dueAt, long.MaxValue);

    private void Loop()
    {
        while (true)
        {
            long due = Volatile.Read(ref _dueAt);
            long now = Environment.TickCount64;
            if (due != long.MaxValue && now >= due)
            {
                if (Interlocked.CompareExchange(ref _dueAt, long.MaxValue, due) == due)
                    try { _fire(); } catch (Exception) { /* never take the clock down */ }
                continue;
            }
            _wake.WaitOne(due == long.MaxValue ? -1 : (int)Math.Min(int.MaxValue, due - now));
        }
    }
}
