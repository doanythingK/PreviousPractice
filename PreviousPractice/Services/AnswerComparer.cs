using PreviousPractice.Models;

namespace PreviousPractice.Services;

public static class AnswerComparer
{
    public static bool IsCorrect(Question question, string rawInput)
    {
        if (question == null)
        {
            return false;
        }

        var normalizedInput = rawInput?.Trim() ?? string.Empty;
        if (question.Type == QuestionType.MultipleChoice)
        {
            if (!int.TryParse(normalizedInput, out var answerNumber))
            {
                return false;
            }

            return question.CorrectAnswers
                .Select(ParseInt)
                .Where(x => x.HasValue)
                .Any(x => x == answerNumber);
        }

        var normalizedCandidate = NormalizeSubjective(rawInput);
        return question.CorrectAnswers.Any(x => NormalizeSubjective(x) == normalizedCandidate);
    }

    private static string NormalizeSubjective(string value)
    {
        return (value ?? string.Empty)
            .Trim()
            .ToUpperInvariant();
    }

    private static int? ParseInt(string raw)
    {
        if (int.TryParse(raw?.Trim(), out var value))
        {
            return value;
        }

        return null;
    }
}
