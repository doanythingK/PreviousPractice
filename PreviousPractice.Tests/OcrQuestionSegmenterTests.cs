using PreviousPractice.Infrastructure;
using PreviousPractice.Models;
using System.Reflection;

namespace PreviousPractice.Tests;

public sealed class OcrQuestionSegmenterTests
{
    [Fact]
    public void HeaderlessOcr_DoesNotCreateLineCountFallbackCandidates()
    {
        var pages = new[]
        {
            Page(
                1,
                Enumerable.Range(1, 20)
                    .Select(index => $"번호 없는 일반 OCR 본문 {index}번째 줄입니다")
                    .ToArray())
        };

        var candidates = OcrQuestionSegmenter.SplitByHeader(pages);

        Assert.Empty(candidates);
    }

    [Fact]
    public void MissingNumber_IsNotInventedFromQuestionSentence()
    {
        var pages = new[]
        {
            Page(
                1,
                "1. 첫 문제",
                "첫 문제 본문",
                "어떤 설명이 옳은가?",
                "보기 내용입니다",
                "3. 셋째 문제")
        };

        var candidates = OcrQuestionSegmenter.SplitByHeader(
            pages,
            new QuestionNumberRange(1, 3));

        Assert.DoesNotContain(candidates, candidate => candidate.Index == 2);
        var first = Assert.Single(candidates, candidate => candidate.Index == 1);
        Assert.Contains("어떤 설명이 옳은가?", first.PreviewText);
    }

    [Fact]
    public void MultiPageQuestion_KeepsContinuationBeforeNextHeader()
    {
        var pages = new[]
        {
            Page(1, "1. 첫 문제", "첫 페이지 본문"),
            Page(2, "이전 문제의 다음 페이지 지문", "2. 둘째 문제", "둘째 본문")
        };

        var candidates = OcrQuestionSegmenter.SplitByHeader(pages, new QuestionNumberRange(1, 2));
        var first = Assert.Single(candidates, candidate => candidate.Index == 1);

        Assert.Equal(2, first.EndPage);
        Assert.Contains("다음 페이지 지문", first.PreviewText);
    }

    [Fact]
    public void MissingHeader_DoesNotInventCandidateFromArbitraryBodyLine()
    {
        var pages = new[]
        {
            Page(1, "1. 첫 문제", "보기 문장입니다", "3. 셋째 문제")
        };

        var candidates = OcrQuestionSegmenter.SplitByHeader(pages, new QuestionNumberRange(1, 3));

        Assert.DoesNotContain(candidates, candidate => candidate.Index == 2);
    }

    [Fact]
    public void HangulHeaders_AreNormalizedSequentially()
    {
        var pages = new[]
        {
            Page(1, "가. 첫 문제", "나. 둘째 문제", "다. 셋째 문제")
        };

        var indexes = OcrQuestionSegmenter.SplitByHeader(pages)
            .Select(candidate => candidate.Index)
            .ToArray();

        Assert.Equal(new[] { 1, 2, 3 }, indexes);
    }

    [Fact]
    public void PercentageRange_IsNotUsedAsQuestionRangeHint()
    {
        var pages = new[]
        {
            Page(1, "성공률은 10-20% 범위이다.", "1. 첫 문제", "2. 둘째 문제")
        };

        var indexes = OcrQuestionSegmenter.SplitByHeader(pages)
            .Select(candidate => candidate.Index)
            .ToArray();

        Assert.Contains(1, indexes);
        Assert.Contains(2, indexes);
    }

    [Fact]
    public void CombinedDocument_NumberResetIsPreservedAsDuplicateCandidates()
    {
        var pages = new[]
        {
            Page(1, "1. 첫 문제", "2. 둘째 문제", "3. 셋째 문제", "4. 넷째 문제", "5. 다섯째 문제"),
            Page(2, "1. 새 시험 첫 문제", "2. 새 시험 둘째 문제", "3. 새 시험 셋째 문제")
        };

        var candidates = OcrQuestionSegmenter.SplitByHeader(pages);

        AssertDuplicateIndex(candidates, 1);
        AssertDuplicateIndex(candidates, 2);
        AssertDuplicateIndex(candidates, 3);
    }

    [Fact]
    public void CombinedDocument_NonOneRangeResetIsPreservedAsDuplicateCandidates()
    {
        var pages = new[]
        {
            Page(1, "21. 첫 문제", "22. 둘째 문제", "23. 셋째 문제", "24. 넷째 문제", "25. 다섯째 문제"),
            Page(2, "21. 새 시험 첫 문제", "22. 새 시험 둘째 문제", "23. 새 시험 셋째 문제")
        };

        var candidates = OcrQuestionSegmenter.SplitByHeader(
            pages,
            new QuestionNumberRange(21, 25));

        AssertDuplicateIndex(candidates, 21);
        AssertDuplicateIndex(candidates, 22);
        AssertDuplicateIndex(candidates, 23);
    }

    [Fact]
    public void CombinedDocument_UnexpectedOneBasedResetIsPreservedForRangeValidation()
    {
        var pages = new[]
        {
            Page(1, "21. 첫 문제", "22. 둘째 문제", "23. 셋째 문제", "24. 넷째 문제", "25. 다섯째 문제"),
            Page(2, "1. 새 시험 첫 문제", "2. 새 시험 둘째 문제", "3. 새 시험 셋째 문제")
        };

        var indexes = OcrQuestionSegmenter.SplitByHeader(
                pages,
                new QuestionNumberRange(21, 25))
            .Select(candidate => candidate.Index)
            .ToArray();

        Assert.Equal(new[] { 21, 22, 23, 24, 25, 1, 2, 3 }, indexes);
    }

    [Fact]
    public void NumberInsideBodyText_IsNotRecoveredAsMissingHeader()
    {
        var pages = new[]
        {
            Page(1, "1. 첫 문제", "2개의 보기 중 하나를 고르시오", "3. 셋째 문제")
        };

        var candidates = OcrQuestionSegmenter.SplitByHeader(
            pages,
            new QuestionNumberRange(1, 3));

        Assert.DoesNotContain(candidates, candidate => candidate.Index == 2);
    }

    [Fact]
    public void HangulAndLatinHeaders_UseTheirDeclaredOrdering()
    {
        const string hangulHeaders = "가나다라마바사아자차카타파하";
        var hangulPages = new[]
        {
            Page(1, hangulHeaders.Select((header, index) => $"{header}. {index + 1}번째 문제").ToArray())
        };
        var latinPages = new[]
        {
            Page(1, "A. 첫 문제", "b. 둘째 문제", "C. 셋째 문제")
        };

        var hangulIndexes = OcrQuestionSegmenter.SplitByHeader(hangulPages)
            .Select(candidate => candidate.Index)
            .ToArray();
        var latinIndexes = OcrQuestionSegmenter.SplitByHeader(latinPages)
            .Select(candidate => candidate.Index)
            .ToArray();

        Assert.Equal(Enumerable.Range(1, 14), hangulIndexes);
        Assert.Equal(new[] { 1, 2, 3 }, latinIndexes);
    }

    [Theory]
    [InlineData("성공률은 10-20% 범위이다.")]
    [InlineData("2019-2023년 중 옳은 것을 고르시오.")]
    [InlineData("값이 10-20 사이인 수를 고르시오.")]
    [InlineData("이 작업을 10-20번 반복한다.")]
    [InlineData("이 동작은 10-20번.")]
    public void NonQuestionNumericRange_IsNotInferred(string text)
    {
        Assert.Null(InferQuestionRange(Page(1, text)));
    }

    [Theory]
    [InlineData("문항 범위: 1~35")]
    [InlineData("1-35번")]
    public void ExplicitQuestionRange_IsInferred(string text)
    {
        Assert.Equal(new QuestionNumberRange(1, 35), InferQuestionRange(Page(1, text)));
    }

    [Fact]
    public void ConflictingQuestionRanges_AreNotInferred()
    {
        Assert.Null(InferQuestionRange(Page(1, "1-20번", "21-40번")));
    }

    private static QuestionNumberRange? InferQuestionRange(params OcrPageResult[] pages)
    {
        var method = typeof(OcrQuestionSegmenter).GetMethod(
            "InferQuestionRangeFromPages",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (QuestionNumberRange?)method.Invoke(null, [pages]);
    }

    private static void AssertDuplicateIndex(
        IEnumerable<OcrQuestionCandidate> candidates,
        int index)
    {
        Assert.Collection(
            candidates.Where(candidate => candidate.Index == index),
            _ => { },
            _ => { });
    }

    private static OcrPageResult Page(int pageIndex, params string[] texts)
    {
        var lines = texts
            .Select((text, index) => new OcrLineResult(
                index + 1,
                text,
                0.05d,
                0.05d + (index * 0.1d),
                0.95d,
                0.1d + (index * 0.1d)))
            .ToArray();

        return new OcrPageResult(
            pageIndex,
            string.Join('\n', texts),
            texts.Sum(text => text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length),
            100f,
            Lines: lines);
    }
}
