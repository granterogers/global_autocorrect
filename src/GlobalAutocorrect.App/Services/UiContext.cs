namespace GlobalAutocorrect.App.Services;

/// <summary>
/// Runs work on the thread a view model was created on (the UI thread in the app). With no synchronisation context, as in
/// unit tests, it runs straight away.
/// </summary>
public sealed class UiContext
{
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;

    public void Post(Action action)
    {
        if (_context is null || SynchronizationContext.Current == _context) action();
        else _context.Post(_ => action(), null);
    }
}

/// <summary><see cref="IProgress{T}"/> that reports on the creating thread, in order, without a thread-pool hop.</summary>
public sealed class UiProgress<T>(Action<T> handler) : IProgress<T>
{
    private readonly UiContext _ui = new();
    public void Report(T value) => _ui.Post(() => handler(value));
}
