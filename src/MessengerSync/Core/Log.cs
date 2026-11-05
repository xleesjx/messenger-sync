namespace MessengerSync.Core;

/// <summary>logs\sync-yyyyMM.log 에 동작 기록을 남긴다.</summary>
public static class Log
{
    private static readonly object Sync = new();

    public static event Action<string>? Written;

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null)
        => Write("ERROR", ex == null ? message : $"{message} :: {ex.GetType().Name}: {ex.Message}\n{ex}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}";
        lock (Sync)
        {
            try
            {
                File.AppendAllText(Path.Combine(Paths.LogDir, $"sync-{DateTime.Now:yyyyMM}.log"),
                    line + Environment.NewLine);
            }
            catch
            {
                // 기록 실패는 동작에 영향을 주지 않는다.
            }
        }
        Written?.Invoke(line);
    }
}
