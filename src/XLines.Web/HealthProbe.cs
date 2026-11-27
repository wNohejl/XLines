using System.Collections;
using System.Net;

namespace XLines.Web;

/// <summary>
/// <c>dotnet XLines.Web.dll --health</c>: the container's healthcheck, run by the app on itself.
///
/// The runtime image has no curl, wget or nc, and nothing may be installed into it (ADR 0006), so
/// the probe is the one binary the image already has. It runs before the host is built: one GET to
/// this container's own <c>/health</c> on loopback, three seconds at most, exit 0 on a 2xx and 1
/// otherwise with one line on stderr. No configuration pipeline, database, scheduler or logging,
/// and nothing written to disk — the root filesystem is read-only.
///
/// <c>/health</c>, not <c>/ready</c>: liveness answers for the process alone (Program.cs), so a
/// database that flaps leaves the container healthy and Caddy, which waits on it, still starts —
/// the desk then shows its own error state rather than nothing at all. A database that is down at
/// boot already stops the container before it listens: the app migrates before it serves.
/// </summary>
public static class HealthProbe
{
    /// <summary>
    /// The whole command line, alone. Matched only as the single argument, so it can never swallow
    /// a <c>--key value</c> configuration override, and no configuration section is called Health.
    /// </summary>
    public const string Flag = "--health";

    public const string Path = "/health";

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public static bool IsRequested(string[] args) => args is [Flag];

    /// <summary>Probes the listener this process's environment configures; the exit code.</summary>
    public static async Task<int> RunAsync()
    {
        var environment = Environment.GetEnvironmentVariables()
            .Cast<DictionaryEntry>()
            .GroupBy(e => (string)e.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (string?)g.First().Value, StringComparer.OrdinalIgnoreCase);
        string? Read(string name) => environment.GetValueOrDefault(name);

        using var handler = new SocketsHttpHandler
        {
            ConnectTimeout = Timeout,
            UseProxy = false,
            // The laptop's container listens on HTTPS only, with the developer's own certificate,
            // issued to localhost and trusted by no store inside the image. The probe connects to
            // itself on loopback and sends nothing, so it does not check the certificate: what it
            // asks is whether the process answers, not who signed it.
            SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
        };
        return await ProbeAsync(Target(Read), HostHeader(Read("AllowedHosts")), handler, Console.Error, Timeout);
    }

    /// <summary>
    /// Where this container listens, read the way Kestrel reads it: <c>ASPNETCORE_URLS</c> first
    /// (a plain-HTTP entry preferred where there is one), then <c>ASPNETCORE_HTTP_PORTS</c>, then
    /// <c>ASPNETCORE_HTTPS_PORTS</c>, then Kestrel's own default. Any host becomes 127.0.0.1.
    /// </summary>
    public static Uri Target(Func<string, string?> environment)
    {
        var urls = Split(environment("ASPNETCORE_URLS"));
        if (urls.Length > 0)
        {
            var url = urls.FirstOrDefault(u => u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) ?? urls[0];
            var https = url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            var authority = url[(url.IndexOf("://", StringComparison.Ordinal) + 3)..].TrimEnd('/');
            var colon = authority.LastIndexOf(':');
            var port = colon >= 0 && int.TryParse(authority[(colon + 1)..], out var p) ? p : https ? 443 : 80;
            return Loopback(https, port);
        }

        if (Split(environment("ASPNETCORE_HTTP_PORTS")) is [var http, ..] && int.TryParse(http, out var httpPort))
            return Loopback(false, httpPort);
        if (Split(environment("ASPNETCORE_HTTPS_PORTS")) is [var tls, ..] && int.TryParse(tls, out var tlsPort))
            return Loopback(true, tlsPort);
        return Loopback(false, 5000);
    }

    /// <summary>
    /// The Host header host filtering will accept. The hosted desk names its one host in
    /// <c>AllowedHosts</c> and answers 400 to any other, 127.0.0.1 included.
    /// </summary>
    public static string? HostHeader(string? allowedHosts)
        => Split(allowedHosts).FirstOrDefault() is { } host && host != "*"
            ? host.StartsWith("*.", StringComparison.Ordinal) ? "probe" + host[1..] : host
            : null;

    /// <summary>One GET; 0 on a 2xx, 1 on anything else, with one line saying what.</summary>
    public static async Task<int> ProbeAsync(Uri target, string? host, HttpMessageHandler handler, TextWriter error, TimeSpan timeout)
    {
        using var client = new HttpClient(handler, disposeHandler: false) { Timeout = timeout };
        using var request = new HttpRequestMessage(HttpMethod.Get, target);
        if (host is not null)
            request.Headers.Host = host;

        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (response.IsSuccessStatusCode)
                return 0;
            await error.WriteLineAsync($"unhealthy: {target} answered {(int)response.StatusCode} {response.ReasonPhrase}");
        }
        catch (TaskCanceledException)
        {
            await error.WriteLineAsync($"unhealthy: {target} did not answer within {timeout.TotalSeconds:0.#}s");
        }
        catch (HttpRequestException e)
        {
            await error.WriteLineAsync($"unhealthy: {target} {e.Message}");
        }

        return 1;
    }

    private static Uri Loopback(bool https, int port)
        => new($"{(https ? "https" : "http")}://{IPAddress.Loopback}:{port}{Path}");

    private static string[] Split(string? value)
        => (value ?? "").Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
