using System.Text.Json;
using PreviousPractice.Models;

namespace PreviousPractice.Data;

public sealed class PracticeRepository : IPracticeRepository
{
    private sealed class PracticeState
    {
        public List<Category> Categories { get; set; } = new();
        public List<Question> Questions { get; set; } = new();
        public List<string> WrongQuestionIds { get; set; } = new();
    }

    private readonly string _filePath;
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly Random _random = new();

    public PracticeRepository()
    {
        var basePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PreviousPractice",
            "data");
        Directory.CreateDirectory(basePath);
        _filePath = Path.Combine(basePath, "practice-store.json");
    }

    public async Task<IReadOnlyList<Category>> GetCategoriesAsync()
    {
        await _sync.WaitAsync().ConfigureAwait(false);
        try
        {
            var state = await LoadStateCoreAsync().ConfigureAwait(false);
            return state.Categories
                .OrderBy(x => x.Name)
                .ToList();
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task<Category> AddOrGetCategoryAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("카테고리 이름을 입력해 주세요.", nameof(name));
        }

        var trimmedName = name.Trim();
        await _sync.WaitAsync().ConfigureAwait(false);
        try
        {
            var state = await LoadStateCoreAsync().ConfigureAwait(false);
            var existing = state.Categories.FirstOrDefault(x =>
                string.Equals(x.Name, trimmedName, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                return existing;
            }

            var category = new Category
            {
                Name = trimmedName
            };
            state.Categories.Add(category);
            await SaveStateCoreAsync(state).ConfigureAwait(false);
            return category;
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task<IReadOnlyList<Question>> GetQuestionsAsync(string categoryId)
    {
        await _sync.WaitAsync().ConfigureAwait(false);
        try
        {
            var state = await LoadStateCoreAsync().ConfigureAwait(false);
            return state.Questions
                .Where(x => x.CategoryId == categoryId)
                .OrderBy(x => x.Index)
                .ToList();
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task<bool> RemoveCategoryAsync(string categoryId)
    {
        await _sync.WaitAsync().ConfigureAwait(false);
        try
        {
            var state = await LoadStateCoreAsync().ConfigureAwait(false);
            var target = state.Categories.FirstOrDefault(x => x.Id == categoryId);
            if (target == null)
            {
                return false;
            }

            state.Categories.Remove(target);
            state.Questions.RemoveAll(x => x.CategoryId == categoryId);
            var questionIds = state.Questions
                .Select(x => x.Id.ToString())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            state.WrongQuestionIds.RemoveAll(x => !questionIds.Contains(x, StringComparer.OrdinalIgnoreCase));

            await SaveStateCoreAsync(state).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task<IReadOnlyList<Question>> GetRandomQuestionsAsync(string categoryId, int count, bool includeWrongOnly = false)
    {
        if (count <= 0)
        {
            return Array.Empty<Question>();
        }

        await _sync.WaitAsync().ConfigureAwait(false);
        try
        {
            var state = await LoadStateCoreAsync().ConfigureAwait(false);

            var questionList = state.Questions
                .Where(x => x.CategoryId == categoryId)
                .ToList();

            if (includeWrongOnly)
            {
                var wrongIds = state.WrongQuestionIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
                questionList = questionList
                    .Where(x => wrongIds.Contains(x.Id.ToString()))
                    .ToList();
            }

            if (questionList.Count == 0)
            {
                return Array.Empty<Question>();
            }

            return questionList
                .OrderBy(_ => _random.Next())
                .Take(Math.Min(count, questionList.Count))
                .ToList();
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task<int> GetQuestionCountBySourceFileAsync(string categoryId, string sourceFileName)
    {
        var normalizedSourceFile = NormalizeSourceFileName(sourceFileName);
        await _sync.WaitAsync().ConfigureAwait(false);
        try
        {
            var state = await LoadStateCoreAsync().ConfigureAwait(false);
            return state.Questions.Count(x =>
                x.CategoryId == categoryId &&
                string.Equals(x.SourceFileName, normalizedSourceFile, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task<IReadOnlyList<SourceFileSummary>> GetSourceFilesAsync(string categoryId)
    {
        await _sync.WaitAsync().ConfigureAwait(false);
        try
        {
            var state = await LoadStateCoreAsync().ConfigureAwait(false);
            return state.Questions
                .Where(x => x.CategoryId == categoryId)
                .GroupBy(x => x.SourceFileName)
                .Select(group => new SourceFileSummary
                {
                    SourceFileName = group.Key,
                    QuestionCount = group.Count()
                })
                .OrderBy(x => x.SourceFileName)
                .ToList();
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task<bool> RemoveQuestionsBySourceFileAsync(string categoryId, string sourceFileName)
    {
        var normalizedSourceFile = NormalizeSourceFileName(sourceFileName);

        await _sync.WaitAsync().ConfigureAwait(false);
        try
        {
            var state = await LoadStateCoreAsync().ConfigureAwait(false);
            var target = state.Questions.Where(x =>
                x.CategoryId == categoryId &&
                string.Equals(x.SourceFileName, normalizedSourceFile, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (target.Count == 0)
            {
                return false;
            }

            var removedQuestionIds = target.Select(x => x.Id.ToString()).ToList();
            state.Questions.RemoveAll(x =>
                x.CategoryId == categoryId &&
                string.Equals(x.SourceFileName, normalizedSourceFile, StringComparison.OrdinalIgnoreCase));

            CleanupWrongQuestions(state, removedQuestionIds);
            await SaveStateCoreAsync(state).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task SaveImportedQuestionsAsync(
        string categoryId,
        string sourceFileName,
        IEnumerable<Question> questions,
        bool overwriteBySourceFile,
        bool updateExistingCorrectAnswers = false)
    {
        var normalizedSourceFile = NormalizeSourceFileName(sourceFileName);

        await _sync.WaitAsync().ConfigureAwait(false);
        try
        {
            var state = await LoadStateCoreAsync().ConfigureAwait(false);
            var removedQuestionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (overwriteBySourceFile)
            {
                var targets = state.Questions
                    .Where(x => x.CategoryId == categoryId &&
                                string.Equals(x.SourceFileName, normalizedSourceFile, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                removedQuestionIds.UnionWith(targets.Select(x => x.Id.ToString()));
                state.Questions.RemoveAll(x => x.CategoryId == categoryId &&
                                                string.Equals(x.SourceFileName, normalizedSourceFile, StringComparison.OrdinalIgnoreCase));
                CleanupWrongQuestions(state, removedQuestionIds);
            }

            foreach (var incoming in questions)
            {
                if (incoming == null)
                {
                    continue;
                }

                var question = new Question
                {
                    Index = incoming.Index,
                    CategoryId = categoryId,
                    SourceFileName = normalizedSourceFile,
                    Prompt = string.IsNullOrWhiteSpace(incoming.Prompt)
                        ? string.Empty
                        : incoming.Prompt.Trim(),
                    Type = incoming.Type,
                    Choices = incoming.Choices?.ToArray() ?? Array.Empty<string>(),
                    CorrectAnswers = incoming.CorrectAnswers?.ToArray() ?? Array.Empty<string>(),
                    ImageSegments = CloneImageSegments(incoming),
                    ImagePath = incoming.ImagePath,
                    ImageTopRatio = incoming.ImageTopRatio,
                    ImageBottomRatio = incoming.ImageBottomRatio
                };

                if (string.IsNullOrWhiteSpace(question.Prompt))
                {
                    question.Prompt = $"문항 {question.Index}";
                }

                question.CorrectAnswers = question.CorrectAnswers
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x.Trim())
                    .ToArray();

                var existing = state.Questions.FirstOrDefault(x =>
                    x.CategoryId == categoryId &&
                    string.Equals(x.SourceFileName, normalizedSourceFile, StringComparison.OrdinalIgnoreCase) &&
                    x.Index == question.Index);

                if (!overwriteBySourceFile && existing != null)
                {
                    if (!string.IsNullOrWhiteSpace(question.Prompt))
                    {
                        existing.Prompt = question.Prompt;
                    }

                    var hadCorrectAnswers = existing.CorrectAnswers.Any();
                    existing.ImageSegments = CloneImageSegments(question);
                    existing.ImagePath = question.ImagePath;
                    existing.ImageTopRatio = question.ImageTopRatio;
                    existing.ImageBottomRatio = question.ImageBottomRatio;

                    if (updateExistingCorrectAnswers)
                    {
                        existing.CorrectAnswers = question.CorrectAnswers;
                        if (question.CorrectAnswers.Length > 0 || !hadCorrectAnswers)
                        {
                            existing.Type = question.Type;
                        }
                    }
                    else
                    {
                        existing.Type = question.Type;
                    }

                    existing.Choices = question.Choices;
                    continue;
                }

                state.Questions.Add(question);
            }

            CleanupWrongQuestions(state, Array.Empty<string>());

            await SaveStateCoreAsync(state).ConfigureAwait(false);
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task<IReadOnlyList<Question>> GetWrongQuestionsAsync()
    {
        await _sync.WaitAsync().ConfigureAwait(false);
        try
        {
            var state = await LoadStateCoreAsync().ConfigureAwait(false);
            var wrongSet = state.WrongQuestionIds
                .Where(x => Guid.TryParse(x, out _))
                .Select(Guid.Parse)
                .ToHashSet();

            return state.Questions
                .Where(x => wrongSet.Contains(x.Id))
                .OrderBy(x => x.SourceFileName)
                .ThenBy(x => x.Index)
                .ToList();
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task MarkWrongAsync(Guid questionId)
    {
        await _sync.WaitAsync().ConfigureAwait(false);
        try
        {
            var state = await LoadStateCoreAsync().ConfigureAwait(false);
            var key = questionId.ToString();
            if (!state.WrongQuestionIds.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                state.WrongQuestionIds.Add(key);
                await SaveStateCoreAsync(state).ConfigureAwait(false);
            }
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task RemoveWrongAsync(Guid questionId)
    {
        await _sync.WaitAsync().ConfigureAwait(false);
        try
        {
            var state = await LoadStateCoreAsync().ConfigureAwait(false);
            var key = questionId.ToString();
            if (state.WrongQuestionIds.RemoveAll(x => string.Equals(x, key, StringComparison.OrdinalIgnoreCase)) > 0)
            {
                await SaveStateCoreAsync(state).ConfigureAwait(false);
            }
        }
        finally
        {
            _sync.Release();
        }
    }

    private static QuestionImageSegment[] CloneImageSegments(Question question)
    {
        if (question.ImageSegments != null && question.ImageSegments.Length > 0)
        {
            return question.ImageSegments
                .Where(x => x != null)
                .Select(x => new QuestionImageSegment
                {
                    PageIndex = x.PageIndex,
                    ImagePath = x.ImagePath,
                    ImageLeftRatio = x.ImageLeftRatio,
                    ImageTopRatio = x.ImageTopRatio,
                    ImageRightRatio = x.ImageRightRatio,
                    ImageBottomRatio = x.ImageBottomRatio,
                    ImagePixelWidth = x.ImagePixelWidth,
                    ImagePixelHeight = x.ImagePixelHeight
                })
                .ToArray();
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

    private async Task<PracticeState> LoadStateCoreAsync()
    {
        if (!File.Exists(_filePath))
        {
            return new PracticeState();
        }

        var json = await File.ReadAllTextAsync(_filePath).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new PracticeState();
        }

        var state = JsonSerializer.Deserialize<PracticeState>(json);
        return state ?? new PracticeState();
    }

    private static void CleanupWrongQuestions(PracticeState state, IEnumerable<string> removedQuestionIds)
    {
        var removedSet = removedQuestionIds
            .Where(x => Guid.TryParse(x, out _))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var existingQuestionIds = state.Questions
            .Select(x => x.Id.ToString())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        state.WrongQuestionIds.RemoveAll(x =>
            removedSet.Contains(x) || !existingQuestionIds.Contains(x, StringComparer.OrdinalIgnoreCase));
    }

    private static string NormalizeSourceFileName(string sourceFileName)
    {
        return string.IsNullOrWhiteSpace(sourceFileName) ? "manual" : sourceFileName.Trim();
    }

    private async Task SaveStateCoreAsync(PracticeState state)
    {
        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempFilePath = _filePath + ".tmp";
        await File.WriteAllTextAsync(tempFilePath, json).ConfigureAwait(false);
        File.Move(tempFilePath, _filePath, overwrite: true);
    }
}
