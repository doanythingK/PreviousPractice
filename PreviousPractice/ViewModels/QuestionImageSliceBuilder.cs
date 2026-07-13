using Microsoft.Maui.Controls;
using PreviousPractice.Models;
using PreviousPractice.Services;

namespace PreviousPractice.ViewModels;

internal static class QuestionImageSliceBuilder
{
    private const double MaxSliceContentScale = 2.4d;

    internal sealed record BuildResult(
        IReadOnlyList<QuestionImageSliceViewModel> Slices,
        int StoredSegmentCount,
        int UnavailableSegmentCount);

    public static IReadOnlyList<QuestionImageSliceViewModel> Build(
        Question? question,
        double viewportWidth,
        double viewportHeight,
        double minSliceRatio,
        double minSliceWidthRatio,
        double displayScale = 1d)
        => BuildWithStatus(
            question,
            viewportWidth,
            viewportHeight,
            minSliceRatio,
            minSliceWidthRatio,
            displayScale).Slices;

    internal static BuildResult BuildWithStatus(
        Question? question,
        double viewportWidth,
        double viewportHeight,
        double minSliceRatio,
        double minSliceWidthRatio,
        double displayScale = 1d)
    {
        if (question == null)
        {
            return new BuildResult(Array.Empty<QuestionImageSliceViewModel>(), 0, 0);
        }

        var declaredSegmentCount = question.ImageSegments is { Length: > 0 }
            ? question.ImageSegments.Length
            : string.IsNullOrWhiteSpace(question.ImagePath) ? 0 : 1;
        var storedSegments = ResolveStoredImageSegments(question).ToArray();
        var slices = new List<QuestionImageSliceViewModel>();
        var unavailableSegmentCount = declaredSegmentCount - storedSegments.Length;
        foreach (var segment in storedSegments)
        {
            var imageSource = BuildQuestionImageSource(segment.ImagePath);
            if (imageSource == null)
            {
                unavailableSegmentCount++;
                continue;
            }

            var geometry = BuildSliceGeometry(
                segment,
                viewportWidth,
                viewportHeight,
                minSliceRatio,
                minSliceWidthRatio,
                displayScale);

            slices.Add(new QuestionImageSliceViewModel
            {
                ImageSource = imageSource,
                PageIndex = segment.PageIndex,
                SequenceNumber = slices.Count + 1,
                AccessibilityDescription = segment.PageIndex > 0
                    ? $"문항 이미지 {slices.Count + 1}, PDF {segment.PageIndex}페이지"
                    : $"문항 이미지 {slices.Count + 1}",
                VisibleWidth = geometry.VisibleWidth,
                VisibleHeight = geometry.VisibleHeight,
                ContentWidth = geometry.ContentWidth,
                ContentHeight = geometry.ContentHeight,
                TranslationX = geometry.TranslationX,
                TranslationY = geometry.TranslationY
            });
        }

        return new BuildResult(slices, declaredSegmentCount, unavailableSegmentCount);
    }

    internal static (
        double VisibleWidth,
        double VisibleHeight,
        double ContentWidth,
        double ContentHeight,
        double TranslationX,
        double TranslationY) BuildSliceGeometry(
        QuestionImageSegment segment,
        double viewportWidth,
        double viewportHeight,
        double minSliceRatio,
        double minSliceWidthRatio,
        double displayScale = 1d)
    {
        var (left, top, right, bottom) = NormalizeStoredBounds(
            segment,
            minSliceRatio,
            minSliceWidthRatio);
        var widthRatio = Math.Clamp(right - left, 0.000_001d, 1d);
        var zoomScale = Math.Clamp(displayScale, 0.75d, 3.5d);
        var contentScale = Math.Min(1d / widthRatio, MaxSliceContentScale);
        var contentWidth = viewportWidth * contentScale * zoomScale;
        var (imagePixelWidth, imagePixelHeight) = ResolveImagePixelSize(segment);
        if (imagePixelWidth <= 0 || imagePixelHeight <= 0)
        {
            imagePixelWidth = contentWidth;
            imagePixelHeight = viewportHeight;
        }

        var contentHeight = contentWidth * imagePixelHeight / imagePixelWidth;
        // 저장된 의미 경계 자체를 clip 범위로 사용한다. 최소 표시 크기를 위해
        // right/bottom 밖을 늘리면 인접 문항이 다시 보일 수 있다.
        var visibleWidth = widthRatio * contentWidth;
        var visibleHeight = (bottom - top) * contentHeight;
        return (
            visibleWidth,
            visibleHeight,
            contentWidth,
            contentHeight,
            -left * contentWidth,
            -top * contentHeight);
    }

    internal static (double Left, double Top, double Right, double Bottom) NormalizeStoredBounds(
        QuestionImageSegment segment,
        double minSliceRatio,
        double minSliceWidthRatio)
    {
        var left = NormalizeRatio(segment.ImageLeftRatio, 0d);
        var top = NormalizeRatio(segment.ImageTopRatio, 0d);
        var right = NormalizeRatio(segment.ImageRightRatio, 1d);
        var bottom = NormalizeRatio(segment.ImageBottomRatio, 1d);
        if (right <= left)
        {
            if (left >= 1d - minSliceWidthRatio)
            {
                left = Math.Max(0d, 1d - minSliceWidthRatio);
                right = 1d;
            }
            else
            {
                right = Math.Min(1d, left + minSliceWidthRatio);
            }
        }

        if (bottom <= top)
        {
            if (top >= 1d - minSliceRatio)
            {
                top = Math.Max(0d, 1d - minSliceRatio);
                bottom = 1d;
            }
            else
            {
                bottom = Math.Min(1d, top + minSliceRatio);
            }
        }

        return (left, top, right, bottom);
    }

    private static double NormalizeRatio(double value, double fallback)
    {
        return double.IsFinite(value)
            ? Math.Clamp(value, 0d, 1d)
            : fallback;
    }

    private static (double Width, double Height) ResolveImagePixelSize(QuestionImageSegment segment)
    {
        if (segment.ImagePixelWidth > 0 && segment.ImagePixelHeight > 0)
        {
            return (segment.ImagePixelWidth, segment.ImagePixelHeight);
        }

        return !string.IsNullOrWhiteSpace(segment.ImagePath) &&
               PngFileValidator.TryValidate(segment.ImagePath, out var width, out var height)
            ? (width, height)
            : (0d, 0d);
    }

    private static IEnumerable<QuestionImageSegment> ResolveStoredImageSegments(Question question)
    {
        if (question.ImageSegments != null && question.ImageSegments.Length > 0)
        {
            return question.ImageSegments
                .Where(x => x != null)!;
        }

        if (string.IsNullOrWhiteSpace(question.ImagePath))
        {
            return Array.Empty<QuestionImageSegment>();
        }

        return new[]
        {
            new QuestionImageSegment
            {
                PageIndex = 1,
                ImagePath = question.ImagePath,
                ImageTopRatio = question.ImageTopRatio,
                ImageBottomRatio = question.ImageBottomRatio
            }
        };
    }

    private static ImageSource? BuildQuestionImageSource(string? imagePath)
    {
        if (!CanOpenImageFile(imagePath))
        {
            return null;
        }

        try
        {
            var stablePath = imagePath!;
            return ImageSource.FromStream(() => File.OpenRead(stablePath));
        }
        catch
        {
            return null;
        }
    }

    internal static bool CanOpenImageFile(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            return false;
        }

        try
        {
            if (string.Equals(Path.GetExtension(imagePath), ".png", StringComparison.OrdinalIgnoreCase))
            {
                return PngFileValidator.TryValidate(imagePath, out _, out _);
            }

            using var stream = File.OpenRead(imagePath);
            return stream.Length > 0;
        }
        catch
        {
            return false;
        }
    }
}
