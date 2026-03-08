using System.Buffers.Binary;
using Microsoft.Maui.Controls;
using PreviousPractice.Models;

namespace PreviousPractice.ViewModels;

internal static class QuestionImageSliceBuilder
{
    public static IReadOnlyList<QuestionImageSliceViewModel> Build(
        Question? question,
        double viewportWidth,
        double viewportHeight,
        double minSliceRatio,
        double minSliceWidthRatio)
    {
        if (question == null)
        {
            return Array.Empty<QuestionImageSliceViewModel>();
        }

        var slices = new List<QuestionImageSliceViewModel>();
        foreach (var segment in ResolveStoredImageSegments(question))
        {
            var imageSource = BuildQuestionImageSource(segment.ImagePath);
            if (imageSource == null)
            {
                continue;
            }

            var left = Math.Clamp(segment.ImageLeftRatio, 0d, 1d);
            var top = Math.Clamp(segment.ImageTopRatio, 0d, 1d);
            var right = Math.Clamp(segment.ImageRightRatio, 0d, 1d);
            var bottom = Math.Clamp(segment.ImageBottomRatio, 0d, 1d);
            if (right <= left)
            {
                right = Math.Min(1d, left + minSliceWidthRatio);
            }

            if (bottom <= top)
            {
                bottom = Math.Min(1d, top + minSliceRatio);
            }

            var contentWidth = viewportWidth;
            var (imagePixelWidth, imagePixelHeight) = ResolveImagePixelSize(segment);
            if (imagePixelWidth <= 0 || imagePixelHeight <= 0)
            {
                imagePixelWidth = contentWidth;
                imagePixelHeight = viewportHeight;
            }

            var contentHeight = contentWidth * imagePixelHeight / imagePixelWidth;
            var visibleWidth = Math.Max(contentWidth * minSliceWidthRatio, (right - left) * contentWidth);
            var visibleHeight = Math.Max(contentHeight * minSliceRatio, (bottom - top) * contentHeight);

            slices.Add(new QuestionImageSliceViewModel
            {
                ImageSource = imageSource,
                VisibleWidth = visibleWidth,
                VisibleHeight = visibleHeight,
                ContentWidth = contentWidth,
                ContentHeight = contentHeight,
                TranslationX = -left * contentWidth,
                TranslationY = -top * contentHeight
            });
        }

        return slices;
    }

    private static (double Width, double Height) ResolveImagePixelSize(QuestionImageSegment segment)
    {
        if (segment.ImagePixelWidth > 0 && segment.ImagePixelHeight > 0)
        {
            return (segment.ImagePixelWidth, segment.ImagePixelHeight);
        }

        return TryReadPngSize(segment.ImagePath, out var width, out var height)
            ? (width, height)
            : (0d, 0d);
    }

    private static bool TryReadPngSize(string? imagePath, out int width, out int height)
    {
        width = 0;
        height = 0;

        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            return false;
        }

        try
        {
            using var stream = File.OpenRead(imagePath);
            Span<byte> header = stackalloc byte[24];
            if (stream.Read(header) < header.Length)
            {
                return false;
            }

            Span<byte> pngSignature = stackalloc byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
            if (!header[..8].SequenceEqual(pngSignature))
            {
                return false;
            }

            width = BinaryPrimitives.ReadInt32BigEndian(header.Slice(16, 4));
            height = BinaryPrimitives.ReadInt32BigEndian(header.Slice(20, 4));
            return width > 0 && height > 0;
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<QuestionImageSegment> ResolveStoredImageSegments(Question question)
    {
        if (question.ImageSegments != null && question.ImageSegments.Length > 0)
        {
            return question.ImageSegments
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.ImagePath));
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
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            return null;
        }

        try
        {
            return ImageSource.FromStream(() => File.OpenRead(imagePath));
        }
        catch
        {
            return null;
        }
    }
}
