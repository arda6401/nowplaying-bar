using System.IO;

namespace NowPlayingBar.Services;

public static class Logger
{
    private static readonly object LockObj = new();
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NowPlayingBar");
    private static readonly string LogFilePath = Path.Combine(LogDirectory, "log.txt");
    private static readonly string OldLogFilePath = Path.Combine(LogDirectory, "log.old.txt");
    private const long MaxLogSizeBytes = 2 * 1024 * 1024; // 2 MB

    static Logger()
    {
        try
        {
            if (!Directory.Exists(LogDirectory))
            {
                Directory.CreateDirectory(LogDirectory);
            }
        }
        catch
        {
            // Suppress error creating log dir
        }
    }

    public static void LogInfo(string message)
    {
        Write("INFO", message);
    }

    public static void LogWarning(string message)
    {
        Write("WARN", message);
    }

    public static void LogError(string message, Exception? ex = null)
    {
        var text = ex != null ? $"{message} | Exception: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}" : message;
        Write("ERROR", text);
    }

    private static void Write(string level, string message)
    {
        try
        {
            lock (LockObj)
            {
                if (File.Exists(LogFilePath))
                {
                    var fileInfo = new FileInfo(LogFilePath);
                    if (fileInfo.Length > MaxLogSizeBytes)
                    {
                        if (File.Exists(OldLogFilePath))
                        {
                            File.Delete(OldLogFilePath);
                        }
                        File.Move(LogFilePath, OldLogFilePath);
                    }
                }

                var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                var line = $"[{timestamp}] [{level}] {message}{Environment.NewLine}";
                File.AppendAllText(LogFilePath, line);
            }
        }
        catch
        {
            // Do not throw from logger
        }
    }
}
