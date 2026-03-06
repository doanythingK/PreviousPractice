namespace PreviousPractice.Models;

public sealed record OcrLineResult
(
    int LineInPage,
    string Text,
    double LeftRatio,
    double TopRatio,
    double RightRatio,
    double BottomRatio
);

public sealed record OcrPageResult
(
    int PageIndex,
    string Text,
    int WordCount,
    float AverageWordConfidence,
    string? ImagePath = null,
    int ImagePixelWidth = 0,
    int ImagePixelHeight = 0,
    IReadOnlyList<OcrLineResult>? Lines = null
);

public sealed class PdfOcrResult
{
    public bool IsSuccess { get; init; }
    public string SourceFileName { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public DateTimeOffset AnalyzedAt { get; init; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<OcrPageResult> Pages { get; init; } = Array.Empty<OcrPageResult>();
    public IReadOnlyList<OcrQuestionCandidate> QuestionCandidates { get; init; } = Array.Empty<OcrQuestionCandidate>();
    public int PageCount => Pages.Count;
    public int TotalWordCount => Pages.Sum(x => x.WordCount);
    public int DetectedQuestionCount => QuestionCandidates.Count;
    public bool HasQuestionCandidates => QuestionCandidates.Count > 0;
    public bool HasText => Pages.Any(x => !string.IsNullOrWhiteSpace(x.Text));

    public string Summary
        => HasText
            ? $"{SourceFileName} 분석 완료 (페이지: {PageCount}, 단어: {TotalWordCount}, 후보문항: {DetectedQuestionCount}개)"
            : $"{SourceFileName} 분석 결과가 비어 있습니다.";

    public string Preview
    {
        get
        {
            var merged = string.Join("\n\n", Pages.Select(x => x.Text).Where(x => !string.IsNullOrWhiteSpace(x)));
            return string.IsNullOrWhiteSpace(merged)
                ? "미리보기가 없습니다."
                : merged;
        }
    }

    public static PdfOcrResult Fail(string sourceFileName, string message)
    {
        return new PdfOcrResult
        {
            IsSuccess = false,
            SourceFileName = sourceFileName,
            Message = message,
            Pages = Array.Empty<OcrPageResult>()
        };
    }

    public static PdfOcrResult Ok(
        string sourceFileName,
        IReadOnlyList<OcrPageResult> pages,
        IReadOnlyList<OcrQuestionCandidate>? questionCandidates = null)
    {
        return new PdfOcrResult
        {
            IsSuccess = true,
            SourceFileName = sourceFileName,
            Message = "OCR 분석 완료",
            Pages = pages.ToArray(),
            QuestionCandidates = questionCandidates?.ToArray() ?? Array.Empty<OcrQuestionCandidate>()
        };
    }
}
