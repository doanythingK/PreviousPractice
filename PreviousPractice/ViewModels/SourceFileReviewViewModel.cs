using System.Collections.ObjectModel;
using Microsoft.Maui.Devices;
using Microsoft.Maui.Storage;
using PreviousPractice.Core;
using PreviousPractice.Data;
using PreviousPractice.Models;

namespace PreviousPractice.ViewModels;

public sealed class SourceFileReviewViewModel : ViewModelBase
{
    private const string SourceFileDirectoryName = "QuestionSourceFiles";
    private const string AnalysisDirectoryName = "QuestionSourceOcr";
    private const double MinQuestionImageSliceRatio = 0.02d;
    private const double MinQuestionImageSliceWidthRatio = 0.08d;
    private const double MinQuestionImageZoom = 0.75d;
    private const double MaxQuestionImageZoom = 3.5d;
    private const double QuestionImageZoomStep = 0.25d;
    private const double WideLayoutMinimumWidth = 900d;
    private const double WideLayoutHorizontalChrome = 372d;
    private const double NarrowLayoutHorizontalChrome = 64d;

    private readonly IPracticeRepository repository = new PracticeRepository();
    private readonly string categoryId;
    private readonly string sourceFileName;
    private readonly string sourceAnalysisDirectory;
    private Question? currentQuestion;
    private string currentAnswerDisplay = string.Empty;
    private string diagnosticsText = string.Empty;
    private string summaryText = "불러오는 중...";
    private double questionImageZoom = 1d;
    private double questionImagePreviewScale = 1d;
    private double currentQuestionImageViewportWidth;
    private double currentQuestionImageViewportHeight;
    private string questionImageNotice = string.Empty;
    private bool useWideLayout;
    private bool isLoading = true;

    public SourceFileReviewViewModel(string categoryId, string categoryName, string sourceFileName)
    {
        this.categoryId = categoryId;
        this.sourceFileName = NormalizeSourceFileName(sourceFileName);
        sourceAnalysisDirectory = Path.Combine(
            FileSystem.AppDataDirectory,
            SourceFileDirectoryName,
            AnalysisDirectoryName);

        PageTitle = Path.GetFileName(this.sourceFileName);
        HeaderText = $"{categoryName} / {Path.GetFileName(this.sourceFileName)}";

        var idiom = DeviceInfo.Idiom;
        var initialWidth = idiom == DeviceIdiom.Desktop
            ? 1200d
            : idiom == DeviceIdiom.Tablet
                ? 900d
                : 390d;
        ApplyWorkspaceMetrics(ResponsiveLayoutCalculator.Calculate(
            initialWidth,
            idiom == DeviceIdiom.Phone ? 760d : 900d,
            WideLayoutMinimumWidth,
            WideLayoutHorizontalChrome,
            NarrowLayoutHorizontalChrome,
            220d,
            900d,
            220d,
            700d), rebuildImages: false);

        Questions = new ObservableCollection<Question>();
        CurrentQuestionImageSlices = new ObservableCollection<QuestionImageSliceViewModel>();
        PreviousQuestionCommand = new RelayCommand(MovePrevious);
        NextQuestionCommand = new RelayCommand(MoveNext);
        SelectQuestionCommand = new RelayCommand<Question?>(SelectQuestion);
        IncreaseQuestionImageZoomCommand = new RelayCommand(
            () => SetQuestionImageZoom(QuestionImageZoom + QuestionImageZoomStep),
            () => CanIncreaseQuestionImageZoom);
        DecreaseQuestionImageZoomCommand = new RelayCommand(
            () => SetQuestionImageZoom(QuestionImageZoom - QuestionImageZoomStep),
            () => CanDecreaseQuestionImageZoom);
        ResetQuestionImageZoomCommand = new RelayCommand(
            () => SetQuestionImageZoom(1d),
            () => CanResetQuestionImageZoom);

        _ = LoadAsync();
    }

    public ObservableCollection<Question> Questions { get; }

    public ObservableCollection<QuestionImageSliceViewModel> CurrentQuestionImageSlices { get; }

    public RelayCommand PreviousQuestionCommand { get; }

    public RelayCommand NextQuestionCommand { get; }

    public RelayCommand<Question?> SelectQuestionCommand { get; }

    public RelayCommand IncreaseQuestionImageZoomCommand { get; }

    public RelayCommand DecreaseQuestionImageZoomCommand { get; }

    public RelayCommand ResetQuestionImageZoomCommand { get; }

    public string PageTitle { get; }

    public string HeaderText { get; }

    public string SummaryText
    {
        get => summaryText;
        private set => SetProperty(ref summaryText, value);
    }

    public string DiagnosticsText
    {
        get => diagnosticsText;
        private set
        {
            if (SetProperty(ref diagnosticsText, value))
            {
                OnPropertyChanged(nameof(CanOpenDiagnostics));
            }
        }
    }

    public bool IsLoading
    {
        get => isLoading;
        private set
        {
            if (SetProperty(ref isLoading, value))
            {
                OnPropertyChanged(nameof(CanOpenDiagnostics));
            }
        }
    }

    public bool CanOpenDiagnostics =>
        !IsLoading && !string.IsNullOrWhiteSpace(DiagnosticsText);

    public Question? CurrentQuestion
    {
        get => currentQuestion;
        set
        {
            if (!ReferenceEquals(currentQuestion, value))
            {
                ResetQuestionImageView();
            }

            if (SetProperty(ref currentQuestion, value))
            {
                UpdateCurrentQuestionState();
            }
        }
    }

    public bool HasQuestions => Questions.Count > 0;

    public bool HasCurrentQuestionImages => CurrentQuestionImageSlices.Count > 0;

    public bool ShowCurrentQuestionText => !HasCurrentQuestionImages;

    public string CurrentQuestionText => CurrentQuestion?.Prompt ?? string.Empty;

    public string CurrentQuestionDisplayTitle =>
        CurrentQuestion == null
            ? "문항 없음"
            : $"{CurrentQuestion.Index}번 문제";

    public string CurrentQuestionPositionText
    {
        get
        {
            if (CurrentQuestion == null || Questions.Count == 0)
            {
                return "0 / 0";
            }

            var position = Questions.IndexOf(CurrentQuestion);
            return $"{position + 1} / {Questions.Count}";
        }
    }

    public string CurrentAnswerDisplay
    {
        get => currentAnswerDisplay;
        private set => SetProperty(ref currentAnswerDisplay, value);
    }

    public bool CanGoPrevious => CurrentQuestion != null && Questions.IndexOf(CurrentQuestion) > 0;

    public bool CanGoNext =>
        CurrentQuestion != null &&
        Questions.IndexOf(CurrentQuestion) >= 0 &&
        Questions.IndexOf(CurrentQuestion) < Questions.Count - 1;

    public bool UseWideLayout => useWideLayout;

    public double CurrentQuestionImageViewportHeight => currentQuestionImageViewportHeight;

    public string QuestionImageNotice
    {
        get => questionImageNotice;
        private set
        {
            if (SetProperty(ref questionImageNotice, value))
            {
                OnPropertyChanged(nameof(HasQuestionImageNotice));
            }
        }
    }

    public bool HasQuestionImageNotice => !string.IsNullOrWhiteSpace(QuestionImageNotice);

    public double QuestionImageZoom
    {
        get => questionImageZoom;
        private set
        {
            if (SetProperty(ref questionImageZoom, value))
            {
                OnPropertyChanged(nameof(QuestionImageZoomText));
                OnPropertyChanged(nameof(CanIncreaseQuestionImageZoom));
                OnPropertyChanged(nameof(CanDecreaseQuestionImageZoom));
                OnPropertyChanged(nameof(CanResetQuestionImageZoom));
                IncreaseQuestionImageZoomCommand.RaiseCanExecuteChanged();
                DecreaseQuestionImageZoomCommand.RaiseCanExecuteChanged();
                ResetQuestionImageZoomCommand.RaiseCanExecuteChanged();
                UpdateCurrentQuestionState();
            }
        }
    }

    public string QuestionImageZoomText => $"{QuestionImageZoom * 100:0}%";

    public bool CanIncreaseQuestionImageZoom => QuestionImageZoom < MaxQuestionImageZoom - 0.001d;

    public bool CanDecreaseQuestionImageZoom => QuestionImageZoom > MinQuestionImageZoom + 0.001d;

    public bool CanResetQuestionImageZoom => Math.Abs(QuestionImageZoom - 1d) > 0.001d;

    public double QuestionImagePreviewScale
    {
        get => questionImagePreviewScale;
        private set => SetProperty(ref questionImagePreviewScale, value);
    }

    private async Task LoadAsync()
    {
        try
        {
            IsLoading = true;
            DiagnosticsText = await LoadDiagnosticsTextAsync();

            var questions = await repository.GetQuestionsAsync(categoryId);
            var matched = questions
                .Where(x => string.Equals(
                    NormalizeSourceFileName(x.SourceFileName),
                    sourceFileName,
                    StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.Index)
                .ToArray();

            Questions.Clear();
            foreach (var question in matched)
            {
                Questions.Add(question);
            }

            SummaryText = matched.Length == 0
                ? "저장된 문항이 없습니다."
                : $"저장된 문항 {matched.Length}개";

            CurrentQuestion = matched.FirstOrDefault();
            OnPropertyChanged(nameof(HasQuestions));
        }
        catch (Exception ex)
        {
            SummaryText = $"문항 보기 로드 실패: {ex.Message}";
            DiagnosticsText = "분석 진단을 불러오지 못했습니다.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void SelectQuestion(Question? question)
    {
        if (question != null)
        {
            CurrentQuestion = question;
        }
    }

    private void MovePrevious()
    {
        if (CurrentQuestion == null)
        {
            return;
        }

        var index = Questions.IndexOf(CurrentQuestion);
        if (index > 0)
        {
            CurrentQuestion = Questions[index - 1];
        }
    }

    private void MoveNext()
    {
        if (CurrentQuestion == null)
        {
            return;
        }

        var index = Questions.IndexOf(CurrentQuestion);
        if (index >= 0 && index < Questions.Count - 1)
        {
            CurrentQuestion = Questions[index + 1];
        }
    }

    private void UpdateCurrentQuestionState()
    {
        CurrentQuestionImageSlices.Clear();
        var result = QuestionImageSliceBuilder.BuildWithStatus(
            CurrentQuestion,
            currentQuestionImageViewportWidth,
            currentQuestionImageViewportHeight,
            MinQuestionImageSliceRatio,
            MinQuestionImageSliceWidthRatio,
            QuestionImageZoom);
        foreach (var slice in result.Slices)
        {
            CurrentQuestionImageSlices.Add(slice);
        }

        QuestionImageNotice = result.UnavailableSegmentCount switch
        {
            <= 0 => string.Empty,
            _ when result.Slices.Count == 0 =>
                "저장된 문제 이미지를 열 수 없어 OCR 텍스트로 대신 표시합니다.",
            _ =>
                $"문제 이미지 {result.StoredSegmentCount}개 중 {result.UnavailableSegmentCount}개를 열 수 없습니다. 진단에서 누락 여부를 확인해 주세요."
        };

        CurrentAnswerDisplay = CurrentQuestion == null
            ? string.Empty
            : string.IsNullOrWhiteSpace(CurrentQuestion.CorrectAnswerDisplay)
                ? "정답 없음"
                : $"정답: {CurrentQuestion.CorrectAnswerDisplay}";

        OnPropertyChanged(nameof(CurrentQuestionText));
        OnPropertyChanged(nameof(CurrentQuestionDisplayTitle));
        OnPropertyChanged(nameof(CurrentQuestionPositionText));
        OnPropertyChanged(nameof(HasCurrentQuestionImages));
        OnPropertyChanged(nameof(ShowCurrentQuestionText));
        OnPropertyChanged(nameof(CanGoPrevious));
        OnPropertyChanged(nameof(CanGoNext));
    }

    public void SetQuestionImageZoom(double value)
    {
        QuestionImageZoom = Math.Clamp(value, MinQuestionImageZoom, MaxQuestionImageZoom);
    }

    public void SetQuestionImagePreviewScale(double gestureScale, double pinchStartZoom)
    {
        var safeStartZoom = Math.Clamp(pinchStartZoom, MinQuestionImageZoom, MaxQuestionImageZoom);
        var effectiveZoom = Math.Clamp(safeStartZoom * gestureScale, MinQuestionImageZoom, MaxQuestionImageZoom);
        QuestionImagePreviewScale = effectiveZoom / safeStartZoom;
    }

    public void CompleteQuestionImagePinch(double pinchStartZoom, double gestureScale, bool canceled)
    {
        QuestionImagePreviewScale = 1d;
        if (!canceled)
        {
            SetQuestionImageZoom(pinchStartZoom * gestureScale);
        }
    }

    public void UpdateWorkspaceSize(double width, double height)
    {
        if (!double.IsFinite(width) || width <= 0d ||
            !double.IsFinite(height) || height <= 0d)
        {
            return;
        }

        ApplyWorkspaceMetrics(ResponsiveLayoutCalculator.Calculate(
            width,
            height,
            WideLayoutMinimumWidth,
            WideLayoutHorizontalChrome,
            NarrowLayoutHorizontalChrome,
            220d,
            900d,
            220d,
            700d), rebuildImages: true);
    }

    private void ApplyWorkspaceMetrics(ResponsiveLayoutMetrics metrics, bool rebuildImages)
    {
        var layoutChanged = useWideLayout != metrics.UseWideLayout;
        var viewportChanged =
            Math.Abs(currentQuestionImageViewportWidth - metrics.ImageViewportWidth) >= 8d ||
            Math.Abs(currentQuestionImageViewportHeight - metrics.ImageViewportHeight) >= 8d;

        useWideLayout = metrics.UseWideLayout;

        if (layoutChanged)
        {
            OnPropertyChanged(nameof(UseWideLayout));
        }

        if (viewportChanged)
        {
            currentQuestionImageViewportWidth = metrics.ImageViewportWidth;
            currentQuestionImageViewportHeight = metrics.ImageViewportHeight;
            OnPropertyChanged(nameof(CurrentQuestionImageViewportHeight));
            if (rebuildImages && CurrentQuestion != null)
            {
                UpdateCurrentQuestionState();
            }
        }
    }

    private void ResetQuestionImageView()
    {
        var zoomChanged = Math.Abs(questionImageZoom - 1d) > 0.001d;
        questionImageZoom = 1d;
        QuestionImagePreviewScale = 1d;
        if (zoomChanged)
        {
            OnPropertyChanged(nameof(QuestionImageZoom));
            OnPropertyChanged(nameof(QuestionImageZoomText));
            OnPropertyChanged(nameof(CanIncreaseQuestionImageZoom));
            OnPropertyChanged(nameof(CanDecreaseQuestionImageZoom));
            OnPropertyChanged(nameof(CanResetQuestionImageZoom));
            IncreaseQuestionImageZoomCommand.RaiseCanExecuteChanged();
            DecreaseQuestionImageZoomCommand.RaiseCanExecuteChanged();
            ResetQuestionImageZoomCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task<string> LoadDiagnosticsTextAsync()
    {
        try
        {
            var safeFileName = GetSafeFileNameWithoutExtension(sourceFileName);
            var diagnosticsPath = Path.Combine(
                sourceAnalysisDirectory,
                $"{safeFileName}.committed.diagnostics.txt");
            if (File.Exists(diagnosticsPath))
            {
                return await File.ReadAllTextAsync(diagnosticsPath);
            }

            return "이 버전에서 문항과 함께 확정 저장된 분석 진단 파일이 없습니다.";
        }
        catch (Exception ex)
        {
            return $"분석 진단 읽기 실패: {ex.Message}";
        }
    }

    private static string NormalizeSourceFileName(string value) =>
        string.IsNullOrWhiteSpace(value) ? "manual" : value.Trim();

    private static string GetSafeFileNameWithoutExtension(string fileName)
    {
        var baseName = Path.GetFileNameWithoutExtension(fileName).Trim();
        if (string.IsNullOrWhiteSpace(baseName))
        {
            return "analysis";
        }

        var invalid = Path.GetInvalidFileNameChars();
        return new string(baseName.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
