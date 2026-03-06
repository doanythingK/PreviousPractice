namespace PreviousPractice.Models;

public sealed class QuestionImageSegment
{
    public int PageIndex { get; set; }
    public string? ImagePath { get; set; }
    public double ImageLeftRatio { get; set; }
    public double ImageTopRatio { get; set; }
    public double ImageRightRatio { get; set; } = 1d;
    public double ImageBottomRatio { get; set; } = 1d;
    public int ImagePixelWidth { get; set; }
    public int ImagePixelHeight { get; set; }
}
