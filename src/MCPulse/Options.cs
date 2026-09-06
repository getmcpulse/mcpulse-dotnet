namespace MCPulse;

/// <summary>Everything <see cref="MCPulse"/> accepts, and what it means when you leave it out.</summary>
public sealed record McPulseOptions
{
    /// <summary>Where payloads go when <see cref="Endpoint"/> is not set.</summary>
    public const string DefaultEndpoint = "https://api.getmcpulse.com";

    /// <summary>Flush when either is reached, whichever comes first.</summary>
    internal const int FlushAtItems = 30;

    internal static readonly TimeSpan FlushEvery = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Hard ceiling on the buffer. Reached only when the network is gone; past it the oldest
    /// payloads are dropped, because a customer's server running out of memory over our analytics
    /// is the one failure we must never cause.
    /// </summary>
    internal const int MaxBuffered = 1000;

    /// <summary>Caps, so one malformed name cannot bloat a batch.</summary>
    internal const int MaxToolName = 200;

    internal const int MaxClientName = 128;

    internal const int MaxTools = 500;

    /// <summary>Best-effort window for the final flush on the way out, and the cap on one batch.</summary>
    internal static readonly TimeSpan ExitFlush = TimeSpan.FromSeconds(1);

    internal static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Ingest key, <c>mp_live_…</c>, minted per MCP in the dashboard.</summary>
    public required string Key { get; init; }

    /// <summary>Point at a local API while developing.</summary>
    public string Endpoint { get; init; } = DefaultEndpoint;

    /// <summary><c>false</c> makes instrumentation a no-op — useful in tests and CI.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Log what is being sent, and why a send failed, to stderr.</summary>
    public bool Debug { get; init; }

    internal string CleanKey => Key?.Trim() ?? string.Empty;

    internal string CleanEndpoint =>
        (string.IsNullOrWhiteSpace(Endpoint) ? DefaultEndpoint : Endpoint).TrimEnd('/');

    /// <summary>
    /// Whether anything should be recorded at all.
    /// </summary>
    /// <remarks>
    /// An empty key turns the SDK off: a server started without its key configured should be
    /// silent, not a source of 401s on every flush.
    /// </remarks>
    internal bool Active => Enabled && CleanKey.Length > 0;

    internal string StreamKey => $"{CleanEndpoint}|{CleanKey}";
}

/// <summary>How a tool call ended. Exactly one of these, always.</summary>
public enum Outcome
{
    /// <summary>Ran and returned a result.</summary>
    Ok,

    /// <summary>Arguments failed validation; the handler never ran.</summary>
    BadArgs,

    /// <summary>Ran and returned <c>isError: true</c>.</summary>
    ToolError,

    /// <summary>Threw.</summary>
    Crashed,
}

internal static class OutcomeExtensions
{
    /// <summary>The value that goes on the wire.</summary>
    internal static string Wire(this Outcome outcome) => outcome switch
    {
        Outcome.Ok => "ok",
        Outcome.BadArgs => "bad_args",
        Outcome.ToolError => "tool_error",
        Outcome.Crashed => "crashed",
        _ => "ok",
    };
}

internal static class Sizes
{
    /// <summary>
    /// The length of <paramref name="text"/> in UTF-16 code units.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>response_bytes</c> and <c>schema_bytes</c> are, today, what JavaScript's
    /// <c>String.length</c> returns — code units, not bytes. The fields are named for bytes and
    /// hold code units, so <c>"café"</c> measures 4 and an emoji measures 2.
    /// </para>
    /// <para>
    /// That is a known wart in the wire format, and fixing it is a pending decision. Until it is
    /// made, every port reproduces the TypeScript behaviour rather than each inventing its own,
    /// because the whole value of these numbers is that they are comparable across a customer's
    /// servers. When the wire fixes it, this method is the one place that changes.
    /// </para>
    /// <para>
    /// .NET is one of the two languages where this needs no work: a .NET string is already UTF-16.
    /// </para>
    /// </remarks>
    internal static int Utf16Length(string text) => text.Length;
}
