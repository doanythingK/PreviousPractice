using System.Collections.ObjectModel;
using PastQuestionPractice.Core;
using PastQuestionPractice.Models;
using PastQuestionPractice.Services;

namespace PastQuestionPractice.ViewModels;

public class MainViewModel : ViewModelBase
{
    private string categoryName = string.Empty;
    private string answerMapText = string.Empty;
    private string feedback = "";

    public ObservableCollection<string> Categories { get; } = new()
    {
        "국어",
        "영어"
    };

    public string CategoryName
    {
        get => categoryName;
        set => SetProperty(ref categoryName, value);
    }

    public string AnswerMapText
    {
        get => answerMapText;
        set => SetProperty(ref answerMapText, value);
    }

    public string Feedback
    {
        get => feedback;
        set => SetProperty(ref feedback, value);
    }

    public RelayCommand ParseAnswerCommand { get; }

    public MainViewModel()
    {
        ParseAnswerCommand = new RelayCommand(ParseAnswer);
    }

    private void ParseAnswer()
    {
        var (_, message) = QuestionSetParser.ParseAnswerMap(AnswerMapText);
        Feedback = string.IsNullOrEmpty(message) ? "정답 맵 파싱이 완료되었습니다." : message;
    }

    public bool CheckSampleQuestion()
    {
        var q = new Question
        {
            Type = QuestionType.MultipleChoice,
            CorrectAnswers = new[] { "2", "4", "5" }
        };

        return AnswerComparer.IsCorrect(q, "4");
    }
}
