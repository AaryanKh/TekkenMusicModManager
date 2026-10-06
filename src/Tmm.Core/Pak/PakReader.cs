using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace Tmm.Core.Pak;

/// <summary>One file in a legacy .pak: where its data lives and how it is compressed.</summary>
public sealed record PakEntry(
    string Path,
    long Offset,              // of the inline entry header; the data follows it
    long Size,                // stored (compressed) size
    long UncompressedSize,
    int CompressionMethod,    // 0 = none, else 1-based index into the pak's method names
    int CompressionBlockSize,
    bool Encrypted,
    IReadOnlyList<(long Start, long End)> Blocks);   // relative to Offset

/// <summary>
/// Reads the index of a version 10/11 .pak (the "path hash" format, UE 4.26 to 5.x) and the data of its
/// entries, decompressing Oodle Kraken blocks with <see cref="Kraken"/>. Tekken 8 keeps its loose audio
/// (WwiseAudio/Media/*.wem) in such paks: pakchunk0 for the base game, pakchunk0_0_P and
/// pakchunk500(_0_P) for updates and Season 2. Encrypted paks and encrypted indexes are refused.
/// </summary>
public sealed class PakReader
{
    private const uint Magic = 0x5A6F12E1;
    private const int FooterSize = 221;   // v8+: guid, encrypted flag, magic, version, index offset/size, hash, 5 method names

    public string FilePath { get; }
    public int Version { get; private set; }
    public string MountPoint { get; private set; } = "";
    public IReadOnlyList<string> CompressionMethods { get; private set; } = Array.Empty<string>();
    /// <summary>Normalised full path ("Polaris/Content/...", single slashes) to entry. Deleted records are left out.</summary>
    public IReadOnlyDictionary<string, PakEntry> Entries { get; private set; } = new Dictionary<string, PakEntry>();

    private PakReader(string path) => FilePath = path;

    public static PakReader Open(string path)
    {
        var r = new PakReader(path);
        using var f = File.OpenRead(path);
        r.ReadIndex(f);
        return r;
    }

    /// <summary>"../../../Polaris/Content/WwiseAudio/Media//123.wem" becomes "Polaris/Content/WwiseAudio/Media/123.wem".</summary>
    public static string Normalize(string path)
    {
        var p = path.Replace('\\', '/');
        while (p.StartsWith("../", StringComparison.Ordinal)) p = p[3..];
        p = Regex.Replace(p, "/{2,}", "/");
        return p.TrimStart('/');
    }

    private void ReadIndex(FileStream f)
    {
        var name = System.IO.Path.GetFileName(FilePath);
        if (f.Length < FooterSize) throw new PackException($"{name} is too small to be a pak");
        var footer = new byte[FooterSize];
        f.Seek(-FooterSize, SeekOrigin.End);
        f.ReadExactly(footer);
        bool encryptedIndex = footer[16] != 0;
        if (BinaryPrimitives.ReadUInt32LittleEndian(footer.AsSpan(17)) != Magic)
            throw new PackException($"{name} is not a pak this app can read (no version 8+ footer)");
        Version = BinaryPrimitives.ReadInt32LittleEndian(footer.AsSpan(21));
        if (Version is < 10 or > 11) throw new PackException($"{name}: pak version {Version} is not supported");
        if (encryptedIndex) throw new PackException($"{name} has an encrypted index");
        long indexOffset = BinaryPrimitives.ReadInt64LittleEndian(footer.AsSpan(25));
        long indexSize = BinaryPrimitives.ReadInt64LittleEndian(footer.AsSpan(33));
        var methods = new List<string>();
        for (int i = 0; i < 5; i++)
        {
            var m = Encoding.ASCII.GetString(footer, 61 + 32 * i, 32).TrimEnd('\0');
            if (m.Length > 0) methods.Add(m);
        }
        CompressionMethods = methods;

        var index = ReadAt(f, indexOffset, indexSize);
        int p = 0;
        MountPoint = FString(index, ref p);
        p += 4;                     // entry count (the directory index is what lists them)
        p += 8;                     // path hash seed
        if (I32(index, ref p) != 0) p += 8 + 8 + 20;    // path hash index: not needed, the full one is read
        if (I32(index, ref p) == 0) throw new PackException($"{name} has no full directory index");
        long dirOffset = I64(index, ref p), dirSize = I64(index, ref p);
        p += 20;
        int encodedSize = I32(index, ref p);
        var encoded = index.AsSpan(p, encodedSize).ToArray();
        p += encodedSize;
        int unencodedCount = I32(index, ref p);
        var unencoded = new List<PakEntry>(unencodedCount);
        for (int i = 0; i < unencodedCount; i++) unencoded.Add(ReadEntry(index, ref p, ""));

        var dir = ReadAt(f, dirOffset, dirSize);
        int q = 0;
        int dirCount = I32(dir, ref q);
        var entries = new Dictionary<string, PakEntry>(StringComparer.OrdinalIgnoreCase);
        for (int d = 0; d < dirCount; d++)
        {
            var dirName = FString(dir, ref q);
            int fileCount = I32(dir, ref q);
            for (int k = 0; k < fileCount; k++)
            {
                var fileName = FString(dir, ref q);
                int location = I32(dir, ref q);
                if (location == int.MinValue) continue;                 // a patch's record of a deleted file
                var full = Normalize(MountPoint + dirName + fileName);
                var entry = location >= 0 ? DecodeEntry(encoded, location, full) : unencoded[-location - 1] with { Path = full };
                entries[full] = entry;
            }
        }
        Entries = entries;
    }

    /// <summary>FPakFile::DecodePakEntry: the bit-packed entry form used for most files.</summary>
    private PakEntry DecodeEntry(byte[] b, int at, string path)
    {
        int p = at;
        uint v = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p)); p += 4;
        int blockSize = (v & 0x3f) == 0x3f ? (int)U32(b, ref p) : (int)((v & 0x3f) << 11);
        int method = (int)((v >> 23) & 0x3f);
        long offset = (v & (1u << 31)) != 0 ? U32(b, ref p) : I64(b, ref p);
        long uncompressed = (v & (1u << 29)) != 0 ? U32(b, ref p) : I64(b, ref p);
        long size = method != 0 ? ((v & (1u << 30)) != 0 ? U32(b, ref p) : I64(b, ref p)) : uncompressed;
        bool encrypted = (v & (1u << 22)) != 0;
        int blockCount = (int)((v >> 6) & 0xffff);

        var blocks = new List<(long, long)>(blockCount);
        if (blockCount > 0)
        {
            // Block offsets are relative to the entry's own offset and start after its inline header.
            long start = HeaderSize(method, blockCount);
            if (blockCount == 1 && !encrypted) blocks.Add((start, start + size));
            else
                for (int i = 0; i < blockCount; i++)
                {
                    long len = U32(b, ref p);
                    blocks.Add((start, start + len));
                    start += encrypted ? (len + 15) & ~15L : len;
                }
        }
        // One block, and the size field was left at zero: the block is the whole file.
        if (blockCount <= 1 && blockSize == 0) blockSize = (int)uncompressed;
        return new PakEntry(path, offset, size, uncompressed, method, blockSize, encrypted, blocks);
    }

    /// <summary>A serialised FPakEntry (the form the index uses for entries that do not fit the packed one).</summary>
    private static PakEntry ReadEntry(byte[] b, ref int p, string path)
    {
        long offset = I64(b, ref p), size = I64(b, ref p), uncompressed = I64(b, ref p);
        int method = (int)U32(b, ref p);
        p += 20;                                                // hash
        var blocks = new List<(long, long)>();
        if (method != 0)
        {
            int count = I32(b, ref p);
            for (int i = 0; i < count; i++) blocks.Add((I64(b, ref p), I64(b, ref p)));
        }
        bool encrypted = (b[p++] & 1) != 0;
        int blockSize = (int)U32(b, ref p);
        return new PakEntry(path, offset, size, uncompressed, method, blockSize, encrypted, blocks);
    }

    /// <summary>Size of the inline FPakEntry header in front of an entry's data (version 10/11).</summary>
    private static long HeaderSize(int method, int blockCount) =>
        8 + 8 + 8 + 4 + 20 + (method != 0 ? 4 + 16L * blockCount : 0) + 1 + 4;

    /// <summary>
    /// The first <paramref name="maxBytes"/> bytes of an entry (all of it by default), decompressing only
    /// the blocks that cover them. Reading a WEM's header this way costs one 64 KiB block, not the file.
    /// </summary>
    public byte[] Read(PakEntry e, long maxBytes = long.MaxValue)
    {
        var name = System.IO.Path.GetFileName(FilePath);
        if (e.Encrypted) throw new PackException($"{name}: '{e.Path}' is encrypted");
        long want = Math.Min(maxBytes, e.UncompressedSize);
        var result = new byte[want];
        using var f = File.OpenRead(FilePath);
        if (e.CompressionMethod == 0)
        {
            f.Seek(e.Offset + HeaderSize(0, 0), SeekOrigin.Begin);
            f.ReadExactly(result);
            return result;
        }
        var method = e.CompressionMethod <= CompressionMethods.Count ? CompressionMethods[e.CompressionMethod - 1] : "?";
        if (!method.Equals("Oodle", StringComparison.OrdinalIgnoreCase))
            throw new PackException($"{name}: compression '{method}' is not supported");
        long written = 0;
        foreach (var (start, end) in e.Blocks)
        {
            if (written >= want) break;
            int raw = (int)Math.Min(e.CompressionBlockSize, e.UncompressedSize - written);
            var block = Kraken.Decompress(ReadAt(f, e.Offset + start, end - start), raw);
            int take = (int)Math.Min(raw, want - written);
            Array.Copy(block, 0, result, written, take);
            written += take;
        }
        if (written != want) throw new PackException($"{name}: '{e.Path}' ended early");
        return result;
    }

    private static byte[] ReadAt(FileStream f, long offset, long size)
    {
        var buf = new byte[size];
        f.Seek(offset, SeekOrigin.Begin);
        f.ReadExactly(buf);
        return buf;
    }

    private static int I32(byte[] b, ref int p) { int v = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p)); p += 4; return v; }
    private static uint U32(byte[] b, ref int p) { uint v = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p)); p += 4; return v; }
    private static long I64(byte[] b, ref int p) { long v = BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(p)); p += 8; return v; }

    private static string FString(byte[] b, ref int p)
    {
        int len = I32(b, ref p);
        if (len == 0) return "";
        string s;
        if (len < 0) { s = Encoding.Unicode.GetString(b, p, -len * 2); p += -len * 2; }
        else { s = Encoding.UTF8.GetString(b, p, len); p += len; }
        return s.TrimEnd('\0');
    }
}

/// <summary>How the engine ranks containers that hold the same file: a patch ("_P") beats the base, and
/// a higher patch number beats a lower one. Used for both .pak and .utoc/.ucas.</summary>
public static class PakPriority
{
    public static int Rank(string fileName)
    {
        var stem = System.IO.Path.GetFileNameWithoutExtension(fileName);
        var m = Regex.Match(stem, @"_(\d+)_P$", RegexOptions.IgnoreCase);
        if (m.Success) return 1 + int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        return stem.EndsWith("_P", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
    }
}
