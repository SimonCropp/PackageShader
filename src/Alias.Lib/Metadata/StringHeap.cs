using System.Text;

namespace Alias.Lib.Metadata;

/// <summary>
/// Reads and writes the #Strings metadata heap.
/// </summary>
public sealed class StringHeap
{
    private readonly byte[] _data;
    private readonly Dictionary<uint, string> _cache = new();
    private readonly Dictionary<string, uint> _indexCache = new();

    /// <summary>
    /// Size of indexes into this heap (2 or 4 bytes).
    /// </summary>
    public int IndexSize { get; set; } = 2;

    /// <summary>
    /// Raw data of this heap.
    /// </summary>
    public byte[] Data => _data;

    public StringHeap(byte[] data)
    {
        _data = data;
    }

    /// <summary>
    /// Creates an empty string heap for building.
    /// </summary>
    public static StringHeap CreateEmpty()
    {
        return new StringHeap([0]); // Empty string at index 0
    }

    /// <summary>
    /// Reads a string at the given index.
    /// </summary>
    public string Read(uint index)
    {
        if (index == 0)
            return string.Empty;

        if (_cache.TryGetValue(index, out var cached))
            return cached;

        if (index >= _data.Length)
            return string.Empty;

        var end = (int)index;
        while (end < _data.Length && _data[end] != 0)
            end++;

        var str = Encoding.UTF8.GetString(_data, (int)index, end - (int)index);
        _cache[index] = str;
        return str;
    }

    /// <summary>
    /// Finds the index of a string if it exists, or returns null.
    /// </summary>
    public uint? FindIndex(string value)
    {
        if (string.IsNullOrEmpty(value))
            return 0;

        if (_indexCache.TryGetValue(value, out var idx))
            return idx;

        // Linear scan (slow, but only used during writing)
        var bytes = Encoding.UTF8.GetBytes(value);
        for (uint i = 1; i < _data.Length; i++)
        {
            if (i + bytes.Length >= _data.Length)
                break;

            bool match = true;
            for (int j = 0; j < bytes.Length; j++)
            {
                if (_data[i + j] != bytes[j])
                {
                    match = false;
                    break;
                }
            }

            if (match && _data[i + bytes.Length] == 0)
            {
                _indexCache[value] = i;
                return i;
            }
        }

        return null;
    }
}

/// <summary>
/// Builds a new string heap with modifications.
/// </summary>
public sealed class StringHeapBuilder
{
    private readonly MemoryStream _stream = new();
    private readonly Dictionary<string, uint> _strings = new();

    public StringHeapBuilder()
    {
        // Empty string at offset 0
        _stream.WriteByte(0);
        _strings[""] = 0;
    }

    /// <summary>
    /// Gets or adds a string to the heap.
    /// </summary>
    public uint GetOrAdd(string value)
    {
        if (string.IsNullOrEmpty(value))
            return 0;

        if (_strings.TryGetValue(value, out var existing))
            return existing;

        var offset = (uint)_stream.Position;
        var bytes = Encoding.UTF8.GetBytes(value);
        _stream.Write(bytes, 0, bytes.Length);
        _stream.WriteByte(0); // Null terminator

        _strings[value] = offset;
        return offset;
    }

    /// <summary>
    /// Copies all strings from an existing heap.
    /// </summary>
    public void CopyFrom(StringHeap source)
    {
        // Copy the raw data (skipping our initial null byte since source has it)
        if (source.Data.Length > 1)
        {
            _stream.SetLength(0);
            _stream.Write(source.Data, 0, source.Data.Length);
        }

        // Rebuild the string index
        _strings.Clear();
        _strings[""] = 0;

        for (uint i = 1; i < source.Data.Length;)
        {
            var str = source.Read(i);
            _strings[str] = i;
            i += (uint)Encoding.UTF8.GetByteCount(str) + 1;
        }
    }

    /// <summary>
    /// Gets the current size of the heap.
    /// </summary>
    public uint Size => (uint)_stream.Length;

    /// <summary>
    /// Determines index size (2 or 4) based on heap size.
    /// </summary>
    public int IndexSize => Size > 0xFFFF ? 4 : 2;

    /// <summary>
    /// Gets the heap data, aligned to 4 bytes.
    /// </summary>
    public byte[] ToArray()
    {
        var data = _stream.ToArray();
        var aligned = (data.Length + 3) & ~3;
        if (aligned > data.Length)
        {
            var result = new byte[aligned];
            Array.Copy(data, result, data.Length);
            return result;
        }
        return data;
    }
}
