using PreviousPractice.Models;
using PreviousPractice.ViewModels;

namespace PreviousPractice;

public partial class MainPage : ContentPage
{
    public MainPage()
    {
        InitializeComponent();
    }

    private async void OnOpenSourceFileReviewClicked(object? sender, EventArgs e)
    {
        if (BindingContext is not MainViewModel viewModel ||
            viewModel.SelectedCategory == null ||
            sender is not Button button ||
            button.CommandParameter is not SourceFileSummary sourceFile)
        {
            return;
        }

        await Navigation.PushAsync(new SourceFileReviewPage(
            viewModel.SelectedCategory.Id,
            viewModel.SelectedCategory.Name,
            sourceFile.SourceFileName));
    }
}
