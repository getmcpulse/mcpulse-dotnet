using System.Text.Json;

namespace MCPulse;

/// <summary>
/// Analytics for MCP servers.
/// </summary>
/// <remarks>
/// <para>
/// Three rules this package keeps, in order of how badly it would hurt to break one:
/// </para>
/// <list type="number">
///   <item><b>Never throw.</b> Every entry point swallows. If MCPulse fails inside a customer's
///   tool call, their tool fails and they blame us.</item>
///   <item><b>Never block.</b> Record, buffer, return. Nothing awaits the network on the path a
///   model is waiting on.</item>
///   <item><b>Never store customer data.</b> Sizes and hashes leave this process. Arguments and
///   results do not, and no option turns that off.</item>
/// </list>
/// </remarks>
public static class McPulse
{
    private static Stream? _stream;
    private static Log _log = DebugLog.Make(false);
    private static readonly Lock Gate = new();

    /// <summary>
    /// Configures MCPulse for this process.
    /// </summary>
    /// <remarks>
    /// Idempotent: calling it twice reuses the same session rather than opening a second one.
    /// Left unguarded, a server built per request would report every call under two sessions and
    /// double both the customer's numbers and their bill.
    /// </remarks>
    public static void Configure(McPulseOptions options)
    {
        try
        {
            lock (Gate)
            {
                _log = DebugLog.Make(options.Debug);

                if (!options.Active)
                {
                    _log("disabled — no key, or Enabled = false");
                    _stream = null;
                    return;
                }

                _stream = Stream.For(options, _log);
                _log("watching");
            }
        }
        catch
        {
            // Deliberately silent. Failing here must look like Configure was never called, and
            // there is no logger to complain to if the options were the thing that was malformed.
        }
    }

    /// <summary>
    /// Wraps one tool invocation so it is recorded.
    /// </summary>
    /// <param name="toolName">The tool being called.</param>
    /// <param name="arguments">The arguments as they arrived from the client.</param>
    /// <param name="clientName">The connected client, if the server knows it yet.</param>
    /// <param name="invoke">The handler to run.</param>
    /// <remarks>
    /// <para>
    /// The result is handed back untouched and an exception is re-raised untouched, so a wrapped
    /// call behaves exactly like an unwrapped one.
    /// </para>
    /// <para>
    /// Wrapping the invocation rather than watching from outside is what lets MCPulse tell a
    /// handler that threw from one that returned an error — a distinction an MCP server erases by
    /// converting both into <c>isError</c> before anything outside can see it.
    /// </para>
    /// </remarks>
    public static async Task<TResult> RecordAsync<TResult>(
        string toolName,
        object? arguments,
        string? clientName,
        Func<Task<TResult>> invoke)
    {
        var stream = Volatile.Read(ref _stream);
        if (stream is null)
        {
            // Not configured, or configured off. Stay entirely out of the way.
            return await invoke().ConfigureAwait(false);
        }

        stream.RememberClient(clientName);

        var startedAt = DateTimeOffset.UtcNow;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        object? result = null;
        var threw = false;
        try
        {
            var value = await invoke().ConfigureAwait(false);
            result = value;
            return value;
        }
        catch
        {
            threw = true;
            // Re-raised untouched: swallowing it would change what the customer's server does.
            throw;
        }
        finally
        {
            try
            {
                Record(stream, toolName, arguments, result, threw, startedAt, started);
            }
            catch
            {
                // Recording must never be the reason a tool call fails.
            }
        }
    }

    /// <summary>
    /// Reports the server's tool list, once per session.
    /// </summary>
    /// <param name="tools">
    /// The tools as the client will see them. <c>schema_bytes</c> is the cost of a tool's presence
    /// in the context window, so it is measured on the JSON that actually goes over the wire, not
    /// on the .NET type it was declared from — pass what <c>tools/list</c> returns.
    /// </param>
    /// <param name="clientName">The connected client, if the server knows it yet.</param>
    public static void RecordStartup(IEnumerable<object> tools, string? clientName = null)
    {
        try
        {
            var stream = Volatile.Read(ref _stream);
            if (stream is null || !stream.ClaimStartup())
            {
                return;
            }

            stream.RememberClient(clientName);

            var described = new List<Dictionary<string, object?>>();
            foreach (var tool in tools.Take(McPulseOptions.MaxTools))
            {
                var name = Emptiness.Member(tool, "Name") ?? Emptiness.Member(tool, "name");
                described.Add(new Dictionary<string, object?>
                {
                    ["name"] = Truncate(AsString(name) ?? "unknown", McPulseOptions.MaxToolName),
                    ["schema_bytes"] = Measure(tool),
                });
            }

            stream.Emit(new Dictionary<string, object?>
            {
                ["v"] = 1,
                ["type"] = "startup",
                ["session_id"] = stream.SessionId,
                ["client_name"] = stream.Client,
                ["tools"] = described,
            });

            stream.Log($"startup: {described.Count} tools, client {stream.Client}");
        }
        catch
        {
            // A startup payload is worth nothing next to the server that would have failed for it.
        }
    }

    /// <summary>
    /// The session these calls are being filed under, or <c>null</c> when recording is off.
    /// </summary>
    /// <remarks>
    /// Exposed so a server can log which session it joined, and so the shared-session guarantee
    /// can be asserted rather than assumed.
    /// </remarks>
    public static string? SessionId => Volatile.Read(ref _stream)?.SessionId;

    /// <summary>
    /// Sends everything buffered and stops accepting more.
    /// </summary>
    /// <remarks>
    /// A <c>ProcessExit</c> handler already does this. Call it by hand only when the server stops
    /// without the process exiting — a test suite, or a host that restarts servers in place.
    /// </remarks>
    public static Task FlushAllAsync() => Stream.FlushAllAsync();

    // ─── Building the payload ────────────────────────────────────────────────

    private static void Record(
        Stream stream,
        string toolName,
        object? arguments,
        object? result,
        bool threw,
        DateTimeOffset startedAt,
        long startedTicks)
    {
        var outcome = DecideOutcome(result, threw);
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(startedTicks);

        stream.Emit(new Dictionary<string, object?>
        {
            ["v"] = 1,
            ["type"] = "call",
            ["session_id"] = stream.SessionId,
            ["client_name"] = stream.Client,
            ["tool_name"] = Truncate(string.IsNullOrEmpty(toolName) ? "unknown" : toolName, McPulseOptions.MaxToolName),
            ["started_at"] = startedAt.ToString("o"),
            ["duration_ms"] = (long)Math.Max(0, elapsed.TotalMilliseconds),
            ["outcome"] = outcome.Wire(),
            ["response_bytes"] = Measure(result),
            // An error is not also an absence — it has its own outcome already.
            ["is_empty"] = outcome == Outcome.Ok && Emptiness.IsEmptyResult(result),
            ["args_hash"] = Hashing.ArgsHash(arguments),
        });
    }

    /// <summary>
    /// What the outcome was, given that the handler is what we wrapped.
    /// </summary>
    /// <remarks>
    /// Wrapping the invocation means a throw arrives here as a throw rather than as the
    /// <c>isError</c> result the server would have converted it into. What cannot be seen from
    /// here is <c>bad_args</c>: the .NET SDK binds and validates arguments before the handler
    /// runs, so a rejected argument set never reaches this wrapper. Reporting it anyway would mean
    /// reading the difference back out of an error message, and error strings are not an interface
    /// anyone promised to keep.
    /// </remarks>
    private static Outcome DecideOutcome(object? result, bool threw)
    {
        if (threw)
        {
            return Outcome.Crashed;
        }

        var flag = Emptiness.Member(result, "IsError") ?? Emptiness.Member(result, "isError");
        return AsBool(flag) == true ? Outcome.ToolError : Outcome.Ok;
    }

    /// <summary>What something costs the context window. Unserialisable means unmeasurable.</summary>
    private static int Measure(object? value)
    {
        if (value is null)
        {
            return 0;
        }

        try
        {
            return Sizes.Utf16Length(JsonSerializer.Serialize(value));
        }
        catch
        {
            return 0;
        }
    }

    private static string? AsString(object? value) => value switch
    {
        null => null,
        string text => text,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
        _ => value.ToString(),
    };

    private static bool? AsBool(object? value) => value switch
    {
        null => null,
        bool flag => flag,
        JsonElement { ValueKind: JsonValueKind.True } => true,
        JsonElement { ValueKind: JsonValueKind.False } => false,
        _ => null,
    };

    private static string Truncate(string value, int limit) =>
        value.Length <= limit ? value : value[..limit];
}
