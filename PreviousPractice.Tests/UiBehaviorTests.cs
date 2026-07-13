using PreviousPractice.Models;
using PreviousPractice.ViewModels;

namespace PreviousPractice.Tests;

public sealed class UiBehaviorTests
{
    [Fact]
    public void ResponsiveLayout_UsesActualWidthInsteadOfDeviceKind()
    {
        var narrow = ResponsiveLayoutCalculator.Calculate(
            width: 360d,
            height: 800d,
            wideLayoutMinimumWidth: 1000d,
            wideHorizontalChrome: 404d,
            narrowHorizontalChrome: 72d,
            minimumViewportWidth: 220d,
            maximumViewportWidth: 920d,
            minimumViewportHeight: 220d,
            maximumViewportHeight: 620d);
        var wide = ResponsiveLayoutCalculator.Calculate(
            width: 1280d,
            height: 900d,
            wideLayoutMinimumWidth: 1000d,
            wideHorizontalChrome: 404d,
            narrowHorizontalChrome: 72d,
            minimumViewportWidth: 220d,
            maximumViewportWidth: 920d,
            minimumViewportHeight: 220d,
            maximumViewportHeight: 620d);

        Assert.False(narrow.UseWideLayout);
        Assert.Equal(288d, narrow.ImageViewportWidth);
        Assert.True(wide.UseWideLayout);
        Assert.Equal(876d, wide.ImageViewportWidth);
    }

    [Fact]
    public void ResponsiveLayout_SwitchesToNarrowWhenDesktopWindowShrinks()
    {
        var metrics = ResponsiveLayoutCalculator.Calculate(
            width: 820d,
            height: 700d,
            wideLayoutMinimumWidth: 1000d,
            wideHorizontalChrome: 404d,
            narrowHorizontalChrome: 72d,
            minimumViewportWidth: 220d,
            maximumViewportWidth: 920d,
            minimumViewportHeight: 220d,
            maximumViewportHeight: 620d);

        Assert.False(metrics.UseWideLayout);
        Assert.Equal(748d, metrics.ImageViewportWidth);
    }

    [Fact]
    public void ImageSliceBuild_ReportsMissingStoredImage()
    {
        var question = new Question
        {
            ImageSegments =
            [
                new QuestionImageSegment
                {
                    PageIndex = 2,
                    ImagePath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.png")
                }
            ]
        };

        var result = QuestionImageSliceBuilder.BuildWithStatus(
            question,
            320d,
            260d,
            0.02d,
            0.08d);

        Assert.Empty(result.Slices);
        Assert.Equal(1, result.StoredSegmentCount);
        Assert.Equal(1, result.UnavailableSegmentCount);
    }

    [Fact]
    public void ImageFileValidation_RejectsTruncatedOrMissingPng()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"previous-practice-ui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var validPath = Path.Combine(directory, "page.png");
        var truncatedPath = Path.Combine(directory, "truncated.png");
        try
        {
            var validPng = Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
            File.WriteAllBytes(validPath, validPng);
            File.WriteAllBytes(truncatedPath, validPng[..24]);

            Assert.True(File.Exists(validPath));
            Assert.True(QuestionImageSliceBuilder.CanOpenImageFile(validPath));
            Assert.True(QuestionImageSliceBuilder.CanOpenImageFile(validPath));
            Assert.False(QuestionImageSliceBuilder.CanOpenImageFile(truncatedPath));
            Assert.False(QuestionImageSliceBuilder.CanOpenImageFile(
                Path.Combine(directory, "missing.png")));

            File.WriteAllBytes(validPath, validPng[..24]);
            Assert.False(QuestionImageSliceBuilder.CanOpenImageFile(validPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MissingImageStatus_CountsBlankStoredSegmentAsUnavailable()
    {
        var question = new Question
        {
            ImageSegments =
            [
                new QuestionImageSegment
                {
                    PageIndex = 3,
                    ImagePath = ""
                }
            ]
        };

        var result = QuestionImageSliceBuilder.BuildWithStatus(
            question,
            320d,
            260d,
            0.02d,
            0.08d);

        Assert.Empty(result.Slices);
        Assert.Equal(1, result.StoredSegmentCount);
        Assert.Equal(1, result.UnavailableSegmentCount);
    }
}
