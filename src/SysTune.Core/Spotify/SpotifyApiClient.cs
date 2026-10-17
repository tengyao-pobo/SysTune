using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace SysTune.Core.Spotify;

/// <summary>Thin, typed wrapper over the Spotify Web API Player endpoints.</summary>
public sealed class SpotifyApiClient
{
    public const string ApiBase = "https://api.spotify.com/v1";
    private readonly HttpClient _http;
    private readonly SpotifyConnection _connection;

    public SpotifyApiClient(HttpClient http, SpotifyConnection connection)
    {
        _http = http;
        _connection = connection;
    }

    /// <summary>GET /me/player. Returns <c>null</c> when there is no active playback (HTTP 204).</summary>
    public async Task<PlaybackState?> GetPlaybackAsync(CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, "/me/player?additional_types=track,episode", null, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        var dto = await ReadJsonAsync(response, SpotifyJsonContext.Default.PlayerDto, ct).ConfigureAwait(false);
        return dto is null ? null : SpotifyMapper.Map(dto);
    }

    /// <summary>GET /me/player/devices.</summary>
    public async Task<IReadOnlyList<SpotifyDevice>> GetDevicesAsync(CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, "/me/player/devices", null, ct).ConfigureAwait(false);
        var dto = await ReadJsonAsync(response, SpotifyJsonContext.Default.DevicesDto, ct).ConfigureAwait(false);
        return (dto?.Devices ?? []).Select(SpotifyMapper.Map).ToList();
    }

    public Task PlayAsync(string? deviceId, CancellationToken ct)
        => SendAndDisposeAsync(HttpMethod.Put, "/me/player/play" + DeviceQuery(deviceId, '?'), null, ct);

    public Task PauseAsync(string? deviceId, CancellationToken ct)
        => SendAndDisposeAsync(HttpMethod.Put, "/me/player/pause" + DeviceQuery(deviceId, '?'), null, ct);

    public Task NextAsync(string? deviceId, CancellationToken ct)
        => SendAndDisposeAsync(HttpMethod.Post, "/me/player/next" + DeviceQuery(deviceId, '?'), null, ct);

    public Task PreviousAsync(string? deviceId, CancellationToken ct)
        => SendAndDisposeAsync(HttpMethod.Post, "/me/player/previous" + DeviceQuery(deviceId, '?'), null, ct);

    public Task SeekAsync(int positionMs, string? deviceId, CancellationToken ct)
        => SendAndDisposeAsync(HttpMethod.Put, "/me/player/seek?position_ms=" + Math.Max(0, positionMs).ToString(CultureInfo.InvariantCulture) + DeviceQuery(deviceId, '&'), null, ct);

    public Task SetVolumeAsync(int percent, string? deviceId, CancellationToken ct)
        => SendAndDisposeAsync(HttpMethod.Put, "/me/player/volume?volume_percent=" + Math.Clamp(percent, 0, 100).ToString(CultureInfo.InvariantCulture) + DeviceQuery(deviceId, '&'), null, ct);

    public Task SetShuffleAsync(bool state, string? deviceId, CancellationToken ct)
        => SendAndDisposeAsync(HttpMethod.Put, "/me/player/shuffle?state=" + (state ? "true" : "false") + DeviceQuery(deviceId, '&'), null, ct);

    public Task SetRepeatAsync(RepeatMode mode, string? deviceId, CancellationToken ct)
        => SendAndDisposeAsync(HttpMethod.Put, "/me/player/repeat?state=" + mode.ToString().ToLowerInvariant() + DeviceQuery(deviceId, '&'), null, ct);

    /// <summary>PUT /me/player — transfers playback to another Spotify Connect device.</summary>
    public Task TransferAsync(string deviceId, bool play, CancellationToken ct)
    {
        var body = "{\"device_ids\":[\"" + JsonEncodedText.Encode(deviceId) + "\"],\"play\":" + (play ? "true" : "false") + "}";
        return SendAndDisposeAsync(HttpMethod.Put, "/me/player", new StringContent(body, Encoding.UTF8, "application/json"), ct);
    }

    private static string DeviceQuery(string? deviceId, char sep)
        => string.IsNullOrEmpty(deviceId) ? "" : sep + "device_id=" + Uri.EscapeDataString(deviceId);

    private async Task SendAndDisposeAsync(HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        using var _ = await SendAsync(method, path, content, ct).ConfigureAwait(false);
    }

    /// <summary>Sends an authorised request; retries once after a forced token refresh on 401.
    /// Throws a typed <see cref="SpotifyException"/> for every non-success outcome.</summary>
    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        byte[]? body = content is null ? null : await content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var contentType = content?.Headers.ContentType;
        content?.Dispose();

        for (var attempt = 0; ; attempt++)
        {
            var token = await _connection.GetAccessTokenAsync(forceRefresh: attempt > 0, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, ApiBase + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body is not null)
            {
                request.Content = new ByteArrayContent(body);
                request.Content.Headers.ContentType = contentType;
            }
            else if (method == HttpMethod.Put || method == HttpMethod.Post)
            {
                // Spotify rejects some body-less PUT/POST requests without Content-Length.
                request.Content = new ByteArrayContent([]);
            }

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new SpotifyNetworkException("無法連線到 Spotify", ex);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new SpotifyNetworkException("Spotify 回應逾時", ex);
            }

            if (response.IsSuccessStatusCode) return response;

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) continue;
                throw await CreateErrorAsync(response, ct).ConfigureAwait(false);
            }
        }
    }

    internal static async Task<SpotifyException> CreateErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string? reason = null;
        string? message = null;
        try
        {
            var env = await response.Content.ReadFromJsonAsync(SpotifyJsonContext.Default.ErrorEnvelopeDto, ct).ConfigureAwait(false);
            reason = env?.Error?.Reason;
            message = env?.Error?.Message;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            // body was not JSON
        }

        var status = response.StatusCode;
        return status switch
        {
            HttpStatusCode.TooManyRequests => new SpotifyRateLimitException("Spotify 請求過於頻繁，稍後自動重試") { StatusCode = status, RetryAfter = GetRetryAfter(response), Reason = reason },
            HttpStatusCode.Unauthorized => new SpotifyAuthException("Spotify 授權失效，請重新連結") { StatusCode = status, Reason = reason },
            HttpStatusCode.Forbidden => new SpotifyException(reason == "PREMIUM_REQUIRED" || (message?.Contains("Premium", StringComparison.OrdinalIgnoreCase) ?? false)
                ? "此操作需要 Spotify Premium"
                : "Spotify 不允許此操作（帳戶權限、裝置限制或應用程式未授權此使用者）") { StatusCode = status, Reason = reason ?? "FORBIDDEN" },
            HttpStatusCode.NotFound => new SpotifyException("找不到可用的播放裝置，請先在任一裝置開啟 Spotify") { StatusCode = status, Reason = reason ?? "NO_ACTIVE_DEVICE" },
            >= HttpStatusCode.InternalServerError => new SpotifyNetworkException($"Spotify 服務暫時無法使用 ({(int)status})") { StatusCode = status, Reason = reason },
            _ => new SpotifyException($"Spotify 回傳錯誤 {(int)status}：{message ?? response.ReasonPhrase}") { StatusCode = status, Reason = reason },
        };
    }

    internal static TimeSpan GetRetryAfter(HttpResponseMessage response)
    {
        var ra = response.Headers.RetryAfter;
        TimeSpan value = ra?.Delta ?? (ra?.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(30));
        return value < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : value > TimeSpan.FromMinutes(10) ? TimeSpan.FromMinutes(10) : value;
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage response, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync(type, ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new SpotifyException("無法解析 Spotify 回應", ex);
        }
    }
}
