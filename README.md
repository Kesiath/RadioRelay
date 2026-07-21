# RadioRelay

RadioRelay is a lightweight UDP voice relay built around tactical radio communication. It provides a Windows client with multiple configurable radios, per-radio push-to-talk bindings, frequency-based routing, optional per-radio passcodes, radio-style audio effects, and a small console server for relaying traffic between connected users.

The project is written in C# on .NET 8. The client is a Windows Forms application using NAudio for local audio I/O, SharpDX DirectInput for joystick/HOTAS button support, Concentus Opus for voice compression, and AES-256-GCM for encrypted radio traffic.

## Features

- Multi-radio Windows client
- Modern responsive dark UI with rounded cards, buttons, status badges, and DPI-safe custom sliders
- Frequency-based voice routing
- Independent PTT A / PTT B bindings per radio
- Keyboard, mouse, joystick, HOTAS, and gamepad PTT support
- Per-radio volume, ear routing, passcode, HUD color, and HUD position
- Local transmit and receive click sounds
- Radio-band audio coloration for HF, VHF, UHF channels
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

- Per-channel names and presets
- Frequency
- Volume
- Ear routing
- Per-radio passcode
- HUD color
- PTT A binding
- PTT B binding

### Frequencies

The server routes voice by frequency. If two users are not on the same frequency, their audio is not relayed to each other for that radio. Frequency matching allows a small tolerance so normal decimal entry differences do not break routing.

### Passcodes and encrypted nets

A blank passcode means the radio is open/unencrypted. A non-blank passcode creates a private encrypted radio net. Only clients using the same passcode on the same frequency can decrypt and hear that traffic. Per-radio passcodes are not sent to the server. The server only sees frequency and a short derived net identifier used for routing/presence. Actual voice payload encryption is handled client-side with AES-256-GCM.

### PTT bindings

Each radio supports two independent PTT slots:

- PTT A
- PTT B

Either binding can key the radio. This allows setups such as a HOTAS button plus a keyboard fallback. Holding both at once still counts as one active transmission; releasing one while the other is still held does not end the transmission. PTT bindings can use supported keyboard, mouse, joystick, HOTAS, or gamepad input.

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

## License

MIT License
