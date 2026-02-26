using PreviousPractice.Models;

namespace PreviousPractice.Data;

public interface IPracticeRepository
{
    Task<IReadOnlyList<Category>> GetCategoriesAsync();

    Task<Category> AddOrGetCategoryAsync(string name);

    Task<IReadOnlyList<Question>> GetQuestionsAsync(string categoryId);

    Task<bool> RemoveCategoryAsync(string categoryId);

    Task<IReadOnlyList<Question>> GetRandomQuestionsAsync(string categoryId, int count, bool includeWrongOnly = false);

    Task SaveImportedQuestionsAsync(string categoryId, string sourceFileName, IEnumerable<Question> questions, bool overwriteBySourceFile);

    Task<IReadOnlyList<Question>> GetWrongQuestionsAsync();

    Task MarkWrongAsync(Guid questionId);

    Task RemoveWrongAsync(Guid questionId);
}
