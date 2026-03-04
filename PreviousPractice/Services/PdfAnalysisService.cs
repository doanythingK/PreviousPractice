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
    private const string ImageCacheRoot = "QuestionSourceImageCache";

    public Task<PdfOcrResult> AnalyzePdfAsync(
        string pdfFilePath,
        IProgress<PdfAnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        AppLog.Info(
            nameof(PdfAnalysisService),
            $"AnalyzePdfAsync 시작 | file={pdfFilePath} | os={Environment.OSVersion.Platform}");
#if WINDOWS
        return AnalyzePdfWithWindowsOcrAsync(pdfFilePath, progress, cancellationToken);
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

    private static PdfOcrResult FailWithLog(string sourceFileName, string message, Exception? ex = null)
    {
        AppLog.Error(
            nameof(PdfAnalysisService),
            $"OCR 실패 | file={sourceFileName} | reason={message}",
            ex);
        return PdfOcrResult.Fail(sourceFileName, message);
    }

    private static void LogPageSummary(string sourceFileName, IReadOnlyList<OcrPageResult> pages)
    {
        if (pages.Count == 0)
        {
            AppLog.Error(
                nameof(PdfAnalysisService),
                $"OCR 페이지 요약 없음 | file={sourceFileName} | pages=0");
            return;
        }

        var nonEmptyPageCount = pages.Count(x => !string.IsNullOrWhiteSpace(x.Text));
        var samples = pages
            .Take(10)
            .Select(p =>
            {
                var head = p.Text.Replace('\r', ' ').Replace('\n', ' ').Trim();
                if (head.Length > 60)
                {
                    head = head[..60] + "...";
                }

                return $"p{p.PageIndex}:words={p.WordCount},textLen={p.Text.Length},img={(string.IsNullOrWhiteSpace(p.ImagePath) ? "N" : "Y")},head={head}";
            })
            .ToArray();

        AppLog.Info(
            nameof(PdfAnalysisService),
            $"OCR 페이지 요약 | file={sourceFileName} | pages={pages.Count} | nonEmpty={nonEmptyPageCount} | sample={string.Join(" | ", samples)}");
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

    private static string PrepareImageDirectory(string sourceFileName)
    {
        var baseDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PreviousPractice",
            ImageCacheRoot);

        Directory.CreateDirectory(baseDirectory);

        var safeName = GetSafeFileNameWithoutExtension(sourceFileName);
        var imageDirectory = Path.Combine(baseDirectory, safeName);
        Directory.CreateDirectory(imageDirectory);

        try
        {
            foreach (var file in Directory.EnumerateFiles(imageDirectory))
            {
                try
                {
                    File.Delete(file);
                }
                catch
                {
                }
            }
        }
        catch
        {
        }

        return imageDirectory;
    }

    private static string GetSafeFileNameWithoutExtension(string fileName)
    {
        var baseName = Path.GetFileNameWithoutExtension(fileName).Trim();
        if (string.IsNullOrWhiteSpace(baseName))
        {
            return "analysis";
        }

        var invalid = Path.GetInvalidFileNameChars();
        return new string(baseName.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    private static string BuildPageImagePath(string imageDirectory, int pageIndex)
    {
        return Path.Combine(imageDirectory, $"page-{pageIndex:D4}.png");
    }

    #if WINDOWS
    private static async Task SaveImageStreamAsync(IRandomAccessStream imageStream, string destinationPath)
    {
        imageStream.Seek(0);
        using var destination = File.Create(destinationPath);
        await imageStream.AsStreamForRead().CopyToAsync(destination);
    }
    #endif

#if WINDOWS
    private static async Task<PdfOcrResult> AnalyzePdfWithWindowsOcrAsync(
        string pdfFilePath,
        IProgress<PdfAnalysisProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pdfFilePath))
        {
            return FailWithLog("unknown.pdf", "문항 PDF 경로가 비어 있습니다.");
        }

        if (!File.Exists(pdfFilePath))
        {
            return FailWithLog(Path.GetFileName(pdfFilePath), "문항 PDF 파일을 찾을 수 없습니다.");
        }

        var sourceFileName = Path.GetFileName(pdfFilePath);
        var ocrEngine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (ocrEngine == null)
        {
            return FailWithLog(sourceFileName, "현재 장치에서 OCR 엔진을 사용할 수 없습니다.");
        }

        try
        {
            var imageDirectory = PrepareImageDirectory(sourceFileName);
            var storageFile = await StorageFile.GetFileFromPathAsync(pdfFilePath);
            using var fileStream = await storageFile.OpenReadAsync();
            var pdfDocument = await PdfDocument.LoadFromStreamAsync(fileStream);

            if (pdfDocument.PageCount <= 0)
            {
                return FailWithLog(sourceFileName, "페이지가 없는 PDF 파일입니다.");
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

                var imagePath = BuildPageImagePath(imageDirectory, (int)i + 1);
                try
                {
                    await SaveImageStreamAsync(imageStream, imagePath);
                }
                catch
                {
                }

                var recognizedText = NormalizeWhitespace(ocrResult.Text);
                var wordCount = string.IsNullOrWhiteSpace(recognizedText)
                    ? 0
                    : recognizedText.Split(
                        new[] { '\r', '\n', ' ' },
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
                var averageConfidence = wordCount == 0 ? 0f : 100f;

                pages.Add(new OcrPageResult(
                    (int)i + 1,
                    recognizedText,
                    wordCount,
                    averageConfidence,
                    File.Exists(imagePath) ? imagePath : null));
            }

            ReportProgress(progress, totalPages, totalPages, "OCR 분석 완료");
            LogPageSummary(sourceFileName, pages);

            if (pages.Count == 0 || !pages.Any(x => !string.IsNullOrWhiteSpace(x.Text)))
            {
                return FailWithLog(sourceFileName, "이미지에서 텍스트를 추출하지 못했습니다.");
            }

            var candidates = OcrQuestionSegmenter.SplitByHeader(pages);
            AppLog.Info(
                nameof(PdfAnalysisService),
                $"OCR 성공 | file={sourceFileName} | pages={pages.Count} | candidates={candidates.Count}");
            return PdfOcrResult.Ok(sourceFileName, pages, candidates);
        }
        catch (OperationCanceledException)
        {
            return FailWithLog(sourceFileName, "OCR 분석이 취소되었습니다.");
        }
        catch (Exception ex)
        {
            return FailWithLog(sourceFileName, $"OCR 처리 중 오류: {ex.Message}", ex);
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
            return FailWithLog("unknown.pdf", "문항 PDF 경로가 비어 있습니다.");
        }

        if (!File.Exists(pdfFilePath))
        {
            return FailWithLog(sourceFileName, "문항 PDF 파일을 찾을 수 없습니다.");
        }

        var pdftoppmPath = ResolveCommandPath("pdftoppm");
        var tesseractPath = ResolveCommandPath("tesseract");
        if (string.IsNullOrWhiteSpace(pdftoppmPath) || string.IsNullOrWhiteSpace(tesseractPath))
        {
            var missingTools = new List<string>();
            if (string.IsNullOrWhiteSpace(pdftoppmPath)) missingTools.Add("pdftoppm");
            if (string.IsNullOrWhiteSpace(tesseractPath)) missingTools.Add("tesseract");

            if (OperatingSystem.IsAndroid() || OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst() || OperatingSystem.IsIOS())
            {
                var platformName = OperatingSystem.IsAndroid()
                    ? "Android"
                    : OperatingSystem.IsIOS()
                        ? "iOS"
                        : OperatingSystem.IsMacCatalyst()
                        ? "Mac Catalyst"
                        : "Mac";

                return FailWithLog(
                    sourceFileName,
                    $"{platformName}에서 PDF OCR을 실행하려면 pdftoppm, tesseract가 앱 번들/패키지 경로에 필요합니다. 누락: {string.Join(", ", missingTools)}");
            }

                return FailWithLog(
                    sourceFileName,
                    $"필요한 OCR 도구를 찾을 수 없습니다. 누락: {string.Join(", ", missingTools)}");
        }

        ReportProgress(progress, 0, 0, "pdftoppm/tesseract 확인 완료");
        var workDirectory = Path.Combine(Path.GetTempPath(), "PreviousPracticePdfOcr", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDirectory);

        try
        {
            var imageDirectory = PrepareImageDirectory(sourceFileName);
            var imageBase = Path.Combine(workDirectory, "page");
            var renderArgs = $"-png -r 300 {Quote(pdfFilePath)} {Quote(imageBase)}";
            var renderResult = await RunCommandAsync(pdftoppmPath, renderArgs, cancellationToken);

            if (renderResult.ExitCode != 0)
            {
                return FailWithLog(
                    sourceFileName,
                    $"pdftoppm 실행 실패: {renderResult.StdErr.Trim()}");
            }

            var images = Directory.GetFiles(workDirectory, "page-*.png")
                .OrderBy(ExtractPageIndexFromFileName)
                .ThenBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (images.Length == 0)
            {
                return FailWithLog(sourceFileName, "PDF 페이지를 이미지로 변환하지 못했습니다.");
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
                var cachedImagePath = BuildPageImagePath(imageDirectory, i + 1);
                var ocrArgs = $"{Quote(imagePath)} stdout -l kor+eng";
                var ocrResult = await RunCommandAsync(tesseractPath, ocrArgs, cancellationToken);
                if (ocrResult.ExitCode != 0)
                {
                    if (string.IsNullOrWhiteSpace(ocrResult.StdOut))
                    {
                        return FailWithLog(
                            sourceFileName,
                            $"이미지 OCR 실행 실패({imagePath}): {ocrResult.StdErr.Trim()}");
                    }

                    var rawFailedOutput = NormalizeWhitespace(ocrResult.StdOut);
                    if (string.IsNullOrWhiteSpace(rawFailedOutput))
                    {
                        return FailWithLog(
                            sourceFileName,
                            "tesseract 실행 결과가 비정상입니다.");
                    }

                    try
                    {
                        File.Copy(imagePath, cachedImagePath, overwrite: true);
                    }
                    catch
                    {
                    }

                    pages.Add(new OcrPageResult(
                        i + 1,
                        string.Empty,
                        0,
                        0f,
                        File.Exists(cachedImagePath) ? cachedImagePath : null));
                    continue;
                }

                var raw = NormalizeWhitespace(ocrResult.StdOut);
                var wordCount = string.IsNullOrWhiteSpace(raw)
                    ? 0
                    : raw.Split(
                        new[] { '\r', '\n', ' ' },
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;

                try
                {
                    File.Copy(imagePath, cachedImagePath, overwrite: true);
                }
                catch
                {
                }

                pages.Add(new OcrPageResult(
                    i + 1,
                    raw,
                    wordCount,
                    0f,
                    File.Exists(cachedImagePath) ? cachedImagePath : null));
            }

            ReportProgress(progress, totalPages, totalPages, "OCR 분석 완료");
            LogPageSummary(sourceFileName, pages);

            if (!pages.Any(x => !string.IsNullOrWhiteSpace(x.Text)))
            {
                return FailWithLog(sourceFileName, "이미지에서 텍스트를 추출하지 못했습니다.");
            }

            var candidates = OcrQuestionSegmenter.SplitByHeader(pages);
            AppLog.Info(
                nameof(PdfAnalysisService),
                $"OCR 성공 | file={sourceFileName} | pages={pages.Count} | candidates={candidates.Count}");
            return PdfOcrResult.Ok(sourceFileName, pages, candidates);
        }
        catch (OperationCanceledException)
        {
            return FailWithLog(sourceFileName, "OCR 분석이 취소되었습니다.");
        }
        catch (Exception ex)
        {
            return FailWithLog(sourceFileName, $"OCR 처리 중 오류: {ex.Message}", ex);
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
