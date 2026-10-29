using System.Windows.Controls;
using GlobalAutocorrect.App.ViewModels;

namespace GlobalAutocorrect.App.Views.Pages;

public partial class AboutPage : Page
{
    public AboutPage(AboutViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
