using System.Collections.Concurrent;

namespace MCPulse;

/// <summary>
/// One session and one buffer per destination, for the life of the process.
/// </summary>
/// <remarks>
/// <para>
/// The obvious shape is to make both where the server is built, which is right for a stdio server
/// — one process, one server, one session — and wrong for an HTTP one. A streamable-HTTP server
/// builds a fresh scope per request, and every tool call would become a session of its own.
/// </para>
/// <para>
/// That is not a cosmetic difference. Retries are found by looking for the same tool twice inside
/// one session, and first-call success is defined as no retry following. With one call per session
/// there can never be a retry, so the server reports a perfect score however badly it is doing —
/// the one number this product exists to tell the truth about.
/// </para>
/// <para>
/// Keyed by endpoint and key rather than a bare singleton: two watched servers reporting to
/// different MCPs in one process are two different streams, and merging them would file one
/// customer's calls under another's.
/// </para>
/// </remarks>
internal sealed class Stream
{
    internal string SessionId { get; } = Hashing.NewSessionId();

    internal PayloadBuffer Buffer { get; }

    internal Log Log { get; }

    private string _clientName = "unknown";
    private int _startupSent;

    private Stream(McPulseOptions options, Log log)
    {
        Log = log;
        Buffer = new PayloadBuffer(options, log);
    }

    /// <summary>
    /// Whoever most recently identified themselves.
    /// </summary>
    /// <remarks>
    /// One value per process per destination, last identification wins. For a server with two
    /// concurrent clients that is an approximation, but it is the same approximation the shared
    /// session already makes, and a name that is occasionally the other client's beats a column
    /// that is always "unknown".
    /// </remarks>
    internal string Client => Volatile.Read(ref _clientName);

    internal void RememberClient(string? name)
    {
        if (!string.IsNullOrEmpty(name))
        {
            Volatile.Write(
                ref _clientName,
                name.Length > McPulseOptions.MaxClientName ? name[..McPulseOptions.MaxClientName] : name);
        }
    }

    internal bool ClaimStartup() => Interlocked.Exchange(ref _startupSent, 1) == 0;

    /// <summary>Diverts payloads away from the buffer. Only tests set it.</summary>
    internal static Action<Dictionary<string, object?>>? Sink { get; set; }

    /// <summary>Hands one payload to the buffer, or to a test's capture.</summary>
    internal void Emit(Dictionary<string, object?> payload)
    {
        var capture = Sink;
        if (capture is not null)
        {
            capture(payload);
            return;
        }

        Buffer.Add(payload);
    }

    // ─── The registry ────────────────────────────────────────────────────────

    private static readonly ConcurrentDictionary<string, Stream> Streams = new();
    private static int _hookRegistered;

    internal static Stream For(McPulseOptions options, Log log) =>
        Streams.GetOrAdd(options.StreamKey, _ =>
        {
            if (Interlocked.Exchange(ref _hookRegistered, 1) == 0)
            {
                // Registered once for the whole package, however many servers are watched.
                // ProcessExit runs before the runtime tears down, which is what makes the final
                // flush possible at all.
                AppDomain.CurrentDomain.ProcessExit += (_, _) => FlushAllAsync().GetAwaiter().GetResult();
            }

            return new Stream(options, log);
        });

    /// <summary>One last flush on the way out, so the final few calls of a session are not lost.</summary>
    internal static async Task FlushAllAsync()
    {
        var pending = Streams.Values.ToList();
        Streams.Clear();

        foreach (var stream in pending)
        {
            try
            {
                await stream.Buffer.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Nothing left to report to.
            }
        }
    }
}
