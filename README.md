# Discord RPC Bridge for Wine

Discord RPC Bridge is a .NET library that enables Discord Rich Presence for games running inside Wine/Proton environments on Linux and macOS. It connects Wine applications to host Discord clients (including Native, Flatpak, Snap, and third-party clients like Vesktop).

Primarily designed for Final Fantasy XIV, Dalamud plugins ([Dalamud.RichPresence](https://github.com/Bronya-Rand/Dalamud.RichPresence)), and custom launchers ([XIVLauncher.Core](https://github.com/goatcorp/XIVLauncher.Core), [XIVLauncher-RB](https://github.com/rankynbass/XIVLauncher.Core), and [XIV on Mac](https://github.com/marzent/XIV-on-Mac)).

---

## Architecture Overview

Discord's Rich Presence on Linux and macOS communicates over local **Unix Domain Sockets** (`discord-ipc-0` through `9`), commonly located under `$XDG_RUNTIME_DIR` or sandboxed container directories (Flatpak, Snap).

Inside *some* versions of Wine/Proton, Wine lacks support for `AF_UNIX` which is needed to communicate with Discord from within Wine. This bridge solves the problem by acting as a lightweight byte-stream relay:

```
┌────────────────────────────────────────────────────────────────────────┐
│                       HOST SYSTEM (Linux / macOS)                      │
│                                                                        │
│   ┌───────────────────────────┐      ┌─────────────────────────────┐   │
│   │       Discord Client      │      │   Discord RPC Bridge Server │   │
│   │ (Native, Flatpak, Vesktop)│◄────►│   (CLI Daemon or Launcher)  │   │
│   └─────────────┬─────────────┘  UDS └──────────────▲──────────────┘   │
└─────────────────┼───────────────────────────────────┼──────────────────┘
                  │                                   │ TCP
                  │                                   │ (localhost:2026)
┌─────────────────┼───────────────────────────────────┼──────────────────┐
│                 ▼                                   │                  │
│      WINE / PROTON PREFIX                           ▼                  │
│   (Cannot reach host AF_UNIX)        ┌──────────────┴──────────────┐   │
│                                      │   Wine Client Application   │   │
│                                      │ (e.g., Dalamud.RichPresence │   │
│                                      │    via DiscordTcpSocket)    │   │
│                                      └─────────────────────────────┘   │
└────────────────────────────────────────────────────────────────────────┘
```

1. **Host Server**: Runs on the Linux/macOS host, finds the real Discord IPC Unix socket, and listens on `127.0.0.1:2026` via TCP.
2. **Wine Client**: Runs inside Wine/Proton and connects to the host server instead of a Unix socket.

---

## 1. Host / Server Setup

You can run the bridge on the host in two ways: as a **standalone binary** (for normal players and external tools) or by **embedding the NuGet package** in your project (for developers).

### Option A: Standalone CLI Binary (Recommended for Users)

Download the latest pre-compiled binary for your system from the [Releases](https://github.com/Bronya-Rand/DiscordRPCBridge-Wine/releases) tab:
- `discord-rpc-bridge-linux-x64.tar.gz` (Standard Linux / Steam Deck)
- `discord-rpc-bridge-osx-arm64.tar.gz` (Apple Silicon)

Extract the archive and run:
```bash
./discord-rpc-bridge
```

#### CLI Options
```text
Usage:
  discord-rpc-bridge [options]

Options:
  -p, --port <port>   TCP port to listen on for client connections (default: 2026)
  -v, --verbose       Enable verbose debug output
  -h, --help          Show this help message and exit
```

#### Running as a systemd User Service (Optional)
To run the bridge automatically in the background on Linux:
```ini
# ~/.config/systemd/user/discord-rpc-bridge.service
[Unit]
Description=Discord RPC Bridge for Wine
After=network.target

[Service]
ExecStart=%h/.local/bin/discord-rpc-bridge
Restart=on-failure

[Install]
WantedBy=default.target
```
Enable and start it with:
```bash
systemctl --user enable --now discord-rpc-bridge
```

---

### Option B: Embedding the Server via NuGet (For Developers)

If you are developing a project (like XIVLauncher or XIV on Mac), install the [`DiscordRPCBridge-Wine`](https://www.nuget.org/packages/DiscordRPCBridge-Wine) NuGet package.

```bash
dotnet add package DiscordRPCBridge-Wine
```

#### Quick Start
```csharp
using DiscordRPCBridge_Wine;

await using var bridge = new RPCBridgeServer();

// Optional logging callbacks
bridge.OnInfo = msg => Console.WriteLine($"[INFO] {msg}");
bridge.OnDebug = msg => Console.WriteLine($"[DEBUG] {msg}");
bridge.OnError = (ex, msg) => Console.Error.WriteLine($"[ERROR] {msg}: {ex?.Message}");

// Start listening (defaults to 127.0.0.1:2026)
bridge.Start(port: 2026);

// Keep running during application lifecycle...

// When shutting down:
await bridge.StopAsync().ConfigureAwait(false);
```

---

## 2. Client-Side Integration (Inside Wine / Plugins / Games)

If you are building a plugin or game running inside Wine (such as [Dalamud.RichPresence](https://github.com/Bronya-Rand/Dalamud.RichPresence)), you use your regular Discord RPC library (e.g., [DiscordRichPresence by Lachee](https://github.com/Lachee/discord-rpc-csharp)), but configure it to use a **TCP transport** rather than a named pipe. The following example below uses the aformentioned Discord library:

Discord's IPC wire format uses framed packets:
- `4 bytes` Opcode (Little-Endian `uint32`)
- `4 bytes` Payload Length (Little-Endian `uint32`)
- `N bytes` JSON Payload

Because the RPC bridge server forwards these raw bytes directly to Discord's socket, the client only needs an `INamedPipeClient` transport that talks to `localhost:2026` via TCP.

### Client Reference Implementation (`DiscordTcpSocket.cs`)

You can drop the following class directly into your C# project running inside Wine:

```csharp
using System;
using System.Net.Sockets;
using DiscordRPC.IO;
using DiscordRPC.Logging;

public class DiscordTcpSocket : INamedPipeClient
{
    private readonly string _host;
    private readonly int _port;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private int _connectedPipe = -1;

    public ILogger Logger { get; set; } = new ConsoleLogger();
    public int ConnectedPipe => _connectedPipe;
    public bool IsConnected => GetIsConnected();

    public DiscordTcpSocket(string host = "127.0.0.1", int port = 2026)
    {
        _host = host;
        _port = port;
    }

    public bool Connect(int pipe)
    {
        try
        {
            Close();
            _client = new TcpClient();
            _client.Connect(_host, _port);
            _stream = _client.GetStream();
            _connectedPipe = pipe >= 0 ? pipe : 0;
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to connect to Discord TCP bridge: {ex.Message}");
            Close();
            return false;
        }
    }

    public bool ReadFrame(out PipeFrame frame)
    {
        frame = default;
        if (_stream == null || !IsConnected) return false;

        try
        {
            if (_stream.Socket.Available < 8) return false;

            // Read 8-byte header (Opcode + Length)
            byte[] header = new byte[8];
            int bytesRead = 0;
            while (bytesRead < 8)
            {
                int read = _stream.Read(header, bytesRead, 8 - bytesRead);
                if (read == 0) return false;
                bytesRead += read;
            }

            uint opVal = BitConverter.ToUInt32(header, 0);
            uint length = BitConverter.ToUInt32(header, 4);

            if (length > PipeFrame.MAX_SIZE) return false;

            // Read payload
            byte[] data = new byte[length];
            bytesRead = 0;
            while (bytesRead < length)
            {
                int read = _stream.Read(data, bytesRead, (int)length - bytesRead);
                if (read == 0) return false;
                bytesRead += read;
            }

            frame = new PipeFrame
            {
                Opcode = (Opcode)opVal,
                Data = data
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool WriteFrame(PipeFrame frame)
    {
        if (_stream == null || !IsConnected) return false;
        if (frame.Length > PipeFrame.MAX_SIZE) return false;

        try
        {
            byte[] buffer = new byte[8 + frame.Data.Length];
            BitConverter.GetBytes((uint)frame.Opcode).CopyTo(buffer, 0);
            BitConverter.GetBytes(frame.Length).CopyTo(buffer, 4);
            Buffer.BlockCopy(frame.Data, 0, buffer, 8, frame.Data.Length);

            _stream.Write(buffer, 0, buffer.Length);
            _stream.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _stream = null;
        _client?.Dispose();
        _client = null;
        _connectedPipe = -1;
    }

    public void Close() => Dispose();

    private bool GetIsConnected()
    {
        if (_stream?.Socket is not { Connected: true } socket) return false;
        try
        {
            if (socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0)
            {
                _connectedPipe = -1;
                return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
```

### Initializing `DiscordRpcClient` with the TCP Socket

Inside your plugin or application initialization (as seen in [`DiscordService.cs`](https://github.com/Bronya-Rand/Dalamud.RichPresence/blob/master/Dalamud.RichPresence/Services/Discord/DiscordService.cs)):

```csharp
using DiscordRPC;
using DiscordRPC.IO;

public class DiscordPresenceManager : IDisposable
{
    private DiscordRpcClient? _client;
    private const string ClientId = "YOUR_DISCORD_APPLICATION_ID";

    public void Initialize(bool isRunningUnderWine)
    {
        // Inside Wine, provide the TCP transport pointing to localhost:2026.
        // On native Windows, passing null will use standard Windows Named Pipes.
        INamedPipeClient? transport = isRunningUnderWine
            ? new DiscordTcpSocket("localhost", 2026)
            : null;

        _client = new DiscordRpcClient(ClientId, client: transport);

        _client.OnReady += (sender, e) =>
        {
            Console.WriteLine($"Connected to Discord as {e.User.Username}");
        };

        _client.Initialize();
    }

    public void SetPresence(string details, string state)
    {
        _client?.SetPresence(new RichPresence
        {
            Details = details,
            State = state,
            Assets = new Assets { LargeImageKey = "icon_main" }
        });
    }

    public void Dispose() => _client?.Dispose();
}
```

---

## API Reference (Server)

### `RPCBridgeServer`
> Main class that listens on TCP and relays frames to the host's Discord Unix Domain Socket. Implements `IAsyncDisposable`.

#### Methods:
- `void Start(int port = 2026)`: Starts the TCP listener on `127.0.0.1` and begins relaying client connections.
- `Task StopAsync()`: Stops the TCP server, terminates active relays, and cleans up resources.
- `ValueTask DisposeAsync()`: Asynchronously disposes of the server.

#### Properties / Events:
- `Action<string>? OnInfo`: Invoked for informational status messages.
- `Action<string>? OnDebug`: Invoked for detailed debug logs (client connections, socket discovery attempts).
- `Action<Exception?, string>? OnError`: Invoked when errors or exceptions occur.

---

## License

This project is licensed under the [MIT License](LICENSE).


