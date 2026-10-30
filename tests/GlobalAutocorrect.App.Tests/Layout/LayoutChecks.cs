using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace GlobalAutocorrect.App.Tests.Layout;

/// <summary>
/// Checks on a laid-out window (9.5, D63). Each returns one line per problem found, empty when all is well. They look at
/// what WPF actually laid out: positions, clips, automation names and WPF's own tab navigation.
/// </summary>
internal static class LayoutChecks
{
    private const double Tolerance = 1.0;

    /// <summary>
    /// The page's rows (the panel holding its title) must sit in a ScrollViewer that scrolls vertically, and turning the
    /// mouse wheel over the page until it stops must bring the lowest row fully into view.
    /// </summary>
    public static List<string> Scrolling(LayoutHost host, FrameworkElement rows)
    {
        var problems = new List<string>();
        if (!Ancestors(rows).OfType<ScrollViewer>().Any(sv => sv.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled))
            problems.Add("the page is not inside a ScrollViewer that scrolls vertically");

        var last = Leaves(rows).OrderBy(e => Bounds(e, host.Root).Bottom).LastOrDefault();
        if (last is null) return problems;

        // The pointer rests over the page's title, as a user's would; wheel events go where the input system sends them.
        var title = Leaves(rows).OrderBy(e => Bounds(e, host.Root).Top).First();
        // Each turn is laid out at once; when the page stops moving, everything queued gets to run before it counts as stopped.
        string Offsets() => string.Join(",", Descendants(host.Root).OfType<ScrollViewer>().Select(sv => sv.VerticalOffset.ToString("0.#", CultureInfo.InvariantCulture)));
        string before = Offsets();
        for (int i = 0; i < 200; i++)
        {
            Wheel(title, -120);
            host.Root.UpdateLayout();
            string now = Offsets();
            if (now == before)
            {
                host.Pump();
                now = Offsets();
                if (now == before) break;
            }
            before = now;
        }

        var bounds = Bounds(last, host.Root);
        var visible = VisibleArea(last, host);
        if (!Contains(visible, bounds))
            problems.Add($"after scrolling to the end, the last row ({Describe(last)}) is not fully in view: {Fmt(bounds)} outside {Fmt(visible)}");
        return problems;
    }

    /// <summary>
    /// No text is clipped (by its own box or any container) or cut short with an ellipsis, nothing runs off the window, and
    /// nothing visible is laid out with no width or height.
    /// </summary>
    public static List<string> Clipping(LayoutHost host, FrameworkElement region)
    {
        var problems = new List<string>();
        foreach (var e in Leaves(region))
        {
            if (e.ActualWidth <= 0.5 || e.ActualHeight <= 0.5)
            {
                problems.Add($"{Describe(e)} is laid out at {e.ActualWidth:0.#} x {e.ActualHeight:0.#}");
                continue;
            }
            var bounds = Bounds(e, host.Root);
            foreach (var (clipper, clip, horizontalOnly) in Clips(e, host))
            {
                bool inside = horizontalOnly
                    ? bounds.Left >= clip.Left - Tolerance && bounds.Right <= clip.Right + Tolerance
                    : Contains(clip, bounds);
                if (!inside)
                {
                    problems.Add($"{Describe(e)} {Fmt(bounds)} is clipped by {clipper.GetType().Name} {Fmt(clip)}");
                    break;
                }
            }
            if (e is TextBlock { TextTrimming: not TextTrimming.None, TextWrapping: TextWrapping.NoWrap } t && NaturalWidth(t) > t.ActualWidth + Tolerance)
                problems.Add($"{Describe(e)} is cut short with an ellipsis ({NaturalWidth(t):0} wide in {t.ActualWidth:0})");
        }
        return problems;
    }

    /// <summary>No two interactive controls overlap.</summary>
    public static List<string> Overlaps(LayoutHost host, FrameworkElement region)
    {
        var problems = new List<string>();
        var controls = Interactive(region).Select(c => (Control: c, Bounds: Bounds(c, host.Root))).ToList();
        for (int i = 0; i < controls.Count; i++)
            for (int j = i + 1; j < controls.Count; j++)
            {
                var overlap = Rect.Intersect(controls[i].Bounds, controls[j].Bounds);
                if (!overlap.IsEmpty && overlap.Width > Tolerance && overlap.Height > Tolerance)
                    problems.Add($"{Describe(controls[i].Control)} {Fmt(controls[i].Bounds)} overlaps {Describe(controls[j].Control)} {Fmt(controls[j].Bounds)}");
            }
        return problems;
    }

    /// <summary>Every interactive control has a name for screen readers: its AutomationProperties.Name or its own text.</summary>
    public static List<string> Names(FrameworkElement region) =>
        Interactive(region).Where(c => string.IsNullOrWhiteSpace(AccessibleName(c)))
            .Select(c => $"{Describe(c)} has no accessible name").ToList();

    /// <summary>
    /// Tab reaches every enabled interactive control (asked of WPF's own keyboard navigation, without moving focus), and
    /// within the page it goes top to bottom, left to right along a row.
    /// </summary>
    public static List<string> TabOrder(LayoutHost host, FrameworkElement region)
    {
        var problems = new List<string>();
        var order = TabSequence(host.Root);
        var inRegion = order.OfType<FrameworkElement>().Where(e => IsWithin(e, region)).ToList();

        foreach (var c in Interactive(region).Where(c => c.IsEnabled))
            if (!order.Contains(c))
                problems.Add($"{Describe(c)} can't be reached with Tab");

        for (int i = 1; i < inRegion.Count; i++)
        {
            Rect a = Bounds(inRegion[i - 1], host.Root), b = Bounds(inRegion[i], host.Root);
            bool sameRow = b.Top < a.Bottom && a.Top < b.Bottom;
            bool backwards = sameRow ? b.Left < a.Left - Tolerance : b.Top < a.Top;
            if (backwards)
                problems.Add($"Tab goes from {Describe(inRegion[i - 1])} {Fmt(a)} back to {Describe(inRegion[i])} {Fmt(b)}");
        }
        return problems;
    }

    // ---- What counts --------------------------------------------------------------------------------------------------

    /// <summary>Controls a user operates, outermost only (a ComboBox's own toggle button is part of the ComboBox).</summary>
    public static List<Control> Interactive(FrameworkElement region) =>
        Descendants(region).OfType<Control>()
            .Where(c => IsInteractive(c) && c.IsVisible && !Ancestors(c).TakeWhile(a => a != region).OfType<Control>().Any(IsInteractive))
            .ToList();

    private static bool IsInteractive(Control c) =>
        c.Focusable && c is ButtonBase or ComboBox or Slider or TextBoxBase or PasswordBox;

    /// <summary>What a user sees: text and interactive controls (not the panels around them), visible only.</summary>
    private static List<FrameworkElement> Leaves(FrameworkElement region)
    {
        var controls = Interactive(region).ToHashSet();
        return Descendants(region).OfType<FrameworkElement>()
            .Where(e => e.IsVisible && (e is TextBlock { Text.Length: > 0 } || (e is Control c && controls.Contains(c))))
            .ToList();
    }

    public static string AccessibleName(Control c)
    {
        string name = AutomationProperties.GetName(c);
        if (!string.IsNullOrWhiteSpace(name)) return name;
        return UIElementAutomationPeer.CreatePeerForElement(c)?.GetName() ?? "";
    }

    // ---- Geometry -----------------------------------------------------------------------------------------------------

    public static Rect Bounds(FrameworkElement e, Visual root) =>
        e.TransformToAncestor(root).TransformBounds(new Rect(0, 0, e.ActualWidth, e.ActualHeight));

    /// <summary>
    /// The clips that apply to an element, in root coordinates: its own and each container's layout clip (set when WPF
    /// gives an element less room than it asked for), anything with ClipToBounds, and the window. A ScrollViewer's
    /// viewport and the window are horizontal only: text below the fold is fine (Scrolling checks it can be reached),
    /// text beyond the right edge is not.
    /// </summary>
    private static IEnumerable<(FrameworkElement Clipper, Rect Clip, bool HorizontalOnly)> Clips(FrameworkElement e, LayoutHost host)
    {
        foreach (var a in new[] { e }.Concat(Ancestors(e).OfType<FrameworkElement>()))
        {
            if (a == host.Root) break;
            bool viewport = a is ScrollContentPresenter;
            Rect? local = LayoutInformation.GetLayoutClip(a)?.Bounds;
            if (local is null && (a.ClipToBounds || viewport)) local = new Rect(0, 0, a.ActualWidth, a.ActualHeight);
            if (local is { } l) yield return (a, a.TransformToAncestor(host.Root).TransformBounds(l), viewport);
        }
        yield return (host.Root, new Rect(0, 0, host.Root.ActualWidth, host.Root.ActualHeight), true);
    }

    /// <summary>The part of the window an element can be seen through: every viewport above it, and the window.</summary>
    private static Rect VisibleArea(FrameworkElement e, LayoutHost host)
    {
        var area = new Rect(0, 0, host.Root.ActualWidth, host.Root.ActualHeight);
        foreach (var scp in Ancestors(e).OfType<ScrollContentPresenter>())
            area.Intersect(Bounds(scp, host.Root));
        return area;
    }

    private static bool Contains(Rect outer, Rect inner) =>
        !outer.IsEmpty && inner.Left >= outer.Left - Tolerance && inner.Top >= outer.Top - Tolerance
        && inner.Right <= outer.Right + Tolerance && inner.Bottom <= outer.Bottom + Tolerance;

    private static double NaturalWidth(TextBlock t) =>
        new FormattedText(t.Text, CultureInfo.CurrentUICulture, t.FlowDirection,
            new Typeface(t.FontFamily, t.FontStyle, t.FontWeight, t.FontStretch), t.FontSize, Brushes.Black,
            VisualTreeHelper.GetDpi(t).PixelsPerDip).WidthIncludingTrailingWhitespace;

    // ---- Input --------------------------------------------------------------------------------------------------------

    /// <summary>A mouse-wheel turn as the input system delivers it: the tunnelling event, then the bubbling one.</summary>
    private static void Wheel(UIElement target, int delta)
    {
        var e = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = UIElement.PreviewMouseWheelEvent };
        target.RaiseEvent(e);
        if (e.Handled) return;
        e.RoutedEvent = UIElement.MouseWheelEvent;
        target.RaiseEvent(e);
    }

    private static readonly object Navigation = typeof(KeyboardNavigation)
        .GetProperty("Current", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
    private static readonly MethodInfo GetNextTab = typeof(KeyboardNavigation)
        .GetMethod("GetNextTab", BindingFlags.NonPublic | BindingFlags.Instance, [typeof(DependencyObject), typeof(DependencyObject), typeof(bool)])!;
    private static readonly MethodInfo GetGroupParent = typeof(KeyboardNavigation)
        .GetMethod("GetGroupParent", BindingFlags.NonPublic | BindingFlags.Instance, [typeof(DependencyObject), typeof(bool)])!;

    /// <summary>
    /// The elements Tab visits, in order, from WPF's own KeyboardNavigation (the code behind the Tab key), asked without
    /// moving focus: focus can't be taken in a window that is never shown, and must never be taken from the user.
    /// </summary>
    public static List<DependencyObject> TabSequence(FrameworkElement root)
    {
        // KeyboardNavigation.Navigate sets which property it navigates by (Tab, Ctrl+Tab or arrows) before asking.
        var saved = NavigationProperty.GetValue(Navigation);
        NavigationProperty.SetValue(Navigation, KeyboardNavigation.TabNavigationProperty);
        try
        {
            var order = new List<DependencyObject>();
            var current = (DependencyObject?)GetNextTab.Invoke(Navigation, [root, root, true]);
            while (current is not null && !order.Contains(current) && order.Count < 500)
            {
                order.Add(current);
                var group = (DependencyObject)GetGroupParent.Invoke(Navigation, [current, true])!;
                current = (DependencyObject?)GetNextTab.Invoke(Navigation, [current, group, false]);
            }
            return order;
        }
        finally { NavigationProperty.SetValue(Navigation, saved); }
    }

    private static readonly FieldInfo NavigationProperty = typeof(KeyboardNavigation)
        .GetField("_navigationProperty", BindingFlags.NonPublic | BindingFlags.Instance)!;

    // ---- Trees --------------------------------------------------------------------------------------------------------

    public static IEnumerable<DependencyObject> Descendants(DependencyObject d)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var child = VisualTreeHelper.GetChild(d, i);
            yield return child;
            foreach (var x in Descendants(child)) yield return x;
        }
    }

    public static IEnumerable<DependencyObject> Ancestors(DependencyObject d)
    {
        for (var p = VisualTreeHelper.GetParent(d); p is not null; p = VisualTreeHelper.GetParent(p)) yield return p;
    }

    private static bool IsWithin(DependencyObject e, DependencyObject region) => e == region || Ancestors(e).Contains(region);

    public static string Describe(FrameworkElement e)
    {
        string what = e switch
        {
            TextBlock t => $"\"{Short(t.Text)}\"",
            Control c when !string.IsNullOrWhiteSpace(AccessibleName(c)) => $"\"{Short(AccessibleName(c))}\"",
            _ => "",
        };
        return $"{e.GetType().Name} {what}".Trim();
    }

    private static string Short(string s) => s.Length > 40 ? s[..40] + "..." : s;

    private static string Fmt(Rect r) =>
        r.IsEmpty ? "(empty)" : string.Create(CultureInfo.InvariantCulture, $"[{r.X:0},{r.Y:0} {r.Width:0}x{r.Height:0}]");
}
