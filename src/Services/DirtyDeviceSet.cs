using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace ESPresense.Services;

/// <summary>
/// A coalescing work queue keyed by device id.
/// </summary>
/// <remarks>
/// Marking an id that is already pending is a no-op, so a device that receives many MQTT messages
/// (one per node that hears it) between two consumer passes is yielded exactly once per pass.
/// Ids are yielded in first-marked order. An id is removed from the pending set immediately before it is
/// yielded, so a mark that arrives while the device is being processed queues it again for the next pass;
/// no update is lost. The pending set is bounded by the number of distinct device ids.
/// </remarks>
internal sealed class DirtyDeviceSet
{
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> _order = new();
    private readonly Channel<bool> _signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true
    });

    /// <summary>Number of ids currently pending.</summary>
    public int Count => _pending.Count;

    /// <summary>
    /// Marks <paramref name="id"/> as needing work. Returns true if it was not already pending.
    /// </summary>
    public bool Mark(string id)
    {
        if (!_pending.TryAdd(id, 0)) return false;
        _order.Enqueue(id);
        _signal.Writer.TryWrite(true);
        return true;
    }

    /// <summary>
    /// Discards every pending id and returns how many were dropped.
    /// </summary>
    public int Clear()
    {
        var cleared = 0;
        while (_order.TryDequeue(out var id))
        {
            if (_pending.TryRemove(id, out _)) cleared++;
        }
        // A concurrent Mark may have added to _pending but not yet to _order; drop those too so the id
        // cannot get stuck as "pending" with no queue entry (Mark would then refuse it forever).
        _pending.Clear();
        return cleared;
    }

    /// <summary>
    /// Yields pending ids as they become available, each at most once per pass, until
    /// <paramref name="cancellationToken"/> is cancelled (which throws <see cref="OperationCanceledException"/>,
    /// matching <c>ChannelReader.ReadAllAsync</c>).
    /// </summary>
    public async IAsyncEnumerable<string> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (await _signal.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // Consume the wake-up before draining so a mark that lands while we drain re-arms the signal.
            _signal.Reader.TryRead(out _);

            while (_order.TryDequeue(out var id))
            {
                cancellationToken.ThrowIfCancellationRequested();
                _pending.TryRemove(id, out _);
                yield return id;
            }
        }
    }
}
