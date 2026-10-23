namespace Lumen.Controls;

/// <summary>Small O(1) LRU used by the renderer.</summary>
internal sealed class LruCache<TKey, TValue> where TKey : notnull
{
    private sealed class Entry
    {
        public Entry(TKey key, TValue value)
        {
            Key = key;
            Value = value;
        }

        public TKey Key { get; }
        public TValue Value { get; set; }
    }

    private readonly int _capacity;
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _map;
    private readonly LinkedList<Entry> _order = new();

    public LruCache(int capacity)
    {
        _capacity = Math.Max(1, capacity);
        _map = new Dictionary<TKey, LinkedListNode<Entry>>(_capacity);
    }

    public int Count => _map.Count;

    public bool TryGet(TKey key, out TValue value)
    {
        if (_map.TryGetValue(key, out var node))
        {
            _order.Remove(node);
            _order.AddFirst(node);
            value = node.Value.Value;
            return true;
        }
        value = default!;
        return false;
    }

    public void Add(TKey key, TValue value)
    {
        if (_map.TryGetValue(key, out var existing))
        {
            existing.Value.Value = value;
            _order.Remove(existing);
            _order.AddFirst(existing);
            return;
        }

        if (_map.Count >= _capacity)
        {
            var last = _order.Last;
            if (last is not null)
            {
                _map.Remove(last.Value.Key);
                _order.RemoveLast();
            }
        }

        var node = _order.AddFirst(new Entry(key, value));
        _map[key] = node;
    }

    public void Clear()
    {
        _map.Clear();
        _order.Clear();
    }
}
