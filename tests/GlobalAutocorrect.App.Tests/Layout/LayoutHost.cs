using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace GlobalAutocorrect.App.Tests.Layout;

/// <summary>
/// Lays a window's content out at a given size and display scale without ever showing it (D63). The content is taken out
/// of its (never shown) window and put in a hidden HwndSource: WPF treats it as on screen, so Loaded runs, navigation
/// works and the real keyboard navigation can be asked for the tab order, but there is no visible window and nothing can
/// take focus or the keyboard from the user.
/// </summary>
internal sealed class LayoutHost : IDisposable
{
    private readonly HwndSource _source;

    private LayoutHost(Window window, Size size, double scale)
    {
        Window = window;
        var content = (FrameworkElement)window.Content;
        window.Content = null;
        ReplaceTitleBar(content, size.Width);

        // What the window gives its content by inheritance.
        Root = new Border
        {
            Width = size.Width,
            Height = size.Height,
            Child = content,
            DataContext = window.DataContext,
            UseLayoutRounding = window.UseLayoutRounding,
        };
        Root.SetValue(TextElement.FontFamilyProperty, window.FontFamily);
        Root.SetValue(TextElement.FontSizeProperty, window.FontSize);
        Root.SetValue(TextElement.ForegroundProperty, window.Foreground);

        // WS_POPUP without WS_VISIBLE: a window handle that is never shown; far off screen in case anything shows it.
        var p = new HwndSourceParameters("GlobalAutocorrect layout check")
        {
            WindowStyle = unchecked((int)0x80000000),
            PositionX = -20000,
            PositionY = -20000,
        };
        _source = new HwndSource(p) { SizeToContent = SizeToContent.WidthAndHeight, RootVisual = Root };
        // The display scale: layout rounding and text are worked out for this many pixels per DIP.
        VisualTreeHelper.SetRootDpi(Root, new DpiScale(scale, scale));
        Size = size;
        Scale = scale;
        Pump();
    }

    public Window Window { get; }
    public Border Root { get; }
    public Size Size { get; }
    public double Scale { get; }

    public static LayoutHost Of(Window window, Size size, double scale) => new(window, size, scale);

    /// <summary>
    /// WPF-UI's title bar needs its window once loaded (it throws without one), so it is swapped for an empty strip of the
    /// height it measures at. Its minimise, maximise and close buttons are WPF-UI's, not part of the layout under test.
    /// </summary>
    private static void ReplaceTitleBar(FrameworkElement content, double width)
    {
        if (content is not Panel panel) return;
        var bar = panel.Children.OfType<Wpf.Ui.Controls.TitleBar>().SingleOrDefault();
        if (bar is null) return;
        bar.Measure(new Size(width, double.PositiveInfinity));
        var strip = new Border { Height = bar.DesiredSize.Height };
        Grid.SetRow(strip, Grid.GetRow(bar));
        int index = panel.Children.IndexOf(bar);
        panel.Children.RemoveAt(index);
        panel.Children.Insert(index, strip);
    }

    /// <summary>
    /// Lets bindings, templates, Loaded handlers, navigation and layout finish, and waits for animations (the navigation
    /// pane opens with one) until the layout is the same twice in a row, at most about 3 seconds.
    /// </summary>
    public void Pump()
    {
        string? last = null;
        for (int i = 0; i < 60; i++)
        {
            Flush(DispatcherPriority.Loaded);
            Flush(DispatcherPriority.ContextIdle);
            Root.UpdateLayout();
            string now = Signature(Root);
            if (now == last) return;
            last = now;
            Thread.Sleep(50);
        }
        throw new InvalidOperationException("The layout never settled.");
    }

    private static string Signature(Visual root)
    {
        var sb = new System.Text.StringBuilder();
        void Walk(DependencyObject d)
        {
            if (d is FrameworkElement fe)
            {
                var at = fe.TranslatePoint(default, (UIElement)root);
                sb.Append(at.X.ToString("0.#")).Append(',').Append(at.Y.ToString("0.#")).Append(',')
                  .Append(fe.ActualWidth.ToString("0.#")).Append(',').Append(fe.ActualHeight.ToString("0.#")).Append(';');
            }
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) Walk(VisualTreeHelper.GetChild(d, i));
        }
        Walk(root);
        return sb.ToString();
    }

    private static void Flush(DispatcherPriority priority)
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(priority, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    public void Dispose()
    {
        _source.RootVisual = null;
        _source.Dispose();
        Window.Close();
    }
}
