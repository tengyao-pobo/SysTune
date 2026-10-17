using System.Globalization;
using System.Text;

namespace SysTune.Core.Logging;

public enum LogLevel { Debug, Info, Warn, Error }

public interface ILog
{
    void Write(LogLevel level, string message, Exception? exception = null);
}

public static class LogExtensions
{
    public static void Debug(this ILog log, string message) => log.Write(LogLevel.Debug, message);

    public static void Info(this ILog log, string message) => log.Write(LogLevel.Info, message);

    public static void Warn(this ILog log, string message, Exception? ex = null) => log.Write(LogLevel.Warn, message, ex);

    public static void Error(this ILog log, string message, Exception? ex = null) => log.Write(LogLevel.Error, message, ex);
}

public sealed class NullLog : ILog
{
    public static readonly NullLog Instance = new();

    public void Write(LogLevel level, string message, Exception? exception = null)
    {
    }
}

/// <summary>
/// Small thread-safe rolling file logger. Rolls to <c>.1</c>, <c>.2</c> ... when the file exceeds
/// <see cref="MaxBytes"/>, keeping <see cref="MaxFiles"/> files. Logging never throws.
/// Messages are expected to never contain secrets (tokens are redacted by callers).
/// </summary>
public sealed class FileLog : ILog
{
    private readonly string _path;
    private readonly Lock _gate = new();

    public FileLog(string path, LogLevel minimum = LogLevel.Info, long maxBytes = 1024 * 1024, int maxFiles = 3)
    {
        _path = path;
        Minimum = minimum;
        MaxBytes = maxBytes;
        MaxFiles = maxFiles;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // logging stays best-effort
        }
    }

    public LogLevel Minimum { get; set; }

    public long MaxBytes { get; }

    public int MaxFiles { get; }

    public string FilePath => _path;

    public void Write(LogLevel level, string message, Exception? exception = null)
    {
        if (level < Minimum) return;
        var sb = new StringBuilder(128);
        sb.Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
          .Append(" [").Append(level.ToString().ToUpperInvariant()).Append("] ")
          .Append(message);
        if (exception is not null) sb.AppendLine().Append(exception);
        sb.AppendLine();
        lock (_gate)
        {
            try
            {
                RollIfNeeded();
                File.AppendAllText(_path, sb.ToString());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // ignore: logging must never take the app down
            }
        }
    }

    private void RollIfNeeded()
    {
        var info = new FileInfo(_path);
        if (!info.Exists || info.Length < MaxBytes) return;
        for (var i = MaxFiles - 1; i >= 1; i--)
        {
            var src = i == 1 ? _path : $"{_path}.{i - 1}";
            var dst = $"{_path}.{i}";
            if (File.Exists(src)) File.Move(src, dst, overwrite: true);
        }
    }
}
