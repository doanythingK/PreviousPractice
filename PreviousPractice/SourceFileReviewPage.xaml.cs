using System.ComponentModel;
using PreviousPractice.ViewModels;
using PreviousPractice.Models;

namespace PreviousPractice;

public partial class SourceFileReviewPage : ContentPage
{
    private double questionImagePinchStartZoom = 1d;
    private double questionImagePinchScale = 1d;
    private CancellationTokenSource? workspaceSizeUpdateCancellation;

    public SourceFileReviewPage(string categoryId, string categoryName, string sourceFileName)
    {
        InitializeComponent();
        var viewModel = new SourceFileReviewViewModel(categoryId, categoryName, sourceFileName);
        BindingContext = viewModel;
        SizeChanged += OnPageSizeChanged;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnPageSizeChanged(object? sender, EventArgs e)
    {
        if (!double.IsFinite(Width) || Width <= 0d ||
            !double.IsFinite(Height) || Height <= 0d)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        var previousCancellation = Interlocked.Exchange(
            ref workspaceSizeUpdateCancellation,
            cancellation);
        previousCancellation?.Cancel();
        _ = ApplyWorkspaceSizeAfterResizeAsync(Width, Height, cancellation);
    }

    private async Task ApplyWorkspaceSizeAfterResizeAsync(
        double width,
        double height,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(140, cancellation.Token);
            if (!cancellation.IsCancellationRequested &&
                BindingContext is SourceFileReviewViewModel viewModel)
            {
                viewModel.UpdateWorkspaceSize(width, height);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(workspaceSizeUpdateCancellation, cancellation))
            {
                workspaceSizeUpdateCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SourceFileReviewViewModel.CurrentQuestion) or
            nameof(SourceFileReviewViewModel.UseWideLayout))
        {
            Dispatcher.Dispatch(ResetQuestionViewPositionAsync);
        }
    }

    private async void ResetQuestionViewPositionAsync()
    {
        if (BindingContext is not SourceFileReviewViewModel viewModel)
        {
            return;
        }

        try
        {
            await Task.Yield();
            var imageScroll = viewModel.UseWideLayout
                ? WideReviewImageScroll
                : NarrowReviewImageScroll;
            await imageScroll.ScrollToAsync(0, 0, animated: false);

            if (viewModel.CurrentQuestion != null)
            {
                var list = viewModel.UseWideLayout
                    ? WideQuestionList
                    : NarrowQuestionList;
                list.ScrollTo(
                    viewModel.CurrentQuestion,
                    position: ScrollToPosition.Center,
                    animate: true);
            }
        }
        catch (Exception)
        {
            // 레이아웃 직후 아직 측정되지 않은 목록은 다음 선택 때 다시 맞춘다.
        }
    }

    private async void OnOpenDiagnosticsClicked(object? sender, EventArgs e)
    {
        if (BindingContext is not SourceFileReviewViewModel viewModel ||
            !viewModel.CanOpenDiagnostics)
        {
            return;
        }

        await Navigation.PushAsync(new SourceFileDiagnosticsPage(
            viewModel.PageTitle,
            viewModel.DiagnosticsText));
    }

    private void OnSelectQuestionClicked(object? sender, EventArgs e)
    {
        if (BindingContext is not SourceFileReviewViewModel viewModel ||
            sender is not Button button ||
            button.CommandParameter is not Question question ||
            !viewModel.SelectQuestionCommand.CanExecute(question))
        {
            return;
        }

        viewModel.SelectQuestionCommand.Execute(question);
    }

    private void OnQuestionImagePinchUpdated(object? sender, PinchGestureUpdatedEventArgs e)
    {
        if (BindingContext is not SourceFileReviewViewModel viewModel)
        {
            return;
        }

        if (e.Status == GestureStatus.Started)
        {
            questionImagePinchStartZoom = viewModel.QuestionImageZoom;
            questionImagePinchScale = 1d;
            return;
        }

        if (e.Status == GestureStatus.Running)
        {
            if (!double.IsFinite(e.Scale) || e.Scale <= 0d)
            {
                return;
            }

            questionImagePinchScale = Math.Clamp(
                questionImagePinchScale * e.Scale,
                0.1d,
                10d);
            viewModel.SetQuestionImagePreviewScale(
                questionImagePinchScale,
                questionImagePinchStartZoom);
            return;
        }

        if (e.Status is GestureStatus.Completed or GestureStatus.Canceled)
        {
            viewModel.CompleteQuestionImagePinch(
                questionImagePinchStartZoom,
                questionImagePinchScale,
                e.Status == GestureStatus.Canceled);
        }
    }
}
