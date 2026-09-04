using System.Collections.ObjectModel;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PreviousPractice.Core;
using PreviousPractice.Data;
using PreviousPractice.Infrastructure;
using PreviousPractice.Models;
using PreviousPractice.Services;
using Microsoft.Maui.Storage;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Devices;

namespace PreviousPractice.ViewModels;

public partial class MainViewModel
{
    private async Task StartPracticeAsync()
    {
        if (IsPracticeStartInProgress || IsPracticeRunning)
        {
            return;
        }

        if (IsPdfAnalysisInProgress)
        {
            Feedback = "문항 분석이 끝난 뒤 연습을 시작해 주세요.";
            return;
        }

        if (IsSourceFileImportInProgress)
        {
            Feedback = "문항 PDF 등록이 끝난 뒤 연습을 시작해 주세요.";
            return;
        }

        var targetCategory = SelectedCategory;
        if (targetCategory == null)
        {
            Feedback = "카테고리를 선택해 주세요.";
            return;
        }

        if (!int.TryParse(PracticeCountText, out var count) || count <= 0)
        {
            Feedback = "문항 수는 1 이상이어야 합니다.";
            return;
        }

        if (count > MaxPracticeCount)
        {
            Feedback = $"연습 가능한 문항은 최대 {MaxPracticeCount}개입니다.";
            return;
        }

        var includeUnanswered = IncludeUnansweredInPractice;
        IsPracticeStartInProgress = true;
        try
        {
            var questions = await repository.GetQuestionsAsync(targetCategory.Id);
            var practiceCandidates = (includeUnanswered
                    ? questions
                    : questions.Where(x => x.CorrectAnswers?.Any(a => !string.IsNullOrWhiteSpace(a)) == true))
                .OrderBy(_ => random.Next())
                .Take(count)
                .ToList();

            if (practiceCandidates.Count == 0)
            {
                Feedback = includeUnanswered
                    ? "출제 가능한 문항이 없습니다."
                    : "정답이 등록된 문항이 없습니다.";
                return;
            }

            await StartWithQuestionsAsync(practiceCandidates);
        }
        finally
        {
            IsPracticeStartInProgress = false;
        }
    }

    private async Task StartWrongPracticeAsync()
    {
        if (IsPracticeStartInProgress || IsPracticeRunning)
        {
            return;
        }

        if (IsPdfAnalysisInProgress)
        {
            Feedback = "문항 분석이 끝난 뒤 오답 연습을 시작해 주세요.";
            return;
        }

        if (IsSourceFileImportInProgress)
        {
            Feedback = "문항 PDF 등록이 끝난 뒤 오답 연습을 시작해 주세요.";
            return;
        }

        IsPracticeStartInProgress = true;
        try
        {
            var wrong = await repository.GetWrongQuestionsAsync();
            if (wrong.Count == 0)
            {
                Feedback = "오답 문제가 없습니다.";
                return;
            }

            PracticeCountText = wrong.Count.ToString();
            await StartWithQuestionsAsync(wrong);
        }
        finally
        {
            IsPracticeStartInProgress = false;
        }
    }

    private async Task StopPracticeAsync()
    {
        if (!CanStopPractice)
        {
            return;
        }

        var confirmed = await ConfirmDestructiveActionAsync(
            "연습 종료",
            "현재 연습을 종료하시겠습니까? 이미 채점한 오답 기록은 유지됩니다.",
            "연습 종료");
        if (!confirmed)
        {
            return;
        }

        if (!CanStopPractice)
        {
            return;
        }

        const string message = "현재 연습을 종료했습니다. 이미 채점한 오답 기록은 유지됩니다.";
        currentSession = Array.Empty<Question>();
        IsPracticeRunning = false;
        IsAnswerRevealed = false;
        CurrentQuestion = null;
        CurrentQuestionImageSlices.Clear();
        QuestionImageNotice = string.Empty;
        ResetQuestionImageView();
        UserAnswer = string.Empty;
        SessionFeedback = message;
        Feedback = message;
        OnPropertyChanged(nameof(CurrentQuestionSourceDisplay));
        OnPropertyChanged(nameof(CurrentQuestionText));
        OnPropertyChanged(nameof(CurrentQuestionChoicesText));
        OnPropertyChanged(nameof(CurrentQuestionSemanticDescription));
        OnPropertyChanged(nameof(HasCurrentQuestionImages));
        OnPropertyChanged(nameof(ShowCurrentQuestionText));
        UpdatePracticeState();
    }

    private async Task SubmitAnswerAsync()
    {
        if (CurrentQuestion == null ||
            !IsPracticeRunning ||
            IsAnswerRevealed ||
            IsAnswerSubmissionInProgress)
        {
            return;
        }

        var question = CurrentQuestion;
        var hasCorrectAnswer = question.CorrectAnswers?.Any(x => !string.IsNullOrWhiteSpace(x)) == true;
        if (hasCorrectAnswer &&
            question.Type == QuestionType.MultipleChoice &&
            !TryParseMultipleChoiceAnswerInput(UserAnswer, question, out _))
        {
            SessionFeedback = UserAnswerValidationMessage;
            return;
        }

        IsAnswerSubmissionInProgress = true;
        try
        {
            var isCorrect = false;
            if (!hasCorrectAnswer)
            {
                SessionFeedback = "정답이 등록되지 않은 문항입니다. 나중에 정답을 매핑한 뒤 채점 가능합니다.";
            }
            else
            {
                isCorrect = AnswerComparer.IsCorrect(question, UserAnswer);
                if (isCorrect)
                {
                    await repository.RemoveWrongAsync(question.Id);
                }
                else
                {
                    await repository.MarkWrongAsync(question.Id);
                }
            }

            var wrongListRefreshSucceeded = !hasCorrectAnswer ||
                                            await TryReloadWrongNonFatalAsync("채점 후 오답 목록 갱신");

            if (hasCorrectAnswer)
            {
                GradedCount++;
                if (isCorrect)
                {
                    CorrectCount++;
                    SessionFeedback = $"정답: {question.CorrectAnswerDisplay}";
                }
                else
                {
                    SessionFeedback = $"오답: 정답은 {question.CorrectAnswerDisplay} 입니다.";
                }
            }

            if (!wrongListRefreshSucceeded)
            {
                SessionFeedback += "\n오답 상태는 저장되었지만 목록 표시는 다음 새로고침 때 갱신됩니다.";
            }

            IsAnswerRevealed = true;
        }
        finally
        {
            IsAnswerSubmissionInProgress = false;
        }
    }

    private void GoToNextQuestion()
    {
        if (!CanGoToNextQuestion)
        {
            return;
        }

        if (SessionCurrentIndex + 1 >= SessionTotalCount)
        {
            var ungradedCount = Math.Max(0, SessionTotalCount - GradedCount);
            var scoreText = GradedCount == 0
                ? "채점 가능한 문항이 없습니다"
                : $"정답 {CorrectCount}/{GradedCount}개";
            var resultMessage = $"연습 완료: {scoreText}" +
                                (ungradedCount == 0 ? string.Empty : $" · 미채점 {ungradedCount}개");
            IsPracticeRunning = false;
            IsAnswerRevealed = false;
            CurrentQuestion = null;
            CurrentQuestionImageSlices.Clear();
            QuestionImageNotice = string.Empty;
            ResetQuestionImageView();
            OnPropertyChanged(nameof(CurrentQuestionSourceDisplay));
            OnPropertyChanged(nameof(CurrentQuestionText));
            OnPropertyChanged(nameof(CurrentQuestionChoicesText));
            OnPropertyChanged(nameof(HasCurrentQuestionImages));
            OnPropertyChanged(nameof(ShowCurrentQuestionText));
            SessionFeedback = resultMessage;
            Feedback = resultMessage;
            UpdatePracticeState();
            return;
        }

        SessionCurrentIndex++;
        SetCurrentQuestion(currentSession[SessionCurrentIndex]);
        UserAnswer = string.Empty;
        SessionFeedback = string.Empty;
        IsAnswerRevealed = false;
    }

    private async Task ReloadWrongAsync()
    {
        var wrong = await repository.GetWrongQuestionsAsync();
        WrongQuestions.Clear();
        foreach (var question in wrong)
        {
            WrongQuestions.Add(question);
        }

        OnPropertyChanged(nameof(CanStartWrongPractice));
        OnPropertyChanged(nameof(WrongQuestionCount));
        UpdatePracticeState();
    }

    private async Task RemoveWrongById(Guid questionId)
    {
        await repository.RemoveWrongAsync(questionId);
        await ReloadWrongAsync();
    }
}
