namespace PreviousPractice.Models;

public sealed record OcrQuestionImageRegion
{
    public int PageIndex { get; init; }
    public int ColumnIndex { get; init; }
    public double LeftRatio { get; init; }
    public double TopRatio { get; init; }
    public double RightRatio { get; init; } = 1d;
    public double BottomRatio { get; init; } = 1d;
    public bool HasReliableGeometry { get; init; } = true;
}
