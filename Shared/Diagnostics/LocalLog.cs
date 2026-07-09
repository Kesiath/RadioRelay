using System;
using System.IO;

namespace RadioRelay.Shared.Diagnostics
{
    public sealed class LocalLog
    {
        private readonly string _baseDirectory;
        private readonly string _filePrefix;
        private readonly Func<DateTime> _clock;

        public LocalLog(string baseDirectory, string filePrefix, Func<DateTime>? clock = null)
        {
            _baseDirectory = baseDirectory ?? throw new ArgumentNullException(nameof(baseDirectory));
            _filePrefix = string.IsNullOrWhiteSpace(filePrefix) ? throw new ArgumentException("File prefix is required.", nameof(filePrefix)) : filePrefix;
            _clock = clock ?? (() => DateTime.UtcNow);
        }

        public string FilePath
        {
            get
            {
                var now = NormalizeUtc(_clock());
                return Path.Combine(_baseDirectory, $"{_filePrefix}-{now:yyyyMMdd}.log");
            }
        }

        public static string DefaultLogDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RadioRelay",
            "logs");

        public void LogLifecycle(string code, string message) =>
            AppendLine($"{Timestamp()} [{code}] {message}");

        public void LogException(string code, string context, Exception exception)
        {
            AppendLine($"{Timestamp()} [{code}] {context}\n{exception.GetType().FullName}: {exception.Message}\n{exception.StackTrace}");
        }

        private string Timestamp() => NormalizeUtc(_clock()).ToString("O");

        private void AppendLine(string line)
        {
            try
            {
                Directory.CreateDirectory(_baseDirectory);
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch
            {
                // Best-effort diagnostics must never crash the app.
            }
        }

        private static DateTime NormalizeUtc(DateTime value) =>
            value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
    }
}
