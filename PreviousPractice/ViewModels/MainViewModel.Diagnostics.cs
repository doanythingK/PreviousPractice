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
        var structuralIssues = PdfStructuralValidator.Validate(rawCandidates, analysis.Pages)
            .Select(issue => new PdfAnalysisStructuralIssueDiagnostics
            {
                Code = issue.Code,
                Index = issue.Index,
                Message = issue.Message
            })
            .ToArray();
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
