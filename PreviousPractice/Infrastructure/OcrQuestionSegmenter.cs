using System.Text;
using System.Text.RegularExpressions;
using PreviousPractice.Models;

namespace PreviousPractice.Infrastructure;

public static class OcrQuestionSegmenter
{
    private const int FallbackLinesPerQuestion = 10;
    private static readonly Regex SingleLineHeaderRegex = new(
        @"^\s*(?:[Qq]\s*)?(?:(?:제|문항|문제)\s*)?(?<index>\d{1,3}|[①②③④⑤⑥⑦⑧⑨⑩⑪⑫⑬⑭⑮⑯⑰⑱⑲⑳]|[가-하]|[A-Za-z])\s*(?:[.)\]\-:：]|\s|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<OcrQuestionCandidate> SplitByHeader(IReadOnlyList<OcrPageResult> pages)
    {
        if (pages.Count == 0)
        {
            return Array.Empty<OcrQuestionCandidate>();
        }

        var lines = new List<(int pageIndex, string line)>();
        for (var pageIndex = 0; pageIndex < pages.Count; pageIndex++)
        {
            var pageText = pages[pageIndex].Text;
            foreach (var rawLine in pageText.Split('\n'))
            {
                var normalized = rawLine.Replace('\r', ' ').Trim();
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    lines.Add((pageIndex + 1, normalized));
                }
            }
        }

        if (lines.Count == 0)
        {
            return Array.Empty<OcrQuestionCandidate>();
        }

        var candidates = new List<OcrQuestionCandidate>();
        OcrQuestionCandidate? current = null;
        var currentBuffer = new StringBuilder();
        var seenIndex = new HashSet<int>();

        for (var i = 0; i < lines.Count; i++)
        {
            var (pageIndex, line) = lines[i];

            var match = SingleLineHeaderRegex.Match(line);
            if (match.Success && TryNormalizeQuestionIndex(match.Groups["index"].Value, out var index))
            {
                if (current is not null)
                {
                    candidates.Add(FinalizeCurrent(current, currentBuffer, i == lines.Count - 1));
                }

                if (seenIndex.Add(index))
                {
                    current = new OcrQuestionCandidate
                    {
                        Index = index,
                        Header = line,
                        StartPage = pageIndex
                    };
                    currentBuffer.Clear();
                }
                else
                {
                    // 같은 번호가 반복으로 들어오면 새 질문으로 간주하지 않는다.
                    current = null;
                    currentBuffer.Clear();
                }

                continue;
            }

            if (current != null)
            {
                currentBuffer.AppendLine(line);
            }
        }

        if (current is not null)
        {
            candidates.Add(FinalizeCurrent(current, currentBuffer, true));
        }

        if (candidates.Count == 0)
        {
            return SplitByHeuristic(lines);
        }

        for (var i = 0; i < candidates.Count; i++)
        {
            if (i == candidates.Count - 1)
            {
                candidates[i] = candidates[i] with { EndPage = pages.Count };
                continue;
            }

            candidates[i] = candidates[i] with { EndPage = candidates[i + 1].StartPage };
        }

        return candidates;
    }

    private static IReadOnlyList<OcrQuestionCandidate> SplitByHeuristic(
        IReadOnlyList<(int pageIndex, string line)> lines)
    {
        if (lines.Count == 0)
        {
            return Array.Empty<OcrQuestionCandidate>();
        }

        var candidates = new List<OcrQuestionCandidate>();
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

            var preview = TrimToLength(string.Join(" ", chunk.Select(x => x.line)), 150);
            candidates.Add(new OcrQuestionCandidate
            {
                Index = index,
                Header = $"[추정] 문항 {index}",
                StartPage = chunk[0].pageIndex,
                EndPage = chunk[^1].pageIndex,
                PreviewText = preview
            });

            index++;
        }

        return candidates;
    }

    private static OcrQuestionCandidate FinalizeCurrent(
        OcrQuestionCandidate current,
        StringBuilder buffer,
        bool isLast)
    {
        var preview = (buffer.ToString() ?? string.Empty).Trim();
        return current with
        {
            EndPage = isLast ? current.StartPage : current.EndPage,
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
}
