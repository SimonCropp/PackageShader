namespace Alias.Lib.Metadata;

/// <summary>
/// Reads and writes the #GUID metadata heap.
/// </summary>
public sealed class GuidHeap
{
    private readonly byte[] _data;

    /// <summary>
    /// Size of indexes into this heap (2 or 4 bytes).
    /// </summary>
    public int IndexSize { get; set; } = 2;

    /// <summary>
    /// Raw data of this heap.
    /// </summary>
    public byte[] Data => _data;

    public GuidHeap(byte[] data)
    {
        _data = data;
    }

    /// <summary>
    /// Creates an empty GUID heap for building.
    /// </summary>
    public static GuidHeap CreateEmpty()
    {
        return new GuidHeap([]);
    }

    /// <summary>
    /// Reads a GUID at the given 1-based index.
    /// </summary>
    public Guid Read(uint index)
    {
        if (index == 0)
            return Guid.Empty;

        // GUID indices are 1-based
        var offset = (index - 1) * 16;
        if (offset + 16 > _data.Length)
            return Guid.Empty;

        var bytes = new byte[16];
        Array.Copy(_data, offset, bytes, 0, 16);
        return new Guid(bytes);
    }

    /// <summary>
    /// Gets the number of GUIDs in this heap.
    /// </summary>
    public int Count => _data.Length / 16;
}

/// <summary>
/// Builds a new GUID heap with modifications.
/// </summary>
public sealed class GuidHeapBuilder
{
    private readonly List<Guid> _guids = new();

    /// <summary>
    /// Gets or adds a GUID to the heap.
    /// </summary>
    public uint GetOrAdd(Guid value)
    {
        if (value == Guid.Empty)
            return 0;

        for (int i = 0; i < _guids.Count; i++)
        {
            if (_guids[i] == value)
                return (uint)(i + 1); // 1-based index
        }

        _guids.Add(value);
        return (uint)_guids.Count; // 1-based index
    }

    /// <summary>
    /// Copies all GUIDs from an existing heap.
    /// </summary>
    public void CopyFrom(GuidHeap source)
    {
        _guids.Clear();
        for (int i = 0; i < source.Count; i++)
        {
            var guid = source.Read((uint)(i + 1));
            _guids.Add(guid);
        }
    }

    /// <summary>
    /// Gets the current size of the heap.
    /// </summary>
    public uint Size => (uint)(_guids.Count * 16);

    /// <summary>
    /// Determines index size (2 or 4) based on GUID count.
    /// </summary>
    public int IndexSize => _guids.Count > 0xFFFF ? 4 : 2;

    /// <summary>
    /// Gets the heap data.
    /// </summary>
    public byte[] ToArray()
    {
        var data = new byte[_guids.Count * 16];
        for (int i = 0; i < _guids.Count; i++)
        {
            Array.Copy(_guids[i].ToByteArray(), 0, data, i * 16, 16);
        }
        return data;
    }
}
