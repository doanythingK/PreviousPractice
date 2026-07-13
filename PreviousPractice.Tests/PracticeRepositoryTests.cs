using System.Text.Json;
using PreviousPractice.Data;
using PreviousPractice.Models;

namespace PreviousPractice.Tests;

public sealed class PracticeRepositoryTests
{
    [Fact]
    public async Task FullOverwrite_PreservesMatchingIdsAndWrongHistory_AndRemovesOnlyMissingQuestions()
    {
        var (repository, directory) = CreateRepository();
        try
        {
            var category = await repository.AddOrGetCategoryAsync("테스트");
            await repository.SaveImportedQuestionsAsync(
                category.Id,
                "sample.pdf",
                [
                    Question(category.Id, 1, QuestionType.MultipleChoice, "1"),
                    Question(category.Id, 2, QuestionType.MultipleChoice, "2"),
                    Question(category.Id, 3, QuestionType.Subjective, "기존")
                ],
                overwriteBySourceFile: true);

            var before = await repository.GetQuestionsAsync(category.Id);
            var firstBefore = before.Single(question => question.Index == 1);
            var secondBefore = before.Single(question => question.Index == 2);
            var removedBefore = before.Single(question => question.Index == 3);
            await repository.MarkWrongAsync(firstBefore.Id);
            await repository.MarkWrongAsync(removedBefore.Id);

            var changedFirst = Question(category.Id, 1, QuestionType.Subjective, "새 정답");
            changedFirst.Prompt = "갱신된 첫 문항";
            changedFirst.Choices = ["새 선택지 A", "새 선택지 B"];
            var changedSecond = Question(category.Id, 2, QuestionType.Subjective, "둘째 새 정답");
            await repository.SaveImportedQuestionsAsync(
                category.Id,
                "sample.pdf",
                [changedFirst, changedSecond],
                overwriteBySourceFile: true);

            var after = await repository.GetQuestionsAsync(category.Id);
            Assert.Equal(2, after.Count);
            var firstAfter = after.Single(question => question.Index == 1);
            var secondAfter = after.Single(question => question.Index == 2);
            Assert.Equal(firstBefore.Id, firstAfter.Id);
            Assert.Equal(secondBefore.Id, secondAfter.Id);
            Assert.Equal("갱신된 첫 문항", firstAfter.Prompt);
            Assert.Equal(QuestionType.Subjective, firstAfter.Type);
            Assert.Equal(new[] { "새 선택지 A", "새 선택지 B" }, firstAfter.Choices);
            Assert.Equal(new[] { "새 정답" }, firstAfter.CorrectAnswers);
            Assert.DoesNotContain(after, question => question.Id == removedBefore.Id);

            var wrongQuestions = await repository.GetWrongQuestionsAsync();
            var preservedWrong = Assert.Single(wrongQuestions);
            Assert.Equal(firstBefore.Id, preservedWrong.Id);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PartialAnswerOverwrite_UpdatesStructureButPreservesUnspecifiedAnswerTypeIdAndWrongHistory()
    {
        var (repository, directory) = CreateRepository();
        try
        {
            var category = await repository.AddOrGetCategoryAsync("테스트");
            await repository.SaveImportedQuestionsAsync(
                category.Id,
                "sample.pdf",
                [
                    Question(category.Id, 1, QuestionType.MultipleChoice, "1"),
                    Question(category.Id, 2, QuestionType.MultipleChoice, "2")
                ],
                overwriteBySourceFile: true);

            var before = await repository.GetQuestionsAsync(category.Id);
            var firstBefore = before.Single(question => question.Index == 1);
            var secondBefore = before.Single(question => question.Index == 2);
            await repository.MarkWrongAsync(secondBefore.Id);

            var incomingFirst = Question(category.Id, 1, QuestionType.Subjective, "3");
            incomingFirst.Prompt = "갱신된 1번 구조";
            incomingFirst.Choices = ["새 1번 선택지"];
            var incomingSecond = Question(category.Id, 2, QuestionType.Subjective);
            incomingSecond.Prompt = "갱신된 2번 구조";
            incomingSecond.Choices = ["새 2번 선택지"];
            var incomingThird = Question(category.Id, 3, QuestionType.Subjective, "신규 정답");

            await repository.SaveImportedQuestionsAsync(
                category.Id,
                "sample.pdf",
                [incomingFirst, incomingSecond, incomingThird],
                overwriteBySourceFile: true,
                updateExistingCorrectAnswers: true,
                answerIndexesToUpdate: [1]);

            var after = await repository.GetQuestionsAsync(category.Id);
            var firstAfter = after.Single(question => question.Index == 1);
            var secondAfter = after.Single(question => question.Index == 2);

            Assert.Equal(firstBefore.Id, firstAfter.Id);
            Assert.Equal(new[] { "3" }, firstAfter.CorrectAnswers);
            Assert.Equal(QuestionType.Subjective, firstAfter.Type);
            Assert.Equal("갱신된 1번 구조", firstAfter.Prompt);
            Assert.Equal(new[] { "새 1번 선택지" }, firstAfter.Choices);

            Assert.Equal(secondBefore.Id, secondAfter.Id);
            Assert.Equal(new[] { "2" }, secondAfter.CorrectAnswers);
            Assert.Equal(QuestionType.MultipleChoice, secondAfter.Type);
            Assert.Equal("갱신된 2번 구조", secondAfter.Prompt);
            Assert.Equal(new[] { "새 2번 선택지" }, secondAfter.Choices);

            var thirdAfter = after.Single(question => question.Index == 3);
            Assert.Equal(new[] { "신규 정답" }, thirdAfter.CorrectAnswers);
            Assert.Equal(QuestionType.Subjective, thirdAfter.Type);

            var wrongQuestion = Assert.Single(await repository.GetWrongQuestionsAsync());
            Assert.Equal(secondBefore.Id, wrongQuestion.Id);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task OverwriteWithNullAnswerIndexSet_KeepsFullOverwriteAnswerAndTypeContract()
    {
        var (repository, directory) = CreateRepository();
        try
        {
            var category = await repository.AddOrGetCategoryAsync("테스트");
            await repository.SaveImportedQuestionsAsync(
                category.Id,
                "sample.pdf",
                [Question(category.Id, 1, QuestionType.MultipleChoice, "1")],
                overwriteBySourceFile: true);

            var before = Assert.Single(await repository.GetQuestionsAsync(category.Id));
            await repository.SaveImportedQuestionsAsync(
                category.Id,
                "sample.pdf",
                [Question(category.Id, 1, QuestionType.Subjective, "전체 갱신")],
                overwriteBySourceFile: true,
                updateExistingCorrectAnswers: true,
                answerIndexesToUpdate: null);

            var after = Assert.Single(await repository.GetQuestionsAsync(category.Id));
            Assert.Equal(before.Id, after.Id);
            Assert.Equal(new[] { "전체 갱신" }, after.CorrectAnswers);
            Assert.Equal(QuestionType.Subjective, after.Type);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PartialAnswerOverwrite_ExplicitBlankClearsOnlySelectedAnswerAndPreservesTypeIdAndWrongHistory()
    {
        var (repository, directory) = CreateRepository();
        try
        {
            var category = await repository.AddOrGetCategoryAsync("테스트");
            await repository.SaveImportedQuestionsAsync(
                category.Id,
                "sample.pdf",
                [
                    Question(category.Id, 1, QuestionType.MultipleChoice, "1"),
                    Question(category.Id, 2, QuestionType.Subjective, "2")
                ],
                overwriteBySourceFile: true);

            var before = await repository.GetQuestionsAsync(category.Id);
            var firstBefore = before.Single(question => question.Index == 1);
            var secondBefore = before.Single(question => question.Index == 2);
            await repository.MarkWrongAsync(secondBefore.Id);

            var incomingFirst = Question(category.Id, 1, QuestionType.Subjective);
            var incomingSecond = Question(category.Id, 2, QuestionType.MultipleChoice);
            incomingSecond.Prompt = "빈 정답으로 갱신된 2번 구조";

            await repository.SaveImportedQuestionsAsync(
                category.Id,
                "sample.pdf",
                [incomingFirst, incomingSecond],
                overwriteBySourceFile: true,
                updateExistingCorrectAnswers: true,
                answerIndexesToUpdate: [2]);

            var after = await repository.GetQuestionsAsync(category.Id);
            var firstAfter = after.Single(question => question.Index == 1);
            var secondAfter = after.Single(question => question.Index == 2);

            Assert.Equal(firstBefore.Id, firstAfter.Id);
            Assert.Equal(new[] { "1" }, firstAfter.CorrectAnswers);
            Assert.Equal(QuestionType.MultipleChoice, firstAfter.Type);

            Assert.Equal(secondBefore.Id, secondAfter.Id);
            Assert.Empty(secondAfter.CorrectAnswers);
            Assert.Equal(QuestionType.Subjective, secondAfter.Type);
            Assert.Equal("빈 정답으로 갱신된 2번 구조", secondAfter.Prompt);

            var wrongQuestion = Assert.Single(await repository.GetWrongQuestionsAsync());
            Assert.Equal(secondBefore.Id, wrongQuestion.Id);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MultipleInstances_SharingAStore_DoNotLoseConcurrentWritesOrLeaveTempFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"previous-practice-{Guid.NewGuid():N}");
        var filePath = Path.Combine(directory, "practice-store.json");
        var firstRepository = new PracticeRepository(filePath);
        var secondRepository = new PracticeRepository(filePath);

        try
        {
            var category = await firstRepository.AddOrGetCategoryAsync("동시성");
            var writes = Enumerable.Range(1, 24)
                .Select(index =>
                {
                    var repository = index % 2 == 0 ? firstRepository : secondRepository;
                    return repository.SaveImportedQuestionsAsync(
                        category.Id,
                        $"source-{index}.pdf",
                        [Question(category.Id, 1, QuestionType.MultipleChoice, index.ToString())],
                        overwriteBySourceFile: true);
                });

            await Task.WhenAll(writes);

            var saved = await secondRepository.GetQuestionsAsync(category.Id);
            Assert.Equal(24, saved.Count);
            Assert.Equal(24, saved.Select(question => question.SourceFileName).Distinct().Count());
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PartialAnswerUpdate_PreservesUnspecifiedExistingAnswers()
    {
        var (repository, directory) = CreateRepository();
        try
        {
            var category = await repository.AddOrGetCategoryAsync("테스트");
            await repository.SaveImportedQuestionsAsync(
                category.Id,
                "sample.pdf",
                [
                    Question(category.Id, 1, QuestionType.MultipleChoice, "1"),
                    Question(category.Id, 2, QuestionType.Subjective, "기존"),
                    Question(category.Id, 3, QuestionType.MultipleChoice, "3")
                ],
                overwriteBySourceFile: true);

            await repository.SaveImportedQuestionsAsync(
                category.Id,
                "sample.pdf",
                [
                    Question(category.Id, 1, QuestionType.MultipleChoice),
                    Question(category.Id, 2, QuestionType.Subjective, "변경"),
                    Question(category.Id, 3, QuestionType.MultipleChoice)
                ],
                overwriteBySourceFile: false,
                updateExistingCorrectAnswers: true,
                answerIndexesToUpdate: [2]);

            var saved = await repository.GetQuestionsAsync(category.Id);
            Assert.Equal(new[] { "1" }, saved.Single(question => question.Index == 1).CorrectAnswers);
            Assert.Equal(new[] { "변경" }, saved.Single(question => question.Index == 2).CorrectAnswers);
            Assert.Equal(new[] { "3" }, saved.Single(question => question.Index == 3).CorrectAnswers);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ExistingSourceWithoutOverwrite_UpdatesAnswersButDoesNotAddNewCropStructure()
    {
        var (repository, directory) = CreateRepository();
        try
        {
            var category = await repository.AddOrGetCategoryAsync("테스트");
            var existing = Question(category.Id, 1, QuestionType.MultipleChoice, "1");
            existing.Prompt = "기존 1번 이미지 구조";
            await repository.SaveImportedQuestionsAsync(
                category.Id,
                "sample.pdf",
                [existing],
                overwriteBySourceFile: true);

            var incomingFirst = Question(category.Id, 1, QuestionType.MultipleChoice, "2");
            incomingFirst.Prompt = "재분석된 1번 이미지 구조";
            var incomingSecond = Question(category.Id, 2, QuestionType.MultipleChoice, "3");
            incomingSecond.Prompt = "새로 검출된 2번 이미지 구조";

            var result = await repository.SaveImportedQuestionsAsync(
                category.Id,
                "sample.pdf",
                [incomingFirst, incomingSecond],
                overwriteBySourceFile: false,
                updateExistingCorrectAnswers: true,
                answerIndexesToUpdate: [1, 2]);

            var saved = Assert.Single(await repository.GetQuestionsAsync(category.Id));
            Assert.Equal(1, saved.Index);
            Assert.Equal("기존 1번 이미지 구조", saved.Prompt);
            Assert.Equal(new[] { "2" }, saved.CorrectAnswers);
            Assert.Equal(0, result.AddedQuestionCount);
            Assert.Equal(1, result.UpdatedQuestionCount);
            Assert.False(result.StructureChanged);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ExplicitBlankAnswer_ClearsOnlyTheSelectedQuestion()
    {
        var (repository, directory) = CreateRepository();
        try
        {
            var category = await repository.AddOrGetCategoryAsync("테스트");
            await repository.SaveImportedQuestionsAsync(
                category.Id,
                "sample.pdf",
                [
                    Question(category.Id, 1, QuestionType.Subjective, "유지"),
                    Question(category.Id, 2, QuestionType.Subjective, "삭제할 정답")
                ],
                overwriteBySourceFile: true);

            await repository.SaveImportedQuestionsAsync(
                category.Id,
                "sample.pdf",
                [Question(category.Id, 2, QuestionType.MultipleChoice)],
                overwriteBySourceFile: false,
                updateExistingCorrectAnswers: true,
                answerIndexesToUpdate: [2]);

            var saved = await repository.GetQuestionsAsync(category.Id);
            Assert.Equal(new[] { "유지" }, saved.Single(question => question.Index == 1).CorrectAnswers);
            var cleared = saved.Single(question => question.Index == 2);
            Assert.Empty(cleared.CorrectAnswers);
            Assert.Equal(QuestionType.Subjective, cleared.Type);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CorruptJson_IsBackedUpBeforeEmptyRecovery()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"previous-practice-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var filePath = Path.Combine(directory, "practice-store.json");
        await File.WriteAllTextAsync(filePath, "{ invalid json");
        var repository = new PracticeRepository(filePath);

        try
        {
            var categories = await repository.GetCategoriesAsync();
            var categoriesAfterReload = await repository.GetCategoriesAsync();

            Assert.Empty(categories);
            Assert.Empty(categoriesAfterReload);
            Assert.Single(Directory.GetFiles(directory, "practice-store.json.corrupt-*.json"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ZeroLengthStore_IsBackedUpBeforePersistingEmptyState()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"previous-practice-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var filePath = Path.Combine(directory, "practice-store.json");
        await File.WriteAllBytesAsync(filePath, []);
        var repository = new PracticeRepository(filePath);

        try
        {
            Assert.Empty(await repository.GetCategoriesAsync());

            var backupPath = Assert.Single(
                Directory.GetFiles(directory, "practice-store.json.corrupt-*.json"));
            Assert.Equal(0, new FileInfo(backupPath).Length);
            Assert.True(new FileInfo(filePath).Length > 0);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(filePath));
            Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);

            var reloaded = new PracticeRepository(filePath);
            Assert.Empty(await reloaded.GetCategoriesAsync());
            Assert.Single(Directory.GetFiles(directory, "practice-store.json.corrupt-*.json"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CorruptBackupFailure_DoesNotOverwriteTheOriginalStore()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"previous-practice-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var filePath = Path.Combine(directory, "practice-store.json");
        await File.WriteAllBytesAsync(filePath, []);
        var repository = new PracticeRepository(
            filePath,
            _ => Path.Combine(directory, "missing-directory", "backup.json"));

        try
        {
            await Assert.ThrowsAsync<IOException>(() => repository.GetCategoriesAsync());
            Assert.True(File.Exists(filePath));
            Assert.Equal(0, new FileInfo(filePath).Length);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Load_NormalizesNestedNullsDuplicateIdsAndInvalidIndexesWithoutDroppingQuestions()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"previous-practice-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var filePath = Path.Combine(directory, "practice-store.json");
        var duplicateQuestionId = Guid.NewGuid();
        var malformedState = new Dictionary<string, object?>
        {
            ["Categories"] = new object?[]
            {
                null,
                new { Id = string.Empty, Name = (string?)null },
                new { Id = "duplicate-category", Name = "첫 카테고리" },
                new { Id = "duplicate-category", Name = "둘째 카테고리" }
            },
            ["Questions"] = new object?[]
            {
                null,
                new
                {
                    Id = Guid.Empty,
                    Index = 0,
                    CategoryId = string.Empty,
                    SourceFileName = (string?)null,
                    Type = 99,
                    Prompt = (string?)null,
                    Choices = (string[]?)null,
                    CorrectAnswers = (string[]?)null,
                    ImageSegments = (object[]?)null
                },
                new
                {
                    Id = duplicateQuestionId,
                    Index = 1,
                    CategoryId = "duplicate-category",
                    SourceFileName = "sample.pdf",
                    Type = (int)QuestionType.MultipleChoice,
                    Prompt = "유지할 첫 문항",
                    Choices = new string?[] { "A", null, "C" },
                    CorrectAnswers = new string?[] { " 1 ", null, string.Empty },
                    ImageSegments = (object[]?)null
                },
                new
                {
                    Id = duplicateQuestionId,
                    Index = 1,
                    CategoryId = "duplicate-category",
                    SourceFileName = "sample.pdf",
                    Type = (int)QuestionType.Subjective,
                    Prompt = "유지할 둘째 문항",
                    Choices = (string[]?)null,
                    CorrectAnswers = (string[]?)null,
                    ImageSegments = (object[]?)null
                }
            },
            ["WrongQuestionIds"] = new string?[]
            {
                Guid.Empty.ToString(),
                duplicateQuestionId.ToString(),
                duplicateQuestionId.ToString(),
                "not-a-guid",
                null
            }
        };
        await File.WriteAllTextAsync(filePath, JsonSerializer.Serialize(malformedState));
        var repository = new PracticeRepository(filePath);

        try
        {
            var categories = await repository.GetCategoriesAsync();
            Assert.Equal(3, categories.Count);
            Assert.All(categories, category =>
            {
                Assert.False(string.IsNullOrWhiteSpace(category.Id));
                Assert.NotNull(category.Name);
            });
            Assert.Equal(categories.Count, categories.Select(category => category.Id).Distinct().Count());

            var questionLists = await Task.WhenAll(
                categories.Select(category => repository.GetQuestionsAsync(category.Id)));
            var questions = questionLists.SelectMany(questionList => questionList).ToArray();
            Assert.Equal(3, questions.Length);
            Assert.All(questions, question =>
            {
                Assert.NotEqual(Guid.Empty, question.Id);
                Assert.True(question.Index > 0);
                Assert.NotNull(question.Choices);
                Assert.NotNull(question.CorrectAnswers);
                Assert.NotNull(question.ImageSegments);
                Assert.True(Enum.IsDefined(question.Type));
            });
            Assert.Equal(questions.Length, questions.Select(question => question.Id).Distinct().Count());
            Assert.All(
                questions.GroupBy(question => (question.CategoryId, question.SourceFileName)),
                group => Assert.Equal(group.Count(), group.Select(question => question.Index).Distinct().Count()));

            var preserved = questions.Single(question => question.Prompt == "유지할 첫 문항");
            Assert.Equal(new[] { "A", string.Empty, "C" }, preserved.Choices);
            Assert.Equal(new[] { "1" }, preserved.CorrectAnswers);
            Assert.Equal(3, (await repository.GetWrongQuestionsAsync()).Count);

            var stableIds = questions.Select(question => question.Id).OrderBy(id => id).ToArray();
            var reloadedRepository = new PracticeRepository(filePath);
            var reloadedLists = await Task.WhenAll(
                categories.Select(category => reloadedRepository.GetQuestionsAsync(category.Id)));
            Assert.Equal(
                stableIds,
                reloadedLists.SelectMany(questionList => questionList)
                    .Select(question => question.Id)
                    .OrderBy(id => id));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static (PracticeRepository Repository, string Directory) CreateRepository()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"previous-practice-{Guid.NewGuid():N}");
        var filePath = Path.Combine(directory, "practice-store.json");
        return (new PracticeRepository(filePath), directory);
    }

    private static Question Question(
        string categoryId,
        int index,
        QuestionType type,
        params string[] answers)
    {
        return new Question
        {
            CategoryId = categoryId,
            SourceFileName = "sample.pdf",
            Index = index,
            Prompt = $"문항 {index}",
            Type = type,
            CorrectAnswers = answers
        };
    }
}
