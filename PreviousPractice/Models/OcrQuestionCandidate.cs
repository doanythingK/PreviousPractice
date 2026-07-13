namespace PreviousPractice.Models;

public sealed record OcrQuestionCandidate
{
    public int Index { get; init; }
    public string Header { get; init; } = string.Empty;
    public bool IsInferred { get; init; }
    public int LogicalStartOrder { get; init; }
    public int LogicalEndOrder { get; init; }
    public int StartPage { get; init; }
    public int EndPage { get; init; }
    public int StartLineInPage { get; init; }
    public int EndLineInPage { get; init; }
    public int StartPageLineCount { get; init; }
    public int EndPageLineCount { get; init; }
    public string? ImagePath { get; init; }
    public string PreviewText { get; init; } = string.Empty;
    public bool HasAmbiguousBoundary { get; init; }
    public string BoundaryIssue { get; init; } = string.Empty;
    public bool UsesSemanticImageRegions { get; init; }
    public IReadOnlyList<OcrQuestionImageRegion> ImageRegions { get; init; } =
        Array.Empty<OcrQuestionImageRegion>();
    public IReadOnlyList<OcrQuestionImageRegion> SharedContextRegions { get; init; } =
        Array.Empty<OcrQuestionImageRegion>();
}
