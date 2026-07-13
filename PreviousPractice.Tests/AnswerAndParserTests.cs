using PreviousPractice.Infrastructure;
using PreviousPractice.Models;
using PreviousPractice.Services;

namespace PreviousPractice.Tests;

public sealed class AnswerAndParserTests
{
    [Fact]
    public void AnswerList_PreservesBlankPositions()
    {
        var result = QuestionSetParser.ParseAnswerMapWithDetails("1,,3");

        var questions = result.Questions.OrderBy(question => question.Index).ToArray();
        Assert.Equal(3, questions.Length);
        Assert.Equal(new[] { "1" }, questions[0].CorrectAnswers);
        Assert.Empty(questions[1].CorrectAnswers);
        Assert.Equal(new[] { "3" }, questions[2].CorrectAnswers);
    }

    [Fact]
    public void SubjectiveAnswer_IgnoresAllWhitespaceAndCase()
    {
        var question = new Question
        {
            Type = QuestionType.Subjective,
            CorrectAnswers = ["Data Base"]
        };

        Assert.True(AnswerComparer.IsCorrect(question, " d a t a\tbase "));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1.0")]
    public void MultipleChoiceAnswer_RejectsNonIntegerInput(string input)
    {
        var question = new Question
        {
            Type = QuestionType.MultipleChoice,
            CorrectAnswers = ["1"]
        };

        Assert.False(AnswerComparer.IsCorrect(question, input));
    }
}
