namespace SharpFAI.Player.ModSupport;

internal sealed class ModPreparationCache<TKey, TValue>(int capacity) where TKey : notnull
{
    private readonly object _gate = new();
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _entries = [];
    private readonly LinkedList<(TKey Key, TValue Value)> _recent = [];

    public bool TryGet(TKey key, out TValue? value)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var node))
            {
                value = default;
                return false;
            }
            _recent.Remove(node);
            _recent.AddFirst(node);
            value = node.Value.Value;
            return true;
        }
    }

    public void Store(TKey key, TValue value)
    {
        lock (_gate)
        {
            if (_entries.Remove(key, out var old)) _recent.Remove(old);
            _entries.Add(key, _recent.AddFirst((key, value)));
            if (_entries.Count <= capacity) return;
            var oldest = _recent.Last!;
            _entries.Remove(oldest.Value.Key);
            _recent.RemoveLast();
        }
    }
}
