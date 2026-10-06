using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Tmm.Core.Pak;

// Minimal IoStore (.utoc/.ucas) support: enough to read a container's index from the game install and
// to write a small uncompressed container of our own. Cooked assets (anything that is not a loose file
// like a .wem) live in IoStore on Tekken 8, so overriding one needs a container rather than a plain pak.
//
// Layouts follow UE 5.1, TOC version 5 (PerfectHashWithOverflow) and container header version 2
// (OptionalSegmentPackages), both read off the game's own pakchunk files. The game compresses with
// Oodle, which this code never decompresses: it reads the uncompressed index, the BLAKE3 chunk hashes
// and the container header (stored uncompressed), and writes everything uncompressed.

/// <summary>
/// 12-byte chunk id: 8-byte id, 2-byte big-endian index, one more byte, the chunk type. Byte 10 is
/// padding for packages but carries data in other chunk types (about 60% of pakchunk0's chunks), so it
/// is kept: the perfect hash runs over all 12 bytes.
/// </summary>
public readonly record struct IoChunkId(ulong Id, ushort Index, byte Extra, byte Type)
{
    public const byte ExportBundleData = 1;
    public const byte ContainerHeader = 6;

    public IoChunkId(ulong id, ushort index, byte type) : this(id, index, 0, type) { }

    public static IoChunkId Package(ulong packageId) => new(packageId, 0, ExportBundleData);
    public static IoChunkId Header(ulong containerId) => new(containerId, 0, ContainerHeader);

    public byte[] ToBytes()
    {
        var b = new byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(b, Id);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(8), Index);
        b[10] = Extra;
        b[11] = Type;
        return b;
    }

    public static IoChunkId FromBytes(ReadOnlySpan<byte> b) =>
        new(BinaryPrimitives.ReadUInt64LittleEndian(b), BinaryPrimitives.ReadUInt16BigEndian(b[8..]), b[10], b[11]);

    /// <summary>FNV-1 64 over the 12 bytes, as FIoStoreTocResource::HashChunkIdWithSeed.</summary>
    public ulong Hash(int seed)
    {
        ulong h = seed != 0 ? (ulong)seed : 0xcbf29ce484222325;
        foreach (var x in ToBytes()) h = (h * 0x00000100000001B3) ^ x;
        return h;
    }
}

/// <summary>The parts of a .utoc this app needs: chunk ids, where they live, their hashes, and the
/// file names in the directory index. Nothing here touches the .ucas.</summary>
public sealed class IoStoreToc
{
    public const string Magic = "-==--==--==--==-";
    public const int HeaderSize = 144;
    public const byte TocVersion = 5;

    public byte Version { get; private init; }
    /// <summary>Where the .utoc was read from; the .ucas sits beside it.</summary>
    public string? Path { get; private init; }
    public uint PartitionCount { get; private init; } = 1;
    public ulong PartitionSize { get; private init; } = ulong.MaxValue;
    public ulong ContainerId { get; private init; }
    public uint CompressionBlockSize { get; private init; }
    public IReadOnlyList<IoChunkId> ChunkIds { get; private init; } = Array.Empty<IoChunkId>();
    public IReadOnlyList<(long Offset, long Length)> OffsetLengths { get; private init; } = Array.Empty<(long, long)>();
    public IReadOnlyList<int> PerfectHashSeeds { get; private init; } = Array.Empty<int>();
    public IReadOnlyList<int> ChunksWithoutPerfectHash { get; private init; } = Array.Empty<int>();
    public IReadOnlyList<(long Offset, int CompressedSize, int UncompressedSize, byte Method)> Blocks { get; private init; } =
        Array.Empty<(long, int, int, byte)>();
    public IReadOnlyList<string> CompressionMethods { get; private init; } = Array.Empty<string>();
    /// <summary>First 20 bytes of BLAKE3 of each chunk's uncompressed data.</summary>
    public IReadOnlyList<byte[]> ChunkHashes { get; private init; } = Array.Empty<byte[]>();
    /// <summary>Full path ("../../../Polaris/Content/...") to chunk index, from the directory index.</summary>
    public IReadOnlyDictionary<string, int> Files { get; private init; } = new Dictionary<string, int>();

    public static IoStoreToc Read(string path) => Parse(File.ReadAllBytes(path), path, path);

    public static IoStoreToc Parse(byte[] d, string name = "utoc", string? path = null)
    {
        if (d.Length < HeaderSize || Encoding.ASCII.GetString(d, 0, 16) != Magic)
            throw new PackException($"{name} is not an IoStore table of contents");
        byte version = d[16];
        if (version is < 3 or > 5)
            throw new PackException($"{name}: unsupported IoStore TOC version {version}");
        uint U32(int o) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o));
        uint headerSize = U32(20), n = U32(24), nBlocks = U32(28), blockEntrySize = U32(32);
        uint nMethods = U32(36), methodLen = U32(40), blockSize = U32(44), dirSize = U32(48);
        ulong containerId = BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(56));
        uint partitionCount = U32(52);
        ulong partitionSize = version >= 3 ? BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(88)) : ulong.MaxValue;
        byte flags = d[80];
        uint nSeeds = version >= 4 ? U32(84) : 0, nNoHash = version >= 5 ? U32(96) : 0;
        if ((flags & 2) != 0) throw new PackException($"{name} is encrypted");
        if (blockEntrySize != 12) throw new PackException($"{name}: unexpected block entry size {blockEntrySize}");

        int o = (int)headerSize;
        var ids = new IoChunkId[n];
        for (int i = 0; i < n; i++, o += 12) ids[i] = IoChunkId.FromBytes(d.AsSpan(o, 12));
        var ols = new (long, long)[n];
        for (int i = 0; i < n; i++, o += 10) ols[i] = ((long)ReadUInt40BE(d.AsSpan(o)), (long)ReadUInt40BE(d.AsSpan(o + 5)));
        var seeds = new int[nSeeds];
        for (int i = 0; i < nSeeds; i++, o += 4) seeds[i] = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(o));
        var noHash = new int[nNoHash];
        for (int i = 0; i < nNoHash; i++, o += 4) noHash[i] = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(o));
        var blocks = new (long, int, int, byte)[nBlocks];
        for (int i = 0; i < nBlocks; i++, o += 12)
        {
            var b = d.AsSpan(o, 12);
            long off = (long)(b[0] | (ulong)b[1] << 8 | (ulong)b[2] << 16 | (ulong)b[3] << 24 | (ulong)b[4] << 32);
            int csz = b[5] | b[6] << 8 | b[7] << 16, usz = b[8] | b[9] << 8 | b[10] << 16;
            blocks[i] = (off, csz, usz, b[11]);
        }
        var methods = new List<string> { "None" };
        for (int i = 0; i < nMethods; i++, o += (int)methodLen)
            methods.Add(Encoding.ASCII.GetString(d, o, (int)methodLen).TrimEnd('\0'));
        if ((flags & 4) != 0)
            throw new PackException($"{name} is signed, which this reader does not handle");
        var files = dirSize > 0 ? ReadDirectoryIndex(d.AsSpan(o, (int)dirSize)) : new Dictionary<string, int>();
        o += (int)dirSize;
        var hashes = new byte[n][];
        for (int i = 0; i < n; i++, o += 33) hashes[i] = d.AsSpan(o, 20).ToArray();

        return new IoStoreToc
        {
            Version = version, Path = path, PartitionCount = Math.Max(1, partitionCount), PartitionSize = partitionSize,
            ContainerId = containerId, CompressionBlockSize = blockSize, ChunkIds = ids,
            OffsetLengths = ols, PerfectHashSeeds = seeds, ChunksWithoutPerfectHash = noHash, Blocks = blocks,
            CompressionMethods = methods, ChunkHashes = hashes, Files = files,
        };
    }

    /// <summary>
    /// Chunk id and stored hash of one file in a container, reading only the header, the directory
    /// index and that file's entries rather than the whole TOC (the game's run to several MB each).
    /// Null when the container does not list the file or cannot be read this way.
    /// </summary>
    public static (IoChunkId Id, byte[] Hash)? FindFile(string utocPath, string virtualPath)
    {
        using var f = File.OpenRead(utocPath);
        var h = new byte[HeaderSize];
        if (f.Read(h, 0, HeaderSize) != HeaderSize || Encoding.ASCII.GetString(h, 0, 16) != Magic) return null;
        byte version = h[16];
        if (version is < 3 or > 5) return null;
        uint U32(int o) => BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(o));
        uint headerSize = U32(20), n = U32(24), nBlocks = U32(28), blockEntrySize = U32(32);
        uint nMethods = U32(36), methodLen = U32(40), dirSize = U32(48);
        byte flags = h[80];
        uint nSeeds = version >= 4 ? U32(84) : 0, nNoHash = version >= 5 ? U32(96) : 0;
        if (dirSize == 0 || (flags & (2 | 4)) != 0) return null;   // no index, encrypted or signed

        long idsAt = headerSize;
        long dirAt = idsAt + 12L * n + 10L * n + 4L * nSeeds + 4L * nNoHash + (long)blockEntrySize * nBlocks + (long)nMethods * methodLen;
        long metaAt = dirAt + dirSize;
        var dir = new byte[dirSize];
        f.Seek(dirAt, SeekOrigin.Begin);
        f.ReadExactly(dir);
        var files = ReadDirectoryIndex(dir);
        if (!files.TryGetValue(MountedPath(virtualPath), out int index) || index < 0 || index >= n) return null;

        var id = new byte[12];
        f.Seek(idsAt + 12L * index, SeekOrigin.Begin);
        f.ReadExactly(id);
        var hash = new byte[20];
        f.Seek(metaAt + 33L * index, SeekOrigin.Begin);
        f.ReadExactly(hash);
        return (IoChunkId.FromBytes(id), hash);
    }

    /// <summary>"Polaris/Content/x.uasset" as the directory index spells it, under the "../../../" mount.</summary>
    public static string MountedPath(string virtualPath)
    {
        var p = virtualPath.Replace('\\', '/');
        return p.StartsWith("../", StringComparison.Ordinal) ? p : IoStoreWriter.MountPoint + p.TrimStart('/');
    }

    /// <summary>Chunk index for <paramref name="id"/> via the perfect hash, the way the engine looks it up.</summary>
    public int Resolve(IoChunkId id)
    {
        int n = ChunkIds.Count;
        if (n == 0) return -1;
        if (PerfectHashSeeds.Count == 0) return IndexOf(ChunkIds, id);
        int seed = PerfectHashSeeds[(int)(id.Hash(0) % (ulong)PerfectHashSeeds.Count)];
        if (seed == 0) return -1;
        int slot;
        if (seed < 0)
        {
            slot = -seed - 1;
            if (slot >= n) return ChunksWithoutPerfectHash.FirstOrDefault(i => ChunkIds[i] == id, -1);
        }
        else slot = (int)(id.Hash(seed) % (ulong)n);
        return ChunkIds[slot] == id ? slot : -1;
    }

    /// <summary>
    /// The uncompressed bytes of chunk <paramref name="index"/>, read from the .ucas beside the .utoc and
    /// decompressed (Oodle Kraken or stored). Checked against the hash the .utoc records, so a decoder
    /// fault surfaces here as an error instead of as wrong data further on.
    /// </summary>
    public byte[] ReadChunk(int index)
    {
        if (Path is null) throw new InvalidOperationException("this table of contents was not read from a file");
        var (offset, length) = OffsetLengths[index];
        var result = new byte[length];
        int first = (int)(offset / CompressionBlockSize);
        long skip = offset % CompressionBlockSize;
        long written = 0;
        var streams = new Dictionary<int, FileStream>();
        try
        {
            for (int k = first; written < length; k++)
            {
                var (blockOffset, compressedSize, uncompressedSize, method) = Blocks[k];
                int partition = PartitionCount > 1 && PartitionSize != ulong.MaxValue ? (int)((ulong)blockOffset / PartitionSize) : 0;
                long inPartition = partition == 0 ? blockOffset : (long)((ulong)blockOffset % PartitionSize);
                if (!streams.TryGetValue(partition, out var ucas))
                {
                    var name = System.IO.Path.ChangeExtension(Path, null) + (partition == 0 ? "" : $"_s{partition}") + ".ucas";
                    streams[partition] = ucas = File.OpenRead(name);
                }
                var raw = new byte[compressedSize];
                ucas.Seek(inPartition, SeekOrigin.Begin);
                ucas.ReadExactly(raw);
                byte[] block;
                var methodName = method < CompressionMethods.Count ? CompressionMethods[method] : "?";
                if (method == 0) block = raw;
                else if (methodName.Equals("Oodle", StringComparison.OrdinalIgnoreCase)) block = Kraken.Decompress(raw, uncompressedSize);
                else throw new PackException($"{System.IO.Path.GetFileName(Path)}: compression '{methodName}' is not supported");

                int take = (int)Math.Min(uncompressedSize - skip, length - written);
                Array.Copy(block, skip, result, written, take);
                written += take;
                skip = 0;
            }
        }
        finally { foreach (var s in streams.Values) s.Dispose(); }

        if (!Blake3.Hash(result, 20).AsSpan().SequenceEqual(ChunkHashes[index]))
            throw new PackException($"{System.IO.Path.GetFileName(Path)}: chunk {index} does not match its recorded hash after decompression");
        return result;
    }

    /// <summary>Chunk index of a file listed in the directory index, or -1.</summary>
    public int IndexOfFile(string virtualPath) => Files.TryGetValue(MountedPath(virtualPath), out var i) ? i : -1;

    private static int IndexOf(IReadOnlyList<IoChunkId> ids, IoChunkId id)
    {
        for (int i = 0; i < ids.Count; i++) if (ids[i] == id) return i;
        return -1;
    }

    /// <summary>
    /// FIoDirectoryIndexResource: mount point, directory entries (name, first child, next sibling,
    /// first file), file entries (name, next file, chunk index), then the string table they index.
    /// </summary>
    private static Dictionary<string, int> ReadDirectoryIndex(ReadOnlySpan<byte> b)
    {
        int p = 0;
        string FStr(ReadOnlySpan<byte> s)
        {
            int len = BinaryPrimitives.ReadInt32LittleEndian(s[p..]); p += 4;
            if (len == 0) return "";
            string v = len < 0
                ? Encoding.Unicode.GetString(s.Slice(p, -len * 2)).TrimEnd('\0')
                : Encoding.UTF8.GetString(s.Slice(p, len)).TrimEnd('\0');
            p += len < 0 ? -len * 2 : len;
            return v;
        }
        var mount = FStr(b);
        int nd = BinaryPrimitives.ReadInt32LittleEndian(b[p..]); p += 4;
        var dirs = new (uint Name, uint Child, uint Sibling, uint File)[nd];
        for (int i = 0; i < nd; i++, p += 16)
            dirs[i] = (U(b, p), U(b, p + 4), U(b, p + 8), U(b, p + 12));
        int nf = BinaryPrimitives.ReadInt32LittleEndian(b[p..]); p += 4;
        var files = new (uint Name, uint Next, uint Chunk)[nf];
        for (int i = 0; i < nf; i++, p += 12) files[i] = (U(b, p), U(b, p + 4), U(b, p + 8));
        int ns = BinaryPrimitives.ReadInt32LittleEndian(b[p..]); p += 4;
        var strings = new string[ns];
        for (int i = 0; i < ns; i++) strings[i] = FStr(b);

        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (nd == 0) return result;
        var stack = new Stack<(uint Dir, string Prefix)>();
        stack.Push((0, mount));
        while (stack.Count > 0)
        {
            var (di, prefix) = stack.Pop();
            var dir = dirs[di];
            var path = dir.Name == uint.MaxValue ? prefix : prefix + strings[dir.Name] + "/";
            for (uint f = dir.File; f != uint.MaxValue; f = files[f].Next)
                result[path + strings[files[f].Name]] = (int)files[f].Chunk;
            for (uint c = dir.Child; c != uint.MaxValue; c = dirs[c].Sibling)
                stack.Push((c, path));
        }
        return result;
    }

    private static uint U(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b[o..]);

    internal static ulong ReadUInt40BE(ReadOnlySpan<byte> b) =>
        (ulong)b[0] << 32 | (ulong)b[1] << 24 | (ulong)b[2] << 16 | (ulong)b[3] << 8 | b[4];

    internal static void WriteUInt40BE(Span<byte> b, ulong v)
    {
        b[0] = (byte)(v >> 32); b[1] = (byte)(v >> 24); b[2] = (byte)(v >> 16); b[3] = (byte)(v >> 8); b[4] = (byte)v;
    }
}

/// <summary>One chunk to store, with the virtual path it is listed under (null for chunks that have no
/// file, like the container header).</summary>
public sealed record IoStoreChunk(IoChunkId Id, byte[] Data, string? Path = null);

/// <summary>Writes an uncompressed IoStore container: &lt;name&gt;.utoc + &lt;name&gt;.ucas, plus the empty
/// &lt;name&gt;.pak the engine needs to find them.</summary>
public static class IoStoreWriter
{
    public const string MountPoint = "../../../";
    public const int BlockSize = 0x10000;
    private const byte FlagIndexed = 8;

    /// <summary>
    /// The .utoc and .ucas bytes for <paramref name="chunks"/>, in perfect-hash order. A container of
    /// packages also needs a <see cref="ContainerHeader"/> chunk registering them, and an
    /// <see cref="EmptyPak"/> beside it.
    /// </summary>
    public static (byte[] Utoc, byte[] Ucas) Build(ulong containerId, IReadOnlyList<IoStoreChunk> chunks)
    {
        var (seeds, overflow, order) = PerfectHash(chunks.Select(c => c.Id).ToList());
        var placed = order.Select(i => chunks[i]).ToList();
        int n = placed.Count;

        // .ucas: each chunk starts on a block boundary of the virtual address space and is cut into
        // 64 KiB blocks, stored raw. The engine always reads a block rounded up to 16 bytes, so pad.
        using var ucas = new MemoryStream();
        var blocks = new List<(long Offset, int Size)>();
        var offsetLengths = new (long Offset, long Length)[n];
        for (int i = 0; i < n; i++)
        {
            var data = placed[i].Data;
            offsetLengths[i] = ((long)blocks.Count * BlockSize, data.Length);
            for (int at = 0; at < data.Length || (at == 0 && data.Length == 0); at += BlockSize)
            {
                int size = Math.Min(BlockSize, data.Length - at);
                blocks.Add((ucas.Position, size));
                ucas.Write(data, at, size);
                ucas.Write(new byte[Align16(size) - size]);
                if (data.Length == 0) break;
            }
        }

        var dir = DirectoryIndex(placed);
        using var toc = new MemoryStream();
        var w = new BinaryWriter(toc);
        w.Write(Encoding.ASCII.GetBytes(IoStoreToc.Magic));
        w.Write(IoStoreToc.TocVersion); w.Write((byte)0); w.Write((ushort)0);
        w.Write((uint)IoStoreToc.HeaderSize);
        w.Write((uint)n);
        w.Write((uint)blocks.Count);
        w.Write(12u);                      // compression block entry size
        w.Write(0u);                       // compression method names: none, every block is raw
        w.Write(32u);                      // compression method name length
        w.Write((uint)BlockSize);
        w.Write((uint)dir.Length);
        w.Write(1u);                       // partition count
        w.Write(containerId);
        w.Write(new byte[16]);             // encryption key guid
        w.Write(FlagIndexed); w.Write((byte)0); w.Write((ushort)0);
        w.Write((uint)seeds.Length);
        w.Write(ulong.MaxValue);           // partition size: one partition, unbounded
        w.Write((uint)overflow.Length);    // chunks without perfect hash
        w.Write(0u);
        w.Write(new byte[40]);
        if (toc.Position != IoStoreToc.HeaderSize) throw new InvalidOperationException("TOC header size drifted");

        foreach (var c in placed) w.Write(c.Id.ToBytes());
        var ol = new byte[10];
        foreach (var (off, len) in offsetLengths)
        {
            IoStoreToc.WriteUInt40BE(ol, (ulong)off);
            IoStoreToc.WriteUInt40BE(ol.AsSpan(5), (ulong)len);
            w.Write(ol);
        }
        foreach (var s in seeds) w.Write(s);
        foreach (var slot in overflow) w.Write(slot);
        foreach (var (off, size) in blocks)
        {
            var e = new byte[12];
            for (int k = 0; k < 5; k++) e[k] = (byte)(off >> (8 * k));
            for (int k = 0; k < 3; k++) { e[5 + k] = (byte)(size >> (8 * k)); e[8 + k] = (byte)(size >> (8 * k)); }
            e[11] = 0;                     // method index 0 = none
            w.Write(e);
        }
        w.Write(dir);
        foreach (var c in placed)
        {
            w.Write(Blake3.Hash(c.Data, 20));
            w.Write(new byte[12]);
            w.Write((byte)0);              // meta flags: not compressed, not memory mapped
        }
        w.Flush();
        return (toc.ToArray(), ucas.ToArray());
    }

    private static int Align16(int v) => (v + 15) & ~15;

    /// <summary>Seeds tried per bucket before its chunks go to the overflow list instead.</summary>
    private const int MaxSeedAttempts = 100_000;

    /// <summary>
    /// Perfect hash over the chunk ids, laid out the way the game's own containers are: ceil(n / 2) seeds,
    /// indexed by hash(id, 0) % seedCount. A bucket of several ids gets a positive seed that sends each to
    /// a distinct free slot (hash(id, seed) % n); a bucket of one gets -(slot + 1). A bucket no seed can
    /// split goes to the overflow list with seed -(n + 1), which sends the engine to a plain lookup.
    /// That last case is not rare: with an even chunk count, two ids whose bytes have the same parity
    /// land in the same slot for every seed (FNV keeps the parity), which is exactly why the game's
    /// two-chunk pakchunk202optional lists both of its chunks as overflow.
    /// Returns the seeds, the overflow slots, and for each slot which input chunk sits there.
    /// </summary>
    public static (int[] Seeds, int[] Overflow, int[] Order) PerfectHash(IReadOnlyList<IoChunkId> ids)
    {
        int n = ids.Count;
        int seedCount = Math.Max(1, (n + 1) / 2);
        var seeds = new int[seedCount];
        var slotOwner = Enumerable.Repeat(-1, n).ToArray();
        var buckets = Enumerable.Range(0, n).GroupBy(i => (int)(ids[i].Hash(0) % (ulong)seedCount))
                                .OrderByDescending(g => g.Count()).ToList();
        var overflow = new List<int>();
        foreach (var bucket in buckets.Where(b => b.Count() > 1))
        {
            var members = bucket.ToList();
            bool placed = false;
            for (int seed = 1; seed <= MaxSeedAttempts && !placed; seed++)
            {
                var slots = members.Select(i => (int)(ids[i].Hash(seed) % (ulong)n)).ToList();
                if (slots.Distinct().Count() != slots.Count || slots.Any(s => slotOwner[s] >= 0)) continue;
                for (int k = 0; k < members.Count; k++) slotOwner[slots[k]] = members[k];
                seeds[bucket.Key] = seed;
                placed = true;
            }
            if (!placed)
            {
                seeds[bucket.Key] = -(n + 1);
                overflow.AddRange(members);
            }
        }
        int free = 0;
        int NextFree() { while (slotOwner[free] >= 0) free++; return free; }
        foreach (var bucket in buckets.Where(b => b.Count() == 1))
        {
            int slot = NextFree();
            slotOwner[slot] = bucket.First();
            seeds[bucket.Key] = -(slot + 1);
        }
        var overflowSlots = new List<int>();
        foreach (var chunk in overflow)
        {
            int slot = NextFree();
            slotOwner[slot] = chunk;
            overflowSlots.Add(slot);
        }
        return (seeds, overflowSlots.ToArray(), slotOwner);
    }

    /// <summary>Directory index listing every chunk that has a path, in the engine's linked-list form.</summary>
    private static byte[] DirectoryIndex(IReadOnlyList<IoStoreChunk> placed)
    {
        var strings = new List<string>();
        int Str(string s) { int i = strings.IndexOf(s); if (i < 0) { strings.Add(s); i = strings.Count - 1; } return i; }

        var dirs = new List<uint[]> { new[] { uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue } };
        var files = new List<uint[]>();
        for (int chunk = 0; chunk < placed.Count; chunk++)
        {
            var path = placed[chunk].Path;
            if (path is null) continue;
            var parts = path.Replace('\\', '/').TrimStart('/').Split('/');
            int dir = 0;
            foreach (var part in parts[..^1])
            {
                int name = Str(part);
                int child = -1;
                for (uint c = dirs[dir][1]; c != uint.MaxValue; c = dirs[(int)c][2])
                    if (dirs[(int)c][0] == name) { child = (int)c; break; }
                if (child < 0)
                {
                    dirs.Add(new[] { (uint)name, uint.MaxValue, dirs[dir][1], uint.MaxValue });
                    child = dirs.Count - 1;
                    dirs[dir][1] = (uint)child;
                }
                dir = child;
            }
            files.Add(new[] { (uint)Str(parts[^1]), dirs[dir][3], (uint)chunk });
            dirs[dir][3] = (uint)(files.Count - 1);
        }

        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        WriteFString(w, MountPoint);
        w.Write(dirs.Count);
        foreach (var d in dirs) foreach (var v in d) w.Write(v);
        w.Write(files.Count);
        foreach (var f in files) foreach (var v in f) w.Write(v);
        w.Write(strings.Count);
        foreach (var s in strings) WriteFString(w, s);
        w.Flush();
        return ms.ToArray();
    }

    private static void WriteFString(BinaryWriter w, string s)
    {
        var bytes = Encoding.ASCII.GetBytes(s);
        w.Write(bytes.Length + 1);
        w.Write(bytes);
        w.Write((byte)0);
    }

    /// <summary>
    /// FIoContainerHeader, version 2: the package store entries that make the engine load these packages
    /// from this container. Imports and shader maps are not supported; every package here has neither.
    /// </summary>
    public static byte[] ContainerHeader(ulong containerId, IReadOnlyList<IoStorePackage> packages)
    {
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(0x496f436eu);              // "nCoI"
        w.Write(2u);                       // EIoContainerHeaderVersion::OptionalSegmentPackages
        w.Write(containerId);
        w.Write(packages.Count);
        foreach (var p in packages) w.Write(p.PackageId);
        w.Write(packages.Count * 24);      // store entries: fixed part only, no imports or shader maps
        foreach (var p in packages)
        {
            w.Write(p.ExportCount); w.Write(p.ExportBundleCount);
            w.Write(0); w.Write(0);        // imported packages: num, offset
            w.Write(0); w.Write(0);        // shader map hashes: num, offset
        }
        w.Write(0);                        // optional segment package ids
        w.Write(0);                        // optional segment store entries size
        w.Write(0);                        // redirects name batch: no names
        w.Write(0);                        // localized packages
        w.Write(0);                        // package redirects
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>
    /// A version 11 pak with no files, identical in layout to the stubs the game ships beside its own
    /// containers. The engine mounts the .utoc/.ucas only when a .pak of the same name sits next to them.
    /// </summary>
    public static byte[] EmptyPak()
    {
        var pathHashIndex = new byte[8];
        var fullDirectoryIndex = new byte[4];
        const int primarySize = 106;

        using var index = new MemoryStream();
        var w = new BinaryWriter(index);
        w.Write(2); w.Write((byte)'/'); w.Write((byte)0);     // mount point "/"
        w.Write(0);                                           // entries
        w.Write(0UL);                                         // path hash seed (no paths to hash)
        w.Write(1); w.Write((long)primarySize); w.Write((long)pathHashIndex.Length); w.Write(SHA1.HashData(pathHashIndex));
        w.Write(1); w.Write((long)primarySize + pathHashIndex.Length); w.Write((long)fullDirectoryIndex.Length);
        w.Write(SHA1.HashData(fullDirectoryIndex));
        w.Write(0);                                           // encoded entries size
        w.Write(0);                                           // unencoded entries
        w.Flush();
        var primary = index.ToArray();
        if (primary.Length != primarySize) throw new InvalidOperationException("pak index size drifted");

        using var pak = new MemoryStream();
        var p = new BinaryWriter(pak);
        p.Write(primary); p.Write(pathHashIndex); p.Write(fullDirectoryIndex);
        p.Write(new byte[16]);                                // encryption key guid
        p.Write((byte)0);                                     // index not encrypted
        p.Write(0x5A6F12E1u);
        p.Write(11);
        p.Write(0L); p.Write((long)primary.Length); p.Write(SHA1.HashData(primary));
        p.Write(new byte[5 * 32]);                            // compression method names
        p.Flush();
        return pak.ToArray();
    }
}

/// <summary>A cooked package to place in a container, with the store-entry facts the engine checks.</summary>
public sealed record IoStorePackage(ulong PackageId, string Path, byte[] Data, int ExportCount = 1, int ExportBundleCount = 1);

/// <summary>What a container header says about one package: export counts, and how many imports and
/// shader maps it declares.</summary>
public sealed record IoStoreEntry(int ExportCount, int ExportBundleCount, int ImportCount, int ShaderMapCount);

/// <summary>Reads FIoContainerHeader (UE 5.0-5.2, versions 0-2), enough to find a package's store entry.</summary>
public static class IoContainerHeader
{
    public static IoStoreEntry? FindStoreEntry(byte[] header, ulong packageId)
    {
        var b = header.AsSpan();
        if (b.Length < 16 || BinaryPrimitives.ReadUInt32LittleEndian(b) != 0x496f436eu)
            throw new PackException("not an IoStore container header");
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(b[4..]);
        if (version > 2) throw new PackException($"container header version {version} is not supported");
        int p = 16;
        if (version < 2) p += 4;                       // package count, dropped in version 2
        int count = BinaryPrimitives.ReadInt32LittleEndian(b[p..]); p += 4;
        int found = -1;
        for (int i = 0; i < count; i++)
            if (BinaryPrimitives.ReadUInt64LittleEndian(b[(p + 8 * i)..]) == packageId) { found = i; break; }
        p += 8 * count;
        p += 4;                                        // store entries byte size
        if (found < 0) return null;
        var e = b[(p + 24 * found)..];
        return new IoStoreEntry(
            BinaryPrimitives.ReadInt32LittleEndian(e),
            BinaryPrimitives.ReadInt32LittleEndian(e[4..]),
            BinaryPrimitives.ReadInt32LittleEndian(e[8..]),
            BinaryPrimitives.ReadInt32LittleEndian(e[16..]));
    }
}
