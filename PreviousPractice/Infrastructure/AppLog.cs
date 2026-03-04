using System.Text;

namespace PreviousPractice.Infrastructure;

public static class AppLog
{
    private static readonly object Sync = new();

    public static string LogDirectoryPath
    {
        get
        {
            var overridePath = Environment.GetEnvironmentVariable("PREVIOUS_PRACTICE_LOG_DIR");
            if (!string.IsNullOrWhiteSpace(overridePath))
            {
                return overridePath.Trim();
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PreviousPractice",
                "logs");
        }
    }

    public static string CurrentLogFilePath
        => Path.Combine(LogDirectoryPath, $"app-{DateTime.Now:yyyyMMdd}.log");

    public static void Info(string scope, string message)
    {
        Write("INFO", scope, message, null);
    }

    public static void Error(string scope, string message, Exception? exception = null)
    {
        Write("ERROR", scope, message, exception);
    }

    private static void Write(string level, string scope, string message, Exception? exception)
    {
        try
        {
            var directory = LogDirectoryPath;
            Directory.CreateDirectory(directory);

            var now = DateTimeOffset.Now;
            var line = new StringBuilder()
                .Append('[').Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz")).Append("] ")
                .Append(level).Append(" [").Append(scope).Append("] ")
                .Append(message ?? string.Empty)
                .ToString();

            if (exception != null)
            {
                line += Environment.NewLine + exception;
            }

            lock (Sync)
            {
                File.AppendAllText(CurrentLogFilePath, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // 로깅 실패는 기능 동작을 막지 않음
        }
    }
}
