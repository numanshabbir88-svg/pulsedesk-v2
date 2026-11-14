namespace PulseDesk.Agent;

/// <summary>
/// HttpClient factory for the agent. The agent must talk directly to its own server:
/// a stale/broken machine or user proxy (e.g. 127.0.0.1:xxxx) otherwise makes
/// enrollment and telemetry fail with "connection refused".
/// </summary>
internal static class HttpFactory
{
    public static HttpClient Create(TimeSpan timeout)
    {
        var handler = new HttpClientHandler
        {
            UseProxy = false,
            Proxy = null,
            AllowAutoRedirect = true,
            AutomaticDecompression = System.Net.DecompressionMethods.All
        };
        return new HttpClient(handler) { Timeout = timeout };
    }
}
