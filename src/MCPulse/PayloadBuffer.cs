using System.Threading.Channels;

namespace MCPulse;

/// <summary>
/// Holds payloads and sends them in batches, on a task of its own.
/// </summary>
/// <remarks>
/// <para>
/// The contract with the tool call that produced a payload is that <see cref="Add"/> returns
/// immediately and never throws. Everything expensive happens on a background loop, so no model
/// ever waits on MCPulse to answer.
/// </para>
/// <para>
/// A bounded channel with <see cref="BoundedChannelFullMode.DropOldest"/> is what makes rule 2 and
/// the memory ceiling the same mechanism: <c>TryWrite</c> never blocks and never fails, and when
/// the network is gone the oldest payloads fall off the back. Recent calls describe what the
/// server is doing now, and that is the more useful half of a buffer that could not be sent.
/// </para>
/// </remarks>
internal sealed class PayloadBuffer : IAsyncDisposable
{
    private readonly McPulseOptions _options;
    private readonly Log _log;
    private readonly Channel<Dictionary<string, object?>> _channel;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _worker;
    private int _closed;

    internal PayloadBuffer(McPulseOptions options, Log log)
    {
        _options = options;
        _log = log;
        _channel = Channel.CreateBounded<Dictionary<string, object?>>(
            new BoundedChannelOptions(McPulseOptions.MaxBuffered)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            });

        _worker = Task.Run(RunAsync);
    }

    /// <summary>Buffers one payload. Returns immediately, never throws.</summary>
    internal void Add(Dictionary<string, object?> payload)
    {
        try
        {
            if (Volatile.Read(ref _closed) == 1)
            {
                return;
            }

            if (!_channel.Writer.TryWrite(payload))
            {
                _log("buffer closed, dropped a payload");
            }
        }
        catch
        {
            // Recording must never be the reason a tool call fails.
        }
    }

    private async Task RunAsync()
    {
        var batch = new List<Dictionary<string, object?>>(McPulseOptions.FlushAtItems);

        try
        {
            while (await _channel.Reader.WaitToReadAsync(_stopping.Token).ConfigureAwait(false))
            {
                // Take what is there, then wait out the flush window for the rest, so a burst of
                // calls leaves as one batch rather than thirty.
                using var window = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
                window.CancelAfter(McPulseOptions.FlushEvery);

                while (batch.Count < McPulseOptions.FlushAtItems)
                {
                    if (_channel.Reader.TryRead(out var payload))
                    {
                        batch.Add(payload);
                        continue;
                    }

                    try
                    {
                        if (!await _channel.Reader.WaitToReadAsync(window.Token).ConfigureAwait(false))
                        {
                            break;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }

                await SendAsync(batch).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        finally
        {
            // Whatever is still queued gets one last chance on the way out.
            while (_channel.Reader.TryRead(out var payload))
            {
                batch.Add(payload);
            }

            await SendAsync(batch).ConfigureAwait(false);
        }
    }

    private async Task SendAsync(List<Dictionary<string, object?>> batch)
    {
        if (batch.Count == 0)
        {
            return;
        }

        var sending = batch.ToArray();
        batch.Clear();

        try
        {
            var sent = await Transport.PostBatchAsync(sending, _options).ConfigureAwait(false);
            _log($"{(sent ? "sent" : "dropped")} {sending.Length} payloads");
        }
        catch (Exception error)
        {
            _log("send failed", error);
        }
    }

    /// <summary>Final flush, best effort. After this the buffer accepts nothing more.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1)
        {
            return;
        }

        _channel.Writer.TryComplete();
        await _stopping.CancelAsync().ConfigureAwait(false);

        try
        {
            // Never longer than the exit window: holding a customer's shutdown open over
            // analytics is the same failure as holding their request path open.
            await _worker.WaitAsync(McPulseOptions.ExitFlush).ConfigureAwait(false);
        }
        catch
        {
            _log("exit flush timed out");
        }
        finally
        {
            _stopping.Dispose();
        }
    }
}
