namespace PastQuestionPractice.Models;

public class Category
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
}
