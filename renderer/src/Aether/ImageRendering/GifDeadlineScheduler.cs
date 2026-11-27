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
    private long _nextGeneration;

    public bool HasEntries => _generations.Count > 0;

    public void Schedule(IGifDeadlineClient client, long deadline)
    {
        long generation = checked(++_nextGeneration);
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
            if (!_generations.TryGetValue(entry.Client, out long generation) || generation != entry.Generation)
                continue;

            if (!entry.Client.IsAnimationActive)
            {
                _generations.Remove(entry.Client);
                continue;
            }

            long next = entry.Client.PresentDueFrames(nowTicks);
            if (!_generations.TryGetValue(entry.Client, out generation) || generation != entry.Generation)
            {
                count++;
                continue;
            }

            if (next > 0 && entry.Client.IsAnimationActive)
                _queue.Enqueue(new Entry(entry.Client, next, generation), next);
            else
                _generations.Remove(entry.Client);
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
