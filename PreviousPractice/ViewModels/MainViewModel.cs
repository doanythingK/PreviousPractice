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
    private DateTimeOffset pdfAnalysisStartAt;
    private string practiceCountText = "1";
    private string feedback = string.Empty;
    private string sessionFeedback = string.Empty;
    private string userAnswer = string.Empty;
    private bool isPracticeRunning;
    private bool overwriteExisting;
    private Category? selectedCategory;
    private Question? currentQuestion;
    private int sessionCount;
    private int currentIndex;
    private int correctCount;
    private int selectedCategoryQuestionCount;
    private int selectedCategoryPracticeQuestionCount;
    private SourceFileSummary? selectedSourceFile;
    private bool includeUnansweredInPractice = true;
    private string selectedSourceFileName = string.Empty;
    private IReadOnlyList<Question> currentSession = Array.Empty<Question>();
    private const double PhoneQuestionImageViewportWidth = 320d;
    private const double PhoneQuestionImageViewportHeight = 260d;
    private const double TabletQuestionImageViewportWidth = 560d;
    private const double TabletQuestionImageViewportHeight = 440d;
    private const double DesktopQuestionImageViewportWidth = 760d;
    private const double DesktopQuestionImageViewportHeight = 620d;
    private const double MinQuestionImageSliceRatio = 0.02d;
    private const double MinQuestionImageSliceWidthRatio = 0.08d;
    private const double QuestionImageCropPaddingRatio = 0.015d;
    private const int MalformedSharedContextFallbackQuestionCount = 3;
    private static readonly char[] QuestionRangeSeparators = ['-', '~', '〜'];
    private static readonly Regex QuestionRangeHintRegex = new(
        @"(?<!\d)(?<start>\d{1,3})\s*[-~〜]\s*(?<end>\d{1,3})\s*(?:번|문항|문제)?(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SharedContextRangeStartRegex = new(
        @"(?<!\d)(?<start>\d{1,3})\s*[-~〜]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public ObservableCollection<Category> Categories { get; } = new();
    public ObservableCollection<SourceFileSummary> SourceFiles { get; } = new();
    public ObservableCollection<Question> WrongQuestions { get; } = new();
    public ObservableCollection<string> SourceFilesDirectory { get; } = new();
    public ObservableCollection<QuestionImageSliceViewModel> CurrentQuestionImageSlices { get; } = new();

    public string PdfAnalysisSummary
    {
        get => pdfAnalysisSummary;
        set => SetProperty(ref pdfAnalysisSummary, value);
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
                OnPropertyChanged(nameof(CanImportAnswerMap));
                OnPropertyChanged(nameof(CanStartPractice));
                OnPropertyChanged(nameof(ShowPdfAnalysisProgress));
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
        !IsPdfAnalysisInProgress &&
        !string.IsNullOrWhiteSpace(SelectedSourceFileName);

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
        set => SetProperty(ref feedback, value);
    }

    public string SessionFeedback
    {
        get => sessionFeedback;
        set => SetProperty(ref sessionFeedback, value);
    }

    public string UserAnswer
    {
        get => userAnswer;
        set => SetProperty(ref userAnswer, value);
    }

    public bool IsPracticeRunning
    {
        get => isPracticeRunning;
        set
        {
            if (SetProperty(ref isPracticeRunning, value))
            {
                OnPropertyChanged(nameof(CanStartPractice));
                OnPropertyChanged(nameof(CanDeleteCategory));
                OnPropertyChanged(nameof(CanDeleteSourceFile));
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
                OnPropertyChanged(nameof(HasSelectedCategory));
                OnPropertyChanged(nameof(CanStartPractice));
                OnPropertyChanged(nameof(CanDeleteCategory));
                OnPropertyChanged(nameof(CanDeleteSourceFile));
                OnPropertyChanged(nameof(CanImportAnswerMap));
                _ = UpdateSelectedCategoryQuestionCountAsync();
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
                _ = UpdateSelectedCategoryQuestionCountAsync();
            }
        }
    }

    public Question? CurrentQuestion
    {
        get => currentQuestion;
        private set => SetProperty(ref currentQuestion, value);
    }

    public int SessionTotalCount
    {
        get => sessionCount;
        private set => SetProperty(ref sessionCount, value);
    }

    public int SessionCurrentIndex
    {
        get => currentIndex;
        private set
        {
            if (SetProperty(ref currentIndex, value))
            {
                OnPropertyChanged(nameof(ProgressDisplay));
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

    public bool CanAddCategory => !string.IsNullOrWhiteSpace(NewCategoryName);

    public bool HasSelectedCategory => SelectedCategory != null;

    public bool CanDeleteCategory => SelectedCategory != null && !IsPracticeRunning;

    public bool CanDeleteSourceFile => SelectedSourceFile != null && SelectedCategory != null && !IsPracticeRunning;

    public bool CanStartPractice =>
        HasSelectedCategory &&
        !IsPracticeRunning &&
        !IsPdfAnalysisInProgress &&
        int.TryParse(PracticeCountText, out var count) && count > 0 &&
        count <= MaxPracticeCount;

    public bool CanStartWrongPractice => !IsPracticeRunning && WrongQuestionCount > 0;

    public string ProgressDisplay =>
        IsPracticeRunning
            ? $"{SessionCurrentIndex + 1}/{SessionTotalCount}"
            : string.Empty;

    public string CurrentQuestionText => CurrentQuestion?.Prompt ?? string.Empty;

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

    public double CurrentQuestionImageViewportWidth
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

    public double CurrentQuestionImageViewportHeight
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

    public bool HasCurrentQuestionImages => CurrentQuestionImageSlices.Count > 0;

    public bool ShowCurrentQuestionText => !HasCurrentQuestionImages;

    public string CurrentQuestionChoicesText => CurrentQuestion is null || CurrentQuestion.Choices.Length == 0
        ? string.Empty
        : string.Join("\n", CurrentQuestion.Choices.Select((x, i) => $"{i + 1}. {x}"));

    public RelayCommand AddCategoryCommand { get; }
    public RelayCommand ImportAnswerMapCommand { get; }
    public RelayCommand StartPracticeCommand { get; }
    public RelayCommand SubmitAnswerCommand { get; }
    public RelayCommand StartWrongPracticeCommand { get; }
    public RelayCommand ReloadWrongCommand { get; }
    public RelayCommand<Guid?> RemoveWrongCommand { get; }
    public RelayCommand DeleteCategoryCommand { get; }
    public RelayCommand<SourceFileSummary?> DeleteSourceFileCommand { get; }
    public RelayCommand LoadAnswerFileCommand { get; }
    public RelayCommand LoadSourceFilesFromDirectoryCommand { get; }
    public RelayCommand ImportSourceFileCommand { get; }

    public MainViewModel() : this(new PracticeRepository(), new PdfAnalysisService())
    {
    }

    public MainViewModel(IPracticeRepository repository, IPdfAnalysisService pdfAnalysisService)
    {
        this.repository = repository;
        this.pdfAnalysisService = pdfAnalysisService;
        sourceFileDirectory = Path.Combine(FileSystem.AppDataDirectory, SourceFileDirectoryName);
        sourceAnalysisDirectory = Path.Combine(sourceFileDirectory, AnalysisDirectoryName);
        AddCategoryCommand = new RelayCommand(async void () => await AddCategoryAsync());
        ImportAnswerMapCommand = new RelayCommand(async void () => await ImportAnswerMapAsync());
        StartPracticeCommand = new RelayCommand(async void () => await StartPracticeAsync());
        SubmitAnswerCommand = new RelayCommand(async void () => await SubmitAnswerAsync());
        StartWrongPracticeCommand = new RelayCommand(async void () => await StartWrongPracticeAsync());
        ReloadWrongCommand = new RelayCommand(async void () => await ReloadWrongAsync());
        RemoveWrongCommand = new RelayCommand<Guid?>(GuidFromObject(RemoveWrongById));
        DeleteCategoryCommand = new RelayCommand(async void () => await DeleteCategoryAsync());
        DeleteSourceFileCommand = new RelayCommand<SourceFileSummary?>(async source => await DeleteSourceFileAsync(source));
        LoadAnswerFileCommand = new RelayCommand(async void () => await LoadAnswerFileAsync());
        LoadSourceFilesFromDirectoryCommand = new RelayCommand(async void () => await LoadSourceFilesFromDirectoryAsync());
        ImportSourceFileCommand = new RelayCommand(async void () => await ImportSourceFileAsync());

        _ = InitializeAsync();
    }

    private static Action<Guid?> GuidFromObject(Func<Guid, Task> action)
    {
        return parameter =>
        {
            if (parameter.HasValue && parameter.Value != Guid.Empty)
            {
                _ = action(parameter.Value);
            }
        };
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
        if (string.IsNullOrWhiteSpace(NewCategoryName))
        {
            Feedback = "카테고리 이름이 비어 있습니다.";
            return;
        }

        var category = await repository.AddOrGetCategoryAsync(NewCategoryName);
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

    private async Task ImportAnswerMapAsync()
    {
        if (SelectedCategory == null)
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

        if (string.IsNullOrWhiteSpace(SelectedSourceFileName))
        {
            Feedback = "문항 파일을 먼저 선택해 주세요.";
            return;
        }

        var normalizedSourceFileName = NormalizeSourceFileName(SelectedSourceFileName);
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
            $"문항 분석 시작 | category={SelectedCategory.Id} | file={sourceFilePath}");

        ClearPdfAnalysisState();
        IsPdfAnalysisInProgress = true;
        pdfAnalysisStartAt = DateTimeOffset.UtcNow;
        PdfAnalysisProgressValue = 0d;
        PdfAnalysisStatus = "문항 PDF OCR 분석을 시작합니다.";
        PdfAnalysisStatusColor = Color.FromArgb("#0EA5E9");
        Feedback = PdfAnalysisStatus;

        try
        {
            var progress = new Progress<PdfAnalysisProgress>(UpdatePdfAnalysisProgress);
            var analysis = await pdfAnalysisService.AnalyzePdfAsync(
                sourceFilePath,
                progress,
                expectedQuestionInput.ExplicitRange);
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
                var refinedCandidates = OcrQuestionSegmenter.SplitByHeader(analysis.Pages, inferredRange);
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
                "문항 데이터 저장 중"));

            await SavePdfAnalysisAsync(normalizedSourceFileName, analysis);
            var diagnostics = BuildPdfAnalysisDiagnostics(
                normalizedSourceFileName,
                analysis,
                expectedQuestionInput,
                resolvedExpectedQuestionRange);
            var diagnosticsPath = await SavePdfAnalysisDiagnosticsAsync(normalizedSourceFileName, diagnostics);
            PdfAnalysisSummary = BuildAnalysisSummary(analysis, diagnostics);
            PdfAnalysisStatus = "문항 분석이 완료되었습니다.";
            PdfAnalysisStatusColor = Color.FromArgb("#16A34A");

            if (diagnostics.HasExpectedQuestionMismatch)
            {
                var mismatchSummary = BuildExpectedQuestionMismatchSummary(diagnostics);
                PdfAnalysisStatus = "예상 문항 범위와 분석 결과가 일치하지 않습니다.";
                PdfAnalysisStatusColor = Color.FromArgb("#DC2626");
                Feedback = $"{mismatchSummary}\n문항 반영을 중단했습니다." +
                           (string.IsNullOrWhiteSpace(diagnosticsPath) ? string.Empty : $"\n진단 파일: {diagnosticsPath}");
                AppLog.Error(
                    nameof(MainViewModel),
                    $"문항 반영 중단 | expectedCount={diagnostics.ExpectedQuestionCount?.ToString() ?? "n/a"} | expectedRange={diagnostics.ExpectedQuestionRange?.ToString() ?? "n/a"} | detected={diagnostics.DistinctCandidateCount} | missing={string.Join(",", diagnostics.MissingIndexes)} | unexpected={string.Join(",", diagnostics.UnexpectedIndexes)} | duplicates={string.Join(",", diagnostics.DuplicateIndexes.Select(x => x.Index))} | diag={diagnosticsPath ?? "n/a"}");
                return;
            }

            var result = QuestionSetParser.ParseAnswerMapWithDetails(AnswerMapText);
            if (!analysis.HasQuestionCandidates && result.IsEmpty)
            {
                Feedback = $"{analysis.Summary} / 문항 후보가 없어 저장할 수 없습니다.";
                AppLog.Error(
                    nameof(MainViewModel),
                    $"문항 저장 중단 | 후보 0개 & 정답맵 비어있음 | file={sourceFilePath}");
                return;
            }

            var shouldOverwrite = OverwriteExisting;
            if (shouldOverwrite)
            {
                var existingCount = await repository.GetQuestionCountBySourceFileAsync(
                    SelectedCategory.Id,
                    normalizedSourceFileName);

                if (existingCount > 0)
                {
                    shouldOverwrite = await ConfirmOverwriteImportAsync(normalizedSourceFileName, existingCount);
                    if (!shouldOverwrite)
                    {
                        Feedback = "문항 반영을 취소했습니다.";
                        return;
                    }
                }
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

            var sourceQuestionIndexes = candidateByIndex.Keys
                .Concat(answerByIndex.Keys)
                .Distinct()
                .OrderBy(x => x)
                .ToArray();

            var questions = sourceQuestionIndexes
                .Select(x =>
                {
                    var hasAnswer = answerByIndex.TryGetValue(x, out var parsedQuestion);
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
                        CategoryId = SelectedCategory.Id,
                        SourceFileName = normalizedSourceFileName,
                        Index = x,
                        Type = hasAnswer
                            ? parsedQuestion.Type
                            : QuestionType.MultipleChoice,
                        CorrectAnswers = hasAnswer
                            ? parsedQuestion.CorrectAnswers
                            : Array.Empty<string>(),
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
                            question.Prompt = $"{SelectedCategory.Name} - {candidate.Header} {candidate.PreviewText}";
                        }
                        else
                        {
                            question.Prompt = $"{SelectedCategory.Name} - {candidate.Header}";
                        }
                    }

                    if (string.IsNullOrWhiteSpace(question.Prompt))
                    {
                        question.Prompt = $"{SelectedCategory.Name} - 문항 {x}";
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

            await repository.SaveImportedQuestionsAsync(
                SelectedCategory.Id,
                normalizedSourceFileName,
                questions,
                overwriteBySourceFile: shouldOverwrite,
                updateExistingCorrectAnswers: true);

            var mismatchMessage = BuildCandidateMismatchMessage(analysis, result, questions);
            var summary = result.HasErrors || !string.IsNullOrWhiteSpace(mismatchMessage)
                ? $"{questions.Length}개 저장"
                  + (!result.HasErrors ? string.Empty : $"(일부 파싱 오류: {string.Join(", ", result.Errors)})")
                  + (string.IsNullOrWhiteSpace(mismatchMessage) ? string.Empty : $" / {mismatchMessage}")
                : $"{questions.Length}개 저장 완료";

            Feedback = $"{analysis.Summary} / {summary}";
            AppLog.Info(
                nameof(MainViewModel),
                $"문항 반영 완료 | file={sourceFilePath} | saved={questions.Length}");
            AnswerMapText = string.Empty;
            await UpdateSelectedCategoryQuestionCountAsync();
            var sourceFile = SourceFiles.FirstOrDefault(x =>
                string.Equals(x.SourceFileName, normalizedSourceFileName, StringComparison.OrdinalIgnoreCase));
            if (sourceFile != null)
            {
                SelectedSourceFile = sourceFile;
            }
            await ReloadWrongAsync();
        }
        catch (Exception ex)
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
        finally
        {
            IsPdfAnalysisInProgress = false;
        }
    }

    private static QuestionImageSegment[] BuildQuestionImageSegments(
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
            .ToDictionary(x => x.PageIndex, x => x);

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

        return null;
    }

    private static bool TryResolveSharedContextRange(
        string? text,
        int currentIndex,
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
        if (!startOnlyMatch.Success ||
            !int.TryParse(startOnlyMatch.Groups["start"].Value, out var inferredStart) ||
            inferredStart <= 0)
        {
            return false;
        }

        questionRange = new QuestionNumberRange(
            inferredStart,
            inferredStart + MalformedSharedContextFallbackQuestionCount - 1);
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
        PdfAnalysisProgressValue = 0d;
        PdfAnalysisStatus = string.Empty;
        PdfAnalysisStatusColor = Color.FromArgb("#334155");
        PdfAnalysisPagesPerSecond = 0d;
        pdfAnalysisStartAt = DateTimeOffset.UtcNow;
        OnPropertyChanged(nameof(ShowPdfAnalysisProgress));
        OnPropertyChanged(nameof(PdfAnalysisProgressText));
    }

    private void UpdatePdfAnalysisProgress(PdfAnalysisProgress progress)
    {
        pdfAnalysisProcessedPages = Math.Max(0, progress.ProcessedPages);
        pdfAnalysisTotalPages = Math.Max(0, progress.TotalPages);

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
    }

    private async Task LoadSourceFilesFromDirectoryAsync()
    {
        try
        {
            Directory.CreateDirectory(sourceFileDirectory);
            var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var files = GetSourceFileSearchDirectories()
                .Where(Directory.Exists)
                .SelectMany(directory => Directory.EnumerateFiles(directory))
                .Where(path => string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name) && seenFiles.Add(name!))
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
                SelectedSourceFileName = SourceFilesDirectory.FirstOrDefault();
            }
        }
        catch (Exception ex)
        {
            Feedback = $"문항 파일 목록을 읽지 못했습니다: {ex.Message}";
        }

        OnPropertyChanged(nameof(CanImportAnswerMap));
    }

    private async Task ImportSourceFileAsync()
    {
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

            Directory.CreateDirectory(sourceFileDirectory);
            var sourceFileName = file.FileName?.Trim();
            if (string.IsNullOrWhiteSpace(sourceFileName))
            {
                Feedback = "문항 파일 이름을 가져올 수 없습니다.";
                return;
            }

            var destinationPath = Path.Combine(sourceFileDirectory, sourceFileName);
            if (File.Exists(destinationPath))
            {
                var overwrite = await ConfirmOverwriteSourceFileAsync(sourceFileName);
                if (!overwrite)
                {
                    Feedback = "문항 파일 등록을 취소했습니다.";
                    return;
                }
            }

            using var sourceStream = await file.OpenReadAsync();
            using var destinationStream = File.Create(destinationPath);
            await sourceStream.CopyToAsync(destinationStream);

            Feedback = $"문항 파일을 등록했습니다: {sourceFileName}";
            await LoadSourceFilesFromDirectoryAsync();
            SelectedSourceFileName = sourceFileName;
        }
        catch (Exception ex)
        {
            Feedback = $"문항 파일 등록을 실패했습니다: {ex.Message}";
        }
    }

    private async Task SavePdfAnalysisAsync(string sourceFileName, PdfOcrResult analysis)
    {
        try
        {
            Directory.CreateDirectory(sourceAnalysisDirectory);

            var safeFileName = GetSafeFileNameWithoutExtension(sourceFileName);
            var outputPath = Path.Combine(sourceAnalysisDirectory, $"{safeFileName}.analysis.json");

            var json = JsonSerializer.Serialize(analysis, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            await File.WriteAllTextAsync(outputPath, json);
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

    private async Task<string?> SavePdfAnalysisDiagnosticsAsync(string sourceFileName, PdfAnalysisDiagnostics diagnostics)
    {
        try
        {
            Directory.CreateDirectory(sourceAnalysisDirectory);

            var safeFileName = GetSafeFileNameWithoutExtension(sourceFileName);
            var jsonPath = Path.Combine(sourceAnalysisDirectory, $"{safeFileName}.diagnostics.json");
            var textPath = Path.Combine(sourceAnalysisDirectory, $"{safeFileName}.diagnostics.txt");

            var json = JsonSerializer.Serialize(diagnostics, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            await File.WriteAllTextAsync(jsonPath, json);
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

        if (startIndex > endIndex)
        {
            errorMessage = "예상 문항 범위의 시작 번호는 끝 번호보다 클 수 없습니다.";
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
        if (Application.Current?.MainPage == null)
        {
            return true;
        }

        var message = $"'{sourceFileName}' 파일의 문항 {existingCount}개가 이미 등록되어 있습니다.\n" +
                      "덮어써서 새로 반영하시겠습니까?";

        return await Application.Current.MainPage.DisplayAlert(
            "문항 반영",
            message,
            "덮어쓰기",
            "취소");
    }

    private static async Task<bool> ConfirmOverwriteSourceFileAsync(string sourceFileName)
    {
        if (Application.Current?.MainPage == null)
        {
            return false;
        }

        var message = $"'{sourceFileName}' 파일이 이미 존재합니다.\n덮어쓰기 하시겠습니까?";

        return await Application.Current.MainPage.DisplayAlert(
            "문항 파일 등록",
            message,
            "덮어쓰기",
            "취소");
    }

    private async Task StartPracticeAsync()
    {
        if (SelectedCategory == null)
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

        var questions = await repository.GetQuestionsAsync(SelectedCategory.Id);
        var practiceCandidates = (IncludeUnansweredInPractice
                ? questions
                : questions.Where(x => x.CorrectAnswers.Any(a => !string.IsNullOrWhiteSpace(a))))
            .OrderBy(_ => random.Next())
            .Take(count)
            .ToList();

        if (practiceCandidates.Count == 0)
        {
            Feedback = IncludeUnansweredInPractice
                ? "출제 가능한 문항이 없습니다."
                : "정답이 등록된 문항이 없습니다.";
            return;
        }

        await StartWithQuestionsAsync(practiceCandidates);
        
    }

    private async Task StartWrongPracticeAsync()
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

    private async Task StartWithQuestionsAsync(IReadOnlyList<Question> questions)
    {
        if (questions.Count == 0)
        {
            Feedback = "출제 가능한 문제가 없습니다.";
            return;
        }

        currentSession = questions.OrderBy(_ => random.Next()).ToList();
        SessionTotalCount = currentSession.Count;
        SessionCurrentIndex = 0;
        CorrectCount = 0;
        IsPracticeRunning = true;
        UserAnswer = string.Empty;
        SessionFeedback = string.Empty;

        Feedback = "연습을 시작합니다.";
        SetCurrentQuestion(currentSession[0]);
        UpdatePracticeState();

        await ReloadWrongAsync();
    }

    private async Task SubmitAnswerAsync()
    {
        if (CurrentQuestion == null || !IsPracticeRunning)
        {
            return;
        }

        var question = CurrentQuestion;
        var hasCorrectAnswer = question.CorrectAnswers.Any(x => !string.IsNullOrWhiteSpace(x));
        if (!hasCorrectAnswer)
        {
            SessionFeedback = "정답이 등록되지 않은 문항입니다. 나중에 정답을 매핑한 뒤 채점 가능합니다.";
        }
        else
        {
            var isCorrect = AnswerComparer.IsCorrect(question, UserAnswer);

            if (isCorrect)
            {
                CorrectCount++;
                await repository.RemoveWrongAsync(question.Id);
                SessionFeedback = $"정답: {question.CorrectAnswerDisplay}";
            }
            else
            {
                await repository.MarkWrongAsync(question.Id);
                SessionFeedback = $"오답: 정답은 {question.CorrectAnswerDisplay} 입니다.";
            }
        }

        await ReloadWrongAsync();

        if (SessionCurrentIndex + 1 >= SessionTotalCount)
        {
            IsPracticeRunning = false;
            CurrentQuestion = null;
            CurrentQuestionImageSlices.Clear();
            OnPropertyChanged(nameof(CurrentQuestionSourceDisplay));
            OnPropertyChanged(nameof(CurrentQuestionText));
            OnPropertyChanged(nameof(CurrentQuestionChoicesText));
            OnPropertyChanged(nameof(HasCurrentQuestionImages));
            OnPropertyChanged(nameof(ShowCurrentQuestionText));
            SessionFeedback += $"\n총 {CorrectCount}/{SessionTotalCount}개 정답";
            UpdatePracticeState();
            return;
        }

        SessionCurrentIndex++;
        SetCurrentQuestion(currentSession[SessionCurrentIndex]);
        UserAnswer = string.Empty;
    }

    private void SetCurrentQuestion(Question question)
    {
        CurrentQuestion = question;
        UpdateCurrentQuestionImageSlices(question);
        OnPropertyChanged(nameof(CurrentQuestionSourceDisplay));
        OnPropertyChanged(nameof(CurrentQuestionText));
        OnPropertyChanged(nameof(CurrentQuestionChoicesText));
        OnPropertyChanged(nameof(HasCurrentQuestionImages));
        OnPropertyChanged(nameof(ShowCurrentQuestionText));
    }

    private void UpdateCurrentQuestionImageSlices(Question? question)
    {
        CurrentQuestionImageSlices.Clear();
        foreach (var slice in BuildQuestionImageSliceViewModels(question))
        {
            CurrentQuestionImageSlices.Add(slice);
        }
    }

    private IEnumerable<QuestionImageSliceViewModel> BuildQuestionImageSliceViewModels(Question? question)
    {
        if (question == null)
        {
            yield break;
        }

        foreach (var segment in ResolveStoredImageSegments(question))
        {
            var imageSource = BuildQuestionImageSource(segment.ImagePath);
            if (imageSource == null)
            {
                continue;
            }

            var left = Math.Clamp(segment.ImageLeftRatio, 0d, 1d);
            var top = Math.Clamp(segment.ImageTopRatio, 0d, 1d);
            var right = Math.Clamp(segment.ImageRightRatio, 0d, 1d);
            var bottom = Math.Clamp(segment.ImageBottomRatio, 0d, 1d);
            if (right <= left)
            {
                right = Math.Min(1d, left + MinQuestionImageSliceWidthRatio);
            }

            if (bottom <= top)
            {
                bottom = Math.Min(1d, top + MinQuestionImageSliceRatio);
            }

            var contentWidth = CurrentQuestionImageViewportWidth;
            var (imagePixelWidth, imagePixelHeight) = ResolveImagePixelSize(segment);
            if (imagePixelWidth <= 0 || imagePixelHeight <= 0)
            {
                imagePixelWidth = contentWidth;
                imagePixelHeight = CurrentQuestionImageViewportHeight;
            }

            var contentHeight = contentWidth * imagePixelHeight / imagePixelWidth;
            var visibleWidth = Math.Max(contentWidth * MinQuestionImageSliceWidthRatio, (right - left) * contentWidth);
            var visibleHeight = Math.Max(contentHeight * MinQuestionImageSliceRatio, (bottom - top) * contentHeight);

            yield return new QuestionImageSliceViewModel
            {
                ImageSource = imageSource,
                VisibleWidth = visibleWidth,
                VisibleHeight = visibleHeight,
                ContentWidth = contentWidth,
                ContentHeight = contentHeight,
                TranslationX = -left * contentWidth,
                TranslationY = -top * contentHeight
            };
        }
    }

    private static (double Width, double Height) ResolveImagePixelSize(QuestionImageSegment segment)
    {
        if (segment.ImagePixelWidth > 0 && segment.ImagePixelHeight > 0)
        {
            return (segment.ImagePixelWidth, segment.ImagePixelHeight);
        }

        return TryReadPngSize(segment.ImagePath, out var width, out var height)
            ? (width, height)
            : (0d, 0d);
    }

    private static bool TryReadPngSize(string? imagePath, out int width, out int height)
    {
        width = 0;
        height = 0;

        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            return false;
        }

        try
        {
            using var stream = File.OpenRead(imagePath);
            Span<byte> header = stackalloc byte[24];
            if (stream.Read(header) < header.Length)
            {
                return false;
            }

            Span<byte> pngSignature = stackalloc byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
            if (!header[..8].SequenceEqual(pngSignature))
            {
                return false;
            }

            width = BinaryPrimitives.ReadInt32BigEndian(header.Slice(16, 4));
            height = BinaryPrimitives.ReadInt32BigEndian(header.Slice(20, 4));
            return width > 0 && height > 0;
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<QuestionImageSegment> ResolveStoredImageSegments(Question question)
    {
        if (question.ImageSegments != null && question.ImageSegments.Length > 0)
        {
            return question.ImageSegments
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.ImagePath));
        }

        if (string.IsNullOrWhiteSpace(question.ImagePath))
        {
            return Array.Empty<QuestionImageSegment>();
        }

        return new[]
        {
            new QuestionImageSegment
            {
                PageIndex = 1,
                ImagePath = question.ImagePath,
                ImageTopRatio = question.ImageTopRatio,
                ImageBottomRatio = question.ImageBottomRatio
            }
        };
    }

    private static ImageSource? BuildQuestionImageSource(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            return null;
        }

        try
        {
            return ImageSource.FromStream(() => File.OpenRead(imagePath));
        }
        catch
        {
            return null;
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

        if (SelectedCategory == null)
        {
            Feedback = "카테고리를 선택해 주세요.";
            return;
        }

        if (IsPracticeRunning)
        {
            Feedback = "진행 중인 연습이 있어 파일을 삭제할 수 없습니다.";
            return;
        }

        var canDelete = await ConfirmDeleteSourceFileAsync(sourceFile.SourceFileName, sourceFile.QuestionCount);
        if (!canDelete)
        {
            Feedback = "문항 파일 삭제를 취소했습니다.";
            return;
        }

        var removed = await repository.RemoveQuestionsBySourceFileAsync(SelectedCategory.Id, sourceFile.SourceFileName);
        if (!removed)
        {
            Feedback = "삭제할 문제를 찾을 수 없습니다.";
            return;
        }

        Feedback = $"파일 '{sourceFile.SourceFileName}' 문항 {sourceFile.QuestionCount}개 삭제 완료";
        await UpdateSelectedCategoryQuestionCountAsync();
        await ReloadWrongAsync();
    }

    private static async Task<bool> ConfirmDeleteSourceFileAsync(string sourceFileName, int questionCount)
    {
        if (Application.Current?.MainPage == null)
        {
            return true;
        }

        var message = $"'{sourceFileName}'의 문항 {questionCount}개를 삭제하시겠습니까?";

        return await Application.Current.MainPage.DisplayAlert(
            "문항 파일 삭제",
            message,
            "삭제",
            "취소");
    }

    private async Task DeleteCategoryAsync()
    {
        if (SelectedCategory == null)
        {
            Feedback = "삭제할 카테고리가 없습니다.";
            return;
        }

        if (IsPracticeRunning)
        {
            Feedback = "진행 중인 연습이 있어 카테고리를 삭제할 수 없습니다.";
            return;
        }

        var target = SelectedCategory;
        var questionCount = selectedCategoryQuestionCount;
        var canDelete = await ConfirmDeleteAsync(target.Name, questionCount);
        if (!canDelete)
        {
            Feedback = "카테고리 삭제를 취소했습니다.";
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

    private static async Task<bool> ConfirmDeleteAsync(string categoryName, int questionCount)
    {
        if (Application.Current?.MainPage == null)
        {
            return true;
        }

        var message = $"'{categoryName}' 카테고리를 삭제하면 해당 카테고리의 문제와 오답 목록이 함께 삭제됩니다.\n" +
                      $"{questionCount}개 문항을 모두 삭제하시겠습니까?";

        return await Application.Current.MainPage.DisplayAlert(
            "카테고리 삭제",
            message,
            "삭제",
            "취소");
    }

    private async Task UpdateSelectedCategoryQuestionCountAsync()
    {
        if (SelectedCategory == null)
        {
            selectedCategoryQuestionCount = 0;
            selectedCategoryPracticeQuestionCount = 0;
            SourceFiles.Clear();
            SelectedSourceFile = null;
            OnPropertyChanged(nameof(SelectedCategoryQuestionCountText));
            OnPropertyChanged(nameof(MaxPracticeCount));
            UpdatePracticeState();
            return;
        }

        var questions = await repository.GetQuestionsAsync(SelectedCategory.Id);
        selectedCategoryQuestionCount = questions.Count;
        selectedCategoryPracticeQuestionCount = IncludeUnansweredInPractice
            ? selectedCategoryQuestionCount
            : questions.Count(x => x.CorrectAnswers.Any(a => !string.IsNullOrWhiteSpace(a)));
        await UpdateSourceFilesAsync();
        OnPropertyChanged(nameof(SelectedCategoryQuestionCountText));
        OnPropertyChanged(nameof(MaxPracticeCount));
        UpdatePracticeState();
    }

    private async Task UpdateSourceFilesAsync()
    {
        if (SelectedCategory == null)
        {
            SourceFiles.Clear();
            SelectedSourceFile = null;
            return;
        }

        var files = await repository.GetSourceFilesAsync(SelectedCategory.Id);
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

        var currentDir = Directory.GetCurrentDirectory();
        if (!string.IsNullOrWhiteSpace(currentDir))
        {
            directories.Add(currentDir);
            directories.Add(Path.Combine(currentDir, "src"));
        }

        var appDir = AppContext.BaseDirectory;
        if (!string.IsNullOrWhiteSpace(appDir))
        {
            directories.Add(appDir);
            directories.Add(Path.Combine(appDir, "src"));
            var projectRootCandidate = Path.GetFullPath(Path.Combine(appDir, "..", "..", "..", "..", ".."));
            directories.Add(Path.Combine(projectRootCandidate, "src"));
        }

        return directories.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private void UpdatePracticeState()
    {
        OnPropertyChanged(nameof(CanStartPractice));
        OnPropertyChanged(nameof(CanStartWrongPractice));
        OnPropertyChanged(nameof(ProgressDisplay));
        OnPropertyChanged(nameof(HasSelectedCategory));
        OnPropertyChanged(nameof(CanDeleteCategory));
        OnPropertyChanged(nameof(CanDeleteSourceFile));
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
            HasExpectedQuestionMismatch = hasExpectedQuestionMismatch,
            CandidateIndexes = candidateIndexes,
            MissingIndexes = missingIndexes,
            UnexpectedIndexes = unexpectedIndexes,
            DuplicateIndexes = duplicateIndexes,
            Candidates = rawCandidates
                .OrderBy(x => x.Index)
                .ThenBy(x => x.StartPage)
                .ThenBy(x => x.StartLineInPage)
                .Select(x => new PdfAnalysisCandidateDiagnostics
                {
                    Index = x.Index,
                    Header = x.Header,
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
            builder.AppendLine($"- {candidate.Index}번 | p{candidate.StartPage}:{candidate.StartLineInPage} -> p{candidate.EndPage}:{candidate.EndLineInPage} | {candidate.Header}");
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
        public bool HasExpectedQuestionMismatch { get; init; }
        public int[] CandidateIndexes { get; init; } = Array.Empty<int>();
        public int[] MissingIndexes { get; init; } = Array.Empty<int>();
        public int[] UnexpectedIndexes { get; init; } = Array.Empty<int>();
        public PdfAnalysisDuplicateDiagnostics[] DuplicateIndexes { get; init; } = Array.Empty<PdfAnalysisDuplicateDiagnostics>();
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
        public int StartPage { get; init; }
        public int StartLineInPage { get; init; }
        public int EndPage { get; init; }
        public int EndLineInPage { get; init; }
        public string PreviewText { get; init; } = string.Empty;
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
