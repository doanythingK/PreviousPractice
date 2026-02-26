using System.Collections.ObjectModel;
using System.IO;
using PreviousPractice.Core;
using PreviousPractice.Data;
using PreviousPractice.Infrastructure;
using PreviousPractice.Models;
using PreviousPractice.Services;
using Microsoft.Maui.Storage;

namespace PreviousPractice.ViewModels;

public class MainViewModel : ViewModelBase
{
    private readonly IPracticeRepository repository;
    private readonly Random random = new();
    private string newCategoryName = string.Empty;
    private string sourceFileName = "manual.txt";
    private string answerMapText = string.Empty;
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
    private SourceFileSummary? selectedSourceFile;
    private IReadOnlyList<Question> currentSession = Array.Empty<Question>();

    public ObservableCollection<Category> Categories { get; } = new();
    public ObservableCollection<SourceFileSummary> SourceFiles { get; } = new();
    public ObservableCollection<Question> WrongQuestions { get; } = new();

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

    public string SourceFileName
    {
        get => sourceFileName;
        set => SetProperty(ref sourceFileName, value);
    }

    public string AnswerMapText
    {
        get => answerMapText;
        set => SetProperty(ref answerMapText, value);
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

    public QuestionType SelectedQuestionType { get; set; } = QuestionType.MultipleChoice;

    public Array QuestionTypes => Enum.GetValues<QuestionType>();

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

    public int MaxPracticeCount => selectedCategoryQuestionCount;

    public int WrongQuestionCount => WrongQuestions.Count;

    public bool CanAddCategory => !string.IsNullOrWhiteSpace(NewCategoryName);

    public bool HasSelectedCategory => SelectedCategory != null;

    public bool CanDeleteCategory => SelectedCategory != null && !IsPracticeRunning;

    public bool CanDeleteSourceFile => SelectedSourceFile != null && SelectedCategory != null && !IsPracticeRunning;

    public bool CanStartPractice =>
        HasSelectedCategory &&
        !IsPracticeRunning &&
        int.TryParse(PracticeCountText, out var count) && count > 0 &&
        count <= MaxPracticeCount;

    public bool CanStartWrongPractice => !IsPracticeRunning && WrongQuestionCount > 0;

    public string ProgressDisplay =>
        IsPracticeRunning
            ? $"{SessionCurrentIndex + 1}/{SessionTotalCount}"
            : string.Empty;

    public string CurrentQuestionText => CurrentQuestion?.Prompt ?? string.Empty;

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

    public MainViewModel() : this(new PracticeRepository())
    {
    }

    public MainViewModel(IPracticeRepository repository)
    {
        this.repository = repository;
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

        _ = LoadAsync();
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

    private async Task LoadAsync()
    {
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

        var result = QuestionSetParser.ParseAnswerMapWithDetails(AnswerMapText, SelectedQuestionType);
        if (result.IsEmpty)
        {
            Feedback = result.Message;
            return;
        }

        var normalizedSourceFileName = NormalizeSourceFileName(SourceFileName);
        var shouldOverwrite = OverwriteExisting;
        if (!shouldOverwrite)
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

        var questions = result.Questions
            .Select(x =>
            {
                x.CategoryId = SelectedCategory.Id;
                x.SourceFileName = normalizedSourceFileName;
                x.Type = SelectedQuestionType;
                x.Prompt = string.IsNullOrWhiteSpace(x.Prompt)
                    ? $"{SelectedCategory.Name} - 문항 {x.Index}"
                    : x.Prompt;
                return x;
            })
            .ToArray();

        await repository.SaveImportedQuestionsAsync(
            SelectedCategory.Id,
            questions.Length == 0 ? "manual" : normalizedSourceFileName,
            questions,
            overwriteBySourceFile: shouldOverwrite);

        var summary = result.HasErrors
            ? $"{questions.Length}개 저장(일부 파싱 오류: {string.Join(", ", result.Errors)})"
            : $"{questions.Length}개 저장 완료";

        Feedback = summary;
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

            await using var stream = await file.OpenReadAsync();
            using var reader = new StreamReader(stream);
            var content = await reader.ReadToEndAsync();
            AnswerMapText = content;

            var fileBaseName = Path.GetFileNameWithoutExtension(file.FileName);
            if (!string.IsNullOrWhiteSpace(fileBaseName))
            {
                SourceFileName = fileBaseName;
            }

            Feedback = $"정답 파일을 불러왔습니다: {file.Name}";
        }
        catch (Exception ex)
        {
            Feedback = $"정답 파일을 불러오지 못했습니다: {ex.Message}";
        }
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

        var questions = await repository.GetRandomQuestionsAsync(SelectedCategory.Id, count);
        await StartWithQuestionsAsync(questions);
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

        await ReloadWrongAsync();

        if (SessionCurrentIndex + 1 >= SessionTotalCount)
        {
            IsPracticeRunning = false;
            CurrentQuestion = null;
            OnPropertyChanged(nameof(CurrentQuestionText));
            OnPropertyChanged(nameof(CurrentQuestionChoicesText));
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
        OnPropertyChanged(nameof(CurrentQuestionText));
        OnPropertyChanged(nameof(CurrentQuestionChoicesText));
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
            SourceFiles.Clear();
            SelectedSourceFile = null;
            OnPropertyChanged(nameof(SelectedCategoryQuestionCountText));
            OnPropertyChanged(nameof(MaxPracticeCount));
            UpdatePracticeState();
            return;
        }

        var questions = await repository.GetQuestionsAsync(SelectedCategory.Id);
        selectedCategoryQuestionCount = questions.Count;
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

    private void UpdatePracticeState()
    {
        OnPropertyChanged(nameof(CanStartPractice));
        OnPropertyChanged(nameof(CanStartWrongPractice));
        OnPropertyChanged(nameof(ProgressDisplay));
        OnPropertyChanged(nameof(HasSelectedCategory));
        OnPropertyChanged(nameof(CanDeleteCategory));
        OnPropertyChanged(nameof(CanDeleteSourceFile));
    }

    private static string NormalizeSourceFileName(string sourceFileName)
    {
        return string.IsNullOrWhiteSpace(sourceFileName) ? "manual" : sourceFileName.Trim();
    }
}
