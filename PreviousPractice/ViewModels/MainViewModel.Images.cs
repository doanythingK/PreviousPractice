using System.Collections.ObjectModel;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PreviousPractice.Core;
using PreviousPractice.Data;
using PreviousPractice.Infrastructure;
using PreviousPractice.Models;
using PreviousPractice.Services;
using Microsoft.Maui.Storage;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Devices;

namespace PreviousPractice.ViewModels;

public partial class MainViewModel
{
    internal static QuestionImageSegment[] BuildQuestionImageSegments(
        OcrQuestionCandidate? candidate,
        IReadOnlyList<OcrQuestionCandidate> allCandidates,
        IReadOnlyList<OcrPageResult> pages)
    {
        if (candidate == null || pages.Count == 0)
        {
            return Array.Empty<QuestionImageSegment>();
        }

        var pageByIndex = pages
            .Where(x => !string.IsNullOrWhiteSpace(x.ImagePath))
            .GroupBy(x => x.PageIndex)
            .ToDictionary(x => x.Key, x => x.First());

        var imageRegions = candidate.ImageRegions ?? Array.Empty<OcrQuestionImageRegion>();
        var sharedContextRegions = candidate.SharedContextRegions ?? Array.Empty<OcrQuestionImageRegion>();
        if (candidate.UsesSemanticImageRegions || imageRegions.Count > 0)
        {
            return sharedContextRegions
                .Concat(imageRegions)
                .Select(region => TryCreateQuestionImageSegment(region, pageByIndex))
                .Where(segment => segment != null)
                .Select(segment => segment!)
                .GroupBy(x => $"{x.PageIndex}:{x.ImageLeftRatio:F6}:{x.ImageTopRatio:F6}:{x.ImageRightRatio:F6}:{x.ImageBottomRatio:F6}")
                .Select(x => x.First())
                .ToArray();
        }

        var sharedSegments = BuildSharedContextImageSegments(candidate, allCandidates, pageByIndex);
        var segments = new List<QuestionImageSegment>();
        for (var pageIndex = candidate.StartPage; pageIndex <= candidate.EndPage; pageIndex++)
        {
            if (!pageByIndex.TryGetValue(pageIndex, out var page) || string.IsNullOrWhiteSpace(page.ImagePath))
            {
                continue;
            }

            var top = pageIndex == candidate.StartPage
                ? ResolveTopImageRatio(candidate.StartLineInPage, candidate.StartPageLineCount)
                : 0d;
            var bottom = pageIndex == candidate.EndPage
                ? ResolveBottomImageRatio(
                    candidate.EndLineInPage,
                    candidate.EndPageLineCount <= 0 ? candidate.StartPageLineCount : candidate.EndPageLineCount,
                    top)
                : 1d;
            var left = 0d;
            var right = 1d;

            if (page.Lines != null && page.Lines.Count > 0)
            {
                var anchorLine = ResolveAnchorLine(candidate, pageIndex, pageByIndex);
                if (anchorLine != null)
                {
                    var topBoundary = pageIndex == candidate.StartPage
                        ? ClampRatio(anchorLine.TopRatio - QuestionImageCropPaddingRatio)
                        : 0d;
                    var nextHeaderTop = ResolveNextHeaderTopRatio(
                        candidate,
                        allCandidates,
                        pageIndex,
                        pageByIndex,
                        anchorLine);
                    var segmentLines = page.Lines
                        .Where(x =>
                            !string.IsNullOrWhiteSpace(x.Text) &&
                            IsSameColumnLine(x, anchorLine) &&
                            x.BottomRatio >= topBoundary &&
                            (!nextHeaderTop.HasValue || x.TopRatio < nextHeaderTop.Value))
                        .ToArray();

                    if (segmentLines.Length > 0)
                    {
                        var horizontalCropLines = segmentLines
                            .Where(x => IsHorizontalCropLine(x, anchorLine))
                            .ToArray();
                        var cropLines = horizontalCropLines.Length > 0
                            ? horizontalCropLines
                            : segmentLines;

                        left = ClampRatio(cropLines.Min(x => x.LeftRatio) - QuestionImageCropPaddingRatio);
                        top = ClampRatio(segmentLines.Min(x => x.TopRatio) - QuestionImageCropPaddingRatio);
                        right = ClampRatio(cropLines.Max(x => x.RightRatio) + QuestionImageCropPaddingRatio);
                        bottom = ClampRatio(segmentLines.Max(x => x.BottomRatio) + QuestionImageCropPaddingRatio);
                    }
                    else if (pageIndex != candidate.StartPage)
                    {
                        continue;
                    }
                }
                else
                {
                    var segmentStartLine = pageIndex == candidate.StartPage ? candidate.StartLineInPage : 1;
                    var segmentEndLine = pageIndex == candidate.EndPage
                        ? candidate.EndLineInPage
                        : page.Lines.Max(x => x.LineInPage);
                    var segmentLines = page.Lines
                        .Where(x => x.LineInPage >= segmentStartLine && x.LineInPage <= segmentEndLine)
                        .ToArray();

                    if (segmentLines.Length > 0)
                    {
                        left = ClampRatio(segmentLines.Min(x => x.LeftRatio) - QuestionImageCropPaddingRatio);
                        top = ClampRatio(segmentLines.Min(x => x.TopRatio) - QuestionImageCropPaddingRatio);
                        right = ClampRatio(segmentLines.Max(x => x.RightRatio) + QuestionImageCropPaddingRatio);
                        bottom = ClampRatio(segmentLines.Max(x => x.BottomRatio) + QuestionImageCropPaddingRatio);
                    }
                }
            }

            right = EnsureMinimumSpan(left, right, MinQuestionImageSliceWidthRatio);
            bottom = EnsureMinimumSpan(top, bottom, MinQuestionImageSliceRatio);

            segments.Add(CreateQuestionImageSegment(page, left, top, right, bottom));
        }

        return sharedSegments
            .Concat(segments)
            .GroupBy(x => $"{x.PageIndex}:{x.ImageLeftRatio:F4}:{x.ImageTopRatio:F4}:{x.ImageRightRatio:F4}:{x.ImageBottomRatio:F4}")
            .Select(x => x.First())
            .ToArray();
    }

    private static QuestionImageSegment[] BuildSharedContextImageSegments(
        OcrQuestionCandidate candidate,
        IReadOnlyList<OcrQuestionCandidate> allCandidates,
        IReadOnlyDictionary<int, OcrPageResult> pageByIndex)
    {
        if (!pageByIndex.TryGetValue(candidate.StartPage, out var page) ||
            page.Lines == null ||
            page.Lines.Count == 0 ||
            !TryGetLine(pageByIndex, candidate.StartPage, candidate.StartLineInPage, out var anchorLine) ||
            anchorLine == null)
        {
            return Array.Empty<QuestionImageSegment>();
        }

        var sharedContext = ResolveSharedContextDefinition(candidate, allCandidates, page.Lines, anchorLine);
        if (sharedContext == null)
        {
            return Array.Empty<QuestionImageSegment>();
        }

        var contextLines = page.Lines
            .Where(x =>
                x.LineInPage >= sharedContext.Value.ContextStartLine &&
                x.LineInPage < sharedContext.Value.QuestionStartLine &&
                !string.IsNullOrWhiteSpace(x.Text) &&
                IsSameColumnLine(x, anchorLine))
            .OrderBy(x => x.LineInPage)
            .ToArray();
        if (contextLines.Length == 0)
        {
            return Array.Empty<QuestionImageSegment>();
        }

        var sharedSegment = BuildImageSegmentFromLines(page, contextLines, contextLines[0]);
        return sharedSegment == null
            ? Array.Empty<QuestionImageSegment>()
            : new[] { sharedSegment };
    }

    private static SharedContextDefinition? ResolveSharedContextDefinition(
        OcrQuestionCandidate candidate,
        IReadOnlyList<OcrQuestionCandidate> allCandidates,
        IReadOnlyList<OcrLineResult> pageLines,
        OcrLineResult anchorLine)
    {
        var samePageCandidates = allCandidates
            .Where(x => x.StartPage == candidate.StartPage && x.Index > 0)
            .OrderBy(x => x.StartLineInPage)
            .ToArray();
        if (samePageCandidates.Length == 0)
        {
            return null;
        }

        for (var lineIndex = candidate.StartLineInPage - 1; lineIndex >= 1; lineIndex--)
        {
            var markerLine = pageLines.FirstOrDefault(x => x.LineInPage == lineIndex);
            if (markerLine == null ||
                string.IsNullOrWhiteSpace(markerLine.Text) ||
                !IsSameColumnLine(markerLine, anchorLine))
            {
                continue;
            }

            if (!TryResolveSharedContextRange(markerLine.Text, candidate.Index, out var sharedRange))
            {
                continue;
            }

            var firstCandidateInRange = samePageCandidates
                .Where(x => sharedRange.Contains(x.Index))
                .OrderBy(x => x.StartLineInPage)
                .FirstOrDefault();
            if (firstCandidateInRange == null ||
                firstCandidateInRange.StartLineInPage <= markerLine.LineInPage)
            {
                continue;
            }

            return new SharedContextDefinition(
                sharedRange,
                markerLine.LineInPage,
                firstCandidateInRange.StartLineInPage);
        }

        foreach (var priorCandidate in samePageCandidates
                     .Where(x => x.StartLineInPage < candidate.StartLineInPage && x.Index < candidate.Index)
                     .OrderByDescending(x => x.StartLineInPage))
        {
            var priorHeaderLine = pageLines.FirstOrDefault(x => x.LineInPage == priorCandidate.StartLineInPage);
            if (priorHeaderLine == null ||
                string.IsNullOrWhiteSpace(priorHeaderLine.Text) ||
                !IsSameColumnLine(priorHeaderLine, anchorLine))
            {
                continue;
            }

            if (!TryResolveSharedContextRange(
                    priorHeaderLine.Text,
                    candidate.Index,
                    priorCandidate.Index,
                    out var sharedRange))
            {
                continue;
            }

            if (!sharedRange.Contains(candidate.Index))
            {
                continue;
            }

            return new SharedContextDefinition(
                sharedRange,
                priorCandidate.StartLineInPage,
                candidate.StartLineInPage);
        }

        return null;
    }

    private static bool TryResolveSharedContextRange(
        string? text,
        int currentIndex,
        out QuestionNumberRange questionRange)
    {
        return TryResolveSharedContextRange(text, currentIndex, fallbackStartIndex: null, out questionRange);
    }

    private static bool TryResolveSharedContextRange(
        string? text,
        int currentIndex,
        int? fallbackStartIndex,
        out QuestionNumberRange questionRange)
    {
        questionRange = default;
        if (!LooksLikeSharedContextMarkerText(text))
        {
            return false;
        }

        var normalized = text!.Trim();
        var explicitMatch = QuestionRangeHintRegex.Match(normalized);
        if (explicitMatch.Success &&
            int.TryParse(explicitMatch.Groups["start"].Value, out var explicitStart) &&
            int.TryParse(explicitMatch.Groups["end"].Value, out var explicitEnd) &&
            explicitStart > 0 &&
            explicitEnd >= explicitStart)
        {
            questionRange = new QuestionNumberRange(explicitStart, explicitEnd);
            return questionRange.Contains(currentIndex);
        }

        var startOnlyMatch = SharedContextRangeStartRegex.Match(normalized);
        if (startOnlyMatch.Success &&
            int.TryParse(startOnlyMatch.Groups["start"].Value, out var inferredStart) &&
            inferredStart > 0)
        {
            questionRange = new QuestionNumberRange(
                inferredStart,
                inferredStart + MalformedSharedContextFallbackQuestionCount - 1);
            if (questionRange.Contains(currentIndex))
            {
                return true;
            }
        }

        if (!fallbackStartIndex.HasValue || fallbackStartIndex.Value <= 0)
        {
            return false;
        }

        var endOnlyMatch = SharedContextRangeEndRegex.Match(normalized);
        if (!endOnlyMatch.Success ||
            !int.TryParse(endOnlyMatch.Groups["end"].Value, out var inferredEnd) ||
            inferredEnd < fallbackStartIndex.Value)
        {
            return false;
        }

        questionRange = new QuestionNumberRange(fallbackStartIndex.Value, inferredEnd);
        return questionRange.Contains(currentIndex);
    }

    private void UpdateCurrentQuestionImageSlices(Question? question)
    {
        CurrentQuestionImageSlices.Clear();
        var result = QuestionImageSliceBuilder.BuildWithStatus(
            question,
            CurrentQuestionImageViewportWidth,
            CurrentQuestionImageViewportHeight,
            MinQuestionImageSliceRatio,
            MinQuestionImageSliceWidthRatio,
            QuestionImageZoom);
        foreach (var slice in result.Slices)
        {
            CurrentQuestionImageSlices.Add(slice);
        }

        QuestionImageNotice = result.UnavailableSegmentCount switch
        {
            <= 0 => string.Empty,
            _ when result.Slices.Count == 0 =>
                "저장된 문제 이미지를 열 수 없어 OCR 텍스트로 대신 표시합니다.",
            _ =>
                $"문제 이미지 {result.StoredSegmentCount}개 중 {result.UnavailableSegmentCount}개를 열 수 없습니다. 파일 검토 화면에서 확인해 주세요."
        };

        OnPropertyChanged(nameof(HasCurrentQuestionImages));
        OnPropertyChanged(nameof(ShowCurrentQuestionText));
    }
}
