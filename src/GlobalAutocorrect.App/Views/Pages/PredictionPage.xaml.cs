using System.Windows.Controls;
using GlobalAutocorrect.App.ViewModels;

namespace GlobalAutocorrect.App.Views.Pages;

public partial class PredictionPage : Page
{
    public PredictionPage(PredictionViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
