using System.Buffers.Binary;
using System.Collections.Concurrent;

namespace PreviousPractice.Services;

internal static class PngFileValidator
{
    private const int MaximumCacheEntries = 1024;
    private static readonly ConcurrentDictionary<string, ValidationCacheEntry> ValidationCache =
        new(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);
    private static readonly uint[] CrcTable = BuildCrcTable();

    private static ReadOnlySpan<byte> Signature =>
        [137, 80, 78, 71, 13, 10, 26, 10];

    internal static bool TryValidate(string imagePath, out int width, out int height)
    {
        width = 0;
        height = 0;

        try
        {
            var fullPath = Path.GetFullPath(imagePath);
            var file = new FileInfo(fullPath);
            file.Refresh();
            if (!file.Exists || file.Length <= 0)
            {
                return false;
            }

            var fingerprint = new FileFingerprint(
                file.Length,
                file.LastWriteTimeUtc.Ticks,
                file.CreationTimeUtc.Ticks);
            if (ValidationCache.TryGetValue(fullPath, out var cached) &&
                cached.Fingerprint == fingerprint)
            {
                width = cached.Width;
                height = cached.Height;
                return cached.IsValid;
            }

            var isValid = TryValidateCore(fullPath, out width, out height);
            if (ValidationCache.Count >= MaximumCacheEntries)
            {
                ValidationCache.Clear();
            }

            ValidationCache[fullPath] = new ValidationCacheEntry(
                fingerprint,
                isValid,
                width,
                height);
            return isValid;
        }
        catch (Exception)
        {
            // 읽기, 구조, CRC 검증 중 하나라도 실패하면 표시 가능한 PNG로 보지 않는다.
            width = 0;
            height = 0;
            return false;
        }
    }

    private static bool TryValidateCore(string imagePath, out int width, out int height)
    {
        width = 0;
        height = 0;

        try
        {
            using var stream = File.OpenRead(imagePath);
            Span<byte> signature = stackalloc byte[8];
            if (!TryReadExactly(stream, signature) || !signature.SequenceEqual(Signature))
            {
                return false;
            }

            var hasHeader = false;
            var hasImageData = false;
            var imageDataLength = 0L;
            var isFirstChunk = true;
            Span<byte> chunkHeader = stackalloc byte[8];
            Span<byte> storedCrcBytes = stackalloc byte[4];
            var buffer = new byte[81920];

            while (TryReadExactly(stream, chunkHeader))
            {
                var dataLength = BinaryPrimitives.ReadUInt32BigEndian(chunkHeader[..4]);
                var chunkType = BinaryPrimitives.ReadUInt32BigEndian(chunkHeader[4..]);
                var remainingBytes = stream.Length - stream.Position;
                if ((long)dataLength + storedCrcBytes.Length > remainingBytes)
                {
                    return false;
                }

                const uint headerChunk = 0x49484452; // IHDR
                const uint imageDataChunk = 0x49444154; // IDAT
                const uint endChunk = 0x49454E44; // IEND
                if (isFirstChunk && chunkType != headerChunk)
                {
                    return false;
                }

                if (chunkType == headerChunk &&
                    (!isFirstChunk || hasHeader || dataLength != 13))
                {
                    return false;
                }

                var crc = UpdateCrc(uint.MaxValue, chunkHeader[4..]);
                var bytesRemaining = (long)dataLength;
                var chunkOffset = 0L;
                while (bytesRemaining > 0)
                {
                    var bytesToRead = (int)Math.Min(buffer.Length, bytesRemaining);
                    var chunkBuffer = buffer.AsSpan(0, bytesToRead);
                    if (!TryReadExactly(stream, chunkBuffer))
                    {
                        return false;
                    }

                    if (chunkType == headerChunk && chunkOffset == 0)
                    {
                        width = BinaryPrimitives.ReadInt32BigEndian(chunkBuffer[..4]);
                        height = BinaryPrimitives.ReadInt32BigEndian(chunkBuffer.Slice(4, 4));
                        if (width <= 0 || height <= 0)
                        {
                            return false;
                        }
                    }

                    crc = UpdateCrc(crc, chunkBuffer);
                    chunkOffset += bytesToRead;
                    bytesRemaining -= bytesToRead;
                }

                if (!TryReadExactly(stream, storedCrcBytes))
                {
                    return false;
                }

                var storedCrc = BinaryPrimitives.ReadUInt32BigEndian(storedCrcBytes);
                if (~crc != storedCrc)
                {
                    return false;
                }

                if (chunkType == headerChunk)
                {
                    hasHeader = true;
                }
                else if (chunkType == imageDataChunk)
                {
                    if (!hasHeader)
                    {
                        return false;
                    }

                    hasImageData = true;
                    imageDataLength += dataLength;
                }
                else if (chunkType == endChunk)
                {
                    return dataLength == 0 &&
                           hasHeader &&
                           hasImageData &&
                           imageDataLength > 0 &&
                           stream.Position == stream.Length;
                }

                isFirstChunk = false;
            }
        }
        catch (Exception)
        {
            // 읽기, 구조, CRC 검증 중 하나라도 실패하면 표시 가능한 PNG로 보지 않는다.
        }

        width = 0;
        height = 0;
        return false;
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint value = 0; value < table.Length; value++)
        {
            var crc = value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0
                    ? 0xEDB88320u ^ (crc >> 1)
                    : crc >> 1;
            }

            table[value] = crc;
        }

        return table;
    }

    private static bool TryReadExactly(Stream stream, Span<byte> buffer)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = stream.Read(buffer[totalRead..]);
            if (read <= 0)
            {
                return false;
            }

            totalRead += read;
        }

        return true;
    }

    private readonly record struct FileFingerprint(
        long Length,
        long LastWriteTimeUtcTicks,
        long CreationTimeUtcTicks);

    private sealed record ValidationCacheEntry(
        FileFingerprint Fingerprint,
        bool IsValid,
        int Width,
        int Height);
}
