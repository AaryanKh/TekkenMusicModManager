using System.Buffers.Binary;

namespace Tmm.Core.Pak;

/// <summary>
/// BLAKE3, hash mode only. IoStore stores BLAKE3 of every uncompressed chunk in the .utoc (first 20
/// bytes, zero-padded to 32), which is what lets the app check a chunk on disk without decompressing
/// it. Oodle-compressed chunks cannot be read here, but their hash can be compared.
/// Straight port of the reference implementation in the BLAKE3 specification; speed is irrelevant
/// at these sizes, and the core has no package dependencies.
/// </summary>
public static class Blake3
{
    private static readonly uint[] Iv =
        { 0x6A09E667, 0xBB67AE85, 0x3C6EF372, 0xA54FF53A, 0x510E527F, 0x9B05688C, 0x1F83D9AB, 0x5BE0CD19 };
    private static readonly int[] Perm = { 2, 6, 3, 10, 7, 0, 4, 13, 1, 11, 12, 5, 9, 14, 15, 8 };

    private const uint ChunkStart = 1, ChunkEnd = 2, Parent = 4, Root = 8;
    private const int BlockLen = 64, ChunkLen = 1024;

    /// <summary>First <paramref name="length"/> bytes of BLAKE3(<paramref name="data"/>).</summary>
    public static byte[] Hash(ReadOnlySpan<byte> data, int length = 32)
    {
        var stack = new Stack<uint[]>();
        int chunks = Math.Max(1, (data.Length + ChunkLen - 1) / ChunkLen);
        Output output = default;
        for (int c = 0; c < chunks; c++)
        {
            var chunk = data.Slice(c * ChunkLen, Math.Min(ChunkLen, data.Length - c * ChunkLen));
            var o = ChunkOutput(chunk, (ulong)c);
            if (c == chunks - 1) { output = o; break; }
            var cv = o.ChainingValue();
            // Merge completed subtrees: one merge per trailing zero bit of the new chunk count.
            for (ulong total = (ulong)c + 1; (total & 1) == 0; total >>= 1)
                cv = ParentOutput(stack.Pop(), cv).ChainingValue();
            stack.Push(cv);
        }
        while (stack.Count > 0)
            output = ParentOutput(stack.Pop(), output.ChainingValue());

        var words = Compress(output.Cv, output.Block, 0, output.BlockLen, output.Flags | Root);
        var bytes = new byte[64];
        for (int i = 0; i < 16; i++) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), words[i]);
        return bytes[..length];
    }

    private readonly record struct Output(uint[] Cv, uint[] Block, ulong Counter, uint BlockLen, uint Flags)
    {
        public uint[] ChainingValue() => Compress(Cv, Block, Counter, BlockLen, Flags)[..8];
    }

    private static Output ChunkOutput(ReadOnlySpan<byte> chunk, ulong counter)
    {
        var cv = (uint[])Iv.Clone();
        int blocks = Math.Max(1, (chunk.Length + BlockLen - 1) / BlockLen);
        for (int b = 0; b < blocks - 1; b++)
            cv = Compress(cv, Words(chunk.Slice(b * BlockLen, BlockLen)), counter, BlockLen, b == 0 ? ChunkStart : 0)[..8];
        var last = chunk[((blocks - 1) * BlockLen)..];
        uint flags = (blocks == 1 ? ChunkStart : 0) | ChunkEnd;
        return new Output(cv, Words(last), counter, (uint)last.Length, flags);
    }

    private static Output ParentOutput(uint[] left, uint[] right) =>
        new((uint[])Iv.Clone(), left.Concat(right).ToArray(), 0, BlockLen, Parent);

    private static uint[] Words(ReadOnlySpan<byte> block)
    {
        Span<byte> padded = stackalloc byte[BlockLen];
        block.CopyTo(padded);
        var w = new uint[16];
        for (int i = 0; i < 16; i++) w[i] = BinaryPrimitives.ReadUInt32LittleEndian(padded[(i * 4)..]);
        return w;
    }

    private static uint[] Compress(uint[] cv, uint[] block, ulong counter, uint blockLen, uint flags)
    {
        var s = new uint[16];
        Array.Copy(cv, s, 8);
        Array.Copy(Iv, 0, s, 8, 4);
        s[12] = (uint)counter; s[13] = (uint)(counter >> 32); s[14] = blockLen; s[15] = flags;
        var m = (uint[])block.Clone();
        for (int r = 0; r < 7; r++)
        {
            G(s, 0, 4, 8, 12, m[0], m[1]); G(s, 1, 5, 9, 13, m[2], m[3]);
            G(s, 2, 6, 10, 14, m[4], m[5]); G(s, 3, 7, 11, 15, m[6], m[7]);
            G(s, 0, 5, 10, 15, m[8], m[9]); G(s, 1, 6, 11, 12, m[10], m[11]);
            G(s, 2, 7, 8, 13, m[12], m[13]); G(s, 3, 4, 9, 14, m[14], m[15]);
            if (r < 6)
            {
                var p = new uint[16];
                for (int i = 0; i < 16; i++) p[i] = m[Perm[i]];
                m = p;
            }
        }
        for (int i = 0; i < 8; i++) { s[i] ^= s[i + 8]; s[i + 8] ^= cv[i]; }
        return s;
    }

    private static void G(uint[] s, int a, int b, int c, int d, uint x, uint y)
    {
        s[a] = s[a] + s[b] + x; s[d] = uint.RotateRight(s[d] ^ s[a], 16);
        s[c] = s[c] + s[d]; s[b] = uint.RotateRight(s[b] ^ s[c], 12);
        s[a] = s[a] + s[b] + y; s[d] = uint.RotateRight(s[d] ^ s[a], 8);
        s[c] = s[c] + s[d]; s[b] = uint.RotateRight(s[b] ^ s[c], 7);
    }
}
