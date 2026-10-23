namespace Lumen.Terminal;

/// <summary>
/// Fixed-capacity ring buffer for scrollback. Adding a line at the limit and
/// trimming the oldest line are both O(1), unlike List.RemoveAt(0).
/// </summary>
public sealed class ScrollbackBuffer : IReadOnlyList<TerminalLine>
{
    private TerminalLine[] _items;
    private int _start;
    private int _count;

    public ScrollbackBuffer(int capacity)
    {
        _items = new TerminalLine[Math.Max(1, capacity)];
        Capacity = Math.Max(0, capacity);
    }

    public int Capacity { get; private set; }

    public int Count => _count;

    public TerminalLine this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
            return _items[(_start + index) % _items.Length];
        }
    }

    public void Add(TerminalLine line)
    {
        if (Capacity <= 0) return;
        if (_count < Capacity)
        {
            _items[(_start + _count) % _items.Length] = line;
            _count++;
            return;
        }

        _items[_start] = line;
        _start = (_start + 1) % _items.Length;
    }

    public void Clear()
    {
        Array.Clear(_items);
        _start = 0;
        _count = 0;
    }

    public void TrimTo(int capacity)
    {
        capacity = Math.Max(0, capacity);
        if (capacity == Capacity) return;

        if (capacity == 0)
        {
            Clear();
            Capacity = 0;
            _items = new TerminalLine[1];
            return;
        }

        if (capacity < _count)
        {
            _start = (_start + (_count - capacity)) % _items.Length;
            _count = capacity;
        }

        if (capacity != _items.Length)
        {
            var next = new TerminalLine[capacity];
            for (int i = 0; i < _count; i++) next[i] = _items[(_start + i) % _items.Length];
            _items = next;
            _start = 0;
        }
        Capacity = capacity;
    }

    public IEnumerator<TerminalLine> GetEnumerator()
    {
        for (int i = 0; i < _count; i++) yield return this[i];
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
