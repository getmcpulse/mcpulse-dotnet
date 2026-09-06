namespace MCPulse;

/// <summary>
/// Debug output, on stderr.
/// </summary>
/// <remarks>
/// <para>
/// stdout is the transport for a stdio MCP server — a single stray line there corrupts the
/// JSON-RPC stream and takes the customer's server down with it. This is the one thing in the
/// package that would be trivially easy to get wrong and catastrophic to ship, so it goes through
/// one place.
/// </para>
/// <para>
/// Deliberately not <c>ILogger</c>: a library that logs through the host's configuration can end
/// up on stdout because of a setting it never saw. An MCP server on stdio is exactly the
/// application most likely to have such a setting.
/// </para>
/// </remarks>
internal delegate void Log(string message, object? detail = null);

internal static class DebugLog
{
    internal static Log Make(bool debug)
    {
        if (!debug)
        {
            return static (_, _) => { };
        }

        return static (message, detail) =>
        {
            try
            {
                var suffix = detail is null ? string.Empty : $" {Format(detail)}";
                Console.Error.WriteLine($"[mcpulse] {message}{suffix}");
            }
            catch
            {
                // Logging is never worth an exception.
            }
        };
    }

    private static string Format(object detail) => detail switch
    {
        Exception error => $"{error.GetType().Name}: {error.Message}",
        _ => detail.ToString() ?? string.Empty,
    };
}
