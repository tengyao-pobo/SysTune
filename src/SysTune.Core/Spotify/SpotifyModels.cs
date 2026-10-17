using System.Text.Json.Serialization;

namespace SysTune.Core.Spotify;

// ---- Domain models (what the rest of the app consumes) ----

public enum RepeatMode { Off, Context, Track }

public sealed record SpotifyDevice(
    string? Id,
    string Name,
    string Type,
    bool IsActive,
    bool IsRestricted,
    bool IsPrivateSession,
    int? VolumePercent,
    bool SupportsVolume);

public sealed record SpotifyItem(
    string Id,
    string Uri,
    string Title,
    string Subtitle,
    string Album,
    string? ImageUrlLarge,
    string? ImageUrlSmall,
    int DurationMs,
    bool IsEpisode);

/// <summary>Snapshot of the user's playback (GET /v1/me/player). <c>null</c> item = nothing playing.</summary>
public sealed record PlaybackState(
    SpotifyDevice? Device,
    SpotifyItem? Item,
    bool IsPlaying,
    int ProgressMs,
    bool Shuffle,
    RepeatMode Repeat,
    string PlayingType,
    PlaybackDisallows Disallows);

public sealed record PlaybackDisallows(bool Pausing, bool Resuming, bool Seeking, bool SkippingNext, bool SkippingPrev, bool TogglingShuffle, bool TogglingRepeat)
{
    public static readonly PlaybackDisallows None = new(false, false, false, false, false, false, false);
}

// ---- Wire DTOs (System.Text.Json source generated, tolerant of missing fields) ----

internal sealed class PlayerDto
{
    [JsonPropertyName("device")] public DeviceDto? Device { get; set; }

    [JsonPropertyName("repeat_state")] public string? RepeatState { get; set; }

    [JsonPropertyName("shuffle_state")] public bool ShuffleState { get; set; }

    [JsonPropertyName("progress_ms")] public int? ProgressMs { get; set; }

    [JsonPropertyName("is_playing")] public bool IsPlaying { get; set; }

    [JsonPropertyName("item")] public ItemDto? Item { get; set; }

    [JsonPropertyName("currently_playing_type")] public string? CurrentlyPlayingType { get; set; }

    [JsonPropertyName("actions")] public ActionsDto? Actions { get; set; }
}

internal sealed class ActionsDto
{
    [JsonPropertyName("disallows")] public Dictionary<string, bool>? Disallows { get; set; }
}

internal sealed class DeviceDto
{
    [JsonPropertyName("id")] public string? Id { get; set; }

    [JsonPropertyName("is_active")] public bool IsActive { get; set; }

    [JsonPropertyName("is_private_session")] public bool IsPrivateSession { get; set; }

    [JsonPropertyName("is_restricted")] public bool IsRestricted { get; set; }

    [JsonPropertyName("name")] public string? Name { get; set; }

    [JsonPropertyName("type")] public string? Type { get; set; }

    [JsonPropertyName("volume_percent")] public int? VolumePercent { get; set; }

    [JsonPropertyName("supports_volume")] public bool? SupportsVolume { get; set; }
}

internal sealed class DevicesDto
{
    [JsonPropertyName("devices")] public List<DeviceDto>? Devices { get; set; }
}

internal sealed class ItemDto
{
    [JsonPropertyName("id")] public string? Id { get; set; }

    [JsonPropertyName("uri")] public string? Uri { get; set; }

    [JsonPropertyName("type")] public string? Type { get; set; }

    [JsonPropertyName("name")] public string? Name { get; set; }

    [JsonPropertyName("duration_ms")] public int DurationMs { get; set; }

    [JsonPropertyName("artists")] public List<NamedDto>? Artists { get; set; }

    [JsonPropertyName("album")] public AlbumDto? Album { get; set; }

    [JsonPropertyName("images")] public List<ImageDto>? Images { get; set; }

    [JsonPropertyName("show")] public AlbumDto? Show { get; set; }
}

internal sealed class AlbumDto
{
    [JsonPropertyName("name")] public string? Name { get; set; }

    [JsonPropertyName("images")] public List<ImageDto>? Images { get; set; }
}

internal sealed class NamedDto
{
    [JsonPropertyName("name")] public string? Name { get; set; }
}

internal sealed class ImageDto
{
    [JsonPropertyName("url")] public string? Url { get; set; }

    [JsonPropertyName("width")] public int? Width { get; set; }

    [JsonPropertyName("height")] public int? Height { get; set; }
}

internal sealed class TokenResponseDto
{
    [JsonPropertyName("access_token")] public string? AccessToken { get; set; }

    [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }

    [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }

    [JsonPropertyName("scope")] public string? Scope { get; set; }

    [JsonPropertyName("token_type")] public string? TokenType { get; set; }

    [JsonPropertyName("error")] public string? Error { get; set; }

    [JsonPropertyName("error_description")] public string? ErrorDescription { get; set; }
}

internal sealed class ErrorEnvelopeDto
{
    [JsonPropertyName("error")] public ErrorDto? Error { get; set; }
}

internal sealed class ErrorDto
{
    [JsonPropertyName("status")] public int Status { get; set; }

    [JsonPropertyName("message")] public string? Message { get; set; }

    [JsonPropertyName("reason")] public string? Reason { get; set; }
}

internal sealed class StoredTokenDto
{
    public string AccessToken { get; set; } = "";

    public string RefreshToken { get; set; } = "";

    public DateTimeOffset ExpiresAt { get; set; }

    public string Scope { get; set; } = "";
}

[JsonSourceGenerationOptions(NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(PlayerDto))]
[JsonSerializable(typeof(DevicesDto))]
[JsonSerializable(typeof(TokenResponseDto))]
[JsonSerializable(typeof(ErrorEnvelopeDto))]
[JsonSerializable(typeof(StoredTokenDto))]
internal sealed partial class SpotifyJsonContext : JsonSerializerContext;

internal static class SpotifyMapper
{
    public static PlaybackState Map(PlayerDto dto)
    {
        var d = dto.Actions?.Disallows;
        bool Dis(string key) => d is not null && d.TryGetValue(key, out var v) && v;
        return new PlaybackState(
            dto.Device is null ? null : Map(dto.Device),
            dto.Item is null ? null : Map(dto.Item),
            dto.IsPlaying,
            Math.Max(0, dto.ProgressMs ?? 0),
            dto.ShuffleState,
            dto.RepeatState switch { "track" => RepeatMode.Track, "context" => RepeatMode.Context, _ => RepeatMode.Off },
            dto.CurrentlyPlayingType ?? "unknown",
            new PlaybackDisallows(Dis("pausing"), Dis("resuming"), Dis("seeking"), Dis("skipping_next"), Dis("skipping_prev"), Dis("toggling_shuffle"), Dis("toggling_repeat_context")));
    }

    public static SpotifyDevice Map(DeviceDto d) => new(
        d.Id,
        string.IsNullOrWhiteSpace(d.Name) ? "未知裝置" : d.Name!,
        d.Type ?? "Unknown",
        d.IsActive,
        d.IsRestricted,
        d.IsPrivateSession,
        d.VolumePercent,
        d.SupportsVolume ?? d.VolumePercent is not null);

    public static SpotifyItem Map(ItemDto i)
    {
        var isEpisode = string.Equals(i.Type, "episode", StringComparison.Ordinal);
        var images = (isEpisode ? i.Images ?? i.Show?.Images : i.Album?.Images) ?? [];
        var subtitle = isEpisode
            ? i.Show?.Name ?? ""
            : string.Join(", ", (i.Artists ?? []).Select(a => a.Name).Where(n => !string.IsNullOrWhiteSpace(n)));
        return new SpotifyItem(
            i.Id ?? i.Uri ?? i.Name ?? "",
            i.Uri ?? "",
            i.Name ?? "",
            subtitle,
            isEpisode ? i.Show?.Name ?? "" : i.Album?.Name ?? "",
            PickImage(images, 300),
            PickImage(images, 64),
            Math.Max(0, i.DurationMs),
            isEpisode);
    }

    /// <summary>Picks the smallest image at least <paramref name="target"/> px wide, else the largest.</summary>
    internal static string? PickImage(List<ImageDto> images, int target)
    {
        ImageDto? best = null;
        foreach (var img in images)
        {
            if (string.IsNullOrEmpty(img.Url)) continue;
            var w = img.Width ?? img.Height ?? 0;
            if (best is null)
            {
                best = img;
                continue;
            }
            var bw = best.Width ?? best.Height ?? 0;
            var fits = w >= target;
            var bestFits = bw >= target;
            if ((fits && (!bestFits || w < bw)) || (!fits && !bestFits && w > bw)) best = img;
        }
        return best?.Url;
    }
}
