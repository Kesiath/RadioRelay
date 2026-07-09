using System;
using System.Threading;
using System.Threading.Tasks;
using RadioRelay.Shared.Diagnostics;

namespace RadioRelay.Server
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            var log = new LocalLog(LocalLog.DefaultLogDirectory, "RadioRelay-server");
            RegisterExceptionHandlers(log);
            log.LogLifecycle(ErrorCodes.ServerStart, "server process starting");

            const int defaultPort = 2302;
            var settings = ServerCommandLineSettings.Parse(args, defaultPort);
            if (settings.PortFallbackUsed)
            {
                Console.WriteLine(
                    $"[RadioRelay] Couldn't parse a port number from argument(s) \"{string.Join(' ', args)}\" " +
                    $"-- expected a number from 1-65535. Falling back to default port {defaultPort}.");
            }

            var server = new RelayServer(settings.Port, settings.Password, new RelayServerOptions { ServerLog = log });
            log.LogLifecycle(ErrorCodes.ServerStart, $"server configured port={settings.Port} passwordEnabled={!string.IsNullOrEmpty(settings.Password)}");
            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

            var runTask = server.RunAsync(cts.Token);
            _ = RunAdminConsoleAsync(server, cts, log);
            Console.WriteLine($"RadioRelay server running{(string.IsNullOrEmpty(settings.Password) ? "" : " with password enabled")}. Commands: help, clients, kick <client>, stats, banlist, ban <ip>, unban <ip>, quit. Press Ctrl+C to stop.");

            try
            {
                await runTask;
            }
            finally
            {
                log.LogLifecycle(ErrorCodes.ServerStop, "server process stopping");
            }
        }

        private static void RegisterExceptionHandlers(LocalLog log)
        {
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                if (e.ExceptionObject is Exception ex)
                    log.LogException(ErrorCodes.ServerUnhandledException, "unhandled AppDomain exception", ex);
            };
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                log.LogException(ErrorCodes.ServerUnhandledException, "unobserved task exception", e.Exception);
            };
        }

        private static async Task RunAdminConsoleAsync(RelayServer server, CancellationTokenSource cts, LocalLog log)
        {
            var admin = new ServerAdminCommandProcessor(server, Console.Out, log);

            while (!cts.IsCancellationRequested)
            {
                string? line;
                try
                {
                    line = await Task.Run(Console.ReadLine, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (line == null) break;
                if (!admin.TryExecute(line))
                {
                    cts.Cancel();
                    break;
                }
            }
        }
    }
}
