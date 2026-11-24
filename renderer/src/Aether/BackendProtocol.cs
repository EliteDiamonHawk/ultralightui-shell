using System.Text.Json;

namespace Aether;

internal sealed record BackendEvent(string Name, string StreamId, string Type, JsonElement? Data, string? BackendId = null);

internal sealed class BackendEventQueue
{
    private const int Capacity = 256;
    private readonly Queue<BackendEvent> _events = new();
    private bool _overflowReported;

    public int Count { get { lock (_events) return _events.Count; } }

    public void Enqueue(BackendEvent value, Action<BackendEvent>? overflow)
    {
        BackendEvent? overflowEvent = null;
        lock (_events)
        {
            if (_events.Count >= Capacity)
            {
                _events.Dequeue();
                if (!_overflowReported)
                {
                    _overflowReported = true;
                    overflowEvent = new BackendEvent("stream.overflow", value.StreamId, value.Type, null, value.BackendId);
                }
            }
            _events.Enqueue(value);
        }
        if (overflowEvent != null)
            overflow?.Invoke(overflowEvent);
    }

    public BackendEvent[] Drain()
    {
        lock (_events)
        {
            BackendEvent[] result = _events.ToArray();
            _events.Clear();
            _overflowReported = false;
            return result;
        }
    }
}
