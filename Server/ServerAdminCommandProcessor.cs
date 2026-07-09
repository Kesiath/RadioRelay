using RadioRelay.Shared.Diagnostics;
using System;
using System.IO;
using System.Linq;
using System.Net;

namespace RadioRelay.Server
{
    public sealed class ServerAdminCommandProcessor
    {
        private readonly RelayServer _server;
        private readonly TextWriter _output;
        private readonly LocalLog? _log;

        public ServerAdminCommandProcessor(RelayServer server, TextWriter output, LocalLog? log = null)
        {
            _server = server;
            _output = output;
            _log = log;
        }

        public bool TryExecute(string? commandLine)
        {
            if (string.IsNullOrWhiteSpace(commandLine)) return true;

            var parts = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var command = parts[0].ToLowerInvariant();
            _log?.LogLifecycle(ErrorCodes.ServerAdminAudit, $"admin command={command} selector={(parts.Length > 1 ? parts[1] : "")}");

            switch (command)
            {
                case "clients":
                    WriteClients();
                    return true;
                case "kick":
                    if (parts.Length != 2)
                    {
                        _output.WriteLine("Usage: kick <client-id|callsign|ip>");
                        return true;
                    }
                    WriteKick(parts[1]);
                    return true;
                case "stats":
                    WriteStats();
                    return true;
                case "banlist":
                    WriteBanList();
                    return true;
                case "ban":
                    if (parts.Length != 2 || !IPAddress.TryParse(parts[1], out var banAddress))
                    {
                        _output.WriteLine("Usage: ban <ip>");
                        return true;
                    }
                    bool banned = _server.BanAddress(banAddress);
                    _output.WriteLine(banned
                        ? $"Banned {NormalizeForDisplay(banAddress)}"
                        : $"Already banned {NormalizeForDisplay(banAddress)}");
                    return true;
                case "unban":
                    if (parts.Length != 2 || !IPAddress.TryParse(parts[1], out var unbanAddress))
                    {
                        _output.WriteLine("Usage: unban <ip>");
                        return true;
                    }
                    bool unbanned = _server.UnbanAddress(unbanAddress);
                    _output.WriteLine(unbanned
                        ? $"Unbanned {NormalizeForDisplay(unbanAddress)}"
                        : $"Not banned {NormalizeForDisplay(unbanAddress)}");
                    return true;
                case "help":
                    WriteHelp();
                    return true;
                case "quit":
                case "exit":
                    return false;
                default:
                    _output.WriteLine($"Unknown command '{parts[0]}'. Try: help");
                    return true;
            }
        }

        private void WriteClients()
        {
            var clients = _server.GetClientSnapshots();
            if (clients.Length == 0)
            {
                _output.WriteLine("No connected clients.");
                return;
            }

            _output.WriteLine("Connected clients:");
            foreach (var client in clients)
            {
                var id = client.ClientId.ToString("N")[..8];
                var callsign = string.IsNullOrWhiteSpace(client.Callsign) ? "(no callsign)" : client.Callsign;
                var frequencies = client.Frequencies.Length == 0
                    ? "--"
                    : string.Join(",", client.Frequencies.Select(f => f.ToString("0.000")));
                var keys = client.Subscriptions.Length == 0
                    ? "legacy"
                    : string.Join(",", client.Subscriptions.Select(s => Convert.ToHexString(s.NetIdHash)));
                var lastSeenAge = DateTime.UtcNow - client.LastSeenUtc;
                _output.WriteLine(
                    $"- {callsign} id={id} endpoint={client.EndPoint} freq={frequencies} keys={keys} lastSeen={FormatDuration(lastSeenAge)} ago");
            }
        }

        private void WriteKick(string selector)
        {
            var result = _server.KickClient(selector, "admin-command");
            if (result.Success && result.Client != null)
            {
                var callsign = string.IsNullOrWhiteSpace(result.Client.Callsign) ? "(no callsign)" : result.Client.Callsign;
                _output.WriteLine($"Kicked {callsign} ({result.Client.ClientId}) from {result.Client.EndPoint}");
                return;
            }

            _output.WriteLine(result.Reason switch
            {
                "ambiguous" => $"Kick selector '{selector}' matched multiple clients; use a client id or IP.",
                "missing-selector" => "Usage: kick <client-id|callsign|ip>",
                _ => $"No client matched '{selector}'."
            });
        }

        private void WriteStats()
        {
            var stats = _server.GetStatsSnapshot();
            _output.WriteLine("Server stats:");
            _output.WriteLine($"Connected clients: {stats.ConnectedClients}");
            _output.WriteLine($"Banned IPs: {stats.BannedIpCount}");
            _output.WriteLine($"Uptime: {FormatDuration(stats.Uptime)}");
            _output.WriteLine($"Datagrams received: {stats.DatagramsReceived}");
            _output.WriteLine($"Datagrams relayed: {stats.DatagramsRelayed}");
            _output.WriteLine($"Datagrams dropped: {stats.DatagramsDropped}");
        }

        private void WriteBanList()
        {
            var banned = _server.BannedAddresses.OrderBy(ip => ip.ToString()).ToArray();
            if (banned.Length == 0)
            {
                _output.WriteLine("Banlist is empty.");
                return;
            }

            _output.WriteLine("Banned IPs:");
            foreach (var ip in banned)
            {
                _output.WriteLine($"- {ip}");
            }
        }

        private void WriteHelp()
        {
            _output.WriteLine("Commands:");
            _output.WriteLine("  clients                 List connected clients.");
            _output.WriteLine("  kick <client|ip>        Disconnect a client by id, callsign, or IP.");
            _output.WriteLine("  stats                   Show server counters and uptime.");
            _output.WriteLine("  banlist                 List banned IP addresses.");
            _output.WriteLine("  ban <ip>                Ban an IP address and disconnect matching clients.");
            _output.WriteLine("  unban <ip>              Remove an IP address from the banlist.");
            _output.WriteLine("  help                    Show this help.");
            _output.WriteLine("  quit                    Stop the server.");
        }

        private static string FormatDuration(TimeSpan duration)
        {
            if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
            return duration.TotalHours >= 1
                ? $"{(int)duration.TotalHours}h {duration.Minutes}m {duration.Seconds}s"
                : $"{duration.Minutes}m {duration.Seconds}s";
        }

        private static IPAddress NormalizeForDisplay(IPAddress address) =>
            address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    }
}
