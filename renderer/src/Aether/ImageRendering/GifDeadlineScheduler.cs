using System.Diagnostics;

namespace Aether;

internal interface IGifDeadlineClient
{
    bool IsAnimationActive { get; }
    long PresentDueFrames(long nowTicks);
}

internal sealed class GifDeadlineScheduler
{
    private sealed record Entry(IGifDeadlineClient Client, long Deadline, long Generation);

    private readonly PriorityQueue<Entry, long> _queue = new();
    private readonly Dictionary<IGifDeadlineClient, long> _generations = new();

    public bool HasEntries => _generations.Count > 0;

    public void Schedule(IGifDeadlineClient client, long deadline)
    {
        long generation = _generations.TryGetValue(client, out long current) ? current + 1 : 1;
        _generations[client] = generation;
        _queue.Enqueue(new Entry(client, deadline, generation), deadline);
    }

    public void Remove(IGifDeadlineClient client) => _generations.Remove(client);

    public long? NextDeadline
    {
        get
        {
            TrimStale();
            return _queue.Count == 0 ? null : _queue.Peek().Deadline;
        }
    }

    public int PresentDue(long nowTicks)
    {
        int count = 0;
        while (true)
        {
            TrimStale();
            if (_queue.Count == 0 || _queue.Peek().Deadline > nowTicks)
                return count;

            Entry entry = _queue.Dequeue();
            if (!entry.Client.IsAnimationActive)
                continue;

            long next = entry.Client.PresentDueFrames(nowTicks);
            if (next > 0 && _generations.TryGetValue(entry.Client, out long generation) && generation == entry.Generation)
                _queue.Enqueue(new Entry(entry.Client, next, generation), next);
            count++;
        }
    }

    public static long MillisecondsToTicks(long milliseconds) =>
        checked((long)(milliseconds * (double)Stopwatch.Frequency / 1000.0));

    private void TrimStale()
    {
        while (_queue.Count > 0)
        {
            Entry entry = _queue.Peek();
            if (_generations.TryGetValue(entry.Client, out long generation) && generation == entry.Generation)
                return;
            _queue.Dequeue();
        }
    }
}
