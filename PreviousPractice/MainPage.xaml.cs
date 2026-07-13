using System.ComponentModel;
using Microsoft.Maui.Accessibility;
using PreviousPractice.Models;
using PreviousPractice.ViewModels;

namespace PreviousPractice;

public partial class MainPage : ContentPage
{
    private double questionImagePinchStartZoom = 1d;
    private double questionImagePinchScale = 1d;
    private CancellationTokenSource? workspaceSizeUpdateCancellation;

    public MainPage()
    {
        InitializeComponent();
        SizeChanged += OnPageSizeChanged;
        if (BindingContext is MainViewModel viewModel)
        {
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
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
                BindingContext is MainViewModel viewModel)
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
        if (e.PropertyName == nameof(MainViewModel.CurrentQuestion))
        {
            Dispatcher.Dispatch(() => ResetQuestionViewPositionAsync(focusAnswer: true));
        }
        else if (e.PropertyName == nameof(MainViewModel.UseWideWorkspaceLayout))
        {
            Dispatcher.Dispatch(() => ResetQuestionViewPositionAsync(focusAnswer: false));
        }
    }

    private async void ResetQuestionViewPositionAsync(bool focusAnswer)
    {
        if (BindingContext is not MainViewModel viewModel)
        {
            return;
        }

        try
        {
            await Task.Yield();
            await PracticeWorkspaceScroll.ScrollToAsync(0, 0, animated: false);

            if (viewModel.IsPracticeRunning && !viewModel.UseWideWorkspaceLayout)
            {
                await PracticeWorkspaceScroll.ScrollToAsync(
                    NarrowPracticeQuestionContent,
                    ScrollToPosition.Start,
                    animated: true);
            }

            if (focusAnswer && viewModel.IsPracticeRunning && viewModel.CurrentQuestion != null)
            {
                var answerEntry = viewModel.UseWideWorkspaceLayout
                    ? WidePracticeAnswerEntry
                    : NarrowPracticeAnswerEntry;
                answerEntry.Focus();
                SemanticScreenReader.Default.Announce(
                    $"{viewModel.ProgressDisplay}. {viewModel.CurrentQuestionSemanticDescription}");
            }
        }
        catch (Exception)
        {
            // 레이아웃 전환 직후 아직 측정되지 않은 뷰는 다음 사용자 스크롤에 맡긴다.
        }
    }

    private async void OnOpenLatestDiagnosticsClicked(object? sender, EventArgs e)
    {
        if (BindingContext is not MainViewModel viewModel ||
            !viewModel.HasLatestPdfDiagnostics)
        {
            return;
        }

        var title = string.IsNullOrWhiteSpace(viewModel.LatestPdfDiagnosticsTitle)
            ? "최근 PDF 분석"
            : viewModel.LatestPdfDiagnosticsTitle;
        await Navigation.PushAsync(new SourceFileDiagnosticsPage(
            title,
            viewModel.LatestPdfDiagnosticsText));
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

    private void OnDeleteSourceFileClicked(object? sender, EventArgs e)
    {
        if (BindingContext is not MainViewModel viewModel ||
            sender is not Button button ||
            button.CommandParameter is not SourceFileSummary sourceFile ||
            !sourceFile.CanDelete ||
            !viewModel.DeleteSourceFileCommand.CanExecute(sourceFile))
        {
            return;
        }

        viewModel.DeleteSourceFileCommand.Execute(sourceFile);
    }

    private void OnRemoveWrongClicked(object? sender, EventArgs e)
    {
        if (BindingContext is not MainViewModel viewModel ||
            sender is not Button button ||
            button.CommandParameter is not Guid questionId ||
            !viewModel.RemoveWrongCommand.CanExecute(questionId))
        {
            return;
        }

        viewModel.RemoveWrongCommand.Execute(questionId);
    }

    private void OnQuestionImagePinchUpdated(object? sender, PinchGestureUpdatedEventArgs e)
    {
        if (BindingContext is not MainViewModel viewModel)
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
