using System.Windows.Controls;
using GlobalAutocorrect.App.ViewModels;

namespace GlobalAutocorrect.App.Views.Pages;

public partial class CleanUpPage : Page
{
    public CleanUpPage(CleanUpViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
