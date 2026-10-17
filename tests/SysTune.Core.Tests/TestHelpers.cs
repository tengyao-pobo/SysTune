using System.Net;
using System.Text;
using SysTune.Core.Common;

namespace SysTune.Core.Tests;

internal sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 17, 0, 0, 0, TimeSpan.Zero);

    public TimeSpan Elapsed { get; set; } = TimeSpan.FromSeconds(100);

    public void Advance(TimeSpan t)
    {
        UtcNow += t;
        Elapsed += t;
    }
}

/// <summary>Programmable HttpMessageHandler that records requests.</summary>
internal sealed class FakeHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();

    public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = [];

    public FakeHandler Enqueue(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _responses.Enqueue(responder);
        return this;
    }

    public FakeHandler Enqueue(HttpStatusCode status, string? json = null, Action<HttpResponseMessage>? configure = null)
        => Enqueue(_ =>
        {
            var r = new HttpResponseMessage(status);
            if (json is not null) r.Content = new StringContent(json, Encoding.UTF8, "application/json");
            configure?.Invoke(r);
            return r;
        });

    public FakeHandler EnqueueThrow(Exception ex) => Enqueue(_ => throw ex);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request, body));
        if (_responses.Count == 0) throw new InvalidOperationException("No response queued for " + request.RequestUri);
        return _responses.Dequeue()(request);
    }
}

internal static class Fixture
{
    public static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}
