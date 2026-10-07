namespace Jellyfin.Plugin.Coast.Updates;

// A bounded, coalesced hint journal. Loss/restarts explicitly request reconciliation;
// it never claims to be a durable replacement for Jellyfin's own database.
internal sealed record ServerChange(long Sequence, string Kind, string? ItemId, string? UserId, string? ItemType);
internal sealed record ChangePage(string Epoch, long Cursor, bool Reset, bool More, IReadOnlyList<ServerChange> Changes);
internal sealed class ChangeJournal(int capacity = 4096)
{
    private readonly object _gate = new();
    private string _epoch = Guid.NewGuid().ToString("N");
    private readonly SortedDictionary<long, ServerChange> _changes = [];
    private readonly Dictionary<(string Kind, string? ItemId, string? UserId), long> _keys = [];
    private long _sequence;
    private long _droppedThrough;

    public void Add(string kind, Guid? itemId = null, Guid? userId = null, string? itemType = null)
    {
        lock (_gate)
        {
            var key = (kind, itemId?.ToString("N"), userId?.ToString("N"));
            if (_keys.TryGetValue(key, out var previous)) _changes.Remove(previous);
            var change = new ServerChange(++_sequence, key.kind, key.Item2, key.Item3, itemType);
            _keys[key] = change.Sequence;
            _changes[change.Sequence] = change;
            while (_changes.Count > capacity)
            {
                var first = _changes.First().Value;
                _changes.Remove(first.Sequence);
                _keys.Remove((first.Kind, first.ItemId, first.UserId));
                _droppedThrough = first.Sequence;
            }
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _epoch = Guid.NewGuid().ToString("N");
            _changes.Clear(); _keys.Clear(); _sequence = 0; _droppedThrough = 0;
        }
    }

    public ChangePage Read(string? epoch, long cursor, int limit = 200)
    {
        lock (_gate)
        {
            if (epoch != _epoch || cursor < _droppedThrough || cursor < 0 || cursor > _sequence)
                return new(_epoch, _sequence, true, false, []);
            var changes = _changes.Values.Where(change => change.Sequence > cursor).Take(Math.Clamp(limit, 1, 200)).ToArray();
            var next = changes.Length == 0 ? _sequence : changes[^1].Sequence;
            return new(_epoch, next, false, next < _sequence, changes);
        }
    }
}
