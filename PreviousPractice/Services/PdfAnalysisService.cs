using System.IO;
using System.Diagnostics;
using System.Text;
using PreviousPractice.Models;

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
    Task<PdfOcrResult> AnalyzePdfAsync(string pdfFilePath, CancellationToken cancellationToken = default);
}

public sealed class PdfAnalysisService : IPdfAnalysisService
{
    public Task<PdfOcrResult> AnalyzePdfAsync(string pdfFilePath, CancellationToken cancellationToken = default)
    {
#if WINDOWS
        return AnalyzePdfWithWindowsOcrAsync(pdfFilePath, cancellationToken);
#else
        return AnalyzePdfWithCommandLineToolsAsync(pdfFilePath, cancellationToken);
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
    private static async Task<PdfOcrResult> AnalyzePdfWithWindowsOcrAsync(string pdfFilePath, CancellationToken cancellationToken = default)
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
            await using var fileStream = await storageFile.OpenReadAsync();
            var pdfDocument = await PdfDocument.LoadFromStreamAsync(fileStream);

            if (pdfDocument.PageCount <= 0)
            {
                return PdfOcrResult.Fail(sourceFileName, "페이지가 없는 PDF 파일입니다.");
            }

            var pages = new List<OcrPageResult>();
            for (uint i = 0; i < pdfDocument.PageCount; i++)
            {
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

                var words = ocrResult.Lines.SelectMany(x => x.Words).ToArray();
                var averageConfidence = words.Length == 0 ? 0f : words.Average(x => x.Confidence);

                pages.Add(new OcrPageResult(
                    (int)i + 1,
                    NormalizeWhitespace(ocrResult.Text),
                    words.Length,
                    averageConfidence));
            }

            if (pages.Count == 0 || !pages.Any(x => !string.IsNullOrWhiteSpace(x.Text)))
            {
                return PdfOcrResult.Fail(sourceFileName, "이미지에서 텍스트를 추출하지 못했습니다.");
            }

            return PdfOcrResult.Ok(sourceFileName, pages);
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

#else
    private static async Task<PdfOcrResult> AnalyzePdfWithCommandLineToolsAsync(string pdfFilePath, CancellationToken cancellationToken = default)
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

            return PdfOcrResult.Fail(
                sourceFileName,
                $"필요한 OCR 도구를 찾을 수 없습니다. 누락: {string.Join(", ", missingTools)}");
        }

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

            var pages = new List<OcrPageResult>();
            for (var i = 0; i < images.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var imagePath = images[i];
                var ocrArgs = $"{Quote(imagePath)} stdout -l kor+eng";
                var ocrResult = await RunCommandAsync(tesseractPath, ocrArgs, cancellationToken);
                if (ocrResult.ExitCode != 0 && string.IsNullOrWhiteSpace(ocrResult.StdOut))
                {
                    pages.Add(new OcrPageResult(i + 1, string.Empty, 0, 0f));
                    continue;
                }

                var raw = NormalizeWhitespace(ocrResult.StdOut);
                var wordCount = string.IsNullOrWhiteSpace(raw)
                    ? 0
                    : raw.Split((char[])['\r', '\n', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;

                pages.Add(new OcrPageResult(i + 1, raw, wordCount, 0f));
            }

            if (!pages.Any(x => !string.IsNullOrWhiteSpace(x.Text)))
            {
                return PdfOcrResult.Fail(sourceFileName, "이미지에서 텍스트를 추출하지 못했습니다.");
            }

            return PdfOcrResult.Ok(sourceFileName, pages);
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

        var paths = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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

        process.Start();

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
