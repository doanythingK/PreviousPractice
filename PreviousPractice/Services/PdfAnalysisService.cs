using System.Collections.Concurrent;
using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PreviousPractice.Models;
using PreviousPractice.Infrastructure;

#if ANDROID
using Android.Runtime;
using AndroidBitmap = Android.Graphics.Bitmap;
using AndroidCanvas = Android.Graphics.Canvas;
using AndroidColor = Android.Graphics.Color;
using AndroidMatrix = Android.Graphics.Matrix;
using AndroidParcelFileDescriptor = Android.OS.ParcelFileDescriptor;
using AndroidParcelFileMode = Android.OS.ParcelFileMode;
using AndroidPdfRenderer = Android.Graphics.Pdf.PdfRenderer;
using AndroidPdfRenderMode = Android.Graphics.Pdf.PdfRenderMode;
using AndroidTask = Android.Gms.Tasks.Task;
using JavaException = Java.Lang.Exception;
using JavaFile = Java.IO.File;
using MlKitInputImage = Xamarin.Google.MLKit.Vision.Common.InputImage;
using MlKitText = Xamarin.Google.MLKit.Vision.Text.Text;
using MlKitTextRecognition = Xamarin.Google.MLKit.Vision.Text.TextRecognition;
using MlKitKoreanTextRecognizerOptions = Xamarin.Google.MLKit.Vision.Text.Korean.KoreanTextRecognizerOptions;
#endif
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
        QuestionNumberRange? expectedQuestionRange = null,
        CancellationToken cancellationToken = default);
}

public sealed record PdfAnalysisProgress(int ProcessedPages, int TotalPages, string Message);

public sealed class PdfAnalysisService : IPdfAnalysisService
{
    private const string ImageCacheRoot = "QuestionSourceImageCache";
    private static readonly TimeSpan CommandExecutionTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ProcessTerminationGracePeriod = TimeSpan.FromSeconds(10);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> SourceCacheLocks =
        new(StringComparer.Ordinal);

    public Task<PdfOcrResult> AnalyzePdfAsync(
        string pdfFilePath,
        IProgress<PdfAnalysisProgress>? progress = null,
        QuestionNumberRange? expectedQuestionRange = null,
        CancellationToken cancellationToken = default)
    {
        AppLog.Info(
            nameof(PdfAnalysisService),
            $"AnalyzePdfAsync 시작 | file={pdfFilePath} | os={Environment.OSVersion.Platform}");
#if WINDOWS
        return AnalyzePdfWithWindowsOcrAsync(pdfFilePath, progress, expectedQuestionRange, cancellationToken);
#elif ANDROID
        // PdfRenderer, bitmap encoding, and content hashing are blocking operations.
        // Keep all Android analysis work off the caller's (normally UI) context. A
        // Progress<T> supplied by the view model still marshals reports to the UI.
        return Task.Run(
            () => AnalyzePdfWithAndroidMlKitAsync(
                pdfFilePath,
                progress,
                expectedQuestionRange,
                cancellationToken),
            cancellationToken);
#elif IOS
        var sourceFileName = string.IsNullOrWhiteSpace(pdfFilePath)
            ? "unknown.pdf"
            : Path.GetFileName(pdfFilePath);
        return Task.FromResult(FailWithLog(
            sourceFileName,
            "iOS에서는 외부 OCR 명령행 도구 실행을 지원하지 않습니다. iOS OCR은 Vision/MLKit 등 플랫폼 전용 어댑터 구현이 필요합니다."));
#else
        return AnalyzePdfWithCommandLineToolsAsync(pdfFilePath, progress, expectedQuestionRange, cancellationToken);
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

    private static int CountWords(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        return text.Split(
            new[] { '\r', '\n', ' ' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
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

    private static ImageCacheTarget PrepareImageDirectory(string pdfFilePath, string sourceFileName)
    {
        var baseDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PreviousPractice",
            ImageCacheRoot);

        Directory.CreateDirectory(baseDirectory);

        var safeName = GetSafeFileNameWithoutExtension(sourceFileName);
        var sourceKey = BuildSourceCacheKey(pdfFilePath);
        var imageDirectory = Path.Combine(baseDirectory, safeName, sourceKey);
        Directory.CreateDirectory(imageDirectory);

        return new ImageCacheTarget(imageDirectory, sourceKey);
    }

    private static async Task<SourceCacheLease> AcquireSourceCacheLeaseAsync(
        string sourceKey,
        CancellationToken cancellationToken)
    {
        var gate = SourceCacheLocks.GetOrAdd(sourceKey, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new SourceCacheLease(gate);
    }

    private static string BuildSourceCacheKey(string pdfFilePath)
    {
        try
        {
            using var stream = File.OpenRead(pdfFilePath);
            var hash = SHA256.HashData(stream);
            return Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant();
        }
        catch (Exception ex)
        {
            var fileInfo = new FileInfo(pdfFilePath);
            var fallbackIdentity = string.Join(
                "|",
                Path.GetFullPath(pdfFilePath),
                fileInfo.Exists ? fileInfo.Length : 0L,
                fileInfo.Exists ? fileInfo.LastWriteTimeUtc.Ticks : 0L);
            var fallbackHash = SHA256.HashData(Encoding.UTF8.GetBytes(fallbackIdentity));
            AppLog.Error(
                nameof(PdfAnalysisService),
                $"PDF 콘텐츠 해시 계산 실패로 경로/메타데이터 키를 사용합니다. file={pdfFilePath}",
                ex);
            return Convert.ToHexString(fallbackHash.AsSpan(0, 12)).ToLowerInvariant();
        }
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

    private static async Task<bool> TryPersistPngAtomicallyAsync(
        string destinationPath,
        int expectedWidth,
        int expectedHeight,
        Func<string, Task> writeAsync)
    {
        var temporaryPath = BuildTemporaryImagePath(destinationPath);
        try
        {
            await writeAsync(temporaryPath).ConfigureAwait(false);
            return CommitValidatedPng(temporaryPath, destinationPath, expectedWidth, expectedHeight);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Error(
                nameof(PdfAnalysisService),
                $"페이지 이미지 캐시 저장 실패 | path={destinationPath}",
                ex);
            return false;
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private static bool TryPersistPngAtomically(
        string destinationPath,
        int expectedWidth,
        int expectedHeight,
        Action<string> write)
    {
        var temporaryPath = BuildTemporaryImagePath(destinationPath);
        try
        {
            write(temporaryPath);
            return CommitValidatedPng(temporaryPath, destinationPath, expectedWidth, expectedHeight);
        }
        catch (Exception ex)
        {
            AppLog.Error(
                nameof(PdfAnalysisService),
                $"페이지 이미지 캐시 저장 실패 | path={destinationPath}",
                ex);
            return false;
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private static string BuildTemporaryImagePath(string destinationPath)
    {
        var directory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException("페이지 이미지 캐시 폴더를 확인할 수 없습니다.");
        var fileName = Path.GetFileName(destinationPath);
        return Path.Combine(directory, $".{fileName}.{Guid.NewGuid():N}.tmp");
    }

    private static bool CommitValidatedPng(
        string temporaryPath,
        string destinationPath,
        int expectedWidth,
        int expectedHeight)
    {
        if (!TryValidatePngFile(temporaryPath, out var actualWidth, out var actualHeight))
        {
            throw new InvalidDataException("렌더링된 페이지 이미지가 완전한 PNG 파일이 아닙니다.");
        }

        if ((expectedWidth > 0 && actualWidth != expectedWidth) ||
            (expectedHeight > 0 && actualHeight != expectedHeight))
        {
            throw new InvalidDataException(
                $"렌더링된 페이지 이미지 크기가 예상과 다릅니다. expected={expectedWidth}x{expectedHeight}, actual={actualWidth}x{actualHeight}");
        }

        // The temporary file is created beside the destination, so rename/replace
        // is an atomic publication on all supported file systems.
        File.Move(temporaryPath, destinationPath, overwrite: true);
        return true;
    }

    private static bool TryValidatePngFile(string imagePath, out int width, out int height)
        => PngFileValidator.TryValidate(imagePath, out width, out height);

    private static bool TryReadPngDimensions(string imagePath, out int width, out int height)
    {
        return TryValidatePngFile(imagePath, out width, out height);
    }

    private static void TryDeleteFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch
        {
        }
    }

    private sealed record ImageCacheTarget(string DirectoryPath, string SourceKey);

    private sealed class SourceCacheLease : IDisposable
    {
        private SemaphoreSlim? gate;

        public SourceCacheLease(SemaphoreSlim gate)
        {
            this.gate = gate;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref gate, null)?.Release();
        }
    }

#if WINDOWS
    private static async Task SaveImageStreamAsync(IRandomAccessStream imageStream, string destinationPath)
    {
        imageStream.Seek(0);
        using var destination = File.Create(destinationPath);
        await imageStream.AsStreamForRead().CopyToAsync(destination);
    }

    private static IReadOnlyList<OcrLineResult> BuildLineResults(
        OcrResult ocrResult,
        int imagePixelWidth,
        int imagePixelHeight)
    {
        if (ocrResult == null || imagePixelWidth <= 0 || imagePixelHeight <= 0)
        {
            return Array.Empty<OcrLineResult>();
        }

        var results = new List<OcrLineResult>();
        var fallbackLineCount = Math.Max(1, ocrResult.Lines.Count);

        for (var i = 0; i < ocrResult.Lines.Count; i++)
        {
            var line = ocrResult.Lines[i];
            var text = line.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var hasReliableGeometry = TryGetLineBounds(
                line,
                out var left,
                out var top,
                out var right,
                out var bottom);
            if (!hasReliableGeometry)
            {
                left = 0d;
                right = 1d;
                top = (double)i / fallbackLineCount;
                bottom = (double)(i + 1) / fallbackLineCount;
            }

            results.Add(new OcrLineResult(
                i + 1,
                text,
                Math.Clamp(left / imagePixelWidth, 0d, 1d),
                Math.Clamp(top / imagePixelHeight, 0d, 1d),
                Math.Clamp(right / imagePixelWidth, 0d, 1d),
                Math.Clamp(bottom / imagePixelHeight, 0d, 1d),
                hasReliableGeometry));
        }

        return results;
    }

    private static bool TryGetLineBounds(
        OcrLine line,
        out double left,
        out double top,
        out double right,
        out double bottom)
    {
        left = 0d;
        top = 0d;
        right = 0d;
        bottom = 0d;

        if (line?.Words == null || line.Words.Count == 0)
        {
            return false;
        }

        var hasBounds = false;
        foreach (var word in line.Words)
        {
            var rect = word.BoundingRect;
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                continue;
            }

            if (!hasBounds)
            {
                left = rect.X;
                top = rect.Y;
                right = rect.X + rect.Width;
                bottom = rect.Y + rect.Height;
                hasBounds = true;
                continue;
            }

            left = Math.Min(left, rect.X);
            top = Math.Min(top, rect.Y);
            right = Math.Max(right, rect.X + rect.Width);
            bottom = Math.Max(bottom, rect.Y + rect.Height);
        }

        return hasBounds;
    }
#endif

#if WINDOWS
    private static async Task<PdfOcrResult> AnalyzePdfWithWindowsOcrAsync(
        string pdfFilePath,
        IProgress<PdfAnalysisProgress>? progress,
        QuestionNumberRange? expectedQuestionRange,
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
            var imageCache = PrepareImageDirectory(pdfFilePath, sourceFileName);
            using var cacheLease = await AcquireSourceCacheLeaseAsync(
                imageCache.SourceKey,
                cancellationToken).ConfigureAwait(false);
            var imageDirectory = imageCache.DirectoryPath;
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
                var imagePersisted = await TryPersistPngAtomicallyAsync(
                    imagePath,
                    softwareBitmap.PixelWidth,
                    softwareBitmap.PixelHeight,
                    temporaryPath => SaveImageStreamAsync(imageStream, temporaryPath));

                var recognizedText = NormalizeWhitespace(ocrResult.Text);
                var wordCount = CountWords(recognizedText);
                var averageConfidence = wordCount == 0 ? 0f : 100f;

                pages.Add(new OcrPageResult(
                    (int)i + 1,
                    recognizedText,
                    wordCount,
                    averageConfidence,
                    imagePersisted ? imagePath : null,
                    softwareBitmap.PixelWidth,
                    softwareBitmap.PixelHeight,
                    BuildLineResults(ocrResult, softwareBitmap.PixelWidth, softwareBitmap.PixelHeight)));
            }

            ReportProgress(progress, totalPages, totalPages, "OCR 분석 완료");
            LogPageSummary(sourceFileName, pages);

            if (pages.Count == 0 || !pages.Any(x => !string.IsNullOrWhiteSpace(x.Text)))
            {
                return FailWithLog(sourceFileName, "이미지에서 텍스트를 추출하지 못했습니다.");
            }

            var candidates = OcrQuestionSegmenter.SplitByHeader(pages, expectedQuestionRange);
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

#elif ANDROID
    private const int AndroidPdfRenderScale = 4;
    private const int AndroidMaxRenderedDimension = 3600;

    private static async Task<PdfOcrResult> AnalyzePdfWithAndroidMlKitAsync(
        string pdfFilePath,
        IProgress<PdfAnalysisProgress>? progress,
        QuestionNumberRange? expectedQuestionRange,
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
        AndroidParcelFileDescriptor? descriptor = null;
        AndroidPdfRenderer? pdfRenderer = null;
        Xamarin.Google.MLKit.Vision.Text.ITextRecognizer? recognizer = null;

        try
        {
            var imageCache = PrepareImageDirectory(pdfFilePath, sourceFileName);
            using var cacheLease = await AcquireSourceCacheLeaseAsync(
                imageCache.SourceKey,
                cancellationToken).ConfigureAwait(false);
            var imageDirectory = imageCache.DirectoryPath;
            descriptor = AndroidParcelFileDescriptor.Open(new JavaFile(pdfFilePath), AndroidParcelFileMode.ReadOnly);
            if (descriptor == null)
            {
                return FailWithLog(sourceFileName, "Android에서 PDF 파일을 열 수 없습니다.");
            }

            pdfRenderer = new AndroidPdfRenderer(descriptor);

            if (pdfRenderer.PageCount <= 0)
            {
                return FailWithLog(sourceFileName, "페이지가 없는 PDF 파일입니다.");
            }

            var totalPages = pdfRenderer.PageCount;
            ReportProgress(progress, 0, totalPages, "Android 고해상도 PDF 렌더링/OCR 시작");

            var options = new MlKitKoreanTextRecognizerOptions.Builder().Build();
            recognizer = MlKitTextRecognition.GetClient(options);

            var pages = new List<OcrPageResult>();
            for (var i = 0; i < totalPages; i++)
            {
                ReportProgress(
                    progress,
                    i,
                    totalPages,
                    $"페이지 {i + 1}/{totalPages} Android OCR 분석 중");

                cancellationToken.ThrowIfCancellationRequested();

                using var bitmap = RenderAndroidPdfPageToBitmap(
                    pdfRenderer,
                    i,
                    out var imageWidth,
                    out var imageHeight);

                var cachedImagePath = BuildPageImagePath(imageDirectory, i + 1);
                var imagePersisted = TryPersistPngAtomically(
                    cachedImagePath,
                    imageWidth,
                    imageHeight,
                    temporaryPath => SaveAndroidBitmapAsPng(bitmap, temporaryPath));

                cancellationToken.ThrowIfCancellationRequested();
                using var inputImage = MlKitInputImage.FromBitmap(bitmap, 0);
                using var ocrText = await AwaitAndroidTaskAsync<MlKitText>(
                    recognizer.Process(inputImage),
                    cancellationToken).ConfigureAwait(false);

                var lineResults = BuildAndroidLineResults(ocrText, imageWidth, imageHeight);
                var recognizedText = NormalizeWhitespace(string.Join("\n", lineResults.Select(x => x.Text)));
                var wordCount = CountWords(recognizedText);
                pages.Add(new OcrPageResult(
                    i + 1,
                    recognizedText,
                    wordCount,
                    wordCount == 0 ? 0f : 100f,
                    imagePersisted ? cachedImagePath : null,
                    imageWidth,
                    imageHeight,
                    lineResults));
            }

            ReportProgress(progress, totalPages, totalPages, "OCR 분석 완료");
            LogPageSummary(sourceFileName, pages);

            if (!pages.Any(x => !string.IsNullOrWhiteSpace(x.Text)))
            {
                return FailWithLog(sourceFileName, "이미지에서 텍스트를 추출하지 못했습니다.");
            }

            var candidates = OcrQuestionSegmenter.SplitByHeader(pages, expectedQuestionRange);
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
            return FailWithLog(sourceFileName, $"Android OCR 처리 중 오류: {ex.Message}", ex);
        }
        finally
        {
            try
            {
                recognizer?.Close();
            }
            catch
            {
            }

            pdfRenderer?.Dispose();
            descriptor?.Dispose();
        }
    }

    private static (int Width, int Height) GetAndroidRenderSize(int pageWidth, int pageHeight)
    {
        var maxPageDimension = Math.Max(pageWidth, pageHeight);
        if (maxPageDimension <= 0)
        {
            return (1, 1);
        }

        var scale = Math.Min(AndroidPdfRenderScale, (double)AndroidMaxRenderedDimension / maxPageDimension);
        scale = Math.Max(1d / maxPageDimension, scale);

        return (
            Math.Max(1, (int)Math.Round(pageWidth * scale)),
            Math.Max(1, (int)Math.Round(pageHeight * scale)));
    }

    private static AndroidBitmap RenderAndroidPdfPageToBitmap(
        AndroidPdfRenderer pdfRenderer,
        int pageIndex,
        out int imageWidth,
        out int imageHeight)
    {
        var page = pdfRenderer.OpenPage(pageIndex);
        AndroidBitmap? bitmap = null;

        try
        {
            (imageWidth, imageHeight) = GetAndroidRenderSize(page.Width, page.Height);
            bitmap = AndroidBitmap.CreateBitmap(imageWidth, imageHeight, AndroidBitmap.Config.Argb8888!);

            using var canvas = new AndroidCanvas(bitmap);
            using var matrix = new AndroidMatrix();

            canvas.DrawColor(AndroidColor.White);
            matrix.SetScale((float)imageWidth / page.Width, (float)imageHeight / page.Height);
            page.Render(bitmap, null, matrix, AndroidPdfRenderMode.ForPrint);
            return bitmap;
        }
        catch
        {
            bitmap?.Dispose();
            throw;
        }
        finally
        {
            try
            {
                page.Close();
            }
            catch
            {
            }

            page.Dispose();
        }
    }

    private static void SaveAndroidBitmapAsPng(AndroidBitmap bitmap, string destinationPath)
    {
        using var stream = File.Create(destinationPath);
        if (!bitmap.Compress(AndroidBitmap.CompressFormat.Png!, 100, stream))
        {
            throw new IOException("렌더링된 PDF 페이지 이미지를 저장하지 못했습니다.");
        }
    }

    private static IReadOnlyList<OcrLineResult> BuildAndroidLineResults(
        MlKitText ocrText,
        int imagePixelWidth,
        int imagePixelHeight)
    {
        var lines = new List<OcrLineResult>();
        var lineIndex = 1;

        foreach (var blockObject in ocrText.TextBlocks)
        {
            if (blockObject is not Java.Lang.Object javaBlock)
            {
                continue;
            }

            var block = javaBlock.JavaCast<MlKitText.TextBlock>();
            foreach (var lineObject in block.Lines)
            {
                if (lineObject is not Java.Lang.Object javaLine)
                {
                    continue;
                }

                var line = javaLine.JavaCast<MlKitText.Line>();
                var text = line.Text?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                var bounds = line.BoundingBox;
                if (bounds == null || imagePixelWidth <= 0 || imagePixelHeight <= 0)
                {
                    lines.Add(new OcrLineResult(
                        lineIndex++,
                        text,
                        0d,
                        0d,
                        1d,
                        1d,
                        HasReliableGeometry: false));
                    continue;
                }

                lines.Add(new OcrLineResult(
                    lineIndex++,
                    text,
                    Math.Clamp((double)bounds.Left / imagePixelWidth, 0d, 1d),
                    Math.Clamp((double)bounds.Top / imagePixelHeight, 0d, 1d),
                    Math.Clamp((double)bounds.Right / imagePixelWidth, 0d, 1d),
                    Math.Clamp((double)bounds.Bottom / imagePixelHeight, 0d, 1d)));
            }
        }

        return lines;
    }

    private static async Task<T> AwaitAndroidTaskAsync<T>(
        AndroidTask task,
        CancellationToken cancellationToken)
        where T : Java.Lang.Object
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var successListener = new AndroidTaskSuccessListener<T>(completion);
        var failureListener = new AndroidTaskFailureListener<T>(completion);
        var canceledListener = new AndroidTaskCanceledListener<T>(completion);

        try
        {
            task.AddOnSuccessListener(successListener);
            task.AddOnFailureListener(failureListener);
            task.AddOnCanceledListener(canceledListener);

            // A .NET CancellationToken cannot stop ML Kit's native Process task.
            // Wait for that task to finish before the caller disposes its bitmap,
            // InputImage, or recognizer, then honor the user's cancellation.
            var result = await completion.Task.ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                result.Dispose();
                throw new OperationCanceledException(cancellationToken);
            }

            return result;
        }
        finally
        {
            successListener.Dispose();
            failureListener.Dispose();
            canceledListener.Dispose();
        }
    }

    private sealed class AndroidTaskSuccessListener<T> : Java.Lang.Object, Android.Gms.Tasks.IOnSuccessListener
        where T : Java.Lang.Object
    {
        private readonly TaskCompletionSource<T> completion;

        public AndroidTaskSuccessListener(TaskCompletionSource<T> completion)
        {
            this.completion = completion;
        }

        public void OnSuccess(Java.Lang.Object? result)
        {
            if (result == null)
            {
                completion.TrySetException(new InvalidOperationException("Android OCR 결과가 비어 있습니다."));
                return;
            }

            completion.TrySetResult(result.JavaCast<T>());
        }
    }

    private sealed class AndroidTaskFailureListener<T> : Java.Lang.Object, Android.Gms.Tasks.IOnFailureListener
        where T : Java.Lang.Object
    {
        private readonly TaskCompletionSource<T> completion;

        public AndroidTaskFailureListener(TaskCompletionSource<T> completion)
        {
            this.completion = completion;
        }

        public void OnFailure(JavaException exception)
        {
            completion.TrySetException(new InvalidOperationException(
                exception.Message ?? "Android OCR 실행에 실패했습니다.",
                exception));
        }
    }

    private sealed class AndroidTaskCanceledListener<T> : Java.Lang.Object, Android.Gms.Tasks.IOnCanceledListener
        where T : Java.Lang.Object
    {
        private readonly TaskCompletionSource<T> completion;

        public AndroidTaskCanceledListener(TaskCompletionSource<T> completion)
        {
            this.completion = completion;
        }

        public void OnCanceled()
        {
            completion.TrySetCanceled();
        }
    }

#elif !IOS && !ANDROID
    private static async Task<PdfOcrResult> AnalyzePdfWithCommandLineToolsAsync(
        string pdfFilePath,
        IProgress<PdfAnalysisProgress>? progress,
        QuestionNumberRange? expectedQuestionRange,
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
            var imageCache = PrepareImageDirectory(pdfFilePath, sourceFileName);
            using var cacheLease = await AcquireSourceCacheLeaseAsync(
                imageCache.SourceKey,
                cancellationToken).ConfigureAwait(false);
            var imageDirectory = imageCache.DirectoryPath;
            var imageBase = Path.Combine(workDirectory, "page");
            var renderResult = await RunCommandAsync(
                pdftoppmPath,
                new[] { "-png", "-r", "300", pdfFilePath, imageBase },
                cancellationToken);

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
                var ocrResult = await RunCommandAsync(
                    tesseractPath,
                    new[] { imagePath, "stdout", "-l", "kor+eng", "tsv" },
                    cancellationToken);

                var imagePersisted = TryReadPngDimensions(imagePath, out var imageWidth, out var imageHeight) &&
                    TryPersistPngAtomically(
                        cachedImagePath,
                        imageWidth,
                        imageHeight,
                        temporaryPath => File.Copy(imagePath, temporaryPath, overwrite: false));
                var persistedImagePath = imagePersisted ? cachedImagePath : null;
                if (ocrResult.ExitCode == 0 &&
                    TryBuildTesseractTsvPageResult(
                        i + 1,
                        ocrResult.StdOut,
                        imagePath,
                        persistedImagePath,
                        out var pageResult))
                {
                    pages.Add(pageResult);
                    continue;
                }

                var plainTextResult = await RunCommandAsync(
                    tesseractPath,
                    new[] { imagePath, "stdout", "-l", "kor+eng" },
                    cancellationToken);
                var raw = NormalizeWhitespace(plainTextResult.StdOut);
                if (plainTextResult.ExitCode != 0)
                {
                    var error = string.IsNullOrWhiteSpace(plainTextResult.StdErr)
                        ? ocrResult.StdErr
                        : plainTextResult.StdErr;
                    return FailWithLog(
                        sourceFileName,
                        $"이미지 OCR 실행 실패({imagePath}): {error.Trim()}");
                }

                if (string.IsNullOrWhiteSpace(raw))
                {
                    pages.Add(new OcrPageResult(
                        i + 1,
                        string.Empty,
                        0,
                        0f,
                        persistedImagePath,
                        imageWidth,
                        imageHeight,
                        Array.Empty<OcrLineResult>()));
                    continue;
                }

                pages.Add(new OcrPageResult(
                    i + 1,
                    raw,
                    CountWords(raw),
                    0f,
                    persistedImagePath,
                    imageWidth,
                    imageHeight));
            }

            ReportProgress(progress, totalPages, totalPages, "OCR 분석 완료");
            LogPageSummary(sourceFileName, pages);

            if (!pages.Any(x => !string.IsNullOrWhiteSpace(x.Text)))
            {
                return FailWithLog(sourceFileName, "이미지에서 텍스트를 추출하지 못했습니다.");
            }

            var candidates = OcrQuestionSegmenter.SplitByHeader(pages, expectedQuestionRange);
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

    private static bool TryBuildTesseractTsvPageResult(
        int pageIndex,
        string tsv,
        string renderedImagePath,
        string? persistedImagePath,
        out OcrPageResult pageResult)
    {
        pageResult = null!;
        if (string.IsNullOrWhiteSpace(tsv) ||
            !TryReadPngDimensions(renderedImagePath, out var imageWidth, out var imageHeight))
        {
            return false;
        }

        var words = new List<TesseractWord>();
        var rows = tsv.Replace("\r", string.Empty).Split('\n');
        for (var rowIndex = 1; rowIndex < rows.Length; rowIndex++)
        {
            var columns = rows[rowIndex].Split('\t');
            if (columns.Length < 12 ||
                !int.TryParse(columns[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var level) ||
                level != 5 ||
                !int.TryParse(columns[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var blockNumber) ||
                !int.TryParse(columns[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var paragraphNumber) ||
                !int.TryParse(columns[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var lineNumber) ||
                !int.TryParse(columns[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var left) ||
                !int.TryParse(columns[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out var top) ||
                !int.TryParse(columns[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) ||
                !int.TryParse(columns[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height))
            {
                continue;
            }

            var text = columns[11].Trim();
            if (string.IsNullOrWhiteSpace(text) || width <= 0 || height <= 0)
            {
                continue;
            }

            _ = float.TryParse(
                columns[10],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var confidence);
            words.Add(new TesseractWord(
                rowIndex,
                blockNumber,
                paragraphNumber,
                lineNumber,
                left,
                top,
                width,
                height,
                confidence,
                text));
        }

        if (words.Count == 0)
        {
            return false;
        }

        var groupedLines = words
            .GroupBy(word => (word.BlockNumber, word.ParagraphNumber, word.LineNumber))
            .OrderBy(group => group.Min(word => word.RowIndex))
            .ToArray();
        var lineResults = new List<OcrLineResult>(groupedLines.Length);
        var lineIndex = 1;
        foreach (var group in groupedLines)
        {
            var orderedWords = group
                .OrderBy(word => word.Left)
                .ThenBy(word => word.RowIndex)
                .ToArray();
            var text = string.Join(" ", orderedWords.Select(word => word.Text));
            var left = orderedWords.Min(word => word.Left);
            var top = orderedWords.Min(word => word.Top);
            var right = orderedWords.Max(word => word.Left + word.Width);
            var bottom = orderedWords.Max(word => word.Top + word.Height);
            lineResults.Add(new OcrLineResult(
                lineIndex++,
                text,
                Math.Clamp((double)left / imageWidth, 0d, 1d),
                Math.Clamp((double)top / imageHeight, 0d, 1d),
                Math.Clamp((double)right / imageWidth, 0d, 1d),
                Math.Clamp((double)bottom / imageHeight, 0d, 1d)));
        }

        var recognizedText = NormalizeWhitespace(string.Join("\n", lineResults.Select(line => line.Text)));
        if (string.IsNullOrWhiteSpace(recognizedText))
        {
            return false;
        }

        var validConfidences = words
            .Where(word => word.Confidence >= 0f)
            .Select(word => word.Confidence)
            .ToArray();
        var averageConfidence = validConfidences.Length == 0
            ? 0f
            : validConfidences.Average();
        pageResult = new OcrPageResult(
            pageIndex,
            recognizedText,
            words.Count,
            averageConfidence,
            persistedImagePath,
            imageWidth,
            imageHeight,
            lineResults);
        return true;
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
            Environment.CurrentDirectory
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
            foreach (var toolsDirectoryName in new[] { "Tools", "tools" })
            {
                paths.Add(Path.Combine(baseDirectory, toolsDirectoryName));
                paths.Add(Path.Combine(baseDirectory, toolsDirectoryName, "linux"));
                paths.Add(Path.Combine(baseDirectory, toolsDirectoryName, "mac"));
                paths.Add(Path.Combine(baseDirectory, toolsDirectoryName, "android"));

                if (OperatingSystem.IsWindows())
                {
                    paths.Add(Path.Combine(baseDirectory, toolsDirectoryName, "windows"));
                }
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
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = commandPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

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
        var outputCompletion = Task.WhenAll(stdOutTask, stdErrTask);
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(CommandExecutionTimeout);
        try
        {
            await process.WaitForExitAsync(timeoutCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await TerminateProcessAndObserveOutputAsync(
                process,
                outputCompletion).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            throw new TimeoutException(
                $"외부 OCR 도구가 제한 시간 {CommandExecutionTimeout.TotalMinutes:0}분 안에 종료되지 않았습니다: {commandPath}");
        }

        try
        {
            await outputCompletion.WaitAsync(ProcessTerminationGracePeriod).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            _ = outputCompletion.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw new TimeoutException(
                $"외부 OCR 도구 종료 후 출력 스트림 정리가 지연되었습니다: {commandPath}",
                ex);
        }

        return new CommandExecutionResult(process.ExitCode, stdOutTask.Result, stdErrTask.Result);
    }

    private static async Task TerminateProcessAndObserveOutputAsync(
        Process process,
        Task outputCompletion)
    {
        RequestProcessTermination(process, entireProcessTree: true);
        var hasExited = await WaitForProcessExitAsync(
            process,
            ProcessTerminationGracePeriod).ConfigureAwait(false);
        if (!hasExited)
        {
            RequestProcessTermination(process, entireProcessTree: false);
            _ = await WaitForProcessExitAsync(
                process,
                ProcessTerminationGracePeriod).ConfigureAwait(false);
        }

        try
        {
            await outputCompletion.WaitAsync(ProcessTerminationGracePeriod).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Disposing Process closes redirected streams. Observe a later fault so
            // it cannot become an unobserved task exception after this method exits.
            _ = outputCompletion.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        catch
        {
            // The process has already been terminated. Reading a closed pipe may
            // fault; observing it is sufficient and must not mask cancellation.
            _ = outputCompletion.Exception;
        }
    }

    private static async Task<bool> WaitForProcessExitAsync(Process process, TimeSpan timeout)
    {
        if (HasProcessExited(process))
        {
            return true;
        }

        using var timeoutCancellation = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCancellation.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested)
        {
            return HasProcessExited(process);
        }
        catch
        {
            return HasProcessExited(process);
        }
    }

    private static void RequestProcessTermination(Process process, bool entireProcessTree)
    {
        if (HasProcessExited(process))
        {
            return;
        }

        try
        {
            process.Kill(entireProcessTree);
            return;
        }
        catch when (entireProcessTree)
        {
            // Some Mac runtimes do not support tree termination. Fall back to
            // terminating the direct child instead of leaving it orphaned.
        }
        catch
        {
            return;
        }

        try
        {
            if (!HasProcessExited(process))
            {
                process.Kill();
            }
        }
        catch
        {
        }
    }

    private static bool HasProcessExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch
        {
            return true;
        }
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

    private sealed record CommandExecutionResult(int ExitCode, string StdOut, string StdErr);

    private sealed record TesseractWord(
        int RowIndex,
        int BlockNumber,
        int ParagraphNumber,
        int LineNumber,
        int Left,
        int Top,
        int Width,
        int Height,
        float Confidence,
        string Text);
#endif
}
