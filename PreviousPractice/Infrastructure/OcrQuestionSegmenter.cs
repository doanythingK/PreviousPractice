using System.Text;
using System.Text.RegularExpressions;
using PreviousPractice.Models;

namespace PreviousPractice.Infrastructure;

public static class OcrQuestionSegmenter
{
    private const int CandidateLogLimit = 20;
    private const double LeftColumnMidpointThreshold = 0.42d;
    private const double RightColumnMidpointThreshold = 0.58d;
    private const int MinColumnLineCount = 2;
    private const int MaxQuestionIndexForwardGap = 8;
    private const int MinQuestionIndexBeforeSequenceReset = 3;
    private const int SequenceResetConfirmationCount = 2;
    private const string HangulQuestionIndexCharacters = "가나다라마바사아자차카타파하";
    private const double HeaderAlignmentTolerance = 0.045d;
    private const double QuestionRegionPaddingRatio = 0.015d;
    // OCR이 페이지를 한 줄로 뭉개는 경우, " 1. ", " 2) " 같은 번호 앞에 강제로 줄바꿈을 삽입한다.
    private static readonly Regex SyntheticQuestionBreakRegex = new(
        @"(?<prefix>^|\s)(?<header>(?:[1-9]|[1-9]\d)\s*[.)•·])(?=\s)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex QuestionRangeRegex = new(
        @"(?<!\d)[\[<(（【〔［]?\s*(?<start>\d{1,3})\s*[-~〜]\s*(?<end>\d{1,3})\s*[\]>)）】〕］]?(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex QuestionRangePrefixCueRegex = new(
        @"(?:(?:문항|문제)(?:\s*번호)?\s*범위|출제\s*범위)\s*[:：]?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex QuestionRangeSuffixCueRegex = new(
        @"^\s*번\s*[.)>）】〕］]?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex NonQuestionRangeSuffixRegex = new(
        @"^\s*(?:%|퍼센트|년|월|일|점|쪽|페이지|원|명|개|세|cm\b|mm\b|m\b|kg\b)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex SingleLineHeaderRegex = new(
        @"^\s*[\[(（【〔［]?\s*(?:(?<prefix>[Qq]|제|문항|문제)\s*)?(?<index>\d{1,3})\s*(?:(?<beon>번)(?=\s|[.)\]）】〕］:：•·．]|$)|(?<delimiter>[.)\]）】〕］\-:：•·．])|(?<space>\s+)|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CircledHeaderRegex = new(
        @"^\s*(?<index>[①②③④⑤⑥⑦⑧⑨⑩⑪⑫⑬⑭⑮⑯⑰⑱⑲⑳])\s*(?:(?<delimiter>[.)\]）】〕］\-:：•·．])|(?<space>\s+)|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex HangulHeaderRegex = new(
        @"^\s*(?<index>[가나다라마바사아자차카타파하])\s*(?:(?<delimiter>[.)\]）】〕］\-:：•·．])|(?<space>\s+)|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LatinHeaderRegex = new(
        @"^\s*(?<index>[A-Za-z])\s*(?:(?<delimiter>[.)\]）】〕］\-:：•·．]{1,2})|(?<space>\s+)|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PermissiveHeaderRegex = new(
        @"^\s*[\[(（【〔［]?\s*(?:(?<prefix>[Qq]|제|문항|문제)\s*)?(?<index>\d{1,3}|[①②③④⑤⑥⑦⑧⑨⑩⑪⑫⑬⑭⑮⑯⑰⑱⑲⑳]|[가나다라마바사아자차카타파하]|[A-Za-z])\s*(?:(?<beon>번)(?=\s|[.)\]）】〕］:：•·．]|$)|(?<delimiter>[.)\]）】〕］\-:：•·．]{1,2})|(?<space>\s+)|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex NumericQuantitySuffixRegex = new(
        @"^(?:개(?:의|를|가|는|\s|$)|명(?:이|을|의|\s|$)|년(?:에|의|\s|$)|월(?:에|의|\s|$)|일(?:에|의|\s|$)|단계(?:에서|에는|의|를|\s|$)|가지(?:의|를|가|는|\s|$)|쪽(?:에|의|\s|$)|페이지(?:에|의|\s|$)|점(?:을|이|의|\s|$)|%|퍼센트|회(?:를|가|는|\s|$)|세(?!\s*번째)(?:의|\s|$)|원(?:을|이|의|\s|$)|cm\b|mm\b|m\b|kg\b)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex NumericBeonActionSuffixRegex = new(
        @"^(?:반복|실행|시도|클릭|사용|선택|입력|호출|수행|연속)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex StrongQuestionSentenceRegex = new(
        @"(?:고르시오|구하시오|답하시오|쓰시오|서술하시오|옳은\s*것은|틀린\s*것은|알맞은\s*것은|무엇인가|어느\s*것인가)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex OptionCueRegex = new(
        @"(?:보기|선택지|첫\s*번째|두\s*번째|세\s*번째|네\s*번째|다섯\s*번째)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ExplicitSharedContextRangeRegex = new(
        @"(?<!\d)[\[<(（【〔［]?\s*(?<start>\d{1,3})\s*[-~〜]\s*(?<end>\d{1,3})\s*[\]>)）】〕］]?(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly string[] BlockingBoilerplatePhrases =
    [
        "출제위원",
        "출제범위",
        "출석수업대체시험",
        "정답 하나",
        "답안정정",
        "답안지에 표기",
        "다음 면에 계속",
        "앞면에서 계속"
    ];
    private static readonly Regex SharedContextMarkerRegex = new(
        @"[<(（【〔［]?[^\r\n]{0,8}(?:\d{1,3}|[%A-Za-z가-힣①-⑳㉠-㉻])\s*[-~〜]\s*(?:\d{1,3}|[%A-Za-z가-힣①-⑳㉠-㉻])[^\r\n]{0,8}[>)）】〕］]?\s*(?:다음|보고|답하)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex QuestionCueRegex = new(
        @"(?:문제|다음|옳은|틀린|고른|설명|해석|판단|답하|보고|보기|물음|맞는|아닌|않은|고르시오|구하|무엇|해당|정의|알맞|올바|바르|사용|넣을|들어갈)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<OcrQuestionCandidate> SplitByHeader(
        IReadOnlyList<OcrPageResult> pages,
        QuestionNumberRange? expectedQuestionRange = null)
    {
        var hasDuplicatePageIndexes = pages
            .GroupBy(x => x.PageIndex)
            .Any(x => x.Count() > 1);
        var effectiveQuestionRange = expectedQuestionRange ?? InferQuestionRangeFromPages(pages);
        if (!TryBuildNormalizedDocument(pages, effectiveQuestionRange, out var document))
        {
            return Array.Empty<OcrQuestionCandidate>();
        }

        var hypotheses = CollectHeaderHypotheses(document.Lines, effectiveQuestionRange);
        var selection = SelectHeaderPath(document.Lines, hypotheses, effectiveQuestionRange);
        if (selection.Markers.Count == 0)
        {
            AppLog.Info(
                nameof(OcrQuestionSegmenter),
                $"분할 결과(헤더 없음) | pages={pages.Count} | lines={document.Lines.Count} | syntheticBreaks={document.SyntheticBreakCount} | hypotheses={hypotheses.Count} | candidates=0");
            return Array.Empty<OcrQuestionCandidate>();
        }

        var candidates = RebuildCandidatesFromMarkers(
            document.Lines,
            document.PageLineCounts,
            selection.Markers,
            document.PageLayouts,
            selection.HasDocumentAmbiguity || hasDuplicatePageIndexes,
            string.Join(
                " ",
                new[]
                {
                    selection.AmbiguityReason,
                    hasDuplicatePageIndexes
                        ? "같은 페이지 번호가 둘 이상 있어 문항의 물리적 순서를 확정할 수 없습니다."
                        : string.Empty
                }.Where(x => !string.IsNullOrWhiteSpace(x))));

        AppLog.Info(
            nameof(OcrQuestionSegmenter),
            $"분할 결과(문서 경로) | pages={pages.Count} | lines={document.Lines.Count} | syntheticBreaks={document.SyntheticBreakCount} | hypotheses={hypotheses.Count} | selected={selection.Markers.Count} | resets={selection.ResetCount} | ambiguous={(selection.HasDocumentAmbiguity ? 1 : 0)} | candidates={candidates.Count}");
        LogCandidateSummary("header", candidates);
        return candidates;
    }

    public static IReadOnlyList<OcrQuestionCandidate> RefineCandidates(
        IReadOnlyList<OcrPageResult> pages,
        QuestionNumberRange expectedQuestionRange,
        IReadOnlyList<OcrQuestionCandidate> seedCandidates)
    {
        var rebuilt = SplitByHeader(pages, expectedQuestionRange);
        AppLog.Info(
            nameof(OcrQuestionSegmenter),
            $"후보 재정제 | expected={expectedQuestionRange} | seed={seedCandidates.Count} | rebuilt={rebuilt.Count}");
        LogCandidateSummary("refine", rebuilt);
        return rebuilt;
    }

    private static QuestionNumberRange? InferQuestionRangeFromPages(IReadOnlyList<OcrPageResult> pages)
    {
        var detectedRanges = new Dictionary<QuestionNumberRange, string>();
        foreach (var text in EnumerateRangeHintText(pages).Take(80))
        {
            foreach (Match match in QuestionRangeRegex.Matches(text))
            {
                if (!IsLikelyQuestionRangeHint(text, match))
                {
                    continue;
                }

                if (!int.TryParse(match.Groups["start"].Value, out var startIndex) ||
                    !int.TryParse(match.Groups["end"].Value, out var endIndex) ||
                    startIndex <= 0 ||
                    endIndex < startIndex)
                {
                    continue;
                }

                var range = new QuestionNumberRange(startIndex, endIndex);
                if (range.Count is < 5 or > 100)
                {
                    continue;
                }

                detectedRanges.TryAdd(range, text);
            }
        }

        if (detectedRanges.Count != 1)
        {
            if (detectedRanges.Count > 1)
            {
                AppLog.Error(
                    nameof(OcrQuestionSegmenter),
                    $"문항 범위 자동 감지 보류 | conflicting={string.Join(",", detectedRanges.Keys.OrderBy(x => x.StartIndex))}");
            }

            return null;
        }

        var detected = detectedRanges.Single();
        AppLog.Info(
            nameof(OcrQuestionSegmenter),
            $"문항 범위 자동 감지 | range={detected.Key} | text={TrimToLength(detected.Value.Trim(), 80)}");
        return detected.Key;
    }

    private static bool IsLikelyQuestionRangeHint(string text, Match match)
    {
        var suffixStart = Math.Min(text.Length, match.Index + match.Length);
        var suffix = text.Substring(suffixStart);
        if (NonQuestionRangeSuffixRegex.IsMatch(suffix))
        {
            return false;
        }

        var prefix = text.Substring(0, Math.Max(0, match.Index));
        if (QuestionRangePrefixCueRegex.IsMatch(prefix))
        {
            return true;
        }

        var standalonePrefix = prefix.Trim();
        return (standalonePrefix.Length == 0 ||
                Regex.IsMatch(standalonePrefix, @"^[\[<(（【〔［]+$", RegexOptions.CultureInvariant)) &&
               QuestionRangeSuffixCueRegex.IsMatch(suffix);
    }

    private static IEnumerable<string> EnumerateRangeHintText(IReadOnlyList<OcrPageResult> pages)
    {
        foreach (var page in pages.OrderBy(x => x.PageIndex).Take(3))
        {
            if (page.Lines != null && page.Lines.Count > 0)
            {
                foreach (var line in page.Lines
                             .OrderBy(x => x.LineInPage)
                             .Take(40)
                             .Select(x => x.Text)
                             .Where(x => !string.IsNullOrWhiteSpace(x)))
                {
                    yield return line;
                }
            }

            foreach (var line in page.Text
                         .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                         .Take(40))
            {
                yield return line;
            }
        }
    }

    private static List<HeaderHypothesis> CollectHeaderHypotheses(
        IReadOnlyList<ParsedLine> lines,
        QuestionNumberRange? expectedQuestionRange)
    {
        var hypotheses = new List<HeaderHypothesis>();
        for (var position = 0; position < lines.Count; position++)
        {
            if (TryCreateHeaderHypothesis(
                    lines,
                    position,
                    SingleLineHeaderRegex,
                    HeaderKind.Numeric,
                    expectedQuestionRange,
                    out var numeric))
            {
                hypotheses.Add(numeric);
                continue;
            }

            foreach (var (regex, kind) in new[]
                     {
                         (CircledHeaderRegex, HeaderKind.Circled),
                         (HangulHeaderRegex, HeaderKind.Hangul),
                         (LatinHeaderRegex, HeaderKind.Latin)
                     })
            {
                if (TryCreateHeaderHypothesis(
                        lines,
                        position,
                        regex,
                        kind,
                        expectedQuestionRange,
                        out var hypothesis))
                {
                    hypotheses.Add(hypothesis);
                    break;
                }
            }
        }

        if (hypotheses.Count == 0)
        {
            return hypotheses;
        }

        var reliableLeftOffsets = hypotheses
            .Where(x => x.HasReliableGeometry && !x.IsBare)
            .Select(x => x.RelativeLeft)
            .Where(double.IsFinite)
            .OrderBy(x => x)
            .ToArray();
        var dominantLeft = reliableLeftOffsets.Length == 0
            ? (double?)null
            : reliableLeftOffsets[Math.Min(reliableLeftOffsets.Length - 1, reliableLeftOffsets.Length / 10)];

        for (var index = 0; index < hypotheses.Count; index++)
        {
            var hypothesis = hypotheses[index];
            var isLikelyChoice = dominantLeft.HasValue &&
                                 hypothesis.HasReliableGeometry &&
                                 !hypothesis.HasExplicitPrefix &&
                                 !hypothesis.HasNumberSuffix &&
                                 hypothesis.RelativeLeft - dominantLeft.Value > HeaderAlignmentTolerance;
            var score = hypothesis.Score;
            if (dominantLeft.HasValue && hypothesis.HasReliableGeometry)
            {
                var leftDifference = Math.Abs(hypothesis.RelativeLeft - dominantLeft.Value);
                score += leftDifference <= HeaderAlignmentTolerance
                    ? 55
                    : -Math.Min(180, (int)(leftDifference * 900));
            }

            if (isLikelyChoice)
            {
                score -= 220;
            }

            hypotheses[index] = hypothesis with
            {
                IsLikelyChoice = isLikelyChoice,
                Score = score
            };
        }

        return hypotheses;
    }

    private static bool TryCreateHeaderHypothesis(
        IReadOnlyList<ParsedLine> lines,
        int position,
        Regex regex,
        HeaderKind kind,
        QuestionNumberRange? expectedQuestionRange,
        out HeaderHypothesis hypothesis)
    {
        hypothesis = default;
        if (position < 0 || position >= lines.Count)
        {
            return false;
        }

        var line = lines[position];
        if (string.IsNullOrWhiteSpace(line.Text) || IsDisallowedHeaderText(line.Text))
        {
            return false;
        }

        var match = regex.Match(line.Text);
        if (!match.Success || !TryNormalizeQuestionIndex(match.Groups["index"].Value, out var index))
        {
            return false;
        }

        var suffix = line.Text.Substring(Math.Min(match.Length, line.Text.Length)).Trim();
        var hasExplicitPrefix = match.Groups["prefix"].Success;
        var hasNumberSuffix = match.Groups["beon"].Success;
        var delimiter = match.Groups["delimiter"].Success
            ? match.Groups["delimiter"].Value
            : string.Empty;
        var hasDelimiter = !string.IsNullOrWhiteSpace(delimiter);
        var isBare = !hasExplicitPrefix && !hasNumberSuffix && !hasDelimiter;
        var nextLineText = position + 1 < lines.Count && lines[position + 1].PageIndex == line.PageIndex
            ? lines[position + 1].Text
            : null;

        if (kind == HeaderKind.Numeric && NumericQuantitySuffixRegex.IsMatch(suffix))
        {
            return false;
        }

        if (kind == HeaderKind.Numeric &&
            hasNumberSuffix &&
            NumericBeonActionSuffixRegex.IsMatch(suffix))
        {
            return false;
        }

        if (isBare && string.IsNullOrWhiteSpace(suffix))
        {
            return false;
        }

        if (LooksLikeCodeLikeSuffix(suffix) ||
            !IsPlausibleHeaderHypothesis(suffix, nextLineText, hasExplicitPrefix || hasNumberSuffix || hasDelimiter))
        {
            return false;
        }

        var hasQuestionMark = suffix.Contains('?');
        var hasQuestionCue = QuestionCueRegex.IsMatch(suffix);
        var mentionsQuestion = suffix.Contains("문제", StringComparison.Ordinal) ||
                               suffix.Contains("문항", StringComparison.Ordinal);
        var hasStrongQuestionEvidence = mentionsQuestion ||
                                        hasQuestionMark ||
                                        suffix.StartsWith("다음", StringComparison.Ordinal) ||
                                        StrongQuestionSentenceRegex.IsMatch(suffix);
        var markerSyntaxRank = hasExplicitPrefix
            ? 4
            : hasNumberSuffix
                ? 3
                : delimiter.Contains('.') || delimiter.Contains('．') ||
                  delimiter.Contains(':') || delimiter.Contains('：')
                    ? 3
                    : hasDelimiter ? 2 : 1;
        var score = 25;
        score += hasExplicitPrefix ? 105 : 0;
        score += hasNumberSuffix ? 45 : 0;
        score += hasDelimiter ? ResolveDelimiterScore(delimiter) : -35;
        score += hasQuestionMark ? 35 : 0;
        score += hasQuestionCue ? 25 : 0;
        score += mentionsQuestion ? 45 : 0;
        score += CountHangulCharacters(suffix) >= 8 ? 10 : 0;
        score += expectedQuestionRange.HasValue && expectedQuestionRange.Value.Contains(index) ? 20 : 0;
        score += line.HasReliableGeometry ? 10 : -10;

        var relativeLeft = line.ColumnRightRatio - line.ColumnLeftRatio > 0.01d
            ? (line.LeftRatio - line.ColumnLeftRatio) /
              (line.ColumnRightRatio - line.ColumnLeftRatio)
            : line.LeftRatio;
        relativeLeft = double.IsFinite(relativeLeft) ? Math.Clamp(relativeLeft, 0d, 1d) : 0d;

        hypothesis = new HeaderHypothesis(
            index,
            position,
            line.PageIndex,
            line.LineInPage,
            line.FragmentIndex,
            line.FragmentCount,
            line.Text,
            kind,
            score,
            relativeLeft,
            line.HasReliableGeometry,
            hasExplicitPrefix,
            hasNumberSuffix,
            hasDelimiter,
            isBare,
            IsLikelyChoice: false,
            hasStrongQuestionEvidence,
            markerSyntaxRank);
        return true;
    }

    private static bool IsPlausibleHeaderHypothesis(
        string suffix,
        string? nextLineText,
        bool hasMarkerSyntax)
    {
        if (string.IsNullOrWhiteSpace(suffix))
        {
            return hasMarkerSyntax && LooksLikeQuestionContinuation(nextLineText);
        }

        if (suffix.Contains('?') || QuestionCueRegex.IsMatch(suffix))
        {
            return true;
        }

        if (CountHangulCharacters(suffix) >= 4)
        {
            return true;
        }

        return hasMarkerSyntax && LooksLikeQuestionContinuation(nextLineText);
    }

    private static int ResolveDelimiterScore(string delimiter)
    {
        if (delimiter.Contains('.') || delimiter.Contains('．') || delimiter.Contains(':') || delimiter.Contains('：'))
        {
            return 35;
        }

        if (delimiter.Contains(')') || delimiter.Contains('）') || delimiter.Contains(']') || delimiter.Contains('】'))
        {
            return 15;
        }

        return 20;
    }

    private static HeaderPathSelection SelectHeaderPath(
        IReadOnlyList<ParsedLine> lines,
        IReadOnlyList<HeaderHypothesis> hypotheses,
        QuestionNumberRange? expectedQuestionRange)
    {
        if (hypotheses.Count == 0)
        {
            return HeaderPathSelection.Empty;
        }

        var ordered = hypotheses
            .OrderBy(x => x.Position)
            .ThenByDescending(x => x.Score)
            .ToArray();
        var sectionStarts = FindSectionStartPositions(ordered, expectedQuestionRange);
        var selected = new List<HeaderHypothesis>();
        var isAmbiguous = false;
        var ambiguityReasons = new List<string>();

        for (var sectionIndex = 0; sectionIndex < sectionStarts.Count; sectionIndex++)
        {
            var startPosition = sectionStarts[sectionIndex];
            var endPosition = sectionIndex + 1 < sectionStarts.Count
                ? sectionStarts[sectionIndex + 1]
                : int.MaxValue;
            var sectionHypotheses = ordered
                .Where(x => x.Position >= startPosition && x.Position < endPosition)
                .ToArray();
            var sectionRange = sectionIndex == 0 ? expectedQuestionRange : null;
            var section = SelectBestMonotonicPath(lines, sectionHypotheses, sectionRange);
            if (section.Markers.Count == 0)
            {
                continue;
            }

            selected.AddRange(section.Markers);
            if (section.IsAmbiguous)
            {
                isAmbiguous = true;
                ambiguityReasons.Add(section.AmbiguityReason);
            }
        }

        selected = selected
            .GroupBy(x => x.Position)
            .Select(x => x.OrderByDescending(y => y.Score).First())
            .OrderBy(x => x.Position)
            .ToList();
        if (selected.Count == 0)
        {
            return HeaderPathSelection.Empty;
        }

        if (selected.Any(x => x.FragmentCount > 1) ||
            selected.GroupBy(x => (x.PageIndex, x.LineInPage)).Any(x => x.Count() > 1))
        {
            isAmbiguous = true;
            ambiguityReasons.Add("한 OCR 행에서 여러 문항 헤더가 검출되어 이미지 경계를 공간적으로 구분할 수 없습니다.");
        }

        if (selected.Any(x =>
                ExplicitSharedContextRangeRegex.IsMatch(x.Header) &&
                (x.Header.Contains("다음", StringComparison.Ordinal) ||
                 x.Header.Contains("보고", StringComparison.Ordinal) ||
                 x.Header.Contains("답하", StringComparison.Ordinal))))
        {
            isAmbiguous = true;
            ambiguityReasons.Add("문항 헤더와 공통 지문 범위가 같은 OCR 행에 있어 경계를 분리할 수 없습니다.");
        }

        var sequenceLacksQuestionEvidence = selected.Count >= 2 &&
                                            selected.All(x =>
                                                !x.HasStrongQuestionEvidence &&
                                                !x.HasExplicitPrefix);
        var sequenceUsesChoiceLikeMarkers = selected.Count >= 2 &&
                                            selected.All(x =>
                                                !x.HasExplicitPrefix &&
                                                !x.HasNumberSuffix &&
                                                (x.Kind != HeaderKind.Numeric || x.MarkerSyntaxRank <= 2));
        if (sequenceLacksQuestionEvidence || sequenceUsesChoiceLikeMarkers)
        {
            isAmbiguous = true;
            ambiguityReasons.Add("선택지에도 흔한 번호 형식만 연속되어 문항 헤더인지 확정할 수 없습니다.");
        }

        var repeatedHeaderBodies = selected
            .Select(x => PermissiveHeaderRegex.Replace(x.Header, string.Empty, 1).Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray();
        if (selected.Count >= 3 &&
            repeatedHeaderBodies.Length == selected.Count &&
            repeatedHeaderBodies.Distinct(StringComparer.Ordinal).Count() * 2 <= selected.Count)
        {
            isAmbiguous = true;
            ambiguityReasons.Add("동일한 질문 문구가 번호만 바뀌어 반복되어 선택지 블록인지 확정할 수 없습니다.");
        }

        var isDenseSequence = selected.Count >= 3 &&
                              selected
                                  .Zip(selected.Skip(1), (left, right) => right.Position - left.Position)
                                  .All(gap => gap == 1);
        var isDenseUnprefixedSequence = isDenseSequence &&
                                        selected
                                            .Select(x => lines[x.Position].ColumnIndex)
                                            .Distinct()
                                            .Count() == 1 &&
                                        selected.All(x => !x.HasExplicitPrefix && !x.HasNumberSuffix);
        var isDenseOptionCueSequence = isDenseSequence &&
                                       repeatedHeaderBodies.Length == selected.Count &&
                                       repeatedHeaderBodies.All(body => OptionCueRegex.IsMatch(body));
        if (isDenseUnprefixedSequence || isDenseOptionCueSequence)
        {
            isAmbiguous = true;
            ambiguityReasons.Add("문항 본문 없이 선택지 표현만 번호별로 연속되어 문항 헤더인지 확정할 수 없습니다.");
        }

        if (selected.Count == 1 &&
            !selected[0].HasStrongQuestionEvidence &&
            !selected[0].HasExplicitPrefix)
        {
            isAmbiguous = true;
            ambiguityReasons.Add("질문 근거가 없는 단일 번호 행만 검출되어 문항 헤더인지 확정할 수 없습니다.");
        }

        var markers = selected
            .Select(x => new StartMarker(
                x.Index,
                x.Position,
                x.PageIndex,
                x.LineInPage,
                x.Header,
                ImagePath: null,
                Inferred: false,
                x.FragmentIndex,
                x.FragmentCount))
            .ToArray();
        return new HeaderPathSelection(
            markers,
            Math.Max(0, sectionStarts.Count - 1),
            isAmbiguous,
            string.Join(" ", ambiguityReasons.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct()));
    }

    private static List<int> FindSectionStartPositions(
        IReadOnlyList<HeaderHypothesis> hypotheses,
        QuestionNumberRange? expectedQuestionRange)
    {
        var first = hypotheses
            .Where(x => !x.IsLikelyChoice)
            .OrderBy(x => x.Position)
            .FirstOrDefault();
        if (first == default)
        {
            return new List<int> { hypotheses.Min(x => x.Position) };
        }

        var resetIndexes = new HashSet<int> { 1, first.Index };
        if (expectedQuestionRange.HasValue)
        {
            resetIndexes.Add(expectedQuestionRange.Value.StartIndex);
        }

        var starts = new List<int> { hypotheses.Min(x => x.Position) };
        foreach (var current in hypotheses.Where(x => !x.IsLikelyChoice).OrderBy(x => x.Position))
        {
            if (!resetIndexes.Contains(current.Index) || current.Position == first.Position)
            {
                continue;
            }

            var hasLargerIndexOnPreviousPage = hypotheses.Any(x =>
                !x.IsLikelyChoice &&
                x.Position < current.Position &&
                x.PageIndex < current.PageIndex &&
                x.Index > current.Index);
            if (!hasLargerIndexOnPreviousPage)
            {
                continue;
            }

            var confirmation = hypotheses
                .Where(x =>
                    !x.IsLikelyChoice &&
                    x.Position > current.Position &&
                    x.Index == current.Index + 1)
                .OrderBy(x => x.Position)
                .FirstOrDefault();
            if (confirmation == default ||
                confirmation.PageIndex < current.PageIndex ||
                confirmation.Position - current.Position > 80 ||
                Math.Abs(confirmation.RelativeLeft - current.RelativeLeft) > HeaderAlignmentTolerance * 1.5d)
            {
                continue;
            }

            starts.Add(current.Position);
        }

        return starts.Distinct().OrderBy(x => x).ToList();
    }

    private static SelectedPath SelectBestMonotonicPath(
        IReadOnlyList<ParsedLine> lines,
        IReadOnlyList<HeaderHypothesis> hypotheses,
        QuestionNumberRange? expectedQuestionRange)
    {
        var nodes = hypotheses
            .Where(x => !expectedQuestionRange.HasValue || expectedQuestionRange.Value.Contains(x.Index))
            .OrderBy(x => x.Position)
            .ThenByDescending(x => x.Score)
            .ToArray();
        if (nodes.Length == 0)
        {
            return SelectedPath.Empty;
        }

        var states = new PathState[nodes.Length];
        for (var index = 0; index < nodes.Length; index++)
        {
            var node = nodes[index];
            var startScore = node.Score + ResolvePathStartScore(node, expectedQuestionRange);
            states[index] = new PathState(startScore, 1, PreviousIndex: -1);

            for (var previousIndex = 0; previousIndex < index; previousIndex++)
            {
                var previousNode = nodes[previousIndex];
                var indexGap = node.Index - previousNode.Index;
                if (indexGap <= 0 || indexGap > MaxQuestionIndexForwardGap)
                {
                    continue;
                }

                var transitionScore = ResolveTransitionScore(lines, previousNode, node, indexGap);
                var candidateScore = states[previousIndex].Score + node.Score + transitionScore;
                var candidateCount = states[previousIndex].Count + 1;
                if (candidateScore > states[index].Score ||
                    (candidateScore == states[index].Score && candidateCount > states[index].Count))
                {
                    states[index] = new PathState(candidateScore, candidateCount, previousIndex);
                }
            }
        }

        var rankedEndpoints = Enumerable.Range(0, nodes.Length)
            .Select(index =>
            {
                var path = RebuildPath(nodes, states, index);
                var score = states[index].Score +
                            ResolvePathEndScore(nodes[index], expectedQuestionRange) -
                            ResolveTrailingResetPenalty(nodes, path);
                return new
                {
                    Index = index,
                    Score = score,
                    states[index].Count,
                    Path = path
                };
            })
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Count)
            .ToArray();
        var bestPath = rankedEndpoints[0].Path;
        if (bestPath.Count == 1 &&
            !bestPath[0].HasExplicitPrefix &&
            !bestPath[0].HasNumberSuffix &&
            (!bestPath[0].HasStrongQuestionEvidence || bestPath[0].MarkerSyntaxRank < 3) &&
            !expectedQuestionRange.HasValue)
        {
            return SelectedPath.Empty;
        }

        var ambiguityReasons = new List<string>();
        var isAmbiguous = false;
        for (var markerIndex = 0; markerIndex < bestPath.Count; markerIndex++)
        {
            var selected = bestPath[markerIndex];
            var lowerBoundary = markerIndex > 0
                ? bestPath[markerIndex - 1].Position
                : int.MinValue;
            var upperBoundary = markerIndex + 1 < bestPath.Count
                ? bestPath[markerIndex + 1].Position
                : int.MaxValue;
            var competitors = nodes
                .Where(x =>
                    x.Index == selected.Index &&
                    x.Position > lowerBoundary &&
                    x.Position < upperBoundary &&
                    x.Position != selected.Position)
                .OrderByDescending(x => x.MarkerSyntaxRank)
                .ThenByDescending(x => x.Score)
                .ToArray();
            if (competitors.Length == 0)
            {
                continue;
            }

            var availableNeighborCount = (markerIndex > 0 ? 1 : 0) +
                                         (markerIndex + 1 < bestPath.Count ? 1 : 0);
            var hasAmbiguousCompetitor = competitors.Any(competing =>
            {
                var competitorMatchesPreviousSyntax = markerIndex > 0 &&
                                                      bestPath[markerIndex - 1].MarkerSyntaxRank ==
                                                      competing.MarkerSyntaxRank;
                var competitorMatchesNextSyntax = markerIndex + 1 < bestPath.Count &&
                                                  bestPath[markerIndex + 1].MarkerSyntaxRank ==
                                                  competing.MarkerSyntaxRank;
                var matchingNeighborCount = (competitorMatchesPreviousSyntax ? 1 : 0) +
                                            (competitorMatchesNextSyntax ? 1 : 0);
                var competitorRestoresNeighborSyntax = competing.MarkerSyntaxRank != selected.MarkerSyntaxRank &&
                                                       availableNeighborCount > 0 &&
                                                       matchingNeighborCount == availableNeighborCount;
                return (!selected.HasReliableGeometry && !competing.HasReliableGeometry) ||
                       competing.MarkerSyntaxRank >= selected.MarkerSyntaxRank ||
                       (selected.HasStrongQuestionEvidence && competing.HasStrongQuestionEvidence) ||
                       competitorRestoresNeighborSyntax;
            });
            if (hasAmbiguousCompetitor)
            {
                isAmbiguous = true;
                ambiguityReasons.Add($"{selected.Index}번 경계 후보가 둘 이상 비슷한 근거로 검출되었습니다.");
            }
        }

        var selectedPositions = bestPath.Select(x => x.Position).ToHashSet();
        var hasUnresolvedSameStyleReset = nodes.Any(x =>
            !selectedPositions.Contains(x.Position) &&
            bestPath.Any(selected =>
                selected.Kind == x.Kind &&
                selected.Position < x.Position &&
                selected.Index == x.Index &&
                x.MarkerSyntaxRank >= selected.MarkerSyntaxRank &&
                x.Score >= selected.Score - 20 &&
                selected.PageIndex == x.PageIndex));
        if (hasUnresolvedSameStyleReset)
        {
            isAmbiguous = true;
            ambiguityReasons.Add("선택된 번호 경로와 경쟁하는 번호 재시작 후보가 남아 있습니다.");
        }

        return new SelectedPath(
            bestPath,
            isAmbiguous,
            string.Join(" ", ambiguityReasons.Distinct()));
    }

    private static int ResolvePathStartScore(
        HeaderHypothesis node,
        QuestionNumberRange? expectedQuestionRange)
    {
        if (expectedQuestionRange.HasValue)
        {
            var distance = Math.Abs(node.Index - expectedQuestionRange.Value.StartIndex);
            return distance == 0 ? 180 : -Math.Min(240, distance * 70);
        }

        return node.Index == 1 ? 90 : -Math.Min(120, Math.Max(0, node.Index - 1) * 5);
    }

    private static int ResolvePathEndScore(
        HeaderHypothesis node,
        QuestionNumberRange? expectedQuestionRange)
    {
        if (!expectedQuestionRange.HasValue)
        {
            return 0;
        }

        var distance = Math.Abs(expectedQuestionRange.Value.EndIndex - node.Index);
        return distance == 0 ? 180 : -Math.Min(240, distance * 30);
    }

    private static int ResolveTransitionScore(
        IReadOnlyList<ParsedLine> lines,
        HeaderHypothesis previous,
        HeaderHypothesis current,
        int indexGap)
    {
        var score = 105 - ((indexGap - 1) * 90);
        score += previous.Kind == current.Kind
            ? 35
            : current.HasStrongQuestionEvidence ? -25 : -240;
        score += Math.Abs(previous.RelativeLeft - current.RelativeLeft) <= HeaderAlignmentTolerance
            ? 30
            : -60;
        score += Math.Min(55, Math.Max(0, current.Position - previous.Position - 1) * 11);
        if (previous.PageIndex == current.PageIndex &&
            previous.LineInPage == current.LineInPage)
        {
            score -= 320;
        }

        if (current.IsLikelyChoice)
        {
            score -= 120;
        }

        return score;
    }

    private static int ResolveTrailingResetPenalty(
        IReadOnlyList<HeaderHypothesis> nodes,
        IReadOnlyList<HeaderHypothesis> path)
    {
        if (path.Count < 2)
        {
            return 0;
        }

        var selectedPositions = path.Select(x => x.Position).ToHashSet();
        var selectedByIndex = path
            .GroupBy(x => x.Index)
            .ToDictionary(x => x.Key, x => x.OrderByDescending(y => y.Score).First());
        var lastIndex = path[^1].Index;
        var penalty = 0;
        foreach (var node in nodes)
        {
            if (selectedPositions.Contains(node.Position) ||
                node.IsLikelyChoice ||
                node.Index > lastIndex ||
                !selectedByIndex.TryGetValue(node.Index, out var selected) ||
                node.Position <= selected.Position ||
                node.Kind != selected.Kind ||
                Math.Abs(node.RelativeLeft - selected.RelativeLeft) > HeaderAlignmentTolerance ||
                node.Score < selected.Score + 35)
            {
                continue;
            }

            penalty += Math.Min(220, 90 + node.Score - selected.Score);
        }

        return Math.Min(440, penalty);
    }

    private static List<HeaderHypothesis> RebuildPath(
        IReadOnlyList<HeaderHypothesis> nodes,
        IReadOnlyList<PathState> states,
        int endpoint)
    {
        var path = new List<HeaderHypothesis>();
        for (var index = endpoint; index >= 0; index = states[index].PreviousIndex)
        {
            path.Add(nodes[index]);
            if (states[index].PreviousIndex < 0)
            {
                break;
            }
        }

        path.Reverse();
        return path;
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
            .GroupBy(x => (x.PageIndex, x.LineInPage))
            .ToDictionary(
                x => x.Key,
                x => x.First().Position);

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
            Inferred: candidate.IsInferred);
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
            $@"^\s*(?:[Qq]\s*)?(?:(?:제|문항|문제)\s*)?{expectedIndex}(?!\d)(?:\s*[.)\]\-:：•·]\s*|번\s*|\s+|$)",
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
                if (gapMarkers.Count != missingCount)
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
            .ToArray();
        if (selectedPositions.Length != missingCount)
        {
            return new List<StartMarker>();
        }

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
        var hasSharedContextMarker = SharedContextMarkerRegex.IsMatch(normalized);
        var hasQuestionCue = QuestionCueRegex.IsMatch(normalized);
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
        if (hangulCount < 4 && !normalized.Contains('?') && !hasSharedContextMarker)
        {
            return false;
        }

        if (!normalized.Contains('?') && !hasSharedContextMarker)
        {
            return false;
        }

        if (!LooksLikeQuestionContinuation(nextLineText) &&
            !normalized.Contains('?') &&
            !hasQuestionCue &&
            !hasSharedContextMarker)
        {
            return false;
        }

        var leftDiff = Math.Abs(line.LeftRatio - anchorLeft);
        score += Math.Max(0, 50 - (int)(leftDiff * 400));
        score += Math.Min(30, hangulCount * 3);
        if (hasQuestionCue)
        {
            score += 25;
        }

        if (hasSharedContextMarker)
        {
            score += 35;
        }

        if (normalized.Contains('?'))
        {
            score += 20;
        }

        if (LooksLikeQuestionContinuation(nextLineText))
        {
            score += 10;
        }

        if (Regex.IsMatch(normalized, @"^[^\d\s]{1,4}[.)\]•·\-~〜]*\s+", RegexOptions.CultureInvariant))
        {
            score += 10;
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

            var verticalGap = line.TopRatio - previousLine.BottomRatio;
            if (verticalGap > 0.01d)
            {
                score += 12;
            }
        }

        return score >= 55;
    }

    private static List<OcrQuestionCandidate> RebuildCandidatesFromMarkers(
        IReadOnlyList<ParsedLine> lines,
        IReadOnlyDictionary<int, int> pageLineCounts,
        IReadOnlyList<StartMarker> markers,
        IReadOnlyDictionary<int, PageLayout>? pageLayouts = null,
        bool hasDocumentAmbiguity = false,
        string ambiguityReason = "")
    {
        var orderedMarkers = markers
            .OrderBy(x => x.Position)
            .ToArray();
        var rebuilt = new List<OcrQuestionCandidate>();
        var effectivePageLayouts = pageLayouts ?? lines
            .Select(x => x.PageIndex)
            .Distinct()
            .ToDictionary(x => x, _ => PageLayout.SingleColumn);
        var sharedContexts = ResolveSharedContextSpans(lines, orderedMarkers);

        for (var index = 0; index < orderedMarkers.Length; index++)
        {
            var marker = orderedMarkers[index];
            var startLine = lines[marker.Position];
            var nextMarkerPosition = index + 1 < orderedMarkers.Length
                ? orderedMarkers[index + 1].Position
                : lines.Count;
            var followingContext = sharedContexts
                .Where(x => x.StartPosition > marker.Position && x.StartPosition < nextMarkerPosition)
                .OrderBy(x => x.StartPosition)
                .FirstOrDefault();
            var semanticEndPosition = followingContext == default
                ? nextMarkerPosition
                : followingContext.StartPosition;
            var endPositionExclusive = Math.Max(marker.Position + 1, semanticEndPosition);
            var bodyLines = lines
                .Skip(marker.Position + 1)
                .Take(Math.Max(0, endPositionExclusive - marker.Position - 1))
                .ToArray();
            var hasInteriorFullWidthRun = startLine.ColumnIndex >= 0 &&
                                          bodyLines.Any(x => x.ColumnIndex < 0);
            var laterMarkerPositions = orderedMarkers
                .Skip(index + 1)
                .Select(x => x.Position)
                .ToHashSet();
            var nextHeaderTopByColumn = orderedMarkers
                .Skip(index + 1)
                .Select(next => new { next.Position, Line = lines[next.Position] })
                .Where(x =>
                    x.Line.PageIndex == startLine.PageIndex &&
                    x.Line.ColumnIndex >= 0)
                .GroupBy(x => x.Line.ColumnIndex)
                .ToDictionary(
                    group => group.Key,
                    group => group.Min(x => x.Line.TopRatio));
            var hasAmbiguousFullWidthContinuation = startLine.ColumnIndex < 0 &&
                effectivePageLayouts.TryGetValue(startLine.PageIndex, out var fullWidthStartLayout) &&
                fullWidthStartLayout.IsTwoColumn &&
                Enumerable.Range(
                        marker.Position + 1,
                        Math.Max(0, lines.Count - marker.Position - 1))
                    .TakeWhile(position => lines[position].PageIndex == startLine.PageIndex)
                    .Any(position =>
                    {
                        var line = lines[position];
                        return line.ColumnIndex >= 0 &&
                               !laterMarkerPositions.Contains(position) &&
                               line.TopRatio >= startLine.BottomRatio - 0.005d &&
                               (!nextHeaderTopByColumn.TryGetValue(line.ColumnIndex, out var nextHeaderTop) ||
                                line.TopRatio < nextHeaderTop - 0.005d);
                    });
            var lastLine = bodyLines.Length > 0
                ? bodyLines[^1]
                : startLine;
            var preview = TrimToLength(
                string.Join(Environment.NewLine, bodyLines.Select(x => x.Text)),
                150);
            var boundaryPosition = semanticEndPosition < lines.Count
                ? semanticEndPosition
                : (int?)null;
            var imageBoundaryPosition = boundaryPosition;
            if (effectivePageLayouts.TryGetValue(startLine.PageIndex, out var startLayout) &&
                startLayout.IsRowMajor &&
                followingContext == default)
            {
                imageBoundaryPosition = orderedMarkers
                    .Skip(index + 1)
                    .Select(next => new { Marker = next, Line = lines[next.Position] })
                    .Where(x =>
                        x.Line.PageIndex == startLine.PageIndex &&
                        x.Line.ColumnIndex == startLine.ColumnIndex)
                    .Select(x => (int?)x.Marker.Position)
                    .FirstOrDefault();
            }
            var imageRegions = BuildQuestionImageRegions(
                lines,
                effectivePageLayouts,
                marker.Position,
                imageBoundaryPosition);
            var sharedContextRegions = sharedContexts
                .Where(x => x.Range.Contains(marker.Index))
                .SelectMany(x => BuildQuestionImageRegions(
                    lines,
                    effectivePageLayouts,
                    x.StartPosition,
                    x.EndPosition,
                    isSharedContext: true))
                .Distinct()
                .ToArray();
            var ownedLinesHaveReliableGeometry = lines
                .Skip(marker.Position)
                .Take(Math.Max(1, endPositionExclusive - marker.Position))
                .All(x => x.HasReliableGeometry);
            var imageLayoutIsReliable = imageRegions
                .Select(x => x.PageIndex)
                .Distinct()
                .All(pageIndex =>
                    !effectivePageLayouts.TryGetValue(pageIndex, out var layout) ||
                    layout.HasReliableStructure);
            var markerHasAmbiguousGeometry = marker.FragmentCount > 1 ||
                                             !ownedLinesHaveReliableGeometry ||
                                             hasInteriorFullWidthRun ||
                                             hasAmbiguousFullWidthContinuation ||
                                             !imageLayoutIsReliable ||
                                             imageRegions.Length == 0 ||
                                             imageRegions.Any(x => !x.HasReliableGeometry) ||
                                             sharedContextRegions.Any(x => !x.HasReliableGeometry);
            var markerAmbiguityReason = marker.FragmentCount > 1
                ? "한 OCR 행에서 여러 헤더가 분리되어 정확한 이미지 좌표를 결정할 수 없습니다."
                : !ownedLinesHaveReliableGeometry ||
                  hasInteriorFullWidthRun ||
                  hasAmbiguousFullWidthContinuation ||
                  !imageLayoutIsReliable ||
                  imageRegions.Length == 0 ||
                  imageRegions.Any(x => !x.HasReliableGeometry) ||
                  sharedContextRegions.Any(x => !x.HasReliableGeometry)
                    ? "문항 경계 좌표가 없거나 서로 겹쳐 안전한 이미지 영역을 결정할 수 없습니다."
                    : string.Empty;

            rebuilt.Add(new OcrQuestionCandidate
            {
                Index = marker.Index,
                Header = marker.Header,
                IsInferred = marker.Inferred,
                LogicalStartOrder = marker.Position,
                LogicalEndOrder = Math.Max(marker.Position, endPositionExclusive - 1),
                StartPage = marker.PageIndex,
                EndPage = lastLine.PageIndex,
                StartLineInPage = marker.LineInPage,
                EndLineInPage = lastLine.LineInPage,
                StartPageLineCount = GetPageLineCount(pageLineCounts, marker.PageIndex),
                EndPageLineCount = GetPageLineCount(pageLineCounts, lastLine.PageIndex),
                ImagePath = marker.ImagePath,
                PreviewText = preview,
                HasAmbiguousBoundary = hasDocumentAmbiguity || markerHasAmbiguousGeometry,
                BoundaryIssue = string.Join(
                    " ",
                    new[] { ambiguityReason, markerAmbiguityReason }
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct()),
                UsesSemanticImageRegions = true,
                ImageRegions = imageRegions,
                SharedContextRegions = sharedContextRegions
            });
        }

        return rebuilt;
    }

    private static ContextSpan[] ResolveSharedContextSpans(
        IReadOnlyList<ParsedLine> lines,
        IReadOnlyList<StartMarker> markers)
    {
        var spans = new List<ContextSpan>();
        var firstMarkerPosition = markers.Count == 0
            ? int.MaxValue
            : markers.Min(x => x.Position);
        for (var position = 0; position < lines.Count; position++)
        {
            var text = lines[position].Text;
            if (string.IsNullOrWhiteSpace(text) ||
                (!text.Contains("다음", StringComparison.Ordinal) &&
                 !text.Contains("보고", StringComparison.Ordinal) &&
                 !text.Contains("답하", StringComparison.Ordinal)))
            {
                continue;
            }

            if (!TryResolveExplicitSharedContextRange(text, out var range))
            {
                if (!LooksLikeStandaloneSharedContext(text))
                {
                    continue;
                }

                // 범위가 없는 도입 문구는 첫 문항 앞에 있을 때만 공통 지문으로 확정한다.
                // 문항 사이의 같은 문구는 앞 문항의 본문일 수 있으므로 이동시키지 않는다.
                if (position >= firstMarkerPosition)
                {
                    continue;
                }

                var firstFollowingMarker = markers
                    .Where(x => x.Position > position)
                    .OrderBy(x => x.Position)
                    .FirstOrDefault();
                if (firstFollowingMarker == default)
                {
                    continue;
                }

                spans.Add(new ContextSpan(
                    new QuestionNumberRange(firstFollowingMarker.Index, firstFollowingMarker.Index),
                    position,
                    firstFollowingMarker.Position));
                continue;
            }
            var firstTarget = markers
                .Where(x => x.Position > position && range.Contains(x.Index))
                .OrderBy(x => x.PageIndex)
                .ThenBy(x =>
                    lines[x.Position].HasReliableGeometry
                        ? lines[x.Position].TopRatio
                        : double.MaxValue)
                .ThenBy(x => x.Position)
                .FirstOrDefault();
            if (firstTarget == default || firstTarget.Position <= position)
            {
                continue;
            }

            spans.Add(new ContextSpan(range, position, firstTarget.Position));
        }

        return spans
            .GroupBy(x => (x.Range, x.StartPosition, x.EndPosition))
            .Select(x => x.First())
            .OrderBy(x => x.StartPosition)
            .ToArray();
    }

    private static bool TryResolveExplicitSharedContextRange(
        string text,
        out QuestionNumberRange range)
    {
        range = default;
        var match = ExplicitSharedContextRangeRegex.Match(text);
        if (!match.Success ||
            !int.TryParse(match.Groups["start"].Value, out var startIndex) ||
            !int.TryParse(match.Groups["end"].Value, out var endIndex) ||
            startIndex <= 0 ||
            endIndex < startIndex ||
            endIndex - startIndex > 50)
        {
            return false;
        }

        var prefix = text.Substring(0, match.Index).Trim();
        if (prefix.Length > 0 &&
            !Regex.IsMatch(
                prefix,
                @"^(?:(?:문항|문제)(?:\s*(?:번호|범위))?\s*[:：]?\s*)?[\[<(（【〔［]*$",
                RegexOptions.CultureInvariant))
        {
            return false;
        }

        var suffix = text.Substring(Math.Min(text.Length, match.Index + match.Length));
        var matchedRange = match.Value.Trim();
        var hasBracketPair = HasExplicitRangeBracketPair(matchedRange);
        var hasPrefixCue = prefix.Contains("문항", StringComparison.Ordinal) ||
                           prefix.Contains("문제", StringComparison.Ordinal);
        var hasSuffixCue = Regex.IsMatch(
            suffix,
            @"^\s*(?:번|문항|문제)(?:\s|[.:：]|$)",
            RegexOptions.CultureInvariant);
        if (!hasBracketPair && !hasPrefixCue && !hasSuffixCue)
        {
            return false;
        }

        if (!suffix.Contains("다음", StringComparison.Ordinal) &&
            !suffix.Contains("보고", StringComparison.Ordinal) &&
            !suffix.Contains("답하", StringComparison.Ordinal))
        {
            return false;
        }

        range = new QuestionNumberRange(startIndex, endIndex);
        return true;
    }

    private static bool HasExplicitRangeBracketPair(string text)
    {
        if (text.Length < 2)
        {
            return false;
        }

        return (text[0], text[^1]) switch
        {
            ('[', ']') or
            ('<', '>') or
            ('(', ')') or
            ('（', '）') or
            ('【', '】') or
            ('〔', '〕') or
            ('［', '］') => true,
            _ => false
        };
    }

    private static bool LooksLikeStandaloneSharedContext(string text)
    {
        var hasContextNoun = text.Contains("다음 글", StringComparison.Ordinal) ||
                             text.Contains("다음 자료", StringComparison.Ordinal) ||
                             text.Contains("다음 보기", StringComparison.Ordinal) ||
                             text.Contains("공통 지문", StringComparison.Ordinal) ||
                             text.Contains("다음 지문", StringComparison.Ordinal);
        var hasContextAction = text.Contains("읽", StringComparison.Ordinal) ||
                               text.Contains("보고", StringComparison.Ordinal) ||
                               text.Contains("답하", StringComparison.Ordinal);
        return hasContextNoun && hasContextAction;
    }

    private static OcrQuestionImageRegion[] BuildQuestionImageRegions(
        IReadOnlyList<ParsedLine> lines,
        IReadOnlyDictionary<int, PageLayout> pageLayouts,
        int startPosition,
        int? boundaryPosition,
        bool isSharedContext = false)
    {
        if (startPosition < 0 || startPosition >= lines.Count)
        {
            return Array.Empty<OcrQuestionImageRegion>();
        }

        var startLine = lines[startPosition];
        var boundaryLine = boundaryPosition.HasValue &&
                           boundaryPosition.Value >= 0 &&
                           boundaryPosition.Value < lines.Count
            ? lines[boundaryPosition.Value]
            : (ParsedLine?)null;
        var pageNumbers = pageLayouts.Keys
            .Where(page => page >= startLine.PageIndex &&
                           (!boundaryLine.HasValue || page <= boundaryLine.Value.PageIndex))
            .OrderBy(page => page)
            .ToArray();
        if (pageNumbers.Length == 0)
        {
            pageNumbers = new[] { startLine.PageIndex };
        }

        var startBoundary = ResolveBoundaryTop(lines, startPosition);
        var endBoundary = boundaryLine.HasValue
            ? ResolveBoundaryTop(lines, boundaryPosition!.Value)
            : new BoundaryTop(1d, true);
        var intervalEndPosition = boundaryPosition ?? lines.Count;
        var intervalHasReliableGeometry = lines
            .Skip(startPosition)
            .Take(Math.Max(1, intervalEndPosition - startPosition))
            .All(x => x.HasReliableGeometry);
        var regions = new List<OcrQuestionImageRegion>();
        var startPageLayout = pageLayouts.TryGetValue(startLine.PageIndex, out var resolvedStartLayout)
            ? resolvedStartLayout
            : PageLayout.SingleColumn;
        var startPageUsesRowMajorOrder = startPageLayout.IsRowMajor && startLine.ColumnIndex >= 0;
        var startsWithFullWidthLine = IsFullWidthLine(startLine, startPageLayout);
        var fullWidthBlockBottom = startBoundary.Top;
        if (startsWithFullWidthLine && !isSharedContext)
        {
            var blockEnd = boundaryPosition ?? lines.Count;
            var fullWidthBlock = lines
                .Skip(startPosition)
                .Take(Math.Max(0, blockEnd - startPosition))
                .TakeWhile(x => x.PageIndex == startLine.PageIndex && x.ColumnIndex < 0)
                .ToArray();
            var fullWidthContentBottom = fullWidthBlock.Length == 0
                ? startLine.BottomRatio
                : fullWidthBlock.Max(x => x.BottomRatio);
            var nextRegularTop = lines
                .Skip(startPosition + Math.Max(1, fullWidthBlock.Length))
                .Where(x =>
                    x.PageIndex == startLine.PageIndex &&
                    x.ColumnIndex >= 0 &&
                    x.TopRatio >= fullWidthContentBottom - 0.005d)
                .Select(x => (double?)x.TopRatio)
                .OrderBy(x => x)
                .FirstOrDefault();
            var fullWidthPadding = nextRegularTop.HasValue
                ? Math.Min(
                    QuestionRegionPaddingRatio,
                    Math.Max(0d, (nextRegularTop.Value - fullWidthContentBottom) / 2d))
                : QuestionRegionPaddingRatio;
            fullWidthBlockBottom = Math.Clamp(
                fullWidthContentBottom + fullWidthPadding,
                startBoundary.Top,
                boundaryLine.HasValue && boundaryLine.Value.PageIndex == startLine.PageIndex
                    ? endBoundary.Top
                    : 1d);
            if (fullWidthBlockBottom > startBoundary.Top)
            {
                regions.Add(new OcrQuestionImageRegion
                {
                    PageIndex = startLine.PageIndex,
                    ColumnIndex = -1,
                    LeftRatio = 0d,
                    TopRatio = startBoundary.Top,
                    RightRatio = 1d,
                    BottomRatio = fullWidthBlockBottom,
                    HasReliableGeometry = startBoundary.IsReliable &&
                                          fullWidthBlock.All(x => x.HasReliableGeometry)
                });
            }
        }

        foreach (var pageNumber in pageNumbers)
        {
            var layout = pageLayouts.TryGetValue(pageNumber, out var resolvedLayout)
                ? resolvedLayout
                : PageLayout.SingleColumn;
            if (startsWithFullWidthLine && isSharedContext)
            {
                var fullTop = pageNumber == startLine.PageIndex ? startBoundary.Top : 0d;
                var fullBottom = boundaryLine.HasValue && pageNumber == boundaryLine.Value.PageIndex
                    ? endBoundary.Top
                    : 1d;
                var fullReliable = intervalHasReliableGeometry &&
                                   startBoundary.IsReliable &&
                                   (!boundaryLine.HasValue || endBoundary.IsReliable) &&
                                   double.IsFinite(fullTop) &&
                                   double.IsFinite(fullBottom) &&
                                   fullBottom > fullTop;
                if (fullBottom <= fullTop)
                {
                    fullBottom = Math.Min(1d, fullTop + 0.001d);
                }

                regions.Add(new OcrQuestionImageRegion
                {
                    PageIndex = pageNumber,
                    ColumnIndex = -1,
                    LeftRatio = 0d,
                    TopRatio = Math.Clamp(fullTop, 0d, 1d),
                    RightRatio = 1d,
                    BottomRatio = Math.Clamp(fullBottom, 0d, 1d),
                    HasReliableGeometry = fullReliable
                });
                continue;
            }

            var startColumn = pageNumber == startLine.PageIndex
                ? startsWithFullWidthLine
                    ? 0
                    : Math.Clamp(startLine.ColumnIndex, 0, layout.IsTwoColumn ? 1 : 0)
                : 0;
            var endColumn = boundaryLine.HasValue && pageNumber == boundaryLine.Value.PageIndex
                ? boundaryLine.Value.ColumnIndex < 0 || startPageUsesRowMajorOrder
                    ? startColumn
                    : Math.Clamp(boundaryLine.Value.ColumnIndex, 0, layout.IsTwoColumn ? 1 : 0)
                : layout.IsTwoColumn ? 1 : 0;
            if (startPageUsesRowMajorOrder && pageNumber == startLine.PageIndex)
            {
                endColumn = startColumn;
            }

            for (var column = startColumn; column <= endColumn; column++)
            {
                var (left, right) = layout.ResolveColumnBand(column);
                var top = pageNumber == startLine.PageIndex && column == startColumn
                    ? startsWithFullWidthLine ? fullWidthBlockBottom : startBoundary.Top
                    : 0d;
                var bottom = boundaryLine.HasValue &&
                             pageNumber == boundaryLine.Value.PageIndex &&
                             column == endColumn
                    ? endBoundary.Top
                    : 1d;
                var reliable = intervalHasReliableGeometry &&
                               startBoundary.IsReliable &&
                               (!boundaryLine.HasValue || endBoundary.IsReliable);
                if (!double.IsFinite(top) || !double.IsFinite(bottom) || bottom <= top)
                {
                    if (startsWithFullWidthLine && top >= bottom)
                    {
                        continue;
                    }

                    reliable = false;
                    top = double.IsFinite(top) ? Math.Clamp(top, 0d, 1d) : 0d;
                    bottom = Math.Min(1d, top + 0.001d);
                }

                regions.Add(new OcrQuestionImageRegion
                {
                    PageIndex = pageNumber,
                    ColumnIndex = column,
                    LeftRatio = Math.Clamp(left, 0d, 1d),
                    TopRatio = Math.Clamp(top, 0d, 1d),
                    RightRatio = Math.Clamp(right, 0d, 1d),
                    BottomRatio = Math.Clamp(bottom, 0d, 1d),
                    HasReliableGeometry = reliable
                });
            }
        }

        return regions
            .Where(x => x.RightRatio > x.LeftRatio && x.BottomRatio > x.TopRatio)
            .GroupBy(x => $"{x.PageIndex}:{x.ColumnIndex}:{x.LeftRatio:F6}:{x.TopRatio:F6}:{x.RightRatio:F6}:{x.BottomRatio:F6}")
            .Select(x => x.First())
            .ToArray();
    }

    private static bool IsFullWidthLine(ParsedLine line, PageLayout layout)
    {
        return IsFullWidthGeometry(
            line.LeftRatio,
            line.RightRatio,
            line.HasReliableGeometry,
            layout);
    }

    private static BoundaryTop ResolveBoundaryTop(
        IReadOnlyList<ParsedLine> lines,
        int position)
    {
        if (position < 0 || position >= lines.Count)
        {
            return new BoundaryTop(0d, false);
        }

        var line = lines[position];
        var padding = QuestionRegionPaddingRatio;
        var previous = lines
            .Take(position)
            .Where(candidate =>
                candidate.PageIndex == line.PageIndex &&
                (candidate.ColumnIndex == line.ColumnIndex || candidate.ColumnIndex < 0) &&
                candidate.TopRatio <= line.TopRatio)
            .OrderByDescending(candidate => candidate.BottomRatio)
            .Select(candidate => (ParsedLine?)candidate)
            .FirstOrDefault();
        if (previous.HasValue &&
            previous.Value.PageIndex == line.PageIndex &&
            (previous.Value.ColumnIndex == line.ColumnIndex || previous.Value.ColumnIndex < 0) &&
            previous.Value.HasReliableGeometry &&
            line.HasReliableGeometry)
        {
            var gap = line.TopRatio - previous.Value.BottomRatio;
            if (gap < -0.005d)
            {
                return new BoundaryTop(Math.Clamp(line.TopRatio, 0d, 1d), false);
            }

            padding = Math.Min(QuestionRegionPaddingRatio, Math.Max(0d, gap / 2d));
        }

        var top = Math.Clamp(line.TopRatio - padding, 0d, 1d);
        return new BoundaryTop(top, line.HasReliableGeometry);
    }

    private static IReadOnlyList<OcrQuestionCandidate> SplitByPermissiveHeader(
        IReadOnlyList<ParsedLine> lines,
        IReadOnlyDictionary<int, int> pageLineCounts,
        QuestionNumberRange? expectedQuestionRange,
        out int headerRegexMatchCount,
        out int normalizedIndexMatchCount,
        out int duplicateIndexCount,
        out int sequenceResetCount)
    {
        headerRegexMatchCount = 0;
        normalizedIndexMatchCount = 0;
        duplicateIndexCount = 0;
        sequenceResetCount = 0;

        var candidates = new List<OcrQuestionCandidate>();
        OcrQuestionCandidate? current = null;
        var currentBuffer = new StringBuilder();
        var seenIndex = new HashSet<int>();

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var match = PermissiveHeaderRegex.Match(line.Text);
            if (match.Success)
            {
                headerRegexMatchCount++;
            }

            if (TryMatchQuestionHeader(lines, i, PermissiveHeaderRegex, expectedQuestionRange, out var index))
            {
                var isSequenceReset = IsConfirmedQuestionSequenceReset(
                    lines,
                    i,
                    PermissiveHeaderRegex,
                    expectedQuestionRange,
                    current,
                    index);
                if (isSequenceReset || ShouldStartNewCandidate(current, index))
                {
                    normalizedIndexMatchCount++;
                    if (isSequenceReset)
                    {
                        sequenceResetCount++;
                        duplicateIndexCount++;
                        seenIndex.Clear();
                    }

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
                            IsInferred = false,
                            LogicalStartOrder = i,
                            LogicalEndOrder = i,
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
                    LogicalEndOrder = i,
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

    private static bool IsConfirmedQuestionSequenceReset(
        IReadOnlyList<ParsedLine> lines,
        int lineIndex,
        Regex headerRegex,
        QuestionNumberRange? expectedQuestionRange,
        OcrQuestionCandidate? current,
        int candidateIndex)
    {
        var resetStartIndex = expectedQuestionRange?.StartIndex ?? 1;
        if (current is null ||
            candidateIndex != resetStartIndex ||
            lineIndex < 0 ||
            lineIndex >= lines.Count)
        {
            return false;
        }

        var minimumPreviousIndex = MinQuestionIndexBeforeSequenceReset;
        var requiredConfirmations = SequenceResetConfirmationCount;
        if (expectedQuestionRange.HasValue)
        {
            minimumPreviousIndex = Math.Max(
                resetStartIndex + 1,
                expectedQuestionRange.Value.EndIndex - 2);
            requiredConfirmations = Math.Min(
                SequenceResetConfirmationCount,
                Math.Max(1, expectedQuestionRange.Value.EndIndex - resetStartIndex));
        }

        if (current.Index < minimumPreviousIndex)
        {
            return false;
        }

        var expectedNextIndex = candidateIndex + 1;
        var confirmationCount = 0;
        for (var position = lineIndex + 1; position < lines.Count; position++)
        {
            if (!TryMatchQuestionHeader(
                    lines,
                    position,
                    headerRegex,
                    expectedQuestionRange,
                    out var nextIndex))
            {
                continue;
            }

            if (nextIndex != expectedNextIndex)
            {
                return false;
            }

            confirmationCount++;
            if (confirmationCount >= requiredConfirmations)
            {
                return true;
            }

            expectedNextIndex++;
        }

        return false;
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

        return index > current.Index &&
               index - current.Index <= MaxQuestionIndexForwardGap;
    }

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

    private static TLine[] TrimLeadingBoilerplateLines<TLine>(
        IReadOnlyList<TLine> sourceLines,
        Func<TLine, string> textSelector,
        QuestionNumberRange? expectedQuestionRange,
        out bool questionSequenceStarted)
    {
        questionSequenceStarted = false;
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
                questionSequenceStarted = true;
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
                   out _) ||
               TryMatchQuestionHeaderText(
                   text,
                   nextText,
                   PermissiveHeaderRegex,
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
            Regex.IsMatch(normalized, @"^\s*[<(（【〔［]?\s*\d{1,3}\s*[-~〜]\s*\d{1,3}", RegexOptions.CultureInvariant))
        {
            return true;
        }

        return BlockingBoilerplatePhrases.Any(phrase =>
            normalized.Contains(phrase, StringComparison.OrdinalIgnoreCase));
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
            return suffix.Contains('?') ||
                   QuestionCueRegex.IsMatch(suffix) ||
                   CountHangulCharacters(suffix) >= 8 ||
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

        raw = string.Concat(raw.Trim().Select(character =>
            character is >= '０' and <= '９'
                ? (char)('0' + character - '０')
                : character));
        if (int.TryParse(raw, out index))
        {
            return index > 0;
        }

        const string circledQuestionIndexes = "①②③④⑤⑥⑦⑧⑨⑩⑪⑫⑬⑭⑮⑯⑰⑱⑲⑳";
        var pos = circledQuestionIndexes.IndexOf(raw, StringComparison.Ordinal);
        if (pos >= 0)
        {
            index = pos + 1;
            return true;
        }

        if (raw.Length == 1)
        {
            var c = raw[0];
            pos = HangulQuestionIndexCharacters.IndexOf(c);
            if (pos >= 0)
            {
                index = pos + 1;
                return true;
            }

            if (char.IsAsciiLetter(c))
            {
                index = char.ToUpperInvariant(c) - 'A' + 1;
                return index is >= 1 and <= 26;
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
        double BottomRatio = 1d,
        int FragmentIndex = 0,
        int FragmentCount = 1,
        int ColumnIndex = 0,
        double ColumnLeftRatio = 0d,
        double ColumnRightRatio = 1d,
        bool HasReliableGeometry = false);

    private readonly record struct NormalizedOcrDocument(
        List<ParsedLine> Lines,
        Dictionary<int, int> PageLineCounts,
        Dictionary<int, PageLayout> PageLayouts,
        int SyntheticBreakCount);

    private readonly record struct StartMarker(
        int Index,
        int Position,
        int PageIndex,
        int LineInPage,
        string Header,
        string? ImagePath,
        bool Inferred,
        int FragmentIndex = 0,
        int FragmentCount = 1);

    private readonly record struct PageLayout(
        bool IsTwoColumn,
        double GutterRatio,
        bool IsRowMajor = false,
        bool HasReliableStructure = true)
    {
        public static PageLayout SingleColumn { get; } = new(false, 1d);
        public static PageLayout AmbiguousSingleColumn { get; } = new(
            false,
            1d,
            HasReliableStructure: false);

        public int ResolveColumnIndex(double left, double right)
        {
            if (!IsTwoColumn)
            {
                return 0;
            }

            return GetHorizontalMidpoint(left, right) < GutterRatio ? 0 : 1;
        }

        public (double Left, double Right) ResolveColumnBand(int columnIndex)
        {
            if (!IsTwoColumn || columnIndex < 0)
            {
                return (0d, 1d);
            }

            return columnIndex <= 0
                ? (0d, GutterRatio)
                : (GutterRatio, 1d);
        }
    }

    private enum HeaderKind
    {
        Numeric,
        Circled,
        Hangul,
        Latin
    }

    private readonly record struct HeaderHypothesis(
        int Index,
        int Position,
        int PageIndex,
        int LineInPage,
        int FragmentIndex,
        int FragmentCount,
        string Header,
        HeaderKind Kind,
        int Score,
        double RelativeLeft,
        bool HasReliableGeometry,
        bool HasExplicitPrefix,
        bool HasNumberSuffix,
        bool HasDelimiter,
        bool IsBare,
        bool IsLikelyChoice,
        bool HasStrongQuestionEvidence,
        int MarkerSyntaxRank);

    private readonly record struct PathState(int Score, int Count, int PreviousIndex);

    private readonly record struct ContextSpan(
        QuestionNumberRange Range,
        int StartPosition,
        int EndPosition);

    private readonly record struct BoundaryTop(double Top, bool IsReliable);

    private readonly record struct SelectedPath(
        IReadOnlyList<HeaderHypothesis> Markers,
        bool IsAmbiguous,
        string AmbiguityReason)
    {
        public static SelectedPath Empty { get; } =
            new(Array.Empty<HeaderHypothesis>(), false, string.Empty);
    }

    private readonly record struct HeaderPathSelection(
        IReadOnlyList<StartMarker> Markers,
        int ResetCount,
        bool HasDocumentAmbiguity,
        string AmbiguityReason)
    {
        public static HeaderPathSelection Empty { get; } =
            new(Array.Empty<StartMarker>(), 0, false, string.Empty);
    }
}
