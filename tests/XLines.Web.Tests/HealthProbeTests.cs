using System.Net;
using System.Net.Sockets;

namespace XLines.Web.Tests;

/// <summary>
/// The container's healthcheck (<c>dotnet XLines.Web.dll --health</c>): exit 0 on a 2xx, 1 on
/// anything else, one line on stderr when it fails, and the loopback address it derives from the
/// environment the compose files give the container.
/// </summary>
public class HealthProbeTests
{
    private static readonly Uri Target = new("http://127.0.0.1:8080/health");

    [Fact]
    public async Task A_2xx_exits_0_and_writes_nothing()
    {
        var error = new StringWriter();
        Assert.Equal(0, await HealthProbe.ProbeAsync(Target, null, Answering(HttpStatusCode.OK), error, HealthProbe.Timeout));
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task A_503_exits_1_with_one_line()
    {
        var error = new StringWriter();
        Assert.Equal(1, await HealthProbe.ProbeAsync(Target, null, Answering(HttpStatusCode.ServiceUnavailable), error, HealthProbe.Timeout));
        Assert.Contains("503", error.ToString());
        Assert.Single(error.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task Connection_refused_exits_1()
    {
        var error = new StringWriter();
        var refused = new Stub((_, _) => throw new HttpRequestException(
            "Connection refused", new SocketException((int)SocketError.ConnectionRefused)));
        Assert.Equal(1, await HealthProbe.ProbeAsync(Target, null, refused, error, HealthProbe.Timeout));
        Assert.Contains("refused", error.ToString());
    }

    [Fact]
    public async Task No_answer_within_the_timeout_exits_1()
    {
        var error = new StringWriter();
        var silent = new Stub(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        Assert.Equal(1, await HealthProbe.ProbeAsync(Target, null, silent, error, TimeSpan.FromMilliseconds(100)));
        Assert.Contains("did not answer", error.ToString());
    }

    [Fact]
    public async Task The_allowed_host_is_sent_as_the_host_header()
    {
        string? sent = null;
        var handler = new Stub((request, _) =>
        {
            sent = request.Headers.Host;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        await HealthProbe.ProbeAsync(Target, HealthProbe.HostHeader("desk.example.com"), handler, new StringWriter(), HealthProbe.Timeout);
        Assert.Equal("desk.example.com", sent);
        Assert.Null(HealthProbe.HostHeader("*"));
    }

    [Theory]
    // The laptop's compose: HTTPS only.
    [InlineData("https://+:9443", null, "https://127.0.0.1:9443/health")]
    // The hosted compose: plain HTTP behind Caddy.
    [InlineData("http://+:8080", null, "http://127.0.0.1:8080/health")]
    [InlineData("https://+:9443;http://[::]:8080", null, "http://127.0.0.1:8080/health")]
    // Nothing set but the aspnet image's own ASPNETCORE_HTTP_PORTS.
    [InlineData(null, "8080", "http://127.0.0.1:8080/health")]
    public void The_target_is_the_containers_own_listener_on_loopback(string? urls, string? httpPorts, string expected)
    {
        var environment = new Dictionary<string, string?> { ["ASPNETCORE_URLS"] = urls, ["ASPNETCORE_HTTP_PORTS"] = httpPorts };
        Assert.Equal(new Uri(expected), HealthProbe.Target(name => environment.GetValueOrDefault(name)));
    }

    [Fact]
    public void Only_the_flag_alone_runs_the_probe()
    {
        Assert.True(HealthProbe.IsRequested(["--health"]));
        Assert.False(HealthProbe.IsRequested([]));
        Assert.False(HealthProbe.IsRequested(["--health", "true"]));
    }

    private static Stub Answering(HttpStatusCode status)
        => new((_, _) => Task.FromResult(new HttpResponseMessage(status)));

    private sealed class Stub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }
}
