using PreviousPractice.ViewModels;

namespace PreviousPractice;

public partial class SourceFileReviewPage : ContentPage
{
    public SourceFileReviewPage(string categoryId, string categoryName, string sourceFileName)
    {
        InitializeComponent();
        BindingContext = new SourceFileReviewViewModel(categoryId, categoryName, sourceFileName);
    }

    private async void OnOpenDiagnosticsClicked(object? sender, EventArgs e)
    {
        if (BindingContext is not SourceFileReviewViewModel viewModel)
        {
            return;
        }

        await Navigation.PushAsync(new SourceFileDiagnosticsPage(
            viewModel.PageTitle,
            viewModel.DiagnosticsText));
    }
}
