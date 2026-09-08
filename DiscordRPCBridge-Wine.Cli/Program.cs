// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Bronya-Rand (Azariel Del Carmen)

using System.Reflection;
using System.Runtime.InteropServices;
using DiscordRPCBridge_Wine;

namespace DiscordRPCBridge_Wine.Cli
{
    internal static class Program
    {
        private const int DefaultPort = 2026;
        private static bool _verbose;

        public static async Task<int> Main(string[] args)
        {
            if (OperatingSystem.IsWindows())
            {
                Console.Error.WriteLine("Error: DiscordRPCBridge-Wine is designed for Linux and macOS host systems.");
                Console.Error.WriteLine("On Windows, this bridge is not required.");
                return 1;
            }
            var port = DefaultPort;

            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];

                if (arg is "-h" or "--help")
                {
                    PrintUsage();
                    return 0;
                }

                if (arg is "-v" or "--verbose")
                {
                    _verbose = true;
                }
                else if (arg is "-p" or "--port")
                {
                    // Avoid ports requiring root perms
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int parsedPort) && parsedPort is >= 1024 and <= 65535)
                    {
                        port = parsedPort;
                    }
                    else
                    {
                        Console.Error.WriteLine("Error: --port requires a valid port number between 1024 and 65535");
                        return 1;
                    }
                }
                else
                {
                    Console.Error.WriteLine($"Error: Unknown argument '{arg}' (Use --help for help)");
                    return 1;
                }
            }

            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
            DebugLog.WriteInfo($"Discord RPC Bridge for Wine v{version}");
            DebugLog.WriteInfo($"Starting TCP bridge on 127.0.0.1:{port}...");

            await using var bridge = new RPCBridgeServer();
            bridge.OnInfo = msg => DebugLog.WriteInfo(msg);
            bridge.OnDebug = msg =>
            {
                if (_verbose) DebugLog.WriteDebug(msg);
            };
            bridge.OnError = (ex, msg) =>
            {
                if (ex != null && _verbose)
                {
                    DebugLog.WriteErrorVerbose(ex, msg);
                }
                else if (ex != null)
                {
                    DebugLog.WriteError(ex, msg);
                }
                else
                {
                    DebugLog.WriteError(null, msg);
                }
            };

            using var shutdownCts = new CancellationTokenSource();

            void HandleShutdown(string signal)
            {
                if (!shutdownCts.IsCancellationRequested)
                {
                    DebugLog.WriteInfo($"Received {signal}, shutting down bridge...");
                    shutdownCts.Cancel();
                }
            }

            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                HandleShutdown("SIGINT / CancelKeyPress");
            };

            PosixSignalRegistration? sigtermReg = null;
            PosixSignalRegistration? sigintReg = null;
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    sigtermReg = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
                    {
                        ctx.Cancel = true;
                        HandleShutdown("SIGTERM");
                    });
                    sigintReg = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx =>
                    {
                        ctx.Cancel = true;
                        HandleShutdown("SIGINT");
                    });
                }
                catch
                {
                    // Fallback to CancelKeyPress if POSIX signal registration fails
                }
            }

            try
            {
                bridge.Start(port);
                DebugLog.WriteInfo($"Bridge server is running. Ready for Wine/Proton client connections.");
                DebugLog.WriteInfo("Press Ctrl+C to stop.");

                // Wait for shutdown signal
                var tcs = new TaskCompletionSource();
                using (shutdownCts.Token.Register(() => tcs.TrySetResult()))
                {
                    await tcs.Task.ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                if (_verbose)
                {
                    DebugLog.WriteErrorVerbose(ex, "Fatal error occurred in the bridge server.");
                }
                else
                {
                    DebugLog.WriteError(ex, "Fatal error occurred in the bridge server.");
                }
                return 1;
            }
            finally
            {
                sigtermReg?.Dispose();
                sigintReg?.Dispose();
                DebugLog.WriteInfo("Stopping bridge server...");
                await bridge.StopAsync().ConfigureAwait(false);
                DebugLog.WriteInfo("Bridge stopped cleanly.");
            }

            return 0;
        }

        private static void PrintUsage()
        {
            Console.WriteLine("Discord RPC Bridge for Wine - Standalone Host Server");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  discord-rpc-bridge [options]");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  -p, --port <port>   TCP port to listen on for client connections (default: 2026)");
            Console.WriteLine("  -v, --verbose       Enable verbose debug output");
            Console.WriteLine("  -h, --help          Show this help message");
        }
    }
}

