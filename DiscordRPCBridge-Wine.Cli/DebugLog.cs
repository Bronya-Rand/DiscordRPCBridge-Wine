namespace DiscordRPCBridge_Wine.Cli
{
    internal enum LogLevel
    {
        Info,
        Debug,
        Error
    }

    internal static class LogLevelExtensions
    {
        public static string ToLogString(this LogLevel level) => level switch
        {
            LogLevel.Info => "[INFO]",
            LogLevel.Debug => "[DEBUG]",
            LogLevel.Error => "[ERROR]",
            _ => "[UNKNOWN]"
        };
    }

    internal static class DebugLog
    {
        private static readonly string LogPath = ResolveLogPath();
        private const long MaxLogFileSize = 5 * 1024 * 1024; // 5 MB
        private const int MaxLogFileCount = 3; // Keep 3 rolled log files
        private static readonly Lock LogLock = new();

        private static string ResolveLogPath()
        {
            try
            {
                // Prefer user's local data directory (~/.local/share on Linux)
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrEmpty(localAppData))
                {
                    var logDir = Path.Combine(localAppData, "discord-rpc-bridge");
                    Directory.CreateDirectory(logDir);
                    return Path.Combine(logDir, "discord-rpc-bridge.log");
                }
            }
            catch
            {
                // Fallback to BaseDirectory if LocalApplicationData fails
            }

            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "discord-rpc-bridge.log");
        }

        public static void Write(LogLevel level, string message)
        {
            lock (LogLock)
            {
                var logState = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {level.ToLogString()} {message}";
                try
                {
                    RollIfNeeded();
                    File.AppendAllText(LogPath, $"{logState}{Environment.NewLine}");

                    if (level == LogLevel.Error)
                        Console.Error.WriteLine(logState);
                    else
                        Console.WriteLine(logState);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[DebugLog] Write failed: {ex.Message}");
                    Console.Error.WriteLine($"Log message: {logState}");
                }
            }
        }

        public static void WriteInfo(string message) => Write(LogLevel.Info, message);
        public static void WriteDebug(string message) => Write(LogLevel.Debug, message);

        public static void WriteError(Exception? ex, string message) =>
            Write(LogLevel.Error, ex != null ? $"{message}: {ex.Message}" : message);

        public static void WriteErrorVerbose(Exception? ex, string message) =>
            Write(LogLevel.Error, ex != null ? $"{message}{Environment.NewLine}{ex}" : message);

        public static void Clear()
        {
            lock (LogLock)
            {
                try
                {
                    if (File.Exists(LogPath))
                        File.WriteAllText(LogPath, string.Empty);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[DebugLog] Clear failed: {ex.Message}");
                }
            }
        }

        private static void RollIfNeeded()
        {
            var info = new FileInfo(LogPath);
            if (!info.Exists || info.Length < MaxLogFileSize)
                return;

            for (var i = MaxLogFileCount - 1; i >= 1; i--)
            {
                var src = $"{LogPath}.{i}";
                var dest = $"{LogPath}.{i + 1}";
                if (File.Exists(src))
                    File.Copy(src, dest, true);
            }

            File.Copy(LogPath, $"{LogPath}.1", true);
            File.WriteAllText(LogPath, string.Empty);
        }
    }
}

