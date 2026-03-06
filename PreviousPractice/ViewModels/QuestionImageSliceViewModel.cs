using Microsoft.Maui.Controls;

namespace PreviousPractice.ViewModels;

public sealed class QuestionImageSliceViewModel
{
    public ImageSource? ImageSource { get; init; }
    public double VisibleWidth { get; init; }
    public double VisibleHeight { get; init; }
    public double ContentWidth { get; init; }
    public double ContentHeight { get; init; }
    public double TranslationX { get; init; }
    public double TranslationY { get; init; }
}
