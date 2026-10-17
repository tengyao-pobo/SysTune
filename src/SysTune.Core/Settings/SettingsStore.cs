using System.Text.Json;
using System.Text.Json.Serialization;
using SysTune.Core.Logging;

namespace SysTune.Core.Settings;

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

/// <summary>
/// Loads/saves <see cref="AppSettings"/> as JSON. Writes are atomic (temp file + replace) and can be
/// debounced so that dragging/resizing does not hammer the disk. A corrupt file is backed up and
/// replaced with defaults rather than crashing the app.
/// </summary>
public sealed class SettingsStore : IDisposable
{
    private readonly string _path;
    private readonly ILog _log;
    private readonly Lock _gate = new();
    private Timer? _debounce;
    private bool _disposed;

    public SettingsStore(string path, ILog? log = null)
    {
        _path = path;
        _log = log ?? NullLog.Instance;
        Current = new AppSettings();
    }

    public string FilePath => _path;

    public AppSettings Current { get; private set; }

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                Current = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings) ?? new AppSettings();
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _log.Warn($"Settings file unreadable, using defaults: {ex.Message}");
            TryBackupCorrupt();
            Current = new AppSettings();
        }
        Current.Normalize();
        return Current;
    }

    /// <summary>Saves immediately (synchronously).</summary>
    public void Save()
    {
        lock (_gate)
        {
            _debounce?.Change(Timeout.Infinite, Timeout.Infinite);
            WriteNow();
        }
    }

    /// <summary>Schedules a save after <paramref name="delay"/>; repeated calls coalesce.</summary>
    public void SaveDebounced(TimeSpan? delay = null)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _debounce ??= new Timer(_ => { lock (_gate) { if (!_disposed) WriteNow(); } });
            _debounce.Change(delay ?? TimeSpan.FromMilliseconds(800), Timeout.InfiniteTimeSpan);
        }
    }

    private void WriteNow()
    {
        try
        {
            Current.Normalize();
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, SettingsJsonContext.Default.AppSettings));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error("Failed to save settings", ex);
        }
    }

    private void TryBackupCorrupt()
    {
        try
        {
            File.Copy(_path, _path + ".corrupt", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"Could not back up corrupt settings: {ex.Message}");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_debounce is not null)
            {
                _debounce.Dispose();
                WriteNow(); // flush pending changes
            }
            _disposed = true;
        }
    }
}
