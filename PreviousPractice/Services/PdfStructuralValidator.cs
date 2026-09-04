using System.Text.RegularExpressions;
using PreviousPractice.Models;

namespace PreviousPractice.Services;

internal static class PdfStructuralValidator
{
    private const int MalformedSharedContextFallbackQuestionCount = 3;
    private static readonly char[] QuestionRangeSeparators = ['-', '~', '〜'];
    private static readonly Regex QuestionRangeHintRegex = new(
        @"(?<!\d)(?<start>\d{1,3})\s*[-~〜]\s*(?<end>\d{1,3})\s*(?:번|문항|문제)?(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SharedContextRangeStartRegex = new(
        @"(?<!\d)(?<start>\d{1,3})\s*[-~〜]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SharedContextRangeEndRegex = new(
        @"[-~〜]\s*(?<end>\d{1,3})(?:번|문항|문제)?(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex StructuralSharedContextMarkerRegex = new(
        @"[<(（【〔［]\s*(?:\d{1,3}|[%A-Za-z가-힣①-⑳㉠-㉻]+)?\s*[-~〜]\s*(?:\d{1,3}|[%A-Za-z가-힣①-⑳㉠-㉻]+)\s*[>)）】〕］]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly string[] StructuralBlockingBoilerplatePhrases =
    [
        "출제위원",
        "출제범위",
        "출석수업대체시험",
        "다음 면에 계속",
        "앞면에서 계속"
    ];

    internal static PdfStructuralIssue[] Validate(
        IReadOnlyList<OcrQuestionCandidate> candidates,
        IReadOnlyList<OcrPageResult> pages)
    {
        var issues = new List<PdfStructuralIssue>();
        foreach (var duplicatePage in pages
                     .GroupBy(x => x.PageIndex)
                     .Where(x => x.Count() > 1))
        {
            issues.Add(new PdfStructuralIssue
            {
                Code = "duplicate-page-index",
                Message = $"p{duplicatePage.Key} 페이지 번호가 {duplicatePage.Count()}번 나타나 문서 순서를 확정할 수 없습니다."
            });
        }

        foreach (var duplicate in candidates
                     .GroupBy(x => x.Index)
                     .Where(x => x.Count() > 1))
        {
            issues.Add(new PdfStructuralIssue
            {
                Code = "duplicate-index",
                Index = duplicate.Key,
                Message = $"{duplicate.Key}번 문항 경계가 {duplicate.Count()}개 검출되었습니다. 합본 또는 번호 재시작 여부를 확인해 주세요."
            });
        }

        var distinctIndexes = candidates
            .Select(x => x.Index)
            .Where(x => x > 0)
            .Distinct()
            .OrderBy(x => x)
            .ToArray();
        for (var index = 1; index < distinctIndexes.Length; index++)
        {
            if (distinctIndexes[index] == distinctIndexes[index - 1] + 1)
            {
                continue;
            }

            issues.Add(new PdfStructuralIssue
            {
                Code = "nonconsecutive-index",
                Index = distinctIndexes[index],
                Message = $"{distinctIndexes[index - 1]}번 다음 문항이 {distinctIndexes[index]}번으로 건너뛰어 번호 연속성을 확인할 수 없습니다."
            });
        }

        var pageByIndex = pages
            .GroupBy(x => x.PageIndex)
            .ToDictionary(x => x.Key, x => x.First());
        var orderedByIndex = candidates
            .OrderBy(x => x.Index)
            .ThenBy(x => x.StartPage)
            .ThenBy(x => x.StartLineInPage)
            .ToArray();

        for (var i = 0; i < orderedByIndex.Length; i++)
        {
            var candidate = orderedByIndex[i];
            if (candidate.HasAmbiguousBoundary)
            {
                issues.Add(new PdfStructuralIssue
                {
                    Code = "ambiguous-boundary",
                    Index = candidate.Index,
                    Message = string.IsNullOrWhiteSpace(candidate.BoundaryIssue)
                        ? $"{candidate.Index}번 문항 경계를 안전하게 결정할 수 없습니다."
                        : $"{candidate.Index}번: {candidate.BoundaryIssue}"
                });
            }

            var imageRegions = candidate.ImageRegions ?? Array.Empty<OcrQuestionImageRegion>();
            var sharedContextRegions = candidate.SharedContextRegions ?? Array.Empty<OcrQuestionImageRegion>();
            if (candidate.UsesSemanticImageRegions && imageRegions.Count == 0)
            {
                issues.Add(new PdfStructuralIssue
                {
                    Code = "missing-image-region",
                    Index = candidate.Index,
                    Message = $"{candidate.Index}번의 의미 기반 이미지 영역을 만들지 못했습니다."
                });
            }

            foreach (var region in sharedContextRegions.Concat(imageRegions))
            {
                if (!double.IsFinite(region.LeftRatio) ||
                    !double.IsFinite(region.TopRatio) ||
                    !double.IsFinite(region.RightRatio) ||
                    !double.IsFinite(region.BottomRatio) ||
                    region.LeftRatio < 0d ||
                    region.TopRatio < 0d ||
                    region.RightRatio > 1d ||
                    region.BottomRatio > 1d ||
                    region.RightRatio <= region.LeftRatio ||
                    region.BottomRatio <= region.TopRatio ||
                    !region.HasReliableGeometry)
                {
                    issues.Add(new PdfStructuralIssue
                    {
                        Code = "invalid-image-region",
                        Index = candidate.Index,
                        Message = $"{candidate.Index}번의 p{region.PageIndex} 이미지 경계가 유효하지 않습니다."
                    });
                }

                if (!pageByIndex.TryGetValue(region.PageIndex, out var regionPage) ||
                    string.IsNullOrWhiteSpace(regionPage.ImagePath) ||
                    !File.Exists(regionPage.ImagePath))
                {
                    issues.Add(new PdfStructuralIssue
                    {
                        Code = "missing-page-image",
                        Index = candidate.Index,
                        Message = $"{candidate.Index}번에 필요한 p{region.PageIndex} 페이지 이미지가 없습니다."
                    });
                }
            }

            var boilerplateProbe = BuildStructuralProbeText(candidate, previewLineLimit: 3);
            var blockingPhrase = FindStructuralBlockingBoilerplatePhrase(boilerplateProbe);
            if (!string.IsNullOrWhiteSpace(blockingPhrase))
            {
                issues.Add(new PdfStructuralIssue
                {
                    Code = "boilerplate-candidate",
                    Index = candidate.Index,
                    Message = $"{candidate.Index}번 후보에 '{blockingPhrase}' 문구가 포함되어 있습니다."
                });
            }

            if (HasInvalidCandidateSpan(candidate))
            {
                issues.Add(new PdfStructuralIssue
                {
                    Code = "invalid-span",
                    Index = candidate.Index,
                    Message = $"{candidate.Index}번 범위가 비정상입니다: p{candidate.StartPage}:{candidate.StartLineInPage} -> p{candidate.EndPage}:{candidate.EndLineInPage}"
                });
            }

            if (HasSuspiciousSharedContextLeak(candidate))
            {
                issues.Add(new PdfStructuralIssue
                {
                    Code = "shared-context-leak",
                    Index = candidate.Index,
                    Message = $"{candidate.Index}번 후보에 현재 번호와 맞지 않는 공통 지문 마커가 섞여 있습니다."
                });
            }

            if (i == 0)
            {
                continue;
            }

            var previous = orderedByIndex[i - 1];
            if (!HasInvalidCandidateSpan(previous) &&
                !HasInvalidCandidateSpan(candidate) &&
                CompareCandidateStart(candidate, previous) <= 0)
            {
                issues.Add(new PdfStructuralIssue
                {
                    Code = "nonincreasing-start",
                    Index = candidate.Index,
                    Message = $"{candidate.Index}번 시작 위치가 {previous.Index}번보다 앞서거나 같습니다."
                });
            }

            if (!HasInvalidCandidateSpan(previous) &&
                !HasInvalidCandidateSpan(candidate) &&
                HasCandidateRangeOverlap(previous, candidate))
            {
                issues.Add(new PdfStructuralIssue
                {
                    Code = "overlapping-range",
                    Index = candidate.Index,
                    Message = $"{previous.Index}번과 {candidate.Index}번 범위가 겹칩니다."
                });
            }
        }

        for (var leftIndex = 0; leftIndex < candidates.Count; leftIndex++)
        {
            for (var rightIndex = leftIndex + 1; rightIndex < candidates.Count; rightIndex++)
            {
                var leftCandidate = candidates[leftIndex];
                var rightCandidate = candidates[rightIndex];
                if (!SemanticImageRegionsOverlap(leftCandidate, rightCandidate))
                {
                    continue;
                }

                issues.Add(new PdfStructuralIssue
                {
                    Code = "overlapping-image-region",
                    Index = rightCandidate.Index,
                    Message = $"{leftCandidate.Index}번과 {rightCandidate.Index}번의 이미지 영역이 겹칩니다."
                });
            }
        }

        var distinctSharedRegions = candidates
            .SelectMany(candidate =>
                candidate.SharedContextRegions ?? Array.Empty<OcrQuestionImageRegion>())
            .GroupBy(region =>
                $"{region.PageIndex}:{region.LeftRatio:F6}:{region.TopRatio:F6}:{region.RightRatio:F6}:{region.BottomRatio:F6}")
            .Select(group => group.First())
            .ToArray();
        foreach (var sharedRegion in distinctSharedRegions)
        {
            foreach (var owner in candidates)
            {
                var ownedRegions = owner.ImageRegions ?? Array.Empty<OcrQuestionImageRegion>();
                if (!ownedRegions.Any(ownedRegion => ImageRegionsOverlap(sharedRegion, ownedRegion)))
                {
                    continue;
                }

                issues.Add(new PdfStructuralIssue
                {
                    Code = "shared-context-image-overlap",
                    Index = owner.Index,
                    Message = $"공통 지문 영역과 {owner.Index}번 문항 이미지 영역이 겹칩니다."
                });
            }
        }

        return issues
            .GroupBy(x => $"{x.Code}:{x.Index}:{x.Message}")
            .Select(x => x.First())
            .OrderBy(x => x.Index ?? int.MaxValue)
            .ThenBy(x => x.Code)
            .ToArray();
    }

    internal static bool SemanticImageRegionsOverlap(
        OcrQuestionCandidate leftCandidate,
        OcrQuestionCandidate rightCandidate)
    {
        var leftRegions = leftCandidate.ImageRegions ?? Array.Empty<OcrQuestionImageRegion>();
        var rightRegions = rightCandidate.ImageRegions ?? Array.Empty<OcrQuestionImageRegion>();
        return leftRegions.Any(left => rightRegions.Any(right => ImageRegionsOverlap(left, right)));
    }

    private static bool ImageRegionsOverlap(
        OcrQuestionImageRegion left,
        OcrQuestionImageRegion right)
    {
        const double overlapTolerance = 0.001d;
        return left.PageIndex == right.PageIndex &&
               Math.Min(left.RightRatio, right.RightRatio) -
                   Math.Max(left.LeftRatio, right.LeftRatio) > overlapTolerance &&
               Math.Min(left.BottomRatio, right.BottomRatio) -
                   Math.Max(left.TopRatio, right.TopRatio) > overlapTolerance;
    }

    private static string BuildStructuralProbeText(
        OcrQuestionCandidate candidate,
        int previewLineLimit)
    {
        return string.Join(
            Environment.NewLine,
            new[] { candidate.Header }
                .Concat(
                    SplitPreviewLines(candidate.PreviewText)
                        .Take(previewLineLimit))
                .Where(text => !string.IsNullOrWhiteSpace(text)));
    }

    private static IEnumerable<string> SplitPreviewLines(string? previewText)
    {
        return string.IsNullOrWhiteSpace(previewText)
            ? Array.Empty<string>()
            : previewText.Split(
                Environment.NewLine,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static bool HasSuspiciousSharedContextLeak(OcrQuestionCandidate candidate)
    {
        foreach (var text in new[] { candidate.Header }
                     .Concat(SplitPreviewLines(candidate.PreviewText).Take(2)))
        {
            if (!LooksLikeStructuralSharedContextMarkerText(text))
            {
                continue;
            }

            if (TryResolveSharedContextRange(text, candidate.Index, out _))
            {
                continue;
            }

            if (TryResolveSharedContextRange(
                    text,
                    candidate.Index,
                    fallbackStartIndex: candidate.Index,
                    out _))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static bool LooksLikeStructuralSharedContextMarkerText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var normalized = text.Trim();
        if (!LooksLikeSharedContextMarkerText(normalized))
        {
            return false;
        }

        return QuestionRangeHintRegex.IsMatch(normalized) ||
               StructuralSharedContextMarkerRegex.IsMatch(normalized) ||
               (normalized.Contains('(') && SharedContextRangeEndRegex.IsMatch(normalized)) ||
               (normalized.Contains('（') && SharedContextRangeEndRegex.IsMatch(normalized)) ||
               (normalized.Contains('(') && SharedContextRangeStartRegex.IsMatch(normalized)) ||
               (normalized.Contains('（') && SharedContextRangeStartRegex.IsMatch(normalized));
    }

    private static string? FindStructuralBlockingBoilerplatePhrase(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return StructuralBlockingBoilerplatePhrases.FirstOrDefault(phrase =>
            text.Contains(phrase, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasInvalidCandidateSpan(OcrQuestionCandidate candidate)
    {
        if (candidate.LogicalStartOrder > 0 || candidate.LogicalEndOrder > 0)
        {
            return candidate.LogicalEndOrder < candidate.LogicalStartOrder;
        }

        return candidate.EndPage < candidate.StartPage ||
               (candidate.EndPage == candidate.StartPage &&
                candidate.EndLineInPage < candidate.StartLineInPage);
    }

    private static int CompareCandidateStart(OcrQuestionCandidate left, OcrQuestionCandidate right)
    {
        var logicalComparison = left.LogicalStartOrder.CompareTo(right.LogicalStartOrder);
        if (logicalComparison != 0)
        {
            return logicalComparison;
        }

        var pageComparison = left.StartPage.CompareTo(right.StartPage);
        if (pageComparison != 0)
        {
            return pageComparison;
        }

        return left.StartLineInPage.CompareTo(right.StartLineInPage);
    }

    private static bool HasCandidateRangeOverlap(
        OcrQuestionCandidate previous,
        OcrQuestionCandidate current)
    {
        if ((previous.LogicalStartOrder > 0 || previous.LogicalEndOrder > 0) &&
            (current.LogicalStartOrder > 0 || current.LogicalEndOrder > 0))
        {
            return current.LogicalStartOrder <= previous.LogicalEndOrder;
        }

        if (current.StartPage < previous.EndPage)
        {
            return true;
        }

        return current.StartPage == previous.EndPage &&
               current.StartLineInPage <= previous.EndLineInPage;
    }

    private static bool LooksLikeSharedContextMarkerText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var normalized = text.Trim();
        if (!normalized.Contains("다음", StringComparison.Ordinal) &&
            !normalized.Contains("답하라", StringComparison.Ordinal) &&
            !normalized.Contains("보고", StringComparison.Ordinal))
        {
            return false;
        }

        return normalized.IndexOfAny(QuestionRangeSeparators) >= 0;
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
}

internal sealed class PdfStructuralIssue
{
    public string Code { get; init; } = string.Empty;
    public int? Index { get; init; }
    public string Message { get; init; } = string.Empty;
}
