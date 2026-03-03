using System.IO;
using System.Diagnostics;
using System.Text;
using PreviousPractice.Models;
using PreviousPractice.Infrastructure;

#if WINDOWS
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;
#endif
#if IOS || MACCATALYST
using Foundation;
using PDFKit;
#endif

namespace PreviousPractice.Services;

public interface IPdfAnalysisService
{
    Task<PdfOcrResult> AnalyzePdfAsync(
        string pdfFilePath,
        IProgress<PdfAnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed record PdfAnalysisProgress(int ProcessedPages, int TotalPages, string Message);

public sealed class PdfAnalysisService : IPdfAnalysisService
{
    public Task<PdfOcrResult> AnalyzePdfAsync(
        string pdfFilePath,
        IProgress<PdfAnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
#if WINDOWS
        return AnalyzePdfWithWindowsOcrAsync(pdfFilePath, progress, cancellationToken);
#elif IOS || MACCATALYST
        return AnalyzePdfWithPdfKitAsync(pdfFilePath, progress, cancellationToken);
#else
        return AnalyzePdfWithCommandLineToolsAsync(pdfFilePath, progress, cancellationToken);
#endif
    }

    private static string NormalizeWhitespace(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = text.Replace("\r", string.Empty);
        normalized = normalized.Replace('\u00A0', ' ');

        var tokens = normalized
            .Split('\n')
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x));

        var sb = new StringBuilder();
        foreach (var token in tokens)
        {
            sb.AppendLine(token);
        }

        return sb.ToString().TrimEnd();
    }

#if WINDOWS
    private static async Task<PdfOcrResult> AnalyzePdfWithWindowsOcrAsync(
        string pdfFilePath,
        IProgress<PdfAnalysisProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pdfFilePath))
        {
            return PdfOcrResult.Fail("unknown.pdf", "문항 PDF 경로가 비어 있습니다.");
        }

        if (!File.Exists(pdfFilePath))
        {
            return PdfOcrResult.Fail(Path.GetFileName(pdfFilePath), "문항 PDF 파일을 찾을 수 없습니다.");
        }

        var sourceFileName = Path.GetFileName(pdfFilePath);
        var ocrEngine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (ocrEngine == null)
        {
            return PdfOcrResult.Fail(sourceFileName, "현재 장치에서 OCR 엔진을 사용할 수 없습니다.");
        }

        try
        {
            var storageFile = await StorageFile.GetFileFromPathAsync(pdfFilePath);
            using var fileStream = await storageFile.OpenReadAsync();
            var pdfDocument = await PdfDocument.LoadFromStreamAsync(fileStream);

            if (pdfDocument.PageCount <= 0)
            {
                return PdfOcrResult.Fail(sourceFileName, "페이지가 없는 PDF 파일입니다.");
            }

            var totalPages = (int)pdfDocument.PageCount;
            ReportProgress(progress, 0, totalPages, "PDF 렌더링/페이지 읽기 시작");

            var pages = new List<OcrPageResult>();
            for (uint i = 0; i < pdfDocument.PageCount; i++)
            {
                ReportProgress(
                    progress,
                    (int)i,
                    totalPages,
                    $"페이지 {i + 1}/{totalPages} OCR 분석 중");

                cancellationToken.ThrowIfCancellationRequested();

                using var pdfPage = pdfDocument.GetPage(i);
                using var imageStream = new InMemoryRandomAccessStream();
                await pdfPage.RenderToStreamAsync(imageStream);

                imageStream.Seek(0);
                var decoder = await BitmapDecoder.CreateAsync(imageStream);
                using var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied);

                var ocrResult = await ocrEngine.RecognizeAsync(softwareBitmap);
                if (ocrResult == null)
                {
                    continue;
                }

                var recognizedText = NormalizeWhitespace(ocrResult.Text);
                var wordCount = string.IsNullOrWhiteSpace(recognizedText)
                    ? 0
                    : recognizedText.Split((char[])['\r', '\n', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
                var averageConfidence = wordCount == 0 ? 0f : 100f;

                pages.Add(new OcrPageResult(
                    (int)i + 1,
                    recognizedText,
                    wordCount,
                    averageConfidence));
            }

            ReportProgress(progress, totalPages, totalPages, "OCR 분석 완료");

            if (pages.Count == 0 || !pages.Any(x => !string.IsNullOrWhiteSpace(x.Text)))
            {
                return PdfOcrResult.Fail(sourceFileName, "이미지에서 텍스트를 추출하지 못했습니다.");
            }

            var candidates = OcrQuestionSegmenter.SplitByHeader(pages);
            return PdfOcrResult.Ok(sourceFileName, pages, candidates);
        }
        catch (OperationCanceledException)
        {
            return PdfOcrResult.Fail(sourceFileName, "OCR 분석이 취소되었습니다.");
        }
        catch (Exception ex)
        {
            return PdfOcrResult.Fail(sourceFileName, $"OCR 처리 중 오류: {ex.Message}");
        }
    }

#elif IOS || MACCATALYST
    private static async Task<PdfOcrResult> AnalyzePdfWithPdfKitAsync(
        string pdfFilePath,
        IProgress<PdfAnalysisProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        var sourceFileName = Path.GetFileName(pdfFilePath);
        if (string.IsNullOrWhiteSpace(pdfFilePath))
        {
            return PdfOcrResult.Fail("unknown.pdf", "문항 PDF 경로가 비어 있습니다.");
        }

        if (!File.Exists(pdfFilePath))
        {
            return PdfOcrResult.Fail(sourceFileName, "문항 PDF 파일을 찾을 수 없습니다.");
        }

        await Task.Yield();

        try
        {
            using var documentUrl = NSUrl.FromFilename(pdfFilePath);
            using var pdfDocument = new PDFDocument(documentUrl);
            if (pdfDocument == null || pdfDocument.PageCount <= 0)
            {
                return PdfOcrResult.Fail(sourceFileName, "PDF 문서가 비어 있거나 열 수 없습니다.");
            }

            var totalPages = pdfDocument.PageCount;
            ReportProgress(progress, 0, totalPages, "PDF 텍스트 추출 시작");

            var pages = new List<OcrPageResult>();
            for (var i = 0; i < pdfDocument.PageCount; i++)
            {
                ReportProgress(
                    progress,
                    i,
                    totalPages,
                    $"페이지 {i + 1}/{totalPages} 텍스트 추출 중");

                cancellationToken.ThrowIfCancellationRequested();

                var page = pdfDocument.GetPage(i);
                if (page == null)
                {
                    continue;
                }

                var raw = NormalizeWhitespace(page.String);
                var wordCount = string.IsNullOrWhiteSpace(raw)
                    ? 0
                    : raw.Split((char[])['\r', '\n', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;

                pages.Add(new OcrPageResult(i + 1, raw, wordCount, 0f));
            }

            ReportProgress(progress, totalPages, totalPages, "텍스트 추출 완료");

            if (pages.Count == 0 || !pages.Any(x => !string.IsNullOrWhiteSpace(x.Text)))
            {
                return PdfOcrResult.Fail(
                    sourceFileName,
                    "이 PDF는 텍스트 추출이 되지 않습니다. 현재 iOS/macOS는 이미지 OCR이 아니라 PDF 내장 텍스트 추출로만 분석됩니다.");
            }

            var candidates = OcrQuestionSegmenter.SplitByHeader(pages);
            return PdfOcrResult.Ok(sourceFileName, pages, candidates);
        }
        catch (OperationCanceledException)
        {
            return PdfOcrResult.Fail(sourceFileName, "OCR 분석이 취소되었습니다.");
        }
        catch (Exception ex)
        {
            return PdfOcrResult.Fail(sourceFileName, $"PDF 텍스트 추출 중 오류: {ex.Message}");
        }
    }

#else
    private static async Task<PdfOcrResult> AnalyzePdfWithCommandLineToolsAsync(
        string pdfFilePath,
        IProgress<PdfAnalysisProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        var sourceFileName = Path.GetFileName(pdfFilePath);
        if (string.IsNullOrWhiteSpace(pdfFilePath))
        {
            return PdfOcrResult.Fail("unknown.pdf", "문항 PDF 경로가 비어 있습니다.");
        }

        if (!File.Exists(pdfFilePath))
        {
            return PdfOcrResult.Fail(sourceFileName, "문항 PDF 파일을 찾을 수 없습니다.");
        }

        var pdftoppmPath = ResolveCommandPath("pdftoppm");
        var tesseractPath = ResolveCommandPath("tesseract");
        if (string.IsNullOrWhiteSpace(pdftoppmPath) || string.IsNullOrWhiteSpace(tesseractPath))
        {
            var missingTools = new List<string>();
            if (string.IsNullOrWhiteSpace(pdftoppmPath)) missingTools.Add("pdftoppm");
            if (string.IsNullOrWhiteSpace(tesseractPath)) missingTools.Add("tesseract");

            if (OperatingSystem.IsAndroid() || OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst())
            {
                var platformName = OperatingSystem.IsAndroid()
                    ? "Android"
                    : OperatingSystem.IsMacCatalyst()
                        ? "Mac Catalyst"
                        : "Mac";

                return PdfOcrResult.Fail(
                    sourceFileName,
                    $"{platformName}에서 PDF OCR을 실행하려면 pdftoppm, tesseract가 앱 번들/패키지 경로에 필요합니다. 누락: {string.Join(", ", missingTools)}");
            }

                return PdfOcrResult.Fail(
                    sourceFileName,
                    $"필요한 OCR 도구를 찾을 수 없습니다. 누락: {string.Join(", ", missingTools)}");
        }

        ReportProgress(progress, 0, 0, "pdftoppm/tesseract 확인 완료");
        var workDirectory = Path.Combine(Path.GetTempPath(), "PreviousPracticePdfOcr", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDirectory);

        try
        {
            var imageBase = Path.Combine(workDirectory, "page");
            var renderArgs = $"-png -r 300 {Quote(pdfFilePath)} {Quote(imageBase)}";
            var renderResult = await RunCommandAsync(pdftoppmPath, renderArgs, cancellationToken);

            if (renderResult.ExitCode != 0)
            {
                return PdfOcrResult.Fail(
                    sourceFileName,
                    $"pdftoppm 실행 실패: {renderResult.StdErr.Trim()}");
            }

            var images = Directory.GetFiles(workDirectory, "page-*.png")
                .OrderBy(ExtractPageIndexFromFileName)
                .ThenBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (images.Length == 0)
            {
                return PdfOcrResult.Fail(sourceFileName, "PDF 페이지를 이미지로 변환하지 못했습니다.");
            }

            var totalPages = images.Length;
            ReportProgress(progress, 0, totalPages, "이미지 생성 완료. OCR 실행 중");

            var pages = new List<OcrPageResult>();
            for (var i = 0; i < images.Length; i++)
            {
                ReportProgress(
                    progress,
                    i,
                    totalPages,
                    $"이미지 {i + 1}/{totalPages} OCR 처리 중");

                cancellationToken.ThrowIfCancellationRequested();

                var imagePath = images[i];
                var ocrArgs = $"{Quote(imagePath)} stdout -l kor+eng";
                var ocrResult = await RunCommandAsync(tesseractPath, ocrArgs, cancellationToken);
                if (ocrResult.ExitCode != 0)
                {
                    if (string.IsNullOrWhiteSpace(ocrResult.StdOut))
                    {
                        return PdfOcrResult.Fail(
                            sourceFileName,
                            $"이미지 OCR 실행 실패({imagePath}): {ocrResult.StdErr.Trim()}");
                    }

                    var rawFailedOutput = NormalizeWhitespace(ocrResult.StdOut);
                    if (string.IsNullOrWhiteSpace(rawFailedOutput))
                    {
                        return PdfOcrResult.Fail(
                            sourceFileName,
                            "tesseract 실행 결과가 비정상입니다.");
                    }

                    pages.Add(new OcrPageResult(i + 1, string.Empty, 0, 0f));
                    continue;
                }

                var raw = NormalizeWhitespace(ocrResult.StdOut);
                var wordCount = string.IsNullOrWhiteSpace(raw)
                    ? 0
                    : raw.Split((char[])['\r', '\n', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;

                pages.Add(new OcrPageResult(i + 1, raw, wordCount, 0f));
            }

            ReportProgress(progress, totalPages, totalPages, "OCR 분석 완료");

            if (!pages.Any(x => !string.IsNullOrWhiteSpace(x.Text)))
            {
                return PdfOcrResult.Fail(sourceFileName, "이미지에서 텍스트를 추출하지 못했습니다.");
            }

            var candidates = OcrQuestionSegmenter.SplitByHeader(pages);
            return PdfOcrResult.Ok(sourceFileName, pages, candidates);
        }
        catch (OperationCanceledException)
        {
            return PdfOcrResult.Fail(sourceFileName, "OCR 분석이 취소되었습니다.");
        }
        catch (Exception ex)
        {
            return PdfOcrResult.Fail(sourceFileName, $"OCR 처리 중 오류: {ex.Message}");
        }
        finally
        {
            try
            {
                Directory.Delete(workDirectory, recursive: true);
            }
            catch
            {
                // 임시 폴더 정리는 선택 동작입니다.
            }
        }
    }

    private static string? ResolveCommandPath(string fileName)
    {
        var candidates = new List<string> { fileName };
        if (OperatingSystem.IsWindows())
        {
            candidates.Add($"{fileName}.exe");
        }

        var baseDirectoryCandidates = new List<string>
        {
            string.Empty,
            AppContext.BaseDirectory,
            Environment.CurrentDirectory,
            Path.GetDirectoryName(typeof(PdfAnalysisService).Assembly.Location) ?? string.Empty
        };

        var paths = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        foreach (var baseDirectory in baseDirectoryCandidates)
        {
            if (string.IsNullOrWhiteSpace(baseDirectory))
            {
                continue;
            }

            paths.Add(baseDirectory);
            paths.Add(Path.Combine(baseDirectory, "tools"));
            paths.Add(Path.Combine(baseDirectory, "tools", "linux"));
            paths.Add(Path.Combine(baseDirectory, "tools", "mac"));
            paths.Add(Path.Combine(baseDirectory, "tools", "android"));

            if (OperatingSystem.IsWindows())
            {
                paths.Add(Path.Combine(baseDirectory, "tools", "windows"));
            }
        }

        foreach (var path in paths)
        {
            foreach (var candidate in candidates)
            {
                var fullPath = Path.Combine(path, candidate);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }
        }

        return null;
    }

    private static void ReportProgress(
        IProgress<PdfAnalysisProgress>? progress,
        int processedPages,
        int totalPages,
        string message)
    {
        if (progress == null)
        {
            return;
        }

        progress.Report(new PdfAnalysisProgress(
            Math.Max(0, processedPages),
            Math.Max(0, totalPages),
            string.IsNullOrWhiteSpace(message) ? string.Empty : message.Trim()));
    }

    private static async Task<CommandExecutionResult> RunCommandAsync(
        string commandPath,
        string arguments,
        CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = commandPath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new CommandExecutionResult(-1, string.Empty, ex.Message);
        }

        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var stdOut = await stdOutTask.ConfigureAwait(false);
        var stdErr = await stdErrTask.ConfigureAwait(false);

        return new CommandExecutionResult(process.ExitCode, stdOut, stdErr);
    }

    private static int ExtractPageIndexFromFileName(string filePath)
    {
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        var dashIndex = fileName.LastIndexOf('-');
        if (dashIndex >= 0 && int.TryParse(fileName.AsSpan(dashIndex + 1), out var index))
        {
            return index;
        }

        return int.MaxValue;
    }

    private static string Quote(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }

        var escaped = value.Replace("\"", "\\\"");
        return $"\"{escaped}\"";
    }

    private sealed record CommandExecutionResult(int ExitCode, string StdOut, string StdErr);
#endif
}
