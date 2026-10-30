using System.Windows;
using System.Windows.Threading;
using Xunit;

namespace GlobalAutocorrect.App.Tests;

/// <summary>The shared test App must never run the real start-up, and must survive the dispatcher pumping.</summary>
public class WpfAppTests
{
    [Fact]
    public void R11_1_TheTestAppNeverStartsTheRealApp()
    {
        bool shuttingDown = true;
        WpfApp.Run(app =>
        {
            var frame = new DispatcherFrame();
            app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);   // lets anything queued run, as a page test that waits for bindings does
            shuttingDown = app.Dispatcher.HasShutdownStarted
                || (bool)typeof(Application).GetField("_appIsShutdown", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(app)!;
        });
        Assert.False(WpfApp.StartupRan, "App.OnStartup ran inside the tests (it starts the keyboard hook and the model)");
        Assert.False(shuttingDown, "the shared test App was shut down");
    }
}
