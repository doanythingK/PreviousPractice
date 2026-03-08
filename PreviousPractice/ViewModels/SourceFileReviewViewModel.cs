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
    private const double PhoneQuestionImageViewportWidth = 320d;
    private const double PhoneQuestionImageViewportHeight = 260d;
    private const double TabletQuestionImageViewportWidth = 560d;
    private const double TabletQuestionImageViewportHeight = 440d;
    private const double DesktopQuestionImageViewportWidth = 760d;
    private const double DesktopQuestionImageViewportHeight = 620d;
    private const double MinQuestionImageSliceRatio = 0.02d;
    private const double MinQuestionImageSliceWidthRatio = 0.08d;

    private readonly IPracticeRepository repository = new PracticeRepository();
    private readonly string categoryId;
    private readonly string sourceFileName;
    private readonly string sourceAnalysisDirectory;
    private Question? currentQuestion;
    private string currentAnswerDisplay = string.Empty;
    private string diagnosticsText = string.Empty;
    private string summaryText = "불러오는 중...";
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

        Questions = new ObservableCollection<Question>();
        CurrentQuestionImageSlices = new ObservableCollection<QuestionImageSliceViewModel>();
        PreviousQuestionCommand = new RelayCommand(MovePrevious);
        NextQuestionCommand = new RelayCommand(MoveNext);
        SelectQuestionCommand = new RelayCommand<Question?>(SelectQuestion);

        _ = LoadAsync();
    }

    public ObservableCollection<Question> Questions { get; }

    public ObservableCollection<QuestionImageSliceViewModel> CurrentQuestionImageSlices { get; }

    public RelayCommand PreviousQuestionCommand { get; }

    public RelayCommand NextQuestionCommand { get; }

    public RelayCommand<Question?> SelectQuestionCommand { get; }

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
        private set => SetProperty(ref diagnosticsText, value);
    }

    public bool IsLoading
    {
        get => isLoading;
        private set => SetProperty(ref isLoading, value);
    }

    public Question? CurrentQuestion
    {
        get => currentQuestion;
        set
        {
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
        foreach (var slice in QuestionImageSliceBuilder.Build(
                     CurrentQuestion,
                     CurrentQuestionImageViewportWidth,
                     CurrentQuestionImageViewportHeight,
                     MinQuestionImageSliceRatio,
                     MinQuestionImageSliceWidthRatio))
        {
            CurrentQuestionImageSlices.Add(slice);
        }

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

    private async Task<string> LoadDiagnosticsTextAsync()
    {
        try
        {
            var safeFileName = GetSafeFileNameWithoutExtension(sourceFileName);
            var diagnosticsPath = Path.Combine(sourceAnalysisDirectory, $"{safeFileName}.diagnostics.txt");
            if (File.Exists(diagnosticsPath))
            {
                return await File.ReadAllTextAsync(diagnosticsPath);
            }

            return "저장된 분석 진단 파일이 없습니다.";
        }
        catch (Exception ex)
        {
            return $"분석 진단 읽기 실패: {ex.Message}";
        }
    }

    private double CurrentQuestionImageViewportWidth
    {
        get
        {
            var idiom = DeviceInfo.Idiom;
            if (idiom == DeviceIdiom.Desktop)
            {
                return DesktopQuestionImageViewportWidth;
            }

            if (idiom == DeviceIdiom.Tablet)
            {
                return TabletQuestionImageViewportWidth;
            }

            return PhoneQuestionImageViewportWidth;
        }
    }

    private double CurrentQuestionImageViewportHeight
    {
        get
        {
            var idiom = DeviceInfo.Idiom;
            if (idiom == DeviceIdiom.Desktop)
            {
                return DesktopQuestionImageViewportHeight;
            }

            if (idiom == DeviceIdiom.Tablet)
            {
                return TabletQuestionImageViewportHeight;
            }

            return PhoneQuestionImageViewportHeight;
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
