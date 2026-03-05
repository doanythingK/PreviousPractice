using System.Text;
using System.Text.RegularExpressions;
using PreviousPractice.Models;

namespace PreviousPractice.Infrastructure;

public static class OcrQuestionSegmenter
{
    private const int FallbackLinesPerQuestion = 10;
    private const int CandidateLogLimit = 20;
    // OCR이 페이지를 한 줄로 뭉개는 경우, " 1. ", " 2) " 같은 번호 앞에 강제로 줄바꿈을 삽입한다.
    private static readonly Regex SyntheticQuestionBreakRegex = new(
        @"(?<=\s)(?<header>(?:[1-9]|[1-9]\d)\s*[.)])(?=\s)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SingleLineHeaderRegex = new(
        @"^\s*(?:[Qq]\s*)?(?:(?:제|문항|문제)\s*)?(?<index>\d{1,3}|[①②③④⑤⑥⑦⑧⑨⑩⑪⑫⑬⑭⑮⑯⑰⑱⑲⑳]|[가-하]|[A-Za-z])\s*(?:[.)\]\-:：]|\s|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<OcrQuestionCandidate> SplitByHeader(IReadOnlyList<OcrPageResult> pages)
    {
        if (pages.Count == 0)
        {
            AppLog.Error(nameof(OcrQuestionSegmenter), "분할 중단 | pages=0");
            return Array.Empty<OcrQuestionCandidate>();
        }

        var lines = new List<ParsedLine>();
        var pageLineCounts = new Dictionary<int, int>();
        var syntheticBreakCount = 0;

        for (var pageIndex = 0; pageIndex < pages.Count; pageIndex++)
        {
            var pageNumber = pageIndex + 1;
            var pageText = ExpandSyntheticLineBreaks(pages[pageIndex].Text, out var insertedBreaks);
            syntheticBreakCount += insertedBreaks;

            var lineInPage = 0;
            foreach (var rawLine in pageText.Split('\n'))
            {
                var normalized = rawLine.Replace('\r', ' ').Trim();
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    continue;
                }

                lineInPage++;
                lines.Add(new ParsedLine(pageNumber, lineInPage, normalized));
            }

            pageLineCounts[pageNumber] = lineInPage;
        }

        if (lines.Count == 0)
        {
            AppLog.Error(
                nameof(OcrQuestionSegmenter),
                $"분할 중단 | pages={pages.Count} | normalizedLines=0");
            return Array.Empty<OcrQuestionCandidate>();
        }

        var headerRegexMatchCount = 0;
        var normalizedIndexMatchCount = 0;
        var duplicateIndexCount = 0;
        var candidates = new List<OcrQuestionCandidate>();
        OcrQuestionCandidate? current = null;
        var currentBuffer = new StringBuilder();
        var seenIndex = new HashSet<int>();

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var match = SingleLineHeaderRegex.Match(line.Text);
            if (match.Success)
            {
                headerRegexMatchCount++;
            }

            if (match.Success && TryNormalizeQuestionIndex(match.Groups["index"].Value, out var index))
            {
                normalizedIndexMatchCount++;
                if (current is not null)
                {
                    candidates.Add(FinalizeCurrent(current, currentBuffer));
                }

                if (seenIndex.Add(index))
                {
                    current = new OcrQuestionCandidate
                    {
                        Index = index,
                        Header = line.Text,
                        StartPage = line.PageIndex,
                        EndPage = line.PageIndex,
                        StartLineInPage = line.LineInPage,
                        EndLineInPage = line.LineInPage,
                        StartPageLineCount = GetPageLineCount(pageLineCounts, line.PageIndex)
                    };
                    currentBuffer.Clear();
                }
                else
                {
                    // 같은 번호가 반복으로 들어오면 새 질문으로 간주하지 않는다.
                    duplicateIndexCount++;
                    current = null;
                    currentBuffer.Clear();
                }

                continue;
            }

            if (current != null)
            {
                currentBuffer.AppendLine(line.Text);
            }
        }

        if (current is not null)
        {
            candidates.Add(FinalizeCurrent(current, currentBuffer));
        }

        if (candidates.Count == 0)
        {
            var fallback = SplitByHeuristic(lines, pageLineCounts);
            AppLog.Info(
                nameof(OcrQuestionSegmenter),
                $"분할 결과(휴리스틱) | pages={pages.Count} | lines={lines.Count} | syntheticBreaks={syntheticBreakCount} | regexMatch={headerRegexMatchCount} | normalized={normalizedIndexMatchCount} | duplicates={duplicateIndexCount} | candidates={fallback.Count}");
            LogCandidateSummary("heuristic", fallback);
            return fallback;
        }

        var resolved = ApplyRanges(candidates, pageLineCounts, pages.Count);
        AppLog.Info(
            nameof(OcrQuestionSegmenter),
            $"분할 결과(헤더) | pages={pages.Count} | lines={lines.Count} | syntheticBreaks={syntheticBreakCount} | regexMatch={headerRegexMatchCount} | normalized={normalizedIndexMatchCount} | duplicates={duplicateIndexCount} | candidates={resolved.Count}");
        LogCandidateSummary("header", resolved);
        return resolved;
    }

    private static IReadOnlyList<OcrQuestionCandidate> SplitByHeuristic(
        IReadOnlyList<ParsedLine> lines,
        IReadOnlyDictionary<int, int> pageLineCounts)
    {
        if (lines.Count == 0)
        {
            return Array.Empty<OcrQuestionCandidate>();
        }

        var roughCandidates = new List<OcrQuestionCandidate>();
        var index = 1;
        var chunkSize = Math.Max(1, FallbackLinesPerQuestion);

        for (var start = 0; start < lines.Count; start += chunkSize)
        {
            var end = Math.Min(start + chunkSize, lines.Count);
            var chunk = lines.Skip(start).Take(end - start).ToArray();
            if (chunk.Length == 0)
            {
                continue;
            }

            var preview = TrimToLength(string.Join(" ", chunk.Select(x => x.Text)), 150);
            roughCandidates.Add(new OcrQuestionCandidate
            {
                Index = index,
                Header = $"[추정] 문항 {index}",
                StartPage = chunk[0].PageIndex,
                EndPage = chunk[^1].PageIndex,
                StartLineInPage = chunk[0].LineInPage,
                EndLineInPage = chunk[^1].LineInPage,
                StartPageLineCount = GetPageLineCount(pageLineCounts, chunk[0].PageIndex),
                PreviewText = preview
            });

            index++;
        }

        return ApplyRanges(roughCandidates, pageLineCounts, lines.Max(x => x.PageIndex));
    }

    private static IReadOnlyList<OcrQuestionCandidate> ApplyRanges(
        IReadOnlyList<OcrQuestionCandidate> candidates,
        IReadOnlyDictionary<int, int> pageLineCounts,
        int totalPages)
    {
        var resolved = new List<OcrQuestionCandidate>(candidates.Count);
        for (var i = 0; i < candidates.Count; i++)
        {
            var current = candidates[i];
            var startPageLineCount = GetPageLineCount(pageLineCounts, current.StartPage);
            var endPage = totalPages;
            var endLineInPage = startPageLineCount;

            if (i + 1 < candidates.Count)
            {
                var next = candidates[i + 1];
                if (next.StartPage == current.StartPage)
                {
                    endPage = current.StartPage;
                    endLineInPage = Math.Max(
                        current.StartLineInPage,
                        Math.Max(1, next.StartLineInPage - 1));
                }
                else
                {
                    endPage = next.StartPage;
                    endLineInPage = startPageLineCount;
                }
            }

            resolved.Add(current with
            {
                EndPage = endPage,
                EndLineInPage = endLineInPage,
                StartPageLineCount = startPageLineCount
            });
        }

        return resolved;
    }

    private static OcrQuestionCandidate FinalizeCurrent(
        OcrQuestionCandidate current,
        StringBuilder buffer)
    {
        var preview = (buffer.ToString() ?? string.Empty).Trim();
        return current with
        {
            PreviewText = TrimToLength(preview, 150)
        };
    }

    private static string TrimToLength(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= maxLength
            ? value
            : value.Substring(0, maxLength).TrimEnd() + "...";
    }

    private static bool TryNormalizeQuestionIndex(string raw, out int index)
    {
        index = 0;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        if (int.TryParse(raw, out index))
        {
            return index > 0;
        }

        var hangul = "①②③④⑤⑥⑦⑧⑨⑩⑪⑫⑬⑭⑮⑯⑰⑱⑲⑳";
        var pos = hangul.IndexOf(raw);
        if (pos >= 0)
        {
            index = pos + 1;
            return true;
        }

        // 가-하 정도가 번호로 사용되는 경우: 가=1, 나=2 ...
        if (raw.Length == 1)
        {
            var c = raw[0];
            if (c >= '가' && c <= '하')
            {
                index = c - '가' + 1;
                return index > 0;
            }
        }

        return false;
    }

    private static string ExpandSyntheticLineBreaks(string? text, out int insertedBreaks)
    {
        insertedBreaks = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = text.Replace("\r", "\n");
        var breakCount = 0;
        var expanded = SyntheticQuestionBreakRegex.Replace(
            normalized,
            match =>
            {
                breakCount++;
                return "\n" + match.Groups["header"].Value;
            });

        insertedBreaks = breakCount;
        return expanded;
    }

    private static int GetPageLineCount(IReadOnlyDictionary<int, int> pageLineCounts, int pageIndex)
    {
        return pageLineCounts.TryGetValue(pageIndex, out var count)
            ? Math.Max(1, count)
            : 1;
    }

    private static void LogCandidateSummary(string mode, IReadOnlyList<OcrQuestionCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            AppLog.Error(nameof(OcrQuestionSegmenter), $"후보 요약 없음 | mode={mode}");
            return;
        }

        var summary = candidates
            .Take(CandidateLogLimit)
            .Select(x =>
                $"{x.Index}@p{x.StartPage}:{x.StartLineInPage}-{x.EndLineInPage}/{x.StartPageLineCount}:{TrimToLength(x.Header, 40)}")
            .ToArray();

        AppLog.Info(
            nameof(OcrQuestionSegmenter),
            $"후보 요약 | mode={mode} | count={candidates.Count} | top={string.Join(" || ", summary)}");
    }

    private readonly record struct ParsedLine(int PageIndex, int LineInPage, string Text);
}
