namespace PreviousPractice.Models;

public class SourceFileSummary
{
    public string SourceFileName { get; init; } = "manual";
    public int QuestionCount { get; init; }

    public string DisplayText => $"{SourceFileName} ({QuestionCount}개)";
}

