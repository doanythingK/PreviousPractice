using PreviousPractice.Models;

namespace PreviousPractice.Infrastructure;

public static partial class OcrQuestionSegmenter
{
    private static bool TryBuildNormalizedDocument(
        IReadOnlyList<OcrPageResult> pages,
        QuestionNumberRange? expectedQuestionRange,
        out NormalizedOcrDocument document)
    {
        document = default;
        if (pages.Count == 0)
        {
            AppLog.Error(nameof(OcrQuestionSegmenter), "분할 중단 | pages=0");
            return false;
        }

        var lines = new List<ParsedLine>();
        var pageLineCounts = new Dictionary<int, int>();
        var pageLayouts = new Dictionary<int, PageLayout>();
        var syntheticBreakCount = 0;

        foreach (var page in pages.OrderBy(x => x.PageIndex))
        {
            var pageNumber = page.PageIndex;
            if (page.Lines != null && page.Lines.Count > 0)
            {
                var sourceLines = page.Lines
                    .OrderBy(x => x.LineInPage)
                    .ToArray();
                var layout = BuildPageLayout(sourceLines);
                var maxLineInPage = 0;
                var parsedPageLines = new List<ParsedLine>();
                for (var sourceIndex = 0; sourceIndex < sourceLines.Length; sourceIndex++)
                {
                    var line = sourceLines[sourceIndex];
                    if (string.IsNullOrWhiteSpace(line.Text))
                    {
                        continue;
                    }

                    maxLineInPage = Math.Max(maxLineInPage, line.LineInPage);
                    var hasReliableGeometry = TryNormalizeGeometry(
                        line,
                        out var left,
                        out var top,
                        out var right,
                        out var bottom);
                    if (!hasReliableGeometry)
                    {
                        left = 0d;
                        right = 1d;
                        top = (double)sourceIndex / Math.Max(1, sourceLines.Length);
                        bottom = (double)(sourceIndex + 1) / Math.Max(1, sourceLines.Length);
                    }

                    var columnIndex = IsFullWidthGeometry(left, right, hasReliableGeometry, layout)
                        ? -1
                        : layout.ResolveColumnIndex(left, right);
                    var (columnLeft, columnRight) = layout.ResolveColumnBand(columnIndex);
                    var parsedLine = new ParsedLine(
                        pageNumber,
                        line.LineInPage,
                        line.Text.Trim(),
                        left,
                        top,
                        right,
                        bottom,
                        FragmentIndex: 0,
                        FragmentCount: 1,
                        columnIndex,
                        columnLeft,
                        columnRight,
                        hasReliableGeometry);
                    var expandedLines = ExpandParsedLine(parsedLine);
                    syntheticBreakCount += Math.Max(0, expandedLines.Count - 1);
                    parsedPageLines.AddRange(expandedLines);
                }

                var orderedPageLines = OrderParsedPageLines(parsedPageLines, layout, out var usesRowMajorOrder);
                layout = layout with { IsRowMajor = usesRowMajorOrder };
                pageLayouts[pageNumber] = layout;
                foreach (var parsedLine in orderedPageLines)
                {
                    lines.Add(parsedLine);
                }

                pageLineCounts[pageNumber] = Math.Max(1, maxLineInPage);
                continue;
            }

            var rawTextLines = page.Text
                .Replace('\r', '\n')
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            pageLayouts[pageNumber] = PageLayout.SingleColumn;
            var rawLines = rawTextLines
                .Select((rawLine, index) => new ParsedLine(
                    pageNumber,
                    index + 1,
                    rawLine.Trim(),
                    0d,
                    (double)index / Math.Max(1, rawTextLines.Length),
                    1d,
                    (double)(index + 1) / Math.Max(1, rawTextLines.Length),
                    FragmentIndex: 0,
                    FragmentCount: 1,
                    ColumnIndex: 0,
                    ColumnLeftRatio: 0d,
                    ColumnRightRatio: 1d,
                    HasReliableGeometry: false))
                .Where(x => !string.IsNullOrWhiteSpace(x.Text))
                .ToArray();

            var lineInPage = 0;
            foreach (var line in rawLines)
            {
                lineInPage = Math.Max(lineInPage, line.LineInPage);
                var expandedLines = ExpandParsedLine(line);
                syntheticBreakCount += Math.Max(0, expandedLines.Count - 1);
                foreach (var expandedLine in expandedLines)
                {
                    lines.Add(expandedLine);
                }
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
            return false;
        }

        document = new NormalizedOcrDocument(lines, pageLineCounts, pageLayouts, syntheticBreakCount);
        return true;
    }

    private static IReadOnlyList<ParsedLine> ExpandParsedLine(ParsedLine line)
    {
        var expandedText = ExpandSyntheticLineBreaks(line.Text, out _);
        var parts = expandedText
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
        if (parts.Length == 0)
        {
            return Array.Empty<ParsedLine>();
        }

        return parts
            .Select((part, index) => line with
            {
                Text = part.Trim(),
                FragmentIndex = index,
                FragmentCount = parts.Length
            })
            .ToArray();
    }

    private static PageLayout BuildPageLayout(IReadOnlyList<OcrLineResult> lines)
    {
        var reliable = lines
            .Select(line => TryNormalizeGeometry(line, out var left, out var top, out var right, out var bottom)
                ? new { line.Text, Left = left, Top = top, Right = right, Bottom = bottom }
                : null)
            .Where(x => x != null)
            .Select(x => x!)
            .ToArray();
        var leftLines = reliable
            .Where(x => GetHorizontalMidpoint(x.Left, x.Right) <= LeftColumnMidpointThreshold)
            .ToArray();
        var rightLines = reliable
            .Where(x => GetHorizontalMidpoint(x.Left, x.Right) >= RightColumnMidpointThreshold)
            .ToArray();
        var middleNarrowLines = reliable.Count(x =>
            GetHorizontalMidpoint(x.Left, x.Right) > LeftColumnMidpointThreshold &&
            GetHorizontalMidpoint(x.Left, x.Right) < RightColumnMidpointThreshold &&
            x.Right - x.Left < 0.45d);
        var horizontalOriginClusterCount = CountHorizontalOriginClusters(
            reliable
                .Where(x => x.Right - x.Left < 0.48d)
                .Select(x => (x.Left, Width: x.Right - x.Left)));
        var hasThreeColumnShape = horizontalOriginClusterCount >= 3 ||
                                  (leftLines.Length > 0 &&
                                   rightLines.Length > 0 &&
                                   middleNarrowLines > 0);
        var isSparseHeaderPair = leftLines.Length == 1 &&
                                 rightLines.Length == 1 &&
                                 TryReadHeaderIndexForOrdering(leftLines[0].Text, out var leftHeaderIndex) &&
                                 TryReadHeaderIndexForOrdering(rightLines[0].Text, out var rightHeaderIndex) &&
                                 rightHeaderIndex == leftHeaderIndex + 1 &&
                                 LooksLikeStrongLayoutHeader(leftLines[0].Text) &&
                                 LooksLikeStrongLayoutHeader(rightLines[0].Text) &&
                                 leftLines[0].Right - leftLines[0].Left < 0.48d &&
                                 rightLines[0].Right - rightLines[0].Left < 0.48d;
        if ((leftLines.Length < MinColumnLineCount || rightLines.Length < MinColumnLineCount) &&
            !isSparseHeaderPair)
        {
            return hasThreeColumnShape
                ? PageLayout.AmbiguousSingleColumn
                : PageLayout.SingleColumn;
        }

        var leftMedian = leftLines.Select(x => x.Left).OrderBy(x => x).ElementAt(leftLines.Length / 2);
        var rightMedian = rightLines.Select(x => x.Left).OrderBy(x => x).ElementAt(rightLines.Length / 2);
        if (rightMedian - leftMedian < 0.25d)
        {
            return PageLayout.SingleColumn;
        }

        var leftRightEdge = leftLines.Select(x => x.Right).OrderBy(x => x).ElementAt(leftLines.Length / 2);
        var rightLeftEdge = rightLines.Select(x => x.Left).OrderBy(x => x).ElementAt(rightLines.Length / 2);
        var gutter = rightLeftEdge > leftRightEdge
            ? (leftRightEdge + rightLeftEdge) / 2d
            : 0.5d;
        var leftHasStrongHeader = leftLines.Any(x => LooksLikeStrongLayoutHeader(x.Text));
        var rightHasStrongHeader = rightLines.Any(x => LooksLikeStrongLayoutHeader(x.Text));
        var requiredAlignedPairs = Math.Min(2, Math.Min(leftLines.Length, rightLines.Length));
        var alignedPairCount = leftLines.Count(left => rightLines.Any(right =>
            Math.Abs(
                (left.Top + left.Bottom) / 2d -
                (right.Top + right.Bottom) / 2d) <= 0.12d));
        var hasPersistentVerticalPairing = isSparseHeaderPair ||
                                           alignedPairCount >= requiredAlignedPairs;
        var hasReliableStructure = leftHasStrongHeader &&
                                   rightHasStrongHeader &&
                                   horizontalOriginClusterCount <= 2 &&
                                   middleNarrowLines == 0 &&
                                   hasPersistentVerticalPairing;
        return new PageLayout(
            true,
            Math.Clamp(gutter, 0.40d, 0.60d),
            HasReliableStructure: hasReliableStructure);
    }

    private static int CountHorizontalOriginClusters(
        IEnumerable<(double Left, double Width)> horizontalBands)
    {
        var clusterCount = 0;
        var clusterStart = double.NaN;
        var clusterWidth = double.NaN;
        foreach (var band in horizontalBands
                     .Where(x => double.IsFinite(x.Left) && double.IsFinite(x.Width) && x.Width > 0d)
                     .OrderBy(x => x.Left))
        {
            var adaptiveTolerance = clusterCount == 0
                ? 0d
                : Math.Clamp(Math.Min(clusterWidth, band.Width) * 0.75d, 0.06d, 0.18d);
            if (clusterCount == 0 || band.Left - clusterStart > adaptiveTolerance)
            {
                clusterCount++;
                clusterStart = band.Left;
                clusterWidth = band.Width;
            }
            else
            {
                clusterWidth = Math.Max(clusterWidth, band.Width);
            }
        }

        return clusterCount;
    }

    private static bool LooksLikeStrongLayoutHeader(string text)
    {
        foreach (var regex in new[]
                 {
                     SingleLineHeaderRegex,
                     CircledHeaderRegex,
                     HangulHeaderRegex,
                     LatinHeaderRegex
                 })
        {
            var match = regex.Match(text);
            if (!match.Success)
            {
                continue;
            }

            var suffix = text.Substring(Math.Min(match.Length, text.Length)).Trim();
            return match.Groups["prefix"].Success ||
                   match.Groups["beon"].Success ||
                   suffix.Contains('?') ||
                   suffix.Contains("문제", StringComparison.Ordinal) ||
                   suffix.Contains("문항", StringComparison.Ordinal) ||
                   suffix.StartsWith("다음", StringComparison.Ordinal) ||
                   StrongQuestionSentenceRegex.IsMatch(suffix);
        }

        return false;
    }

    private static IReadOnlyList<ParsedLine> OrderParsedPageLines(
        IReadOnlyList<ParsedLine> lines,
        PageLayout layout,
        out bool usesRowMajorOrder)
    {
        var hasUnreliableGeometry = lines.Any(x => !x.HasReliableGeometry);
        usesRowMajorOrder = layout.IsTwoColumn && ShouldUseRowMajorOrder(lines, hasUnreliableGeometry);
        if (!layout.IsTwoColumn)
        {
            return lines
                .OrderBy(x => hasUnreliableGeometry ? x.LineInPage : x.TopRatio)
                .ThenBy(x => x.LeftRatio)
                .ThenBy(x => x.FragmentIndex)
                .ToArray();
        }

        var fullWidthLines = lines
            .Where(x => x.ColumnIndex < 0)
            .OrderBy(x => x.TopRatio)
            .ThenBy(x => x.LineInPage)
            .ThenBy(x => x.FragmentIndex)
            .ToArray();
        if (fullWidthLines.Length == 0)
        {
            return OrderColumnRegion(lines, hasUnreliableGeometry, usesRowMajorOrder);
        }

        var regularLines = lines
            .Where(x => x.ColumnIndex >= 0)
            .ToList();
        var ordered = new List<ParsedLine>(lines.Count);
        foreach (var fullWidthLine in fullWidthLines)
        {
            var preceding = regularLines
                .Where(x => x.TopRatio < fullWidthLine.TopRatio - 0.005d)
                .ToArray();
            ordered.AddRange(OrderColumnRegion(preceding, hasUnreliableGeometry, usesRowMajorOrder));
            foreach (var precedingLine in preceding)
            {
                regularLines.Remove(precedingLine);
            }

            ordered.Add(fullWidthLine);
        }

        ordered.AddRange(OrderColumnRegion(regularLines, hasUnreliableGeometry, usesRowMajorOrder));
        return ordered;
    }

    private static ParsedLine[] OrderColumnRegion(
        IEnumerable<ParsedLine> lines,
        bool useLineNumberOrder,
        bool useRowMajorOrder)
    {
        var materialized = lines.ToArray();
        var columnMajor = materialized
            .OrderBy(x => x.ColumnIndex)
            .ThenBy(x => useLineNumberOrder ? x.LineInPage : x.TopRatio)
            .ThenBy(x => x.LeftRatio)
            .ThenBy(x => x.FragmentIndex)
            .ToArray();
        var rowMajor = materialized
            .OrderBy(x => useLineNumberOrder ? x.LineInPage : x.TopRatio)
            .ThenBy(x => x.ColumnIndex)
            .ThenBy(x => x.LeftRatio)
            .ThenBy(x => x.FragmentIndex)
            .ToArray();

        return useRowMajorOrder ? rowMajor : columnMajor;
    }

    private static bool ShouldUseRowMajorOrder(
        IReadOnlyList<ParsedLine> lines,
        bool useLineNumberOrder)
    {
        var columnMajor = lines
            .Where(x => x.ColumnIndex >= 0)
            .OrderBy(x => x.ColumnIndex)
            .ThenBy(x => useLineNumberOrder ? x.LineInPage : x.TopRatio)
            .ThenBy(x => x.LeftRatio)
            .ThenBy(x => x.FragmentIndex)
            .ToArray();
        var rowMajor = lines
            .Where(x => x.ColumnIndex >= 0)
            .OrderBy(x => useLineNumberOrder ? x.LineInPage : x.TopRatio)
            .ThenBy(x => x.ColumnIndex)
            .ThenBy(x => x.LeftRatio)
            .ThenBy(x => x.FragmentIndex)
            .ToArray();
        return ScoreHeaderReadingOrder(rowMajor) > ScoreHeaderReadingOrder(columnMajor);
    }

    private static int ScoreHeaderReadingOrder(IReadOnlyList<ParsedLine> lines)
    {
        var indexes = lines
            .Select(line => TryReadHeaderIndexForOrdering(line.Text, out var index) ? index : (int?)null)
            .Where(index => index.HasValue)
            .Select(index => index!.Value)
            .ToArray();
        if (indexes.Length < 2)
        {
            return 0;
        }

        var score = 0;
        for (var index = 1; index < indexes.Length; index++)
        {
            var difference = indexes[index] - indexes[index - 1];
            score += difference == 1
                ? 12
                : difference > 1
                    ? Math.Max(-8, 3 - difference)
                    : -12;
        }

        return score;
    }

    private static bool TryReadHeaderIndexForOrdering(string text, out int index)
    {
        index = 0;
        foreach (var regex in new[]
                 {
                     SingleLineHeaderRegex,
                     CircledHeaderRegex,
                     HangulHeaderRegex,
                     LatinHeaderRegex
                 })
        {
            var match = regex.Match(text);
            if (match.Success && TryNormalizeQuestionIndex(match.Groups["index"].Value, out index))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsFullWidthGeometry(
        double left,
        double right,
        bool hasReliableGeometry,
        PageLayout layout)
    {
        return layout.IsTwoColumn &&
               hasReliableGeometry &&
               left < layout.GutterRatio - 0.12d &&
               right > layout.GutterRatio + 0.12d;
    }

    private static bool TryNormalizeGeometry(
        OcrLineResult line,
        out double left,
        out double top,
        out double right,
        out double bottom)
    {
        left = line.LeftRatio;
        top = line.TopRatio;
        right = line.RightRatio;
        bottom = line.BottomRatio;
        if (!line.HasReliableGeometry ||
            !double.IsFinite(left) ||
            !double.IsFinite(top) ||
            !double.IsFinite(right) ||
            !double.IsFinite(bottom) ||
            right <= left ||
            bottom <= top)
        {
            return false;
        }

        left = Math.Clamp(left, 0d, 1d);
        top = Math.Clamp(top, 0d, 1d);
        right = Math.Clamp(right, 0d, 1d);
        bottom = Math.Clamp(bottom, 0d, 1d);
        return right > left && bottom > top &&
               !(left <= 0d && top <= 0d && right >= 1d && bottom >= 1d);
    }

    private static double GetHorizontalMidpoint(double leftRatio, double rightRatio)
    {
        return (leftRatio + rightRatio) / 2d;
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
}
