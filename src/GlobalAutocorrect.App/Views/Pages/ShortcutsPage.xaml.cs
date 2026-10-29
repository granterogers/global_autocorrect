using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GlobalAutocorrect.App.ViewModels;

namespace GlobalAutocorrect.App.Views.Pages;

public partial class ShortcutsPage : Page
{
    public ShortcutsPage(ShortcutsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }

    private static ShortcutEditor? EditorOf(object sender) => (sender as FrameworkElement)?.DataContext as ShortcutEditor;

    private void OnRecorderGotFocus(object sender, KeyboardFocusChangedEventArgs e) => EditorOf(sender)?.StartRecording();

    private void OnRecorderLostFocus(object sender, KeyboardFocusChangedEventArgs e) => EditorOf(sender)?.StopRecording();

    private void OnRecorderKeyDown(object sender, KeyEventArgs e)
    {
        var editor = EditorOf(sender);
        if (editor is null) return;

        // Alt combinations arrive as Key.System; the real key is in SystemKey.
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None)
        {
            editor.StopRecording();   // plain Tab moves on, as in any form
            return;
        }
        e.Handled = editor.Record(Keyboard.Modifiers, key);
    }
}
