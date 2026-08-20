# Discord RPC Bridge for Wine

Discord RPC Bridge is a .NET library that bridges Discord's Rich Presence between Wine/Proton.CrossOver environments and Discord clients on Linux and macOS (including Native, Flatpak, Snap and third-party clients like Vesktop).

Primarily designed for Final Fantasy XIV and the third-party launchers [XIVLauncher.Core](https://github.com/goatcorp/XIVLauncher.Core) and [XIV On Mac](https://github.com/marzent/XIV-on-Mac).

## How It Works
1. The bridge starts a local TCP server on `localhost:2026` (or a custom port).
2. The game inside Wine/Proton/CrossOver sends RPC commands to the bridge over TCP.
3. The bridge finds the host Discord's IPC socket (`discord-ipc-0` to `9`) and relays the data between the game and Discord.

## Usage

### Quick Start
```c#
using DiscordRPCBridge_Wine;

await using var bridge = new RPCBridgeServer();

// Optional logging callbacks
bridge.OnInfo = msg => Console.WriteLine($"INFO: {msg}");
bridge.OnDebug = msg => Console.WriteLine($"DEBUG: {msg}");
bridge.OnError = (ex, msg) => Console.WriteLine($"ERROR: {ex} - {msg}");

bridge.Start(); // Listens on localhost:2026 by default

// Rest of your code

// When shutting down:
await bridge.StopAsync().ConfigureAwait(false);
```

### Advanced Example
```c#
using DiscordRPCBridge_Wine;

namespace Program;

public class DiscordRpcRunner : IAsyncDisposable
{
    private readonly RPCBridgeServer _bridge = new();
    private readonly int? _port;

    public DiscordRpcRunner(int port = 2026)
    {
        this._port = port;

        this._bridge.OnInfo += info => Log($"INFO: {info}");
        this._bridge.OnError = (ex, msg) => Log($"ERROR: {ex} - {msg}");
    }
    
    public void Start() => this._bridge.Start(_port);

    public async Task StopAsync() => 
        await this._bridge.StopAsync().ConfigureAwait(false);

    public async ValueTask DisposeAsync() => 
        await this._bridge.DisposeAsync().ConfigureAwait(false);

    private void Log(string message) => Console.WriteLine(message);
}
```

## API Reference

### RPCBridgeServer
> The main class that bridges the connection between the game and Discord's IPC socket for RPC via TCP. Implements `IAsyncDisposable`.

Functions:
- `void Start(int port = 2026)`: Starts the RPC bridge on `localhost` and listens for connections.
- `Task StopAsync()`: Stops the RPC bridge, relays and waits for server cleanup.
- `ValueTask DisposeAsync()`: Async Dispose. Calls `StopAsync` and disposes of the server resources.

Properties:
- `Action<string>? OnInfo`: Invoked for informational messages.
- `Action<string>? OnDebug`: Invoked for debug messages.
- `Action<Exception?, string>? OnError`: Invoked for error messages and exceptions.

