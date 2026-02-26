using PastQuestionPractice.Models;

namespace PastQuestionPractice.Infrastructure;

public static class QuestionSetParser
{
    public static (Question[] Questions, string Message) ParseAnswerMap(string answerMapText)
    {
        var list = new List<Question>();

        if (string.IsNullOrWhiteSpace(answerMapText))
        {
            return (list.ToArray(), "정답 텍스트가 비어 있습니다.");
        }

        // 입력 형식: "1:3|4,2:정답,3:5"
        var pairs = answerMapText
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var pair in pairs)
        {
            var splitted = pair.Split(':', 2);
            if (splitted.Length != 2)
            {
                continue;
            }

            if (!int.TryParse(splitted[0].Trim(), out var idx))
            {
                continue;
            }

            var answers = splitted[1]
                .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToArray();

            list.Add(new Question
            {
                Index = idx,
                CorrectAnswers = answers
            });
        }

        var message = list.Count == 0 ? "유효한 정답 데이터가 없습니다." : string.Empty;
        return (list.ToArray(), message);
    }
}
