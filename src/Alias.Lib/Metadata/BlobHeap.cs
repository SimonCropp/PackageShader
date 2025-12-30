namespace Alias.Lib.Metadata;

/// <summary>
/// Reads and writes the #Blob metadata heap.
/// </summary>
public sealed class BlobHeap
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

    public BlobHeap(byte[] data)
    {
        _data = data;
    }

    /// <summary>
    /// Creates an empty blob heap for building.
    /// </summary>
    public static BlobHeap CreateEmpty()
    {
        return new BlobHeap([0]); // Empty blob at index 0
    }

    /// <summary>
    /// Reads a blob at the given index.
    /// </summary>
    public byte[] Read(uint index)
    {
        if (index == 0 || index >= _data.Length)
            return [];

        int position = (int)index;
        var length = ReadCompressedUInt32(ref position);

        if (length == 0 || position + length > _data.Length)
            return [];

        var result = new byte[length];
        Array.Copy(_data, position, result, 0, (int)length);
        return result;
    }

    private uint ReadCompressedUInt32(ref int position)
    {
        var b = _data[position];

        if ((b & 0x80) == 0)
        {
            position++;
            return b;
        }

        if ((b & 0x40) == 0)
        {
            var result = (uint)((b & 0x3f) << 8) | _data[position + 1];
            position += 2;
            return result;
        }

        var value = (uint)((b & 0x1f) << 24)
            | (uint)(_data[position + 1] << 16)
            | (uint)(_data[position + 2] << 8)
            | _data[position + 3];
        position += 4;
        return value;
    }
}

/// <summary>
/// Builds a new blob heap with modifications.
/// </summary>
public sealed class BlobHeapBuilder
{
    private readonly MemoryStream _stream = new();
    private readonly Dictionary<byte[], uint> _blobs = new(new ByteArrayComparer());

    public BlobHeapBuilder()
    {
        // Empty blob at offset 0
        _stream.WriteByte(0);
        _blobs[Array.Empty<byte>()] = 0;
    }

    /// <summary>
    /// Gets or adds a blob to the heap.
    /// </summary>
    public uint GetOrAdd(byte[] value)
    {
        if (value == null || value.Length == 0)
            return 0;

        if (_blobs.TryGetValue(value, out var existing))
            return existing;

        var offset = (uint)_stream.Position;
        WriteCompressedUInt32((uint)value.Length);
        _stream.Write(value, 0, value.Length);

        _blobs[value] = offset;
        return offset;
    }

    /// <summary>
    /// Copies all blobs from an existing heap.
    /// </summary>
    public void CopyFrom(BlobHeap source)
    {
        // Copy the raw data
        if (source.Data.Length > 1)
        {
            _stream.SetLength(0);
            _stream.Write(source.Data, 0, source.Data.Length);
        }

        // We don't rebuild the index - new blobs will just be appended
        _blobs.Clear();
        _blobs[Array.Empty<byte>()] = 0;
    }

    private void WriteCompressedUInt32(uint value)
    {
        if (value < 0x80)
        {
            _stream.WriteByte((byte)value);
        }
        else if (value < 0x4000)
        {
            _stream.WriteByte((byte)(0x80 | (value >> 8)));
            _stream.WriteByte((byte)(value & 0xff));
        }
        else
        {
            _stream.WriteByte((byte)(0xc0 | (value >> 24)));
            _stream.WriteByte((byte)((value >> 16) & 0xff));
            _stream.WriteByte((byte)((value >> 8) & 0xff));
            _stream.WriteByte((byte)(value & 0xff));
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

    private class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public bool Equals(byte[]? x, byte[]? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x == null || y == null) return false;
            if (x.Length != y.Length) return false;
            for (int i = 0; i < x.Length; i++)
            {
                if (x[i] != y[i]) return false;
            }
            return true;
        }

        public int GetHashCode(byte[] obj)
        {
            if (obj == null || obj.Length == 0) return 0;
            int hash = 17;
            foreach (var b in obj)
                hash = hash * 31 + b;
            return hash;
        }
    }
}
