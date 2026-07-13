using System.Collections.Concurrent;
using System.Text.Json;
using PreviousPractice.Infrastructure;
using PreviousPractice.Models;

namespace PreviousPractice.Data;

public sealed class PracticeRepository : IPracticeRepository
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> StoreLocks =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions StateJsonOptions = new()
    {
        WriteIndented = true
    };

    private sealed class PracticeState
    {
        public List<Category> Categories { get; set; } = new();
        public List<Question> Questions { get; set; } = new();
        public List<string> WrongQuestionIds { get; set; } = new();
    }

    private readonly string _filePath;
    private readonly SemaphoreSlim _sync;
    private readonly Func<string, string> _corruptBackupPathFactory;
    private readonly Random _random = new();

    public PracticeRepository() : this(BuildDefaultStorePath())
    {
    }

    internal PracticeRepository(
        string filePath,
        Func<string, string>? corruptBackupPathFactory = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("저장소 파일 경로가 비어 있습니다.", nameof(filePath));
        }

        _filePath = Path.GetFullPath(filePath);
        _sync = StoreLocks.GetOrAdd(_filePath, static _ => new SemaphoreSlim(1, 1));
        _corruptBackupPathFactory = corruptBackupPathFactory ?? CreateCorruptBackupPath;
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static string BuildDefaultStorePath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PreviousPractice",
            "data",
            "practice-store.json");
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

    public async Task<ImportedQuestionsSaveResult> SaveImportedQuestionsAsync(
        string categoryId,
        string sourceFileName,
        IEnumerable<Question> questions,
        bool overwriteBySourceFile,
        bool updateExistingCorrectAnswers = false,
        IReadOnlyCollection<int>? answerIndexesToUpdate = null)
    {
        ArgumentNullException.ThrowIfNull(questions);
        var normalizedSourceFile = NormalizeSourceFileName(sourceFileName);
        var answerUpdateIndexes = answerIndexesToUpdate?.ToHashSet();
        var importedQuestions = questions
            .Where(incoming => incoming != null)
            .Select(incoming => CreateImportedQuestion(categoryId, normalizedSourceFile, incoming))
            .GroupBy(question => question.Index)
            .Select(group => group.Last())
            .ToArray();

        await _sync.WaitAsync().ConfigureAwait(false);
        try
        {
            var state = await LoadStateCoreAsync().ConfigureAwait(false);
            var addedQuestionCount = 0;
            var updatedQuestionCount = 0;
            var removedQuestionCount = 0;
            var structureChanged = false;

            if (overwriteBySourceFile)
            {
                var isPartialAnswerOverwrite = updateExistingCorrectAnswers &&
                                               answerUpdateIndexes != null;
                var targets = state.Questions
                    .Where(x => x.CategoryId == categoryId &&
                                string.Equals(x.SourceFileName, normalizedSourceFile, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var targetByIndex = targets.ToDictionary(question => question.Index);
                var importedIndexes = importedQuestions
                    .Select(question => question.Index)
                    .ToHashSet();

                foreach (var question in importedQuestions)
                {
                    if (targetByIndex.TryGetValue(question.Index, out var existing))
                    {
                        if (isPartialAnswerOverwrite)
                        {
                            ApplyQuestionStructure(existing, question);
                            if (answerUpdateIndexes!.Contains(question.Index))
                            {
                                existing.CorrectAnswers = question.CorrectAnswers.ToArray();
                                if (question.CorrectAnswers.Length > 0)
                                {
                                    existing.Type = question.Type;
                                }
                            }
                        }
                        else
                        {
                            ApplyQuestionContent(existing, question);
                        }

                        updatedQuestionCount++;
                        structureChanged = true;
                    }
                    else
                    {
                        state.Questions.Add(question);
                        addedQuestionCount++;
                        structureChanged = true;
                    }
                }

                var removedTargets = targets
                    .Where(question => !importedIndexes.Contains(question.Index))
                    .ToArray();
                state.Questions.RemoveAll(question => removedTargets.Contains(question));
                removedQuestionCount = removedTargets.Length;
                structureChanged |= removedQuestionCount > 0;
                CleanupWrongQuestions(state, removedTargets.Select(question => question.Id.ToString()));
            }
            else
            {
                var sourceAlreadyExists = state.Questions.Any(question =>
                    question.CategoryId == categoryId &&
                    string.Equals(
                        question.SourceFileName,
                        normalizedSourceFile,
                        StringComparison.OrdinalIgnoreCase));
                var isExistingAnswerOnlyUpdate =
                    sourceAlreadyExists && updateExistingCorrectAnswers;

                foreach (var question in importedQuestions)
                {
                    var existing = state.Questions.FirstOrDefault(x =>
                        x.CategoryId == categoryId &&
                        string.Equals(x.SourceFileName, normalizedSourceFile, StringComparison.OrdinalIgnoreCase) &&
                        x.Index == question.Index);

                    if (existing != null)
                    {
                        if (updateExistingCorrectAnswers)
                        {
                            var shouldUpdateAnswer = answerUpdateIndexes == null ||
                                                     answerUpdateIndexes.Contains(question.Index);
                            if (shouldUpdateAnswer)
                            {
                                existing.CorrectAnswers = question.CorrectAnswers;
                                if (question.CorrectAnswers.Length > 0)
                                {
                                    existing.Type = question.Type;
                                }

                                updatedQuestionCount++;
                            }
                        }
                        else
                        {
                            ApplyQuestionContent(existing, question);
                            updatedQuestionCount++;
                            structureChanged = true;
                        }

                        continue;
                    }

                    if (!isExistingAnswerOnlyUpdate)
                    {
                        state.Questions.Add(question);
                        addedQuestionCount++;
                        structureChanged = true;
                    }
                }
            }

            CleanupWrongQuestions(state, Array.Empty<string>());

            await SaveStateCoreAsync(state).ConfigureAwait(false);
            return new ImportedQuestionsSaveResult(
                addedQuestionCount,
                updatedQuestionCount,
                removedQuestionCount,
                structureChanged);
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

    private static Question CreateImportedQuestion(
        string categoryId,
        string sourceFileName,
        Question incoming)
    {
        var question = new Question
        {
            Index = incoming.Index,
            CategoryId = categoryId,
            SourceFileName = sourceFileName,
            Prompt = string.IsNullOrWhiteSpace(incoming.Prompt)
                ? $"문항 {incoming.Index}"
                : incoming.Prompt.Trim(),
            Type = Enum.IsDefined(incoming.Type)
                ? incoming.Type
                : QuestionType.MultipleChoice,
            Choices = NormalizeChoices(incoming.Choices),
            CorrectAnswers = NormalizeCorrectAnswers(incoming.CorrectAnswers),
            ImageSegments = CloneImageSegments(incoming),
            ImagePath = incoming.ImagePath,
            ImageTopRatio = incoming.ImageTopRatio,
            ImageBottomRatio = incoming.ImageBottomRatio
        };

        return question;
    }

    private static void ApplyQuestionContent(Question target, Question source)
    {
        ApplyQuestionStructure(target, source);
        target.Type = source.Type;
        target.CorrectAnswers = source.CorrectAnswers.ToArray();
    }

    private static void ApplyQuestionStructure(Question target, Question source)
    {
        target.Index = source.Index;
        target.CategoryId = source.CategoryId;
        target.SourceFileName = source.SourceFileName;
        target.Prompt = source.Prompt;
        target.Choices = source.Choices.ToArray();
        target.ImageSegments = CloneImageSegments(source);
        target.ImagePath = source.ImagePath;
        target.ImageTopRatio = source.ImageTopRatio;
        target.ImageBottomRatio = source.ImageBottomRatio;
    }

    private static string[] NormalizeChoices(string[]? choices)
    {
        return choices?
                   .Select(choice => choice ?? string.Empty)
                   .ToArray() ??
               Array.Empty<string>();
    }

    private static string[] NormalizeCorrectAnswers(string[]? correctAnswers)
    {
        return correctAnswers?
                   .Where(answer => !string.IsNullOrWhiteSpace(answer))
                   .Select(answer => answer.Trim())
                   .ToArray() ??
               Array.Empty<string>();
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
            return await RecoverCorruptStateAsync(
                    new InvalidDataException("저장소 파일이 비어 있습니다."))
                .ConfigureAwait(false);
        }

        try
        {
            var normalizedState = NormalizeState(
                JsonSerializer.Deserialize<PracticeState>(json),
                out var stateChanged);
            if (stateChanged)
            {
                await SaveStateCoreAsync(normalizedState).ConfigureAwait(false);
            }

            return normalizedState;
        }
        catch (JsonException ex)
        {
            return await RecoverCorruptStateAsync(ex).ConfigureAwait(false);
        }
    }

    private async Task<PracticeState> RecoverCorruptStateAsync(Exception exception)
    {
        var backupPath = BackupCorruptStateFile();
        AppLog.Error(
            nameof(PracticeRepository),
            $"손상 저장소를 백업한 뒤 새 상태로 복구합니다. backup={backupPath}",
            exception);

        var recoveredState = new PracticeState();
        await SaveStateCoreAsync(recoveredState).ConfigureAwait(false);
        return recoveredState;
    }

    private static PracticeState NormalizeState(PracticeState? state, out bool changed)
    {
        changed = false;
        if (state == null)
        {
            changed = true;
            return new PracticeState();
        }

        var categoryIdMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var usedCategoryIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalizedCategories = new List<Category>();
        if (state.Categories == null)
        {
            changed = true;
        }
        else
        {
            foreach (var category in state.Categories)
            {
                if (category == null)
                {
                    changed = true;
                    continue;
                }

                var originalId = category.Id ?? string.Empty;
                var normalizedId = originalId;
                if (string.IsNullOrWhiteSpace(normalizedId) || !usedCategoryIds.Add(normalizedId))
                {
                    normalizedId = CreateUniqueCategoryId(usedCategoryIds);
                    changed = true;
                }

                categoryIdMap.TryAdd(originalId, normalizedId);
                var normalizedName = category.Name ?? string.Empty;
                if (!string.Equals(category.Name, normalizedName, StringComparison.Ordinal))
                {
                    changed = true;
                }

                normalizedCategories.Add(new Category
                {
                    Id = normalizedId,
                    Name = normalizedName
                });
            }
        }

        var questionIdMap = new Dictionary<Guid, List<Guid>>();
        var usedQuestionIds = new HashSet<Guid>();
        var normalizedQuestions = new List<Question>();
        if (state.Questions == null)
        {
            changed = true;
        }
        else
        {
            foreach (var question in state.Questions)
            {
                if (question == null)
                {
                    changed = true;
                    continue;
                }

                var normalizedId = question.Id;
                if (normalizedId == Guid.Empty || !usedQuestionIds.Add(normalizedId))
                {
                    normalizedId = CreateUniqueQuestionId(usedQuestionIds);
                    changed = true;
                }

                if (!questionIdMap.TryGetValue(question.Id, out var mappedIds))
                {
                    mappedIds = new List<Guid>();
                    questionIdMap.Add(question.Id, mappedIds);
                }

                mappedIds.Add(normalizedId);

                var originalCategoryId = question.CategoryId ?? string.Empty;
                var normalizedCategoryId = categoryIdMap.TryGetValue(originalCategoryId, out var mappedCategoryId)
                    ? mappedCategoryId
                    : originalCategoryId;
                var normalizedSourceFileName = NormalizeSourceFileName(question.SourceFileName);
                var normalizedType = Enum.IsDefined(question.Type)
                    ? question.Type
                    : QuestionType.MultipleChoice;
                var normalizedChoices = NormalizeChoices(question.Choices);
                var normalizedCorrectAnswers = NormalizeCorrectAnswers(question.CorrectAnswers);
                var normalizedImageSegments = CloneImageSegments(question);
                var normalizedPrompt = question.Prompt ?? string.Empty;

                if (!string.Equals(question.CategoryId, normalizedCategoryId, StringComparison.Ordinal) ||
                    !string.Equals(question.SourceFileName, normalizedSourceFileName, StringComparison.Ordinal) ||
                    question.Type != normalizedType ||
                    question.Choices == null ||
                    !question.Choices.SequenceEqual(normalizedChoices) ||
                    question.CorrectAnswers == null ||
                    !question.CorrectAnswers.SequenceEqual(normalizedCorrectAnswers) ||
                    question.ImageSegments == null ||
                    question.ImageSegments.Length != normalizedImageSegments.Length ||
                    question.Prompt == null)
                {
                    changed = true;
                }

                normalizedQuestions.Add(new Question
                {
                    Id = normalizedId,
                    Index = question.Index,
                    CategoryId = normalizedCategoryId,
                    SourceFileName = normalizedSourceFileName,
                    Type = normalizedType,
                    Prompt = normalizedPrompt,
                    Choices = normalizedChoices,
                    CorrectAnswers = normalizedCorrectAnswers,
                    ImageSegments = normalizedImageSegments,
                    ImagePath = question.ImagePath,
                    ImageTopRatio = question.ImageTopRatio,
                    ImageBottomRatio = question.ImageBottomRatio
                });
            }
        }

        NormalizeQuestionIndexes(normalizedQuestions, ref changed);
        foreach (var question in normalizedQuestions.Where(question => string.IsNullOrWhiteSpace(question.Prompt)))
        {
            question.Prompt = $"문항 {question.Index}";
            changed = true;
        }

        var normalizedWrongQuestionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (state.WrongQuestionIds == null)
        {
            changed = true;
        }
        else
        {
            foreach (var rawId in state.WrongQuestionIds)
            {
                if (!Guid.TryParse(rawId, out var originalId) ||
                    !questionIdMap.TryGetValue(originalId, out var mappedIds))
                {
                    changed = true;
                    continue;
                }

                foreach (var mappedId in mappedIds)
                {
                    if (!normalizedWrongQuestionIds.Add(mappedId.ToString()))
                    {
                        changed = true;
                    }
                }

                if (mappedIds.Count != 1 || mappedIds[0] != originalId)
                {
                    changed = true;
                }
            }
        }

        return new PracticeState
        {
            Categories = normalizedCategories,
            Questions = normalizedQuestions,
            WrongQuestionIds = normalizedWrongQuestionIds.ToList()
        };
    }

    private static void NormalizeQuestionIndexes(IReadOnlyList<Question> questions, ref bool changed)
    {
        var reservedIndexes = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        foreach (var question in questions.Where(question => question.Index > 0))
        {
            GetReservedIndexes(reservedIndexes, question).Add(question.Index);
        }

        var retainedIndexes = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        foreach (var question in questions)
        {
            var groupKey = BuildQuestionGroupKey(question);
            var retained = retainedIndexes.TryGetValue(groupKey, out var existingRetained)
                ? existingRetained
                : retainedIndexes[groupKey] = new HashSet<int>();
            if (question.Index > 0 && retained.Add(question.Index))
            {
                continue;
            }

            var reserved = GetReservedIndexes(reservedIndexes, question);
            var replacementIndex = 1;
            while (reserved.Contains(replacementIndex))
            {
                replacementIndex++;
            }

            question.Index = replacementIndex;
            reserved.Add(replacementIndex);
            retained.Add(replacementIndex);
            changed = true;
        }
    }

    private static HashSet<int> GetReservedIndexes(
        IDictionary<string, HashSet<int>> reservedIndexes,
        Question question)
    {
        var groupKey = BuildQuestionGroupKey(question);
        if (!reservedIndexes.TryGetValue(groupKey, out var reserved))
        {
            reserved = new HashSet<int>();
            reservedIndexes.Add(groupKey, reserved);
        }

        return reserved;
    }

    private static string BuildQuestionGroupKey(Question question)
    {
        return $"{question.CategoryId}\u001f{question.SourceFileName.ToUpperInvariant()}";
    }

    private static string CreateUniqueCategoryId(ISet<string> usedCategoryIds)
    {
        string id;
        do
        {
            id = Guid.NewGuid().ToString("N");
        } while (!usedCategoryIds.Add(id));

        return id;
    }

    private static Guid CreateUniqueQuestionId(ISet<Guid> usedQuestionIds)
    {
        Guid id;
        do
        {
            id = Guid.NewGuid();
        } while (!usedQuestionIds.Add(id));

        return id;
    }

    private string BackupCorruptStateFile()
    {
        if (!File.Exists(_filePath))
        {
            throw new FileNotFoundException("백업할 손상 저장소 파일이 없습니다.", _filePath);
        }

        var backupPath = _corruptBackupPathFactory(_filePath);
        var backupCreated = false;
        try
        {
            using var sourceStream = new FileStream(
                _filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            using var backupStream = new FileStream(
                backupPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
            backupCreated = true;
            sourceStream.CopyTo(backupStream);
            backupStream.Flush(flushToDisk: true);
            return backupPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            if (backupCreated)
            {
                TryDeleteFile(backupPath);
            }

            throw new IOException("손상 저장소 백업에 실패하여 원본을 보존합니다.", ex);
        }
    }

    private static string CreateCorruptBackupPath(string filePath)
    {
        return $"{filePath}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.json";
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
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempFilePath = Path.Combine(
            directory ?? string.Empty,
            $".{Path.GetFileName(_filePath)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             tempFilePath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, state, StateJsonOptions).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempFilePath, _filePath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(tempFilePath);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 실패한 임시/부분 백업 정리는 원래 예외를 가리지 않음
        }
    }
}
