using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MCPulse;

/// <summary>Posting one batch.</summary>
internal static class Transport
{
    // One client for the process. A fresh HttpClient per send exhausts sockets, and this library
    // runs inside somebody else's server where that failure would be blamed on them.
    private static readonly HttpClient Client = new() { Timeout = McPulseOptions.SendTimeout };

    private static readonly JsonSerializerOptions Json = new()
    {
        // The API parses this; it is never hashed, so the writer's escaping does not matter here.
        // Canonical output comes from Canonical, not from System.Text.Json.
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Sends one batch and reports whether it landed. Never throws — a caller must not have to
    /// catch.
    /// </summary>
    /// <remarks>
    /// A failed batch is dropped, deliberately. Retrying means either a queue that grows while the
    /// network is down, or duplicate rows when a 202 is lost on the way back. Neither is worth it
    /// for analytics: the next flush is five seconds away, and a gap in a chart is a far smaller
    /// problem than memory growth inside someone else's server.
    /// </remarks>
    internal static async Task<bool> PostBatchAsync(
        IReadOnlyList<Dictionary<string, object?>> payloads,
        McPulseOptions options,
        CancellationToken cancellationToken = default)
    {
        if (payloads.Count == 0)
        {
            return true;
        }

        try
        {
            var body = JsonSerializer.Serialize(new { batch = payloads }, Json);

            using var request = new HttpRequestMessage(
                HttpMethod.Post, $"{options.CleanEndpoint}/v1/ingest")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.CleanKey);
            request.Headers.UserAgent.ParseAdd("mcpulse-dotnet");

            using var response = await Client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            return response.IsSuccessStatusCode;
        }
        catch
        {
            // DNS, TLS, timeout, a proxy that hung up, a cancelled shutdown. All the same to us.
            return false;
        }
    }
}
