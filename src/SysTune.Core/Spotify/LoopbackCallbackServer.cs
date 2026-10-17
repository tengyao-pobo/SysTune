using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SysTune.Core.Spotify;

/// <summary>Result of the OAuth redirect to the loopback address.</summary>
public sealed record AuthorizationCallback(string? Code, string? State, string? Error);

/// <summary>
/// Minimal HTTP listener bound to 127.0.0.1 that captures the OAuth redirect.
/// Uses a raw <see cref="TcpListener"/> instead of HttpListener so that no URL ACL
/// reservation or administrator rights are required on Windows, and it never listens on
/// a non-loopback interface.
/// </summary>
public sealed class LoopbackCallbackServer : IDisposable
{
    private const int MaxRequestBytes = 16 * 1024;
    private readonly TcpListener _listener;
    private readonly string _path;

    public LoopbackCallbackServer(int port, string path = "/callback")
    {
        _path = path;
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Server.ExclusiveAddressUse = true;
        _listener.Start();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>
    /// Waits for a request to the callback path. Requests to other paths (e.g. favicon) receive 404
    /// and are ignored. Returns once a request with a code or error arrives.
    /// </summary>
    public async Task<AuthorizationCallback> WaitForCallbackAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            client.ReceiveTimeout = 5000;
            client.SendTimeout = 5000;
            var stream = client.GetStream();
            string? target;
            try
            {
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                readCts.CancelAfter(TimeSpan.FromSeconds(5));
                target = await ReadRequestTargetAsync(stream, readCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                continue;
            }

            if (target is null || !TryParse(target, _path, out var callback))
            {
                await WriteResponseAsync(stream, 404, "Not Found", "<p>Not found.</p>", cancellationToken).ConfigureAwait(false);
                continue;
            }

            var ok = callback.Error is null && callback.Code is not null;
            await WriteResponseAsync(stream, 200, "OK", ok ? SuccessHtml : FailureHtml(callback.Error), cancellationToken).ConfigureAwait(false);
            return callback;
        }
    }

    /// <summary>Parses a request target such as <c>/callback?code=x&amp;state=y</c>.</summary>
    internal static bool TryParse(string target, string path, out AuthorizationCallback callback)
    {
        callback = new AuthorizationCallback(null, null, null);
        var q = target.IndexOf('?', StringComparison.Ordinal);
        var p = q >= 0 ? target[..q] : target;
        if (!string.Equals(p, path, StringComparison.Ordinal)) return false;
        string? code = null, state = null, error = null;
        if (q >= 0)
        {
            foreach (var pair in target[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=', StringComparison.Ordinal);
                var key = Uri.UnescapeDataString(eq >= 0 ? pair[..eq] : pair);
                var value = eq >= 0 ? Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' ')) : "";
                switch (key)
                {
                    case "code": code = value; break;
                    case "state": state = value; break;
                    case "error": error = value; break;
                }
            }
        }
        if (code is null && error is null) return false;
        callback = new AuthorizationCallback(code, state, error);
        return true;
    }

    private static async Task<string?> ReadRequestTargetAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxRequestBytes];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
            if (buffer.AsSpan(0, total).IndexOf("\r\n\r\n"u8) >= 0) break;
        }
        var text = Encoding.ASCII.GetString(buffer, 0, total);
        var lineEnd = text.IndexOf("\r\n", StringComparison.Ordinal);
        var requestLine = lineEnd >= 0 ? text[..lineEnd] : text;
        var parts = requestLine.Split(' ');
        if (parts.Length < 3 || parts[0] != "GET") return null;
        return parts[1];
    }

    private static async Task WriteResponseAsync(NetworkStream stream, int status, string reason, string body, CancellationToken ct)
    {
        try
        {
            var bodyBytes = Encoding.UTF8.GetBytes(body);
            var header = $"HTTP/1.1 {status} {reason}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bodyBytes.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct).ConfigureAwait(false);
            await stream.WriteAsync(bodyBytes, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // browser went away; nothing to do
        }
    }

    private const string PageStyle = "<style>body{font-family:'Segoe UI Variable','Segoe UI',sans-serif;background:#111318;color:#eceff4;display:grid;place-items:center;height:100vh;margin:0}main{text-align:center;padding:32px 40px;border-radius:16px;background:#1c1f26;box-shadow:0 8px 32px #0006}h1{font-size:20px;font-weight:600;margin:0 0 8px}p{opacity:.75;margin:0}</style>";

    private static readonly string SuccessHtml =
        "<!doctype html><html lang=\"zh-Hant\"><meta charset=\"utf-8\"><title>SysTune</title>" + PageStyle +
        "<main><h1>✓ 已連結 Spotify</h1><p>可以關閉此分頁，回到 SysTune。</p></main></html>";

    private static string FailureHtml(string? error) =>
        "<!doctype html><html lang=\"zh-Hant\"><meta charset=\"utf-8\"><title>SysTune</title>" + PageStyle +
        "<main><h1>未完成連結</h1><p>Spotify 回傳：" + WebUtility.HtmlEncode(error ?? "unknown") + "</p></main></html>";

    public void Dispose() => _listener.Stop();
}
