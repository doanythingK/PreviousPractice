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
    private void ClearPdfAnalysisState()
    {
        pdfAnalysisProcessedPages = 0;
        pdfAnalysisTotalPages = 0;
        PdfAnalysisSummary = string.Empty;
        LatestPdfDiagnosticsText = string.Empty;
        LatestPdfDiagnosticsTitle = string.Empty;
        PdfAnalysisProgressValue = 0d;
        PdfAnalysisStatus = string.Empty;
        PdfAnalysisStatusColor = Color.FromArgb("#334155");
        PdfAnalysisPagesPerSecond = 0d;
        pdfAnalysisStartAt = DateTimeOffset.UtcNow;
        OnPropertyChanged(nameof(ShowPdfAnalysisProgress));
        OnPropertyChanged(nameof(ShowPdfAnalysisPanel));
        OnPropertyChanged(nameof(PdfAnalysisProgressText));
    }

    private void UpdatePdfAnalysisProgress(PdfAnalysisProgress progress)
    {
        pdfAnalysisProcessedPages = Math.Max(0, progress.ProcessedPages);
        pdfAnalysisTotalPages = Math.Max(0, progress.TotalPages);
        OnPropertyChanged(nameof(PdfAnalysisProgressText));

        PdfAnalysisStatus = progress.Message;
        if (IsPdfAnalysisInProgress)
        {
            PdfAnalysisStatusColor = Color.FromArgb("#0EA5E9");
        }

        if (pdfAnalysisStartAt != default && pdfAnalysisProcessedPages > 0)
        {
            var elapsed = Math.Max(0.000_001, (DateTimeOffset.UtcNow - pdfAnalysisStartAt).TotalSeconds);
            PdfAnalysisPagesPerSecond = pdfAnalysisProcessedPages / elapsed;
        }
        else
        {
            PdfAnalysisPagesPerSecond = 0d;
        }

        var progressValue = progress.TotalPages <= 0
            ? 0d
            : Math.Min(1d, (double)progress.ProcessedPages / progress.TotalPages);
        PdfAnalysisProgressValue = Math.Max(0d, progressValue);
    }

    private static string BuildAnalysisSummary(PdfOcrResult analysis, PdfAnalysisDiagnostics? diagnostics = null)
    {
        var baseSummary = $"{analysis.Summary}\n미리보기:\n{analysis.Preview}";
        if (diagnostics?.ExpectedQuestionCount is int expectedQuestionCount)
        {
            baseSummary += $"\n입력 문항 수: {expectedQuestionCount}";

            if (diagnostics.ExpectedQuestionRange is QuestionNumberRange expectedRange)
            {
                var rangeLabel = diagnostics.ExpectedQuestionRangeWasAutoInferred
                    ? "자동 추정 문항 범위"
                    : "예상 문항 범위";
                baseSummary += $"\n{rangeLabel}: {expectedRange} ({expectedRange.Count}문항) / 분석 후보 수: {diagnostics.DistinctCandidateCount}";
            }
            else
            {
                baseSummary += "\n자동 추정 문항 범위: 확인 실패";
            }

            if (!string.IsNullOrWhiteSpace(diagnostics.ExpectedQuestionRangeReason))
            {
                baseSummary += $"\n범위 결정 근거: {diagnostics.ExpectedQuestionRangeReason}";
            }

            baseSummary += diagnostics.HasExpectedQuestionMismatch
                ? $"\n예상 비교 결과: 불일치 ({BuildExpectedQuestionMismatchSummary(diagnostics)})"
                : "\n예상 비교 결과: 일치";
        }

        if (diagnostics is not null)
        {
            baseSummary += diagnostics.HasBlockingStructuralIssues
                ? $"\n구조 검증 결과: 실패 ({BuildStructuralIssueSummary(diagnostics)})"
                : "\n구조 검증 결과: 통과";
        }

        if (!analysis.HasQuestionCandidates)
        {
            return baseSummary + "\n현재 OCR 텍스트에서 문항 번호 헤더 후보를 찾지 못했습니다.";
        }

        var topCandidates = analysis.QuestionCandidates
            .Take(10)
            .Select(x => $"{x.Index}:{x.Header}")
            .ToArray();

        return baseSummary + $"\n분할 후보(최대 10개): {string.Join(", ", topCandidates)}";
    }

    private static string BuildExpectedQuestionMismatchSummary(PdfAnalysisDiagnostics diagnostics)
    {
        if (diagnostics.ExpectedQuestionCount == null)
        {
            return string.Empty;
        }

        var messages = new List<string>();
        if (diagnostics.ExpectedQuestionRange is QuestionNumberRange expectedRange)
        {
            var rangeLabel = diagnostics.ExpectedQuestionRangeWasAutoInferred
                ? "자동 추정"
                : "예상";
            messages.Add($"{rangeLabel} {expectedRange.StartIndex}-{expectedRange.EndIndex} ({expectedRange.Count}개) / 분석 {diagnostics.DistinctCandidateCount}개");
        }
        else
        {
            messages.Add($"입력 문항 수 {diagnostics.ExpectedQuestionCount.Value}개에 맞는 시작 번호를 자동 추정하지 못했습니다.");
        }

        if (!string.IsNullOrWhiteSpace(diagnostics.ExpectedQuestionRangeReason))
        {
            messages.Add(diagnostics.ExpectedQuestionRangeReason);
        }

        if (diagnostics.MissingIndexes.Length > 0)
        {
            messages.Add($"누락 번호: {string.Join(", ", diagnostics.MissingIndexes)}");
        }

        if (diagnostics.UnexpectedIndexes.Length > 0)
        {
            messages.Add($"예상 범위 밖 번호: {string.Join(", ", diagnostics.UnexpectedIndexes)}");
        }

        if (diagnostics.DuplicateIndexes.Length > 0)
        {
            messages.Add($"중복 번호: {string.Join(", ", diagnostics.DuplicateIndexes.Select(x => $"{x.Index}({x.Count})"))}");
        }

        return string.Join(" / ", messages);
    }

    private static string BuildStructuralIssueSummary(PdfAnalysisDiagnostics diagnostics)
    {
        if (diagnostics.StructuralIssues.Length == 0)
        {
            return string.Empty;
        }

        var messages = diagnostics.StructuralIssues
            .Take(3)
            .Select(x => x.Message)
            .ToList();
        if (diagnostics.StructuralIssues.Length > messages.Count)
        {
            messages.Add($"외 {diagnostics.StructuralIssues.Length - messages.Count}건");
        }

        return string.Join(" / ", messages);
    }

    internal static PdfAnalysisStructuralIssueDiagnostics[] BuildStructuralIssues(
        IReadOnlyList<OcrQuestionCandidate> candidates,
        IReadOnlyList<OcrPageResult> pages)
    {
        var issues = new List<PdfAnalysisStructuralIssueDiagnostics>();
        foreach (var duplicatePage in pages
                     .GroupBy(x => x.PageIndex)
                     .Where(x => x.Count() > 1))
        {
            issues.Add(new PdfAnalysisStructuralIssueDiagnostics
            {
                Code = "duplicate-page-index",
                Message = $"p{duplicatePage.Key} 페이지 번호가 {duplicatePage.Count()}번 나타나 문서 순서를 확정할 수 없습니다."
            });
        }

        foreach (var duplicate in candidates
                     .GroupBy(x => x.Index)
                     .Where(x => x.Count() > 1))
        {
            issues.Add(new PdfAnalysisStructuralIssueDiagnostics
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

            issues.Add(new PdfAnalysisStructuralIssueDiagnostics
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
                issues.Add(new PdfAnalysisStructuralIssueDiagnostics
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
                issues.Add(new PdfAnalysisStructuralIssueDiagnostics
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
                    issues.Add(new PdfAnalysisStructuralIssueDiagnostics
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
                    issues.Add(new PdfAnalysisStructuralIssueDiagnostics
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
                issues.Add(new PdfAnalysisStructuralIssueDiagnostics
                {
                    Code = "boilerplate-candidate",
                    Index = candidate.Index,
                    Message = $"{candidate.Index}번 후보에 '{blockingPhrase}' 문구가 포함되어 있습니다."
                });
            }

            if (HasInvalidCandidateSpan(candidate))
            {
                issues.Add(new PdfAnalysisStructuralIssueDiagnostics
                {
                    Code = "invalid-span",
                    Index = candidate.Index,
                    Message = $"{candidate.Index}번 범위가 비정상입니다: p{candidate.StartPage}:{candidate.StartLineInPage} -> p{candidate.EndPage}:{candidate.EndLineInPage}"
                });
            }

            if (HasSuspiciousSharedContextLeak(candidate))
            {
                issues.Add(new PdfAnalysisStructuralIssueDiagnostics
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
                issues.Add(new PdfAnalysisStructuralIssueDiagnostics
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
                issues.Add(new PdfAnalysisStructuralIssueDiagnostics
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

                issues.Add(new PdfAnalysisStructuralIssueDiagnostics
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

                issues.Add(new PdfAnalysisStructuralIssueDiagnostics
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

    private static string BuildCandidateMismatchMessage(
        PdfOcrResult analysis,
        AnswerMapParseResult parseResult,
        IReadOnlyList<Question> questions)
    {
        var messages = new List<string>();

        if (parseResult.Questions.Count() == 0)
        {
            messages.Add("정답이 없어 미채점 상태로 저장합니다.");
        }
        else if (!analysis.HasQuestionCandidates)
        {
            messages.Add("문항 분할 후보가 없어 수량 비교를 생략했습니다.");
        }
        else
        {
            var expected = analysis.DetectedQuestionCount;
            var parsed = parseResult.Questions.Count();

            if (parsed > expected)
            {
                messages.Add($"정답 항목 수({parsed}개)가 추정 문항 수({expected}개)보다 많습니다. 중복/헤더 미검출 항목 확인이 필요합니다.");
            }
            else if (parsed < expected)
            {
                messages.Add($"정답 항목 수({parsed}개)가 추정 문항 수({expected}개)보다 적습니다. 누락된 문항이 있을 수 있습니다.");
            }
        }

        var questionsWithoutImage = questions.Count(x =>
            (x.ImageSegments == null || x.ImageSegments.Length == 0) &&
            string.IsNullOrWhiteSpace(x.ImagePath));
        if (questionsWithoutImage > 0)
        {
            messages.Add($"OCR에서 정확히 대응되는 문항 후보가 없는 항목 {questionsWithoutImage}개는 이미지 없이 저장했습니다.");
        }

        return string.Join(" / ", messages);
    }

    private static PdfAnalysisDiagnostics BuildPdfAnalysisDiagnostics(
        string sourceFileName,
        PdfOcrResult analysis,
        ExpectedQuestionInput expectedQuestionInput,
        ExpectedQuestionRangeResolution expectedQuestionRangeResolution)
    {
        var rawCandidates = analysis.QuestionCandidates
            .Where(x => x.Index > 0)
            .ToArray();
        var candidateIndexes = rawCandidates
            .Select(x => x.Index)
            .Distinct()
            .OrderBy(x => x)
            .ToArray();
        var duplicateIndexes = rawCandidates
            .GroupBy(x => x.Index)
            .Where(x => x.Count() > 1)
            .OrderBy(x => x.Key)
            .Select(x => new PdfAnalysisDuplicateDiagnostics
            {
                Index = x.Key,
                Count = x.Count(),
                Headers = x.Select(y => y.Header).Take(5).ToArray()
            })
            .ToArray();
        var expectedQuestionRange = expectedQuestionRangeResolution.Range;
        var structuralIssues = BuildStructuralIssues(rawCandidates, analysis.Pages);
        var missingIndexes = expectedQuestionRange.HasValue
            ? Enumerable.Range(expectedQuestionRange.Value.StartIndex, expectedQuestionRange.Value.Count)
                .Except(candidateIndexes)
                .ToArray()
            : Array.Empty<int>();
        var unexpectedIndexes = expectedQuestionRange.HasValue
            ? candidateIndexes
                .Where(x => !expectedQuestionRange.Value.Contains(x))
                .ToArray()
            : Array.Empty<int>();
        var hasExpectedQuestionMismatch = expectedQuestionInput.HasValue &&
            (!expectedQuestionRange.HasValue ||
             candidateIndexes.Length != expectedQuestionRange.Value.Count ||
             missingIndexes.Length > 0 ||
             unexpectedIndexes.Length > 0 ||
             duplicateIndexes.Length > 0);

        return new PdfAnalysisDiagnostics
        {
            GeneratedAt = DateTimeOffset.Now,
            SourceFileName = sourceFileName,
            ExpectedQuestionInput = expectedQuestionInput.RawText,
            ExpectedQuestionCount = expectedQuestionInput.ExpectedCountOrNull,
            ExpectedQuestionRange = expectedQuestionRange,
            ExpectedQuestionRangeWasAutoInferred = expectedQuestionRangeResolution.IsAutoInferred && expectedQuestionRange.HasValue,
            ExpectedQuestionRangeReason = expectedQuestionRangeResolution.Reason,
            PageCount = analysis.PageCount,
            TotalWordCount = analysis.TotalWordCount,
            RawCandidateCount = rawCandidates.Length,
            DistinctCandidateCount = candidateIndexes.Length,
            HasBlockingStructuralIssues = structuralIssues.Length > 0,
            HasExpectedQuestionMismatch = hasExpectedQuestionMismatch,
            CandidateIndexes = candidateIndexes,
            MissingIndexes = missingIndexes,
            UnexpectedIndexes = unexpectedIndexes,
            DuplicateIndexes = duplicateIndexes,
            StructuralIssues = structuralIssues,
            Candidates = rawCandidates
                .OrderBy(x => x.Index)
                .ThenBy(x => x.StartPage)
                .ThenBy(x => x.StartLineInPage)
                .Select(x => new PdfAnalysisCandidateDiagnostics
                {
                    Index = x.Index,
                    Header = x.Header,
                    IsInferred = x.IsInferred,
                    LogicalStartOrder = x.LogicalStartOrder,
                    LogicalEndOrder = x.LogicalEndOrder,
                    StartPage = x.StartPage,
                    StartLineInPage = x.StartLineInPage,
                    EndPage = x.EndPage,
                    EndLineInPage = x.EndLineInPage,
                    PreviewText = x.PreviewText
                })
                .ToArray(),
            Pages = analysis.Pages
                .OrderBy(x => x.PageIndex)
                .Select(x => new PdfAnalysisPageDiagnostics
                {
                    PageIndex = x.PageIndex,
                    WordCount = x.WordCount,
                    LineCount = x.Lines?.Count ?? 0,
                    LeadingLines = BuildLeadingLines(x),
                    CandidateIndexes = rawCandidates
                        .Where(candidate => candidate.StartPage == x.PageIndex)
                        .Select(candidate => candidate.Index)
                        .Distinct()
                        .OrderBy(index => index)
                        .ToArray()
                })
                .ToArray()
        };
    }
}
