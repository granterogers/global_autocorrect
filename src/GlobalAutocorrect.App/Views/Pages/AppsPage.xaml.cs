using System.Windows.Controls;
using GlobalAutocorrect.App.ViewModels;

namespace GlobalAutocorrect.App.Views.Pages;

public partial class AppsPage : Page
{
    public AppsPage(AppsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
