// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Bronya-Rand (Azariel Del Carmen)

using System.Net;
using System.Net.Sockets;

namespace DiscordRPCBridge_Wine
{
    /// <summary>
    /// The TCP server that bridges connections from clients to the Discord IPC socket.
    /// </summary>
    public sealed class RPCBridgeServer : IAsyncDisposable
    {
        private CancellationTokenSource? cts;
        private Task? runTask;

        public Action<string>? OnInfo { get; set; }
        public Action<string>? OnDebug { get; set; }
        public Action<Exception?, string>? OnError { get; set; }

        public void Start(int port = 2026)
        {
            if (runTask != null)
                throw new InvalidOperationException("Server is already running.");
            cts = new CancellationTokenSource();
            runTask = StartAsync(port, cts.Token);
        }

        public async Task StopAsync()
        {
            if (cts == null) return;
            await cts.CancelAsync();
            if (runTask != null)
                await runTask; // Wait for the server task to complete
        }
        public async ValueTask DisposeAsync() => await StopAsync();

        private async Task StartAsync(int port, CancellationToken token)
        {
            var socketResolver = new SocketResolver
            {
                LogCallback = OnInfo,
                LogDebugCallback = OnDebug,
                LogErrorCallback = OnError
            };

            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();

            try
            {
                while (!token.IsCancellationRequested)
                {
                    TcpClient? tcpClient = null;
                    try
                    {
                        // Wait for the client to connect
                        tcpClient = await listener.AcceptTcpClientAsync(token);
                        OnDebug?.Invoke($"Client connected to bridge from {tcpClient.Client.RemoteEndPoint}");

                        // Find the Discord socket path (0-9)
                        string? socketPath = null;
                        int? connectedPipe = null;
                        for (var i = 0; i <= 9; i++)
                        {
                            var discordSocketPath = socketResolver.FindSocket(i);
                            if (!string.IsNullOrEmpty(discordSocketPath))
                            {
                                socketPath = discordSocketPath;
                                connectedPipe = i;
                                break;
                            }
                        }

                        // Exit if no Discord socket was found
                        if (socketPath == null || connectedPipe == null)
                        {
                            OnError?.Invoke(null, "Could not find Discord socket path.");
                            continue;
                        }

                        // Connect to the Discord socket
                        var discordEndPoint = new UnixDomainSocketEndPoint(socketPath);
                        using var discordSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                        await discordSocket.ConnectAsync(discordEndPoint, token);
                        OnInfo?.Invoke($"Connected to Discord socket at {socketPath} (pipe {connectedPipe})");

                        // Relay data between the client and the Discord socket
                        await using var tcpStream = tcpClient.GetStream();
                        await using var unixStream = new NetworkStream(discordSocket, ownsSocket: false);

                        using var discordRelayCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                        var clientToDiscord = tcpStream.CopyToAsync(unixStream, discordRelayCts.Token);
                        var discordToClient = unixStream.CopyToAsync(tcpStream, discordRelayCts.Token);

                        await Task.WhenAny(clientToDiscord, discordToClient);
                        await discordRelayCts.CancelAsync();
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        // Server shutdown requested, exit the loop
                        break;
                    }
                    catch (OperationCanceledException) { /* Relay canceled due to client disconnect, ignore */ }
                    catch (IOException)
                    {
                        OnInfo?.Invoke("Disconnected from client or Discord.");
                    }
                    catch (Exception ex)
                    {
                        OnError?.Invoke(ex, "Unexpected bridge error");
                    }
                    finally
                    {
                        tcpClient?.Dispose();
                    }
                }
            }
            finally
            {
                listener.Stop();
                OnInfo?.Invoke("RPC Bridge server stopped.");
            }
        }
    }
}
