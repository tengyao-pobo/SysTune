using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SysTune.Core.Common;

namespace SysTune.Core.Spotify;

/// <summary>
/// Spotify Accounts service calls for the Authorization Code flow with PKCE.
/// No client secret is used or stored; see https://developer.spotify.com/documentation/web-api/tutorials/code-pkce-flow.
/// </summary>
public sealed class SpotifyAuthClient
{
    public const string AccountsBase = "https://accounts.spotify.com";

    /// <summary>Minimum scopes needed: read playback/devices and control playback.</summary>
    public const string Scopes = "user-read-playback-state user-modify-playback-state user-read-currently-playing";

    private readonly HttpClient _http;
    private readonly IClock _clock;

    public SpotifyAuthClient(HttpClient http, IClock? clock = null)
    {
        _http = http;
        _clock = clock ?? SystemClock.Instance;
    }

    public static Uri BuildAuthorizeUri(string clientId, string redirectUri, string codeChallenge, string state)
    {
        var query = string.Join('&',
            "response_type=code",
            "client_id=" + Uri.EscapeDataString(clientId),
            "scope=" + Uri.EscapeDataString(Scopes),
            "redirect_uri=" + Uri.EscapeDataString(redirectUri),
            "code_challenge_method=S256",
            "code_challenge=" + Uri.EscapeDataString(codeChallenge),
            "state=" + Uri.EscapeDataString(state));
        return new Uri(AccountsBase + "/authorize?" + query);
    }

    public Task<TokenSet> ExchangeCodeAsync(string clientId, string code, string redirectUri, string codeVerifier, CancellationToken ct)
        => RequestTokenAsync(
            [
                new("grant_type", "authorization_code"),
                new("code", code),
                new("redirect_uri", redirectUri),
                new("client_id", clientId),
                new("code_verifier", codeVerifier),
            ],
            previousRefreshToken: null,
            ct);

    public Task<TokenSet> RefreshAsync(string clientId, string refreshToken, CancellationToken ct)
        => RequestTokenAsync(
            [
                new("grant_type", "refresh_token"),
                new("refresh_token", refreshToken),
                new("client_id", clientId),
            ],
            previousRefreshToken: refreshToken,
            ct);

    private async Task<TokenSet> RequestTokenAsync(KeyValuePair<string, string>[] form, string? previousRefreshToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, AccountsBase + "/api/token")
        {
            Content = new FormUrlEncodedContent(form),
        };
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new SpotifyNetworkException("無法連線到 Spotify 帳戶服務", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new SpotifyNetworkException("連線 Spotify 帳戶服務逾時", ex);
        }

        using (response)
        {
            TokenResponseDto? dto = null;
            try
            {
                dto = await response.Content.ReadFromJsonAsync(SpotifyJsonContext.Default.TokenResponseDto, ct).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                // handled below by status checks
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new SpotifyRateLimitException("Spotify 帳戶服務要求稍後再試") { StatusCode = response.StatusCode, RetryAfter = SpotifyApiClient.GetRetryAfter(response) };
            }
            if ((int)response.StatusCode >= 500)
            {
                throw new SpotifyNetworkException($"Spotify 帳戶服務暫時無法使用 ({(int)response.StatusCode})") { StatusCode = response.StatusCode };
            }
            if (!response.IsSuccessStatusCode || dto?.AccessToken is null)
            {
                var error = dto?.Error ?? "unknown_error";
                var message = error switch
                {
                    "invalid_grant" => "Spotify 授權已失效，請重新連結",
                    "invalid_client" => "Client ID 無效，請確認 Spotify 開發者應用程式設定",
                    "invalid_request" => "授權要求無效（請確認 Redirect URI 設定一致）",
                    _ => "Spotify 授權失敗",
                };
                throw new SpotifyAuthException($"{message} ({error})") { StatusCode = response.StatusCode, Reason = error };
            }

            var refresh = string.IsNullOrEmpty(dto.RefreshToken) ? previousRefreshToken : dto.RefreshToken;
            if (string.IsNullOrEmpty(refresh))
            {
                throw new SpotifyAuthException("Spotify 未回傳 refresh token") { Reason = "missing_refresh_token" };
            }
            var expiresIn = dto.ExpiresIn > 0 ? dto.ExpiresIn : 3600;
            return new TokenSet(dto.AccessToken, refresh, _clock.UtcNow.AddSeconds(expiresIn), dto.Scope ?? "");
        }
    }

    internal static string Describe(TokenSet t) => string.Create(CultureInfo.InvariantCulture, $"expires {t.ExpiresAt:O}");
}
