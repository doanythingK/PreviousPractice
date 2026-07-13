using System.Collections.ObjectModel;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PreviousPractice.Core;
using PreviousPractice.Data;
using PreviousPractice.Infrastructure;
using PreviousPractice.Models;
using PreviousPractice.Services;
using Microsoft.Maui.Storage;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Devices;

namespace PreviousPractice.ViewModels;

public class MainViewModel : ViewModelBase
{
    private readonly IPracticeRepository repository;
    private readonly IPdfAnalysisService pdfAnalysisService;
    private readonly Random random = new();
    private const string SourceFileDirectoryName = "QuestionSourceFiles";
    private const string AnalysisDirectoryName = "QuestionSourceOcr";
    private readonly string sourceFileDirectory;
    private readonly string sourceAnalysisDirectory;
    private string newCategoryName = string.Empty;
    private string answerMapText = string.Empty;
    private string expectedQuestionRangeText = string.Empty;
    private string pdfAnalysisSummary = string.Empty;
    private string pdfAnalysisStatus = string.Empty;
    private Color pdfAnalysisStatusColor = Color.FromArgb("#334155");
    private double pdfAnalysisProgress;
    private int pdfAnalysisTotalPages;
    private int pdfAnalysisProcessedPages;
    private double pdfAnalysisPagesPerSecond;
    private bool isPdfAnalysisInProgress;
    private bool isPdfAnalysisCommitStarted;
    private bool isSourceFileImportInProgress;
    private bool isInitializing = true;
    private bool isSelectedCategoryLoading;
    private bool isStructuralOperationInProgress;
    private int structuralOperationGate;
    private CancellationTokenSource? pdfAnalysisCancellation;
    private DateTimeOffset pdfAnalysisStartAt;
    private string practiceCountText = "1";
    private string feedback = string.Empty;
    private string sessionFeedback = string.Empty;
    private string userAnswer = string.Empty;
    private string selectedWorkspaceSection = WorkspaceSectionImport;
    private bool isTopSummaryExpanded;
    private double questionImageZoom = 1d;
    private double questionImagePreviewScale = 1d;
    private double currentQuestionImageViewportWidth;
    private double currentQuestionImageViewportHeight;
    private string questionImageNotice = string.Empty;
    private string latestPdfDiagnosticsText = string.Empty;
    private string latestPdfDiagnosticsTitle = string.Empty;
    private bool useWideWorkspaceLayout;
    private bool isPracticeRunning;
    private bool isPracticeStartInProgress;
    private bool isAnswerRevealed;
    private bool isAnswerSubmissionInProgress;
    private bool overwriteExisting;
    private Category? selectedCategory;
    private Question? currentQuestion;
    private int sessionCount;
    private int currentIndex;
    private int correctCount;
    private int gradedCount;
    private int selectedCategoryQuestionCount;
    private int selectedCategoryPracticeQuestionCount;
    private int selectedCategoryRefreshVersion;
    private SourceFileSummary? selectedSourceFile;
    private bool includeUnansweredInPractice = true;
    private string selectedSourceFileName = string.Empty;
    private IReadOnlyList<Question> currentSession = Array.Empty<Question>();
    private const double MinQuestionImageSliceRatio = 0.02d;
    private const double MinQuestionImageSliceWidthRatio = 0.08d;
    private const double MinQuestionImageZoom = 0.75d;
    private const double MaxQuestionImageZoom = 3.5d;
    private const double QuestionImageZoomStep = 0.25d;
    private const double WideWorkspaceMinimumWidth = 1000d;
    private const double WideWorkspaceHorizontalChrome = 404d;
    private const double NarrowWorkspaceHorizontalChrome = 72d;
    private const double QuestionImageCropPaddingRatio = 0.015d;
    private const int MalformedSharedContextFallbackQuestionCount = 3;
    private const int MaximumExpectedQuestionNumber = 999;
    private const int MaximumExpectedQuestionCount = 999;
    private const string WorkspaceSectionImport = "import";
    private const string WorkspaceSectionPractice = "practice";
    private const string WorkspaceSectionWrong = "wrong";
    private static readonly char[] QuestionRangeSeparators = ['-', '~', '〜'];
    private static readonly Regex QuestionRangeHintRegex = new(
        @"(?<!\d)(?<start>\d{1,3})\s*[-~〜]\s*(?<end>\d{1,3})\s*(?:번|문항|문제)?(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SharedContextRangeStartRegex = new(
        @"(?<!\d)(?<start>\d{1,3})\s*[-~〜]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SharedContextRangeEndRegex = new(
        @"[-~〜]\s*(?<end>\d{1,3})(?:번|문항|문제)?(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex StructuralSharedContextMarkerRegex = new(
        @"[<(（【〔［]\s*(?:\d{1,3}|[%A-Za-z가-힣①-⑳㉠-㉻]+)?\s*[-~〜]\s*(?:\d{1,3}|[%A-Za-z가-힣①-⑳㉠-㉻]+)\s*[>)）】〕］]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly string[] StructuralBlockingBoilerplatePhrases =
    [
        "출제위원",
        "출제범위",
        "출석수업대체시험",
        "다음 면에 계속",
        "앞면에서 계속"
    ];

    public ObservableCollection<Category> Categories { get; } = new();
    public ObservableCollection<SourceFileSummary> SourceFiles { get; } = new();
    public ObservableCollection<Question> WrongQuestions { get; } = new();
    public ObservableCollection<string> SourceFilesDirectory { get; } = new();
    public ObservableCollection<QuestionImageSliceViewModel> CurrentQuestionImageSlices { get; } = new();

    public string PdfAnalysisSummary
    {
        get => pdfAnalysisSummary;
        set
        {
            if (SetProperty(ref pdfAnalysisSummary, value))
            {
                OnPropertyChanged(nameof(HasPdfAnalysisSummary));
                OnPropertyChanged(nameof(ShowPdfAnalysisPanel));
            }
        }
    }

    public string PdfAnalysisStatus
    {
        get => pdfAnalysisStatus;
        private set => SetProperty(ref pdfAnalysisStatus, value);
    }

    public Color PdfAnalysisStatusColor
    {
        get => pdfAnalysisStatusColor;
        private set => SetProperty(ref pdfAnalysisStatusColor, value);
    }

    public bool IsPdfAnalysisInProgress
    {
        get => isPdfAnalysisInProgress;
        private set
        {
            if (SetProperty(ref isPdfAnalysisInProgress, value))
            {
                OnPropertyChanged(nameof(CanModifyQuestionStructure));
                OnPropertyChanged(nameof(CanAddCategory));
                OnPropertyChanged(nameof(CanImportAnswerMap));
                OnPropertyChanged(nameof(CanStartPractice));
                OnPropertyChanged(nameof(CanStartWrongPractice));
                OnPropertyChanged(nameof(CanDeleteCategory));
                OnPropertyChanged(nameof(CanDeleteSourceFile));
                OnPropertyChanged(nameof(ShowPdfAnalysisProgress));
                OnPropertyChanged(nameof(ShowPdfAnalysisPanel));
                OnPropertyChanged(nameof(CanCancelPdfAnalysis));
                CancelPdfAnalysisCommand.RaiseCanExecuteChanged();
                UpdateSourceFileActionState();
            }
        }
    }

    public double PdfAnalysisProgressValue
    {
        get => pdfAnalysisProgress;
        private set
        {
            if (SetProperty(ref pdfAnalysisProgress, value))
            {
                OnPropertyChanged(nameof(PdfAnalysisProgressText));
            }
        }
    }

    public string PdfAnalysisProgressText =>
        $"{(int)(PdfAnalysisProgressValue * 100)}% ({pdfAnalysisProcessedPages}/" +
        $"{(pdfAnalysisTotalPages <= 0 ? "?" : pdfAnalysisTotalPages.ToString())})";

    public double PdfAnalysisPagesPerSecond
    {
        get => pdfAnalysisPagesPerSecond;
        private set
        {
            if (SetProperty(ref pdfAnalysisPagesPerSecond, value))
            {
                OnPropertyChanged(nameof(PdfAnalysisSpeedText));
            }
        }
    }

    public string PdfAnalysisSpeedText =>
        pdfAnalysisStartAt == default
            ? "처리 속도: 0.0 p/s"
            : $"처리 속도: {PdfAnalysisPagesPerSecond:0.0} p/s";

    public bool ShowPdfAnalysisProgress => IsPdfAnalysisInProgress || pdfAnalysisProcessedPages > 0;

    public bool ShowPdfAnalysisPanel => HasPdfAnalysisSummary || ShowPdfAnalysisProgress;

    public bool CanCancelPdfAnalysis =>
        IsPdfAnalysisInProgress &&
        !isPdfAnalysisCommitStarted &&
        pdfAnalysisCancellation is { IsCancellationRequested: false };

    public bool IsSourceFileImportInProgress
    {
        get => isSourceFileImportInProgress;
        private set
        {
            if (SetProperty(ref isSourceFileImportInProgress, value))
            {
                OnPropertyChanged(nameof(CanModifyQuestionStructure));
                OnPropertyChanged(nameof(CanAddCategory));
                OnPropertyChanged(nameof(CanImportAnswerMap));
                OnPropertyChanged(nameof(CanStartPractice));
                OnPropertyChanged(nameof(CanStartWrongPractice));
                OnPropertyChanged(nameof(CanDeleteCategory));
                OnPropertyChanged(nameof(CanDeleteSourceFile));
                UpdateSourceFileActionState();
            }
        }
    }

    public bool IsStructuralOperationInProgress
    {
        get => isStructuralOperationInProgress;
        private set
        {
            if (SetProperty(ref isStructuralOperationInProgress, value))
            {
                UpdatePracticeState();
            }
        }
    }

    public bool IsInitializing
    {
        get => isInitializing;
        private set
        {
            if (SetProperty(ref isInitializing, value))
            {
                UpdatePracticeState();
            }
        }
    }

    public bool IsSelectedCategoryLoading
    {
        get => isSelectedCategoryLoading;
        private set
        {
            if (SetProperty(ref isSelectedCategoryLoading, value))
            {
                UpdatePracticeState();
            }
        }
    }

    public string NewCategoryName
    {
        get => newCategoryName;
        set
        {
            if (SetProperty(ref newCategoryName, value))
            {
                OnPropertyChanged(nameof(CanAddCategory));
            }
        }
    }

    public string SelectedSourceFileName
    {
        get => selectedSourceFileName;
        set
        {
            if (SetProperty(ref selectedSourceFileName, value))
            {
                OnPropertyChanged(nameof(CanImportAnswerMap));
            }
        }
    }

    public bool CanImportAnswerMap =>
        SelectedCategory != null &&
        CanModifyQuestionStructure &&
        IsPdfAnalysisSupported &&
        !string.IsNullOrWhiteSpace(SelectedSourceFileName);

    public bool IsPdfAnalysisSupported => DeviceInfo.Platform != DevicePlatform.iOS;

    public bool ShowPdfAnalysisUnsupportedNotice => !IsPdfAnalysisSupported;

    public string AnswerMapText
    {
        get => answerMapText;
        set
        {
            if (SetProperty(ref answerMapText, value))
            {
                OnPropertyChanged(nameof(CanImportAnswerMap));
            }
        }
    }

    public string ExpectedQuestionRangeText
    {
        get => expectedQuestionRangeText;
        set => SetProperty(ref expectedQuestionRangeText, value);
    }

    public string PracticeCountText
    {
        get => practiceCountText;
        set
        {
            if (SetProperty(ref practiceCountText, value))
            {
                OnPropertyChanged(nameof(CanStartPractice));
            }
        }
    }

    public string Feedback
    {
        get => feedback;
        set
        {
            if (SetProperty(ref feedback, value))
            {
                OnPropertyChanged(nameof(HasFeedback));
                OnPropertyChanged(nameof(FeedbackTitle));
                OnPropertyChanged(nameof(FeedbackBackgroundColor));
                OnPropertyChanged(nameof(FeedbackBorderColor));
                OnPropertyChanged(nameof(FeedbackTitleColor));
            }
        }
    }

    public bool HasFeedback => !string.IsNullOrWhiteSpace(Feedback);

    public string FeedbackTitle => ResolveFeedbackTone(Feedback) switch
    {
        FeedbackTone.Success => "완료",
        FeedbackTone.Warning => "확인",
        FeedbackTone.Error => "문제 발생",
        _ => "상태"
    };

    public Color FeedbackBackgroundColor => ResolveFeedbackTone(Feedback) switch
    {
        FeedbackTone.Success => Color.FromArgb("#F0FDF4"),
        FeedbackTone.Warning => Color.FromArgb("#FFF7ED"),
        FeedbackTone.Error => Color.FromArgb("#FEF2F2"),
        _ => Color.FromArgb("#F8FAFC")
    };

    public Color FeedbackBorderColor => ResolveFeedbackTone(Feedback) switch
    {
        FeedbackTone.Success => Color.FromArgb("#86EFAC"),
        FeedbackTone.Warning => Color.FromArgb("#FDBA74"),
        FeedbackTone.Error => Color.FromArgb("#FCA5A5"),
        _ => Color.FromArgb("#CBD5E1")
    };

    public Color FeedbackTitleColor => ResolveFeedbackTone(Feedback) switch
    {
        FeedbackTone.Success => Color.FromArgb("#166534"),
        FeedbackTone.Warning => Color.FromArgb("#C2410C"),
        FeedbackTone.Error => Color.FromArgb("#B91C1C"),
        _ => Color.FromArgb("#0F766E")
    };

    public string SelectedWorkspaceSection
    {
        get => selectedWorkspaceSection;
        private set
        {
            if (SetProperty(ref selectedWorkspaceSection, value))
            {
                OnPropertyChanged(nameof(IsImportWorkspaceSelected));
                OnPropertyChanged(nameof(IsPracticeWorkspaceSelected));
                OnPropertyChanged(nameof(IsWrongWorkspaceSelected));
                OnPropertyChanged(nameof(ShowImportWorkspace));
                OnPropertyChanged(nameof(ShowPracticeWorkspace));
                OnPropertyChanged(nameof(ShowWrongWorkspace));
            }
        }
    }

    public bool IsImportWorkspaceSelected => SelectedWorkspaceSection == WorkspaceSectionImport;

    public bool IsPracticeWorkspaceSelected => SelectedWorkspaceSection == WorkspaceSectionPractice;

    public bool IsWrongWorkspaceSelected => SelectedWorkspaceSection == WorkspaceSectionWrong;

    public bool ShowImportWorkspace => IsImportWorkspaceSelected;

    public bool ShowPracticeWorkspace => IsPracticeWorkspaceSelected;

    public bool ShowWrongWorkspace => IsWrongWorkspaceSelected;

    public bool UseWideWorkspaceLayout => useWideWorkspaceLayout;

    public bool IsTopSummaryExpanded
    {
        get => isTopSummaryExpanded;
        private set
        {
            if (SetProperty(ref isTopSummaryExpanded, value))
            {
                OnPropertyChanged(nameof(TopSummaryToggleText));
            }
        }
    }

    public string TopSummaryToggleText => IsTopSummaryExpanded ? "접기" : "펼치기";

    public bool HasPdfAnalysisSummary => !string.IsNullOrWhiteSpace(PdfAnalysisSummary);

    public string LatestPdfDiagnosticsText
    {
        get => latestPdfDiagnosticsText;
        private set
        {
            if (SetProperty(ref latestPdfDiagnosticsText, value))
            {
                OnPropertyChanged(nameof(HasLatestPdfDiagnostics));
            }
        }
    }

    public string LatestPdfDiagnosticsTitle
    {
        get => latestPdfDiagnosticsTitle;
        private set => SetProperty(ref latestPdfDiagnosticsTitle, value);
    }

    public bool HasLatestPdfDiagnostics => !string.IsNullOrWhiteSpace(LatestPdfDiagnosticsText);

    public string SessionFeedback
    {
        get => sessionFeedback;
        set
        {
            if (SetProperty(ref sessionFeedback, value))
            {
                OnPropertyChanged(nameof(HasSessionFeedback));
            }
        }
    }

    public bool HasSessionFeedback => !string.IsNullOrWhiteSpace(SessionFeedback);

    public string UserAnswer
    {
        get => userAnswer;
        set
        {
            if (SetProperty(ref userAnswer, value))
            {
                NotifyAnswerInputValidationState();
            }
        }
    }

    public bool IsPracticeRunning
    {
        get => isPracticeRunning;
        set
        {
            if (SetProperty(ref isPracticeRunning, value))
            {
                OnPropertyChanged(nameof(CanModifyQuestionStructure));
                OnPropertyChanged(nameof(CanAddCategory));
                OnPropertyChanged(nameof(CanImportAnswerMap));
                OnPropertyChanged(nameof(CanStartPractice));
                OnPropertyChanged(nameof(CanStartWrongPractice));
                OnPropertyChanged(nameof(CanDeleteCategory));
                OnPropertyChanged(nameof(CanDeleteSourceFile));
                OnPropertyChanged(nameof(CanStopPractice));
                NotifyAnswerInputValidationState();
                UpdateSourceFileActionState();
            }
        }
    }

    public bool IsPracticeStartInProgress
    {
        get => isPracticeStartInProgress;
        private set
        {
            if (SetProperty(ref isPracticeStartInProgress, value))
            {
                OnPropertyChanged(nameof(CanModifyQuestionStructure));
                OnPropertyChanged(nameof(CanAddCategory));
                OnPropertyChanged(nameof(CanImportAnswerMap));
                OnPropertyChanged(nameof(CanStartPractice));
                OnPropertyChanged(nameof(CanStartWrongPractice));
                OnPropertyChanged(nameof(CanDeleteCategory));
                OnPropertyChanged(nameof(CanDeleteSourceFile));
                UpdateSourceFileActionState();
            }
        }
    }

    public bool OverwriteExisting
    {
        get => overwriteExisting;
        set => SetProperty(ref overwriteExisting, value);
    }

    public Category? SelectedCategory
    {
        get => selectedCategory;
        set
        {
            if (SetProperty(ref selectedCategory, value))
            {
                ResetSelectedCategorySummary();
                OnPropertyChanged(nameof(HasSelectedCategory));
                OnPropertyChanged(nameof(CanStartPractice));
                OnPropertyChanged(nameof(CanDeleteCategory));
                OnPropertyChanged(nameof(CanDeleteSourceFile));
                OnPropertyChanged(nameof(CanImportAnswerMap));
                RunBackground(
                    UpdateSelectedCategoryQuestionCountAsync,
                    "카테고리 문항 수를 갱신하지 못했습니다");
            }
        }
    }

    public SourceFileSummary? SelectedSourceFile
    {
        get => selectedSourceFile;
        set
        {
            if (SetProperty(ref selectedSourceFile, value))
            {
                OnPropertyChanged(nameof(CanDeleteSourceFile));
            }
        }
    }

    public bool IncludeUnansweredInPractice
    {
        get => includeUnansweredInPractice;
        set
        {
            if (SetProperty(ref includeUnansweredInPractice, value))
            {
                OnPropertyChanged(nameof(MaxPracticeCount));
                OnPropertyChanged(nameof(CanStartPractice));
                RunBackground(
                    UpdateSelectedCategoryQuestionCountAsync,
                    "연습 가능 문항 수를 갱신하지 못했습니다");
            }
        }
    }

    public Question? CurrentQuestion
    {
        get => currentQuestion;
        private set
        {
            if (SetProperty(ref currentQuestion, value))
            {
                OnPropertyChanged(nameof(CurrentQuestionSemanticDescription));
                NotifyAnswerInputValidationState();
            }
        }
    }

    public int SessionTotalCount
    {
        get => sessionCount;
        private set
        {
            if (SetProperty(ref sessionCount, value))
            {
                OnPropertyChanged(nameof(NextQuestionButtonText));
            }
        }
    }

    public int SessionCurrentIndex
    {
        get => currentIndex;
        private set
        {
            if (SetProperty(ref currentIndex, value))
            {
                OnPropertyChanged(nameof(ProgressDisplay));
                OnPropertyChanged(nameof(NextQuestionButtonText));
            }
        }
    }

    public int CorrectCount
    {
        get => correctCount;
        private set
        {
            if (SetProperty(ref correctCount, value))
            {
                OnPropertyChanged(nameof(ProgressDisplay));
            }
        }
    }

    public string SelectedCategoryQuestionCountText
    {
        get => selectedCategoryQuestionCount.ToString();
    }

    public int MaxPracticeCount =>
        IncludeUnansweredInPractice
            ? selectedCategoryQuestionCount
            : selectedCategoryPracticeQuestionCount;

    public int WrongQuestionCount => WrongQuestions.Count;

    public bool CanModifyQuestionStructure =>
        !IsInitializing &&
        !IsSelectedCategoryLoading &&
        !IsStructuralOperationInProgress &&
        !IsPracticeRunning &&
        !IsPracticeStartInProgress &&
        !IsPdfAnalysisInProgress &&
        !IsSourceFileImportInProgress;

    public bool CanAddCategory => CanModifyQuestionStructure && !string.IsNullOrWhiteSpace(NewCategoryName);

    public bool HasSelectedCategory => SelectedCategory != null;

    public bool CanDeleteCategory => SelectedCategory != null && CanModifyQuestionStructure;

    public bool CanDeleteSourceFile =>
        SelectedSourceFile != null &&
        SelectedCategory != null &&
        CanModifyQuestionStructure;

    public bool CanStartPractice =>
        HasSelectedCategory &&
        CanModifyQuestionStructure &&
        int.TryParse(PracticeCountText, out var count) && count > 0 &&
        count <= MaxPracticeCount;

    public bool CanStartWrongPractice =>
        CanModifyQuestionStructure &&
        WrongQuestionCount > 0;

    public bool IsMultipleChoiceAnswerExpected =>
        IsPracticeRunning &&
        CurrentQuestion?.Type == QuestionType.MultipleChoice &&
        CurrentQuestion.CorrectAnswers?.Any(x => !string.IsNullOrWhiteSpace(x)) == true;

    public string UserAnswerValidationMessage
    {
        get
        {
            if (!IsMultipleChoiceAnswerExpected)
            {
                return string.Empty;
            }

            if (string.IsNullOrWhiteSpace(UserAnswer))
            {
                return "객관식 답을 입력해 주세요.";
            }

            if (!TryParsePositiveInteger(UserAnswer, out var answerNumber))
            {
                return "객관식 답은 1 이상의 정수 하나만 입력해 주세요.";
            }

            var choiceCount = CurrentQuestion?.Choices?.Length ?? 0;
            return choiceCount > 0 && answerNumber > choiceCount
                ? $"객관식 답은 1부터 {choiceCount} 사이에서 입력해 주세요."
                : string.Empty;
        }
    }

    public int GradedCount
    {
        get => gradedCount;
        private set => SetProperty(ref gradedCount, value);
    }

    public bool IsAnswerRevealed
    {
        get => isAnswerRevealed;
        private set
        {
            if (SetProperty(ref isAnswerRevealed, value))
            {
                OnPropertyChanged(nameof(CanEnterAnswer));
                OnPropertyChanged(nameof(CanGoToNextQuestion));
                OnPropertyChanged(nameof(NextQuestionButtonText));
                NotifyAnswerInputValidationState();
            }
        }
    }

    public bool IsAnswerSubmissionInProgress
    {
        get => isAnswerSubmissionInProgress;
        private set
        {
            if (SetProperty(ref isAnswerSubmissionInProgress, value))
            {
                OnPropertyChanged(nameof(SubmitAnswerButtonText));
                OnPropertyChanged(nameof(CanStopPractice));
                NotifyAnswerInputValidationState();
            }
        }
    }

    public bool HasUserAnswerValidationError =>
        !string.IsNullOrWhiteSpace(UserAnswerValidationMessage);

    public bool CanSubmitAnswer =>
        IsPracticeRunning &&
        CurrentQuestion != null &&
        !IsAnswerRevealed &&
        !IsAnswerSubmissionInProgress &&
        !HasUserAnswerValidationError;

    public bool CanEnterAnswer =>
        IsPracticeRunning &&
        CurrentQuestion != null &&
        !IsAnswerRevealed &&
        !IsAnswerSubmissionInProgress;

    public bool CanGoToNextQuestion =>
        IsPracticeRunning &&
        CurrentQuestion != null &&
        IsAnswerRevealed &&
        !IsAnswerSubmissionInProgress;

    public bool CanStopPractice => IsPracticeRunning && !IsAnswerSubmissionInProgress;

    public string SubmitAnswerButtonText =>
        IsAnswerSubmissionInProgress ? "채점 중…" : "채점";

    public string NextQuestionButtonText =>
        SessionCurrentIndex + 1 >= SessionTotalCount ? "결과 보기" : "다음 문제";

    public string ProgressDisplay =>
        IsPracticeRunning
            ? $"{SessionCurrentIndex + 1}/{SessionTotalCount}"
            : string.Empty;

    public string CurrentQuestionText => CurrentQuestion?.Prompt ?? string.Empty;

    public string CurrentQuestionSemanticDescription
    {
        get
        {
            return string.Join(
                ". ",
                new[]
                {
                    CurrentQuestionSourceDisplay,
                    CurrentQuestionText,
                    CurrentQuestionChoicesText
                }.Where(value => !string.IsNullOrWhiteSpace(value)));
        }
    }

    public string CurrentQuestionSourceDisplay
    {
        get
        {
            if (CurrentQuestion == null)
            {
                return string.Empty;
            }

            var sourceFileName = string.IsNullOrWhiteSpace(CurrentQuestion.SourceFileName)
                ? "파일 정보 없음"
                : Path.GetFileName(CurrentQuestion.SourceFileName);
            var questionIndexText = CurrentQuestion.Index > 0
                ? $"{CurrentQuestion.Index}번 문제"
                : "문항 번호 없음";

            return $"파일: {sourceFileName} / {questionIndexText}";
        }
    }

    public double CurrentQuestionImageViewportWidth => currentQuestionImageViewportWidth;

    public double CurrentQuestionImageViewportHeight => currentQuestionImageViewportHeight;

    public bool HasCurrentQuestionImages => CurrentQuestionImageSlices.Count > 0;

    public bool ShowCurrentQuestionText => !HasCurrentQuestionImages;

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

    public string CurrentQuestionChoicesText => CurrentQuestion is null || CurrentQuestion.Choices.Length == 0
        ? string.Empty
        : string.Join("\n", CurrentQuestion.Choices.Select((x, i) => $"{i + 1}. {x}"));

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
                UpdateCurrentQuestionImageSlices(CurrentQuestion);
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

    public RelayCommand AddCategoryCommand { get; }
    public RelayCommand ImportAnswerMapCommand { get; }
    public RelayCommand StartPracticeCommand { get; }
    public RelayCommand SubmitAnswerCommand { get; }
    public RelayCommand NextQuestionCommand { get; }
    public RelayCommand StopPracticeCommand { get; }
    public RelayCommand CancelPdfAnalysisCommand { get; }
    public RelayCommand StartWrongPracticeCommand { get; }
    public RelayCommand ReloadWrongCommand { get; }
    public RelayCommand<Guid?> RemoveWrongCommand { get; }
    public RelayCommand DeleteCategoryCommand { get; }
    public RelayCommand<SourceFileSummary?> DeleteSourceFileCommand { get; }
    public RelayCommand LoadAnswerFileCommand { get; }
    public RelayCommand LoadSourceFilesFromDirectoryCommand { get; }
    public RelayCommand ImportSourceFileCommand { get; }
    public RelayCommand ShowImportWorkspaceCommand { get; }
    public RelayCommand ShowPracticeWorkspaceCommand { get; }
    public RelayCommand ShowWrongWorkspaceCommand { get; }
    public RelayCommand ToggleTopSummaryCommand { get; }
    public RelayCommand IncreaseQuestionImageZoomCommand { get; }
    public RelayCommand DecreaseQuestionImageZoomCommand { get; }
    public RelayCommand ResetQuestionImageZoomCommand { get; }
    public RelayCommand ClearFeedbackCommand { get; }

    public MainViewModel() : this(new PracticeRepository(), new PdfAnalysisService())
    {
    }

    public MainViewModel(IPracticeRepository repository, IPdfAnalysisService pdfAnalysisService)
    {
        this.repository = repository;
        this.pdfAnalysisService = pdfAnalysisService;
        var idiom = DeviceInfo.Idiom;
        var initialWidth = idiom == DeviceIdiom.Desktop
            ? 1280d
            : idiom == DeviceIdiom.Tablet
                ? 1000d
                : 390d;
        ApplyWorkspaceMetrics(ResponsiveLayoutCalculator.Calculate(
            initialWidth,
            idiom == DeviceIdiom.Phone ? 760d : 900d,
            WideWorkspaceMinimumWidth,
            WideWorkspaceHorizontalChrome,
            NarrowWorkspaceHorizontalChrome,
            220d,
            920d,
            220d,
            620d), rebuildImages: false);
        isTopSummaryExpanded = UseWideWorkspaceLayout;
        sourceFileDirectory = Path.Combine(FileSystem.AppDataDirectory, SourceFileDirectoryName);
        sourceAnalysisDirectory = Path.Combine(sourceFileDirectory, AnalysisDirectoryName);
        AddCategoryCommand = CreateAsyncCommand(AddCategoryAsync);
        ImportAnswerMapCommand = CreateAsyncCommand(ImportAnswerMapAsync);
        StartPracticeCommand = CreateAsyncCommand(StartPracticeAsync);
        SubmitAnswerCommand = CreateAsyncCommand(SubmitAnswerAsync);
        NextQuestionCommand = new RelayCommand(GoToNextQuestion);
        StopPracticeCommand = CreateAsyncCommand(StopPracticeAsync);
        CancelPdfAnalysisCommand = new RelayCommand(CancelPdfAnalysis, () => CanCancelPdfAnalysis);
        StartWrongPracticeCommand = CreateAsyncCommand(StartWrongPracticeAsync);
        ReloadWrongCommand = CreateAsyncCommand(ReloadWrongAsync);
        RemoveWrongCommand = CreateAsyncCommand<Guid?>(async questionId =>
        {
            if (questionId.HasValue && questionId.Value != Guid.Empty)
            {
                await RemoveWrongById(questionId.Value);
            }
        });
        DeleteCategoryCommand = CreateAsyncCommand(DeleteCategoryAsync);
        DeleteSourceFileCommand = CreateAsyncCommand<SourceFileSummary?>(DeleteSourceFileAsync);
        LoadAnswerFileCommand = CreateAsyncCommand(LoadAnswerFileAsync);
        LoadSourceFilesFromDirectoryCommand = CreateAsyncCommand(LoadSourceFilesFromDirectoryAsync);
        ImportSourceFileCommand = CreateAsyncCommand(ImportSourceFileAsync);
        ShowImportWorkspaceCommand = new RelayCommand(() => SetWorkspaceSection(WorkspaceSectionImport));
        ShowPracticeWorkspaceCommand = new RelayCommand(() => SetWorkspaceSection(WorkspaceSectionPractice));
        ShowWrongWorkspaceCommand = new RelayCommand(() => SetWorkspaceSection(WorkspaceSectionWrong));
        ToggleTopSummaryCommand = new RelayCommand(() => IsTopSummaryExpanded = !IsTopSummaryExpanded);
        IncreaseQuestionImageZoomCommand = new RelayCommand(
            () => SetQuestionImageZoom(QuestionImageZoom + QuestionImageZoomStep),
            () => CanIncreaseQuestionImageZoom);
        DecreaseQuestionImageZoomCommand = new RelayCommand(
            () => SetQuestionImageZoom(QuestionImageZoom - QuestionImageZoomStep),
            () => CanDecreaseQuestionImageZoom);
        ResetQuestionImageZoomCommand = new RelayCommand(
            () => SetQuestionImageZoom(1d),
            () => CanResetQuestionImageZoom);
        ClearFeedbackCommand = new RelayCommand(() => Feedback = string.Empty);

        _ = InitializeAsync();
    }

    private RelayCommand CreateAsyncCommand(Func<Task> execute)
    {
        var isExecuting = 0;
        RelayCommand? command = null;
        command = new RelayCommand(
            async void () =>
            {
                if (Interlocked.CompareExchange(ref isExecuting, 1, 0) != 0)
                {
                    return;
                }

                command!.RaiseCanExecuteChanged();
                try
                {
                    await ExecuteSafelyAsync(execute, "작업 중 오류가 발생했습니다");
                }
                finally
                {
                    Interlocked.Exchange(ref isExecuting, 0);
                    command!.RaiseCanExecuteChanged();
                }
            },
            () => Volatile.Read(ref isExecuting) == 0);
        return command;
    }

    private RelayCommand<T> CreateAsyncCommand<T>(Func<T?, Task> execute)
    {
        var isExecuting = 0;
        RelayCommand<T>? command = null;
        command = new RelayCommand<T>(
            async parameter =>
            {
                if (Interlocked.CompareExchange(ref isExecuting, 1, 0) != 0)
                {
                    return;
                }

                command!.RaiseCanExecuteChanged();
                try
                {
                    await ExecuteSafelyAsync(
                        () => execute(parameter),
                        "작업 중 오류가 발생했습니다");
                }
                finally
                {
                    Interlocked.Exchange(ref isExecuting, 0);
                    command!.RaiseCanExecuteChanged();
                }
            },
            _ => Volatile.Read(ref isExecuting) == 0);
        return command;
    }

    private bool TryBeginStructuralOperation(string busyMessage)
    {
        if (!CanModifyQuestionStructure ||
            Interlocked.CompareExchange(ref structuralOperationGate, 1, 0) != 0)
        {
            Feedback = busyMessage;
            return false;
        }

        IsStructuralOperationInProgress = true;
        return true;
    }

    private void EndStructuralOperation()
    {
        Interlocked.Exchange(ref structuralOperationGate, 0);
        IsStructuralOperationInProgress = false;
    }

    private void RunBackground(Func<Task> execute, string failureMessage)
    {
        _ = ExecuteSafelyAsync(execute, failureMessage);
    }

    private async Task ExecuteSafelyAsync(Func<Task> execute, string failureMessage)
    {
        try
        {
            await execute();
        }
        catch (Exception ex)
        {
            Feedback = $"{failureMessage}: {ex.Message}";
            AppLog.Error(nameof(MainViewModel), failureMessage, ex);
        }
    }

    private async Task InitializeAsync()
    {
        try
        {
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Feedback = $"초기 데이터를 불러오지 못했습니다: {ex.Message}";
            AppLog.Error(nameof(MainViewModel), "초기화 실패", ex);
        }
        finally
        {
            IsInitializing = false;
        }
    }

    private async Task LoadAsync()
    {
        await LoadSourceFilesFromDirectoryAsync();
        var categories = await repository.GetCategoriesAsync();
        Categories.Clear();
        foreach (var category in categories)
        {
            Categories.Add(category);
        }

        if (Categories.Count > 0)
        {
            SelectedCategory = Categories[0];
        }

        await UpdateSelectedCategoryQuestionCountAsync();
        await ReloadWrongAsync();
        UpdatePracticeState();
    }

    private async Task AddCategoryAsync()
    {
        if (!CanModifyQuestionStructure)
        {
            Feedback = IsPracticeRunning
                ? "연습 중에는 카테고리를 추가할 수 없습니다."
                : "문항 분석 중에는 카테고리를 추가할 수 없습니다.";
            return;
        }

        if (string.IsNullOrWhiteSpace(NewCategoryName))
        {
            Feedback = "카테고리 이름이 비어 있습니다.";
            return;
        }

        if (!TryBeginStructuralOperation("다른 문항 구조 변경 작업이 진행 중입니다."))
        {
            return;
        }

        var categoryName = NewCategoryName;
        try
        {
            var category = await repository.AddOrGetCategoryAsync(categoryName);
            if (!Categories.Any(x => x.Id == category.Id))
            {
                Categories.Add(category);
                Feedback = $"카테고리 '{category.Name}'를 추가했습니다.";
            }
            else
            {
                Feedback = $"카테고리 '{category.Name}'가 이미 존재합니다.";
            }

            SelectedCategory = category;
            NewCategoryName = string.Empty;
            await UpdateSelectedCategoryQuestionCountAsync();
        }
        finally
        {
            EndStructuralOperation();
        }
    }

    private async Task ImportAnswerMapAsync()
    {
        if (!IsPdfAnalysisSupported)
        {
            Feedback = "현재 iOS에서는 PDF OCR 분석을 지원하지 않습니다. Windows, Android 또는 OCR 도구가 설치된 Mac에서 문항을 먼저 반영해 주세요.";
            return;
        }

        if (!CanModifyQuestionStructure)
        {
            Feedback = IsPracticeRunning
                ? "연습 중에는 문항을 가져오거나 갱신할 수 없습니다."
                : "이미 문항 분석이 진행 중입니다.";
            return;
        }

        SetWorkspaceSection(WorkspaceSectionImport);

        var targetCategory = SelectedCategory;
        if (targetCategory == null)
        {
            Feedback = "카테고리를 선택해 주세요.";
            return;
        }

        if (!TryParseExpectedQuestionInput(
                ExpectedQuestionRangeText,
                out var expectedQuestionInput,
                out var expectedQuestionInputError))
        {
            Feedback = expectedQuestionInputError;
            return;
        }

        var selectedSourceFileSnapshot = SelectedSourceFileName;
        if (string.IsNullOrWhiteSpace(selectedSourceFileSnapshot))
        {
            Feedback = "문항 파일을 먼저 선택해 주세요.";
            return;
        }

        var answerMapSnapshot = AnswerMapText;
        var explicitEmptyAnswerIndexes = GetExplicitEmptyAnswerIndexes(answerMapSnapshot);
        var result = QuestionSetParser.ParseAnswerMapWithDetails(answerMapSnapshot);
        var blockingParseErrors = result.Errors
            .Where(error => !IsAllowedExplicitEmptyAnswerError(error, explicitEmptyAnswerIndexes))
            .ToArray();
        if (blockingParseErrors.Length > 0)
        {
            Feedback = $"정답맵 검증 오류: {string.Join(", ", blockingParseErrors)}\n문항 반영을 중단했습니다.";
            return;
        }

        var nonPositiveMultipleChoiceAnswerIndexes = result.Questions
            .Where(question =>
                question.Type == QuestionType.MultipleChoice &&
                question.CorrectAnswers.Any(answer =>
                    int.TryParse(answer, out var value) && value <= 0))
            .Select(question => question.Index)
            .Distinct()
            .OrderBy(index => index)
            .ToArray();
        if (nonPositiveMultipleChoiceAnswerIndexes.Length > 0)
        {
            Feedback = "객관식 정답은 1 이상의 정수여야 합니다. 확인할 문항: " +
                       string.Join(", ", nonPositiveMultipleChoiceAnswerIndexes) +
                       "\n문항 반영을 중단했습니다.";
            return;
        }

        var answerIndexesToUpdate = result.Questions
            .Select(x => x.Index)
            .Where(x => x > 0)
            .ToHashSet();
        var overwriteExistingSnapshot = OverwriteExisting;
        var normalizedSourceFileName = NormalizeSourceFileName(selectedSourceFileSnapshot);
        var sourceFilePath = ResolveSourceFilePath(normalizedSourceFileName);
        if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath))
        {
            Feedback = $"문항 파일을 찾을 수 없습니다: {normalizedSourceFileName}";
            AppLog.Error(
                nameof(MainViewModel),
                $"문항 파일 경로 확인 실패 | source={normalizedSourceFileName}");
            return;
        }

        AppLog.Info(
            nameof(MainViewModel),
            $"문항 분석 시작 | category={targetCategory.Id} | file={sourceFilePath}");

        ClearPdfAnalysisState();
        isPdfAnalysisCommitStarted = false;
        pdfAnalysisCancellation?.Dispose();
        var analysisCancellation = new CancellationTokenSource();
        pdfAnalysisCancellation = analysisCancellation;
        IsPdfAnalysisInProgress = true;
        pdfAnalysisStartAt = DateTimeOffset.UtcNow;
        PdfAnalysisProgressValue = 0d;
        PdfAnalysisStatus = "문항 PDF OCR 분석을 시작합니다.";
        PdfAnalysisStatusColor = Color.FromArgb("#0EA5E9");
        Feedback = PdfAnalysisStatus;

        var questionCommitCompleted = false;
        var committedQuestionCount = 0;

        try
        {
            var progress = new Progress<PdfAnalysisProgress>(value =>
            {
                if (ReferenceEquals(pdfAnalysisCancellation, analysisCancellation) &&
                    !analysisCancellation.IsCancellationRequested)
                {
                    UpdatePdfAnalysisProgress(value);
                }
            });
            var analysis = await pdfAnalysisService.AnalyzePdfAsync(
                sourceFilePath,
                progress,
                expectedQuestionInput.ExplicitRange,
                analysisCancellation.Token);
            analysisCancellation.Token.ThrowIfCancellationRequested();
            if (!analysis.IsSuccess)
            {
                PdfAnalysisSummary = $"문항 분석 실패: {analysis.Message}";
                PdfAnalysisStatus = "문항 분석이 실패했습니다. 상태를 확인해 주세요.";
                PdfAnalysisStatusColor = Color.FromArgb("#DC2626");
                Feedback = $"{PdfAnalysisSummary}\n로그: {AppLog.CurrentLogFilePath}";
                AppLog.Error(
                    nameof(MainViewModel),
                    $"문항 분석 실패 | file={sourceFilePath} | reason={analysis.Message}");
                return;
            }

            AppLog.Info(
                nameof(MainViewModel),
                $"문항 분석 성공 | file={sourceFilePath} | pages={analysis.PageCount} | candidates={analysis.DetectedQuestionCount}");

            var resolvedExpectedQuestionRange = ResolveExpectedQuestionRange(analysis, expectedQuestionInput);
            if (resolvedExpectedQuestionRange.IsAutoInferred &&
                resolvedExpectedQuestionRange.Range is QuestionNumberRange inferredRange)
            {
                PdfAnalysisStatus = "예상 문항 범위에 맞춰 구조를 다시 검증하고 있습니다.";
                var refinedCandidates = await Task.Run(
                    () => OcrQuestionSegmenter.SplitByHeader(analysis.Pages, inferredRange),
                    analysisCancellation.Token);
                analysis = WithQuestionCandidates(analysis, refinedCandidates);
                AppLog.Info(
                    nameof(MainViewModel),
                    $"문항 범위 자동 추정 | file={sourceFilePath} | count={resolvedExpectedQuestionRange.ExpectedCount} | range={inferredRange} | reason={resolvedExpectedQuestionRange.Reason}");
            }
            else if (expectedQuestionInput.IsCountOnly)
            {
                AppLog.Error(
                    nameof(MainViewModel),
                    $"문항 범위 자동 추정 실패 | file={sourceFilePath} | count={expectedQuestionInput.ExpectedCount} | reason={resolvedExpectedQuestionRange.Reason}");
            }

            UpdatePdfAnalysisProgress(new PdfAnalysisProgress(
                analysis.PageCount,
                analysis.PageCount,
                "OCR 완료 · 문항 구조 검증 중"));

            analysisCancellation.Token.ThrowIfCancellationRequested();

            var diagnostics = await Task.Run(
                () => BuildPdfAnalysisDiagnostics(
                    normalizedSourceFileName,
                    analysis,
                    expectedQuestionInput,
                    resolvedExpectedQuestionRange),
                analysisCancellation.Token);
            var repairResult = await Task.Run(
                () => TryAutoRepairAnalysis(
                    normalizedSourceFileName,
                    analysis,
                    diagnostics,
                    expectedQuestionInput,
                    resolvedExpectedQuestionRange),
                analysisCancellation.Token);
            analysis = repairResult.Analysis;
            diagnostics = repairResult.Diagnostics;
            analysisCancellation.Token.ThrowIfCancellationRequested();
            await SavePdfAnalysisAsync(
                normalizedSourceFileName,
                analysis,
                saveAsLatestAttempt: true);
            analysisCancellation.Token.ThrowIfCancellationRequested();
            LatestPdfDiagnosticsTitle = Path.GetFileName(normalizedSourceFileName);
            LatestPdfDiagnosticsText = BuildDiagnosticsText(diagnostics);
            var diagnosticsPath = await SavePdfAnalysisDiagnosticsAsync(
                normalizedSourceFileName,
                diagnostics,
                saveAsLatestAttempt: true);
            analysisCancellation.Token.ThrowIfCancellationRequested();
            PdfAnalysisSummary = BuildAnalysisSummary(analysis, diagnostics);
            PdfAnalysisStatus = "문항 분석이 완료되었습니다.";
            PdfAnalysisStatusColor = Color.FromArgb("#16A34A");

            if (diagnostics.HasExpectedQuestionMismatch || diagnostics.HasBlockingStructuralIssues)
            {
                var blockingSummaries = new List<string>();
                if (diagnostics.HasExpectedQuestionMismatch)
                {
                    blockingSummaries.Add(BuildExpectedQuestionMismatchSummary(diagnostics));
                }

                if (diagnostics.HasBlockingStructuralIssues)
                {
                    blockingSummaries.Add($"구조 검증 실패: {BuildStructuralIssueSummary(diagnostics)}");
                }

                PdfAnalysisStatus = diagnostics.HasBlockingStructuralIssues
                    ? "문항 구조 검증에 실패했습니다."
                    : "예상 문항 범위와 분석 결과가 일치하지 않습니다.";
                PdfAnalysisStatusColor = Color.FromArgb("#DC2626");
                Feedback = $"{string.Join("\n", blockingSummaries)}\n문항 반영을 중단했습니다." +
                           (string.IsNullOrWhiteSpace(diagnosticsPath) ? string.Empty : $"\n진단 파일: {diagnosticsPath}");
                AppLog.Error(
                    nameof(MainViewModel),
                    $"문항 반영 중단 | expectedCount={diagnostics.ExpectedQuestionCount?.ToString() ?? "n/a"} | expectedRange={diagnostics.ExpectedQuestionRange?.ToString() ?? "n/a"} | detected={diagnostics.DistinctCandidateCount} | missing={string.Join(",", diagnostics.MissingIndexes)} | unexpected={string.Join(",", diagnostics.UnexpectedIndexes)} | duplicates={string.Join(",", diagnostics.DuplicateIndexes.Select(x => x.Index))} | structural={string.Join(",", diagnostics.StructuralIssues.Select(x => $"{x.Code}:{x.Index?.ToString() ?? "n/a"}"))} | diag={diagnosticsPath ?? "n/a"}");
                return;
            }

            if (!analysis.HasQuestionCandidates)
            {
                PdfAnalysisStatus = "문항 후보 검증에 실패했습니다.";
                PdfAnalysisStatusColor = Color.FromArgb("#DC2626");
                Feedback = $"{analysis.Summary}\nOCR 문항 후보가 없어 문항 반영을 중단했습니다.";
                AppLog.Error(
                    nameof(MainViewModel),
                    $"문항 저장 중단 | 후보 0개 | file={sourceFilePath}");
                return;
            }

            var candidateByIndex = analysis.QuestionCandidates
                .Where(x => x.Index > 0)
                .GroupBy(x => x.Index)
                .ToDictionary(x => x.Key, x => x.First());

            var answerByIndex = result.Questions
                .GroupBy(x => x.Index)
                .ToDictionary(
                    x => x.Key,
                    x => x.OrderByDescending(q => q.CorrectAnswers.Length).First());

            var answerIndexesOutsideCandidates = answerByIndex.Keys
                .Except(candidateByIndex.Keys)
                .OrderBy(x => x)
                .ToArray();
            if (answerIndexesOutsideCandidates.Length > 0)
            {
                PdfAnalysisStatus = "정답맵 문항 번호 검증에 실패했습니다.";
                PdfAnalysisStatusColor = Color.FromArgb("#DC2626");
                Feedback = "정답맵에 OCR 문항 후보와 일치하지 않는 번호가 있습니다: " +
                           $"{string.Join(", ", answerIndexesOutsideCandidates)}\n" +
                           "정답맵 또는 OCR 분석 결과를 확인한 뒤 다시 시도해 주세요.";
                AppLog.Error(
                    nameof(MainViewModel),
                    $"문항 저장 중단 | 후보 밖 정답 번호={string.Join(",", answerIndexesOutsideCandidates)} | file={sourceFilePath}");
                return;
            }

            var existingCount = await repository.GetQuestionCountBySourceFileAsync(
                targetCategory.Id,
                normalizedSourceFileName);
            var shouldOverwrite = overwriteExistingSnapshot;
            if (shouldOverwrite && existingCount > 0)
            {
                shouldOverwrite = await ConfirmOverwriteImportAsync(normalizedSourceFileName, existingCount);
                if (!shouldOverwrite)
                {
                    Feedback = "문항 덮어쓰기를 취소했거나 확인창을 표시할 수 없어 반영을 중단했습니다.";
                    return;
                }
            }

            var sourceQuestionIndexes = candidateByIndex.Keys
                .OrderBy(x => x)
                .ToArray();

            var questions = sourceQuestionIndexes
                .Select(x =>
                {
                    var hasAnswer = answerByIndex.TryGetValue(x, out var parsedQuestion);
                    var correctAnswers = hasAnswer && parsedQuestion != null
                        ? parsedQuestion.CorrectAnswers
                        : Array.Empty<string>();
                    var questionType = hasAnswer && parsedQuestion != null
                        ? parsedQuestion.Type
                        : QuestionType.MultipleChoice;
                    candidateByIndex.TryGetValue(x, out var matchedCandidate);
                    var imageSegments = BuildQuestionImageSegments(
                        matchedCandidate,
                        analysis.QuestionCandidates,
                        analysis.Pages);
                    var primaryImageSegment = imageSegments.FirstOrDefault();

                    if (matchedCandidate != null && imageSegments.Length > 1)
                    {
                        AppLog.Info(
                            nameof(MainViewModel),
                            $"공유 지문 세그먼트 적용 | file={normalizedSourceFileName} | index={x} | segments={imageSegments.Length} | start=p{matchedCandidate.StartPage}:{matchedCandidate.StartLineInPage}");
                    }

                    var question = new Question
                    {
                        CategoryId = targetCategory.Id,
                        SourceFileName = normalizedSourceFileName,
                        Index = x,
                        Type = questionType,
                        CorrectAnswers = correctAnswers,
                        Choices = Array.Empty<string>(),
                        ImageSegments = imageSegments,
                        ImagePath = primaryImageSegment?.ImagePath,
                        ImageTopRatio = primaryImageSegment?.ImageTopRatio ?? 0d,
                        ImageBottomRatio = primaryImageSegment?.ImageBottomRatio ?? 1d
                    };

                    if (candidateByIndex.TryGetValue(x, out var candidate))
                    {
                        if (!string.IsNullOrWhiteSpace(candidate.PreviewText))
                        {
                            question.Prompt = $"{targetCategory.Name} - {candidate.Header} {candidate.PreviewText}";
                        }
                        else
                        {
                            question.Prompt = $"{targetCategory.Name} - {candidate.Header}";
                        }
                    }

                    if (string.IsNullOrWhiteSpace(question.Prompt))
                    {
                        question.Prompt = $"{targetCategory.Name} - 문항 {x}";
                    }

                    return question;
                })
                .ToArray();

            if (questions.Length == 0)
            {
                Feedback = $"{analysis.Summary} / 저장할 문항 후보가 없습니다. 정답 맵에서 문항 번호를 먼저 넣어주세요.";
                AppLog.Error(
                    nameof(MainViewModel),
                    $"문항 저장 중단 | 생성된 questions=0 | file={sourceFilePath}");
                return;
            }

            analysisCancellation.Token.ThrowIfCancellationRequested();
            isPdfAnalysisCommitStarted = true;
            OnPropertyChanged(nameof(CanCancelPdfAnalysis));
            CancelPdfAnalysisCommand.RaiseCanExecuteChanged();
            PdfAnalysisStatus = "검증된 문항을 저장하고 있습니다.";

            var saveResult = await repository.SaveImportedQuestionsAsync(
                targetCategory.Id,
                normalizedSourceFileName,
                questions,
                overwriteBySourceFile: shouldOverwrite,
                updateExistingCorrectAnswers: true,
                answerIndexesToUpdate: answerIndexesToUpdate);
            questionCommitCompleted = true;
            var isAnswerOnlyUpdate = !shouldOverwrite && !saveResult.StructureChanged;
            committedQuestionCount = isAnswerOnlyUpdate
                ? saveResult.UpdatedQuestionCount
                : questions.Length;
            if (saveResult.StructureChanged)
            {
                // 등록된 이미지 경계가 실제로 새 분석으로 교체된 경우에만
                // 파일 검토 화면이 읽는 canonical 진단을 함께 승격한다.
                await SavePdfAnalysisAsync(normalizedSourceFileName, analysis);
                await SavePdfAnalysisDiagnosticsAsync(normalizedSourceFileName, diagnostics);
            }

            var mismatchMessage = isAnswerOnlyUpdate
                ? string.Empty
                : BuildCandidateMismatchMessage(analysis, result, questions);
            var existingQuestionUpdateMessage = isAnswerOnlyUpdate
                ? "기존 이미지 경계는 유지하고 정답맵에 지정된 기존 번호만 갱신했습니다. 새로 검출된 번호를 추가하려면 덮어쓰기를 켜고 다시 반영해 주세요"
                : string.Empty;
            var summary = isAnswerOnlyUpdate
                ? saveResult.UpdatedQuestionCount > 0
                    ? $"기존 문항 정답 {saveResult.UpdatedQuestionCount}개 갱신 완료"
                    : "갱신할 기존 문항 정답 없음"
                : !string.IsNullOrWhiteSpace(mismatchMessage)
                    ? $"{questions.Length}개 저장 / {mismatchMessage}"
                    : $"{questions.Length}개 저장 완료";

            Feedback = $"{analysis.Summary} / {summary}"
                       + (string.IsNullOrWhiteSpace(existingQuestionUpdateMessage) ? string.Empty : $" / {existingQuestionUpdateMessage}");
            AppLog.Info(
                nameof(MainViewModel),
                $"문항 반영 완료 | file={sourceFilePath} | added={saveResult.AddedQuestionCount} | updated={saveResult.UpdatedQuestionCount} | removed={saveResult.RemovedQuestionCount} | structureChanged={saveResult.StructureChanged}");
            if (string.Equals(AnswerMapText, answerMapSnapshot, StringComparison.Ordinal))
            {
                AnswerMapText = string.Empty;
            }
            await UpdateSelectedCategoryQuestionCountAsync();
            var sourceFile = SourceFiles.FirstOrDefault(x =>
                string.Equals(x.SourceFileName, normalizedSourceFileName, StringComparison.OrdinalIgnoreCase));
            if (sourceFile != null)
            {
                SelectedSourceFile = sourceFile;
            }
            await ReloadWrongAsync();
            PdfAnalysisProgressValue = 1d;
            PdfAnalysisStatus = isAnswerOnlyUpdate
                ? $"기존 문항 정답 {saveResult.UpdatedQuestionCount}개 갱신과 화면 새로고침이 완료되었습니다."
                : $"문항 {questions.Length}개 반영과 화면 갱신이 완료되었습니다.";
            PdfAnalysisStatusColor = Color.FromArgb("#16A34A");
        }
        catch (OperationCanceledException) when (analysisCancellation.IsCancellationRequested)
        {
            PdfAnalysisSummary = "문항 분석이 취소되었습니다.";
            PdfAnalysisStatus = "사용자 요청으로 문항 분석을 취소했습니다.";
            PdfAnalysisStatusColor = Color.FromArgb("#475569");
            Feedback = PdfAnalysisStatus;
            AppLog.Info(
                nameof(MainViewModel),
                $"문항 분석 취소 | file={sourceFilePath}");
        }
        catch (Exception ex)
        {
            if (questionCommitCompleted)
            {
                PdfAnalysisStatus = "문항 저장은 완료되었지만 화면 갱신에 실패했습니다.";
                PdfAnalysisStatusColor = Color.FromArgb("#D97706");
                Feedback = $"문항 {committedQuestionCount}개 저장은 완료되었습니다. " +
                           $"화면 목록을 갱신하지 못했습니다: {ex.Message}\n" +
                           "다시 가져오지 말고 카테고리나 화면을 다시 열어 확인해 주세요.";
                AppLog.Error(
                    nameof(MainViewModel),
                    $"문항 저장 후 화면 갱신 실패 | file={sourceFilePath} | saved={committedQuestionCount}",
                    ex);
            }
            else
            {
                Feedback = $"문항 분석 중 오류가 발생했습니다: {ex.Message}";
                PdfAnalysisSummary = $"문항 분석 실패: {ex.Message}";
                PdfAnalysisStatus = "문항 분석 중 오류가 발생했습니다.";
                PdfAnalysisStatusColor = Color.FromArgb("#DC2626");
                AppLog.Error(
                    nameof(MainViewModel),
                    $"문항 분석 예외 | file={sourceFilePath}",
                    ex);
            }
        }
        finally
        {
            if (ReferenceEquals(pdfAnalysisCancellation, analysisCancellation))
            {
                pdfAnalysisCancellation = null;
            }

            isPdfAnalysisCommitStarted = false;
            analysisCancellation.Dispose();
            IsPdfAnalysisInProgress = false;
        }
    }

    private void CancelPdfAnalysis()
    {
        if (!IsPdfAnalysisInProgress || pdfAnalysisCancellation == null)
        {
            return;
        }

        PdfAnalysisStatus = "문항 분석 취소를 요청했습니다. 현재 작업을 정리하고 있습니다.";
        PdfAnalysisStatusColor = Color.FromArgb("#D97706");
        pdfAnalysisCancellation.Cancel();
        OnPropertyChanged(nameof(CanCancelPdfAnalysis));
        CancelPdfAnalysisCommand.RaiseCanExecuteChanged();
    }

    internal static QuestionImageSegment[] BuildQuestionImageSegments(
        OcrQuestionCandidate? candidate,
        IReadOnlyList<OcrQuestionCandidate> allCandidates,
        IReadOnlyList<OcrPageResult> pages)
    {
        if (candidate == null || pages.Count == 0)
        {
            return Array.Empty<QuestionImageSegment>();
        }

        var pageByIndex = pages
            .Where(x => !string.IsNullOrWhiteSpace(x.ImagePath))
            .GroupBy(x => x.PageIndex)
            .ToDictionary(x => x.Key, x => x.First());

        var imageRegions = candidate.ImageRegions ?? Array.Empty<OcrQuestionImageRegion>();
        var sharedContextRegions = candidate.SharedContextRegions ?? Array.Empty<OcrQuestionImageRegion>();
        if (candidate.UsesSemanticImageRegions || imageRegions.Count > 0)
        {
            return sharedContextRegions
                .Concat(imageRegions)
                .Select(region => TryCreateQuestionImageSegment(region, pageByIndex))
                .Where(segment => segment != null)
                .Select(segment => segment!)
                .GroupBy(x => $"{x.PageIndex}:{x.ImageLeftRatio:F6}:{x.ImageTopRatio:F6}:{x.ImageRightRatio:F6}:{x.ImageBottomRatio:F6}")
                .Select(x => x.First())
                .ToArray();
        }

        var sharedSegments = BuildSharedContextImageSegments(candidate, allCandidates, pageByIndex);
        var segments = new List<QuestionImageSegment>();
        for (var pageIndex = candidate.StartPage; pageIndex <= candidate.EndPage; pageIndex++)
        {
            if (!pageByIndex.TryGetValue(pageIndex, out var page) || string.IsNullOrWhiteSpace(page.ImagePath))
            {
                continue;
            }

            var top = pageIndex == candidate.StartPage
                ? ResolveTopImageRatio(candidate.StartLineInPage, candidate.StartPageLineCount)
                : 0d;
            var bottom = pageIndex == candidate.EndPage
                ? ResolveBottomImageRatio(
                    candidate.EndLineInPage,
                    candidate.EndPageLineCount <= 0 ? candidate.StartPageLineCount : candidate.EndPageLineCount,
                    top)
                : 1d;
            var left = 0d;
            var right = 1d;

            if (page.Lines != null && page.Lines.Count > 0)
            {
                var anchorLine = ResolveAnchorLine(candidate, pageIndex, pageByIndex);
                if (anchorLine != null)
                {
                    var topBoundary = pageIndex == candidate.StartPage
                        ? ClampRatio(anchorLine.TopRatio - QuestionImageCropPaddingRatio)
                        : 0d;
                    var nextHeaderTop = ResolveNextHeaderTopRatio(
                        candidate,
                        allCandidates,
                        pageIndex,
                        pageByIndex,
                        anchorLine);
                    var segmentLines = page.Lines
                        .Where(x =>
                            !string.IsNullOrWhiteSpace(x.Text) &&
                            IsSameColumnLine(x, anchorLine) &&
                            x.BottomRatio >= topBoundary &&
                            (!nextHeaderTop.HasValue || x.TopRatio < nextHeaderTop.Value))
                        .ToArray();

                    if (segmentLines.Length > 0)
                    {
                        var horizontalCropLines = segmentLines
                            .Where(x => IsHorizontalCropLine(x, anchorLine))
                            .ToArray();
                        var cropLines = horizontalCropLines.Length > 0
                            ? horizontalCropLines
                            : segmentLines;

                        left = ClampRatio(cropLines.Min(x => x.LeftRatio) - QuestionImageCropPaddingRatio);
                        top = ClampRatio(segmentLines.Min(x => x.TopRatio) - QuestionImageCropPaddingRatio);
                        right = ClampRatio(cropLines.Max(x => x.RightRatio) + QuestionImageCropPaddingRatio);
                        bottom = ClampRatio(segmentLines.Max(x => x.BottomRatio) + QuestionImageCropPaddingRatio);
                    }
                    else if (pageIndex != candidate.StartPage)
                    {
                        continue;
                    }
                }
                else
                {
                    var segmentStartLine = pageIndex == candidate.StartPage ? candidate.StartLineInPage : 1;
                    var segmentEndLine = pageIndex == candidate.EndPage
                        ? candidate.EndLineInPage
                        : page.Lines.Max(x => x.LineInPage);
                    var segmentLines = page.Lines
                        .Where(x => x.LineInPage >= segmentStartLine && x.LineInPage <= segmentEndLine)
                        .ToArray();

                    if (segmentLines.Length > 0)
                    {
                        left = ClampRatio(segmentLines.Min(x => x.LeftRatio) - QuestionImageCropPaddingRatio);
                        top = ClampRatio(segmentLines.Min(x => x.TopRatio) - QuestionImageCropPaddingRatio);
                        right = ClampRatio(segmentLines.Max(x => x.RightRatio) + QuestionImageCropPaddingRatio);
                        bottom = ClampRatio(segmentLines.Max(x => x.BottomRatio) + QuestionImageCropPaddingRatio);
                    }
                }
            }

            right = EnsureMinimumSpan(left, right, MinQuestionImageSliceWidthRatio);
            bottom = EnsureMinimumSpan(top, bottom, MinQuestionImageSliceRatio);

            segments.Add(CreateQuestionImageSegment(page, left, top, right, bottom));
        }

        return sharedSegments
            .Concat(segments)
            .GroupBy(x => $"{x.PageIndex}:{x.ImageLeftRatio:F4}:{x.ImageTopRatio:F4}:{x.ImageRightRatio:F4}:{x.ImageBottomRatio:F4}")
            .Select(x => x.First())
            .ToArray();
    }

    private static QuestionImageSegment? TryCreateQuestionImageSegment(
        OcrQuestionImageRegion region,
        IReadOnlyDictionary<int, OcrPageResult> pageByIndex)
    {
        if (!pageByIndex.TryGetValue(region.PageIndex, out var page) ||
            string.IsNullOrWhiteSpace(page.ImagePath) ||
            !double.IsFinite(region.LeftRatio) ||
            !double.IsFinite(region.TopRatio) ||
            !double.IsFinite(region.RightRatio) ||
            !double.IsFinite(region.BottomRatio))
        {
            return null;
        }

        var left = ClampRatio(region.LeftRatio);
        var top = ClampRatio(region.TopRatio);
        var right = ClampRatio(region.RightRatio);
        var bottom = ClampRatio(region.BottomRatio);
        if (right <= left || bottom <= top)
        {
            return null;
        }

        return CreateQuestionImageSegment(page, left, top, right, bottom);
    }

    private static QuestionImageSegment[] BuildSharedContextImageSegments(
        OcrQuestionCandidate candidate,
        IReadOnlyList<OcrQuestionCandidate> allCandidates,
        IReadOnlyDictionary<int, OcrPageResult> pageByIndex)
    {
        if (!pageByIndex.TryGetValue(candidate.StartPage, out var page) ||
            page.Lines == null ||
            page.Lines.Count == 0 ||
            !TryGetLine(pageByIndex, candidate.StartPage, candidate.StartLineInPage, out var anchorLine) ||
            anchorLine == null)
        {
            return Array.Empty<QuestionImageSegment>();
        }

        var sharedContext = ResolveSharedContextDefinition(candidate, allCandidates, page.Lines, anchorLine);
        if (sharedContext == null)
        {
            return Array.Empty<QuestionImageSegment>();
        }

        var contextLines = page.Lines
            .Where(x =>
                x.LineInPage >= sharedContext.Value.ContextStartLine &&
                x.LineInPage < sharedContext.Value.QuestionStartLine &&
                !string.IsNullOrWhiteSpace(x.Text) &&
                IsSameColumnLine(x, anchorLine))
            .OrderBy(x => x.LineInPage)
            .ToArray();
        if (contextLines.Length == 0)
        {
            return Array.Empty<QuestionImageSegment>();
        }

        var sharedSegment = BuildImageSegmentFromLines(page, contextLines, contextLines[0]);
        return sharedSegment == null
            ? Array.Empty<QuestionImageSegment>()
            : new[] { sharedSegment };
    }

    private static SharedContextDefinition? ResolveSharedContextDefinition(
        OcrQuestionCandidate candidate,
        IReadOnlyList<OcrQuestionCandidate> allCandidates,
        IReadOnlyList<OcrLineResult> pageLines,
        OcrLineResult anchorLine)
    {
        var samePageCandidates = allCandidates
            .Where(x => x.StartPage == candidate.StartPage && x.Index > 0)
            .OrderBy(x => x.StartLineInPage)
            .ToArray();
        if (samePageCandidates.Length == 0)
        {
            return null;
        }

        for (var lineIndex = candidate.StartLineInPage - 1; lineIndex >= 1; lineIndex--)
        {
            var markerLine = pageLines.FirstOrDefault(x => x.LineInPage == lineIndex);
            if (markerLine == null ||
                string.IsNullOrWhiteSpace(markerLine.Text) ||
                !IsSameColumnLine(markerLine, anchorLine))
            {
                continue;
            }

            if (!TryResolveSharedContextRange(markerLine.Text, candidate.Index, out var sharedRange))
            {
                continue;
            }

            var firstCandidateInRange = samePageCandidates
                .Where(x => sharedRange.Contains(x.Index))
                .OrderBy(x => x.StartLineInPage)
                .FirstOrDefault();
            if (firstCandidateInRange == null ||
                firstCandidateInRange.StartLineInPage <= markerLine.LineInPage)
            {
                continue;
            }

            return new SharedContextDefinition(
                sharedRange,
                markerLine.LineInPage,
                firstCandidateInRange.StartLineInPage);
        }

        foreach (var priorCandidate in samePageCandidates
                     .Where(x => x.StartLineInPage < candidate.StartLineInPage && x.Index < candidate.Index)
                     .OrderByDescending(x => x.StartLineInPage))
        {
            var priorHeaderLine = pageLines.FirstOrDefault(x => x.LineInPage == priorCandidate.StartLineInPage);
            if (priorHeaderLine == null ||
                string.IsNullOrWhiteSpace(priorHeaderLine.Text) ||
                !IsSameColumnLine(priorHeaderLine, anchorLine))
            {
                continue;
            }

            if (!TryResolveSharedContextRange(
                    priorHeaderLine.Text,
                    candidate.Index,
                    priorCandidate.Index,
                    out var sharedRange))
            {
                continue;
            }

            if (!sharedRange.Contains(candidate.Index))
            {
                continue;
            }

            return new SharedContextDefinition(
                sharedRange,
                priorCandidate.StartLineInPage,
                candidate.StartLineInPage);
        }

        return null;
    }

    private static bool TryResolveSharedContextRange(
        string? text,
        int currentIndex,
        out QuestionNumberRange questionRange)
    {
        return TryResolveSharedContextRange(text, currentIndex, fallbackStartIndex: null, out questionRange);
    }

    private static bool TryResolveSharedContextRange(
        string? text,
        int currentIndex,
        int? fallbackStartIndex,
        out QuestionNumberRange questionRange)
    {
        questionRange = default;
        if (!LooksLikeSharedContextMarkerText(text))
        {
            return false;
        }

        var normalized = text!.Trim();
        var explicitMatch = QuestionRangeHintRegex.Match(normalized);
        if (explicitMatch.Success &&
            int.TryParse(explicitMatch.Groups["start"].Value, out var explicitStart) &&
            int.TryParse(explicitMatch.Groups["end"].Value, out var explicitEnd) &&
            explicitStart > 0 &&
            explicitEnd >= explicitStart)
        {
            questionRange = new QuestionNumberRange(explicitStart, explicitEnd);
            return questionRange.Contains(currentIndex);
        }

        var startOnlyMatch = SharedContextRangeStartRegex.Match(normalized);
        if (startOnlyMatch.Success &&
            int.TryParse(startOnlyMatch.Groups["start"].Value, out var inferredStart) &&
            inferredStart > 0)
        {
            questionRange = new QuestionNumberRange(
                inferredStart,
                inferredStart + MalformedSharedContextFallbackQuestionCount - 1);
            if (questionRange.Contains(currentIndex))
            {
                return true;
            }
        }

        if (!fallbackStartIndex.HasValue || fallbackStartIndex.Value <= 0)
        {
            return false;
        }

        var endOnlyMatch = SharedContextRangeEndRegex.Match(normalized);
        if (!endOnlyMatch.Success ||
            !int.TryParse(endOnlyMatch.Groups["end"].Value, out var inferredEnd) ||
            inferredEnd < fallbackStartIndex.Value)
        {
            return false;
        }

        questionRange = new QuestionNumberRange(fallbackStartIndex.Value, inferredEnd);
        return questionRange.Contains(currentIndex);
    }

    private static bool LooksLikeSharedContextMarkerText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var normalized = text.Trim();
        if (!normalized.Contains("다음", StringComparison.Ordinal) &&
            !normalized.Contains("답하라", StringComparison.Ordinal) &&
            !normalized.Contains("보고", StringComparison.Ordinal))
        {
            return false;
        }

        return normalized.IndexOfAny(QuestionRangeSeparators) >= 0;
    }

    private static QuestionImageSegment? BuildImageSegmentFromLines(
        OcrPageResult page,
        IReadOnlyList<OcrLineResult> segmentLines,
        OcrLineResult anchorLine)
    {
        if (string.IsNullOrWhiteSpace(page.ImagePath) || segmentLines.Count == 0)
        {
            return null;
        }

        var horizontalCropLines = segmentLines
            .Where(x => IsHorizontalCropLine(x, anchorLine))
            .ToArray();
        var cropLines = horizontalCropLines.Length > 0
            ? horizontalCropLines
            : segmentLines.ToArray();

        var left = ClampRatio(cropLines.Min(x => x.LeftRatio) - QuestionImageCropPaddingRatio);
        var top = ClampRatio(segmentLines.Min(x => x.TopRatio) - QuestionImageCropPaddingRatio);
        var right = EnsureMinimumSpan(
            left,
            ClampRatio(cropLines.Max(x => x.RightRatio) + QuestionImageCropPaddingRatio),
            MinQuestionImageSliceWidthRatio);
        var bottom = EnsureMinimumSpan(
            top,
            ClampRatio(segmentLines.Max(x => x.BottomRatio) + QuestionImageCropPaddingRatio),
            MinQuestionImageSliceRatio);

        return CreateQuestionImageSegment(page, left, top, right, bottom);
    }

    private static QuestionImageSegment CreateQuestionImageSegment(
        OcrPageResult page,
        double left,
        double top,
        double right,
        double bottom)
    {
        return new QuestionImageSegment
        {
            PageIndex = page.PageIndex,
            ImagePath = page.ImagePath,
            ImageLeftRatio = left,
            ImageTopRatio = top,
            ImageRightRatio = right,
            ImageBottomRatio = bottom,
            ImagePixelWidth = page.ImagePixelWidth,
            ImagePixelHeight = page.ImagePixelHeight
        };
    }

    private static OcrLineResult? ResolveAnchorLine(
        OcrQuestionCandidate candidate,
        int pageIndex,
        IReadOnlyDictionary<int, OcrPageResult> pageByIndex)
    {
        if (candidate.StartPage == pageIndex &&
            TryGetLine(pageByIndex, pageIndex, candidate.StartLineInPage, out var startLine))
        {
            return startLine;
        }

        if (candidate.StartPage != pageIndex &&
            TryGetLine(pageByIndex, candidate.StartPage, candidate.StartLineInPage, out var originalAnchor))
        {
            return originalAnchor;
        }

        return null;
    }

    private static bool TryGetLine(
        IReadOnlyDictionary<int, OcrPageResult> pageByIndex,
        int pageIndex,
        int lineInPage,
        out OcrLineResult? line)
    {
        line = null;
        if (!pageByIndex.TryGetValue(pageIndex, out var page) || page.Lines == null)
        {
            return false;
        }

        line = page.Lines.FirstOrDefault(x => x.LineInPage == lineInPage);
        return line != null;
    }

    private static double? ResolveNextHeaderTopRatio(
        OcrQuestionCandidate currentCandidate,
        IReadOnlyList<OcrQuestionCandidate> allCandidates,
        int pageIndex,
        IReadOnlyDictionary<int, OcrPageResult> pageByIndex,
        OcrLineResult anchorLine)
    {
        foreach (var nextCandidate in allCandidates
                     .Where(x =>
                         x.Index != currentCandidate.Index &&
                         x.StartPage == pageIndex &&
                         (pageIndex != currentCandidate.StartPage || x.StartLineInPage > currentCandidate.StartLineInPage))
                     .OrderBy(x => x.StartLineInPage))
        {
            if (!TryGetLine(pageByIndex, pageIndex, nextCandidate.StartLineInPage, out var nextHeader) || nextHeader == null)
            {
                continue;
            }

            if (IsSameColumnLine(nextHeader, anchorLine))
            {
                return ClampRatio(nextHeader.TopRatio - QuestionImageCropPaddingRatio);
            }
        }

        return null;
    }

    private static bool IsHorizontalCropLine(OcrLineResult line, OcrLineResult anchorLine)
    {
        var anchorWidth = Math.Max(0d, anchorLine.RightRatio - anchorLine.LeftRatio);
        if (anchorWidth >= 0.55d)
        {
            return true;
        }

        var anchorCenter = (anchorLine.LeftRatio + anchorLine.RightRatio) / 2d;
        var lineCenter = (line.LeftRatio + line.RightRatio) / 2d;
        if (Math.Abs(lineCenter - anchorCenter) <= 0.20d)
        {
            return true;
        }

        var expandedLeft = ClampRatio(anchorLine.LeftRatio - 0.08d);
        var expandedRight = ClampRatio(anchorLine.RightRatio + 0.08d);
        var overlap = Math.Min(line.RightRatio, expandedRight) - Math.Max(line.LeftRatio, expandedLeft);
        return overlap >= 0.12d;
    }

    private static bool IsSameColumnLine(OcrLineResult line, OcrLineResult anchorLine)
    {
        if (IsHorizontalCropLine(line, anchorLine))
        {
            return true;
        }

        var anchorCenter = (anchorLine.LeftRatio + anchorLine.RightRatio) / 2d;
        var expandedLeft = ClampRatio(anchorLine.LeftRatio - 0.08d);

        // 우측 칼럼의 짧은 줄은 중심점이 중앙 쪽으로 치우쳐도 같은 문항인 경우가 있다.
        if (anchorCenter >= 0.55d && line.LeftRatio >= expandedLeft)
        {
            return true;
        }

        return false;
    }

    private static double ClampRatio(double value)
    {
        return Math.Clamp(value, 0d, 1d);
    }

    private static double EnsureMinimumSpan(double start, double end, double minimumSpan)
    {
        var clampedStart = ClampRatio(start);
        var clampedEnd = ClampRatio(end);
        if (clampedEnd - clampedStart >= minimumSpan)
        {
            return clampedEnd;
        }

        return Math.Min(1d, clampedStart + minimumSpan);
    }

    private static double ResolveTopImageRatio(int startLine, int totalLines)
    {
        var safeTotalLines = Math.Max(1, totalLines);
        var clampedStartLine = Math.Clamp(startLine, 1, safeTotalLines);
        return Math.Clamp((double)(clampedStartLine - 1) / safeTotalLines, 0d, 1d);
    }

    private static double ResolveBottomImageRatio(int endLine, int totalLines, double top)
    {
        var safeTotalLines = Math.Max(1, totalLines);
        var minimumLine = Math.Max(1, (int)Math.Ceiling(top * safeTotalLines));
        var clampedEndLine = Math.Clamp(endLine, minimumLine, safeTotalLines);
        var bottom = (double)clampedEndLine / safeTotalLines;
        if (bottom - top < MinQuestionImageSliceRatio)
        {
            bottom = Math.Min(1d, top + MinQuestionImageSliceRatio);
        }

        return Math.Clamp(bottom, 0d, 1d);
    }

    private void ClearPdfAnalysisState()
    {
        pdfAnalysisProcessedPages = 0;
        pdfAnalysisTotalPages = 0;
        PdfAnalysisSummary = string.Empty;
        LatestPdfDiagnosticsText = string.Empty;
        LatestPdfDiagnosticsTitle = string.Empty;
        PdfAnalysisProgressValue = 0d;
        PdfAnalysisStatus = string.Empty;
        PdfAnalysisStatusColor = Color.FromArgb("#334155");
        PdfAnalysisPagesPerSecond = 0d;
        pdfAnalysisStartAt = DateTimeOffset.UtcNow;
        OnPropertyChanged(nameof(ShowPdfAnalysisProgress));
        OnPropertyChanged(nameof(ShowPdfAnalysisPanel));
        OnPropertyChanged(nameof(PdfAnalysisProgressText));
    }

    private void UpdatePdfAnalysisProgress(PdfAnalysisProgress progress)
    {
        pdfAnalysisProcessedPages = Math.Max(0, progress.ProcessedPages);
        pdfAnalysisTotalPages = Math.Max(0, progress.TotalPages);
        OnPropertyChanged(nameof(PdfAnalysisProgressText));

        PdfAnalysisStatus = progress.Message;
        if (IsPdfAnalysisInProgress)
        {
            PdfAnalysisStatusColor = Color.FromArgb("#0EA5E9");
        }

        if (pdfAnalysisStartAt != default && pdfAnalysisProcessedPages > 0)
        {
            var elapsed = Math.Max(0.000_001, (DateTimeOffset.UtcNow - pdfAnalysisStartAt).TotalSeconds);
            PdfAnalysisPagesPerSecond = pdfAnalysisProcessedPages / elapsed;
        }
        else
        {
            PdfAnalysisPagesPerSecond = 0d;
        }

        var progressValue = progress.TotalPages <= 0
            ? 0d
            : Math.Min(1d, (double)progress.ProcessedPages / progress.TotalPages);
        PdfAnalysisProgressValue = Math.Max(0d, progressValue);
    }

    private async Task LoadAnswerFileAsync()
    {
        if (!TryBeginStructuralOperation("다른 문항 구조 변경 작업이 진행 중입니다."))
        {
            return;
        }

        try
        {
            var options = new PickOptions
            {
                PickerTitle = "정답 파일 선택",
                FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                {
                    { DevicePlatform.WinUI, new[] { ".txt", ".csv", ".md", ".text" } },
                    { DevicePlatform.MacCatalyst, new[] { "public.plain-text" } },
                    { DevicePlatform.iOS, new[] { "public.text" } },
                    { DevicePlatform.Android, new[] { "text/plain" } },
                })
            };

            var file = await FilePicker.PickAsync(options);
            if (file == null)
            {
                Feedback = "정답 파일 선택이 취소되었습니다.";
                return;
            }

            using var stream = await file.OpenReadAsync();
            using var reader = new StreamReader(stream);
            var content = await reader.ReadToEndAsync();
            AnswerMapText = content;

            Feedback = $"정답 파일을 불러왔습니다: {file.FileName}";
        }
        catch (Exception ex)
        {
            Feedback = $"정답 파일을 불러오지 못했습니다: {ex.Message}";
        }
        finally
        {
            EndStructuralOperation();
        }
    }

    private Task LoadSourceFilesFromDirectoryAsync()
    {
        try
        {
            Directory.CreateDirectory(sourceFileDirectory);
            var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var files = EnumerateSourcePdfFiles()
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name) && seenFiles.Add(name))
                .Select(name => name!)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();

            SourceFilesDirectory.Clear();
            foreach (var file in files)
            {
                SourceFilesDirectory.Add(file);
            }

            if (!SourceFilesDirectory.Any())
            {
                SelectedSourceFileName = string.Empty;
            }
            else if (string.IsNullOrWhiteSpace(SelectedSourceFileName) ||
                     !SourceFilesDirectory.Contains(SelectedSourceFileName, StringComparer.OrdinalIgnoreCase))
            {
                SelectedSourceFileName = SourceFilesDirectory.FirstOrDefault() ?? string.Empty;
            }
        }
        catch (Exception ex)
        {
            Feedback = $"문항 파일 목록을 읽지 못했습니다: {ex.Message}";
        }

        OnPropertyChanged(nameof(CanImportAnswerMap));
        return Task.CompletedTask;
    }

    private IEnumerable<string> EnumerateSourcePdfFiles()
    {
        foreach (var directory in GetSourceFileSearchDirectories())
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                continue;
            }

            string[] files;
            try
            {
                files = Directory
                    .EnumerateFiles(directory, "*.pdf", SearchOption.TopDirectoryOnly)
                    .ToArray();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                AppLog.Error(
                    nameof(MainViewModel),
                    $"문항 파일 검색 폴더 건너뜀 | directory={directory} | reason={ex.Message}",
                    ex);
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }
        }
    }

    private async Task ImportSourceFileAsync()
    {
        if (!CanModifyQuestionStructure)
        {
            Feedback = IsSourceFileImportInProgress
                ? "이미 문항 PDF 등록이 진행 중입니다."
                : IsPracticeRunning
                    ? "연습 중에는 문항 PDF를 추가하거나 덮어쓸 수 없습니다."
                    : "문항 분석 또는 다른 구조 변경 중에는 PDF를 추가하거나 덮어쓸 수 없습니다.";
            return;
        }

        IsSourceFileImportInProgress = true;
        try
        {
            var options = new PickOptions
            {
                PickerTitle = "문항 PDF 선택",
                FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                {
                    { DevicePlatform.WinUI, new[] { ".pdf" } },
                    { DevicePlatform.MacCatalyst, new[] { "public.pdf", "com.adobe.pdf" } },
                    { DevicePlatform.iOS, new[] { "public.pdf", "com.adobe.pdf" } },
                    { DevicePlatform.Android, new[] { "application/pdf", "application/octet-stream", ".pdf" } },
                })
            };

            var file = await FilePicker.PickAsync(options);
            if (file == null)
            {
                Feedback = "문항 파일 선택이 취소되었습니다.";
                return;
            }

            if (!CanContinueSourceFileImport())
            {
                Feedback = "상태가 변경되어 문항 PDF 추가를 중단했습니다.";
                return;
            }

            Directory.CreateDirectory(sourceFileDirectory);
            var rawSourceFileName = file.FileName?.Trim();
            var sourceFileName = string.IsNullOrWhiteSpace(rawSourceFileName)
                ? string.Empty
                : Path.GetFileName(rawSourceFileName);
            if (string.IsNullOrWhiteSpace(sourceFileName))
            {
                Feedback = "문항 파일 이름을 가져올 수 없습니다.";
                return;
            }

            if (!string.Equals(Path.GetExtension(sourceFileName), ".pdf", StringComparison.OrdinalIgnoreCase))
            {
                Feedback = "PDF 파일만 등록할 수 있습니다.";
                return;
            }

            var destinationPath = Path.Combine(sourceFileDirectory, sourceFileName);
            if (File.Exists(destinationPath))
            {
                var overwrite = await ConfirmOverwriteSourceFileAsync(sourceFileName);
                if (!overwrite)
                {
                    Feedback = "문항 파일 등록을 취소했거나 확인창을 표시할 수 없어 중단했습니다.";
                    return;
                }
            }

            if (!CanContinueSourceFileImport())
            {
                Feedback = "상태가 변경되어 문항 PDF 추가를 중단했습니다.";
                return;
            }

            Feedback = $"문항 PDF를 안전하게 등록하고 있습니다: {sourceFileName}";
            await CopyPdfFileAtomicallyAsync(file, destinationPath);

            await LoadSourceFilesFromDirectoryAsync();
            SelectedSourceFileName = sourceFileName;
            Feedback = $"문항 파일을 등록했습니다: {sourceFileName}";
        }
        catch (Exception ex)
        {
            Feedback = $"문항 파일 등록을 실패했습니다: {ex.Message}";
            AppLog.Error(nameof(MainViewModel), "문항 PDF 등록 실패", ex);
        }
        finally
        {
            IsSourceFileImportInProgress = false;
        }
    }

    private bool CanContinueSourceFileImport()
    {
        return IsSourceFileImportInProgress &&
               !IsPracticeRunning &&
               !IsPracticeStartInProgress &&
               !IsPdfAnalysisInProgress;
    }

    private static async Task CopyPdfFileAtomicallyAsync(FileResult sourceFile, string destinationPath)
    {
        var destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrWhiteSpace(destinationDirectory))
        {
            throw new InvalidOperationException("PDF 저장 폴더를 확인할 수 없습니다.");
        }

        Directory.CreateDirectory(destinationDirectory);
        var stagedFilePath = Path.Combine(
            destinationDirectory,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var sourceStream = await sourceFile.OpenReadAsync())
            await using (var stagedStream = new FileStream(
                             stagedFilePath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 81920,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await sourceStream.CopyToAsync(stagedStream);
                await stagedStream.FlushAsync();
                stagedStream.Flush(flushToDisk: true);
            }

            ValidatePdfFile(stagedFilePath);
            PromoteStagedFile(stagedFilePath, destinationPath);
        }
        finally
        {
            TryDeleteFile(stagedFilePath);
        }
    }

    private static void ValidatePdfFile(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> header = stackalloc byte[5];
        if (stream.Length < header.Length ||
            stream.Read(header) != header.Length ||
            !header.SequenceEqual("%PDF-"u8))
        {
            throw new InvalidDataException("선택한 파일에서 유효한 PDF 헤더를 찾지 못했습니다.");
        }
    }

    private static void PromoteStagedFile(string stagedFilePath, string destinationPath)
    {
        if (!File.Exists(destinationPath))
        {
            File.Move(stagedFilePath, destinationPath);
            return;
        }

        var backupFilePath = destinationPath + $".replace-{Guid.NewGuid():N}.bak";
        var promoted = false;
        try
        {
            File.Replace(stagedFilePath, destinationPath, backupFilePath, ignoreMetadataErrors: true);
            promoted = true;
        }
        catch (NotSupportedException)
        {
            File.Move(stagedFilePath, destinationPath, overwrite: true);
            promoted = true;
        }
        finally
        {
            if (promoted)
            {
                TryDeleteFile(backupFilePath);
            }
        }
    }

    private static void TryDeleteFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error(
                nameof(MainViewModel),
                $"임시 파일 정리 실패 | path={filePath}",
                ex);
        }
    }

    private async Task SavePdfAnalysisAsync(
        string sourceFileName,
        PdfOcrResult analysis,
        bool saveAsLatestAttempt = false)
    {
        try
        {
            Directory.CreateDirectory(sourceAnalysisDirectory);

            var safeFileName = GetSafeFileNameWithoutExtension(sourceFileName);
            var fileQualifier = saveAsLatestAttempt ? ".latest-attempt" : ".committed";
            var outputPath = Path.Combine(
                sourceAnalysisDirectory,
                $"{safeFileName}{fileQualifier}.analysis.json");

            await using var outputStream = new FileStream(
                outputPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous);
            await JsonSerializer.SerializeAsync(outputStream, analysis, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            await outputStream.FlushAsync();
            AppLog.Info(
                nameof(MainViewModel),
                $"분석 JSON 저장 완료 | file={sourceFileName} | path={outputPath}");
        }
        catch (Exception ex)
        {
            // 분석 저장은 선택 동작입니다. 저장 실패는 현재 임시 미리보기에 영향 없음.
            AppLog.Error(
                nameof(MainViewModel),
                $"분석 JSON 저장 실패 | file={sourceFileName}",
                ex);
        }
    }

    private async Task<string?> SavePdfAnalysisDiagnosticsAsync(
        string sourceFileName,
        PdfAnalysisDiagnostics diagnostics,
        bool saveAsLatestAttempt = false)
    {
        try
        {
            Directory.CreateDirectory(sourceAnalysisDirectory);

            var safeFileName = GetSafeFileNameWithoutExtension(sourceFileName);
            var fileQualifier = saveAsLatestAttempt ? ".latest-attempt" : ".committed";
            var jsonPath = Path.Combine(
                sourceAnalysisDirectory,
                $"{safeFileName}{fileQualifier}.diagnostics.json");
            var textPath = Path.Combine(
                sourceAnalysisDirectory,
                $"{safeFileName}{fileQualifier}.diagnostics.txt");

            await using (var jsonStream = new FileStream(
                             jsonPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 81920,
                             FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(jsonStream, diagnostics, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                await jsonStream.FlushAsync();
            }
            await File.WriteAllTextAsync(textPath, BuildDiagnosticsText(diagnostics));
            AppLog.Info(
                nameof(MainViewModel),
                $"분석 진단 저장 완료 | file={sourceFileName} | json={jsonPath} | text={textPath}");
            return jsonPath;
        }
        catch (Exception ex)
        {
            AppLog.Error(
                nameof(MainViewModel),
                $"분석 진단 저장 실패 | file={sourceFileName}",
                ex);
            return null;
        }
    }

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

    private static HashSet<int> GetExplicitEmptyAnswerIndexes(string? answerMapText)
    {
        var latestExplicitStates = new Dictionary<int, bool>();
        if (string.IsNullOrWhiteSpace(answerMapText))
        {
            return new HashSet<int>();
        }

        var pairs = answerMapText
            .Replace("\r", string.Empty)
            .Split(new[] { ',', '\n', ';' }, StringSplitOptions.TrimEntries);
        if (!pairs.Any(x => x.Contains(':', StringComparison.Ordinal)))
        {
            return new HashSet<int>();
        }

        foreach (var pair in pairs.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            var split = pair.Split(':', 2);
            if (split.Length != 2 ||
                !int.TryParse(split[0].Trim(), out var index) ||
                index <= 0)
            {
                continue;
            }

            var hasAnswer = split[1]
                .Split('|', StringSplitOptions.None)
                .Any(x => !string.IsNullOrWhiteSpace(x));
            latestExplicitStates[index] = !hasAnswer;
        }

        return latestExplicitStates
            .Where(x => x.Value)
            .Select(x => x.Key)
            .ToHashSet();
    }

    private static bool IsAllowedExplicitEmptyAnswerError(
        string error,
        IReadOnlySet<int> explicitEmptyAnswerIndexes)
    {
        const string prefix = "정답 없음:";
        if (string.IsNullOrWhiteSpace(error) ||
            !error.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        return int.TryParse(error[prefix.Length..].Trim(), out var index) &&
               explicitEmptyAnswerIndexes.Contains(index);
    }


    private static bool TryParseExpectedQuestionInput(
        string? rawValue,
        out ExpectedQuestionInput expectedQuestionInput,
        out string errorMessage)
    {
        expectedQuestionInput = ExpectedQuestionInput.Empty;
        errorMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return true;
        }

        var normalized = rawValue.Trim();
        var separatorIndex = normalized.IndexOfAny(QuestionRangeSeparators);

        if (separatorIndex < 0)
        {
            if (!int.TryParse(normalized, out var count) || count <= 0)
            {
                errorMessage = "예상 문항 입력은 `25` 또는 `31-55` 형식으로 입력해 주세요.";
                return false;
            }

            if (count > MaximumExpectedQuestionCount)
            {
                errorMessage = $"예상 문항 수는 최대 {MaximumExpectedQuestionCount}개까지 입력할 수 있습니다.";
                return false;
            }

            expectedQuestionInput = ExpectedQuestionInput.FromCount(normalized, count);
            return true;
        }

        var startText = normalized[..separatorIndex].Trim();
        var endText = normalized[(separatorIndex + 1)..].Trim();

        if (!int.TryParse(startText, out var startIndex) ||
            !int.TryParse(endText, out var endIndex) ||
            startIndex <= 0 ||
            endIndex <= 0)
        {
            errorMessage = "예상 문항 입력은 `25` 또는 `31-55` 형식으로 입력해 주세요.";
            return false;
        }

        if (startIndex > MaximumExpectedQuestionNumber || endIndex > MaximumExpectedQuestionNumber)
        {
            errorMessage = $"예상 문항 번호는 최대 {MaximumExpectedQuestionNumber}번까지 입력할 수 있습니다.";
            return false;
        }

        if (startIndex > endIndex)
        {
            errorMessage = "예상 문항 범위의 시작 번호는 끝 번호보다 클 수 없습니다.";
            return false;
        }

        var expectedCount = endIndex - startIndex + 1;
        if (expectedCount > MaximumExpectedQuestionCount)
        {
            errorMessage = $"예상 문항 범위는 최대 {MaximumExpectedQuestionCount}개까지 입력할 수 있습니다.";
            return false;
        }

        expectedQuestionInput = ExpectedQuestionInput.FromRange(
            normalized,
            new QuestionNumberRange(startIndex, endIndex));
        return true;
    }

    private static ExpectedQuestionRangeResolution ResolveExpectedQuestionRange(
        PdfOcrResult analysis,
        ExpectedQuestionInput expectedQuestionInput)
    {
        if (!expectedQuestionInput.HasValue)
        {
            return ExpectedQuestionRangeResolution.None;
        }

        if (expectedQuestionInput.ExplicitRange is QuestionNumberRange explicitRange)
        {
            return new ExpectedQuestionRangeResolution(
                explicitRange,
                expectedQuestionInput.ExpectedCount,
                IsAutoInferred: false,
                "직접 입력한 범위를 사용했습니다.");
        }

        var expectedCount = expectedQuestionInput.ExpectedCount;
        if (expectedCount <= 0)
        {
            return new ExpectedQuestionRangeResolution(
                null,
                expectedCount,
                IsAutoInferred: true,
                "입력한 문항 수가 올바르지 않습니다.");
        }

        if (TryFindExpectedQuestionRangeHint(analysis.Pages, expectedCount, out var hintedRange, out var hintedReason))
        {
            return new ExpectedQuestionRangeResolution(
                hintedRange,
                expectedCount,
                IsAutoInferred: true,
                hintedReason);
        }

        if (TryInferExpectedQuestionRangeFromCandidates(
                analysis.QuestionCandidates,
                expectedCount,
                out var inferredRange,
                out var inferredReason))
        {
            return new ExpectedQuestionRangeResolution(
                inferredRange,
                expectedCount,
                IsAutoInferred: true,
                inferredReason);
        }

        return new ExpectedQuestionRangeResolution(
            null,
            expectedCount,
            IsAutoInferred: true,
            $"입력한 문항 수 {expectedCount}개에 맞는 시작 번호를 OCR 결과에서 찾지 못했습니다.");
    }

    private static bool TryFindExpectedQuestionRangeHint(
        IReadOnlyList<OcrPageResult> pages,
        int expectedCount,
        out QuestionNumberRange expectedQuestionRange,
        out string reason)
    {
        expectedQuestionRange = default;
        reason = string.Empty;

        if (expectedCount <= 0)
        {
            return false;
        }

        foreach (var line in EnumerateQuestionRangeHintLines(pages))
        {
            foreach (Match match in QuestionRangeHintRegex.Matches(line.Text))
            {
                if (!int.TryParse(match.Groups["start"].Value, out var startIndex) ||
                    !int.TryParse(match.Groups["end"].Value, out var endIndex) ||
                    startIndex <= 0 ||
                    endIndex < startIndex)
                {
                    continue;
                }

                var range = new QuestionNumberRange(startIndex, endIndex);
                if (range.Count != expectedCount)
                {
                    continue;
                }

                expectedQuestionRange = range;
                reason = $"페이지 {line.PageIndex} 줄 {line.LineInPage} 범위 표기 `{match.Value.Trim()}`를 사용했습니다.";
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<PageLineText> EnumerateQuestionRangeHintLines(IReadOnlyList<OcrPageResult> pages)
    {
        foreach (var page in pages.OrderBy(x => x.PageIndex))
        {
            if (page.Lines != null && page.Lines.Count > 0)
            {
                foreach (var line in page.Lines
                             .OrderBy(x => x.LineInPage)
                             .Take(12)
                             .Where(x => !string.IsNullOrWhiteSpace(x.Text)))
                {
                    yield return new PageLineText(page.PageIndex, line.LineInPage, line.Text.Trim());
                }

                continue;
            }

            var textLines = page.Text
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Take(12)
                .ToArray();

            for (var index = 0; index < textLines.Length; index++)
            {
                yield return new PageLineText(page.PageIndex, index + 1, textLines[index]);
            }
        }
    }

    private static bool TryInferExpectedQuestionRangeFromCandidates(
        IReadOnlyList<OcrQuestionCandidate> questionCandidates,
        int expectedCount,
        out QuestionNumberRange expectedQuestionRange,
        out string reason)
    {
        expectedQuestionRange = default;
        reason = string.Empty;

        if (expectedCount <= 0)
        {
            return false;
        }

        var candidateIndexes = questionCandidates
            .Where(x => x.Index > 0)
            .Select(x => x.Index)
            .Distinct()
            .OrderBy(x => x)
            .ToArray();

        if (candidateIndexes.Length == 0)
        {
            return false;
        }

        var possibleStarts = new HashSet<int>();
        foreach (var candidateIndex in candidateIndexes)
        {
            for (var offset = 0; offset < expectedCount; offset++)
            {
                var startIndex = candidateIndex - offset;
                if (startIndex > 0)
                {
                    possibleStarts.Add(startIndex);
                }
            }
        }

        QuestionNumberRange? bestRange = null;
        int[] bestHits = Array.Empty<int>();
        var bestScore = int.MinValue;
        var bestEarliestOffset = int.MaxValue;
        var bestSpan = int.MaxValue;

        foreach (var startIndex in possibleStarts.OrderBy(x => x))
        {
            var currentRange = new QuestionNumberRange(startIndex, startIndex + expectedCount - 1);
            var hits = candidateIndexes
                .Where(currentRange.Contains)
                .ToArray();

            if (hits.Length == 0)
            {
                continue;
            }

            var earliestOffset = hits.Min() - currentRange.StartIndex;
            var latestOffset = hits.Max() - currentRange.StartIndex;
            var span = latestOffset - earliestOffset;
            var score = (hits.Length * 1000) - (earliestOffset * 10) - span - currentRange.StartIndex;

            if (score > bestScore ||
                (score == bestScore && earliestOffset < bestEarliestOffset) ||
                (score == bestScore && earliestOffset == bestEarliestOffset && span < bestSpan) ||
                (score == bestScore && earliestOffset == bestEarliestOffset && span == bestSpan && startIndex < (bestRange?.StartIndex ?? int.MaxValue)))
            {
                bestRange = currentRange;
                bestHits = hits;
                bestScore = score;
                bestEarliestOffset = earliestOffset;
                bestSpan = span;
            }
        }

        if (bestRange == null)
        {
            return false;
        }

        var minimumRequiredHits = Math.Min(2, expectedCount);
        if (bestHits.Length < minimumRequiredHits)
        {
            reason = $"후보 번호가 {bestHits.Length}개만 겹쳐 시작 번호를 판단하기 어렵습니다.";
            return false;
        }

        expectedQuestionRange = bestRange.Value;
        var hitPreview = string.Join(", ", bestHits.Take(10));
        if (bestHits.Length > 10)
        {
            hitPreview += ", ...";
        }

        reason = $"후보 번호 분포({hitPreview})를 기준으로 {expectedQuestionRange} 범위를 선택했습니다.";
        return true;
    }

    private static PdfOcrResult WithQuestionCandidates(
        PdfOcrResult analysis,
        IReadOnlyList<OcrQuestionCandidate> questionCandidates)
    {
        return new PdfOcrResult
        {
            IsSuccess = analysis.IsSuccess,
            SourceFileName = analysis.SourceFileName,
            Message = analysis.Message,
            AnalyzedAt = analysis.AnalyzedAt,
            Pages = analysis.Pages.ToArray(),
            QuestionCandidates = questionCandidates.ToArray()
        };
    }

    private static async Task<bool> ConfirmOverwriteImportAsync(string sourceFileName, int existingCount)
    {
        var message = $"'{sourceFileName}' 파일의 문항 {existingCount}개가 이미 등록되어 있습니다.\n" +
                      "덮어써서 새로 반영하시겠습니까?";

        return await ConfirmDestructiveActionAsync(
            "문항 반영",
            message,
            "덮어쓰기");
    }

    private static async Task<bool> ConfirmOverwriteSourceFileAsync(string sourceFileName)
    {
        var message = $"'{sourceFileName}' 파일이 이미 존재합니다.\n덮어쓰기 하시겠습니까?";

        return await ConfirmDestructiveActionAsync(
            "문항 파일 등록",
            message,
            "덮어쓰기");
    }

    private static async Task<bool> ConfirmDestructiveActionAsync(
        string title,
        string message,
        string acceptText)
    {
        var page = App.CurrentPage;
        if (page == null)
        {
            AppLog.Error(
                nameof(MainViewModel),
                $"확인창을 표시할 현재 페이지가 없어 작업을 중단했습니다. title={title}");
            return false;
        }

        try
        {
            return await page.DisplayAlert(title, message, acceptText, "취소");
        }
        catch (Exception ex)
        {
            AppLog.Error(
                nameof(MainViewModel),
                $"확인창 표시 실패로 작업을 중단했습니다. title={title}",
                ex);
            return false;
        }
    }

    private async Task StartPracticeAsync()
    {
        if (IsPracticeStartInProgress || IsPracticeRunning)
        {
            return;
        }

        if (IsPdfAnalysisInProgress)
        {
            Feedback = "문항 분석이 끝난 뒤 연습을 시작해 주세요.";
            return;
        }

        if (IsSourceFileImportInProgress)
        {
            Feedback = "문항 PDF 등록이 끝난 뒤 연습을 시작해 주세요.";
            return;
        }

        var targetCategory = SelectedCategory;
        if (targetCategory == null)
        {
            Feedback = "카테고리를 선택해 주세요.";
            return;
        }

        if (!int.TryParse(PracticeCountText, out var count) || count <= 0)
        {
            Feedback = "문항 수는 1 이상이어야 합니다.";
            return;
        }

        if (count > MaxPracticeCount)
        {
            Feedback = $"연습 가능한 문항은 최대 {MaxPracticeCount}개입니다.";
            return;
        }

        var includeUnanswered = IncludeUnansweredInPractice;
        IsPracticeStartInProgress = true;
        try
        {
            var questions = await repository.GetQuestionsAsync(targetCategory.Id);
            var practiceCandidates = (includeUnanswered
                    ? questions
                    : questions.Where(x => x.CorrectAnswers?.Any(a => !string.IsNullOrWhiteSpace(a)) == true))
                .OrderBy(_ => random.Next())
                .Take(count)
                .ToList();

            if (practiceCandidates.Count == 0)
            {
                Feedback = includeUnanswered
                    ? "출제 가능한 문항이 없습니다."
                    : "정답이 등록된 문항이 없습니다.";
                return;
            }

            await StartWithQuestionsAsync(practiceCandidates);
        }
        finally
        {
            IsPracticeStartInProgress = false;
        }
    }

    private async Task StartWrongPracticeAsync()
    {
        if (IsPracticeStartInProgress || IsPracticeRunning)
        {
            return;
        }

        if (IsPdfAnalysisInProgress)
        {
            Feedback = "문항 분석이 끝난 뒤 오답 연습을 시작해 주세요.";
            return;
        }

        if (IsSourceFileImportInProgress)
        {
            Feedback = "문항 PDF 등록이 끝난 뒤 오답 연습을 시작해 주세요.";
            return;
        }

        IsPracticeStartInProgress = true;
        try
        {
            var wrong = await repository.GetWrongQuestionsAsync();
            if (wrong.Count == 0)
            {
                Feedback = "오답 문제가 없습니다.";
                return;
            }

            PracticeCountText = wrong.Count.ToString();
            await StartWithQuestionsAsync(wrong);
        }
        finally
        {
            IsPracticeStartInProgress = false;
        }
    }

    private async Task StartWithQuestionsAsync(IReadOnlyList<Question> questions)
    {
        if (questions.Count == 0)
        {
            Feedback = "출제 가능한 문제가 없습니다.";
            return;
        }

        SetWorkspaceSection(WorkspaceSectionPractice);
        currentSession = questions.OrderBy(_ => random.Next()).ToList();
        SessionTotalCount = currentSession.Count;
        SessionCurrentIndex = 0;
        CorrectCount = 0;
        GradedCount = 0;
        IsPracticeRunning = true;
        IsAnswerRevealed = false;
        IsAnswerSubmissionInProgress = false;
        UserAnswer = string.Empty;
        SessionFeedback = string.Empty;

        Feedback = "연습을 시작합니다.";
        SetCurrentQuestion(currentSession[0]);
        UpdatePracticeState();

        if (!await TryReloadWrongNonFatalAsync("연습 시작 후 오답 목록 갱신"))
        {
            Feedback = "연습을 시작했습니다. 오답 목록 표시는 다음 새로고침 때 갱신됩니다.";
        }
    }

    private async Task StopPracticeAsync()
    {
        if (!CanStopPractice)
        {
            return;
        }

        var confirmed = await ConfirmDestructiveActionAsync(
            "연습 종료",
            "현재 연습을 종료하시겠습니까? 이미 채점한 오답 기록은 유지됩니다.",
            "연습 종료");
        if (!confirmed)
        {
            return;
        }

        if (!CanStopPractice)
        {
            return;
        }

        const string message = "현재 연습을 종료했습니다. 이미 채점한 오답 기록은 유지됩니다.";
        currentSession = Array.Empty<Question>();
        IsPracticeRunning = false;
        IsAnswerRevealed = false;
        CurrentQuestion = null;
        CurrentQuestionImageSlices.Clear();
        QuestionImageNotice = string.Empty;
        ResetQuestionImageView();
        UserAnswer = string.Empty;
        SessionFeedback = message;
        Feedback = message;
        OnPropertyChanged(nameof(CurrentQuestionSourceDisplay));
        OnPropertyChanged(nameof(CurrentQuestionText));
        OnPropertyChanged(nameof(CurrentQuestionChoicesText));
        OnPropertyChanged(nameof(CurrentQuestionSemanticDescription));
        OnPropertyChanged(nameof(HasCurrentQuestionImages));
        OnPropertyChanged(nameof(ShowCurrentQuestionText));
        UpdatePracticeState();
    }

    private void NotifyAnswerInputValidationState()
    {
        OnPropertyChanged(nameof(IsMultipleChoiceAnswerExpected));
        OnPropertyChanged(nameof(UserAnswerValidationMessage));
        OnPropertyChanged(nameof(HasUserAnswerValidationError));
        OnPropertyChanged(nameof(CanSubmitAnswer));
        OnPropertyChanged(nameof(CanEnterAnswer));
        OnPropertyChanged(nameof(CanGoToNextQuestion));
    }

    private static bool TryParsePositiveInteger(string? rawInput, out int answerNumber)
    {
        answerNumber = 0;
        var normalized = rawInput?.Trim() ?? string.Empty;
        return normalized.Length > 0 &&
               normalized.All(x => x is >= '0' and <= '9') &&
               int.TryParse(normalized, out answerNumber) &&
               answerNumber > 0;
    }

    private static bool TryParseMultipleChoiceAnswerInput(
        string? rawInput,
        Question question,
        out int answerNumber)
    {
        if (!TryParsePositiveInteger(rawInput, out answerNumber))
        {
            return false;
        }

        var choiceCount = question.Choices?.Length ?? 0;
        return choiceCount == 0 || answerNumber <= choiceCount;
    }

    private async Task SubmitAnswerAsync()
    {
        if (CurrentQuestion == null ||
            !IsPracticeRunning ||
            IsAnswerRevealed ||
            IsAnswerSubmissionInProgress)
        {
            return;
        }

        var question = CurrentQuestion;
        var hasCorrectAnswer = question.CorrectAnswers?.Any(x => !string.IsNullOrWhiteSpace(x)) == true;
        if (hasCorrectAnswer &&
            question.Type == QuestionType.MultipleChoice &&
            !TryParseMultipleChoiceAnswerInput(UserAnswer, question, out _))
        {
            SessionFeedback = UserAnswerValidationMessage;
            return;
        }

        IsAnswerSubmissionInProgress = true;
        try
        {
            var isCorrect = false;
            if (!hasCorrectAnswer)
            {
                SessionFeedback = "정답이 등록되지 않은 문항입니다. 나중에 정답을 매핑한 뒤 채점 가능합니다.";
            }
            else
            {
                isCorrect = AnswerComparer.IsCorrect(question, UserAnswer);
                if (isCorrect)
                {
                    await repository.RemoveWrongAsync(question.Id);
                }
                else
                {
                    await repository.MarkWrongAsync(question.Id);
                }
            }

            var wrongListRefreshSucceeded = !hasCorrectAnswer ||
                                            await TryReloadWrongNonFatalAsync("채점 후 오답 목록 갱신");

            if (hasCorrectAnswer)
            {
                GradedCount++;
                if (isCorrect)
                {
                    CorrectCount++;
                    SessionFeedback = $"정답: {question.CorrectAnswerDisplay}";
                }
                else
                {
                    SessionFeedback = $"오답: 정답은 {question.CorrectAnswerDisplay} 입니다.";
                }
            }

            if (!wrongListRefreshSucceeded)
            {
                SessionFeedback += "\n오답 상태는 저장되었지만 목록 표시는 다음 새로고침 때 갱신됩니다.";
            }

            IsAnswerRevealed = true;
        }
        finally
        {
            IsAnswerSubmissionInProgress = false;
        }
    }

    private void GoToNextQuestion()
    {
        if (!CanGoToNextQuestion)
        {
            return;
        }

        if (SessionCurrentIndex + 1 >= SessionTotalCount)
        {
            var ungradedCount = Math.Max(0, SessionTotalCount - GradedCount);
            var scoreText = GradedCount == 0
                ? "채점 가능한 문항이 없습니다"
                : $"정답 {CorrectCount}/{GradedCount}개";
            var resultMessage = $"연습 완료: {scoreText}" +
                                (ungradedCount == 0 ? string.Empty : $" · 미채점 {ungradedCount}개");
            IsPracticeRunning = false;
            IsAnswerRevealed = false;
            CurrentQuestion = null;
            CurrentQuestionImageSlices.Clear();
            QuestionImageNotice = string.Empty;
            ResetQuestionImageView();
            OnPropertyChanged(nameof(CurrentQuestionSourceDisplay));
            OnPropertyChanged(nameof(CurrentQuestionText));
            OnPropertyChanged(nameof(CurrentQuestionChoicesText));
            OnPropertyChanged(nameof(HasCurrentQuestionImages));
            OnPropertyChanged(nameof(ShowCurrentQuestionText));
            SessionFeedback = resultMessage;
            Feedback = resultMessage;
            UpdatePracticeState();
            return;
        }

        SessionCurrentIndex++;
        SetCurrentQuestion(currentSession[SessionCurrentIndex]);
        UserAnswer = string.Empty;
        SessionFeedback = string.Empty;
        IsAnswerRevealed = false;
    }

    private void SetCurrentQuestion(Question question)
    {
        ResetQuestionImageView();
        CurrentQuestion = question;
        UpdateCurrentQuestionImageSlices(question);
        OnPropertyChanged(nameof(CurrentQuestionSourceDisplay));
        OnPropertyChanged(nameof(CurrentQuestionText));
        OnPropertyChanged(nameof(CurrentQuestionChoicesText));
        OnPropertyChanged(nameof(CurrentQuestionSemanticDescription));
        OnPropertyChanged(nameof(HasCurrentQuestionImages));
        OnPropertyChanged(nameof(ShowCurrentQuestionText));
    }

    private void UpdateCurrentQuestionImageSlices(Question? question)
    {
        CurrentQuestionImageSlices.Clear();
        var result = QuestionImageSliceBuilder.BuildWithStatus(
            question,
            CurrentQuestionImageViewportWidth,
            CurrentQuestionImageViewportHeight,
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
                $"문제 이미지 {result.StoredSegmentCount}개 중 {result.UnavailableSegmentCount}개를 열 수 없습니다. 파일 검토 화면에서 확인해 주세요."
        };

        OnPropertyChanged(nameof(HasCurrentQuestionImages));
        OnPropertyChanged(nameof(ShowCurrentQuestionText));
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
            WideWorkspaceMinimumWidth,
            WideWorkspaceHorizontalChrome,
            NarrowWorkspaceHorizontalChrome,
            220d,
            920d,
            220d,
            620d), rebuildImages: true);
    }

    private void ApplyWorkspaceMetrics(ResponsiveLayoutMetrics metrics, bool rebuildImages)
    {
        var layoutChanged = useWideWorkspaceLayout != metrics.UseWideLayout;
        var viewportChanged =
            Math.Abs(currentQuestionImageViewportWidth - metrics.ImageViewportWidth) >= 8d ||
            Math.Abs(currentQuestionImageViewportHeight - metrics.ImageViewportHeight) >= 8d;

        useWideWorkspaceLayout = metrics.UseWideLayout;

        if (layoutChanged)
        {
            OnPropertyChanged(nameof(UseWideWorkspaceLayout));
        }

        if (viewportChanged)
        {
            currentQuestionImageViewportWidth = metrics.ImageViewportWidth;
            currentQuestionImageViewportHeight = metrics.ImageViewportHeight;
            OnPropertyChanged(nameof(CurrentQuestionImageViewportWidth));
            OnPropertyChanged(nameof(CurrentQuestionImageViewportHeight));
            if (rebuildImages && CurrentQuestion != null)
            {
                UpdateCurrentQuestionImageSlices(CurrentQuestion);
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

    private async Task<bool> TryReloadWrongNonFatalAsync(string operation)
    {
        try
        {
            await ReloadWrongAsync();
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error(nameof(MainViewModel), operation, ex);
            return false;
        }
    }

    private async Task ReloadWrongAsync()
    {
        var wrong = await repository.GetWrongQuestionsAsync();
        WrongQuestions.Clear();
        foreach (var question in wrong)
        {
            WrongQuestions.Add(question);
        }

        OnPropertyChanged(nameof(CanStartWrongPractice));
        OnPropertyChanged(nameof(WrongQuestionCount));
        UpdatePracticeState();
    }

    private async Task RemoveWrongById(Guid questionId)
    {
        await repository.RemoveWrongAsync(questionId);
        await ReloadWrongAsync();
    }

    private async Task DeleteSourceFileAsync(SourceFileSummary? sourceFile)
    {
        if (sourceFile == null)
        {
            Feedback = "삭제할 파일을 선택해 주세요.";
            return;
        }

        var targetCategory = SelectedCategory;
        if (targetCategory == null)
        {
            Feedback = "카테고리를 선택해 주세요.";
            return;
        }

        if (!CanModifyQuestionStructure)
        {
            Feedback = IsPracticeRunning
                ? "진행 중인 연습이 있어 파일을 삭제할 수 없습니다."
                : "문항 분석 중에는 파일을 삭제할 수 없습니다.";
            return;
        }

        if (!TryBeginStructuralOperation("다른 문항 구조 변경 작업이 진행 중입니다."))
        {
            return;
        }

        try
        {
            var canDelete = await ConfirmDeleteSourceFileAsync(sourceFile.SourceFileName, sourceFile.QuestionCount);
            if (!canDelete)
            {
                Feedback = "등록 문항 삭제를 취소했거나 확인창을 표시할 수 없어 중단했습니다.";
                return;
            }

            var removed = await repository.RemoveQuestionsBySourceFileAsync(targetCategory.Id, sourceFile.SourceFileName);
            if (!removed)
            {
                Feedback = "삭제할 문제를 찾을 수 없습니다.";
                return;
            }

            Feedback = $"'{sourceFile.SourceFileName}'의 등록 문항 {sourceFile.QuestionCount}개를 삭제했습니다. 원본 PDF는 유지됩니다.";
            await UpdateSelectedCategoryQuestionCountAsync();
            await ReloadWrongAsync();
        }
        finally
        {
            EndStructuralOperation();
        }
    }

    private static async Task<bool> ConfirmDeleteSourceFileAsync(string sourceFileName, int questionCount)
    {
        var message = $"'{sourceFileName}'에서 앱에 등록된 문항 {questionCount}개를 삭제하시겠습니까?\n" +
                      "원본 PDF 파일은 삭제하지 않습니다.";

        return await ConfirmDestructiveActionAsync(
            "등록 문항 삭제",
            message,
            "삭제");
    }

    private async Task DeleteCategoryAsync()
    {
        var target = SelectedCategory;
        if (target == null)
        {
            Feedback = "삭제할 카테고리가 없습니다.";
            return;
        }

        if (!CanModifyQuestionStructure)
        {
            Feedback = IsPracticeRunning
                ? "진행 중인 연습이 있어 카테고리를 삭제할 수 없습니다."
                : "문항 분석 중에는 카테고리를 삭제할 수 없습니다.";
            return;
        }

        if (!TryBeginStructuralOperation("다른 문항 구조 변경 작업이 진행 중입니다."))
        {
            return;
        }

        try
        {
            var questionCount = selectedCategoryQuestionCount;
            var canDelete = await ConfirmDeleteAsync(target.Name, questionCount);
            if (!canDelete)
            {
                Feedback = "카테고리 삭제를 취소했거나 확인창을 표시할 수 없어 중단했습니다.";
                return;
            }

            var removed = await repository.RemoveCategoryAsync(target.Id);
            if (!removed)
            {
                Feedback = "카테고리를 찾을 수 없습니다.";
                return;
            }

            Categories.Remove(target);

            SelectedCategory = Categories.Count > 0
                ? Categories[0]
                : null;

            Feedback = $"카테고리 '{target.Name}' 삭제했습니다.";
            await UpdateSelectedCategoryQuestionCountAsync();
            await ReloadWrongAsync();
        }
        finally
        {
            EndStructuralOperation();
        }
    }

    private static async Task<bool> ConfirmDeleteAsync(string categoryName, int questionCount)
    {
        var message = $"'{categoryName}' 카테고리를 삭제하면 해당 카테고리의 문제와 오답 목록이 함께 삭제됩니다.\n" +
                      $"{questionCount}개 문항을 모두 삭제하시겠습니까?";

        return await ConfirmDestructiveActionAsync(
            "카테고리 삭제",
            message,
            "삭제");
    }

    private void ResetSelectedCategorySummary()
    {
        selectedCategoryQuestionCount = 0;
        selectedCategoryPracticeQuestionCount = 0;
        SourceFiles.Clear();
        SelectedSourceFile = null;
        OnPropertyChanged(nameof(SelectedCategoryQuestionCountText));
        OnPropertyChanged(nameof(MaxPracticeCount));
        UpdatePracticeState();
    }

    private async Task UpdateSelectedCategoryQuestionCountAsync()
    {
        var refreshVersion = Interlocked.Increment(ref selectedCategoryRefreshVersion);
        var targetCategory = SelectedCategory;
        IsSelectedCategoryLoading = true;
        try
        {
            if (targetCategory == null)
            {
                ResetSelectedCategorySummary();
                return;
            }

            var questions = await repository.GetQuestionsAsync(targetCategory.Id);
            if (refreshVersion != Volatile.Read(ref selectedCategoryRefreshVersion) ||
                SelectedCategory?.Id != targetCategory.Id)
            {
                return;
            }

            selectedCategoryQuestionCount = questions.Count;
            selectedCategoryPracticeQuestionCount = questions.Count(x =>
                x.CorrectAnswers.Any(answer => !string.IsNullOrWhiteSpace(answer)));
            await UpdateSourceFilesAsync(targetCategory, refreshVersion);
            if (refreshVersion != Volatile.Read(ref selectedCategoryRefreshVersion) ||
                SelectedCategory?.Id != targetCategory.Id)
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedCategoryQuestionCountText));
            OnPropertyChanged(nameof(MaxPracticeCount));
            UpdatePracticeState();
        }
        finally
        {
            if (refreshVersion == Volatile.Read(ref selectedCategoryRefreshVersion))
            {
                IsSelectedCategoryLoading = false;
            }
        }
    }

    private async Task UpdateSourceFilesAsync(Category targetCategory, int refreshVersion)
    {
        var files = await repository.GetSourceFilesAsync(targetCategory.Id);
        if (refreshVersion != Volatile.Read(ref selectedCategoryRefreshVersion) ||
            SelectedCategory?.Id != targetCategory.Id)
        {
            return;
        }

        SourceFiles.Clear();
        foreach (var file in files)
        {
            SourceFiles.Add(file);
        }

        SelectedSourceFile = SourceFiles.FirstOrDefault();
        OnPropertyChanged(nameof(CanDeleteSourceFile));
    }

    private string? ResolveSourceFilePath(string sourceFileName)
    {
        if (string.IsNullOrWhiteSpace(sourceFileName))
        {
            return null;
        }

        return GetSourceFileSearchDirectories()
            .Where(Directory.Exists)
            .Select(directory => Path.Combine(directory, sourceFileName))
            .FirstOrDefault(File.Exists);
    }

    private IEnumerable<string> GetSourceFileSearchDirectories()
    {
        var directories = new List<string> { sourceFileDirectory };
        if (!ShouldSearchDevelopmentSourceDirectories())
        {
            return directories.Distinct(StringComparer.OrdinalIgnoreCase);
        }

        var currentDir = Directory.GetCurrentDirectory();
        if (!string.IsNullOrWhiteSpace(currentDir))
        {
            directories.Add(currentDir);
            directories.Add(Path.Combine(currentDir, "src"));
            AddAncestorSourceDirectories(directories, currentDir);
        }

        var appDir = AppContext.BaseDirectory;
        if (!string.IsNullOrWhiteSpace(appDir))
        {
            directories.Add(appDir);
            directories.Add(Path.Combine(appDir, "src"));
            AddAncestorSourceDirectories(directories, appDir);
        }

        return directories.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static bool ShouldSearchDevelopmentSourceDirectories()
    {
#if DEBUG
        return DeviceInfo.Platform != DevicePlatform.Android &&
               DeviceInfo.Platform != DevicePlatform.iOS;
#else
        return false;
#endif
    }

    private static void AddAncestorSourceDirectories(ICollection<string> directories, string startDirectory)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(startDirectory));
        for (var depth = 0; depth < 10 && directory != null; depth++)
        {
            directories.Add(Path.Combine(directory.FullName, "src"));
            directory = directory.Parent;
        }
    }

    private void UpdatePracticeState()
    {
        OnPropertyChanged(nameof(CanModifyQuestionStructure));
        OnPropertyChanged(nameof(CanAddCategory));
        OnPropertyChanged(nameof(CanImportAnswerMap));
        OnPropertyChanged(nameof(CanStartPractice));
        OnPropertyChanged(nameof(CanStartWrongPractice));
        OnPropertyChanged(nameof(ProgressDisplay));
        OnPropertyChanged(nameof(HasSelectedCategory));
        OnPropertyChanged(nameof(CanDeleteCategory));
        OnPropertyChanged(nameof(CanDeleteSourceFile));
        OnPropertyChanged(nameof(CanStopPractice));
        UpdateSourceFileActionState();
        NotifyAnswerInputValidationState();
    }

    private void UpdateSourceFileActionState()
    {
        var canDelete = CanModifyQuestionStructure;
        foreach (var sourceFile in SourceFiles)
        {
            sourceFile.CanDelete = canDelete;
        }
    }

    private void SetWorkspaceSection(string section)
    {
        if (string.IsNullOrWhiteSpace(section))
        {
            return;
        }

        SelectedWorkspaceSection = section;
    }

    private static (PdfOcrResult Analysis, PdfAnalysisDiagnostics Diagnostics) TryAutoRepairAnalysis(
        string sourceFileName,
        PdfOcrResult analysis,
        PdfAnalysisDiagnostics diagnostics,
        ExpectedQuestionInput expectedQuestionInput,
        ExpectedQuestionRangeResolution expectedQuestionRangeResolution)
    {
        if (expectedQuestionRangeResolution.Range is not QuestionNumberRange expectedRange)
        {
            return (analysis, diagnostics);
        }

        if (!diagnostics.HasExpectedQuestionMismatch && !diagnostics.HasBlockingStructuralIssues)
        {
            return (analysis, diagnostics);
        }

        var currentAnalysis = analysis;
        var currentDiagnostics = diagnostics;

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var seedCandidates = BuildAutomaticRepairSeedCandidates(
                currentAnalysis.QuestionCandidates,
                currentDiagnostics.StructuralIssues);
            if (seedCandidates.Length == 0 ||
                seedCandidates.Length >= currentAnalysis.QuestionCandidates.Count)
            {
                AppLog.Info(
                    nameof(MainViewModel),
                    $"자동 보정 생략 | file={sourceFileName} | attempt={attempt} | current={currentAnalysis.QuestionCandidates.Count} | seed={seedCandidates.Length} | issues={currentDiagnostics.StructuralIssues.Length}");
                break;
            }

            var repairedCandidates = OcrQuestionSegmenter.RefineCandidates(
                currentAnalysis.Pages,
                expectedRange,
                seedCandidates);
            if (repairedCandidates.Count == 0)
            {
                AppLog.Info(
                    nameof(MainViewModel),
                    $"자동 보정 실패 | file={sourceFileName} | attempt={attempt} | seed={seedCandidates.Length} | rebuilt=0");
                break;
            }

            var candidateAnalysis = WithQuestionCandidates(currentAnalysis, repairedCandidates);
            var candidateDiagnostics = BuildPdfAnalysisDiagnostics(
                sourceFileName,
                candidateAnalysis,
                expectedQuestionInput,
                expectedQuestionRangeResolution);

            AppLog.Info(
                nameof(MainViewModel),
                $"자동 보정 시도 | file={sourceFileName} | attempt={attempt} | beforeIssues={currentDiagnostics.StructuralIssues.Length} | beforeMismatch={(currentDiagnostics.HasExpectedQuestionMismatch ? 1 : 0)} | seed={seedCandidates.Length} | rebuilt={repairedCandidates.Count} | afterIssues={candidateDiagnostics.StructuralIssues.Length} | afterMismatch={(candidateDiagnostics.HasExpectedQuestionMismatch ? 1 : 0)}");

            if (!IsBetterDiagnostics(candidateDiagnostics, currentDiagnostics))
            {
                AppLog.Info(
                    nameof(MainViewModel),
                    $"자동 보정 롤백 | file={sourceFileName} | attempt={attempt} | beforeIssues={currentDiagnostics.StructuralIssues.Length} | afterIssues={candidateDiagnostics.StructuralIssues.Length} | beforeMismatch={(currentDiagnostics.HasExpectedQuestionMismatch ? 1 : 0)} | afterMismatch={(candidateDiagnostics.HasExpectedQuestionMismatch ? 1 : 0)}");
                break;
            }

            currentAnalysis = candidateAnalysis;
            currentDiagnostics = candidateDiagnostics;

            if (!currentDiagnostics.HasExpectedQuestionMismatch &&
                !currentDiagnostics.HasBlockingStructuralIssues)
            {
                break;
            }
        }

        return (currentAnalysis, currentDiagnostics);
    }

    private static OcrQuestionCandidate[] BuildAutomaticRepairSeedCandidates(
        IReadOnlyList<OcrQuestionCandidate> candidates,
        IReadOnlyList<PdfAnalysisStructuralIssueDiagnostics> issues)
    {
        if (candidates.Count == 0 || issues.Count == 0)
        {
            return candidates.ToArray();
        }

        var candidateByIndex = candidates
            .GroupBy(x => x.Index)
            .ToDictionary(x => x.Key, x => x.First());
        var removableIndexes = new HashSet<int>();

        foreach (var issue in issues)
        {
            if (!issue.Index.HasValue ||
                !candidateByIndex.TryGetValue(issue.Index.Value, out var candidate))
            {
                continue;
            }

            if (issue.Code is "invalid-span" or "boilerplate-candidate" or "shared-context-leak")
            {
                if (candidate.IsInferred)
                {
                    removableIndexes.Add(candidate.Index);
                }

                continue;
            }

            if (issue.Code is "overlapping-range" or "nonincreasing-start")
            {
                var overlappingCandidates = ResolveOverlappingCandidates(candidates, candidate.Index);
                foreach (var overlappingCandidate in overlappingCandidates.Where(x => x.IsInferred))
                {
                    removableIndexes.Add(overlappingCandidate.Index);
                }
            }
        }

        return candidates
            .Where(x => !removableIndexes.Contains(x.Index))
            .ToArray();
    }

    private static OcrQuestionCandidate[] ResolveOverlappingCandidates(
        IReadOnlyList<OcrQuestionCandidate> candidates,
        int candidateIndex)
    {
        var ordered = candidates
            .OrderBy(x => x.Index)
            .ThenBy(x => x.LogicalStartOrder)
            .ThenBy(x => x.StartPage)
            .ThenBy(x => x.StartLineInPage)
            .ToArray();
        var position = Array.FindIndex(ordered, x => x.Index == candidateIndex);
        if (position < 0)
        {
            return Array.Empty<OcrQuestionCandidate>();
        }

        var result = new List<OcrQuestionCandidate> { ordered[position] };
        if (position > 0)
        {
            result.Add(ordered[position - 1]);
        }

        if (position + 1 < ordered.Length)
        {
            result.Add(ordered[position + 1]);
        }

        return result.ToArray();
    }

    private static bool IsBetterDiagnostics(
        PdfAnalysisDiagnostics candidate,
        PdfAnalysisDiagnostics baseline)
    {
        var candidateScore = GetDiagnosticsPenaltyScore(candidate);
        var baselineScore = GetDiagnosticsPenaltyScore(baseline);
        return candidateScore < baselineScore;
    }

    private static int GetDiagnosticsPenaltyScore(PdfAnalysisDiagnostics diagnostics)
    {
        var mismatchPenalty = diagnostics.HasExpectedQuestionMismatch ? 1000 : 0;
        return mismatchPenalty + diagnostics.StructuralIssues.Length;
    }

    private static string BuildAnalysisSummary(PdfOcrResult analysis, PdfAnalysisDiagnostics? diagnostics = null)
    {
        var baseSummary = $"{analysis.Summary}\n미리보기:\n{analysis.Preview}";
        if (diagnostics?.ExpectedQuestionCount is int expectedQuestionCount)
        {
            baseSummary += $"\n입력 문항 수: {expectedQuestionCount}";

            if (diagnostics.ExpectedQuestionRange is QuestionNumberRange expectedRange)
            {
                var rangeLabel = diagnostics.ExpectedQuestionRangeWasAutoInferred
                    ? "자동 추정 문항 범위"
                    : "예상 문항 범위";
                baseSummary += $"\n{rangeLabel}: {expectedRange} ({expectedRange.Count}문항) / 분석 후보 수: {diagnostics.DistinctCandidateCount}";
            }
            else
            {
                baseSummary += "\n자동 추정 문항 범위: 확인 실패";
            }

            if (!string.IsNullOrWhiteSpace(diagnostics.ExpectedQuestionRangeReason))
            {
                baseSummary += $"\n범위 결정 근거: {diagnostics.ExpectedQuestionRangeReason}";
            }

            baseSummary += diagnostics.HasExpectedQuestionMismatch
                ? $"\n예상 비교 결과: 불일치 ({BuildExpectedQuestionMismatchSummary(diagnostics)})"
                : "\n예상 비교 결과: 일치";
        }

        if (diagnostics is not null)
        {
            baseSummary += diagnostics.HasBlockingStructuralIssues
                ? $"\n구조 검증 결과: 실패 ({BuildStructuralIssueSummary(diagnostics)})"
                : "\n구조 검증 결과: 통과";
        }

        if (!analysis.HasQuestionCandidates)
        {
            return baseSummary + "\n현재 OCR 텍스트에서 문항 번호 헤더 후보를 찾지 못했습니다.";
        }

        var topCandidates = analysis.QuestionCandidates
            .Take(10)
            .Select(x => $"{x.Index}:{x.Header}")
            .ToArray();

        return baseSummary + $"\n분할 후보(최대 10개): {string.Join(", ", topCandidates)}";
    }

    private static string BuildExpectedQuestionMismatchSummary(PdfAnalysisDiagnostics diagnostics)
    {
        if (diagnostics.ExpectedQuestionCount == null)
        {
            return string.Empty;
        }

        var messages = new List<string>();
        if (diagnostics.ExpectedQuestionRange is QuestionNumberRange expectedRange)
        {
            var rangeLabel = diagnostics.ExpectedQuestionRangeWasAutoInferred
                ? "자동 추정"
                : "예상";
            messages.Add($"{rangeLabel} {expectedRange.StartIndex}-{expectedRange.EndIndex} ({expectedRange.Count}개) / 분석 {diagnostics.DistinctCandidateCount}개");
        }
        else
        {
            messages.Add($"입력 문항 수 {diagnostics.ExpectedQuestionCount.Value}개에 맞는 시작 번호를 자동 추정하지 못했습니다.");
        }

        if (!string.IsNullOrWhiteSpace(diagnostics.ExpectedQuestionRangeReason))
        {
            messages.Add(diagnostics.ExpectedQuestionRangeReason);
        }

        if (diagnostics.MissingIndexes.Length > 0)
        {
            messages.Add($"누락 번호: {string.Join(", ", diagnostics.MissingIndexes)}");
        }

        if (diagnostics.UnexpectedIndexes.Length > 0)
        {
            messages.Add($"예상 범위 밖 번호: {string.Join(", ", diagnostics.UnexpectedIndexes)}");
        }

        if (diagnostics.DuplicateIndexes.Length > 0)
        {
            messages.Add($"중복 번호: {string.Join(", ", diagnostics.DuplicateIndexes.Select(x => $"{x.Index}({x.Count})"))}");
        }

        return string.Join(" / ", messages);
    }

    private static string BuildStructuralIssueSummary(PdfAnalysisDiagnostics diagnostics)
    {
        if (diagnostics.StructuralIssues.Length == 0)
        {
            return string.Empty;
        }

        var messages = diagnostics.StructuralIssues
            .Take(3)
            .Select(x => x.Message)
            .ToList();
        if (diagnostics.StructuralIssues.Length > messages.Count)
        {
            messages.Add($"외 {diagnostics.StructuralIssues.Length - messages.Count}건");
        }

        return string.Join(" / ", messages);
    }

    internal static PdfAnalysisStructuralIssueDiagnostics[] BuildStructuralIssues(
        IReadOnlyList<OcrQuestionCandidate> candidates,
        IReadOnlyList<OcrPageResult> pages)
    {
        var issues = new List<PdfAnalysisStructuralIssueDiagnostics>();
        foreach (var duplicatePage in pages
                     .GroupBy(x => x.PageIndex)
                     .Where(x => x.Count() > 1))
        {
            issues.Add(new PdfAnalysisStructuralIssueDiagnostics
            {
                Code = "duplicate-page-index",
                Message = $"p{duplicatePage.Key} 페이지 번호가 {duplicatePage.Count()}번 나타나 문서 순서를 확정할 수 없습니다."
            });
        }

        foreach (var duplicate in candidates
                     .GroupBy(x => x.Index)
                     .Where(x => x.Count() > 1))
        {
            issues.Add(new PdfAnalysisStructuralIssueDiagnostics
            {
                Code = "duplicate-index",
                Index = duplicate.Key,
                Message = $"{duplicate.Key}번 문항 경계가 {duplicate.Count()}개 검출되었습니다. 합본 또는 번호 재시작 여부를 확인해 주세요."
            });
        }

        var distinctIndexes = candidates
            .Select(x => x.Index)
            .Where(x => x > 0)
            .Distinct()
            .OrderBy(x => x)
            .ToArray();
        for (var index = 1; index < distinctIndexes.Length; index++)
        {
            if (distinctIndexes[index] == distinctIndexes[index - 1] + 1)
            {
                continue;
            }

            issues.Add(new PdfAnalysisStructuralIssueDiagnostics
            {
                Code = "nonconsecutive-index",
                Index = distinctIndexes[index],
                Message = $"{distinctIndexes[index - 1]}번 다음 문항이 {distinctIndexes[index]}번으로 건너뛰어 번호 연속성을 확인할 수 없습니다."
            });
        }

        var pageByIndex = pages
            .GroupBy(x => x.PageIndex)
            .ToDictionary(x => x.Key, x => x.First());
        var orderedByIndex = candidates
            .OrderBy(x => x.Index)
            .ThenBy(x => x.StartPage)
            .ThenBy(x => x.StartLineInPage)
            .ToArray();

        for (var i = 0; i < orderedByIndex.Length; i++)
        {
            var candidate = orderedByIndex[i];
            if (candidate.HasAmbiguousBoundary)
            {
                issues.Add(new PdfAnalysisStructuralIssueDiagnostics
                {
                    Code = "ambiguous-boundary",
                    Index = candidate.Index,
                    Message = string.IsNullOrWhiteSpace(candidate.BoundaryIssue)
                        ? $"{candidate.Index}번 문항 경계를 안전하게 결정할 수 없습니다."
                        : $"{candidate.Index}번: {candidate.BoundaryIssue}"
                });
            }

            var imageRegions = candidate.ImageRegions ?? Array.Empty<OcrQuestionImageRegion>();
            var sharedContextRegions = candidate.SharedContextRegions ?? Array.Empty<OcrQuestionImageRegion>();
            if (candidate.UsesSemanticImageRegions && imageRegions.Count == 0)
            {
                issues.Add(new PdfAnalysisStructuralIssueDiagnostics
                {
                    Code = "missing-image-region",
                    Index = candidate.Index,
                    Message = $"{candidate.Index}번의 의미 기반 이미지 영역을 만들지 못했습니다."
                });
            }

            foreach (var region in sharedContextRegions.Concat(imageRegions))
            {
                if (!double.IsFinite(region.LeftRatio) ||
                    !double.IsFinite(region.TopRatio) ||
                    !double.IsFinite(region.RightRatio) ||
                    !double.IsFinite(region.BottomRatio) ||
                    region.LeftRatio < 0d ||
                    region.TopRatio < 0d ||
                    region.RightRatio > 1d ||
                    region.BottomRatio > 1d ||
                    region.RightRatio <= region.LeftRatio ||
                    region.BottomRatio <= region.TopRatio ||
                    !region.HasReliableGeometry)
                {
                    issues.Add(new PdfAnalysisStructuralIssueDiagnostics
                    {
                        Code = "invalid-image-region",
                        Index = candidate.Index,
                        Message = $"{candidate.Index}번의 p{region.PageIndex} 이미지 경계가 유효하지 않습니다."
                    });
                }

                if (!pageByIndex.TryGetValue(region.PageIndex, out var regionPage) ||
                    string.IsNullOrWhiteSpace(regionPage.ImagePath) ||
                    !File.Exists(regionPage.ImagePath))
                {
                    issues.Add(new PdfAnalysisStructuralIssueDiagnostics
                    {
                        Code = "missing-page-image",
                        Index = candidate.Index,
                        Message = $"{candidate.Index}번에 필요한 p{region.PageIndex} 페이지 이미지가 없습니다."
                    });
                }
            }

            var boilerplateProbe = BuildStructuralProbeText(candidate, previewLineLimit: 3);
            var blockingPhrase = FindStructuralBlockingBoilerplatePhrase(boilerplateProbe);
            if (!string.IsNullOrWhiteSpace(blockingPhrase))
            {
                issues.Add(new PdfAnalysisStructuralIssueDiagnostics
                {
                    Code = "boilerplate-candidate",
                    Index = candidate.Index,
                    Message = $"{candidate.Index}번 후보에 '{blockingPhrase}' 문구가 포함되어 있습니다."
                });
            }

            if (HasInvalidCandidateSpan(candidate))
            {
                issues.Add(new PdfAnalysisStructuralIssueDiagnostics
                {
                    Code = "invalid-span",
                    Index = candidate.Index,
                    Message = $"{candidate.Index}번 범위가 비정상입니다: p{candidate.StartPage}:{candidate.StartLineInPage} -> p{candidate.EndPage}:{candidate.EndLineInPage}"
                });
            }

            if (HasSuspiciousSharedContextLeak(candidate))
            {
                issues.Add(new PdfAnalysisStructuralIssueDiagnostics
                {
                    Code = "shared-context-leak",
                    Index = candidate.Index,
                    Message = $"{candidate.Index}번 후보에 현재 번호와 맞지 않는 공통 지문 마커가 섞여 있습니다."
                });
            }

            if (i == 0)
            {
                continue;
            }

            var previous = orderedByIndex[i - 1];
            if (!HasInvalidCandidateSpan(previous) &&
                !HasInvalidCandidateSpan(candidate) &&
                CompareCandidateStart(candidate, previous) <= 0)
            {
                issues.Add(new PdfAnalysisStructuralIssueDiagnostics
                {
                    Code = "nonincreasing-start",
                    Index = candidate.Index,
                    Message = $"{candidate.Index}번 시작 위치가 {previous.Index}번보다 앞서거나 같습니다."
                });
            }

            if (!HasInvalidCandidateSpan(previous) &&
                !HasInvalidCandidateSpan(candidate) &&
                HasCandidateRangeOverlap(previous, candidate))
            {
                issues.Add(new PdfAnalysisStructuralIssueDiagnostics
                {
                    Code = "overlapping-range",
                    Index = candidate.Index,
                    Message = $"{previous.Index}번과 {candidate.Index}번 범위가 겹칩니다."
                });
            }
        }

        for (var leftIndex = 0; leftIndex < candidates.Count; leftIndex++)
        {
            for (var rightIndex = leftIndex + 1; rightIndex < candidates.Count; rightIndex++)
            {
                var leftCandidate = candidates[leftIndex];
                var rightCandidate = candidates[rightIndex];
                if (!SemanticImageRegionsOverlap(leftCandidate, rightCandidate))
                {
                    continue;
                }

                issues.Add(new PdfAnalysisStructuralIssueDiagnostics
                {
                    Code = "overlapping-image-region",
                    Index = rightCandidate.Index,
                    Message = $"{leftCandidate.Index}번과 {rightCandidate.Index}번의 이미지 영역이 겹칩니다."
                });
            }
        }

        var distinctSharedRegions = candidates
            .SelectMany(candidate =>
                candidate.SharedContextRegions ?? Array.Empty<OcrQuestionImageRegion>())
            .GroupBy(region =>
                $"{region.PageIndex}:{region.LeftRatio:F6}:{region.TopRatio:F6}:{region.RightRatio:F6}:{region.BottomRatio:F6}")
            .Select(group => group.First())
            .ToArray();
        foreach (var sharedRegion in distinctSharedRegions)
        {
            foreach (var owner in candidates)
            {
                var ownedRegions = owner.ImageRegions ?? Array.Empty<OcrQuestionImageRegion>();
                if (!ownedRegions.Any(ownedRegion => ImageRegionsOverlap(sharedRegion, ownedRegion)))
                {
                    continue;
                }

                issues.Add(new PdfAnalysisStructuralIssueDiagnostics
                {
                    Code = "shared-context-image-overlap",
                    Index = owner.Index,
                    Message = $"공통 지문 영역과 {owner.Index}번 문항 이미지 영역이 겹칩니다."
                });
            }
        }

        return issues
            .GroupBy(x => $"{x.Code}:{x.Index}:{x.Message}")
            .Select(x => x.First())
            .OrderBy(x => x.Index ?? int.MaxValue)
            .ThenBy(x => x.Code)
            .ToArray();
    }

    internal static bool SemanticImageRegionsOverlap(
        OcrQuestionCandidate leftCandidate,
        OcrQuestionCandidate rightCandidate)
    {
        var leftRegions = leftCandidate.ImageRegions ?? Array.Empty<OcrQuestionImageRegion>();
        var rightRegions = rightCandidate.ImageRegions ?? Array.Empty<OcrQuestionImageRegion>();
        return leftRegions.Any(left => rightRegions.Any(right => ImageRegionsOverlap(left, right)));
    }

    private static bool ImageRegionsOverlap(
        OcrQuestionImageRegion left,
        OcrQuestionImageRegion right)
    {
        const double overlapTolerance = 0.001d;
        return left.PageIndex == right.PageIndex &&
               Math.Min(left.RightRatio, right.RightRatio) -
                   Math.Max(left.LeftRatio, right.LeftRatio) > overlapTolerance &&
               Math.Min(left.BottomRatio, right.BottomRatio) -
                   Math.Max(left.TopRatio, right.TopRatio) > overlapTolerance;
    }

    private static string BuildStructuralProbeText(
        OcrQuestionCandidate candidate,
        int previewLineLimit)
    {
        return string.Join(
            Environment.NewLine,
            new[] { candidate.Header }
                .Concat(
                    SplitPreviewLines(candidate.PreviewText)
                        .Take(previewLineLimit))
                .Where(text => !string.IsNullOrWhiteSpace(text)));
    }

    private static IEnumerable<string> SplitPreviewLines(string? previewText)
    {
        return string.IsNullOrWhiteSpace(previewText)
            ? Array.Empty<string>()
            : previewText.Split(
                Environment.NewLine,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static bool HasSuspiciousSharedContextLeak(OcrQuestionCandidate candidate)
    {
        foreach (var text in new[] { candidate.Header }
                     .Concat(SplitPreviewLines(candidate.PreviewText).Take(2)))
        {
            if (!LooksLikeStructuralSharedContextMarkerText(text))
            {
                continue;
            }

            if (TryResolveSharedContextRange(text, candidate.Index, out _))
            {
                continue;
            }

            if (TryResolveSharedContextRange(
                    text,
                    candidate.Index,
                    fallbackStartIndex: candidate.Index,
                    out _))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static bool LooksLikeStructuralSharedContextMarkerText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var normalized = text.Trim();
        if (!LooksLikeSharedContextMarkerText(normalized))
        {
            return false;
        }

        return QuestionRangeHintRegex.IsMatch(normalized) ||
               StructuralSharedContextMarkerRegex.IsMatch(normalized) ||
               (normalized.Contains('(') && SharedContextRangeEndRegex.IsMatch(normalized)) ||
               (normalized.Contains('（') && SharedContextRangeEndRegex.IsMatch(normalized)) ||
               (normalized.Contains('(') && SharedContextRangeStartRegex.IsMatch(normalized)) ||
               (normalized.Contains('（') && SharedContextRangeStartRegex.IsMatch(normalized));
    }

    private static string? FindStructuralBlockingBoilerplatePhrase(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return StructuralBlockingBoilerplatePhrases.FirstOrDefault(phrase =>
            text.Contains(phrase, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasInvalidCandidateSpan(OcrQuestionCandidate candidate)
    {
        if (candidate.LogicalStartOrder > 0 || candidate.LogicalEndOrder > 0)
        {
            return candidate.LogicalEndOrder < candidate.LogicalStartOrder;
        }

        return candidate.EndPage < candidate.StartPage ||
               (candidate.EndPage == candidate.StartPage &&
                candidate.EndLineInPage < candidate.StartLineInPage);
    }

    private static int CompareCandidateStart(OcrQuestionCandidate left, OcrQuestionCandidate right)
    {
        var logicalComparison = left.LogicalStartOrder.CompareTo(right.LogicalStartOrder);
        if (logicalComparison != 0)
        {
            return logicalComparison;
        }

        var pageComparison = left.StartPage.CompareTo(right.StartPage);
        if (pageComparison != 0)
        {
            return pageComparison;
        }

        return left.StartLineInPage.CompareTo(right.StartLineInPage);
    }

    private static bool HasCandidateRangeOverlap(
        OcrQuestionCandidate previous,
        OcrQuestionCandidate current)
    {
        if ((previous.LogicalStartOrder > 0 || previous.LogicalEndOrder > 0) &&
            (current.LogicalStartOrder > 0 || current.LogicalEndOrder > 0))
        {
            return current.LogicalStartOrder <= previous.LogicalEndOrder;
        }

        if (current.StartPage < previous.EndPage)
        {
            return true;
        }

        return current.StartPage == previous.EndPage &&
               current.StartLineInPage <= previous.EndLineInPage;
    }

    private static string BuildCandidateMismatchMessage(
        PdfOcrResult analysis,
        AnswerMapParseResult parseResult,
        IReadOnlyList<Question> questions)
    {
        var messages = new List<string>();

        if (parseResult.Questions.Count() == 0)
        {
            messages.Add("정답이 없어 미채점 상태로 저장합니다.");
        }
        else if (!analysis.HasQuestionCandidates)
        {
            messages.Add("문항 분할 후보가 없어 수량 비교를 생략했습니다.");
        }
        else
        {
            var expected = analysis.DetectedQuestionCount;
            var parsed = parseResult.Questions.Count();

            if (parsed > expected)
            {
                messages.Add($"정답 항목 수({parsed}개)가 추정 문항 수({expected}개)보다 많습니다. 중복/헤더 미검출 항목 확인이 필요합니다.");
            }
            else if (parsed < expected)
            {
                messages.Add($"정답 항목 수({parsed}개)가 추정 문항 수({expected}개)보다 적습니다. 누락된 문항이 있을 수 있습니다.");
            }
        }

        var questionsWithoutImage = questions.Count(x =>
            (x.ImageSegments == null || x.ImageSegments.Length == 0) &&
            string.IsNullOrWhiteSpace(x.ImagePath));
        if (questionsWithoutImage > 0)
        {
            messages.Add($"OCR에서 정확히 대응되는 문항 후보가 없는 항목 {questionsWithoutImage}개는 이미지 없이 저장했습니다.");
        }

        return string.Join(" / ", messages);
    }

    private static string NormalizeSourceFileName(string sourceFileName)
    {
        return string.IsNullOrWhiteSpace(sourceFileName) ? "manual" : sourceFileName.Trim();
    }

    private static PdfAnalysisDiagnostics BuildPdfAnalysisDiagnostics(
        string sourceFileName,
        PdfOcrResult analysis,
        ExpectedQuestionInput expectedQuestionInput,
        ExpectedQuestionRangeResolution expectedQuestionRangeResolution)
    {
        var rawCandidates = analysis.QuestionCandidates
            .Where(x => x.Index > 0)
            .ToArray();
        var candidateIndexes = rawCandidates
            .Select(x => x.Index)
            .Distinct()
            .OrderBy(x => x)
            .ToArray();
        var duplicateIndexes = rawCandidates
            .GroupBy(x => x.Index)
            .Where(x => x.Count() > 1)
            .OrderBy(x => x.Key)
            .Select(x => new PdfAnalysisDuplicateDiagnostics
            {
                Index = x.Key,
                Count = x.Count(),
                Headers = x.Select(y => y.Header).Take(5).ToArray()
            })
            .ToArray();
        var expectedQuestionRange = expectedQuestionRangeResolution.Range;
        var structuralIssues = BuildStructuralIssues(rawCandidates, analysis.Pages);
        var missingIndexes = expectedQuestionRange.HasValue
            ? Enumerable.Range(expectedQuestionRange.Value.StartIndex, expectedQuestionRange.Value.Count)
                .Except(candidateIndexes)
                .ToArray()
            : Array.Empty<int>();
        var unexpectedIndexes = expectedQuestionRange.HasValue
            ? candidateIndexes
                .Where(x => !expectedQuestionRange.Value.Contains(x))
                .ToArray()
            : Array.Empty<int>();
        var hasExpectedQuestionMismatch = expectedQuestionInput.HasValue &&
            (!expectedQuestionRange.HasValue ||
             candidateIndexes.Length != expectedQuestionRange.Value.Count ||
             missingIndexes.Length > 0 ||
             unexpectedIndexes.Length > 0 ||
             duplicateIndexes.Length > 0);

        return new PdfAnalysisDiagnostics
        {
            GeneratedAt = DateTimeOffset.Now,
            SourceFileName = sourceFileName,
            ExpectedQuestionInput = expectedQuestionInput.RawText,
            ExpectedQuestionCount = expectedQuestionInput.ExpectedCountOrNull,
            ExpectedQuestionRange = expectedQuestionRange,
            ExpectedQuestionRangeWasAutoInferred = expectedQuestionRangeResolution.IsAutoInferred && expectedQuestionRange.HasValue,
            ExpectedQuestionRangeReason = expectedQuestionRangeResolution.Reason,
            PageCount = analysis.PageCount,
            TotalWordCount = analysis.TotalWordCount,
            RawCandidateCount = rawCandidates.Length,
            DistinctCandidateCount = candidateIndexes.Length,
            HasBlockingStructuralIssues = structuralIssues.Length > 0,
            HasExpectedQuestionMismatch = hasExpectedQuestionMismatch,
            CandidateIndexes = candidateIndexes,
            MissingIndexes = missingIndexes,
            UnexpectedIndexes = unexpectedIndexes,
            DuplicateIndexes = duplicateIndexes,
            StructuralIssues = structuralIssues,
            Candidates = rawCandidates
                .OrderBy(x => x.Index)
                .ThenBy(x => x.StartPage)
                .ThenBy(x => x.StartLineInPage)
                .Select(x => new PdfAnalysisCandidateDiagnostics
                {
                    Index = x.Index,
                    Header = x.Header,
                    IsInferred = x.IsInferred,
                    LogicalStartOrder = x.LogicalStartOrder,
                    LogicalEndOrder = x.LogicalEndOrder,
                    StartPage = x.StartPage,
                    StartLineInPage = x.StartLineInPage,
                    EndPage = x.EndPage,
                    EndLineInPage = x.EndLineInPage,
                    PreviewText = x.PreviewText
                })
                .ToArray(),
            Pages = analysis.Pages
                .OrderBy(x => x.PageIndex)
                .Select(x => new PdfAnalysisPageDiagnostics
                {
                    PageIndex = x.PageIndex,
                    WordCount = x.WordCount,
                    LineCount = x.Lines?.Count ?? 0,
                    LeadingLines = BuildLeadingLines(x),
                    CandidateIndexes = rawCandidates
                        .Where(candidate => candidate.StartPage == x.PageIndex)
                        .Select(candidate => candidate.Index)
                        .Distinct()
                        .OrderBy(index => index)
                        .ToArray()
                })
                .ToArray()
        };
    }

    private static string[] BuildLeadingLines(OcrPageResult page)
    {
        if (page.Lines != null && page.Lines.Count > 0)
        {
            return page.Lines
                .OrderBy(x => x.LineInPage)
                .Take(8)
                .Select(x => $"{x.LineInPage}: {x.Text}")
                .ToArray();
        }

        return page.Text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(8)
            .Select((text, index) => $"{index + 1}: {text}")
            .ToArray();
    }

    private static string BuildDiagnosticsText(PdfAnalysisDiagnostics diagnostics)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"파일: {diagnostics.SourceFileName}");
        builder.AppendLine($"생성 시각: {diagnostics.GeneratedAt:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"페이지 수: {diagnostics.PageCount}");
        builder.AppendLine($"전체 단어 수: {diagnostics.TotalWordCount}");
        builder.AppendLine($"원시 후보 수: {diagnostics.RawCandidateCount}");
        builder.AppendLine($"고유 후보 수: {diagnostics.DistinctCandidateCount}");
        builder.AppendLine($"후보 번호: {FormatIntArray(diagnostics.CandidateIndexes)}");

        if (!string.IsNullOrWhiteSpace(diagnostics.ExpectedQuestionInput))
        {
            builder.AppendLine($"입력값: {diagnostics.ExpectedQuestionInput}");
        }

        if (diagnostics.ExpectedQuestionCount is int expectedQuestionCount)
        {
            builder.AppendLine($"입력 문항 수: {expectedQuestionCount}");
        }

        if (diagnostics.ExpectedQuestionRange is QuestionNumberRange expectedRange)
        {
            builder.AppendLine($"결정된 문항 범위: {expectedRange}");
            builder.AppendLine($"범위 결정 방식: {(diagnostics.ExpectedQuestionRangeWasAutoInferred ? "자동 추정" : "직접 입력")}");
            builder.AppendLine($"예상 비교 불일치: {(diagnostics.HasExpectedQuestionMismatch ? "예" : "아니오")}");
            builder.AppendLine($"누락 번호: {FormatIntArray(diagnostics.MissingIndexes)}");
            builder.AppendLine($"예상 범위 밖 번호: {FormatIntArray(diagnostics.UnexpectedIndexes)}");
        }
        else if (diagnostics.ExpectedQuestionCount is int)
        {
            builder.AppendLine("결정된 문항 범위: 없음");
            builder.AppendLine("범위 결정 방식: 자동 추정 실패");
            builder.AppendLine($"예상 비교 불일치: {(diagnostics.HasExpectedQuestionMismatch ? "예" : "아니오")}");
        }

        if (!string.IsNullOrWhiteSpace(diagnostics.ExpectedQuestionRangeReason))
        {
            builder.AppendLine($"범위 결정 근거: {diagnostics.ExpectedQuestionRangeReason}");
        }

        builder.AppendLine($"중복 번호: {(diagnostics.DuplicateIndexes.Length == 0 ? "없음" : string.Join(", ", diagnostics.DuplicateIndexes.Select(x => $"{x.Index}({x.Count})")))}");
        builder.AppendLine($"구조 검증 차단: {(diagnostics.HasBlockingStructuralIssues ? "예" : "아니오")}");
        if (diagnostics.StructuralIssues.Length > 0)
        {
            builder.AppendLine("[구조 문제]");
            foreach (var issue in diagnostics.StructuralIssues)
            {
                builder.AppendLine($"- {(issue.Index.HasValue ? $"{issue.Index}번 " : string.Empty)}{issue.Code}: {issue.Message}");
            }

            builder.AppendLine();
        }

        builder.AppendLine();
        builder.AppendLine("[페이지별 상단 줄]");
        foreach (var page in diagnostics.Pages)
        {
            builder.AppendLine($"- 페이지 {page.PageIndex} | 후보: {FormatIntArray(page.CandidateIndexes)} | lineCount={page.LineCount} | words={page.WordCount}");
            foreach (var line in page.LeadingLines)
            {
                builder.AppendLine($"  {line}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("[후보 위치]");
        foreach (var candidate in diagnostics.Candidates)
        {
            builder.AppendLine($"- {candidate.Index}번{(candidate.IsInferred ? " [추정]" : string.Empty)} | ord {candidate.LogicalStartOrder}->{candidate.LogicalEndOrder} | p{candidate.StartPage}:{candidate.StartLineInPage} -> p{candidate.EndPage}:{candidate.EndLineInPage} | {candidate.Header}");
            if (!string.IsNullOrWhiteSpace(candidate.PreviewText))
            {
                builder.AppendLine($"  preview: {candidate.PreviewText}");
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatIntArray(IReadOnlyList<int> values)
    {
        return values.Count == 0 ? "없음" : string.Join(", ", values);
    }

    private static FeedbackTone ResolveFeedbackTone(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return FeedbackTone.Neutral;
        }

        var normalized = message.Trim();
        if (new[]
            {
                "실패", "오류", "중단", "찾을 수 없", "일치하지 않", "누락", "잘못"
            }.Any(normalized.Contains))
        {
            return FeedbackTone.Error;
        }

        if (new[]
            {
                "취소", "확인", "없습니다", "없어", "지원하지", "정리"
            }.Any(normalized.Contains))
        {
            return FeedbackTone.Warning;
        }

        if (new[]
            {
                "완료", "저장했습니다", "등록했습니다", "추가했습니다", "삭제했습니다",
                "시작합니다", "시작했습니다", "종료했습니다", "불러왔습니다"
            }.Any(normalized.Contains))
        {
            return FeedbackTone.Success;
        }

        return FeedbackTone.Neutral;
    }

    private enum FeedbackTone
    {
        Neutral,
        Success,
        Warning,
        Error
    }

    private sealed class PdfAnalysisDiagnostics
    {
        public DateTimeOffset GeneratedAt { get; init; }
        public string SourceFileName { get; init; } = string.Empty;
        public string ExpectedQuestionInput { get; init; } = string.Empty;
        public int? ExpectedQuestionCount { get; init; }
        public QuestionNumberRange? ExpectedQuestionRange { get; init; }
        public bool ExpectedQuestionRangeWasAutoInferred { get; init; }
        public string ExpectedQuestionRangeReason { get; init; } = string.Empty;
        public int PageCount { get; init; }
        public int TotalWordCount { get; init; }
        public int RawCandidateCount { get; init; }
        public int DistinctCandidateCount { get; init; }
        public bool HasBlockingStructuralIssues { get; init; }
        public bool HasExpectedQuestionMismatch { get; init; }
        public int[] CandidateIndexes { get; init; } = Array.Empty<int>();
        public int[] MissingIndexes { get; init; } = Array.Empty<int>();
        public int[] UnexpectedIndexes { get; init; } = Array.Empty<int>();
        public PdfAnalysisDuplicateDiagnostics[] DuplicateIndexes { get; init; } = Array.Empty<PdfAnalysisDuplicateDiagnostics>();
        public PdfAnalysisStructuralIssueDiagnostics[] StructuralIssues { get; init; } = Array.Empty<PdfAnalysisStructuralIssueDiagnostics>();
        public PdfAnalysisCandidateDiagnostics[] Candidates { get; init; } = Array.Empty<PdfAnalysisCandidateDiagnostics>();
        public PdfAnalysisPageDiagnostics[] Pages { get; init; } = Array.Empty<PdfAnalysisPageDiagnostics>();
    }

    private sealed class PdfAnalysisDuplicateDiagnostics
    {
        public int Index { get; init; }
        public int Count { get; init; }
        public string[] Headers { get; init; } = Array.Empty<string>();
    }

    private sealed class PdfAnalysisCandidateDiagnostics
    {
        public int Index { get; init; }
        public string Header { get; init; } = string.Empty;
        public bool IsInferred { get; init; }
        public int LogicalStartOrder { get; init; }
        public int LogicalEndOrder { get; init; }
        public int StartPage { get; init; }
        public int StartLineInPage { get; init; }
        public int EndPage { get; init; }
        public int EndLineInPage { get; init; }
        public string PreviewText { get; init; } = string.Empty;
    }

    internal sealed class PdfAnalysisStructuralIssueDiagnostics
    {
        public string Code { get; init; } = string.Empty;
        public int? Index { get; init; }
        public string Message { get; init; } = string.Empty;
    }

    private sealed class PdfAnalysisPageDiagnostics
    {
        public int PageIndex { get; init; }
        public int WordCount { get; init; }
        public int LineCount { get; init; }
        public string[] LeadingLines { get; init; } = Array.Empty<string>();
        public int[] CandidateIndexes { get; init; } = Array.Empty<int>();
    }

    private readonly record struct ExpectedQuestionInput(
        string RawText,
        int? Count,
        QuestionNumberRange? ExplicitRange)
    {
        public static ExpectedQuestionInput Empty => new(string.Empty, null, null);

        public bool HasValue => Count.HasValue || ExplicitRange.HasValue;

        public bool IsCountOnly => Count.HasValue && !ExplicitRange.HasValue;

        public int ExpectedCount => ExplicitRange?.Count ?? Count ?? 0;

        public int? ExpectedCountOrNull => HasValue ? ExpectedCount : null;

        public static ExpectedQuestionInput FromCount(string rawText, int count)
        {
            return new ExpectedQuestionInput(rawText, count, null);
        }

        public static ExpectedQuestionInput FromRange(string rawText, QuestionNumberRange range)
        {
            return new ExpectedQuestionInput(rawText, range.Count, range);
        }
    }

    private readonly record struct ExpectedQuestionRangeResolution(
        QuestionNumberRange? Range,
        int ExpectedCount,
        bool IsAutoInferred,
        string Reason)
    {
        public static ExpectedQuestionRangeResolution None => new(null, 0, false, string.Empty);
    }

    private readonly record struct SharedContextDefinition(
        QuestionNumberRange QuestionRange,
        int ContextStartLine,
        int QuestionStartLine);

    private readonly record struct PageLineText(int PageIndex, int LineInPage, string Text);
}
