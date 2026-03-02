namespace PreviousPractice.Models;

public class PracticeQuestion
{
    public string CategoryId { get; init; } = string.Empty;
    public Question Question { get; init; } = new();
    public string UserAnswer { get; set; } = string.Empty;
    public bool IsCorrect { get; set; }
}
