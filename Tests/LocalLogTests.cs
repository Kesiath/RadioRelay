using System.Diagnostics;
using RadioRelay.Shared.Diagnostics;

namespace RadioRelay.Tests;

public class LocalLogTests
{
    [Fact]
    public void FilePath_uses_base_directory_prefix_and_current_date()
    {
        string directory = CreateTempDirectory();
        var clock = new FixedClock(new DateTime(2026, 7, 8, 21, 30, 0, DateTimeKind.Utc));
        var log = new LocalLog(directory, "RadioRelay-client", clock.Now);

        Assert.Equal(Path.Combine(directory, "RadioRelay-client-20260708.log"), log.FilePath);
    }

    [Fact]
    public void LogLifecycle_appends_timestamped_line()
    {
        string directory = CreateTempDirectory();
        var clock = new FixedClock(new DateTime(2026, 7, 8, 21, 30, 0, DateTimeKind.Utc));
        var log = new LocalLog(directory, "RadioRelay-client", clock.Now);

        log.LogLifecycle(ErrorCodes.ClientAppStart, "app started");

        var text = File.ReadAllText(log.FilePath);
        Assert.Contains("2026-07-08T21:30:00.0000000Z", text);
        Assert.Contains(ErrorCodes.ClientAppStart, text);
        Assert.Contains("app started", text);
    }

    [Fact]
    public void LogException_appends_code_context_type_message_and_stack_trace()
    {
        string directory = CreateTempDirectory();
        var clock = new FixedClock(new DateTime(2026, 7, 8, 21, 30, 0, DateTimeKind.Utc));
        var log = new LocalLog(directory, "RadioRelay-server", clock.Now);

        Exception ex = CaptureException();
        log.LogException(ErrorCodes.ServerUnhandledException, "startup loop", ex);

        var text = File.ReadAllText(log.FilePath);
        Assert.Contains(ErrorCodes.ServerUnhandledException, text);
        Assert.Contains("startup loop", text);
        Assert.Contains(nameof(InvalidOperationException), text);
        Assert.Contains("boom", text);
        Assert.Contains(nameof(CaptureException), text);
    }

    [Fact]
    public void Logging_swallows_file_io_failures()
    {
        string fileAsDirectory = Path.Combine(Path.GetTempPath(), "RadioRelayTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(fileAsDirectory)!);
        File.WriteAllText(fileAsDirectory, "not a directory");
        var log = new LocalLog(fileAsDirectory, "RadioRelay-client", () => DateTime.UtcNow);

        var exception = Record.Exception(() => log.LogLifecycle(ErrorCodes.ClientAppStart, "app started"));

        Assert.Null(exception);
    }

    [Fact]
    public void Error_codes_are_unique()
    {
        var codes = ErrorCodes.All;
        Assert.Equal(codes.Length, codes.Distinct(StringComparer.Ordinal).Count());
    }

    private static Exception CaptureException()
    {
        try
        {
            ThrowBoom();
            throw new UnreachableException();
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static void ThrowBoom() => throw new InvalidOperationException("boom");

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RadioRelayTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class FixedClock
    {
        private readonly DateTime _now;
        public FixedClock(DateTime now) => _now = now;
        public DateTime Now() => _now;
    }
}
