using PreviousPractice.Infrastructure;
using PreviousPractice.Models;
using PreviousPractice.Services;
using PreviousPractice.ViewModels;

namespace PreviousPractice.Tests;

public sealed class OcrSegmentationBoundaryTests
{
    [Fact]
    public void NumericChoices_DoNotBecomeQuestionHeaders()
    {
        var page = Page(
            1,
            Line(1, "1. 다음 설명으로 옳은 것은?", .05, .05, .45, .09),
            Line(2, "1) 첫 번째 보기 설명입니다", .14, .12, .44, .15),
            Line(3, "2) 두 번째 보기 설명입니다", .14, .17, .44, .20),
            Line(4, "3) 세 번째 보기 설명입니다", .14, .22, .44, .25),
            Line(5, "4) 네 번째 보기 설명입니다", .14, .27, .44, .30),
            Line(6, "2. 실제 두 번째 문제입니다", .05, .42, .45, .46),
            Line(7, "둘째 문제 본문입니다", .09, .48, .44, .51),
            Line(8, "3. 실제 세 번째 문제입니다", .05, .62, .45, .66));

        var candidates = OcrQuestionSegmenter.SplitByHeader(
            new[] { page },
            new QuestionNumberRange(1, 3));

        Assert.Equal(new[] { 1, 2, 3 }, candidates.Select(x => x.Index));
        Assert.Contains("4) 네 번째 보기", candidates[0].PreviewText);
        Assert.DoesNotContain(candidates, x => x.Header.StartsWith("2)", StringComparison.Ordinal));
        Assert.All(candidates, candidate => Assert.False(candidate.HasAmbiguousBoundary));
    }

    [Fact]
    public void NumericChoices_WithSameIndent_UseLaterQuestionEvidence()
    {
        var page = Page(
            1,
            Line(1, "1. 다음 설명으로 옳은 것은?", .05, .05, .45, .09),
            Line(2, "1) 첫 번째 보기 설명입니다", .05, .12, .45, .15),
            Line(3, "2) 두 번째 보기 설명입니다", .05, .17, .45, .20),
            Line(4, "3) 세 번째 보기 설명입니다", .05, .22, .45, .25),
            Line(5, "4) 네 번째 보기 설명입니다", .05, .27, .45, .30),
            Line(6, "2. 실제 두 번째 문제입니다", .05, .42, .45, .46),
            Line(7, "둘째 문제 본문입니다", .05, .48, .45, .51),
            Line(8, "3. 실제 세 번째 문제입니다", .05, .62, .45, .66));

        var candidates = OcrQuestionSegmenter.SplitByHeader(new[] { page });

        Assert.Equal(new[] { 1, 2, 3 }, candidates.Select(x => x.Index));
        Assert.Contains("4) 네 번째 보기", candidates[0].PreviewText);
        Assert.DoesNotContain(candidates, x => x.Header.StartsWith("2)", StringComparison.Ordinal));
        Assert.All(candidates, candidate => Assert.False(candidate.HasAmbiguousBoundary));
    }

    [Fact]
    public void CircledChoices_DoNotOverrideNumericQuestionSequence()
    {
        var candidates = OcrQuestionSegmenter.SplitByHeader(
            new[]
            {
                Page(
                    1,
                    Line(1, "1. 다음 설명으로 옳은 것은?", .05, .05, .45, .09),
                    Line(2, "① 첫 번째 보기 설명입니다", .05, .12, .45, .15),
                    Line(3, "② 두 번째 보기 설명입니다", .05, .17, .45, .20),
                    Line(4, "③ 세 번째 보기 설명입니다", .05, .22, .45, .25),
                    Line(5, "④ 네 번째 보기 설명입니다", .05, .27, .45, .30),
                    Line(6, "2. 실제 두 번째 문제입니다", .05, .42, .45, .46),
                    Line(7, "3. 실제 세 번째 문제입니다", .05, .62, .45, .66))
            });

        Assert.Equal(new[] { 1, 2, 3 }, candidates.Select(x => x.Index));
        Assert.Contains("④ 네 번째 보기", candidates[0].PreviewText);
    }

    [Theory]
    [InlineData("① 첫 번째 보기입니다", "② 두 번째 보기입니다", "③ 세 번째 보기입니다", "④ 네 번째 보기입니다")]
    [InlineData("A. 첫 번째 보기입니다", "B. 두 번째 보기입니다", "C. 세 번째 보기입니다", "D. 네 번째 보기입니다")]
    [InlineData("1) 첫 번째 보기입니다", "2) 두 번째 보기입니다", "3) 세 번째 보기입니다", "4) 네 번째 보기입니다")]
    public void OptionOnlyNumberBlock_IsNeverTrustedAsQuestionSequence(
        string first,
        string second,
        string third,
        string fourth)
    {
        var candidates = OcrQuestionSegmenter.SplitByHeader(
            new[]
            {
                Page(
                    1,
                    Line(1, first, .12, .10, .45, .14),
                    Line(2, second, .12, .20, .45, .24),
                    Line(3, third, .12, .30, .45, .34),
                    Line(4, fourth, .12, .40, .45, .44))
            },
            new QuestionNumberRange(1, 4));

        Assert.NotEmpty(candidates);
        Assert.All(candidates, candidate => Assert.True(candidate.HasAmbiguousBoundary));
    }

    [Theory]
    [InlineData("① 다음 설명 중 옳은 것은?", "② 다음 설명 중 옳은 것은?", "③ 다음 설명 중 옳은 것은?", "④ 다음 설명 중 옳은 것은?")]
    [InlineData("1) 다음 설명 중 옳은 것은?", "2) 다음 설명 중 옳은 것은?", "3) 다음 설명 중 옳은 것은?", "4) 다음 설명 중 옳은 것은?")]
    [InlineData("1. 다음 설명 중 옳은 것은?", "2. 다음 설명 중 옳은 것은?", "3. 다음 설명 중 옳은 것은?", "4. 다음 설명 중 옳은 것은?")]
    public void QuestionLikeOptionBlock_IsStillAmbiguous(
        string first,
        string second,
        string third,
        string fourth)
    {
        var candidates = OcrQuestionSegmenter.SplitByHeader(
            new[]
            {
                Page(
                    1,
                    Line(1, first, .12, .10, .45, .14),
                    Line(2, second, .12, .20, .45, .24),
                    Line(3, third, .12, .30, .45, .34),
                    Line(4, fourth, .12, .40, .45, .44))
            },
            new QuestionNumberRange(1, 4));

        Assert.Equal(new[] { 1, 2, 3, 4 }, candidates.Select(x => x.Index));
        Assert.All(candidates, candidate => Assert.True(candidate.HasAmbiguousBoundary));
    }

    [Fact]
    public void DenseVariedOptionQuestions_AreStillAmbiguous()
    {
        var candidates = OcrQuestionSegmenter.SplitByHeader(
            new[]
            {
                Page(
                    1,
                    Line(1, "1. 첫 번째 보기 설명은 옳은가?", .05, .10, .45, .14),
                    Line(2, "2. 두 번째 보기 설명은 틀린가?", .05, .20, .45, .24),
                    Line(3, "3. 세번째 보기 설명은 적절한가?", .05, .30, .45, .34),
                    Line(4, "4. 네 번째 보기 설명은 타당한가?", .05, .40, .45, .44))
            },
            new QuestionNumberRange(1, 4));

        Assert.Equal(new[] { 1, 2, 3, 4 }, candidates.Select(x => x.Index));
        Assert.All(candidates, candidate => Assert.True(candidate.HasAmbiguousBoundary));
    }

    [Theory]
    [InlineData("1. 첫 번째 보기입니다", "2. 두 번째 보기입니다", "3. 세번째 보기입니다", "4. 네 번째 보기입니다")]
    [InlineData("1번 첫 번째 보기입니다", "2번 두 번째 보기입니다", "3번 세번째 보기입니다", "4번 네 번째 보기입니다")]
    public void WeakOptionSequence_WithStrongNumericSyntaxIsAmbiguous(
        string first,
        string second,
        string third,
        string fourth)
    {
        var candidates = OcrQuestionSegmenter.SplitByHeader(
            new[]
            {
                Page(
                    1,
                    Line(1, first, .05, .10, .45, .14),
                    Line(2, second, .05, .20, .45, .24),
                    Line(3, third, .05, .30, .45, .34),
                    Line(4, fourth, .05, .40, .45, .44))
            },
            new QuestionNumberRange(1, 4));

        Assert.Equal(new[] { 1, 2, 3, 4 }, candidates.Select(x => x.Index));
        Assert.All(candidates, candidate => Assert.True(candidate.HasAmbiguousBoundary));
    }

    [Fact]
    public void SingleWeakChoice_WithExpectedRangeIsAmbiguous()
    {
        var candidate = Assert.Single(OcrQuestionSegmenter.SplitByHeader(
            new[]
            {
                Page(
                    1,
                    Line(1, "일반 본문입니다", .05, .10, .45, .14),
                    Line(2, "1) 첫 번째 보기입니다", .05, .30, .45, .34))
            },
            new QuestionNumberRange(1, 1)));

        Assert.True(candidate.HasAmbiguousBoundary);
    }

    [Fact]
    public void LeftmostFalseMarker_DoesNotHideStrongSameIndexCompetitor()
    {
        var candidates = OcrQuestionSegmenter.SplitByHeader(
            new[]
            {
                Page(
                    1,
                    Line(1, "1. 첫 번째 보기입니다", .02, .05, .36, .09),
                    Line(2, "1. 첫 문제입니다", .10, .20, .45, .24),
                    Line(3, "2. 둘째 문제입니다", .10, .50, .45, .54))
            },
            new QuestionNumberRange(1, 2));

        Assert.Equal(new[] { 1, 2 }, candidates.Select(x => x.Index));
        Assert.All(candidates, candidate => Assert.True(candidate.HasAmbiguousBoundary));
    }

    [Fact]
    public void SingleStrongDotHeader_IsKeptWithoutExpectedRange()
    {
        var candidates = OcrQuestionSegmenter.SplitByHeader(
            new[]
            {
                Page(
                    1,
                    Line(1, "1. 다음 중 옳은 것은?", .05, .10, .45, .14),
                    Line(2, "① 첫 번째 보기입니다", .12, .20, .45, .24))
            });

        var candidate = Assert.Single(candidates);
        Assert.Equal(1, candidate.Index);
        Assert.False(candidate.HasAmbiguousBoundary);
        Assert.Contains("① 첫 번째 보기", candidate.PreviewText);
    }

    [Fact]
    public void UnreliableBodyGeometry_MarksOwningQuestionAmbiguous()
    {
        var candidates = OcrQuestionSegmenter.SplitByHeader(
            new[]
            {
                Page(
                    1,
                    Line(1, "1. 첫 문제입니다", .05, .10, .45, .14),
                    Line(2, "좌표가 손상된 본문입니다", double.NaN, double.NaN, double.NaN, double.NaN),
                    Line(3, "2. 둘째 문제입니다", .05, .60, .45, .64))
            },
            new QuestionNumberRange(1, 2));

        Assert.Equal(new[] { 1, 2 }, candidates.Select(x => x.Index));
        Assert.True(candidates[0].HasAmbiguousBoundary);
    }

    [Theory]
    [InlineData("2 단계에서는 다음 처리를 수행한다")]
    [InlineData("2 개의 보기 중 하나를 고르시오")]
    [InlineData("2 페이지에서 계속된다")]
    public void NumericQuantityAtLineStart_StaysInPreviousQuestion(string bodyText)
    {
        var candidates = OcrQuestionSegmenter.SplitByHeader(
            new[]
            {
                Page(
                    1,
                    Line(1, "1. 첫 문제입니다", .05, .05, .45, .09),
                    Line(2, bodyText, .05, .14, .45, .18),
                    Line(3, "2. 실제 둘째 문제입니다", .05, .40, .45, .44))
            },
            new QuestionNumberRange(1, 2));

        Assert.Equal(new[] { 1, 2 }, candidates.Select(x => x.Index));
        Assert.Contains(bodyText, candidates[0].PreviewText);
    }

    [Fact]
    public void CenteredBarePageNumber_DoesNotSplitContinuation()
    {
        var candidates = OcrQuestionSegmenter.SplitByHeader(
            new[]
            {
                Page(1, Line(1, "1. 첫 문제입니다", .05, .10, .45, .14)),
                Page(
                    2,
                    Line(1, "2", .48, .02, .52, .05),
                    Line(2, "이전 문제의 계속되는 지문입니다", .05, .10, .45, .14),
                    Line(3, "2. 실제 둘째 문제입니다", .05, .40, .45, .44))
            },
            new QuestionNumberRange(1, 2));

        Assert.Equal(new[] { 1, 2 }, candidates.Select(x => x.Index));
        Assert.Contains("계속되는 지문", candidates[0].PreviewText);
    }

    [Theory]
    [InlineData("1번 첫 문제입니다", "2번 둘째 문제입니다")]
    [InlineData("제1번 첫 문제입니다", "제2번 둘째 문제입니다")]
    [InlineData("문제 1번 첫 문제입니다", "문제 2번 둘째 문제입니다")]
    [InlineData("(1) 첫 문제입니다", "[2] 둘째 문제입니다")]
    [InlineData("1．첫 문제입니다", "2．둘째 문제입니다")]
    [InlineData("１．첫 문제입니다", "２．둘째 문제입니다")]
    public void CommonNumericHeaderForms_AreRecognized(string first, string second)
    {
        var candidates = OcrQuestionSegmenter.SplitByHeader(
            new[]
            {
                Page(
                    1,
                    Line(1, first, .05, .10, .45, .14),
                    Line(2, second, .05, .40, .45, .44))
            });

        Assert.Equal(new[] { 1, 2 }, candidates.Select(x => x.Index));
    }

    [Fact]
    public void MixedHeaderStyles_AreEvaluatedTogether()
    {
        var candidates = OcrQuestionSegmenter.SplitByHeader(
            new[]
            {
                Page(
                    1,
                    Line(1, "1. 첫 문제입니다", .05, .10, .45, .14),
                    Line(2, "② 둘째 문제입니다", .05, .35, .45, .39),
                    Line(3, "3. 셋째 문제입니다", .05, .60, .45, .64))
            },
            new QuestionNumberRange(1, 3));

        Assert.Equal(new[] { 1, 2, 3 }, candidates.Select(x => x.Index));
    }

    [Fact]
    public void ShortTwoColumnPage_UsesColumnReadingOrder()
    {
        var candidates = OcrQuestionSegmenter.SplitByHeader(
            new[]
            {
                Page(
                    1,
                    Line(1, "1. 첫 문제입니다", .05, .10, .42, .14),
                    Line(2, "첫 문제 본문입니다", .08, .20, .42, .24),
                    Line(3, "2. 둘째 문제입니다", .05, .50, .42, .54),
                    Line(4, "둘째 문제 본문입니다", .08, .60, .42, .64),
                    Line(5, "3. 셋째 문제입니다", .55, .10, .92, .14),
                    Line(6, "셋째 문제 본문입니다", .58, .20, .92, .24),
                    Line(7, "4. 넷째 문제입니다", .55, .50, .92, .54),
                    Line(8, "넷째 문제 본문입니다", .58, .60, .92, .64))
            },
            new QuestionNumberRange(1, 4));

        Assert.Equal(new[] { 1, 2, 3, 4 }, candidates.Select(x => x.Index));
        Assert.All(candidates, candidate => Assert.False(candidate.HasAmbiguousBoundary));
    }

    [Fact]
    public void RowMajorTwoColumnPage_UsesNumberSequenceEvidence()
    {
        var candidates = OcrQuestionSegmenter.SplitByHeader(
            new[]
            {
                Page(
                    1,
                    Line(1, "1. 첫 문제입니다", .05, .10, .42, .14),
                    Line(2, "2. 둘째 문제입니다", .55, .10, .92, .14),
                    Line(3, "3. 셋째 문제입니다", .05, .55, .42, .59),
                    Line(4, "4. 넷째 문제입니다", .55, .55, .92, .59))
            },
            new QuestionNumberRange(1, 4));

        Assert.Equal(new[] { 1, 2, 3, 4 }, candidates.Select(x => x.Index));
        Assert.All(candidates, candidate => Assert.False(candidate.HasAmbiguousBoundary));
    }

    [Fact]
    public void SparseTwoColumnPage_WithOneQuestionPerColumnKeepsColumnRegions()
    {
        var candidates = OcrQuestionSegmenter.SplitByHeader(
            new[]
            {
                Page(
                    1,
                    Line(1, "1. 왼쪽 문제입니다", .05, .10, .42, .14),
                    Line(2, "2. 오른쪽 문제입니다", .55, .10, .92, .14))
            },
            new QuestionNumberRange(1, 2));

        Assert.Equal(new[] { 1, 2 }, candidates.Select(x => x.Index));
        Assert.Contains(candidates[0].ImageRegions, region => region.ColumnIndex == 0);
        Assert.All(candidates[1].ImageRegions, region => Assert.Equal(1, region.ColumnIndex));
        Assert.All(candidates, candidate => Assert.False(candidate.HasAmbiguousBoundary));
    }

    [Fact]
    public void StaggeredSparseTwoColumnPage_StillUsesColumnReadingOrder()
    {
        var candidates = OcrQuestionSegmenter.SplitByHeader(
            new[]
            {
                Page(
                    1,
                    Line(1, "1. 왼쪽 문제입니다", .05, .10, .42, .14),
                    Line(2, "2. 오른쪽 문제입니다", .55, .60, .92, .64))
            },
            new QuestionNumberRange(1, 2));

        Assert.Equal(new[] { 1, 2 }, candidates.Select(x => x.Index));
        Assert.Contains(candidates[0].ImageRegions, region => region.ColumnIndex == 0);
        Assert.All(candidates[1].ImageRegions, region => Assert.Equal(1, region.ColumnIndex));
        Assert.All(candidates, candidate => Assert.False(candidate.HasAmbiguousBoundary));
    }

    [Fact]
    public void TwoColumnShapeWithoutQuestionEvidenceInBothColumns_IsAmbiguous()
    {
        var candidates = OcrQuestionSegmenter.SplitByHeader(
            new[]
            {
                Page(
                    1,
                    Line(1, "1. 첫 문제입니다", .05, .10, .42, .14),
                    Line(2, "2. 둘째 문제입니다", .05, .55, .42, .59),
                    Line(3, "① 표의 첫 번째 값", .62, .10, .90, .14),
                    Line(4, "② 표의 두 번째 값", .62, .55, .90, .59))
            },
            new QuestionNumberRange(1, 2));

        Assert.Equal(new[] { 1, 2 }, candidates.Select(x => x.Index));
        Assert.All(candidates, candidate => Assert.True(candidate.HasAmbiguousBoundary));
    }

    [Fact]
    public void ThreeColumnLikeGeometry_IsNotTrustedAsTwoColumns()
    {
        var candidates = OcrQuestionSegmenter.SplitByHeader(
            new[]
            {
                Page(
                    1,
                    Line(1, "1. 왼쪽 문제입니다", .03, .10, .28, .14),
                    Line(2, "왼쪽 본문입니다", .03, .25, .28, .29),
                    Line(3, "중앙 표 값입니다", .38, .10, .62, .14),
                    Line(4, "중앙 표 설명입니다", .38, .25, .62, .29),
                    Line(5, "2. 오른쪽 문제입니다", .72, .10, .97, .14),
                    Line(6, "오른쪽 본문입니다", .72, .25, .97, .29))
            },
            new QuestionNumberRange(1, 2));

        Assert.NotEmpty(candidates);
        Assert.False(
            candidates.Select(x => x.Index).SequenceEqual(new[] { 1, 2 }) &&
            candidates.All(x => !x.HasAmbiguousBoundary));
        Assert.All(candidates, candidate => Assert.True(candidate.HasAmbiguousBoundary));
    }

    [Fact]
    public void SparseThreeColumnHeaders_AreNotTrustedAsSingleColumn()
    {
        var candidates = OcrQuestionSegmenter.SplitByHeader(
            new[]
            {
                Page(
                    1,
                    Line(1, "문제 1. 왼쪽 문제입니다", .03, .10, .28, .14),
                    Line(2, "문제 2. 중앙 문제입니다", .38, .35, .62, .39),
                    Line(3, "문제 3. 오른쪽 문제입니다", .72, .60, .97, .64))
            },
            new QuestionNumberRange(1, 3));

        Assert.NotEmpty(candidates);
        Assert.False(
            candidates.Select(x => x.Index).SequenceEqual(new[] { 1, 2, 3 }) &&
            candidates.All(x => !x.HasAmbiguousBoundary));
        Assert.All(candidates, candidate => Assert.True(candidate.HasAmbiguousBoundary));
    }

    [Fact]
    public void SingleStrongMiddleColumnLine_MakesTwoColumnLayoutAmbiguous()
    {
        var candidates = OcrQuestionSegmenter.SplitByHeader(
            new[]
            {
                Page(
                    1,
                    Line(1, "문제 1. 왼쪽 문제입니다", .03, .10, .28, .14),
                    Line(2, "왼쪽 본문입니다", .03, .30, .28, .34),
                    Line(3, "문제 2. 중앙 문제입니다", .39, .05, .61, .09),
                    Line(4, "문제 3. 오른쪽 문제입니다", .72, .10, .97, .14),
                    Line(5, "오른쪽 본문입니다", .72, .30, .97, .34))
            },
            new QuestionNumberRange(1, 3));

        Assert.NotEmpty(candidates);
        Assert.False(
            candidates.Select(x => x.Index).SequenceEqual(new[] { 1, 2, 3 }) &&
            candidates.All(x => !x.HasAmbiguousBoundary));
        Assert.All(candidates, candidate => Assert.True(candidate.HasAmbiguousBoundary));
    }

    [Fact]
    public void AsymmetricThreeColumnOrigins_AreAlsoAmbiguous()
    {
        var candidates = OcrQuestionSegmenter.SplitByHeader(
            new[]
            {
                Page(
                    1,
                    Line(1, "문제 1. 왼쪽 문제입니다", .02, .05, .20, .09),
                    Line(2, "왼쪽 본문입니다", .02, .30, .20, .34),
                    Line(3, "문제 2. 중앙 문제입니다", .26, .10, .46, .14),
                    Line(4, "중앙 본문입니다", .26, .30, .46, .34),
                    Line(5, "문제 3. 오른쪽 문제입니다", .70, .05, .96, .09),
                    Line(6, "오른쪽 본문입니다", .70, .30, .96, .34))
            },
            new QuestionNumberRange(1, 3));

        Assert.NotEmpty(candidates);
        Assert.False(
            candidates.Select(x => x.Index).SequenceEqual(new[] { 1, 2, 3 }) &&
            candidates.All(x => !x.HasAmbiguousBoundary));
        Assert.All(candidates, candidate => Assert.True(candidate.HasAmbiguousBoundary));
    }

    [Fact]
    public void NarrowAdjacentColumnBands_AreNotMergedByFixedOriginTolerance()
    {
        var candidates = OcrQuestionSegmenter.SplitByHeader(
            new[]
            {
                Page(
                    1,
                    Line(1, "문제 1. 왼쪽 문제입니다", .02, .05, .16, .09),
                    Line(2, "왼쪽 본문입니다", .02, .30, .16, .34),
                    Line(3, "문제 2. 중앙 문제입니다", .19, .10, .33, .14),
                    Line(4, "중앙 본문입니다", .19, .30, .33, .34),
                    Line(5, "문제 3. 오른쪽 문제입니다", .68, .05, .96, .09),
                    Line(6, "오른쪽 본문입니다", .68, .30, .96, .34))
            },
            new QuestionNumberRange(1, 3));

        Assert.NotEmpty(candidates);
        Assert.All(candidates, candidate => Assert.True(candidate.HasAmbiguousBoundary));
    }

    [Fact]
    public void InteriorFullWidthRun_InColumnQuestionIsAmbiguous()
    {
        var candidates = OcrQuestionSegmenter.SplitByHeader(
            new[]
            {
                Page(
                    1,
                    Line(1, "1. 첫 문제입니다", .05, .10, .42, .14),
                    Line(2, "왼쪽 본문입니다", .08, .20, .42, .24),
                    Line(3, "오른쪽 그림 설명입니다", .55, .10, .92, .14),
                    Line(4, "전폭 표 또는 그림입니다", .05, .35, .95, .45),
                    Line(5, "2. 둘째 문제입니다", .05, .60, .42, .64),
                    Line(6, "3. 셋째 문제입니다", .55, .55, .92, .59),
                    Line(7, "4. 넷째 문제입니다", .55, .80, .92, .84))
            },
            new QuestionNumberRange(1, 4));

        Assert.Equal(new[] { 1, 2, 3, 4 }, candidates.Select(x => x.Index));
        Assert.Contains(candidates, candidate => candidate.HasAmbiguousBoundary);
    }

    [Fact]
    public void ImageRegions_UseColumnBandAndSharedNextHeaderBoundary()
    {
        var candidates = OcrQuestionSegmenter.SplitByHeader(
            new[]
            {
                Page(
                    1,
                    Line(1, "1. 첫 문제입니다", .05, .10, .42, .14),
                    Line(2, "들여쓴 본문입니다", .16, .18, .40, .22),
                    Line(3, "2. 둘째 문제입니다", .05, .60, .42, .64),
                    Line(4, "둘째 본문입니다", .10, .70, .42, .74),
                    Line(5, "3. 오른쪽 첫 문제입니다", .55, .10, .92, .14),
                    Line(6, "오른쪽 본문입니다", .60, .20, .92, .24))
            },
            new QuestionNumberRange(1, 3));

        var first = Assert.Single(candidates[0].ImageRegions);
        var second = candidates[1].ImageRegions[0];
        Assert.Equal(0d, first.LeftRatio);
        Assert.InRange(first.RightRatio, .40d, .60d);
        Assert.Equal(first.BottomRatio, second.TopRatio, precision: 8);
        Assert.True(first.BottomRatio > .22d);
    }

    [Fact]
    public void SharedContext_EndsPreviousQuestionAndIsAttachedOnlyToTargetRange()
    {
        var candidates = OcrQuestionSegmenter.SplitByHeader(
            new[]
            {
                Page(
                    1,
                    Line(1, "5. 다섯째 문제입니다", .05, .05, .45, .09),
                    Line(2, "다섯째 본문입니다", .08, .12, .45, .16),
                    Line(3, "[6~8] 다음 글을 보고 답하시오", .05, .25, .45, .29),
                    Line(4, "공통 지문 내용입니다", .05, .31, .45, .40),
                    Line(5, "6. 여섯째 문제입니다", .05, .45, .45, .49),
                    Line(6, "7. 일곱째 문제입니다", .05, .62, .45, .66),
                    Line(7, "8. 여덟째 문제입니다", .05, .79, .45, .83))
            },
            new QuestionNumberRange(5, 8));

        Assert.Equal(new[] { 5, 6, 7, 8 }, candidates.Select(x => x.Index));
        Assert.DoesNotContain("공통 지문", candidates[0].PreviewText);
        Assert.Empty(candidates[0].SharedContextRegions);
        Assert.All(candidates.Skip(1), candidate => Assert.NotEmpty(candidate.SharedContextRegions));
        Assert.True(candidates[0].ImageRegions[^1].BottomRatio <= candidates[1].SharedContextRegions[0].TopRatio + .000_001d);
    }

    [Fact]
    public void LeadingSharedContext_IsPreservedForFirstQuestion()
    {
        var candidates = OcrQuestionSegmenter.SplitByHeader(
            new[]
            {
                Page(
                    1,
                    Line(1, "다음 자료를 읽고 답하시오", .05, .05, .45, .09),
                    Line(2, "공통 자료 내용입니다", .05, .12, .45, .22),
                    Line(3, "1. 첫 문제입니다", .05, .30, .45, .34),
                    Line(4, "2. 둘째 문제입니다", .05, .60, .45, .64))
            },
            new QuestionNumberRange(1, 2));

        Assert.Equal(new[] { 1, 2 }, candidates.Select(x => x.Index));
        Assert.NotEmpty(candidates[0].SharedContextRegions);
        Assert.Empty(candidates[1].SharedContextRegions);
        Assert.True(candidates[0].SharedContextRegions[0].TopRatio < candidates[0].ImageRegions[0].TopRatio);
    }

    [Fact]
    public void FullWidthSharedContext_IsNotClippedToOneColumn()
    {
        var candidates = OcrQuestionSegmenter.SplitByHeader(
            new[]
            {
                Page(
                    1,
                    Line(1, "[1~4] 다음 자료를 읽고 답하시오", .05, .03, .95, .07),
                    Line(2, "공통 자료 내용입니다", .05, .09, .95, .16),
                    Line(3, "1. 첫 문제입니다", .05, .30, .42, .34),
                    Line(4, "2. 둘째 문제입니다", .05, .60, .42, .64),
                    Line(5, "3. 셋째 문제입니다", .55, .30, .92, .34),
                    Line(6, "4. 넷째 문제입니다", .55, .60, .92, .64))
            },
            new QuestionNumberRange(1, 4));

        Assert.Equal(new[] { 1, 2, 3, 4 }, candidates.Select(x => x.Index));
        Assert.All(
            candidates,
            candidate =>
            {
                var context = Assert.Single(candidate.SharedContextRegions);
                Assert.Equal(-1, context.ColumnIndex);
                Assert.Equal(0d, context.LeftRatio);
                Assert.Equal(1d, context.RightRatio);
            });
    }

    [Fact]
    public void FullWidthSharedContext_StopsAtEarliestPhysicalTargetHeader()
    {
        var candidates = OcrQuestionSegmenter.SplitByHeader(
            new[]
            {
                Page(
                    1,
                    Line(1, "[1~2] 다음 자료를 읽고 답하시오", .05, .03, .95, .07),
                    Line(2, "공통 자료입니다", .05, .09, .95, .16),
                    Line(3, "1. 왼쪽 문제입니다", .05, .50, .42, .54),
                    Line(4, "왼쪽 본문입니다", .08, .62, .42, .66),
                    Line(5, "2. 오른쪽 문제입니다", .55, .20, .92, .24),
                    Line(6, "오른쪽 본문입니다", .58, .32, .92, .36))
            },
            new QuestionNumberRange(1, 2));

        var shared = Assert.Single(candidates[0].SharedContextRegions);
        Assert.Equal(shared, Assert.Single(candidates[1].SharedContextRegions));
        Assert.True(shared.BottomRatio <= candidates[1].ImageRegions[0].TopRatio + .000_001d);
        Assert.False(PdfStructuralValidator.SemanticImageRegionsOverlap(
            new OcrQuestionCandidate { ImageRegions = new[] { shared } },
            candidates[1]));
    }

    [Fact]
    public void SharedContextCoveringNonTargetColumn_IsBlockedByStructuralDiagnostics()
    {
        var page = Page(
            1,
            Line(1, "[1~2] 다음 자료를 읽고 답하시오", .05, .03, .95, .07),
            Line(2, "공통 자료입니다", .05, .09, .95, .16),
            Line(3, "1. 왼쪽 첫 문제입니다", .05, .30, .42, .34),
            Line(4, "2. 왼쪽 둘째 문제입니다", .05, .60, .42, .64),
            Line(5, "3. 오른쪽 첫 문제입니다", .55, .20, .92, .24),
            Line(6, "4. 오른쪽 둘째 문제입니다", .55, .60, .92, .64));
        var candidates = OcrQuestionSegmenter.SplitByHeader(
            new[] { page },
            new QuestionNumberRange(1, 4));

        var issue = Assert.Single(
            PdfStructuralValidator.Validate(candidates, new[] { page }),
            item => item.Code == "shared-context-image-overlap" && item.Index == 3);
        Assert.Contains("3번", issue.Message);
    }

    [Fact]
    public void BareNumericValueRangeAtLineStart_IsNotSharedContext()
    {
        const string body = "10~20의 값을 보고 답하시오";
        var candidates = OcrQuestionSegmenter.SplitByHeader(
            new[]
            {
                Page(
                    1,
                    Line(1, "9. 아홉째 문제입니다", .05, .05, .45, .09),
                    Line(2, body, .05, .15, .45, .19),
                    Line(3, "10. 열째 문제입니다", .05, .45, .45, .49))
            },
            new QuestionNumberRange(9, 10));

        Assert.Contains(body, candidates[0].PreviewText);
        Assert.All(candidates, candidate => Assert.Empty(candidate.SharedContextRegions));
    }

    [Fact]
    public void NumericValueRangeInsideQuestion_IsNotSharedContext()
    {
        var candidates = OcrQuestionSegmenter.SplitByHeader(
            new[]
            {
                Page(
                    1,
                    Line(1, "5. 다섯째 문제입니다", .05, .05, .45, .09),
                    Line(2, "다음 그래프의 10~20 값을 보고 답하시오", .05, .15, .45, .19),
                    Line(3, "6. 여섯째 문제입니다", .05, .45, .45, .49))
            },
            new QuestionNumberRange(5, 6));

        Assert.Equal(new[] { 5, 6 }, candidates.Select(x => x.Index));
        Assert.Contains("10~20 값", candidates[0].PreviewText);
        Assert.All(candidates, candidate => Assert.Empty(candidate.SharedContextRegions));
    }

    [Fact]
    public void HeaderAndSharedRangeOnSameOcrLine_IsAmbiguous()
    {
        var candidates = OcrQuestionSegmenter.SplitByHeader(
            new[]
            {
                Page(
                    1,
                    Line(1, "6. [6~8] 다음 글을 보고 답하시오", .05, .05, .45, .09),
                    Line(2, "7. 일곱째 문제입니다", .05, .45, .45, .49),
                    Line(3, "8. 여덟째 문제입니다", .05, .75, .45, .79))
            },
            new QuestionNumberRange(6, 8));

        Assert.Equal(new[] { 6, 7, 8 }, candidates.Select(x => x.Index));
        Assert.All(candidates, candidate => Assert.True(candidate.HasAmbiguousBoundary));
    }

    [Fact]
    public void MultipleHeadersInOnePhysicalLine_AreMarkedAmbiguous()
    {
        var candidates = OcrQuestionSegmenter.SplitByHeader(
            new[]
            {
                Page(
                    1,
                    Line(1, "1. 첫 문제입니다 2. 둘째 문제입니다 3. 셋째 문제입니다", .05, .10, .95, .16))
            },
            new QuestionNumberRange(1, 3));

        Assert.Equal(new[] { 1, 2, 3 }, candidates.Select(x => x.Index));
        Assert.All(candidates, candidate => Assert.True(candidate.HasAmbiguousBoundary));
    }

    [Fact]
    public void CompetingSameIndex_WithEqualOrStrongerMarkerSyntax_IsAmbiguous()
    {
        var candidates = OcrQuestionSegmenter.SplitByHeader(
            new[]
            {
                Page(
                    1,
                    Line(1, "1. 네트워크 계층 구조이다", .05, .05, .45, .09),
                    Line(2, "본문 설명이다", .08, .12, .45, .16),
                    Line(3, "2) 다음 문제 설명 중 옳은 것은?", .05, .20, .45, .24),
                    Line(4, "보기 설명이다", .08, .27, .45, .31),
                    Line(5, "2. 계층 구조이다", .05, .48, .45, .52))
            },
            new QuestionNumberRange(1, 2));

        Assert.Equal(new[] { 1, 2 }, candidates.Select(x => x.Index));
        Assert.All(candidates, candidate => Assert.True(candidate.HasAmbiguousBoundary));
    }

    [Fact]
    public void StrongLowerRankSameIndexCompetitor_IsStillAmbiguous()
    {
        var candidates = OcrQuestionSegmenter.SplitByHeader(
            new[]
            {
                Page(
                    1,
                    Line(1, "1) 실제 첫 문제입니다", .05, .05, .45, .09),
                    Line(2, "첫 문제 본문입니다", .08, .12, .45, .16),
                    Line(3, "2. 다음 문제 설명 중 옳은 것은?", .05, .22, .45, .26),
                    Line(4, "보기 본문입니다", .08, .30, .45, .34),
                    Line(5, "2) 실제 둘째 문제입니다", .05, .45, .45, .49),
                    Line(6, "둘째 문제 본문입니다", .08, .55, .45, .59),
                    Line(7, "3) 실제 셋째 문제입니다", .05, .72, .45, .76))
            },
            new QuestionNumberRange(1, 3));

        Assert.Equal(new[] { 1, 2, 3 }, candidates.Select(x => x.Index));
        Assert.All(candidates, candidate => Assert.True(candidate.HasAmbiguousBoundary));
    }

    [Fact]
    public void WeakLowerRankCompetitor_MatchingNeighborSyntaxIsAmbiguous()
    {
        var candidates = OcrQuestionSegmenter.SplitByHeader(
            new[]
            {
                Page(
                    1,
                    Line(1, "1) 첫 번째 문제입니다", .05, .05, .45, .09),
                    Line(2, "첫 문제 본문입니다", .08, .12, .45, .16),
                    Line(3, "2. 다음 문제 설명 중 옳은 것은?", .05, .22, .45, .26),
                    Line(4, "보기 본문입니다", .08, .30, .45, .34),
                    Line(5, "2) 계층 구조이다", .05, .45, .45, .49),
                    Line(6, "둘째 본문입니다", .08, .55, .45, .59),
                    Line(7, "3) 세 번째 문제입니다", .05, .72, .45, .76))
            },
            new QuestionNumberRange(1, 3));

        Assert.Equal(new[] { 1, 2, 3 }, candidates.Select(x => x.Index));
        Assert.All(candidates, candidate => Assert.True(candidate.HasAmbiguousBoundary));
    }

    [Fact]
    public void EverySameIndexCompetitor_IsEvaluatedForNeighborSyntax()
    {
        var candidates = OcrQuestionSegmenter.SplitByHeader(
            new[]
            {
                Page(
                    1,
                    Line(1, "1) 첫 번째 문제입니다", .05, .05, .45, .09),
                    Line(2, "첫 문제 본문입니다", .08, .12, .45, .16),
                    Line(3, "문제 2. 다음 문제 설명 중 옳은 것은?", .05, .22, .45, .26),
                    Line(4, "2. 임시 계층 구조이다", .05, .34, .45, .38),
                    Line(5, "2) 실제 계층 구조이다", .05, .46, .45, .50),
                    Line(6, "3) 셋째 문제입니다", .05, .72, .45, .76))
            },
            new QuestionNumberRange(1, 3));

        Assert.Equal(new[] { 1, 2, 3 }, candidates.Select(x => x.Index));
        Assert.All(candidates, candidate => Assert.True(candidate.HasAmbiguousBoundary));
    }

    [Fact]
    public void CompetingFirstMarker_IsAlsoCheckedForAmbiguity()
    {
        var candidates = OcrQuestionSegmenter.SplitByHeader(
            new[]
            {
                Page(
                    1,
                    Line(1, "1. 계층 구조이다", .05, .08, .45, .12),
                    Line(2, "본문 설명이다", .08, .18, .45, .22),
                    Line(3, "1번 다음 문제 설명 중 옳은 것은?", .05, .48, .45, .52))
            },
            new QuestionNumberRange(1, 1));

        var candidate = Assert.Single(candidates);
        Assert.True(candidate.HasAmbiguousBoundary);
    }

    [Fact]
    public void WeakQuestionNumberReset_OnNewPageIsNotMixedIntoOneSequence()
    {
        var candidates = OcrQuestionSegmenter.SplitByHeader(
            new[]
            {
                Page(
                    1,
                    Line(1, "1. 계층 구조이다", .05, .10, .45, .14),
                    Line(2, "2. 전송 구조이다", .05, .35, .45, .39),
                    Line(3, "3. 응용 구조이다", .05, .60, .45, .64)),
                Page(
                    2,
                    Line(1, "1. 계층 구조이다", .05, .10, .45, .14),
                    Line(2, "2. 옳은 것은 무엇인가", .05, .35, .45, .39),
                    Line(3, "3. 틀린 것은 무엇인가", .05, .60, .45, .64))
            },
            new QuestionNumberRange(1, 3));

        Assert.Equal(2, candidates.Count(x => x.Index == 1));
        Assert.Equal(2, candidates.Count(x => x.Index == 2));
        Assert.Equal(2, candidates.Count(x => x.Index == 3));
    }

    [Fact]
    public void PageReset_IsFoundEvenWhenTrailingChoicesHaveSmallerNumbers()
    {
        var candidates = OcrQuestionSegmenter.SplitByHeader(
            new[]
            {
                Page(
                    1,
                    Line(1, "21. 첫 문제입니다", .05, .05, .45, .09),
                    Line(2, "22) 둘째 문제입니다", .05, .15, .45, .19),
                    Line(3, "23) 셋째 문제입니다", .05, .25, .45, .29),
                    Line(4, "24) 넷째 문제입니다", .05, .35, .45, .39),
                    Line(5, "25) 다섯째 문제입니다", .05, .45, .45, .49),
                    Line(6, "1) 첫 번째 보기입니다", .05, .60, .45, .64),
                    Line(7, "2) 두 번째 보기입니다", .05, .68, .45, .72),
                    Line(8, "3) 세 번째 보기입니다", .05, .76, .45, .80),
                    Line(9, "4) 네 번째 보기입니다", .05, .84, .45, .88)),
                Page(
                    2,
                    Line(1, "21) 새 시험 첫 문제입니다", .05, .05, .45, .09),
                    Line(2, "22. 새 시험 둘째 문제입니다", .05, .20, .45, .24),
                    Line(3, "23. 새 시험 셋째 문제입니다", .05, .35, .45, .39),
                    Line(4, "24. 새 시험 넷째 문제입니다", .05, .50, .45, .54),
                    Line(5, "25. 새 시험 다섯째 문제입니다", .05, .65, .45, .69))
            },
            new QuestionNumberRange(21, 25));

        Assert.Equal(2, candidates.Count(x => x.Index == 21));
        Assert.Equal(2, candidates.Count(x => x.Index == 22));
        Assert.Equal(2, candidates.Count(x => x.Index == 23));
        Assert.Equal(2, candidates.Count(x => x.Index == 24));
        Assert.Equal(2, candidates.Count(x => x.Index == 25));
    }

    [Fact]
    public void RangeLessContextBetweenQuestions_RemainsInPreviousQuestion()
    {
        const string body = "다음 글을 읽고 답하시오";
        var candidates = OcrQuestionSegmenter.SplitByHeader(
            new[]
            {
                Page(
                    1,
                    Line(1, "1. 첫 문제입니다", .05, .08, .45, .12),
                    Line(2, body, .05, .20, .45, .24),
                    Line(3, "지문 내용입니다", .05, .28, .45, .34),
                    Line(4, "2. 둘째 문제입니다", .05, .55, .45, .59))
            },
            new QuestionNumberRange(1, 2));

        Assert.Contains(body, candidates[0].PreviewText);
        Assert.All(candidates, candidate => Assert.Empty(candidate.SharedContextRegions));
    }

    [Theory]
    [InlineData("2번 반복한다")]
    [InlineData("2번 실행한다")]
    [InlineData("2번 클릭한다")]
    public void NumberOfActions_IsNotTreatedAsQuestionHeader(string body)
    {
        var candidates = OcrQuestionSegmenter.SplitByHeader(
            new[]
            {
                Page(
                    1,
                    Line(1, "1. 첫 문제입니다", .05, .08, .45, .12),
                    Line(2, body, .05, .20, .45, .24),
                    Line(3, "2. 둘째 문제입니다", .05, .55, .45, .59))
            },
            new QuestionNumberRange(1, 2));

        Assert.Equal(new[] { 1, 2 }, candidates.Select(x => x.Index));
        Assert.Contains(body, candidates[0].PreviewText);
    }

    [Fact]
    public void ImperativeQuestionHeaders_AreStrongQuestionEvidence()
    {
        var candidates = OcrQuestionSegmenter.SplitByHeader(
            new[]
            {
                Page(
                    1,
                    Line(1, "1. 옳은 것을 고르시오", .05, .10, .45, .14),
                    Line(2, "2. 틀린 것은 무엇인가", .05, .40, .45, .44))
            },
            new QuestionNumberRange(1, 2));

        Assert.Equal(new[] { 1, 2 }, candidates.Select(x => x.Index));
        Assert.All(candidates, candidate => Assert.False(candidate.HasAmbiguousBoundary));
    }

    [Fact]
    public void RightColumnQuestion_BeforeFullWidthHeaderHasSemanticRegion()
    {
        var candidates = OcrQuestionSegmenter.SplitByHeader(
            new[]
            {
                Page(
                    1,
                    Line(1, "1. 왼쪽 문제입니다", .05, .10, .42, .14),
                    Line(2, "왼쪽 본문입니다", .08, .20, .42, .24),
                    Line(3, "2. 오른쪽 문제입니다", .55, .10, .92, .14),
                    Line(4, "오른쪽 본문입니다", .58, .20, .92, .24),
                    Line(5, "3. 다음 전폭 문제입니다", .05, .72, .95, .76))
            },
            new QuestionNumberRange(1, 3));

        Assert.Equal(new[] { 1, 2, 3 }, candidates.Select(x => x.Index));
        Assert.NotEmpty(candidates[1].ImageRegions);
        Assert.All(candidates[1].ImageRegions, region => Assert.Equal(1, region.ColumnIndex));
        Assert.False(candidates[1].HasAmbiguousBoundary);
    }

    [Fact]
    public void OverlappingOcrBoxes_MakeSharedBoundaryAmbiguous()
    {
        var candidates = OcrQuestionSegmenter.SplitByHeader(
            new[]
            {
                Page(
                    1,
                    Line(1, "1. 첫 문제입니다", .05, .10, .45, .14),
                    Line(2, "겹쳐 인식된 본문입니다", .05, .42, .45, .66),
                    Line(3, "2. 둘째 문제입니다", .05, .60, .45, .64))
            },
            new QuestionNumberRange(1, 2));

        Assert.Equal(new[] { 1, 2 }, candidates.Select(x => x.Index));
        Assert.All(candidates, candidate => Assert.True(candidate.HasAmbiguousBoundary));
    }

    [Fact]
    public void RowMajorBoundary_SearchesPreviousLineInSameColumn()
    {
        var candidates = OcrQuestionSegmenter.SplitByHeader(
            new[]
            {
                Page(
                    1,
                    Line(1, "1. 왼쪽 첫 문제입니다", .05, .10, .42, .14),
                    Line(2, "2. 오른쪽 첫 문제입니다", .55, .10, .92, .14),
                    Line(3, "왼쪽 겹친 본문입니다", .08, .20, .42, .60),
                    Line(4, "오른쪽 본문입니다", .58, .20, .92, .24),
                    Line(5, "3. 왼쪽 둘째 문제입니다", .05, .55, .42, .59),
                    Line(6, "4. 오른쪽 둘째 문제입니다", .55, .55, .92, .59))
            },
            new QuestionNumberRange(1, 4));

        Assert.Equal(new[] { 1, 2, 3, 4 }, candidates.Select(x => x.Index));
        Assert.True(candidates[0].HasAmbiguousBoundary);
        Assert.True(candidates[2].HasAmbiguousBoundary);
    }

    [Fact]
    public void DuplicatePageIndexes_AreNeverTreatedAsReliableOrder()
    {
        var candidates = OcrQuestionSegmenter.SplitByHeader(
            new[]
            {
                Page(1, Line(1, "1. 첫 문제입니다", .05, .10, .45, .14)),
                Page(1, Line(1, "2. 둘째 문제입니다", .05, .50, .45, .54))
            },
            new QuestionNumberRange(1, 2));

        Assert.Equal(new[] { 1, 2 }, candidates.Select(x => x.Index));
        Assert.All(candidates, candidate => Assert.True(candidate.HasAmbiguousBoundary));
    }

    [Fact]
    public void SyntheticFallbackCoordinates_AreNotPromotedToReliableGeometry()
    {
        var candidates = OcrQuestionSegmenter.SplitByHeader(
            new[]
            {
                Page(
                    1,
                    Line(1, "1. 첫 문제입니다", 0d, .05, 1d, .45, hasReliableGeometry: false),
                    Line(2, "2. 둘째 문제입니다", 0d, .50, 1d, .95, hasReliableGeometry: false))
            },
            new QuestionNumberRange(1, 2));

        Assert.Equal(new[] { 1, 2 }, candidates.Select(x => x.Index));
        Assert.All(candidates, candidate => Assert.True(candidate.HasAmbiguousBoundary));
    }

    [Fact]
    public void FullWidthQuestionHeader_DoesNotCaptureOtherColumnQuestion()
    {
        var candidates = OcrQuestionSegmenter.SplitByHeader(
            new[]
            {
                Page(
                    1,
                    Line(1, "1. 전폭 문제입니다", .05, .05, .95, .09),
                    Line(2, "2. 왼쪽 문제입니다", .05, .50, .42, .54),
                    Line(3, "왼쪽 본문입니다", .08, .62, .42, .66),
                    Line(4, "3. 오른쪽 문제입니다", .55, .10, .92, .14),
                    Line(5, "오른쪽 본문입니다", .58, .22, .92, .26))
            },
            new QuestionNumberRange(1, 3));

        Assert.Equal(new[] { 1, 2, 3 }, candidates.Select(x => x.Index));
        var firstRegions = candidates[0].ImageRegions;
        Assert.Contains(firstRegions, region => region.ColumnIndex == -1 && region.BottomRatio < .20d);
        Assert.DoesNotContain(
            firstRegions,
            region => region.ColumnIndex == -1 && region.BottomRatio > .20d);
        Assert.DoesNotContain(
            firstRegions,
            region => region.ColumnIndex == 1 && region.TopRatio < .20d);
        Assert.False(
            PdfStructuralValidator.SemanticImageRegionsOverlap(candidates[0], candidates[2]),
            $"q1={string.Join(';', candidates[0].ImageRegions)} q3={string.Join(';', candidates[2].ImageRegions)}");
    }

    [Fact]
    public void ReversedPageInput_IsSortedByPageIndex()
    {
        var candidates = OcrQuestionSegmenter.SplitByHeader(
            new[]
            {
                Page(2, Line(1, "2. 둘째 문제입니다", .05, .10, .45, .14)),
                Page(1, Line(1, "1. 첫 문제입니다", .05, .10, .45, .14))
            },
            new QuestionNumberRange(1, 2));

        Assert.Equal(new[] { 1, 2 }, candidates.Select(x => x.Index));
        Assert.Equal(1, candidates[0].StartPage);
        Assert.Equal(2, candidates[1].StartPage);
    }

    [Fact]
    public void MultiPageQuestion_TransitionsFromRightToNextPageLeftColumn()
    {
        var candidates = OcrQuestionSegmenter.SplitByHeader(
            new[]
            {
                Page(
                    1,
                    Line(1, "앞부분 일반 텍스트", .05, .10, .42, .14),
                    Line(2, "앞부분 보조 텍스트", .05, .30, .42, .34),
                    Line(3, "1. 첫 문제입니다", .55, .10, .92, .14),
                    Line(4, "첫 문제 오른쪽 본문", .58, .20, .92, .24)),
                Page(
                    2,
                    Line(1, "첫 문제의 다음 페이지 지문", .05, .10, .42, .14),
                    Line(2, "2. 둘째 문제입니다", .05, .50, .42, .54),
                    Line(3, "오른쪽 일반 텍스트", .55, .10, .92, .14),
                    Line(4, "오른쪽 보조 텍스트", .55, .30, .92, .34))
            },
            new QuestionNumberRange(1, 2));

        Assert.Equal(new[] { 1, 2 }, candidates.Select(x => x.Index));
        Assert.Collection(
            candidates[0].ImageRegions,
            firstPage =>
            {
                Assert.Equal(1, firstPage.PageIndex);
                Assert.Equal(1, firstPage.ColumnIndex);
            },
            secondPage =>
            {
                Assert.Equal(2, secondPage.PageIndex);
                Assert.Equal(0, secondPage.ColumnIndex);
                Assert.Equal(candidates[1].ImageRegions[0].TopRatio, secondPage.BottomRatio, precision: 8);
            });
    }

    private static OcrLineResult Line(
        int lineInPage,
        string text,
        double left,
        double top,
        double right,
        double bottom,
        bool hasReliableGeometry = true)
    {
        return new OcrLineResult(
            lineInPage,
            text,
            left,
            top,
            right,
            bottom,
            hasReliableGeometry);
    }

    private static OcrPageResult Page(int pageIndex, params OcrLineResult[] lines)
    {
        return new OcrPageResult(
            pageIndex,
            string.Join('\n', lines.Select(x => x.Text)),
            lines.Sum(x => x.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length),
            100f,
            Lines: lines);
    }
}
