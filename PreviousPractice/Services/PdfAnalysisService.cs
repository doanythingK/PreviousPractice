using System.IO;
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
        var fileName = Path.GetFileName(pdfFilePath);
        return Task.FromResult(
            PdfOcrResult.Fail(
                string.IsNullOrWhiteSpace(fileName) ? "unknown.pdf" : fileName,
                "현재 플랫폼은 문항 PDF의 OCR 추출이 미구현 상태입니다."));
#endif
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
#endif
}
