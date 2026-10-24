namespace Lumen.Terminal;

/// <summary>
/// Bounded byte buffer feeding the live hex view. Kept independent from the VT
/// parser so every received byte is visible even when it is not printable.
/// </summary>
public sealed class HexBuffer
{
    public const int BytesPerLine = 16;

    private readonly object _gate = new();
    private readonly List<byte> _bytes = new();
    private readonly int _capacity;
    private long _baseOffset;

    public HexBuffer(int capacity = 1024 * 1024)
    {
        _capacity = Math.Max(BytesPerLine * 64, capacity);
    }

    public long BaseOffset
    {
        get { lock (_gate) return _baseOffset; }
    }

    public long TotalBytes
    {
        get { lock (_gate) return _baseOffset + _bytes.Count; }
    }

    public int Count
    {
        get { lock (_gate) return _bytes.Count; }
    }

    public int LineCount
    {
        get
        {
            lock (_gate)
            {
                return (_bytes.Count + BytesPerLine - 1) / BytesPerLine;
            }
        }
    }

    public void Append(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        lock (_gate)
        {
            foreach (byte b in data) _bytes.Add(b);
            Trim();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _bytes.Clear();
            _baseOffset = 0;
        }
    }

    public long LineOffset(int lineIndex) => BaseOffset + (long)lineIndex * BytesPerLine;

    /// <summary>Copies one rendered line of bytes and returns how many were written.</summary>
    public int CopyLine(int lineIndex, Span<byte> destination)
    {
        lock (_gate)
        {
            long relative = (long)lineIndex * BytesPerLine;
            if (relative < 0 || relative >= _bytes.Count) return 0;

            int offset = (int)relative;
            int count = Math.Min(Math.Min(BytesPerLine, destination.Length), _bytes.Count - offset);
            for (int i = 0; i < count; i++) destination[i] = _bytes[offset + i];
            return count;
        }
    }

    public byte[] ToArray()
    {
        lock (_gate) return _bytes.ToArray();
    }

    private void Trim()
    {
        if (_bytes.Count <= _capacity) return;

        int remove = _bytes.Count - _capacity;
        remove = (remove + BytesPerLine - 1) / BytesPerLine * BytesPerLine;
        if (remove >= _bytes.Count) remove = _bytes.Count / BytesPerLine * BytesPerLine;
        if (remove <= 0) return;

        _bytes.RemoveRange(0, remove);
        _baseOffset += remove;
    }
}
