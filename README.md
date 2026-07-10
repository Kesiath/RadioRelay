# RadioRelay

RadioRelay is a lightweight UDP voice relay built around the feel of tactical radio communication. It provides a Windows client with multiple configurable radios, per-radio push-to-talk bindings, frequency-based routing, optional per-radio passcodes, radio-style audio effects, and a small console server for relaying traffic between connected users.

The project is written in C# on .NET 8. The client is a Windows Forms application using NAudio for local audio I/O, SharpDX DirectInput for joystick/HOTAS button support, Concentus Opus for voice compression, and AES-256-GCM for encrypted radio traffic.

## Features

- Multi-radio Windows client
- Modern responsive dark UI with rounded cards, buttons, status badges, and DPI-safe custom sliders
- Frequency-based voice routing
- Independent PTT A / PTT B bindings per radio
- Keyboard, mouse, joystick, HOTAS, and gamepad PTT support
- Per-radio volume, ear routing, passcode, HUD color, and HUD position
- Local transmit and receive click sounds
- Radio-band audio coloration for HF, VHF, UHF, and intercom-style channels
- Optional encrypted radio nets using per-radio passcodes
- Server password support
- User presence and per-channel listener counts
- Transmission overlay / HUD for active TX and RX activity
- Control lock mode for preventing accidental radio edits during gameplay
- Import/export of operational radio settings
- Server-side admin commands for clients, stats, kicks, bans, and banlist management
- Persistent server IP bans
- Local diagnostic logging
- xUnit test coverage for protocol, server behavior, UI policy, audio helpers, and settings

## Project layout

```text
Client/   Windows Forms client, UI, audio engine, PTT input, networking
Server/   UDP relay server and admin console
Shared/   Shared protocol, audio codec, radio effects, security, diagnostics
Tests/    xUnit test project
```

## Requirements

### Running the client

- Windows
- A microphone and playback device
- .NET 8 Desktop Runtime, unless using a self-contained published build

### Running the server

- Windows, Linux, or another platform supported by .NET 8
- UDP port access through the host firewall/router
- .NET 8 Runtime, unless using a self-contained published build

### Building from source

- .NET 8 SDK
- Windows is required to build/run the WinForms client and the full test project because the client targets `net8.0-windows`.

## Quick start

### 1. Start the server

From the repository root:

```bash
dotnet run --project Server/RadioRelay.Server.csproj -- 2302
```

With a server password:

```bash
dotnet run --project Server/RadioRelay.Server.csproj -- 2302 myServerPassword
```

The first numeric argument is used as the UDP port. The first non-port argument is used as the server password. If no port is provided, the server defaults to port `2302`.

When running, the server console accepts admin commands:

```text
help
clients
stats
kick <client-id|callsign|ip>
ban <ip>
unban <ip>
banlist
quit
```

### 2. Start the client

From the repository root on Windows:

```bash
dotnet run --project Client/RadioRelay.Client.csproj
```

In the client:

1. Enter the server address.
2. Enter the server UDP port.
3. Enter the server password if the server requires one.
4. Set your callsign.
5. Select input and output audio devices.
6. Configure each radio frequency and passcode as needed.
7. Bind PTT A and/or PTT B for each radio.
8. Click **Connect**.

## Client usage

### Radios

Each radio row represents a separate tunable channel. Frequency determines who can hear whom. Users on the same frequency can hear each other if their passcode/net settings are compatible.

A radio can be configured with:

- Frequency
- Volume
- Ear routing
- Per-radio passcode
- HUD color
- PTT A binding
- PTT B binding

### Frequencies

The server routes voice by frequency. If two users are not on the same frequency, their audio is not relayed to each other for that radio.

Frequency matching allows a small tolerance so normal decimal entry differences do not break routing.

### Passcodes and encrypted nets

A blank passcode means the radio is open/unencrypted.

A non-blank passcode creates a private encrypted radio net. Only clients using the same passcode on the same frequency can decrypt and hear that traffic.

Per-radio passcodes are not sent to the server. The server only sees frequency and a short derived net identifier used for routing/presence. Actual voice payload encryption is handled client-side with AES-256-GCM.

### PTT bindings

Each radio supports two independent PTT slots:

- PTT A
- PTT B

Either binding can key the radio. This allows setups such as a HOTAS button plus a keyboard fallback. Holding both at once still counts as one active transmission; releasing one while the other is still held does not end the transmission.

PTT bindings can use supported keyboard, mouse, joystick, HOTAS, or gamepad input.

### Audio controls

The client includes controls for:

- Input device
- Output device
- Input gain
- TX click volume
- RX click volume
- Talk-over/interference warning volume
- Per-radio receive volume

The audio engine applies radio-style filtering and compression to simulate different bands and channel types.

### HUD / transmission overlay

The overlay shows active transmit and receive activity. Each radio can have its own HUD color. HUD positions can be customized and are saved with local settings.

Use **Customize HUD** to reposition HUD elements, then finish customization when done.

### Control lock

Control lock prevents accidental changes to radio controls while the app is in use. This is useful once the correct frequencies, passcodes, and PTT bindings are configured.

### Settings import/export

The client stores local settings under:

```text
%AppData%\RadioRelay\settings.json
```

The export/import feature is designed for operational radio settings, including:

- Server IP
- Port
- Server password
- Radio names
- Frequencies
- Radio passcodes

It intentionally does not have to replace every local preference, such as personal device choices or HUD placement.

## Server usage

### Launch examples

Default port:

```bash
dotnet run --project Server/RadioRelay.Server.csproj
```

Specific port:

```bash
dotnet run --project Server/RadioRelay.Server.csproj -- 2302
```

Specific port and password:

```bash
dotnet run --project Server/RadioRelay.Server.csproj -- 2302 myServerPassword
```

Password with default port:

```bash
dotnet run --project Server/RadioRelay.Server.csproj -- myServerPassword
```

### Admin commands

| Command | Description |
| --- | --- |
| `help` | Show available commands. |
| `clients` | List connected clients, callsigns, endpoints, frequencies, net hashes, and last-seen age. |
| `stats` | Show connected client count, ban count, uptime, received datagrams, relayed datagrams, and dropped datagrams. |
| `kick <client-id\|callsign\|ip>` | Disconnect a matching client. |
| `ban <ip>` | Ban an IP address and disconnect matching clients. |
| `unban <ip>` | Remove an IP address from the banlist. |
| `banlist` | List banned IP addresses. |
| `quit` | Stop the server. |

The server persists its banlist under:

```text
%AppData%\RadioRelay\server-banlist.txt
```

On non-Windows systems, the exact base path is determined by .NET's application data folder behavior for the current user.

## Network model

RadioRelay uses UDP.

The server is intentionally simple: it does not decode voice and does not need to know radio passcodes. Clients subscribe to frequencies, send Opus-compressed audio frames, and the server forwards matching packets to subscribed clients.

Packet types include:

- Subscribe
- Audio
- Heartbeat
- Heartbeat ACK
- Disconnect
- Presence update

The client uses heartbeat ACKs to determine whether the connection is actually healthy. UDP send success alone does not prove that the server received a packet.

## Security model

RadioRelay has two separate password concepts:

### Server password

The server password controls whether a client is allowed to join a specific server.

This is an access-control password, not a radio encryption key.

### Radio passcodes

Radio passcodes are per-radio net keys. They are used to derive encryption material for voice packets. Matching passcodes are required to decrypt encrypted transmissions.

Radio passcodes are stored locally in the user's settings file. Treat exported settings files as sensitive if they include operational passcodes.

## Building

Build the server:

```bash
dotnet build Server/RadioRelay.Server.csproj -c Release
```

Build the client on Windows:

```bash
dotnet build Client/RadioRelay.Client.csproj -c Release
```

Build everything that can be built from the current platform:

```bash
dotnet build
```

If there is no solution file in your checkout, build individual project files directly as shown above.

## Publishing

### Publish the server

Framework-dependent:

```bash
dotnet publish Server/RadioRelay.Server.csproj -c Release -o publish/server
```

Self-contained Windows x64 example:

```bash
dotnet publish Server/RadioRelay.Server.csproj -c Release -r win-x64 --self-contained true -o publish/server-win-x64
```

### Publish the client

Framework-dependent Windows build:

```bash
dotnet publish Client/RadioRelay.Client.csproj -c Release -o publish/client
```

Self-contained Windows x64 build:

```bash
dotnet publish Client/RadioRelay.Client.csproj -c Release -r win-x64 --self-contained true -o publish/client-win-x64
```

The client embeds its WAV assets into the assembly, so the published app does not need a separate `Assets` folder beside the executable.

## Testing

Run tests from Windows:

```bash
dotnet test Tests/RadioRelay.Tests.csproj
```

The test project targets `net8.0-windows` and references the WinForms client, so full test execution should be done on Windows.

## Troubleshooting

### Users cannot hear each other

Check the following:

1. All users are connected to the same server IP and UDP port.
2. The server password matches, if one is configured.
3. The users are on the same radio frequency.
4. The radio passcodes match, or all radios involved are blank/open.
5. The receiving radio volume is above 0%.
6. The correct output device is selected.
7. The transmitting user's PTT binding is actually keyed.
8. The server console shows the clients in `clients`.
9. The firewall allows UDP traffic on the server port.

### Client says it is connected but no one can hear it

Use the server `clients` command. If the client is not listed, it has not successfully subscribed to the server. Disconnect and reconnect the client, then verify the server logs and password/port settings.

If this happens after a server restart, make sure the client sends a fresh Subscribe packet after reconnect or after health is restored. The server builds its active user table from Subscribe packets.

### PTT does not work

Check that the correct PTT slot is bound for the radio you are trying to transmit on. Also confirm that control lock is not preventing edits while rebinding.

For joystick/HOTAS input, confirm the device is connected before launching the client and that Windows sees the input device.

### Audio is too quiet or too loud

Adjust these controls first:

- Per-radio volume
- Input gain
- TX click volume
- RX click volume
- Talk-over warning volume
- Windows input/output device levels

If the entire app needs a louder baseline, apply a central gain multiplier in the audio engine rather than editing individual WAV files.

### Server port will not open

Confirm that:

- The selected UDP port is not already in use.
- The host firewall allows inbound UDP on that port.
- Router/NAT forwarding is configured if clients connect from outside the LAN.
- Clients are using the public address when connecting over the internet.

### Settings seem wrong after import

Settings import is operationally focused. It is intended to import server/radio net information without necessarily overwriting every local hardware and UI preference.

## Developer notes

- Voice frames are Opus-encoded at 16 kHz mono with 20 ms frames.
- Audio packets are relayed by the server without decoding voice.
- Frequency and net-hash matching determine routing.
- Blank radio passcodes produce open traffic.
- Non-blank radio passcodes produce encrypted traffic.
- Server access password and radio passcodes are separate systems.
- The server has basic datagram rate limiting and malformed/auth log flood limiting.
- Persistent local client settings and server banlist live under the RadioRelay application data directory.

## License

MIT License
