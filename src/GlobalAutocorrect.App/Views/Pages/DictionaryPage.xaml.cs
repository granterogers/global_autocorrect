using System.Windows.Controls;
using GlobalAutocorrect.App.ViewModels;

namespace GlobalAutocorrect.App.Views.Pages;

public partial class DictionaryPage : Page
{
    public DictionaryPage(DictionaryViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
