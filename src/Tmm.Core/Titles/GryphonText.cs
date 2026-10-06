using System.Buffers.Binary;
using System.Text;

namespace Tmm.Core.Titles;

/// <summary>One string in a text table: its key, and where its value sits in the package.</summary>
public sealed record GryphonTextEntry(string Key, string Value, int ValueOffset, int ValueCapacity)
{
    /// <summary>Longest value, in UTF-8 bytes, that fits without moving anything else. The stored length
    /// includes the terminating null and is padded to 4 bytes, so a slot's room is its padded size - 1.</summary>
    public int MaxBytes => ValueCapacity - 1;
}

/// <summary>
/// Reads and patches a <c>GryphonTextBinaryAsset</c> package (the game's GTB_*.uasset text tables).
///
/// The asset has one property, DataSize, followed by a "gbtd" block: a 0x40-byte header (record count
/// at +0x14), then records of
///   "text", u32 16, u32 keySize, u32 valueSize, 8-byte key hash, key, value
/// where key and value are UTF-8, null-terminated and zero-padded to a multiple of 4. The block runs to
/// the end of the package, and DataSize equals its length.
///
/// Patching only ever rewrites a value inside its existing padded size, so no length, offset or hash
/// anywhere in the package changes. That keeps the cooked package valid without re-serialising it.
/// </summary>
public sealed class GryphonText
{
    private static readonly byte[] BlockMagic = "gbtd"u8.ToArray();
    private static readonly byte[] RecordMagic = "text"u8.ToArray();

    private readonly byte[] _data;
    private readonly Dictionary<string, GryphonTextEntry> _byKey;

    public IReadOnlyList<GryphonTextEntry> Entries { get; }

    private GryphonText(byte[] data, List<GryphonTextEntry> entries)
    {
        _data = data;
        Entries = entries;
        _byKey = entries.ToDictionary(e => e.Key, StringComparer.Ordinal);
    }

    /// <summary>Parse a copy of <paramref name="package"/>. Throws when it is not a text table this code
    /// understands, rather than guessing.</summary>
    public static GryphonText Parse(byte[] package)
    {
        var d = (byte[])package.Clone();
        int block = d.AsSpan().IndexOf(BlockMagic);
        if (block < 8) throw new TmmException("not a Gryphon text table: no gbtd block");
        int dataSize = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(block - 8));
        if (block + dataSize != d.Length)
            throw new TmmException($"text table DataSize {dataSize} does not match the block ({d.Length - block} bytes)");
        int count = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(block + 0x14));

        var entries = new List<GryphonTextEntry>(count);
        int p = block + 0x40;
        for (int i = 0; i < count; i++)
        {
            if (p + 24 > d.Length || !d.AsSpan(p, 4).SequenceEqual(RecordMagic))
                throw new TmmException($"text table record {i} is malformed at 0x{p:X}");
            int keySize = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p + 8));
            int valueSize = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p + 12));
            int keyAt = p + 24, valueAt = keyAt + keySize;
            if (keySize <= 0 || valueSize <= 0 || valueAt + valueSize > d.Length)
                throw new TmmException($"text table record {i} overruns the block");
            entries.Add(new GryphonTextEntry(Utf8(d, keyAt, keySize), Utf8(d, valueAt, valueSize), valueAt, valueSize));
            p = valueAt + valueSize;
        }
        if (p != d.Length) throw new TmmException("text table has trailing bytes after its last record");
        return new GryphonText(d, entries);
    }

    private static string Utf8(byte[] d, int at, int size)
    {
        int len = Array.IndexOf(d, (byte)0, at, size);
        return Encoding.UTF8.GetString(d, at, (len < 0 ? at + size : len) - at);
    }

    public GryphonTextEntry? Find(string key) => _byKey.TryGetValue(key, out var e) ? e : null;

    /// <summary>Replace a value in place. The text must fit <see cref="GryphonTextEntry.MaxBytes"/>.</summary>
    public void Set(string key, string value)
    {
        var e = Find(key) ?? throw new TmmException($"text table has no entry '{key}'");
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > e.MaxBytes)
            throw new TmmException($"'{value}' is {bytes.Length} bytes; '{key}' holds at most {e.MaxBytes}");
        if (Array.IndexOf(bytes, (byte)0) >= 0) throw new TmmException("a title cannot contain a null character");
        var slot = _data.AsSpan(e.ValueOffset, e.ValueCapacity);
        slot.Clear();
        bytes.CopyTo(slot);
    }

    /// <summary>The package bytes, with every <see cref="Set"/> applied. Same length as the input.</summary>
    public byte[] ToArray() => (byte[])_data.Clone();
}
