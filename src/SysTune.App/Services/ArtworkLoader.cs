using System.Windows.Media;
using System.Windows.Media.Imaging;
using SysTune.Core.Logging;

namespace SysTune.App.Services;

/// <summary>
/// Downloads and decodes album art off the UI thread, with a small LRU cache. Images are decoded
/// at display size (not full resolution) and frozen, keeping memory use flat over long sessions.
/// </summary>
internal sealed class ArtworkLoader
{
    private const int DecodeSize = 192;
    private const int MaxBytes = 3 * 1024 * 1024;
    private readonly HttpClient _http;
    private readonly ILog _log;
    private readonly LinkedList<(string Key, ImageSource Image)> _lru = new();
    private readonly Lock _gate = new();

    public ArtworkLoader(HttpClient http, ILog log)
    {
        _http = http;
        _log = log;
    }

    public async Task<ImageSource?> LoadUrlAsync(string url, CancellationToken ct)
    {
        if (TryGet(url, out var cached)) return cached;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;
        try
        {
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxBytes) return null;
            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            return Decode(url, bytes);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or InvalidOperationException)
        {
            if (!ct.IsCancellationRequested) _log.Debug("Artwork download failed: " + ex.Message);
            return null;
        }
    }

    public ImageSource? LoadBytes(string key, byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0) return null;
        return TryGet(key, out var cached) ? cached : Decode(key, bytes);
    }

    private ImageSource? Decode(string key, byte[] bytes)
    {
        if (bytes.Length > MaxBytes) return null;
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            img.DecodePixelWidth = DecodeSize;
            img.StreamSource = new MemoryStream(bytes, writable: false);
            img.EndInit();
            img.Freeze();
            lock (_gate)
            {
                _lru.AddFirst((key, img));
                while (_lru.Count > 12) _lru.RemoveLast();
            }
            return img;
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or InvalidOperationException or ArgumentException or FormatException or System.Runtime.InteropServices.COMException or OverflowException)
        {
            _log.Debug("Artwork decode failed: " + ex.Message);
            return null;
        }
    }

    private bool TryGet(string key, out ImageSource? image)
    {
        lock (_gate)
        {
            for (var node = _lru.First; node is not null; node = node.Next)
            {
                if (node.Value.Key == key)
                {
                    _lru.Remove(node);
                    _lru.AddFirst(node);
                    image = node.Value.Image;
                    return true;
                }
            }
        }
        image = null;
        return false;
    }
}
