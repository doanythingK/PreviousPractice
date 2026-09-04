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
    private async Task AddCategoryAsync()
    {
        if (!CanModifyQuestionStructure)
        {
            Feedback = IsPracticeRunning
                ? "연습 중에는 카테고리를 추가할 수 없습니다."
                : "문항 분석 중에는 카테고리를 추가할 수 없습니다.";
            return;
        }

        if (string.IsNullOrWhiteSpace(NewCategoryName))
        {
            Feedback = "카테고리 이름이 비어 있습니다.";
            return;
        }

        if (!TryBeginStructuralOperation("다른 문항 구조 변경 작업이 진행 중입니다."))
        {
            return;
        }

        var categoryName = NewCategoryName;
        try
        {
            var category = await repository.AddOrGetCategoryAsync(categoryName);
            if (!Categories.Any(x => x.Id == category.Id))
            {
                Categories.Add(category);
                Feedback = $"카테고리 '{category.Name}'를 추가했습니다.";
            }
            else
            {
                Feedback = $"카테고리 '{category.Name}'가 이미 존재합니다.";
            }

            SelectedCategory = category;
            NewCategoryName = string.Empty;
            await UpdateSelectedCategoryQuestionCountAsync();
        }
        finally
        {
            EndStructuralOperation();
        }
    }

    private async Task ImportAnswerMapAsync()
    {
        if (!IsPdfAnalysisSupported)
        {
            Feedback = "현재 iOS에서는 PDF OCR 분석을 지원하지 않습니다. Windows, Android 또는 OCR 도구가 설치된 Mac에서 문항을 먼저 반영해 주세요.";
            return;
        }

        if (!CanModifyQuestionStructure)
        {
            Feedback = IsPracticeRunning
                ? "연습 중에는 문항을 가져오거나 갱신할 수 없습니다."
                : "이미 문항 분석이 진행 중입니다.";
            return;
        }

        SetWorkspaceSection(WorkspaceSectionImport);

        var targetCategory = SelectedCategory;
        if (targetCategory == null)
        {
            Feedback = "카테고리를 선택해 주세요.";
            return;
        }

        if (!TryParseExpectedQuestionInput(
                ExpectedQuestionRangeText,
                out var expectedQuestionInput,
                out var expectedQuestionInputError))
        {
            Feedback = expectedQuestionInputError;
            return;
        }

        var selectedSourceFileSnapshot = SelectedSourceFileName;
        if (string.IsNullOrWhiteSpace(selectedSourceFileSnapshot))
        {
            Feedback = "문항 파일을 먼저 선택해 주세요.";
            return;
        }

        var answerMapSnapshot = AnswerMapText;
        var explicitEmptyAnswerIndexes = GetExplicitEmptyAnswerIndexes(answerMapSnapshot);
        var result = QuestionSetParser.ParseAnswerMapWithDetails(answerMapSnapshot);
        var blockingParseErrors = result.Errors
            .Where(error => !IsAllowedExplicitEmptyAnswerError(error, explicitEmptyAnswerIndexes))
            .ToArray();
        if (blockingParseErrors.Length > 0)
        {
            Feedback = $"정답맵 검증 오류: {string.Join(", ", blockingParseErrors)}\n문항 반영을 중단했습니다.";
            return;
        }

        var nonPositiveMultipleChoiceAnswerIndexes = result.Questions
            .Where(question =>
                question.Type == QuestionType.MultipleChoice &&
                question.CorrectAnswers.Any(answer =>
                    int.TryParse(answer, out var value) && value <= 0))
            .Select(question => question.Index)
            .Distinct()
            .OrderBy(index => index)
            .ToArray();
        if (nonPositiveMultipleChoiceAnswerIndexes.Length > 0)
        {
            Feedback = "객관식 정답은 1 이상의 정수여야 합니다. 확인할 문항: " +
                       string.Join(", ", nonPositiveMultipleChoiceAnswerIndexes) +
                       "\n문항 반영을 중단했습니다.";
            return;
        }

        var answerIndexesToUpdate = result.Questions
            .Select(x => x.Index)
            .Where(x => x > 0)
            .ToHashSet();
        var overwriteExistingSnapshot = OverwriteExisting;
        var normalizedSourceFileName = NormalizeSourceFileName(selectedSourceFileSnapshot);
        var sourceFilePath = ResolveSourceFilePath(normalizedSourceFileName);
        if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath))
        {
            Feedback = $"문항 파일을 찾을 수 없습니다: {normalizedSourceFileName}";
            AppLog.Error(
                nameof(MainViewModel),
                $"문항 파일 경로 확인 실패 | source={normalizedSourceFileName}");
            return;
        }

        AppLog.Info(
            nameof(MainViewModel),
            $"문항 분석 시작 | category={targetCategory.Id} | file={sourceFilePath}");

        ClearPdfAnalysisState();
        isPdfAnalysisCommitStarted = false;
        pdfAnalysisCancellation?.Dispose();
        var analysisCancellation = new CancellationTokenSource();
        pdfAnalysisCancellation = analysisCancellation;
        IsPdfAnalysisInProgress = true;
        pdfAnalysisStartAt = DateTimeOffset.UtcNow;
        PdfAnalysisProgressValue = 0d;
        PdfAnalysisStatus = "문항 PDF OCR 분석을 시작합니다.";
        PdfAnalysisStatusColor = Color.FromArgb("#0EA5E9");
        Feedback = PdfAnalysisStatus;

        var questionCommitCompleted = false;
        var committedQuestionCount = 0;

        try
        {
            var progress = new Progress<PdfAnalysisProgress>(value =>
            {
                if (ReferenceEquals(pdfAnalysisCancellation, analysisCancellation) &&
                    !analysisCancellation.IsCancellationRequested)
                {
                    UpdatePdfAnalysisProgress(value);
                }
            });
            var analysis = await pdfAnalysisService.AnalyzePdfAsync(
                sourceFilePath,
                progress,
                expectedQuestionInput.ExplicitRange,
                analysisCancellation.Token);
            analysisCancellation.Token.ThrowIfCancellationRequested();
            if (!analysis.IsSuccess)
            {
                PdfAnalysisSummary = $"문항 분석 실패: {analysis.Message}";
                PdfAnalysisStatus = "문항 분석이 실패했습니다. 상태를 확인해 주세요.";
                PdfAnalysisStatusColor = Color.FromArgb("#DC2626");
                Feedback = $"{PdfAnalysisSummary}\n로그: {AppLog.CurrentLogFilePath}";
                AppLog.Error(
                    nameof(MainViewModel),
                    $"문항 분석 실패 | file={sourceFilePath} | reason={analysis.Message}");
                return;
            }

            AppLog.Info(
                nameof(MainViewModel),
                $"문항 분석 성공 | file={sourceFilePath} | pages={analysis.PageCount} | candidates={analysis.DetectedQuestionCount}");

            var resolvedExpectedQuestionRange = ResolveExpectedQuestionRange(analysis, expectedQuestionInput);
            if (resolvedExpectedQuestionRange.IsAutoInferred &&
                resolvedExpectedQuestionRange.Range is QuestionNumberRange inferredRange)
            {
                PdfAnalysisStatus = "예상 문항 범위에 맞춰 구조를 다시 검증하고 있습니다.";
                var refinedCandidates = await Task.Run(
                    () => OcrQuestionSegmenter.SplitByHeader(analysis.Pages, inferredRange),
                    analysisCancellation.Token);
                analysis = WithQuestionCandidates(analysis, refinedCandidates);
                AppLog.Info(
                    nameof(MainViewModel),
                    $"문항 범위 자동 추정 | file={sourceFilePath} | count={resolvedExpectedQuestionRange.ExpectedCount} | range={inferredRange} | reason={resolvedExpectedQuestionRange.Reason}");
            }
            else if (expectedQuestionInput.IsCountOnly)
            {
                AppLog.Error(
                    nameof(MainViewModel),
                    $"문항 범위 자동 추정 실패 | file={sourceFilePath} | count={expectedQuestionInput.ExpectedCount} | reason={resolvedExpectedQuestionRange.Reason}");
            }

            UpdatePdfAnalysisProgress(new PdfAnalysisProgress(
                analysis.PageCount,
                analysis.PageCount,
                "OCR 완료 · 문항 구조 검증 중"));

            analysisCancellation.Token.ThrowIfCancellationRequested();

            var diagnostics = await Task.Run(
                () => BuildPdfAnalysisDiagnostics(
                    normalizedSourceFileName,
                    analysis,
                    expectedQuestionInput,
                    resolvedExpectedQuestionRange),
                analysisCancellation.Token);
            var repairResult = await Task.Run(
                () => TryAutoRepairAnalysis(
                    normalizedSourceFileName,
                    analysis,
                    diagnostics,
                    expectedQuestionInput,
                    resolvedExpectedQuestionRange),
                analysisCancellation.Token);
            analysis = repairResult.Analysis;
            diagnostics = repairResult.Diagnostics;
            analysisCancellation.Token.ThrowIfCancellationRequested();
            await SavePdfAnalysisAsync(
                normalizedSourceFileName,
                analysis,
                saveAsLatestAttempt: true);
            analysisCancellation.Token.ThrowIfCancellationRequested();
            LatestPdfDiagnosticsTitle = Path.GetFileName(normalizedSourceFileName);
            LatestPdfDiagnosticsText = BuildDiagnosticsText(diagnostics);
            var diagnosticsPath = await SavePdfAnalysisDiagnosticsAsync(
                normalizedSourceFileName,
                diagnostics,
                saveAsLatestAttempt: true);
            analysisCancellation.Token.ThrowIfCancellationRequested();
            PdfAnalysisSummary = BuildAnalysisSummary(analysis, diagnostics);
            PdfAnalysisStatus = "문항 분석이 완료되었습니다.";
            PdfAnalysisStatusColor = Color.FromArgb("#16A34A");

            if (diagnostics.HasExpectedQuestionMismatch || diagnostics.HasBlockingStructuralIssues)
            {
                var blockingSummaries = new List<string>();
                if (diagnostics.HasExpectedQuestionMismatch)
                {
                    blockingSummaries.Add(BuildExpectedQuestionMismatchSummary(diagnostics));
                }

                if (diagnostics.HasBlockingStructuralIssues)
                {
                    blockingSummaries.Add($"구조 검증 실패: {BuildStructuralIssueSummary(diagnostics)}");
                }

                PdfAnalysisStatus = diagnostics.HasBlockingStructuralIssues
                    ? "문항 구조 검증에 실패했습니다."
                    : "예상 문항 범위와 분석 결과가 일치하지 않습니다.";
                PdfAnalysisStatusColor = Color.FromArgb("#DC2626");
                Feedback = $"{string.Join("\n", blockingSummaries)}\n문항 반영을 중단했습니다." +
                           (string.IsNullOrWhiteSpace(diagnosticsPath) ? string.Empty : $"\n진단 파일: {diagnosticsPath}");
                AppLog.Error(
                    nameof(MainViewModel),
                    $"문항 반영 중단 | expectedCount={diagnostics.ExpectedQuestionCount?.ToString() ?? "n/a"} | expectedRange={diagnostics.ExpectedQuestionRange?.ToString() ?? "n/a"} | detected={diagnostics.DistinctCandidateCount} | missing={string.Join(",", diagnostics.MissingIndexes)} | unexpected={string.Join(",", diagnostics.UnexpectedIndexes)} | duplicates={string.Join(",", diagnostics.DuplicateIndexes.Select(x => x.Index))} | structural={string.Join(",", diagnostics.StructuralIssues.Select(x => $"{x.Code}:{x.Index?.ToString() ?? "n/a"}"))} | diag={diagnosticsPath ?? "n/a"}");
                return;
            }

            if (!analysis.HasQuestionCandidates)
            {
                PdfAnalysisStatus = "문항 후보 검증에 실패했습니다.";
                PdfAnalysisStatusColor = Color.FromArgb("#DC2626");
                Feedback = $"{analysis.Summary}\nOCR 문항 후보가 없어 문항 반영을 중단했습니다.";
                AppLog.Error(
                    nameof(MainViewModel),
                    $"문항 저장 중단 | 후보 0개 | file={sourceFilePath}");
                return;
            }

            var candidateByIndex = analysis.QuestionCandidates
                .Where(x => x.Index > 0)
                .GroupBy(x => x.Index)
                .ToDictionary(x => x.Key, x => x.First());

            var answerByIndex = result.Questions
                .GroupBy(x => x.Index)
                .ToDictionary(
                    x => x.Key,
                    x => x.OrderByDescending(q => q.CorrectAnswers.Length).First());

            var answerIndexesOutsideCandidates = answerByIndex.Keys
                .Except(candidateByIndex.Keys)
                .OrderBy(x => x)
                .ToArray();
            if (answerIndexesOutsideCandidates.Length > 0)
            {
                PdfAnalysisStatus = "정답맵 문항 번호 검증에 실패했습니다.";
                PdfAnalysisStatusColor = Color.FromArgb("#DC2626");
                Feedback = "정답맵에 OCR 문항 후보와 일치하지 않는 번호가 있습니다: " +
                           $"{string.Join(", ", answerIndexesOutsideCandidates)}\n" +
                           "정답맵 또는 OCR 분석 결과를 확인한 뒤 다시 시도해 주세요.";
                AppLog.Error(
                    nameof(MainViewModel),
                    $"문항 저장 중단 | 후보 밖 정답 번호={string.Join(",", answerIndexesOutsideCandidates)} | file={sourceFilePath}");
                return;
            }

            var existingCount = await repository.GetQuestionCountBySourceFileAsync(
                targetCategory.Id,
                normalizedSourceFileName);
            var shouldOverwrite = overwriteExistingSnapshot;
            if (shouldOverwrite && existingCount > 0)
            {
                shouldOverwrite = await ConfirmOverwriteImportAsync(normalizedSourceFileName, existingCount);
                if (!shouldOverwrite)
                {
                    Feedback = "문항 덮어쓰기를 취소했거나 확인창을 표시할 수 없어 반영을 중단했습니다.";
                    return;
                }
            }

            var sourceQuestionIndexes = candidateByIndex.Keys
                .OrderBy(x => x)
                .ToArray();

            var questions = sourceQuestionIndexes
                .Select(x =>
                {
                    var hasAnswer = answerByIndex.TryGetValue(x, out var parsedQuestion);
                    var correctAnswers = hasAnswer && parsedQuestion != null
                        ? parsedQuestion.CorrectAnswers
                        : Array.Empty<string>();
                    var questionType = hasAnswer && parsedQuestion != null
                        ? parsedQuestion.Type
                        : QuestionType.MultipleChoice;
                    candidateByIndex.TryGetValue(x, out var matchedCandidate);
                    var imageSegments = BuildQuestionImageSegments(
                        matchedCandidate,
                        analysis.QuestionCandidates,
                        analysis.Pages);
                    var primaryImageSegment = imageSegments.FirstOrDefault();

                    if (matchedCandidate != null && imageSegments.Length > 1)
                    {
                        AppLog.Info(
                            nameof(MainViewModel),
                            $"공유 지문 세그먼트 적용 | file={normalizedSourceFileName} | index={x} | segments={imageSegments.Length} | start=p{matchedCandidate.StartPage}:{matchedCandidate.StartLineInPage}");
                    }

                    var question = new Question
                    {
                        CategoryId = targetCategory.Id,
                        SourceFileName = normalizedSourceFileName,
                        Index = x,
                        Type = questionType,
                        CorrectAnswers = correctAnswers,
                        Choices = Array.Empty<string>(),
                        ImageSegments = imageSegments,
                        ImagePath = primaryImageSegment?.ImagePath,
                        ImageTopRatio = primaryImageSegment?.ImageTopRatio ?? 0d,
                        ImageBottomRatio = primaryImageSegment?.ImageBottomRatio ?? 1d
                    };

                    if (candidateByIndex.TryGetValue(x, out var candidate))
                    {
                        if (!string.IsNullOrWhiteSpace(candidate.PreviewText))
                        {
                            question.Prompt = $"{targetCategory.Name} - {candidate.Header} {candidate.PreviewText}";
                        }
                        else
                        {
                            question.Prompt = $"{targetCategory.Name} - {candidate.Header}";
                        }
                    }

                    if (string.IsNullOrWhiteSpace(question.Prompt))
                    {
                        question.Prompt = $"{targetCategory.Name} - 문항 {x}";
                    }

                    return question;
                })
                .ToArray();

            if (questions.Length == 0)
            {
                Feedback = $"{analysis.Summary} / 저장할 문항 후보가 없습니다. 정답 맵에서 문항 번호를 먼저 넣어주세요.";
                AppLog.Error(
                    nameof(MainViewModel),
                    $"문항 저장 중단 | 생성된 questions=0 | file={sourceFilePath}");
                return;
            }

            analysisCancellation.Token.ThrowIfCancellationRequested();
            isPdfAnalysisCommitStarted = true;
            OnPropertyChanged(nameof(CanCancelPdfAnalysis));
            CancelPdfAnalysisCommand.RaiseCanExecuteChanged();
            PdfAnalysisStatus = "검증된 문항을 저장하고 있습니다.";

            var saveResult = await repository.SaveImportedQuestionsAsync(
                targetCategory.Id,
                normalizedSourceFileName,
                questions,
                overwriteBySourceFile: shouldOverwrite,
                updateExistingCorrectAnswers: true,
                answerIndexesToUpdate: answerIndexesToUpdate);
            questionCommitCompleted = true;
            var isAnswerOnlyUpdate = !shouldOverwrite && !saveResult.StructureChanged;
            committedQuestionCount = isAnswerOnlyUpdate
                ? saveResult.UpdatedQuestionCount
                : questions.Length;
            if (saveResult.StructureChanged)
            {
                // 등록된 이미지 경계가 실제로 새 분석으로 교체된 경우에만
                // 파일 검토 화면이 읽는 canonical 진단을 함께 승격한다.
                await SavePdfAnalysisAsync(normalizedSourceFileName, analysis);
                await SavePdfAnalysisDiagnosticsAsync(normalizedSourceFileName, diagnostics);
            }

            var mismatchMessage = isAnswerOnlyUpdate
                ? string.Empty
                : BuildCandidateMismatchMessage(analysis, result, questions);
            var existingQuestionUpdateMessage = isAnswerOnlyUpdate
                ? "기존 이미지 경계는 유지하고 정답맵에 지정된 기존 번호만 갱신했습니다. 새로 검출된 번호를 추가하려면 덮어쓰기를 켜고 다시 반영해 주세요"
                : string.Empty;
            var summary = isAnswerOnlyUpdate
                ? saveResult.UpdatedQuestionCount > 0
                    ? $"기존 문항 정답 {saveResult.UpdatedQuestionCount}개 갱신 완료"
                    : "갱신할 기존 문항 정답 없음"
                : !string.IsNullOrWhiteSpace(mismatchMessage)
                    ? $"{questions.Length}개 저장 / {mismatchMessage}"
                    : $"{questions.Length}개 저장 완료";

            Feedback = $"{analysis.Summary} / {summary}"
                       + (string.IsNullOrWhiteSpace(existingQuestionUpdateMessage) ? string.Empty : $" / {existingQuestionUpdateMessage}");
            AppLog.Info(
                nameof(MainViewModel),
                $"문항 반영 완료 | file={sourceFilePath} | added={saveResult.AddedQuestionCount} | updated={saveResult.UpdatedQuestionCount} | removed={saveResult.RemovedQuestionCount} | structureChanged={saveResult.StructureChanged}");
            if (string.Equals(AnswerMapText, answerMapSnapshot, StringComparison.Ordinal))
            {
                AnswerMapText = string.Empty;
            }
            await UpdateSelectedCategoryQuestionCountAsync();
            var sourceFile = SourceFiles.FirstOrDefault(x =>
                string.Equals(x.SourceFileName, normalizedSourceFileName, StringComparison.OrdinalIgnoreCase));
            if (sourceFile != null)
            {
                SelectedSourceFile = sourceFile;
            }
            await ReloadWrongAsync();
            PdfAnalysisProgressValue = 1d;
            PdfAnalysisStatus = isAnswerOnlyUpdate
                ? $"기존 문항 정답 {saveResult.UpdatedQuestionCount}개 갱신과 화면 새로고침이 완료되었습니다."
                : $"문항 {questions.Length}개 반영과 화면 갱신이 완료되었습니다.";
            PdfAnalysisStatusColor = Color.FromArgb("#16A34A");
        }
        catch (OperationCanceledException) when (analysisCancellation.IsCancellationRequested)
        {
            PdfAnalysisSummary = "문항 분석이 취소되었습니다.";
            PdfAnalysisStatus = "사용자 요청으로 문항 분석을 취소했습니다.";
            PdfAnalysisStatusColor = Color.FromArgb("#475569");
            Feedback = PdfAnalysisStatus;
            AppLog.Info(
                nameof(MainViewModel),
                $"문항 분석 취소 | file={sourceFilePath}");
        }
        catch (Exception ex)
        {
            if (questionCommitCompleted)
            {
                PdfAnalysisStatus = "문항 저장은 완료되었지만 화면 갱신에 실패했습니다.";
                PdfAnalysisStatusColor = Color.FromArgb("#D97706");
                Feedback = $"문항 {committedQuestionCount}개 저장은 완료되었습니다. " +
                           $"화면 목록을 갱신하지 못했습니다: {ex.Message}\n" +
                           "다시 가져오지 말고 카테고리나 화면을 다시 열어 확인해 주세요.";
                AppLog.Error(
                    nameof(MainViewModel),
                    $"문항 저장 후 화면 갱신 실패 | file={sourceFilePath} | saved={committedQuestionCount}",
                    ex);
            }
            else
            {
                Feedback = $"문항 분석 중 오류가 발생했습니다: {ex.Message}";
                PdfAnalysisSummary = $"문항 분석 실패: {ex.Message}";
                PdfAnalysisStatus = "문항 분석 중 오류가 발생했습니다.";
                PdfAnalysisStatusColor = Color.FromArgb("#DC2626");
                AppLog.Error(
                    nameof(MainViewModel),
                    $"문항 분석 예외 | file={sourceFilePath}",
                    ex);
            }
        }
        finally
        {
            if (ReferenceEquals(pdfAnalysisCancellation, analysisCancellation))
            {
                pdfAnalysisCancellation = null;
            }

            isPdfAnalysisCommitStarted = false;
            analysisCancellation.Dispose();
            IsPdfAnalysisInProgress = false;
        }
    }

    private async Task LoadAnswerFileAsync()
    {
        if (!TryBeginStructuralOperation("다른 문항 구조 변경 작업이 진행 중입니다."))
        {
            return;
        }

        try
        {
            var options = new PickOptions
            {
                PickerTitle = "정답 파일 선택",
                FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                {
                    { DevicePlatform.WinUI, new[] { ".txt", ".csv", ".md", ".text" } },
                    { DevicePlatform.MacCatalyst, new[] { "public.plain-text" } },
                    { DevicePlatform.iOS, new[] { "public.text" } },
                    { DevicePlatform.Android, new[] { "text/plain" } },
                })
            };

            var file = await FilePicker.PickAsync(options);
            if (file == null)
            {
                Feedback = "정답 파일 선택이 취소되었습니다.";
                return;
            }

            using var stream = await file.OpenReadAsync();
            using var reader = new StreamReader(stream);
            var content = await reader.ReadToEndAsync();
            AnswerMapText = content;

            Feedback = $"정답 파일을 불러왔습니다: {file.FileName}";
        }
        catch (Exception ex)
        {
            Feedback = $"정답 파일을 불러오지 못했습니다: {ex.Message}";
        }
        finally
        {
            EndStructuralOperation();
        }
    }

    private Task LoadSourceFilesFromDirectoryAsync()
    {
        try
        {
            Directory.CreateDirectory(sourceFileDirectory);
            var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var files = EnumerateSourcePdfFiles()
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name) && seenFiles.Add(name))
                .Select(name => name!)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();

            SourceFilesDirectory.Clear();
            foreach (var file in files)
            {
                SourceFilesDirectory.Add(file);
            }

            if (!SourceFilesDirectory.Any())
            {
                SelectedSourceFileName = string.Empty;
            }
            else if (string.IsNullOrWhiteSpace(SelectedSourceFileName) ||
                     !SourceFilesDirectory.Contains(SelectedSourceFileName, StringComparer.OrdinalIgnoreCase))
            {
                SelectedSourceFileName = SourceFilesDirectory.FirstOrDefault() ?? string.Empty;
            }
        }
        catch (Exception ex)
        {
            Feedback = $"문항 파일 목록을 읽지 못했습니다: {ex.Message}";
        }

        OnPropertyChanged(nameof(CanImportAnswerMap));
        return Task.CompletedTask;
    }

    private async Task ImportSourceFileAsync()
    {
        if (!CanModifyQuestionStructure)
        {
            Feedback = IsSourceFileImportInProgress
                ? "이미 문항 PDF 등록이 진행 중입니다."
                : IsPracticeRunning
                    ? "연습 중에는 문항 PDF를 추가하거나 덮어쓸 수 없습니다."
                    : "문항 분석 또는 다른 구조 변경 중에는 PDF를 추가하거나 덮어쓸 수 없습니다.";
            return;
        }

        IsSourceFileImportInProgress = true;
        try
        {
            var options = new PickOptions
            {
                PickerTitle = "문항 PDF 선택",
                FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                {
                    { DevicePlatform.WinUI, new[] { ".pdf" } },
                    { DevicePlatform.MacCatalyst, new[] { "public.pdf", "com.adobe.pdf" } },
                    { DevicePlatform.iOS, new[] { "public.pdf", "com.adobe.pdf" } },
                    { DevicePlatform.Android, new[] { "application/pdf", "application/octet-stream", ".pdf" } },
                })
            };

            var file = await FilePicker.PickAsync(options);
            if (file == null)
            {
                Feedback = "문항 파일 선택이 취소되었습니다.";
                return;
            }

            if (!CanContinueSourceFileImport())
            {
                Feedback = "상태가 변경되어 문항 PDF 추가를 중단했습니다.";
                return;
            }

            Directory.CreateDirectory(sourceFileDirectory);
            var rawSourceFileName = file.FileName?.Trim();
            var sourceFileName = string.IsNullOrWhiteSpace(rawSourceFileName)
                ? string.Empty
                : Path.GetFileName(rawSourceFileName);
            if (string.IsNullOrWhiteSpace(sourceFileName))
            {
                Feedback = "문항 파일 이름을 가져올 수 없습니다.";
                return;
            }

            if (!string.Equals(Path.GetExtension(sourceFileName), ".pdf", StringComparison.OrdinalIgnoreCase))
            {
                Feedback = "PDF 파일만 등록할 수 있습니다.";
                return;
            }

            var destinationPath = Path.Combine(sourceFileDirectory, sourceFileName);
            if (File.Exists(destinationPath))
            {
                var overwrite = await ConfirmOverwriteSourceFileAsync(sourceFileName);
                if (!overwrite)
                {
                    Feedback = "문항 파일 등록을 취소했거나 확인창을 표시할 수 없어 중단했습니다.";
                    return;
                }
            }

            if (!CanContinueSourceFileImport())
            {
                Feedback = "상태가 변경되어 문항 PDF 추가를 중단했습니다.";
                return;
            }

            Feedback = $"문항 PDF를 안전하게 등록하고 있습니다: {sourceFileName}";
            await CopyPdfFileAtomicallyAsync(file, destinationPath);

            await LoadSourceFilesFromDirectoryAsync();
            SelectedSourceFileName = sourceFileName;
            Feedback = $"문항 파일을 등록했습니다: {sourceFileName}";
        }
        catch (Exception ex)
        {
            Feedback = $"문항 파일 등록을 실패했습니다: {ex.Message}";
            AppLog.Error(nameof(MainViewModel), "문항 PDF 등록 실패", ex);
        }
        finally
        {
            IsSourceFileImportInProgress = false;
        }
    }

    private async Task DeleteSourceFileAsync(SourceFileSummary? sourceFile)
    {
        if (sourceFile == null)
        {
            Feedback = "삭제할 파일을 선택해 주세요.";
            return;
        }

        var targetCategory = SelectedCategory;
        if (targetCategory == null)
        {
            Feedback = "카테고리를 선택해 주세요.";
            return;
        }

        if (!CanModifyQuestionStructure)
        {
            Feedback = IsPracticeRunning
                ? "진행 중인 연습이 있어 파일을 삭제할 수 없습니다."
                : "문항 분석 중에는 파일을 삭제할 수 없습니다.";
            return;
        }

        if (!TryBeginStructuralOperation("다른 문항 구조 변경 작업이 진행 중입니다."))
        {
            return;
        }

        try
        {
            var canDelete = await ConfirmDeleteSourceFileAsync(sourceFile.SourceFileName, sourceFile.QuestionCount);
            if (!canDelete)
            {
                Feedback = "등록 문항 삭제를 취소했거나 확인창을 표시할 수 없어 중단했습니다.";
                return;
            }

            var removed = await repository.RemoveQuestionsBySourceFileAsync(targetCategory.Id, sourceFile.SourceFileName);
            if (!removed)
            {
                Feedback = "삭제할 문제를 찾을 수 없습니다.";
                return;
            }

            Feedback = $"'{sourceFile.SourceFileName}'의 등록 문항 {sourceFile.QuestionCount}개를 삭제했습니다. 원본 PDF는 유지됩니다.";
            await UpdateSelectedCategoryQuestionCountAsync();
            await ReloadWrongAsync();
        }
        finally
        {
            EndStructuralOperation();
        }
    }

    private async Task DeleteCategoryAsync()
    {
        var target = SelectedCategory;
        if (target == null)
        {
            Feedback = "삭제할 카테고리가 없습니다.";
            return;
        }

        if (!CanModifyQuestionStructure)
        {
            Feedback = IsPracticeRunning
                ? "진행 중인 연습이 있어 카테고리를 삭제할 수 없습니다."
                : "문항 분석 중에는 카테고리를 삭제할 수 없습니다.";
            return;
        }

        if (!TryBeginStructuralOperation("다른 문항 구조 변경 작업이 진행 중입니다."))
        {
            return;
        }

        try
        {
            var questionCount = selectedCategoryQuestionCount;
            var canDelete = await ConfirmDeleteAsync(target.Name, questionCount);
            if (!canDelete)
            {
                Feedback = "카테고리 삭제를 취소했거나 확인창을 표시할 수 없어 중단했습니다.";
                return;
            }

            var removed = await repository.RemoveCategoryAsync(target.Id);
            if (!removed)
            {
                Feedback = "카테고리를 찾을 수 없습니다.";
                return;
            }

            Categories.Remove(target);

            SelectedCategory = Categories.Count > 0
                ? Categories[0]
                : null;

            Feedback = $"카테고리 '{target.Name}' 삭제했습니다.";
            await UpdateSelectedCategoryQuestionCountAsync();
            await ReloadWrongAsync();
        }
        finally
        {
            EndStructuralOperation();
        }
    }
}
