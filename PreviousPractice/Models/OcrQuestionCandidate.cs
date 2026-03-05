namespace PreviousPractice.Models;

public sealed record OcrQuestionCandidate
{
    public int Index { get; init; }
    public string Header { get; init; } = string.Empty;
    public int StartPage { get; init; }
    public int EndPage { get; init; }
    public int StartLineInPage { get; init; }
    public int EndLineInPage { get; init; }
    public int StartPageLineCount { get; init; }
    public string? ImagePath { get; init; }
    public string PreviewText { get; init; } = string.Empty;
}
