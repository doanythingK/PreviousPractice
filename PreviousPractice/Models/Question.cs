namespace PreviousPractice.Models;

public class Question
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public int Index { get; set; }
    public string CategoryId { get; set; } = string.Empty;
    public string SourceFileName { get; set; } = string.Empty;
    public QuestionType Type { get; set; }
    public string Prompt { get; set; } = string.Empty;
    public string[] Choices { get; set; } = Array.Empty<string>();
    public string[] CorrectAnswers { get; set; } = Array.Empty<string>();
    public QuestionImageSegment[] ImageSegments { get; set; } = Array.Empty<QuestionImageSegment>();
    public string? ImagePath { get; set; }
    public double ImageTopRatio { get; set; }
    public double ImageBottomRatio { get; set; } = 1d;

    public string CorrectAnswerDisplay => string.Join(", ", CorrectAnswers);
}
