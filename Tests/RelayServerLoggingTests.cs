using System.IO;
using System.Net;
using System.Net.Sockets;
using RadioRelay.Server;
using RadioRelay.Shared.Protocol;

namespace RadioRelay.Tests;

public class RelayServerLoggingTests
{
    [Fact]
    public async Task Audio_transmission_start_does_not_write_message_log_line()
    {
        int port = GetAvailableUdpPort();
        var originalOut = Console.Out;
        using var writer = new StringWriter();
        using var cts = new CancellationTokenSource();
        Console.SetOut(writer);
        var server = new RelayServer(port);
        var runTask = server.RunAsync(cts.Token);

        try
        {
            await Task.Delay(150);
            using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var clientId = Guid.NewGuid();
            await SendSubscribe(client, port, clientId);
            Assert.True(await ReceivesAck(client, TimeSpan.FromSeconds(2)));
            writer.GetStringBuilder().Clear();

            var audio = new AudioPacket
            {
                ClientId = clientId,
                Frequency = 251.000f,
                IsTransmissionStart = true,
                SenderName = "Uzi 1",
                RadioName = "RADIO 1",
                Payload = new byte[] { 1, 2, 3 }
            }.Encode();
            await client.SendAsync(audio, audio.Length, new IPEndPoint(IPAddress.Loopback, port));
            await Task.Delay(150);

            var output = writer.ToString();
            Assert.DoesNotContain("[Message]", output);
            Assert.DoesNotContain("transmitting", output);
        }
        finally
        {
            cts.Cancel();
            await WaitForServerToStop(runTask);
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public async Task Auth_and_ban_rejections_log_explicit_audit_reasons()
    {
        int port = GetAvailableUdpPort();
        var originalOut = Console.Out;
        using var writer = new StringWriter();
        using var cts = new CancellationTokenSource();
        Console.SetOut(writer);
        var server = new RelayServer(port, "swordfish", new RelayServerOptions
        {
            BanListPath = CreateTempBanListPath(),
            LogFloodLimitWindow = TimeSpan.Zero
        });
        server.BanAddress(IPAddress.Parse("127.0.0.2"));
        var runTask = server.RunAsync(cts.Token);

        try
        {
            await Task.Delay(150);
            using var badPasswordClient = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var bannedClient = new UdpClient(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 0));

            await SendSubscribe(badPasswordClient, port, Guid.NewGuid(), "WrongPassword", "wrong");
            await SendSubscribe(bannedClient, port, Guid.NewGuid(), "Banned", "swordfish");
            await Task.Delay(200);

            var output = writer.ToString();
            Assert.Contains("[Auth] Rejected subscribe", output);
            Assert.Contains("reason=invalid-server-password", output);
            Assert.Contains("[Ban] Rejected packet", output);
            Assert.Contains("reason=banned-ip", output);
        }
        finally
        {
            cts.Cancel();
            await WaitForServerToStop(runTask);
            Console.SetOut(originalOut);
        }
    }

    private static async Task SendSubscribe(UdpClient client, int port, Guid clientId)
    {
        await SendSubscribe(client, port, clientId, "LoggerTest", "");
    }

    private static async Task SendSubscribe(UdpClient client, int port, Guid clientId, string callsign, string serverPassword)
    {
        var packet = new SubscribePacket
        {
            ClientId = clientId,
            Callsign = callsign,
            Frequencies = new[] { 251.000f },
            ServerPassword = serverPassword
        }.Encode();
        await client.SendAsync(packet, packet.Length, new IPEndPoint(IPAddress.Loopback, port));
    }

    private static async Task<bool> ReceivesAck(UdpClient client, TimeSpan timeout)
    {
        var receiveTask = client.ReceiveAsync();
        var completed = await Task.WhenAny(receiveTask, Task.Delay(timeout));
        if (completed != receiveTask) return false;

        var result = await receiveTask;
        return result.Buffer.Length > 0 && PacketPeek.GetType(result.Buffer) == PacketType.HeartbeatAck;
    }

    private static string CreateTempBanListPath()
    {
        string directory = Path.Combine(Path.GetTempPath(), "RadioRelayTests", Guid.NewGuid().ToString("N"));
        return Path.Combine(directory, "server-banlist.txt");
    }

    private static int GetAvailableUdpPort()
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
    }

    private static async Task WaitForServerToStop(Task runTask)
    {
        var completed = await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(2)));
        if (completed == runTask)
        {
            try { await runTask; }
            catch { }
        }
    }
}
