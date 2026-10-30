using System.ComponentModel;
using GlobalAutocorrect.App.ViewModels;
using Wpf.Ui.Controls;

namespace GlobalAutocorrect.App.Views;

/// <summary>The first-run window (9.4): what the app does, the model download, and the shortcuts. See <see cref="FirstRunViewModel"/>.</summary>
public partial class FirstRunWindow : FluentWindow
{
    private readonly FirstRunViewModel _viewModel;

    public FirstRunWindow(FirstRunViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.Finished += Close;
        viewModel.PropertyChanged += OnViewModelChanged;
    }

    // Each step starts at its top, even if the one before was scrolled (D63).
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FirstRunViewModel.Step)) StepScroller.ScrollToHome();
    }

    /// <summary>Closing the window mid-download stops the download; it can be started again next time.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        _viewModel.CancelDownload();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Finished -= Close;
        _viewModel.PropertyChanged -= OnViewModelChanged;
        base.OnClosed(e);
    }
}
