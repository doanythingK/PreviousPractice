using PreviousPractice.Models;

namespace PreviousPractice.Infrastructure;

public sealed record AnswerMapParseResult(IEnumerable<Question> Questions, IReadOnlyList<string> Errors, string Message)
{
    public bool HasErrors => Errors.Count > 0;
    public bool IsEmpty => !Questions.Any();
}

public static class QuestionSetParser
{
    public static AnswerMapParseResult ParseAnswerMapWithDetails(string answerMapText, QuestionType defaultType = QuestionType.MultipleChoice)
    {
        var parsedByIndex = new Dictionary<int, Question>();
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(answerMapText))
        {
            return new AnswerMapParseResult(Array.Empty<Question>(), Array.Empty<string>(), "정답 텍스트가 비어 있습니다.");
        }

        var pairs = answerMapText
            .Replace("\r", string.Empty)
            .Split(new[] { ',', '\n', ';' }, StringSplitOptions.TrimEntries);

        var hasExplicitMapping = pairs.Any(x => x.Contains(':', StringComparison.Ordinal));
        if (hasExplicitMapping)
        {
            ParseAsExplicitMapping(pairs, defaultType, parsedByIndex, errors);
        }
        else
        {
            ParseAsAnswerList(pairs, defaultType, parsedByIndex);
        }

        if (parsedByIndex.Count == 0)
        {
            return new AnswerMapParseResult(Array.Empty<Question>(), errors, "유효한 정답 데이터가 없습니다.");
        }

        var ordered = parsedByIndex.Values
            .OrderBy(x => x.Index)
            .ToList();

        return new AnswerMapParseResult(ordered, errors, string.Empty);
    }

    private static void ParseAsExplicitMapping(
        IEnumerable<string> pairs,
        QuestionType defaultType,
        Dictionary<int, Question> parsedByIndex,
        IList<string> errors)
    {
        foreach (var pair in pairs.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            var splitted = pair.Split(':', 2);
            if (splitted.Length != 2)
            {
                errors.Add($"형식 오류: {pair}");
                continue;
            }

            if (!int.TryParse(splitted[0].Trim(), out var idx) || idx <= 0)
            {
                errors.Add($"문항 번호 오류: {splitted[0]}");
                continue;
            }

            var answers = splitted[1]
                .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => x.Trim())
                .ToArray();

            if (answers.Length == 0)
            {
                errors.Add($"정답 없음: {idx}");
            }

            var question = new Question
            {
                Index = idx,
                Type = defaultType,
                CorrectAnswers = answers,
                Prompt = $"문항 {idx}",
                SourceFileName = string.Empty
            };

            if (parsedByIndex.ContainsKey(idx))
            {
                errors.Add($"문항 중복: {idx} (최근 값으로 반영)");
            }

            parsedByIndex[idx] = question;
        }
    }

    private static void ParseAsAnswerList(
        IEnumerable<string> answers,
        QuestionType defaultType,
        Dictionary<int, Question> parsedByIndex)
    {
        var index = 0;
        foreach (var rawAnswer in answers)
        {
            index++;

            var token = rawAnswer?.Trim() ?? string.Empty;
            var splitAnswers = token
                .Split('|', StringSplitOptions.TrimEntries)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .ToArray();

            var question = new Question
            {
                Index = index,
                Type = defaultType,
                CorrectAnswers = splitAnswers,
                Prompt = $"문항 {index}",
                SourceFileName = string.Empty
            };

            parsedByIndex[index] = question;

        }
    }

    public static (Question[] Questions, string Message) ParseAnswerMap(string answerMapText, QuestionType defaultType = QuestionType.MultipleChoice)
    {
        var result = ParseAnswerMapWithDetails(answerMapText, defaultType);
        return (result.Questions.ToArray(), result.Message);
    }
}
