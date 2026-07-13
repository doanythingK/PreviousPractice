using PreviousPractice.Models;
using PreviousPractice.ViewModels;

namespace PreviousPractice.Tests;

public sealed class QuestionImageRegionTests
{
    [Fact]
    public void PrecomputedSemanticRegions_AreMappedWithoutTextBoundingBoxShrink()
    {
        var candidate = new OcrQuestionCandidate
        {
            Index = 1,
            ImageRegions = new[]
            {
                Region(1, 0, .10, .50, .60)
            },
            SharedContextRegions = new[]
            {
                Region(1, 0, .02, .50, .10)
            }
        };
        var page = new OcrPageResult(
            1,
            "1. 문제",
            2,
            100f,
            ImagePath: "semantic-page.png",
            ImagePixelWidth: 1000,
            ImagePixelHeight: 1400,
            Lines: new[]
            {
                new OcrLineResult(1, "1.", .05, .10, .08, .12),
                new OcrLineResult(2, "짧은 본문", .15, .15, .30, .18)
            });

        var segments = MainViewModel.BuildQuestionImageSegments(
            candidate,
            new[] { candidate },
            new[] { page });

        Assert.Collection(
            segments,
            shared =>
            {
                Assert.Equal(.02, shared.ImageTopRatio, precision: 8);
                Assert.Equal(.10, shared.ImageBottomRatio, precision: 8);
            },
            question =>
            {
                Assert.Equal(0d, question.ImageLeftRatio);
                Assert.Equal(.50, question.ImageRightRatio, precision: 8);
                Assert.Equal(.10, question.ImageTopRatio, precision: 8);
                Assert.Equal(.60, question.ImageBottomRatio, precision: 8);
            });
    }

    [Fact]
    public void InvalidLegacyEdgeRatios_AreShiftedBackIntoVisibleImage()
    {
        var segment = new QuestionImageSegment
        {
            ImageLeftRatio = 1d,
            ImageRightRatio = 1d,
            ImageTopRatio = 1d,
            ImageBottomRatio = 1d
        };

        var bounds = QuestionImageSliceBuilder.NormalizeStoredBounds(segment, .02d, .08d);

        Assert.Equal(.92d, bounds.Left, precision: 8);
        Assert.Equal(1d, bounds.Right);
        Assert.Equal(.98d, bounds.Top, precision: 8);
        Assert.Equal(1d, bounds.Bottom);
    }

    [Fact]
    public void SemanticCandidateWithoutRegions_DoesNotFallBackToLegacyLineCrop()
    {
        var candidate = new OcrQuestionCandidate
        {
            Index = 1,
            UsesSemanticImageRegions = true,
            StartPage = 1,
            EndPage = 1,
            StartLineInPage = 1,
            EndLineInPage = 2,
            StartPageLineCount = 2,
            EndPageLineCount = 2
        };
        var page = new OcrPageResult(
            1,
            "1. 문제\n본문",
            3,
            100f,
            ImagePath: "legacy-fallback-must-not-run.png",
            Lines: new[]
            {
                new OcrLineResult(1, "1. 문제", .05, .10, .45, .14),
                new OcrLineResult(2, "본문", .05, .20, .45, .24)
            });

        var segments = MainViewModel.BuildQuestionImageSegments(
            candidate,
            new[] { candidate },
            new[] { page });

        Assert.Empty(segments);

        var issue = Assert.Single(
            MainViewModel.BuildStructuralIssues(new[] { candidate }, new[] { page }),
            x => x.Code == "missing-image-region");
        Assert.Equal(1, issue.Index);
    }

    [Fact]
    public void SemanticRegionOverlap_IsDetectedOnlyForQuestionOwnedRegions()
    {
        var first = new OcrQuestionCandidate
        {
            Index = 1,
            ImageRegions = new[] { Region(1, 0, .10, .50, .40) },
            SharedContextRegions = new[] { Region(1, -1, .02, 1d, .10) }
        };
        var overlapping = new OcrQuestionCandidate
        {
            Index = 2,
            ImageRegions = new[] { Region(1, 0, .35, .50, .60) }
        };
        var adjacent = new OcrQuestionCandidate
        {
            Index = 3,
            ImageRegions = new[] { Region(1, 0, .40, .50, .70) },
            SharedContextRegions = new[] { Region(1, -1, .02, 1d, .10) }
        };

        Assert.True(MainViewModel.SemanticImageRegionsOverlap(first, overlapping));
        Assert.False(MainViewModel.SemanticImageRegionsOverlap(first, adjacent));

        var overlapIssue = Assert.Single(
            MainViewModel.BuildStructuralIssues(
                new[] { first, overlapping },
                Array.Empty<OcrPageResult>()),
            x => x.Code == "overlapping-image-region");
        Assert.Equal(2, overlapIssue.Index);
    }

    [Fact]
    public void SharedContextOverlap_WithAnyQuestionRegionIsStructuralIssue()
    {
        var sharedOwner = new OcrQuestionCandidate
        {
            Index = 1,
            ImageRegions = new[] { Region(1, 0, .40, .50, .60) },
            SharedContextRegions = new[] { Region(1, -1, .05, 1d, .30) }
        };
        var coveredQuestion = new OcrQuestionCandidate
        {
            Index = 3,
            ImageRegions = new[] { Region(1, 1, .20, 1d, .50) }
        };

        var issue = Assert.Single(
            MainViewModel.BuildStructuralIssues(
                new[] { sharedOwner, coveredQuestion },
                Array.Empty<OcrPageResult>()),
            x => x.Code == "shared-context-image-overlap");

        Assert.Equal(3, issue.Index);
    }

    [Fact]
    public void NonconsecutiveQuestionIndexes_AreStructuralIssueWithoutExpectedRange()
    {
        var candidates = new[]
        {
            new OcrQuestionCandidate { Index = 1 },
            new OcrQuestionCandidate { Index = 2 },
            new OcrQuestionCandidate { Index = 4 }
        };

        var issue = Assert.Single(
            MainViewModel.BuildStructuralIssues(candidates, Array.Empty<OcrPageResult>()),
            x => x.Code == "nonconsecutive-index");

        Assert.Equal(4, issue.Index);
    }

    [Fact]
    public void DuplicatePageIndexes_DoNotCrashSemanticSegmentMapping()
    {
        var candidate = new OcrQuestionCandidate
        {
            Index = 1,
            UsesSemanticImageRegions = true,
            ImageRegions = new[] { Region(1, 0, .10, .50, .40) }
        };
        var pages = new[]
        {
            new OcrPageResult(1, "첫 페이지", 2, 100f, ImagePath: "first.png"),
            new OcrPageResult(1, "중복 페이지", 2, 100f, ImagePath: "duplicate.png")
        };

        var segment = Assert.Single(MainViewModel.BuildQuestionImageSegments(
            candidate,
            new[] { candidate },
            pages));

        Assert.Equal("first.png", segment.ImagePath);
    }

    [Fact]
    public void SliceViewport_DoesNotExpandBeyondStoredSemanticBounds()
    {
        var segment = new QuestionImageSegment
        {
            PageIndex = 1,
            ImageLeftRatio = .10,
            ImageRightRatio = .30,
            ImageTopRatio = .10,
            ImageBottomRatio = .105,
            ImagePixelWidth = 1000,
            ImagePixelHeight = 2000
        };

        var geometry = QuestionImageSliceBuilder.BuildSliceGeometry(
            segment,
            viewportWidth: 1000,
            viewportHeight: 800,
            minSliceRatio: .02,
            minSliceWidthRatio: .08);

        Assert.Equal(480d, geometry.VisibleWidth, precision: 6);
        Assert.Equal(24d, geometry.VisibleHeight, precision: 6);
    }

    private static OcrQuestionImageRegion Region(
        int page,
        int column,
        double top,
        double right,
        double bottom)
    {
        return new OcrQuestionImageRegion
        {
            PageIndex = page,
            ColumnIndex = column,
            LeftRatio = 0d,
            TopRatio = top,
            RightRatio = right,
            BottomRatio = bottom
        };
    }

}
