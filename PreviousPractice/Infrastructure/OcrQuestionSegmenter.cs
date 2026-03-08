using System.Text;
using System.Text.RegularExpressions;
using PreviousPractice.Models;

namespace PreviousPractice.Infrastructure;

public static class OcrQuestionSegmenter
{
    private const int FallbackLinesPerQuestion = 10;
    private const int CandidateLogLimit = 20;
    private const double LeftColumnMidpointThreshold = 0.45d;
    private const double RightColumnMidpointThreshold = 0.55d;
    private const int MinColumnLineCount = 5;
    // OCR이 페이지를 한 줄로 뭉개는 경우, " 1. ", " 2) " 같은 번호 앞에 강제로 줄바꿈을 삽입한다.
    private static readonly Regex SyntheticQuestionBreakRegex = new(
        @"(?<=\s)(?<header>(?:[1-9]|[1-9]\d)\s*[.)])(?=\s)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SingleLineHeaderRegex = new(
        @"^\s*(?:[Qq]\s*)?(?:(?:제|문항|문제)\s*)?(?<index>\d{1,3})\s*(?:[.)\]\-:：]|\s|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<OcrQuestionCandidate> SplitByHeader(
        IReadOnlyList<OcrPageResult> pages,
        QuestionNumberRange? expectedQuestionRange = null)
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
            var page = pages[pageIndex];
            var pageNumber = page.PageIndex;
            if (page.Lines != null && page.Lines.Count > 0)
            {
                var trimmedLines = TrimLeadingBoilerplateLines(
                    page.Lines
                        .OrderBy(x => x.LineInPage)
                        .ToArray(),
                    x => x.Text,
                    expectedQuestionRange);
                var orderedLines = ReorderPageLinesByColumns(trimmedLines);
                var maxLineInPage = 0;
                foreach (var line in orderedLines)
                {
                    if (string.IsNullOrWhiteSpace(line.Text))
                    {
                        continue;
                    }

                    maxLineInPage = Math.Max(maxLineInPage, line.LineInPage);
                    lines.Add(new ParsedLine(
                        pageNumber,
                        line.LineInPage,
                        line.Text.Trim(),
                        line.LeftRatio,
                        line.TopRatio,
                        line.RightRatio,
                        line.BottomRatio));
                }

                pageLineCounts[pageNumber] = Math.Max(1, maxLineInPage);
                continue;
            }

            var pageText = ExpandSyntheticLineBreaks(page.Text, out var insertedBreaks);
            syntheticBreakCount += insertedBreaks;

            var rawLines = pageText.Split('\n')
                .Select((rawLine, index) => new ParsedLine(pageNumber, index + 1, rawLine.Replace('\r', ' ').Trim()))
                .Where(x => !string.IsNullOrWhiteSpace(x.Text))
                .ToArray();
            var trimmedTextLines = TrimLeadingBoilerplateLines(rawLines, x => x.Text, expectedQuestionRange);

            var lineInPage = 0;
            foreach (var line in trimmedTextLines)
            {
                lineInPage = Math.Max(lineInPage, line.LineInPage);
                lines.Add(line);
            }

            pageLineCounts[pageNumber] = Math.Max(
                lineInPage,
                rawLines.Length == 0 ? 0 : rawLines.Max(x => x.LineInPage));
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

            if (TryMatchQuestionHeader(lines, i, SingleLineHeaderRegex, expectedQuestionRange, out var index))
            {
                if (ShouldStartNewCandidate(current, index))
                {
                    normalizedIndexMatchCount++;
                    if (seenIndex.Add(index))
                    {
                        if (current is not null)
                        {
                            candidates.Add(FinalizeCurrent(current, currentBuffer));
                        }

                        current = new OcrQuestionCandidate
                        {
                            Index = index,
                            Header = line.Text,
                            StartPage = line.PageIndex,
                            EndPage = line.PageIndex,
                            StartLineInPage = line.LineInPage,
                            EndLineInPage = line.LineInPage,
                            StartPageLineCount = GetPageLineCount(pageLineCounts, line.PageIndex),
                            EndPageLineCount = GetPageLineCount(pageLineCounts, line.PageIndex)
                        };
                        currentBuffer.Clear();
                        continue;
                    }

                    duplicateIndexCount++;
                }
            }

            if (current != null)
            {
                currentBuffer.AppendLine(line.Text);
                current = current with
                {
                    EndPage = line.PageIndex,
                    EndLineInPage = line.LineInPage,
                    EndPageLineCount = GetPageLineCount(pageLineCounts, line.PageIndex)
                };
            }
        }

        if (current is not null)
        {
            candidates.Add(FinalizeCurrent(current, currentBuffer));
        }

        if (candidates.Count == 0)
        {
            candidates = SplitByPermissiveHeader(
                    lines,
                    pageLineCounts,
                    expectedQuestionRange,
                    out headerRegexMatchCount,
                    out normalizedIndexMatchCount,
                    out duplicateIndexCount)
                .ToList();
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

        if (expectedQuestionRange.HasValue)
        {
            candidates = RecoverExpectedRangeCandidates(
                lines,
                pageLineCounts,
                expectedQuestionRange.Value,
                candidates);
        }

        AppLog.Info(
            nameof(OcrQuestionSegmenter),
            $"분할 결과(헤더) | pages={pages.Count} | lines={lines.Count} | syntheticBreaks={syntheticBreakCount} | regexMatch={headerRegexMatchCount} | normalized={normalizedIndexMatchCount} | duplicates={duplicateIndexCount} | candidates={candidates.Count}");
        LogCandidateSummary("header", candidates);
        return candidates;
    }

    private static List<OcrQuestionCandidate> RecoverExpectedRangeCandidates(
        IReadOnlyList<ParsedLine> lines,
        IReadOnlyDictionary<int, int> pageLineCounts,
        QuestionNumberRange expectedQuestionRange,
        IReadOnlyList<OcrQuestionCandidate> candidates)
    {
        if (lines.Count == 0 || candidates.Count == 0)
        {
            return candidates.ToList();
        }

        var linePositions = lines
            .Select((line, position) => new { line.PageIndex, line.LineInPage, Position = position })
            .ToDictionary(
                x => (x.PageIndex, x.LineInPage),
                x => x.Position);

        var markers = candidates
            .Where(x => x.Index > 0 && expectedQuestionRange.Contains(x.Index))
            .Select(candidate => TryCreateStartMarker(candidate, linePositions, out var marker) ? marker : (StartMarker?)null)
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .OrderBy(x => x.Position)
            .ToList();

        if (markers.Count == 0)
        {
            return candidates.ToList();
        }

        var recoveredExactCount = 0;
        foreach (var missingIndex in Enumerable.Range(expectedQuestionRange.StartIndex, expectedQuestionRange.Count))
        {
            if (markers.Any(x => x.Index == missingIndex))
            {
                continue;
            }

            if (TryFindExactMissingIndexMarker(lines, markers, missingIndex, out var marker))
            {
                markers.Add(marker);
                recoveredExactCount++;
            }
        }

        markers = markers
            .OrderBy(x => x.Position)
            .ToList();

        var recoveredInferredCount = 0;
        markers = RecoverInferredGapMarkers(lines, markers, expectedQuestionRange, ref recoveredInferredCount);

        if (recoveredExactCount == 0 && recoveredInferredCount == 0)
        {
            return candidates.ToList();
        }

        var rebuilt = RebuildCandidatesFromMarkers(lines, pageLineCounts, markers);
        AppLog.Info(
            nameof(OcrQuestionSegmenter),
            $"누락 문항 복구 | expected={expectedQuestionRange} | exact={recoveredExactCount} | inferred={recoveredInferredCount} | candidates={rebuilt.Count}");
        return rebuilt;
    }

    private static bool TryCreateStartMarker(
        OcrQuestionCandidate candidate,
        IReadOnlyDictionary<(int PageIndex, int LineInPage), int> linePositions,
        out StartMarker marker)
    {
        marker = default;
        if (!linePositions.TryGetValue((candidate.StartPage, candidate.StartLineInPage), out var position))
        {
            return false;
        }

        marker = new StartMarker(
            candidate.Index,
            position,
            candidate.StartPage,
            candidate.StartLineInPage,
            candidate.Header,
            candidate.ImagePath,
            Inferred: false);
        return true;
    }

    private static bool TryFindExactMissingIndexMarker(
        IReadOnlyList<ParsedLine> lines,
        IReadOnlyList<StartMarker> markers,
        int missingIndex,
        out StartMarker marker)
    {
        marker = default;
        var previousMarker = markers
            .Where(x => x.Index < missingIndex)
            .OrderByDescending(x => x.Index)
            .FirstOrDefault();
        var nextMarker = markers
            .Where(x => x.Index > missingIndex)
            .OrderBy(x => x.Index)
            .FirstOrDefault();

        var searchStart = previousMarker == default ? 0 : previousMarker.Position + 1;
        var searchEnd = nextMarker == default ? lines.Count - 1 : nextMarker.Position - 1;
        if (searchStart > searchEnd)
        {
            return false;
        }

        for (var position = searchStart; position <= searchEnd; position++)
        {
            if (!TryMatchExpectedIndexHeader(lines, position, missingIndex))
            {
                continue;
            }

            var line = lines[position];
            marker = new StartMarker(
                missingIndex,
                position,
                line.PageIndex,
                line.LineInPage,
                line.Text,
                ImagePath: null,
                Inferred: true);
            return true;
        }

        return false;
    }

    private static bool TryMatchExpectedIndexHeader(
        IReadOnlyList<ParsedLine> lines,
        int lineIndex,
        int expectedIndex)
    {
        if (lineIndex < 0 || lineIndex >= lines.Count)
        {
            return false;
        }

        var line = lines[lineIndex];
        if (string.IsNullOrWhiteSpace(line.Text) || IsDisallowedHeaderText(line.Text))
        {
            return false;
        }

        var nextLineText = lineIndex + 1 < lines.Count && lines[lineIndex + 1].PageIndex == line.PageIndex
            ? lines[lineIndex + 1].Text
            : null;
        var regex = new Regex(
            $@"^\s*(?:[Qq]\s*)?(?:(?:제|문항|문제)\s*)?{expectedIndex}(?!\d)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        var match = regex.Match(line.Text);
        if (!match.Success)
        {
            return false;
        }

        var suffix = line.Text.Substring(Math.Min(match.Length, line.Text.Length)).Trim();
        return IsPlausibleQuestionHeaderText(line.Text, suffix, nextLineText);
    }

    private static List<StartMarker> RecoverInferredGapMarkers(
        IReadOnlyList<ParsedLine> lines,
        IReadOnlyList<StartMarker> markers,
        QuestionNumberRange expectedQuestionRange,
        ref int recoveredInferredCount)
    {
        var orderedMarkers = markers
            .OrderBy(x => x.Position)
            .ToList();

        var changed = true;
        while (changed)
        {
            changed = false;
            for (var index = 0; index < orderedMarkers.Count - 1; index++)
            {
                var current = orderedMarkers[index];
                var next = orderedMarkers[index + 1];
                var missingCount = next.Index - current.Index - 1;
                if (missingCount <= 0)
                {
                    continue;
                }

                var gapMarkers = InferGapMarkers(lines, current, next, missingCount);
                if (gapMarkers.Count == 0)
                {
                    continue;
                }

                foreach (var marker in gapMarkers)
                {
                    if (marker.Index < expectedQuestionRange.StartIndex ||
                        marker.Index > expectedQuestionRange.EndIndex ||
                        orderedMarkers.Any(x => x.Index == marker.Index))
                    {
                        continue;
                    }

                    orderedMarkers.Add(marker);
                    recoveredInferredCount++;
                    changed = true;
                }

                if (changed)
                {
                    orderedMarkers = orderedMarkers
                        .OrderBy(x => x.Position)
                        .ToList();
                    break;
                }
            }
        }

        return orderedMarkers;
    }

    private static List<StartMarker> InferGapMarkers(
        IReadOnlyList<ParsedLine> lines,
        StartMarker current,
        StartMarker next,
        int missingCount)
    {
        var gapStart = current.Position + 1;
        var gapEnd = next.Position - 1;
        if (gapStart > gapEnd)
        {
            return new List<StartMarker>();
        }

        var anchorLeft = lines[current.Position].LeftRatio;
        var candidatePositions = new List<(int Position, int Score)>();
        for (var position = gapStart; position <= gapEnd; position++)
        {
            if (!LooksLikeInferredHeaderLine(lines, position, anchorLeft, out var score))
            {
                continue;
            }

            candidatePositions.Add((position, score));
        }

        if (candidatePositions.Count == 0)
        {
            return new List<StartMarker>();
        }

        var topScore = candidatePositions.Max(x => x.Score);
        var selectedPositions = candidatePositions
            .Where(x => x.Score >= topScore - 25)
            .OrderBy(x => x.Position)
            .ThenByDescending(x => x.Score)
            .Take(missingCount)
            .ToArray();

        var inferredMarkers = new List<StartMarker>();
        for (var i = 0; i < selectedPositions.Length; i++)
        {
            var missingIndex = current.Index + i + 1;
            var line = lines[selectedPositions[i].Position];
            inferredMarkers.Add(new StartMarker(
                missingIndex,
                selectedPositions[i].Position,
                line.PageIndex,
                line.LineInPage,
                $"[추정] {missingIndex}. {line.Text}",
                ImagePath: null,
                Inferred: true));
        }

        return inferredMarkers;
    }

    private static bool LooksLikeInferredHeaderLine(
        IReadOnlyList<ParsedLine> lines,
        int lineIndex,
        double anchorLeft,
        out int score)
    {
        score = 0;
        if (lineIndex < 0 || lineIndex >= lines.Count)
        {
            return false;
        }

        var line = lines[lineIndex];
        var normalized = line.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized) ||
            IsDisallowedHeaderText(normalized) ||
            Regex.IsMatch(normalized, @"^\d", RegexOptions.CultureInvariant) ||
            LooksLikeCodeLikeSuffix(normalized))
        {
            return false;
        }

        if (Regex.IsMatch(
                normalized,
                @"^(?:0\s|O\s|[㉠-㉻]|[①-⑳])",
                RegexOptions.CultureInvariant))
        {
            return false;
        }

        var nextLineText = lineIndex + 1 < lines.Count && lines[lineIndex + 1].PageIndex == line.PageIndex
            ? lines[lineIndex + 1].Text
            : null;

        var hangulCount = CountHangulCharacters(normalized);
        if (hangulCount < 4 && !normalized.Contains('?'))
        {
            return false;
        }

        if (!LooksLikeQuestionContinuation(nextLineText) && !normalized.Contains('?'))
        {
            return false;
        }

        var leftDiff = Math.Abs(line.LeftRatio - anchorLeft);
        score += Math.Max(0, 50 - (int)(leftDiff * 400));
        score += Math.Min(30, hangulCount * 3);
        if (normalized.Contains('?'))
        {
            score += 20;
        }

        if (Regex.IsMatch(normalized, @"^[^\d\s]{1,4}[.)\]•·\-~〜]*\s+", RegexOptions.CultureInvariant))
        {
            score += 25;
        }

        var nextTokenIndex = normalized.IndexOf("다음", StringComparison.Ordinal);
        if (nextTokenIndex >= 0 && nextTokenIndex <= 10)
        {
            score += 20;
        }

        var previousLine = lineIndex > 0 ? lines[lineIndex - 1] : default;
        if (lineIndex > 0 && previousLine.PageIndex == line.PageIndex)
        {
            var previousLeftDiff = previousLine.LeftRatio - line.LeftRatio;
            if (previousLeftDiff > 0.02d)
            {
                score += 10;
            }
        }

        return score >= 40;
    }

    private static List<OcrQuestionCandidate> RebuildCandidatesFromMarkers(
        IReadOnlyList<ParsedLine> lines,
        IReadOnlyDictionary<int, int> pageLineCounts,
        IReadOnlyList<StartMarker> markers)
    {
        var orderedMarkers = markers
            .OrderBy(x => x.Position)
            .ToArray();
        var rebuilt = new List<OcrQuestionCandidate>();

        for (var index = 0; index < orderedMarkers.Length; index++)
        {
            var marker = orderedMarkers[index];
            var startLine = lines[marker.Position];
            var nextMarkerPosition = index + 1 < orderedMarkers.Length
                ? orderedMarkers[index + 1].Position
                : lines.Count;
            var endPositionExclusive = Math.Max(marker.Position + 1, nextMarkerPosition);
            var bodyLines = lines
                .Skip(marker.Position + 1)
                .Take(Math.Max(0, endPositionExclusive - marker.Position - 1))
                .ToArray();
            var lastLine = bodyLines.Length > 0
                ? bodyLines[^1]
                : startLine;
            var preview = TrimToLength(
                string.Join(Environment.NewLine, bodyLines.Select(x => x.Text)),
                150);

            rebuilt.Add(new OcrQuestionCandidate
            {
                Index = marker.Index,
                Header = marker.Header,
                StartPage = marker.PageIndex,
                EndPage = lastLine.PageIndex,
                StartLineInPage = marker.LineInPage,
                EndLineInPage = lastLine.LineInPage,
                StartPageLineCount = GetPageLineCount(pageLineCounts, marker.PageIndex),
                EndPageLineCount = GetPageLineCount(pageLineCounts, lastLine.PageIndex),
                ImagePath = marker.ImagePath,
                PreviewText = preview
            });
        }

        return rebuilt;
    }

    private static IReadOnlyList<OcrQuestionCandidate> SplitByPermissiveHeader(
        IReadOnlyList<ParsedLine> lines,
        IReadOnlyDictionary<int, int> pageLineCounts,
        QuestionNumberRange? expectedQuestionRange,
        out int headerRegexMatchCount,
        out int normalizedIndexMatchCount,
        out int duplicateIndexCount)
    {
        headerRegexMatchCount = 0;
        normalizedIndexMatchCount = 0;
        duplicateIndexCount = 0;

        var permissiveHeaderRegex = new Regex(
            @"^\s*(?:[Qq]\s*)?(?:(?:제|문항|문제)\s*)?(?<index>\d{1,3}|[①②③④⑤⑥⑦⑧⑨⑩⑪⑫⑬⑭⑮⑯⑰⑱⑲⑳]|[가-하]|[A-Za-z])\s*(?:[.)\]\-:：]|\s|$)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        var candidates = new List<OcrQuestionCandidate>();
        OcrQuestionCandidate? current = null;
        var currentBuffer = new StringBuilder();
        var seenIndex = new HashSet<int>();

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var match = permissiveHeaderRegex.Match(line.Text);
            if (match.Success)
            {
                headerRegexMatchCount++;
            }

            if (TryMatchQuestionHeader(lines, i, permissiveHeaderRegex, expectedQuestionRange, out var index) &&
                ShouldStartNewCandidate(current, index))
            {
                normalizedIndexMatchCount++;
                if (seenIndex.Add(index))
                {
                    if (current is not null)
                    {
                        candidates.Add(FinalizeCurrent(current, currentBuffer));
                    }

                    current = new OcrQuestionCandidate
                    {
                        Index = index,
                        Header = line.Text,
                        StartPage = line.PageIndex,
                        EndPage = line.PageIndex,
                        StartLineInPage = line.LineInPage,
                        EndLineInPage = line.LineInPage,
                        StartPageLineCount = GetPageLineCount(pageLineCounts, line.PageIndex),
                        EndPageLineCount = GetPageLineCount(pageLineCounts, line.PageIndex)
                    };
                    currentBuffer.Clear();
                    continue;
                }

                duplicateIndexCount++;
            }

            if (current != null)
            {
                currentBuffer.AppendLine(line.Text);
                current = current with
                {
                    EndPage = line.PageIndex,
                    EndLineInPage = line.LineInPage,
                    EndPageLineCount = GetPageLineCount(pageLineCounts, line.PageIndex)
                };
            }
        }

        if (current is not null)
        {
            candidates.Add(FinalizeCurrent(current, currentBuffer));
        }

        return candidates;
    }

    private static bool ShouldStartNewCandidate(OcrQuestionCandidate? current, int index)
    {
        if (index <= 0)
        {
            return false;
        }

        if (current == null)
        {
            return true;
        }

        return index > current.Index;
    }

    private static OcrLineResult[] ReorderPageLinesByColumns(IReadOnlyList<OcrLineResult> lines)
    {
        if (lines.Count == 0)
        {
            return Array.Empty<OcrLineResult>();
        }

        var leftColumnCount = 0;
        var rightColumnCount = 0;

        foreach (var line in lines)
        {
            var midpoint = GetHorizontalMidpoint(line.LeftRatio, line.RightRatio);
            if (midpoint <= LeftColumnMidpointThreshold)
            {
                leftColumnCount++;
            }
            else if (midpoint >= RightColumnMidpointThreshold)
            {
                rightColumnCount++;
            }
        }

        if (leftColumnCount < MinColumnLineCount || rightColumnCount < MinColumnLineCount)
        {
            return lines
                .OrderBy(x => x.TopRatio)
                .ThenBy(x => x.LineInPage)
                .ToArray();
        }

        return lines
            .OrderBy(GetColumnIndex)
            .ThenBy(x => x.TopRatio)
            .ThenBy(x => x.LeftRatio)
            .ThenBy(x => x.LineInPage)
            .ToArray();
    }

    private static int GetColumnIndex(OcrLineResult line)
    {
        var midpoint = GetHorizontalMidpoint(line.LeftRatio, line.RightRatio);
        return midpoint <= LeftColumnMidpointThreshold ? 0 : 1;
    }

    private static double GetHorizontalMidpoint(double leftRatio, double rightRatio)
    {
        return (leftRatio + rightRatio) / 2d;
    }

    private static TLine[] TrimLeadingBoilerplateLines<TLine>(
        IReadOnlyList<TLine> sourceLines,
        Func<TLine, string> textSelector,
        QuestionNumberRange? expectedQuestionRange)
    {
        if (sourceLines.Count == 0)
        {
            return Array.Empty<TLine>();
        }

        var firstHeaderIndex = -1;
        for (var i = 0; i < sourceLines.Count; i++)
        {
            var currentText = textSelector(sourceLines[i]);
            var nextText = i + 1 < sourceLines.Count
                ? textSelector(sourceLines[i + 1])
                : null;

            if (IsLikelyQuestionHeaderLine(currentText, nextText, expectedQuestionRange))
            {
                firstHeaderIndex = i;
                break;
            }
        }

        if (firstHeaderIndex <= 0)
        {
            return sourceLines.ToArray();
        }

        return sourceLines.Skip(firstHeaderIndex).ToArray();
    }

    private static bool TryMatchQuestionHeader(
        IReadOnlyList<ParsedLine> lines,
        int lineIndex,
        Regex regex,
        QuestionNumberRange? expectedQuestionRange,
        out int index)
    {
        index = 0;
        if (lineIndex < 0 || lineIndex >= lines.Count)
        {
            return false;
        }

        var line = lines[lineIndex];
        var nextLineText = lineIndex + 1 < lines.Count && lines[lineIndex + 1].PageIndex == line.PageIndex
            ? lines[lineIndex + 1].Text
            : null;

        return TryMatchQuestionHeaderText(
            line.Text,
            nextLineText,
            regex,
            expectedQuestionRange,
            out index);
    }

    private static bool IsLikelyQuestionHeaderLine(
        string? text,
        string? nextText,
        QuestionNumberRange? expectedQuestionRange)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return TryMatchQuestionHeaderText(
            text,
            nextText,
            SingleLineHeaderRegex,
            expectedQuestionRange,
            out _);
    }

    private static bool TryMatchQuestionHeaderText(
        string? text,
        string? nextLineText,
        Regex regex,
        QuestionNumberRange? expectedQuestionRange,
        out int index)
    {
        index = 0;
        if (string.IsNullOrWhiteSpace(text) || IsDisallowedHeaderText(text))
        {
            return false;
        }

        var match = regex.Match(text);
        if (!match.Success || !TryNormalizeQuestionIndex(match.Groups["index"].Value, out index))
        {
            return false;
        }

        if (expectedQuestionRange.HasValue && !expectedQuestionRange.Value.Contains(index))
        {
            return false;
        }

        var suffix = text.Substring(Math.Min(match.Length, text.Length)).Trim();
        if (!IsPlausibleQuestionHeaderText(text, suffix, nextLineText))
        {
            index = 0;
            return false;
        }

        return true;
    }

    private static bool IsDisallowedHeaderText(string text)
    {
        var normalized = text.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return true;
        }

        if (Regex.IsMatch(normalized, @"^\s*제\s*\d+\s*과목\s*$", RegexOptions.CultureInvariant) ||
            Regex.IsMatch(normalized, @"^\s*\d+\s*(?:학기|과목|교시)\s*$", RegexOptions.CultureInvariant) ||
            Regex.IsMatch(normalized, @"^\s*\(?\d+\s*[-~〜]\s*.*번\)?\s*$", RegexOptions.CultureInvariant))
        {
            return true;
        }

        return normalized.Contains("출제위원", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("출제범위", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPlausibleQuestionHeaderText(
        string fullText,
        string suffix,
        string? nextLineText)
    {
        if (IsDisallowedHeaderText(fullText))
        {
            return false;
        }

        if (Regex.IsMatch(suffix, @"^(?:학기|과목|교시)\b", RegexOptions.CultureInvariant))
        {
            return false;
        }

        if (LooksLikeCodeLikeSuffix(suffix))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(suffix))
        {
            return CountHangulCharacters(suffix) >= 2 ||
                   suffix.Contains('?') ||
                   LooksLikeQuestionContinuation(nextLineText);
        }

        return LooksLikeQuestionContinuation(nextLineText);
    }

    private static bool LooksLikeCodeLikeSuffix(string suffix)
    {
        if (string.IsNullOrWhiteSpace(suffix))
        {
            return false;
        }

        var normalized = suffix.Trim();
        if (Regex.IsMatch(normalized, @"^[A-Z0-9_+\-*/=().,:;]+$", RegexOptions.CultureInvariant))
        {
            return true;
        }

        return Regex.IsMatch(normalized, @"^\d+(?:\.\d+)?$", RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeQuestionContinuation(string? nextLineText)
    {
        if (string.IsNullOrWhiteSpace(nextLineText) || IsDisallowedHeaderText(nextLineText))
        {
            return false;
        }

        var normalized = nextLineText.Trim();
        if (Regex.IsMatch(normalized, @"^\d{1,3}\s*(?:[.)\]\-:：]|$)", RegexOptions.CultureInvariant))
        {
            return false;
        }

        return CountHangulCharacters(normalized) >= 2 ||
               normalized.Contains('?');
    }

    private static int CountHangulCharacters(string text)
    {
        return text.Count(c => c >= '가' && c <= '힣');
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
                EndPageLineCount = GetPageLineCount(pageLineCounts, chunk[^1].PageIndex),
                PreviewText = preview
            });

            index++;
        }

        return roughCandidates;
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

    private readonly record struct ParsedLine(
        int PageIndex,
        int LineInPage,
        string Text,
        double LeftRatio = 0d,
        double TopRatio = 0d,
        double RightRatio = 1d,
        double BottomRatio = 1d);

    private readonly record struct StartMarker(
        int Index,
        int Position,
        int PageIndex,
        int LineInPage,
        string Header,
        string? ImagePath,
        bool Inferred);
}
