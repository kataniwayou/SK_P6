namespace Messaging.Contracts.Diagnostics;

/// <summary>
/// Offline container healthcheck (offline-build support). The runtime image no longer ships
/// <c>wget</c>/<c>curl</c> (dropped so the image builds with no Debian apt network), so the compose
/// healthcheck runs the app itself as a dotnet self-probe: <c>dotnet &lt;App&gt;.dll --healthcheck</c>.
/// Each entry point checks <c>args is ["--healthcheck"]</c> at the top and returns this method's exit
/// code (0 healthy, 1 not) WITHOUT building the host. Kubernetes does not use this — its readiness/
/// liveness probes are <c>httpGet</c> (kubelet-side, no in-container tool required).
/// </summary>
public static class OfflineHealthProbe
{
    /// <summary>
    /// GET <c>http://localhost:{port}/health/ready</c> with a short timeout; exit 0 on 2xx, else 1.
    /// Never throws — any failure (connection refused, timeout, non-2xx) is exit 1 (unhealthy).
    /// </summary>
    public static async Task<int> RunAsync(int port)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var response = await client.GetAsync($"http://localhost:{port}/health/ready")
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch
        {
            return 1;
        }
    }
}
