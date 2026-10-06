// Kraken decompressor: a C# port of the Kraken path of ooz (https://github.com/powzix/ooz).
//
// Original work: "Kraken Decompressor for Windows", Copyright (C) 2016, Powzix.
// This port: Copyright (C) 2026, the TekkenMusicModManager authors.
//
// This program is free software: you can redistribute it and/or modify it under the terms of the GNU
// General Public License as published by the Free Software Foundation, either version 3 of the License,
// or (at your option) any later version.
//
// This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even
// the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public
// License for more details. You should have received a copy of the GNU General Public License along with
// this program. If not, see <http://www.gnu.org/licenses/>.
//
// Only the Kraken codec (decoder type 6) is ported, because that is what Tekken 8's containers use.
// The structure, names and arithmetic follow kraken.cpp closely so the two can be compared side by side;
// the SSE helpers are replaced by scalar equivalents with the same read-then-write behaviour. Like the
// original, the decoder may read and write a few bytes past the ends of its buffers, so Decompress pads
// every buffer it owns. Output is verified by the callers against the BLAKE3 hashes in the .utoc.

using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Tmm.Core.Pak;

public static unsafe class Kraken
{
    private const int SafeSpace = 64;
    private const int ScratchSize = 0x6C000;

    /// <summary>Decompress one Oodle block of <paramref name="rawSize"/> bytes. Throws on bad or unsupported data.</summary>
    public static byte[] Decompress(ReadOnlySpan<byte> compressed, int rawSize)
    {
        var output = new byte[rawSize];
        Decompress(compressed, output);
        return output;
    }

    /// <summary>Decompress one Oodle block into <paramref name="output"/>, which must be exactly the raw size.</summary>
    public static void Decompress(ReadOnlySpan<byte> compressed, Span<byte> output)
    {
        // Pad both sides: the bit readers peek a few bytes beyond either end of their stream.
        var src = new byte[compressed.Length + 2 * SafeSpace];
        compressed.CopyTo(src.AsSpan(SafeSpace));
        var dst = new byte[output.Length + SafeSpace];
        void* scratch = NativeMemory.AlignedAlloc(ScratchSize + SafeSpace, 16);
        try
        {
            int n;
            fixed (byte* s = src, d = dst)
                n = DecompressCore(s + SafeSpace, compressed.Length, d, output.Length, (byte*)scratch);
            if (n != output.Length)
                throw new PackException("Oodle Kraken data could not be decompressed (corrupt, or a codec this app does not read)");
            dst.AsSpan(0, output.Length).CopyTo(output);
        }
        finally { NativeMemory.AlignedFree(scratch); }
    }

    // ------------------------------------------------------------------ structures

    private struct KrakenHeader
    {
        public int decoder_type;
        public bool restart_decoder;
        public bool uncompressed;
        public bool use_checksums;
    }

    private struct KrakenQuantumHeader
    {
        public uint compressed_size;
        public uint checksum;
        public byte flag1;
        public byte flag2;
        public uint whole_match_distance;
    }

    private struct KrakenLzTable
    {
        public byte* cmd_stream;
        public int cmd_stream_size;
        public int* offs_stream;
        public int offs_stream_size;
        public byte* lit_stream;
        public int lit_stream_size;
        public int* len_stream;
        public int len_stream_size;
    }

    private struct BitReader
    {
        public byte* p, p_end;
        public uint bits;
        public int bitpos;
    }

    private struct BitReader2
    {
        public byte* p, p_end;
        public uint bitpos;
    }

    private struct HuffReader
    {
        public byte* output, output_end;
        public byte* src, src_mid, src_end, src_mid_org;
        public int src_bitpos, src_mid_bitpos, src_end_bitpos;
        public uint src_bits, src_mid_bits, src_end_bits;
    }

    private struct HuffRange
    {
        public ushort symbol;
        public ushort num;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TansLutEnt
    {
        public uint x;
        public byte bits_x;
        public byte symbol;
        public ushort w;
    }

    private struct TansDecoderParams
    {
        public TansLutEnt* lut;
        public byte* dst, dst_end;
        public byte* ptr_f, ptr_b;
        public uint bits_f, bits_b;
        public int bitpos_f, bitpos_b;
        public uint state_0, state_1, state_2, state_3, state_4;
    }

    // ------------------------------------------------------------------ small helpers

    private static uint BSR(uint x) => (uint)BitOperations.Log2(x);
    private static uint BSF(uint x) => (uint)BitOperations.TrailingZeroCount(x);
    private static int CountLeadingZeros(uint bits) => BitOperations.LeadingZeroCount(bits);
    private static uint Rotl(uint x, int n) => BitOperations.RotateLeft(x, n);
    private static uint ByteSwap(uint x) => BinaryPrimitives.ReverseEndianness(x);
    private static ulong ByteSwap(ulong x) => BinaryPrimitives.ReverseEndianness(x);
    private static uint ReadU32(byte* p) => *(uint*)p;
    private static ushort ReadU16(byte* p) => *(ushort*)p;
    private static void Copy64(byte* d, byte* s) => *(ulong*)d = *(ulong*)s;
    private static long Min(long a, long b) => a < b ? a : b;
    private static void MemSet(byte* d, byte v, long n) { if (n > 0) new Span<byte>(d, (int)n).Fill(v); }
    private static void MemMove(byte* d, byte* s, long n) { if (n > 0) Buffer.MemoryCopy(s, d, n, n); }
    private static byte* AlignPointer(byte* p, int align) => (byte*)(((nuint)p + (nuint)(align - 1)) & ~(nuint)(align - 1));

    /// <summary>_mm_add_epi8 over 8 bytes: d[i] = s[i] + t[i] mod 256, with both read before d is written.</summary>
    private static void Copy64Add(byte* d, byte* s, byte* t)
    {
        ulong a = *(ulong*)s, b = *(ulong*)t;
        const ulong H = 0x8080808080808080UL;
        *(ulong*)d = ((a & ~H) + (b & ~H)) ^ ((a ^ b) & H);
    }

    private static void Copy64Bytes(byte* d, byte* s)
    {
        for (int i = 0; i < 64; i += 16)
        {
            ulong lo = *(ulong*)(s + i), hi = *(ulong*)(s + i + 8);
            *(ulong*)(d + i) = lo;
            *(ulong*)(d + i + 8) = hi;
        }
    }

    private static readonly uint[] Bitmasks =
    {
        0x1, 0x3, 0x7, 0xf, 0x1f, 0x3f, 0x7f, 0xff,
        0x1ff, 0x3ff, 0x7ff, 0xfff, 0x1fff, 0x3fff, 0x7fff, 0xffff,
        0x1ffff, 0x3ffff, 0x7ffff, 0xfffff, 0x1fffff, 0x3fffff, 0x7fffff,
        0xffffff, 0x1ffffff, 0x3ffffff, 0x7ffffff, 0xfffffff, 0x1fffffff, 0x3fffffff, 0x7fffffff, 0xffffffff,
    };

    // ------------------------------------------------------------------ bit reader

    private static void BitReader_Refill(BitReader* bits)
    {
        while (bits->bitpos > 0)
        {
            bits->bits |= (uint)(bits->p < bits->p_end ? *bits->p : 0) << bits->bitpos;
            bits->bitpos -= 8;
            bits->p++;
        }
    }

    private static void BitReader_RefillBackwards(BitReader* bits)
    {
        while (bits->bitpos > 0)
        {
            bits->p--;
            bits->bits |= (uint)(bits->p >= bits->p_end ? *bits->p : 0) << bits->bitpos;
            bits->bitpos -= 8;
        }
    }

    private static int BitReader_ReadBit(BitReader* bits)
    {
        BitReader_Refill(bits);
        int r = (int)(bits->bits >> 31);
        bits->bits <<= 1;
        bits->bitpos += 1;
        return r;
    }

    private static int BitReader_ReadBitNoRefill(BitReader* bits)
    {
        int r = (int)(bits->bits >> 31);
        bits->bits <<= 1;
        bits->bitpos += 1;
        return r;
    }

    private static int BitReader_ReadBitsNoRefill(BitReader* bits, int n)
    {
        int r = (int)(bits->bits >> (32 - n));
        bits->bits <<= n;
        bits->bitpos += n;
        return r;
    }

    private static int BitReader_ReadBitsNoRefillZero(BitReader* bits, int n)
    {
        int r = (int)(bits->bits >> 1 >> (31 - n));
        bits->bits <<= n;
        bits->bitpos += n;
        return r;
    }

    private static uint BitReader_ReadMoreThan24Bits(BitReader* bits, int n)
    {
        uint rv;
        if (n <= 24)
            rv = (uint)BitReader_ReadBitsNoRefillZero(bits, n);
        else
        {
            rv = (uint)BitReader_ReadBitsNoRefill(bits, 24) << (n - 24);
            BitReader_Refill(bits);
            rv += (uint)BitReader_ReadBitsNoRefill(bits, n - 24);
        }
        BitReader_Refill(bits);
        return rv;
    }

    private static uint BitReader_ReadMoreThan24BitsB(BitReader* bits, int n)
    {
        uint rv;
        if (n <= 24)
            rv = (uint)BitReader_ReadBitsNoRefillZero(bits, n);
        else
        {
            rv = (uint)BitReader_ReadBitsNoRefill(bits, 24) << (n - 24);
            BitReader_RefillBackwards(bits);
            rv += (uint)BitReader_ReadBitsNoRefill(bits, n - 24);
        }
        BitReader_RefillBackwards(bits);
        return rv;
    }

    private static uint BitReader_ReadDistance(BitReader* bits, uint v)
    {
        uint w, m, n, rv;
        if (v < 0xF0)
        {
            n = (v >> 4) + 4;
            w = Rotl(bits->bits | 1, (int)n);
            bits->bitpos += (int)n;
            m = (2u << (int)n) - 1;
            bits->bits = w & ~m;
            rv = ((w & m) << 4) + (v & 0xF) - 248;
        }
        else
        {
            n = v - 0xF0 + 4;
            w = Rotl(bits->bits | 1, (int)n);
            bits->bitpos += (int)n;
            m = (2u << (int)n) - 1;
            bits->bits = w & ~m;
            rv = 8322816 + ((w & m) << 12);
            BitReader_Refill(bits);
            rv += bits->bits >> 20;
            bits->bitpos += 12;
            bits->bits <<= 12;
        }
        BitReader_Refill(bits);
        return rv;
    }

    private static uint BitReader_ReadDistanceB(BitReader* bits, uint v)
    {
        uint w, m, n, rv;
        if (v < 0xF0)
        {
            n = (v >> 4) + 4;
            w = Rotl(bits->bits | 1, (int)n);
            bits->bitpos += (int)n;
            m = (2u << (int)n) - 1;
            bits->bits = w & ~m;
            rv = ((w & m) << 4) + (v & 0xF) - 248;
        }
        else
        {
            n = v - 0xF0 + 4;
            w = Rotl(bits->bits | 1, (int)n);
            bits->bitpos += (int)n;
            m = (2u << (int)n) - 1;
            bits->bits = w & ~m;
            rv = 8322816 + ((w & m) << 12);
            BitReader_RefillBackwards(bits);
            rv += bits->bits >> (32 - 12);
            bits->bitpos += 12;
            bits->bits <<= 12;
        }
        BitReader_RefillBackwards(bits);
        return rv;
    }

    private static bool BitReader_ReadLength(BitReader* bits, uint* v)
    {
        int n = CountLeadingZeros(bits->bits);
        if (n > 12) return false;
        bits->bitpos += n;
        bits->bits <<= n;
        BitReader_Refill(bits);
        n += 7;
        bits->bitpos += n;
        uint rv = (bits->bits >> (32 - n)) - 64;
        bits->bits <<= n;
        *v = rv;
        BitReader_Refill(bits);
        return true;
    }

    private static bool BitReader_ReadLengthB(BitReader* bits, uint* v)
    {
        int n = CountLeadingZeros(bits->bits);
        if (n > 12) return false;
        bits->bitpos += n;
        bits->bits <<= n;
        BitReader_RefillBackwards(bits);
        n += 7;
        bits->bitpos += n;
        uint rv = (bits->bits >> (32 - n)) - 64;
        bits->bits <<= n;
        *v = rv;
        BitReader_RefillBackwards(bits);
        return true;
    }

    // ------------------------------------------------------------------ headers

    private static byte* Kraken_ParseHeader(KrakenHeader* hdr, byte* p)
    {
        int b = p[0];
        if ((b & 0xF) == 0xC)
        {
            if (((b >> 4) & 3) != 0) return null;
            hdr->restart_decoder = ((b >> 7) & 1) != 0;
            hdr->uncompressed = ((b >> 6) & 1) != 0;
            b = p[1];
            hdr->decoder_type = b & 0x7F;
            hdr->use_checksums = (b >> 7) != 0;
            if (hdr->decoder_type != 6 && hdr->decoder_type != 10 && hdr->decoder_type != 5 && hdr->decoder_type != 11 && hdr->decoder_type != 12)
                return null;
            return p + 2;
        }
        return null;
    }

    private static byte* Kraken_ParseQuantumHeader(KrakenQuantumHeader* hdr, byte* p, bool use_checksum)
    {
        uint v = (uint)((p[0] << 16) | (p[1] << 8) | p[2]);
        uint size = v & 0x3FFFF;
        if (size != 0x3ffff)
        {
            hdr->compressed_size = size + 1;
            hdr->flag1 = (byte)((v >> 18) & 1);
            hdr->flag2 = (byte)((v >> 19) & 1);
            if (use_checksum)
            {
                hdr->checksum = (uint)((p[3] << 16) | (p[4] << 8) | p[5]);
                return p + 6;
            }
            return p + 3;
        }
        v >>= 18;
        if (v == 1)
        {
            // memset
            hdr->checksum = p[3];
            hdr->compressed_size = 0;
            hdr->whole_match_distance = 0;
            return p + 4;
        }
        return null;
    }

    // ------------------------------------------------------------------ huffman

    /// <summary>output[i] = input[reverse of the 11 bits of i]: the scalar form of ReverseBitsArray2048.</summary>
    private static void ReverseBitsArray2048(byte* input, byte* output)
    {
        for (int i = 0; i < 2048; i++)
        {
            int r = 0;
            for (int b = 0; b < 11; b++) r |= ((i >> b) & 1) << (10 - b);
            output[i] = input[r];
        }
    }

    private static bool Kraken_DecodeBytesCore(HuffReader* hr, byte* bits2len, byte* bits2sym)
    {
        byte* src = hr->src;
        uint src_bits = hr->src_bits;
        int src_bitpos = hr->src_bitpos;

        byte* src_mid = hr->src_mid;
        uint src_mid_bits = hr->src_mid_bits;
        int src_mid_bitpos = hr->src_mid_bitpos;

        byte* src_end = hr->src_end;
        uint src_end_bits = hr->src_end_bits;
        int src_end_bitpos = hr->src_end_bitpos;

        int k, n;
        byte* dst = hr->output;
        byte* dst_end = hr->output_end;

        if (src > src_mid)
            return false;

        if (hr->src_end - src_mid >= 4 && dst_end - dst >= 6)
        {
            dst_end -= 5;
            src_end -= 4;

            while (dst < dst_end && src <= src_mid && src_mid <= src_end)
            {
                src_bits |= ReadU32(src) << src_bitpos;
                src += (31 - src_bitpos) >> 3;

                src_end_bits |= ByteSwap(ReadU32(src_end)) << src_end_bitpos;
                src_end -= (31 - src_end_bitpos) >> 3;

                src_mid_bits |= ReadU32(src_mid) << src_mid_bitpos;
                src_mid += (31 - src_mid_bitpos) >> 3;

                src_bitpos |= 0x18;
                src_end_bitpos |= 0x18;
                src_mid_bitpos |= 0x18;

                k = (int)(src_bits & 0x7FF); n = bits2len[k]; src_bits >>= n; src_bitpos -= n; dst[0] = bits2sym[k];
                k = (int)(src_end_bits & 0x7FF); n = bits2len[k]; src_end_bits >>= n; src_end_bitpos -= n; dst[1] = bits2sym[k];
                k = (int)(src_mid_bits & 0x7FF); n = bits2len[k]; src_mid_bits >>= n; src_mid_bitpos -= n; dst[2] = bits2sym[k];
                k = (int)(src_bits & 0x7FF); n = bits2len[k]; src_bits >>= n; src_bitpos -= n; dst[3] = bits2sym[k];
                k = (int)(src_end_bits & 0x7FF); n = bits2len[k]; src_end_bits >>= n; src_end_bitpos -= n; dst[4] = bits2sym[k];
                k = (int)(src_mid_bits & 0x7FF); n = bits2len[k]; src_mid_bits >>= n; src_mid_bitpos -= n; dst[5] = bits2sym[k];
                dst += 6;
            }
            dst_end += 5;

            src -= src_bitpos >> 3;
            src_bitpos &= 7;

            src_end += 4 + (src_end_bitpos >> 3);
            src_end_bitpos &= 7;

            src_mid -= src_mid_bitpos >> 3;
            src_mid_bitpos &= 7;
        }
        for (;;)
        {
            if (dst >= dst_end)
                break;

            if (src_mid - src <= 1)
            {
                if (src_mid - src == 1)
                    src_bits |= (uint)*src << src_bitpos;
            }
            else
                src_bits |= (uint)ReadU16(src) << src_bitpos;
            k = (int)(src_bits & 0x7FF);
            n = bits2len[k];
            src_bitpos -= n;
            src_bits >>= n;
            *dst++ = bits2sym[k];
            src += (7 - src_bitpos) >> 3;
            src_bitpos &= 7;

            if (dst < dst_end)
            {
                if (src_end - src_mid <= 1)
                {
                    if (src_end - src_mid == 1)
                    {
                        src_end_bits |= (uint)*src_mid << src_end_bitpos;
                        src_mid_bits |= (uint)*src_mid << src_mid_bitpos;
                    }
                }
                else
                {
                    uint v = ReadU16(src_end - 2);
                    src_end_bits |= (((v >> 8) | (v << 8)) & 0xffff) << src_end_bitpos;
                    src_mid_bits |= (uint)ReadU16(src_mid) << src_mid_bitpos;
                }
                n = bits2len[src_end_bits & 0x7FF];
                *dst++ = bits2sym[src_end_bits & 0x7FF];
                src_end_bitpos -= n;
                src_end_bits >>= n;
                src_end -= (7 - src_end_bitpos) >> 3;
                src_end_bitpos &= 7;
                if (dst < dst_end)
                {
                    n = bits2len[src_mid_bits & 0x7FF];
                    *dst++ = bits2sym[src_mid_bits & 0x7FF];
                    src_mid_bitpos -= n;
                    src_mid_bits >>= n;
                    src_mid += (7 - src_mid_bitpos) >> 3;
                    src_mid_bitpos &= 7;
                }
            }
            if (src > src_mid || src_mid > src_end)
                return false;
        }
        if (src != hr->src_mid_org || src_end != src_mid)
            return false;
        return true;
    }

    private static int Huff_ReadCodeLengthsOld(BitReader* bits, byte* syms, uint* code_prefix)
    {
        if (BitReader_ReadBitNoRefill(bits) != 0)
        {
            int n, sym = 0, codelen, num_symbols = 0;
            int avg_bits_x4 = 32;
            int forced_bits = BitReader_ReadBitsNoRefill(bits, 2);

            uint thres_for_valid_gamma_bits = 1u << (31 - (int)(20u >> forced_bits));
            bool skip = BitReader_ReadBit(bits) != 0;
            do
            {
                if (!skip)
                {
                    // Run of zeros
                    if ((bits->bits & 0xff000000) == 0)
                        return -1;
                    sym += BitReader_ReadBitsNoRefill(bits, 2 * (CountLeadingZeros(bits->bits) + 1)) - 2 + 1;
                    if (sym >= 256)
                        break;
                }
                skip = false;
                BitReader_Refill(bits);
                // Read out the gamma value for the # of symbols
                if ((bits->bits & 0xff000000) == 0)
                    return -1;
                n = BitReader_ReadBitsNoRefill(bits, 2 * (CountLeadingZeros(bits->bits) + 1)) - 2 + 1;
                // Overflow?
                if (sym + n > 256)
                    return -1;
                BitReader_Refill(bits);
                num_symbols += n;
                do
                {
                    if (bits->bits < thres_for_valid_gamma_bits)
                        return -1; // too big gamma value?

                    int lz = CountLeadingZeros(bits->bits);
                    int v = BitReader_ReadBitsNoRefill(bits, lz + forced_bits + 1) + ((lz - 1) << forced_bits);
                    codelen = (-(v & 1) ^ (v >> 1)) + ((avg_bits_x4 + 2) >> 2);
                    if (codelen < 1 || codelen > 11)
                        return -1;
                    avg_bits_x4 = codelen + ((3 * avg_bits_x4 + 2) >> 2);
                    BitReader_Refill(bits);
                    syms[code_prefix[codelen]++] = (byte)sym++;
                } while (--n != 0);
            } while (sym != 256);
            return (sym == 256) && (num_symbols >= 2) ? num_symbols : -1;
        }
        else
        {
            // Sparse symbol encoding
            int num_symbols = BitReader_ReadBitsNoRefill(bits, 8);
            if (num_symbols == 0)
                return -1;
            if (num_symbols == 1)
                syms[0] = (byte)BitReader_ReadBitsNoRefill(bits, 8);
            else
            {
                int codelen_bits = BitReader_ReadBitsNoRefill(bits, 3);
                if (codelen_bits > 4)
                    return -1;
                for (int i = 0; i < num_symbols; i++)
                {
                    BitReader_Refill(bits);
                    int sym = BitReader_ReadBitsNoRefill(bits, 8);
                    int codelen = BitReader_ReadBitsNoRefillZero(bits, codelen_bits) + 1;
                    if (codelen > 11)
                        return -1;
                    syms[code_prefix[codelen]++] = (byte)sym;
                }
            }
            return num_symbols;
        }
    }

    private static int BitReader_ReadFluff(BitReader* bits, int num_symbols)
    {
        if (num_symbols == 256)
            return 0;

        int x = 257 - num_symbols;
        if (x > num_symbols)
            x = num_symbols;

        x *= 2;

        int y = (int)BSR((uint)(x - 1)) + 1;

        uint v = bits->bits >> (32 - y);
        uint z = (1u << y) - (uint)x;

        if ((v >> 1) >= z)
        {
            bits->bits <<= y;
            bits->bitpos += y;
            return (int)(v - z);
        }
        bits->bits <<= (y - 1);
        bits->bitpos += (y - 1);
        return (int)(v >> 1);
    }

    private static readonly uint[] kRiceCodeBits2Value =
    {
        0x80000000, 0x00000007, 0x10000006, 0x00000006, 0x20000005, 0x00000105, 0x10000005, 0x00000005,
        0x30000004, 0x00000204, 0x10000104, 0x00000104, 0x20000004, 0x00010004, 0x10000004, 0x00000004,
        0x40000003, 0x00000303, 0x10000203, 0x00000203, 0x20000103, 0x00010103, 0x10000103, 0x00000103,
        0x30000003, 0x00020003, 0x10010003, 0x00010003, 0x20000003, 0x01000003, 0x10000003, 0x00000003,
        0x50000002, 0x00000402, 0x10000302, 0x00000302, 0x20000202, 0x00010202, 0x10000202, 0x00000202,
        0x30000102, 0x00020102, 0x10010102, 0x00010102, 0x20000102, 0x01000102, 0x10000102, 0x00000102,
        0x40000002, 0x00030002, 0x10020002, 0x00020002, 0x20010002, 0x01010002, 0x10010002, 0x00010002,
        0x30000002, 0x02000002, 0x11000002, 0x01000002, 0x20000002, 0x00000012, 0x10000002, 0x00000002,
        0x60000001, 0x00000501, 0x10000401, 0x00000401, 0x20000301, 0x00010301, 0x10000301, 0x00000301,
        0x30000201, 0x00020201, 0x10010201, 0x00010201, 0x20000201, 0x01000201, 0x10000201, 0x00000201,
        0x40000101, 0x00030101, 0x10020101, 0x00020101, 0x20010101, 0x01010101, 0x10010101, 0x00010101,
        0x30000101, 0x02000101, 0x11000101, 0x01000101, 0x20000101, 0x00000111, 0x10000101, 0x00000101,
        0x50000001, 0x00040001, 0x10030001, 0x00030001, 0x20020001, 0x01020001, 0x10020001, 0x00020001,
        0x30010001, 0x02010001, 0x11010001, 0x01010001, 0x20010001, 0x00010011, 0x10010001, 0x00010001,
        0x40000001, 0x03000001, 0x12000001, 0x02000001, 0x21000001, 0x01000011, 0x11000001, 0x01000001,
        0x30000001, 0x00000021, 0x10000011, 0x00000011, 0x20000001, 0x00001001, 0x10000001, 0x00000001,
        0x70000000, 0x00000600, 0x10000500, 0x00000500, 0x20000400, 0x00010400, 0x10000400, 0x00000400,
        0x30000300, 0x00020300, 0x10010300, 0x00010300, 0x20000300, 0x01000300, 0x10000300, 0x00000300,
        0x40000200, 0x00030200, 0x10020200, 0x00020200, 0x20010200, 0x01010200, 0x10010200, 0x00010200,
        0x30000200, 0x02000200, 0x11000200, 0x01000200, 0x20000200, 0x00000210, 0x10000200, 0x00000200,
        0x50000100, 0x00040100, 0x10030100, 0x00030100, 0x20020100, 0x01020100, 0x10020100, 0x00020100,
        0x30010100, 0x02010100, 0x11010100, 0x01010100, 0x20010100, 0x00010110, 0x10010100, 0x00010100,
        0x40000100, 0x03000100, 0x12000100, 0x02000100, 0x21000100, 0x01000110, 0x11000100, 0x01000100,
        0x30000100, 0x00000120, 0x10000110, 0x00000110, 0x20000100, 0x00001100, 0x10000100, 0x00000100,
        0x60000000, 0x00050000, 0x10040000, 0x00040000, 0x20030000, 0x01030000, 0x10030000, 0x00030000,
        0x30020000, 0x02020000, 0x11020000, 0x01020000, 0x20020000, 0x00020010, 0x10020000, 0x00020000,
        0x40010000, 0x03010000, 0x12010000, 0x02010000, 0x21010000, 0x01010010, 0x11010000, 0x01010000,
        0x30010000, 0x00010020, 0x10010010, 0x00010010, 0x20010000, 0x00011000, 0x10010000, 0x00010000,
        0x50000000, 0x04000000, 0x13000000, 0x03000000, 0x22000000, 0x02000010, 0x12000000, 0x02000000,
        0x31000000, 0x01000020, 0x11000010, 0x01000010, 0x21000000, 0x01001000, 0x11000000, 0x01000000,
        0x40000000, 0x00000030, 0x10000020, 0x00000020, 0x20000010, 0x00001010, 0x10000010, 0x00000010,
        0x30000000, 0x00002000, 0x10001000, 0x00001000, 0x20000000, 0x00100000, 0x10000000, 0x00000000,
    };

    private static readonly byte[] kRiceCodeBits2Len =
    {
        0, 1, 1, 2, 1, 2, 2, 3, 1, 2, 2, 3, 2, 3, 3, 4, 1, 2, 2, 3, 2, 3, 3, 4, 2, 3, 3, 4, 3, 4, 4, 5,
        1, 2, 2, 3, 2, 3, 3, 4, 2, 3, 3, 4, 3, 4, 4, 5, 2, 3, 3, 4, 3, 4, 4, 5, 3, 4, 4, 5, 4, 5, 5, 6,
        1, 2, 2, 3, 2, 3, 3, 4, 2, 3, 3, 4, 3, 4, 4, 5, 2, 3, 3, 4, 3, 4, 4, 5, 3, 4, 4, 5, 4, 5, 5, 6,
        2, 3, 3, 4, 3, 4, 4, 5, 3, 4, 4, 5, 4, 5, 5, 6, 3, 4, 4, 5, 4, 5, 5, 6, 4, 5, 5, 6, 5, 6, 6, 7,
        1, 2, 2, 3, 2, 3, 3, 4, 2, 3, 3, 4, 3, 4, 4, 5, 2, 3, 3, 4, 3, 4, 4, 5, 3, 4, 4, 5, 4, 5, 5, 6,
        2, 3, 3, 4, 3, 4, 4, 5, 3, 4, 4, 5, 4, 5, 5, 6, 3, 4, 4, 5, 4, 5, 5, 6, 4, 5, 5, 6, 5, 6, 6, 7,
        2, 3, 3, 4, 3, 4, 4, 5, 3, 4, 4, 5, 4, 5, 5, 6, 3, 4, 4, 5, 4, 5, 5, 6, 4, 5, 5, 6, 5, 6, 6, 7,
        3, 4, 4, 5, 4, 5, 5, 6, 4, 5, 5, 6, 5, 6, 6, 7, 4, 5, 5, 6, 5, 6, 6, 7, 5, 6, 6, 7, 6, 7, 7, 8,
    };

    private static bool DecodeGolombRiceLengths(byte* dst, long size, BitReader2* br)
    {
        byte* p = br->p, p_end = br->p_end;
        byte* dst_end = dst + size;
        if (p >= p_end)
            return false;

        int count = -(int)br->bitpos;
        uint v = (uint)(*p++ & (255 >> (int)br->bitpos));
        for (;;)
        {
            if (v == 0)
                count += 8;
            else
            {
                uint x = kRiceCodeBits2Value[v];
                *(uint*)&dst[0] = (uint)count + (x & 0x0f0f0f0f);
                *(uint*)&dst[4] = (x >> 4) & 0x0f0f0f0f;
                dst += kRiceCodeBits2Len[v];
                if (dst >= dst_end)
                    break;
                count = (int)(x >> 28);
            }
            if (p >= p_end)
                return false;
            v = *p++;
        }
        // went too far, step back
        if (dst > dst_end)
        {
            long n = dst - dst_end;
            do v &= v - 1; while (--n != 0);
        }
        // step back if byte not finished
        int bitpos = 0;
        if ((v & 1) == 0)
        {
            p--;
            uint q = BSF(v);
            bitpos = (int)(8 - q);
        }
        br->p = p;
        br->bitpos = (uint)bitpos;
        return true;
    }

    private static bool DecodeGolombRiceBits(byte* dst, uint size, uint bitcount, BitReader2* br)
    {
        if (bitcount == 0)
            return true;
        byte* dst_end = dst + size;
        byte* p = br->p;
        int bitpos = (int)br->bitpos;

        uint bits_required = (uint)bitpos + bitcount * size;
        uint bytes_required = (bits_required + 7) >> 3;
        if (bytes_required > br->p_end - p)
            return false;

        br->p = p + (bits_required >> 3);
        br->bitpos = bits_required & 7;

        ulong bak = *(ulong*)dst_end;

        if (bitcount < 2)
        {
            do
            {
                ulong bits = (byte)(ByteSwap(ReadU32(p)) >> (24 - bitpos));
                p += 1;
                bits = (bits | (bits << 28)) & 0xF0000000FUL;
                bits = (bits | (bits << 14)) & 0x3000300030003UL;
                bits = (bits | (bits << 7)) & 0x0101010101010101UL;
                *(ulong*)dst = *(ulong*)dst * 2 + ByteSwap(bits);
                dst += 8;
            } while (dst < dst_end);
        }
        else if (bitcount == 2)
        {
            do
            {
                ulong bits = (ushort)(ByteSwap(ReadU32(p)) >> (16 - bitpos));
                p += 2;
                bits = (bits | (bits << 24)) & 0xFF000000FFUL;
                bits = (bits | (bits << 12)) & 0xF000F000F000FUL;
                bits = (bits | (bits << 6)) & 0x0303030303030303UL;
                *(ulong*)dst = *(ulong*)dst * 4 + ByteSwap(bits);
                dst += 8;
            } while (dst < dst_end);
        }
        else
        {
            do
            {
                ulong bits = (ByteSwap(ReadU32(p)) >> (8 - bitpos)) & 0xffffff;
                p += 3;
                bits = (bits | (bits << 20)) & 0xFFF00000FFFUL;
                bits = (bits | (bits << 10)) & 0x3F003F003F003FUL;
                bits = (bits | (bits << 5)) & 0x0707070707070707UL;
                *(ulong*)dst = *(ulong*)dst * 8 + ByteSwap(bits);
                dst += 8;
            } while (dst < dst_end);
        }
        *(ulong*)dst_end = bak;
        return true;
    }

    private static int Huff_ConvertToRanges(HuffRange* range, int num_symbols, int P, byte* symlen, BitReader* bits)
    {
        int num_ranges = P >> 1, v, sym_idx = 0;

        // Start with space?
        if ((P & 1) != 0)
        {
            BitReader_Refill(bits);
            v = *symlen++;
            if (v >= 8)
                return -1;
            sym_idx = BitReader_ReadBitsNoRefill(bits, v + 1) + (1 << (v + 1)) - 1;
        }
        int syms_used = 0;

        for (int i = 0; i < num_ranges; i++)
        {
            BitReader_Refill(bits);
            v = symlen[0];
            if (v >= 9)
                return -1;
            int num = BitReader_ReadBitsNoRefillZero(bits, v) + (1 << v);
            v = symlen[1];
            if (v >= 8)
                return -1;
            int space = BitReader_ReadBitsNoRefill(bits, v + 1) + (1 << (v + 1)) - 1;
            range[i].symbol = (ushort)sym_idx;
            range[i].num = (ushort)num;
            syms_used += num;
            sym_idx += num + space;
            symlen += 2;
        }

        if (sym_idx >= 256 || syms_used >= num_symbols || sym_idx + num_symbols - syms_used > 256)
            return -1;

        range[num_ranges].symbol = (ushort)sym_idx;
        range[num_ranges].num = (ushort)(num_symbols - syms_used);

        return num_ranges + 1;
    }

    private static int Huff_ReadCodeLengthsNew(BitReader* bits, byte* syms, uint* code_prefix)
    {
        int forced_bits = BitReader_ReadBitsNoRefill(bits, 2);

        int num_symbols = BitReader_ReadBitsNoRefill(bits, 8) + 1;

        int fluff = BitReader_ReadFluff(bits, num_symbols);

        byte* code_len = stackalloc byte[512 + 64];
        BitReader2 br2;
        br2.bitpos = (uint)((bits->bitpos - 24) & 7);
        br2.p_end = bits->p_end;
        br2.p = bits->p - (uint)((24 - bits->bitpos + 7) >> 3);

        if (!DecodeGolombRiceLengths(code_len, num_symbols + fluff, &br2))
            return -1;
        MemSet(code_len + (num_symbols + fluff), 0, 16);
        if (!DecodeGolombRiceBits(code_len, (uint)num_symbols, (uint)forced_bits, &br2))
            return -1;

        // Reset the bits decoder.
        bits->bitpos = 24;
        bits->p = br2.p;
        bits->bits = 0;
        BitReader_Refill(bits);
        bits->bits <<= (int)br2.bitpos;
        bits->bitpos += (int)br2.bitpos;

        uint running_sum = 0x1e;
        for (int i = 0; i < num_symbols; i++)
        {
            int v = code_len[i];
            v = -(v & 1) ^ (v >> 1);
            code_len[i] = (byte)(v + (int)(running_sum >> 2) + 1);
            if (code_len[i] < 1 || code_len[i] > 11)
                return -1;
            running_sum = (uint)(running_sum + v);
        }

        HuffRange* range = stackalloc HuffRange[128];
        int ranges = Huff_ConvertToRanges(range, num_symbols, fluff, &code_len[num_symbols], bits);
        if (ranges <= 0)
            return -1;

        byte* cp = code_len;
        for (int i = 0; i < ranges; i++)
        {
            int sym = range[i].symbol;
            int n = range[i].num;
            do
            {
                syms[code_prefix[*cp++]++] = (byte)sym++;
            } while (--n != 0);
        }

        return num_symbols;
    }

    private static bool Huff_MakeLut(uint* prefix_org, uint* prefix_cur, byte* bits2len, byte* bits2sym, byte* syms)
    {
        uint currslot = 0;
        for (uint i = 1; i < 11; i++)
        {
            uint start = prefix_org[i];
            uint count = prefix_cur[i] - start;
            if (count != 0)
            {
                uint stepsize = 1u << (int)(11 - i);
                uint num_to_set = count << (int)(11 - i);
                if (currslot + num_to_set > 2048)
                    return false;
                MemSet(&bits2len[currslot], (byte)i, num_to_set);

                byte* p = &bits2sym[currslot];
                for (uint j = 0; j != count; j++, p += stepsize)
                    MemSet(p, syms[start + j], stepsize);
                currslot += num_to_set;
            }
        }
        if (prefix_cur[11] - prefix_org[11] != 0)
        {
            uint num_to_set = prefix_cur[11] - prefix_org[11];
            if (currslot + num_to_set > 2048)
                return false;
            MemSet(&bits2len[currslot], 11, num_to_set);
            MemMove(&bits2sym[currslot], &syms[prefix_org[11]], num_to_set);
            currslot += num_to_set;
        }
        return currslot == 2048;
    }

    private static readonly uint[] CodePrefixOrg = { 0x0, 0x0, 0x2, 0x6, 0xE, 0x1E, 0x3E, 0x7E, 0xFE, 0x1FE, 0x2FE, 0x3FE };

    private static int Kraken_DecodeBytes_Type12(byte* src, long src_size, byte* output, int output_size, int type)
    {
        BitReader bits;
        byte* src_end = src + src_size;

        bits.bitpos = 24;
        bits.bits = 0;
        bits.p = src;
        bits.p_end = src_end;
        BitReader_Refill(&bits);

        uint* code_prefix_org = stackalloc uint[12];
        uint* code_prefix = stackalloc uint[12];
        for (int i = 0; i < 12; i++) code_prefix_org[i] = code_prefix[i] = CodePrefixOrg[i];
        byte* syms = stackalloc byte[1280];
        int num_syms;
        if (BitReader_ReadBitNoRefill(&bits) == 0)
            num_syms = Huff_ReadCodeLengthsOld(&bits, syms, code_prefix);
        else if (BitReader_ReadBitNoRefill(&bits) == 0)
            num_syms = Huff_ReadCodeLengthsNew(&bits, syms, code_prefix);
        else
            return -1;

        if (num_syms < 1)
            return -1;
        src = bits.p - ((24 - bits.bitpos) / 8);

        if (num_syms == 1)
        {
            MemSet(output, syms[0], output_size);
            return (int)(src - src_end);
        }

        byte* lut_len = stackalloc byte[2048 + 16];
        byte* lut_sym = stackalloc byte[2048 + 16];
        if (!Huff_MakeLut(code_prefix_org, code_prefix, lut_len, lut_sym, syms))
            return -1;

        byte* rev_len = stackalloc byte[2048];
        byte* rev_sym = stackalloc byte[2048];
        ReverseBitsArray2048(lut_len, rev_len);
        ReverseBitsArray2048(lut_sym, rev_sym);

        HuffReader hr;
        if (type == 1)
        {
            if (src + 3 > src_end)
                return -1;
            uint split_mid = ReadU16(src);
            src += 2;
            hr.output = output;
            hr.output_end = output + output_size;
            hr.src = src;
            hr.src_end = src_end;
            hr.src_mid_org = hr.src_mid = src + split_mid;
            hr.src_bitpos = 0; hr.src_bits = 0;
            hr.src_mid_bitpos = 0; hr.src_mid_bits = 0;
            hr.src_end_bitpos = 0; hr.src_end_bits = 0;
            if (!Kraken_DecodeBytesCore(&hr, rev_len, rev_sym))
                return -1;
        }
        else
        {
            if (src + 6 > src_end)
                return -1;

            int half_output_size = (output_size + 1) >> 1;
            uint split_mid = ReadU32(src) & 0xFFFFFF;
            src += 3;
            if (split_mid > src_end - src)
                return -1;
            byte* src_mid = src + split_mid;
            uint split_left = ReadU16(src);
            src += 2;
            if (src_mid - src < split_left + 2 || src_end - src_mid < 3)
                return -1;
            uint split_right = ReadU16(src_mid);
            if (src_end - (src_mid + 2) < split_right + 2)
                return -1;

            hr.output = output;
            hr.output_end = output + half_output_size;
            hr.src = src;
            hr.src_end = src_mid;
            hr.src_mid_org = hr.src_mid = src + split_left;
            hr.src_bitpos = 0; hr.src_bits = 0;
            hr.src_mid_bitpos = 0; hr.src_mid_bits = 0;
            hr.src_end_bitpos = 0; hr.src_end_bits = 0;
            if (!Kraken_DecodeBytesCore(&hr, rev_len, rev_sym))
                return -1;

            hr.output = output + half_output_size;
            hr.output_end = output + output_size;
            hr.src = src_mid + 2;
            hr.src_end = src_end;
            hr.src_mid_org = hr.src_mid = src_mid + 2 + split_right;
            hr.src_bitpos = 0; hr.src_bits = 0;
            hr.src_mid_bitpos = 0; hr.src_mid_bits = 0;
            hr.src_end_bitpos = 0; hr.src_end_bits = 0;
            if (!Kraken_DecodeBytesCore(&hr, rev_len, rev_sym))
                return -1;
        }
        return (int)src_size;
    }

    // ------------------------------------------------------------------ multi-array, recursive, RLE

    private static int Kraken_DecodeMultiArray(byte* src, byte* src_end, byte* dst, byte* dst_end,
                                               byte** array_data, int* array_lens, int array_count,
                                               int* total_size_out, bool force_memmove, byte* scratch, byte* scratch_end)
    {
        byte* src_org = src;

        if (src_end - src < 4)
            return -1;

        int decoded_size;
        int num_arrays_in_file = *src++;
        if ((num_arrays_in_file & 0x80) == 0)
            return -1;
        num_arrays_in_file &= 0x3f;

        if (dst == scratch)
        {
            scratch += (scratch_end - scratch - 0xc000) >> 1;
            dst_end = scratch;
        }

        int total_size = 0;

        if (num_arrays_in_file == 0)
        {
            for (int i = 0; i < array_count; i++)
            {
                byte* chunk_dst = dst;
                int dec = Kraken_DecodeBytes(&chunk_dst, src, src_end, &decoded_size, dst_end - dst, force_memmove, scratch, scratch_end);
                if (dec < 0)
                    return -1;
                dst += decoded_size;
                array_lens[i] = decoded_size;
                array_data[i] = chunk_dst;
                src += dec;
                total_size += decoded_size;
            }
            *total_size_out = total_size;
            return (int)(src - src_org);
        }

        byte** entropy_array_data = stackalloc byte*[32];
        uint* entropy_array_size = stackalloc uint[32];

        // First loop just decodes everything to scratch
        byte* scratch_cur = scratch;

        for (int i = 0; i < num_arrays_in_file; i++)
        {
            byte* chunk_dst = scratch_cur;
            int dec = Kraken_DecodeBytes(&chunk_dst, src, src_end, &decoded_size, scratch_end - scratch_cur, force_memmove, scratch_cur, scratch_end);
            if (dec < 0)
                return -1;
            entropy_array_data[i] = chunk_dst;
            entropy_array_size[i] = (uint)decoded_size;
            scratch_cur += decoded_size;
            total_size += decoded_size;
            src += dec;
        }
        *total_size_out = total_size;

        if (src_end - src < 3)
            return -1;

        int Q = ReadU16(src);
        src += 2;

        int out_size;
        if (Kraken_GetBlockSize(src, src_end, &out_size, total_size) < 0)
            return -1;
        int num_indexes = out_size;

        int num_lens = num_indexes - array_count;
        if (num_lens < 1)
            return -1;

        if (scratch_end - scratch_cur < num_indexes)
            return -1;
        byte* interval_lenlog2 = scratch_cur;
        scratch_cur += num_indexes;

        if (scratch_end - scratch_cur < num_indexes)
            return -1;
        byte* interval_indexes = scratch_cur;
        scratch_cur += num_indexes;

        if ((Q & 0x8000) != 0)
        {
            int size_out;
            int n = Kraken_DecodeBytes(&interval_indexes, src, src_end, &size_out, num_indexes, false, scratch_cur, scratch_end);
            if (n < 0 || size_out != num_indexes)
                return -1;
            src += n;

            for (int i = 0; i < num_indexes; i++)
            {
                int t = interval_indexes[i];
                interval_lenlog2[i] = (byte)(t >> 4);
                interval_indexes[i] = (byte)(t & 0xF);
            }

            num_lens = num_indexes;
        }
        else
        {
            int lenlog2_chunksize = num_indexes - array_count;

            int size_out;
            int n = Kraken_DecodeBytes(&interval_indexes, src, src_end, &size_out, num_indexes, false, scratch_cur, scratch_end);
            if (n < 0 || size_out != num_indexes)
                return -1;
            src += n;

            n = Kraken_DecodeBytes(&interval_lenlog2, src, src_end, &size_out, lenlog2_chunksize, false, scratch_cur, scratch_end);
            if (n < 0 || size_out != lenlog2_chunksize)
                return -1;
            src += n;

            for (int i = 0; i < lenlog2_chunksize; i++)
                if (interval_lenlog2[i] > 16)
                    return -1;
        }

        if (scratch_end - scratch_cur < 4)
            return -1;

        scratch_cur = AlignPointer(scratch_cur, 4);
        if (scratch_end - scratch_cur < num_lens * 4)
            return -1;
        uint* decoded_intervals = (uint*)scratch_cur;

        int varbits_complen = Q & 0x3FFF;
        if (src_end - src < varbits_complen)
            return -1;

        byte* f = src;
        uint bits_f = 0;
        int bitpos_f = 24;

        byte* src_end_actual = src + varbits_complen;

        byte* b = src_end_actual;
        uint bits_b = 0;
        int bitpos_b = 24;

        int ii;
        for (ii = 0; ii + 2 <= num_lens; ii += 2)
        {
            bits_f |= ByteSwap(ReadU32(f)) >> (24 - bitpos_f);
            f += (bitpos_f + 7) >> 3;

            bits_b |= ((uint*)b)[-1] >> (24 - bitpos_b);
            b -= (bitpos_b + 7) >> 3;

            int numbits_f = interval_lenlog2[ii + 0];
            int numbits_b = interval_lenlog2[ii + 1];

            bits_f = Rotl(bits_f | 1, numbits_f);
            bitpos_f += numbits_f - 8 * ((bitpos_f + 7) >> 3);

            bits_b = Rotl(bits_b | 1, numbits_b);
            bitpos_b += numbits_b - 8 * ((bitpos_b + 7) >> 3);

            int value_f = (int)(bits_f & Bitmasks[numbits_f]);
            bits_f &= ~Bitmasks[numbits_f];

            int value_b = (int)(bits_b & Bitmasks[numbits_b]);
            bits_b &= ~Bitmasks[numbits_b];

            decoded_intervals[ii + 0] = (uint)value_f;
            decoded_intervals[ii + 1] = (uint)value_b;
        }

        // read final one since above loop reads 2
        if (ii < num_lens)
        {
            bits_f |= ByteSwap(ReadU32(f)) >> (24 - bitpos_f);
            int numbits_f = interval_lenlog2[ii];
            bits_f = Rotl(bits_f | 1, numbits_f);
            int value_f = (int)(bits_f & Bitmasks[numbits_f]);
            decoded_intervals[ii + 0] = (uint)value_f;
        }

        if (interval_indexes[num_indexes - 1] != 0)
            return -1;

        int indi = 0, leni = 0, source;
        int increment_leni = (Q & 0x8000) != 0 ? 1 : 0;

        for (int arri = 0; arri < array_count; arri++)
        {
            array_data[arri] = dst;
            if (indi >= num_indexes)
                return -1;

            while ((source = interval_indexes[indi++]) != 0)
            {
                if (source > num_arrays_in_file)
                    return -1;
                if (leni >= num_lens)
                    return -1;
                int cur_len = (int)decoded_intervals[leni++];
                int bytes_left = (int)entropy_array_size[source - 1];
                if (cur_len > bytes_left || cur_len > dst_end - dst)
                    return -1;
                byte* blksrc = entropy_array_data[source - 1];
                entropy_array_size[source - 1] -= (uint)cur_len;
                entropy_array_data[source - 1] += cur_len;
                byte* dstx = dst;
                dst += cur_len;
                MemMove(dstx, blksrc, cur_len);
            }
            leni += increment_leni;
            array_lens[arri] = (int)(dst - array_data[arri]);
        }

        if (indi != num_indexes || leni != num_lens)
            return -1;

        for (int i = 0; i < num_arrays_in_file; i++)
            if (entropy_array_size[i] != 0)
                return -1;
        return (int)(src_end_actual - src_org);
    }

    private static int Krak_DecodeRecursive(byte* src, long src_size, byte* output, int output_size, byte* scratch, byte* scratch_end)
    {
        byte* src_org = src;
        byte* output_end = output + output_size;
        byte* src_end = src + src_size;

        if (src_size < 6)
            return -1;

        int n = src[0] & 0x7f;
        if (n < 2)
            return -1;

        if ((src[0] & 0x80) == 0)
        {
            src++;
            do
            {
                int decoded_size;
                int dec = Kraken_DecodeBytes(&output, src, src_end, &decoded_size, output_end - output, true, scratch, scratch_end);
                if (dec < 0)
                    return -1;
                output += decoded_size;
                src += dec;
            } while (--n != 0);
            if (output != output_end)
                return -1;
            return (int)(src - src_org);
        }
        else
        {
            byte* array_data;
            int array_len, decoded_size;
            int dec = Kraken_DecodeMultiArray(src, src_end, output, output_end, &array_data, &array_len, 1, &decoded_size, true, scratch, scratch_end);
            if (dec < 0)
                return -1;
            output += decoded_size;
            if (output != output_end)
                return -1;
            return dec;
        }
    }

    private static int Krak_DecodeRLE(byte* src, long src_size, byte* dst, int dst_size, byte* scratch, byte* scratch_end)
    {
        if (src_size <= 1)
        {
            if (src_size != 1)
                return -1;
            MemSet(dst, src[0], dst_size);
            return 1;
        }
        byte* dst_end = dst + dst_size;
        byte* cmd_ptr = src + 1, cmd_ptr_end = src + src_size;
        // Unpack the first X bytes of the command buffer?
        if (src[0] != 0)
        {
            byte* dst_ptr = scratch;
            int dec_size;
            int n = Kraken_DecodeBytes(&dst_ptr, src, src + src_size, &dec_size, scratch_end - scratch, true, scratch, scratch_end);
            if (n <= 0)
                return -1;
            long cmd_len = src_size - n + dec_size;
            if (cmd_len > scratch_end - scratch)
                return -1;
            MemMove(dst_ptr + dec_size, src + n, src_size - n);
            cmd_ptr = dst_ptr;
            cmd_ptr_end = &dst_ptr[cmd_len];
        }

        byte rle_byte = 0;

        while (cmd_ptr < cmd_ptr_end)
        {
            uint cmd = cmd_ptr_end[-1];
            if (cmd - 1 >= 0x2f)
            {
                cmd_ptr_end--;
                uint bytes_to_copy = unchecked((uint)-1 - cmd) & 0xF;
                uint bytes_to_rle = cmd >> 4;
                if (dst_end - dst < bytes_to_copy + bytes_to_rle || cmd_ptr_end - cmd_ptr < bytes_to_copy)
                    return -1;
                MemMove(dst, cmd_ptr, bytes_to_copy);
                cmd_ptr += bytes_to_copy;
                dst += bytes_to_copy;
                MemSet(dst, rle_byte, bytes_to_rle);
                dst += bytes_to_rle;
            }
            else if (cmd >= 0x10)
            {
                uint data = (uint)ReadU16(cmd_ptr_end - 2) - 4096;
                cmd_ptr_end -= 2;
                uint bytes_to_copy = data & 0x3F;
                uint bytes_to_rle = data >> 6;
                if (dst_end - dst < bytes_to_copy + bytes_to_rle || cmd_ptr_end - cmd_ptr < bytes_to_copy)
                    return -1;
                MemMove(dst, cmd_ptr, bytes_to_copy);
                cmd_ptr += bytes_to_copy;
                dst += bytes_to_copy;
                MemSet(dst, rle_byte, bytes_to_rle);
                dst += bytes_to_rle;
            }
            else if (cmd == 1)
            {
                rle_byte = *cmd_ptr++;
                cmd_ptr_end--;
            }
            else if (cmd >= 9)
            {
                uint bytes_to_rle = ((uint)ReadU16(cmd_ptr_end - 2) - 0x8ff) * 128;
                cmd_ptr_end -= 2;
                if (dst_end - dst < bytes_to_rle)
                    return -1;
                MemSet(dst, rle_byte, bytes_to_rle);
                dst += bytes_to_rle;
            }
            else
            {
                uint bytes_to_copy = ((uint)ReadU16(cmd_ptr_end - 2) - 511) * 64;
                cmd_ptr_end -= 2;
                if (cmd_ptr_end - cmd_ptr < bytes_to_copy || dst_end - dst < bytes_to_copy)
                    return -1;
                MemMove(dst, cmd_ptr, bytes_to_copy);
                dst += bytes_to_copy;
                cmd_ptr += bytes_to_copy;
            }
        }
        if (cmd_ptr_end != cmd_ptr)
            return -1;

        if (dst != dst_end)
            return -1;

        return (int)src_size;
    }

    // ------------------------------------------------------------------ tANS

    private struct TansData
    {
        public uint A_used;
        public uint B_used;
        public fixed byte A[256];
        public fixed uint B[256];
    }

    private static void SimpleSortBytes(byte* p, byte* pend)
    {
        if (p == pend) return;
        for (byte* lp = p + 1; lp != pend; lp++)
        {
            byte t = lp[0];
            byte* rp;
            for (rp = lp; rp > p && t < rp[-1]; rp--)
                rp[0] = rp[-1];
            rp[0] = t;
        }
    }

    private static void SimpleSortUInts(uint* p, uint* pend)
    {
        if (p == pend) return;
        for (uint* lp = p + 1; lp != pend; lp++)
        {
            uint t = lp[0];
            uint* rp;
            for (rp = lp; rp > p && t < rp[-1]; rp--)
                rp[0] = rp[-1];
            rp[0] = t;
        }
    }

    private static bool Tans_DecodeTable(BitReader* bits, int L_bits, TansData* tans_data)
    {
        BitReader_Refill(bits);
        if (BitReader_ReadBitNoRefill(bits) != 0)
        {
            int Q = BitReader_ReadBitsNoRefill(bits, 3);
            int num_symbols = BitReader_ReadBitsNoRefill(bits, 8) + 1;
            if (num_symbols < 2)
                return false;
            int fluff = BitReader_ReadFluff(bits, num_symbols);
            int total_rice_values = fluff + num_symbols;
            byte* rice = stackalloc byte[512 + 64];
            BitReader2 br2;

            // another bit reader...
            br2.p = bits->p - (uint)((24 - bits->bitpos + 7) >> 3);
            br2.p_end = bits->p_end;
            br2.bitpos = (uint)((bits->bitpos - 24) & 7);

            if (!DecodeGolombRiceLengths(rice, total_rice_values, &br2))
                return false;
            MemSet(rice + total_rice_values, 0, 16);

            // Switch back to other bitreader impl
            bits->bitpos = 24;
            bits->p = br2.p;
            bits->bits = 0;
            BitReader_Refill(bits);
            bits->bits <<= (int)br2.bitpos;
            bits->bitpos += (int)br2.bitpos;

            HuffRange* range = stackalloc HuffRange[133];
            fluff = Huff_ConvertToRanges(range, num_symbols, fluff, &rice[num_symbols], bits);
            if (fluff < 0)
                return false;

            BitReader_Refill(bits);

            uint L = 1u << L_bits;
            byte* cur_rice_ptr = rice;
            int average = 6;
            int somesum = 0;
            byte* tanstable_A = tans_data->A;
            uint* tanstable_B = tans_data->B;

            for (int ri = 0; ri < fluff; ri++)
            {
                int symbol = range[ri].symbol;
                int num = range[ri].num;
                do
                {
                    BitReader_Refill(bits);

                    int nextra = Q + *cur_rice_ptr++;
                    if (nextra > 15)
                        return false;
                    int v = BitReader_ReadBitsNoRefillZero(bits, nextra) + (1 << nextra) - (1 << Q);

                    int average_div4 = average >> 2;
                    int limit = 2 * average_div4;
                    if (v <= limit)
                        v = average_div4 + (-(v & 1) ^ (int)((uint)v >> 1));
                    if (limit > v)
                        limit = v;
                    v += 1;
                    average += limit - average_div4;
                    *tanstable_A = (byte)symbol;
                    *tanstable_B = (uint)((symbol << 16) + v);
                    tanstable_A += v == 1 ? 1 : 0;
                    tanstable_B += v >= 2 ? 1 : 0;
                    somesum += v;
                    symbol += 1;
                } while (--num != 0);
            }
            tans_data->A_used = (uint)(tanstable_A - tans_data->A);
            tans_data->B_used = (uint)(tanstable_B - tans_data->B);
            if (somesum != L)
                return false;

            return true;
        }
        else
        {
            bool* seen = stackalloc bool[256];
            for (int i = 0; i < 256; i++) seen[i] = false;
            uint L = 1u << L_bits;

            int count = BitReader_ReadBitsNoRefill(bits, 3) + 1;

            int bits_per_sym = (int)BSR((uint)L_bits) + 1;
            int max_delta_bits = BitReader_ReadBitsNoRefill(bits, bits_per_sym);

            if (max_delta_bits == 0 || max_delta_bits > L_bits)
                return false;

            byte* tanstable_A = tans_data->A;
            uint* tanstable_B = tans_data->B;

            int weight = 0;
            int total_weights = 0;

            do
            {
                BitReader_Refill(bits);

                int sym = BitReader_ReadBitsNoRefill(bits, 8);
                if (seen[sym])
                    return false;

                int delta = BitReader_ReadBitsNoRefill(bits, max_delta_bits);

                weight += delta;

                if (weight == 0)
                    return false;

                seen[sym] = true;
                if (weight == 1)
                    *tanstable_A++ = (byte)sym;
                else
                    *tanstable_B++ = (uint)((sym << 16) + weight);

                total_weights += weight;
            } while (--count != 0);

            BitReader_Refill(bits);

            int sym2 = BitReader_ReadBitsNoRefill(bits, 8);
            if (seen[sym2])
                return false;

            if (L - total_weights < weight || L - total_weights <= 1)
                return false;

            *tanstable_B++ = (uint)((sym2 << 16) + (L - total_weights));

            tans_data->A_used = (uint)(tanstable_A - tans_data->A);
            tans_data->B_used = (uint)(tanstable_B - tans_data->B);

            SimpleSortBytes(tans_data->A, tanstable_A);
            SimpleSortUInts(tans_data->B, tanstable_B);
            return true;
        }
    }

    private static void Tans_InitLut(TansData* tans_data, int L_bits, TansLutEnt* lut)
    {
        TansLutEnt** pointers = stackalloc TansLutEnt*[4];

        int L = 1 << L_bits;
        int a_used = (int)tans_data->A_used;

        uint slots_left_to_alloc = (uint)(L - a_used);

        uint sa = slots_left_to_alloc >> 2;
        pointers[0] = lut;
        uint sb = sa + ((slots_left_to_alloc & 3) > 0 ? 1u : 0u);
        pointers[1] = lut + sb;
        sb += sa + ((slots_left_to_alloc & 3) > 1 ? 1u : 0u);
        pointers[2] = lut + sb;
        sb += sa + ((slots_left_to_alloc & 3) > 2 ? 1u : 0u);
        pointers[3] = lut + sb;

        // Setup the single entrys with weight=1
        {
            TansLutEnt* lut_singles = lut + slots_left_to_alloc;
            TansLutEnt le;
            le.w = 0;
            le.bits_x = (byte)L_bits;
            le.x = (1u << L_bits) - 1;
            le.symbol = 0;
            for (int i = 0; i < a_used; i++)
            {
                lut_singles[i] = le;
                lut_singles[i].symbol = tans_data->A[i];
            }
        }

        // Setup the entrys with weight >= 2
        int weights_sum = 0;
        for (int i = 0; i < tans_data->B_used; i++)
        {
            int weight = (int)(tans_data->B[i] & 0xffff);
            int symbol = (int)(tans_data->B[i] >> 16);
            if (weight > 4)
            {
                uint sym_bits = BSR((uint)weight);
                int Z = L_bits - (int)sym_bits;
                TansLutEnt le;
                le.symbol = (byte)symbol;
                le.bits_x = (byte)Z;
                le.x = (1u << Z) - 1;
                le.w = (ushort)((L - 1) & (weight << Z));
                int what_to_add = 1 << Z;
                int X = (1 << (int)(sym_bits + 1)) - weight;

                for (int j = 0; j < 4; j++)
                {
                    TansLutEnt* dst = pointers[j];

                    int Y = (weight + ((weights_sum - j - 1) & 3)) >> 2;
                    if (X >= Y)
                    {
                        for (int n = Y; n != 0; n--)
                        {
                            *dst++ = le;
                            le.w = (ushort)(le.w + what_to_add);
                        }
                        X -= Y;
                    }
                    else
                    {
                        for (int n = X; n != 0; n--)
                        {
                            *dst++ = le;
                            le.w = (ushort)(le.w + what_to_add);
                        }
                        Z--;

                        what_to_add >>= 1;
                        le.bits_x = (byte)Z;
                        le.w = 0;
                        le.x >>= 1;
                        for (int n = Y - X; n != 0; n--)
                        {
                            *dst++ = le;
                            le.w = (ushort)(le.w + what_to_add);
                        }
                        X = weight;
                    }
                    pointers[j] = dst;
                }
            }
            else
            {
                uint bits = ((1u << weight) - 1) << (weights_sum & 3);
                bits |= bits >> 4;
                int n = weight, ww = weight;
                do
                {
                    uint idx = BSF(bits);
                    bits &= bits - 1;
                    TansLutEnt* dst = pointers[idx]++;
                    dst->symbol = (byte)symbol;
                    uint weight_bits = BSR((uint)ww);
                    dst->bits_x = (byte)(L_bits - (int)weight_bits);
                    dst->x = (1u << (L_bits - (int)weight_bits)) - 1;
                    dst->w = (ushort)((L - 1) & (ww++ << (L_bits - (int)weight_bits)));
                } while (--n != 0);
            }
            weights_sum += weight;
        }
    }

    private static bool Tans_Decode(TansDecoderParams* prm)
    {
        TansLutEnt* lut = prm->lut, e;
        byte* dst = prm->dst, dst_end = prm->dst_end;
        byte* ptr_f = prm->ptr_f, ptr_b = prm->ptr_b;
        uint bits_f = prm->bits_f, bits_b = prm->bits_b;
        int bitpos_f = prm->bitpos_f, bitpos_b = prm->bitpos_b;
        uint state_0 = prm->state_0, state_1 = prm->state_1;
        uint state_2 = prm->state_2, state_3 = prm->state_3;
        uint state_4 = prm->state_4;

        if (ptr_f > ptr_b)
            return false;

        if (dst < dst_end)
        {
            for (;;)
            {
                // TANS_FORWARD_BITS
                bits_f |= ReadU32(ptr_f) << bitpos_f; ptr_f += (31 - bitpos_f) >> 3; bitpos_f |= 24;
                e = &lut[state_0]; *dst++ = e->symbol; bitpos_f -= e->bits_x; state_0 = (bits_f & e->x) + e->w; bits_f >>= e->bits_x; if (dst >= dst_end) break;
                e = &lut[state_1]; *dst++ = e->symbol; bitpos_f -= e->bits_x; state_1 = (bits_f & e->x) + e->w; bits_f >>= e->bits_x; if (dst >= dst_end) break;
                bits_f |= ReadU32(ptr_f) << bitpos_f; ptr_f += (31 - bitpos_f) >> 3; bitpos_f |= 24;
                e = &lut[state_2]; *dst++ = e->symbol; bitpos_f -= e->bits_x; state_2 = (bits_f & e->x) + e->w; bits_f >>= e->bits_x; if (dst >= dst_end) break;
                e = &lut[state_3]; *dst++ = e->symbol; bitpos_f -= e->bits_x; state_3 = (bits_f & e->x) + e->w; bits_f >>= e->bits_x; if (dst >= dst_end) break;
                bits_f |= ReadU32(ptr_f) << bitpos_f; ptr_f += (31 - bitpos_f) >> 3; bitpos_f |= 24;
                e = &lut[state_4]; *dst++ = e->symbol; bitpos_f -= e->bits_x; state_4 = (bits_f & e->x) + e->w; bits_f >>= e->bits_x; if (dst >= dst_end) break;
                // TANS_BACKWARD_BITS
                bits_b |= ByteSwap(((uint*)ptr_b)[-1]) << bitpos_b; ptr_b -= (31 - bitpos_b) >> 3; bitpos_b |= 24;
                e = &lut[state_0]; *dst++ = e->symbol; bitpos_b -= e->bits_x; state_0 = (bits_b & e->x) + e->w; bits_b >>= e->bits_x; if (dst >= dst_end) break;
                e = &lut[state_1]; *dst++ = e->symbol; bitpos_b -= e->bits_x; state_1 = (bits_b & e->x) + e->w; bits_b >>= e->bits_x; if (dst >= dst_end) break;
                bits_b |= ByteSwap(((uint*)ptr_b)[-1]) << bitpos_b; ptr_b -= (31 - bitpos_b) >> 3; bitpos_b |= 24;
                e = &lut[state_2]; *dst++ = e->symbol; bitpos_b -= e->bits_x; state_2 = (bits_b & e->x) + e->w; bits_b >>= e->bits_x; if (dst >= dst_end) break;
                e = &lut[state_3]; *dst++ = e->symbol; bitpos_b -= e->bits_x; state_3 = (bits_b & e->x) + e->w; bits_b >>= e->bits_x; if (dst >= dst_end) break;
                bits_b |= ByteSwap(((uint*)ptr_b)[-1]) << bitpos_b; ptr_b -= (31 - bitpos_b) >> 3; bitpos_b |= 24;
                e = &lut[state_4]; *dst++ = e->symbol; bitpos_b -= e->bits_x; state_4 = (bits_b & e->x) + e->w; bits_b >>= e->bits_x; if (dst >= dst_end) break;
            }
        }

        if (ptr_b - ptr_f + (bitpos_f >> 3) + (bitpos_b >> 3) != 0)
            return false;

        uint states_or = state_0 | state_1 | state_2 | state_3 | state_4;
        if ((states_or & ~0xFFu) != 0)
            return false;

        dst_end[0] = (byte)state_0;
        dst_end[1] = (byte)state_1;
        dst_end[2] = (byte)state_2;
        dst_end[3] = (byte)state_3;
        dst_end[4] = (byte)state_4;
        return true;
    }

    private static int Krak_DecodeTans(byte* src, long src_size, byte* dst, int dst_size, byte* scratch, byte* scratch_end)
    {
        if (src_size < 8 || dst_size < 5)
            return -1;

        byte* src_end = src + src_size;

        BitReader br;
        TansData tans_data;

        br.bitpos = 24;
        br.bits = 0;
        br.p = src;
        br.p_end = src_end;
        BitReader_Refill(&br);

        // reserved bit
        if (BitReader_ReadBitNoRefill(&br) != 0)
            return -1;

        int L_bits = BitReader_ReadBitsNoRefill(&br, 2) + 8;

        if (!Tans_DecodeTable(&br, L_bits, &tans_data))
            return -1;

        src = br.p - (24 - br.bitpos) / 8;

        if (src >= src_end)
            return -1;

        uint lut_space_required = (uint)(((sizeof(TansLutEnt) << L_bits) + 15) & ~15);
        if (lut_space_required > scratch_end - scratch)
            return -1;

        TansDecoderParams prm;
        prm.dst = dst;
        prm.dst_end = dst + dst_size - 5;

        prm.lut = (TansLutEnt*)AlignPointer(scratch, 16);
        Tans_InitLut(&tans_data, L_bits, prm.lut);

        // Read out the initial state
        uint L_mask = (1u << L_bits) - 1;
        uint bits_f = ReadU32(src);
        src += 4;
        uint bits_b = ByteSwap(ReadU32(src_end - 4));
        src_end -= 4;
        uint bitpos_f = 32, bitpos_b = 32;

        // Read first two.
        prm.state_0 = bits_f & L_mask;
        prm.state_1 = bits_b & L_mask;
        bits_f >>= L_bits; bitpos_f -= (uint)L_bits;
        bits_b >>= L_bits; bitpos_b -= (uint)L_bits;

        // Read next two.
        prm.state_2 = bits_f & L_mask;
        prm.state_3 = bits_b & L_mask;
        bits_f >>= L_bits; bitpos_f -= (uint)L_bits;
        bits_b >>= L_bits; bitpos_b -= (uint)L_bits;

        // Refill more bits
        bits_f |= ReadU32(src) << (int)bitpos_f;
        src += (31 - bitpos_f) >> 3;
        bitpos_f |= 24;

        // Read final state variable
        prm.state_4 = bits_f & L_mask;
        bits_f >>= L_bits; bitpos_f -= (uint)L_bits;

        prm.bits_f = bits_f;
        prm.ptr_f = src - (bitpos_f >> 3);
        prm.bitpos_f = (int)(bitpos_f & 7);

        prm.bits_b = bits_b;
        prm.ptr_b = src_end + (bitpos_b >> 3);
        prm.bitpos_b = (int)(bitpos_b & 7);

        if (!Tans_Decode(&prm))
            return -1;

        return (int)src_size;
    }

    // ------------------------------------------------------------------ entropy dispatch

    private static int Kraken_GetBlockSize(byte* src, byte* src_end, int* dest_size, int dest_capacity)
    {
        byte* src_org = src;
        int src_size, dst_size;

        if (src_end - src < 2)
            return -1; // too few bytes

        int chunk_type = (src[0] >> 4) & 0x7;
        if (chunk_type == 0)
        {
            if (src[0] >= 0x80)
            {
                // In this mode, memcopy stores the length in the bottom 12 bits.
                src_size = ((src[0] << 8) | src[1]) & 0xFFF;
                src += 2;
            }
            else
            {
                if (src_end - src < 3)
                    return -1; // too few bytes
                src_size = (src[0] << 16) | (src[1] << 8) | src[2];
                if ((src_size & ~0x3ffff) != 0)
                    return -1; // reserved bits must not be set
                src += 3;
            }
            if (src_size > dest_capacity || src_end - src < src_size)
                return -1;
            *dest_size = src_size;
            return (int)(src + src_size - src_org);
        }

        if (chunk_type >= 6)
            return -1;

        // In all the other modes, the initial bytes encode the src_size and the dst_size
        if (src[0] >= 0x80)
        {
            if (src_end - src < 3)
                return -1; // too few bytes

            // short mode, 10 bit sizes
            uint bits = (uint)((src[0] << 16) | (src[1] << 8) | src[2]);
            src_size = (int)(bits & 0x3ff);
            dst_size = src_size + (int)((bits >> 10) & 0x3ff) + 1;
            src += 3;
        }
        else
        {
            // long mode, 18 bit sizes
            if (src_end - src < 5)
                return -1; // too few bytes
            uint bits = (uint)((src[1] << 24) | (src[2] << 16) | (src[3] << 8) | src[4]);
            src_size = (int)(bits & 0x3ffff);
            dst_size = (int)(((bits >> 18) | ((uint)src[0] << 14)) & 0x3FFFF) + 1;
            if (src_size >= dst_size)
                return -1;
            src += 5;
        }
        if (src_end - src < src_size || dst_size > dest_capacity)
            return -1;
        *dest_size = dst_size;
        return src_size;
    }

    private static int Kraken_DecodeBytes(byte** output, byte* src, byte* src_end, int* decoded_size, long output_size,
                                          bool force_memmove, byte* scratch, byte* scratch_end)
    {
        byte* src_org = src;
        int src_size, dst_size;

        if (src_end - src < 2)
            return -1; // too few bytes

        int chunk_type = (src[0] >> 4) & 0x7;
        if (chunk_type == 0)
        {
            if (src[0] >= 0x80)
            {
                // In this mode, memcopy stores the length in the bottom 12 bits.
                src_size = ((src[0] << 8) | src[1]) & 0xFFF;
                src += 2;
            }
            else
            {
                if (src_end - src < 3)
                    return -1; // too few bytes
                src_size = (src[0] << 16) | (src[1] << 8) | src[2];
                if ((src_size & ~0x3ffff) != 0)
                    return -1; // reserved bits must not be set
                src += 3;
            }
            if (src_size > output_size || src_end - src < src_size)
                return -1;
            *decoded_size = src_size;
            if (force_memmove)
                MemMove(*output, src, src_size);
            else
                *output = src;
            return (int)(src + src_size - src_org);
        }

        // In all the other modes, the initial bytes encode the src_size and the dst_size
        if (src[0] >= 0x80)
        {
            if (src_end - src < 3)
                return -1; // too few bytes

            // short mode, 10 bit sizes
            uint bits = (uint)((src[0] << 16) | (src[1] << 8) | src[2]);
            src_size = (int)(bits & 0x3ff);
            dst_size = src_size + (int)((bits >> 10) & 0x3ff) + 1;
            src += 3;
        }
        else
        {
            // long mode, 18 bit sizes
            if (src_end - src < 5)
                return -1; // too few bytes
            uint bits = (uint)((src[1] << 24) | (src[2] << 16) | (src[3] << 8) | src[4]);
            src_size = (int)(bits & 0x3ffff);
            dst_size = (int)(((bits >> 18) | ((uint)src[0] << 14)) & 0x3FFFF) + 1;
            if (src_size >= dst_size)
                return -1;
            src += 5;
        }
        if (src_end - src < src_size || dst_size > output_size)
            return -1;

        byte* dst = *output;
        if (dst == scratch)
        {
            if (scratch_end - scratch < dst_size)
                return -1;
            scratch += dst_size;
        }

        int src_used = -1;
        switch (chunk_type)
        {
            case 2:
            case 4:
                src_used = Kraken_DecodeBytes_Type12(src, src_size, dst, dst_size, chunk_type >> 1);
                break;
            case 5:
                src_used = Krak_DecodeRecursive(src, src_size, dst, dst_size, scratch, scratch_end);
                break;
            case 3:
                src_used = Krak_DecodeRLE(src, src_size, dst, dst_size, scratch, scratch_end);
                break;
            case 1:
                src_used = Krak_DecodeTans(src, src_size, dst, dst_size, scratch, scratch_end);
                break;
        }
        if (src_used != src_size)
            return -1;
        *decoded_size = dst_size;
        return (int)(src + src_size - src_org);
    }

    // ------------------------------------------------------------------ LZ

    private static void CombineScaledOffsetArrays(int* offs_stream, long offs_stream_size, int scale, byte* low_bits)
    {
        for (long i = 0; i != offs_stream_size; i++)
            offs_stream[i] = scale * offs_stream[i] - low_bits[i];
    }

    private static bool Kraken_UnpackOffsets(byte* src, byte* src_end,
                                             byte* packed_offs_stream, byte* packed_offs_stream_extra, int packed_offs_stream_size,
                                             int multi_dist_scale,
                                             byte* packed_litlen_stream, int packed_litlen_stream_size,
                                             int* offs_stream, int* len_stream,
                                             bool excess_flag, int excess_bytes)
    {
        BitReader bits_a, bits_b;
        int n, i;
        int u32_len_stream_size = 0;

        bits_a.bitpos = 24;
        bits_a.bits = 0;
        bits_a.p = src;
        bits_a.p_end = src_end;
        BitReader_Refill(&bits_a);

        bits_b.bitpos = 24;
        bits_b.bits = 0;
        bits_b.p = src_end;
        bits_b.p_end = src;
        BitReader_RefillBackwards(&bits_b);

        if (!excess_flag)
        {
            if (bits_b.bits < 0x2000)
                return false;
            n = 31 - (int)BSR(bits_b.bits);
            bits_b.bitpos += n;
            bits_b.bits <<= n;
            BitReader_RefillBackwards(&bits_b);
            n++;
            u32_len_stream_size = (int)(bits_b.bits >> (32 - n)) - 1;
            bits_b.bitpos += n;
            bits_b.bits <<= n;
            BitReader_RefillBackwards(&bits_b);
        }

        if (multi_dist_scale == 0)
        {
            // Traditional way of coding offsets
            byte* packed_offs_stream_end = packed_offs_stream + packed_offs_stream_size;
            while (packed_offs_stream != packed_offs_stream_end)
            {
                *offs_stream++ = -(int)BitReader_ReadDistance(&bits_a, *packed_offs_stream++);
                if (packed_offs_stream == packed_offs_stream_end)
                    break;
                *offs_stream++ = -(int)BitReader_ReadDistanceB(&bits_b, *packed_offs_stream++);
            }
        }
        else
        {
            // New way of coding offsets
            int* offs_stream_org = offs_stream;
            byte* packed_offs_stream_end = packed_offs_stream + packed_offs_stream_size;
            uint cmd, offs;
            while (packed_offs_stream != packed_offs_stream_end)
            {
                cmd = *packed_offs_stream++;
                if ((cmd >> 3) > 26)
                    return false;
                offs = ((8 + (cmd & 7)) << (int)(cmd >> 3)) | BitReader_ReadMoreThan24Bits(&bits_a, (int)(cmd >> 3));
                *offs_stream++ = 8 - (int)offs;
                if (packed_offs_stream == packed_offs_stream_end)
                    break;
                cmd = *packed_offs_stream++;
                if ((cmd >> 3) > 26)
                    return false;
                offs = ((8 + (cmd & 7)) << (int)(cmd >> 3)) | BitReader_ReadMoreThan24BitsB(&bits_b, (int)(cmd >> 3));
                *offs_stream++ = 8 - (int)offs;
            }
            if (multi_dist_scale != 1)
                CombineScaledOffsetArrays(offs_stream_org, offs_stream - offs_stream_org, multi_dist_scale, packed_offs_stream_extra);
        }
        uint* u32_len_stream_buf = stackalloc uint[512]; // max count is 128kb / 256 = 512
        if (u32_len_stream_size > 512)
            return false;

        uint* u32_len_stream = u32_len_stream_buf, u32_len_stream_end = u32_len_stream_buf + u32_len_stream_size;
        for (i = 0; i + 1 < u32_len_stream_size; i += 2)
        {
            if (!BitReader_ReadLength(&bits_a, &u32_len_stream[i + 0]))
                return false;
            if (!BitReader_ReadLengthB(&bits_b, &u32_len_stream[i + 1]))
                return false;
        }
        if (i < u32_len_stream_size)
        {
            if (!BitReader_ReadLength(&bits_a, &u32_len_stream[i + 0]))
                return false;
        }

        bits_a.p -= (24 - bits_a.bitpos) >> 3;
        bits_b.p += (24 - bits_b.bitpos) >> 3;

        if (bits_a.p != bits_b.p)
            return false;

        for (i = 0; i < packed_litlen_stream_size; i++)
        {
            uint v = packed_litlen_stream[i];
            if (v == 255)
                v = *u32_len_stream++ + 255;
            len_stream[i] = (int)(v + 3);
        }
        if (u32_len_stream != u32_len_stream_end)
            return false;

        return true;
    }

    private static bool Kraken_ReadLzTable(int mode, byte* src, byte* src_end, byte* dst, int dst_size, long offset,
                                           byte* scratch, byte* scratch_end, KrakenLzTable* lztable)
    {
        byte* outp;
        int decode_count, n;
        byte* packed_offs_stream;
        byte* packed_len_stream;

        if (mode > 1)
            return false;

        if (src_end - src < 13)
            return false;

        if (offset == 0)
        {
            Copy64(dst, src);
            dst += 8;
            src += 8;
        }

        if ((*src & 0x80) != 0)
        {
            byte flag = *src++;
            if ((flag & 0xc0) != 0x80)
                return false; // reserved flag set

            return false; // excess bytes not supported
        }

        // Disable no copy optimization if source and dest overlap
        bool force_copy = dst <= src_end && src <= dst + dst_size;

        // Decode lit stream, bounded by dst_size
        outp = scratch;
        n = Kraken_DecodeBytes(&outp, src, src_end, &decode_count, Min(scratch_end - scratch, dst_size), force_copy, scratch, scratch_end);
        if (n < 0)
            return false;
        src += n;
        lztable->lit_stream = outp;
        lztable->lit_stream_size = decode_count;
        scratch += decode_count;

        // Decode command stream, bounded by dst_size
        outp = scratch;
        n = Kraken_DecodeBytes(&outp, src, src_end, &decode_count, Min(scratch_end - scratch, dst_size), force_copy, scratch, scratch_end);
        if (n < 0)
            return false;
        src += n;
        lztable->cmd_stream = outp;
        lztable->cmd_stream_size = decode_count;
        scratch += decode_count;

        // Check if to decode the multistuff crap
        if (src_end - src < 3)
            return false;

        int offs_scaling = 0;
        byte* packed_offs_stream_extra = null;

        if ((src[0] & 0x80) != 0)
        {
            // uses the mode where distances are coded with 2 tables
            offs_scaling = src[0] - 127;
            src++;

            packed_offs_stream = scratch;
            n = Kraken_DecodeBytes(&packed_offs_stream, src, src_end, &lztable->offs_stream_size,
                                   Min(scratch_end - scratch, lztable->cmd_stream_size), false, scratch, scratch_end);
            if (n < 0)
                return false;
            src += n;
            scratch += lztable->offs_stream_size;

            if (offs_scaling != 1)
            {
                packed_offs_stream_extra = scratch;
                n = Kraken_DecodeBytes(&packed_offs_stream_extra, src, src_end, &decode_count,
                                       Min(scratch_end - scratch, lztable->offs_stream_size), false, scratch, scratch_end);
                if (n < 0 || decode_count != lztable->offs_stream_size)
                    return false;
                src += n;
                scratch += decode_count;
            }
        }
        else
        {
            // Decode packed offset stream, it's bounded by the command length.
            packed_offs_stream = scratch;
            n = Kraken_DecodeBytes(&packed_offs_stream, src, src_end, &lztable->offs_stream_size,
                                   Min(scratch_end - scratch, lztable->cmd_stream_size), false, scratch, scratch_end);
            if (n < 0)
                return false;
            src += n;
            scratch += lztable->offs_stream_size;
        }

        // Decode packed litlen stream. It's bounded by 1/4 of dst_size.
        packed_len_stream = scratch;
        n = Kraken_DecodeBytes(&packed_len_stream, src, src_end, &lztable->len_stream_size,
                               Min(scratch_end - scratch, dst_size >> 2), false, scratch, scratch_end);
        if (n < 0)
            return false;
        src += n;
        scratch += lztable->len_stream_size;

        // Reserve memory for final dist stream
        scratch = AlignPointer(scratch, 16);
        lztable->offs_stream = (int*)scratch;
        scratch += lztable->offs_stream_size * 4;

        // Reserve memory for final len stream
        scratch = AlignPointer(scratch, 16);
        lztable->len_stream = (int*)scratch;
        scratch += lztable->len_stream_size * 4;

        if (scratch + 64 > scratch_end)
            return false;

        return Kraken_UnpackOffsets(src, src_end, packed_offs_stream, packed_offs_stream_extra,
                                    lztable->offs_stream_size, offs_scaling,
                                    packed_len_stream, lztable->len_stream_size,
                                    lztable->offs_stream, lztable->len_stream, false, 0);
    }

    private static bool Kraken_ProcessLzRuns_Type0(KrakenLzTable* lzt, byte* dst, byte* dst_end, byte* dst_start)
    {
        byte* cmd_stream = lzt->cmd_stream, cmd_stream_end = cmd_stream + lzt->cmd_stream_size;
        int* len_stream = lzt->len_stream;
        int* len_stream_end = lzt->len_stream + lzt->len_stream_size;
        byte* lit_stream = lzt->lit_stream;
        byte* lit_stream_end = lzt->lit_stream + lzt->lit_stream_size;
        int* offs_stream = lzt->offs_stream;
        int* offs_stream_end = lzt->offs_stream + lzt->offs_stream_size;
        byte* copyfrom;
        uint final_len;
        int offset;
        int* recent_offs = stackalloc int[7];
        int last_offset;

        recent_offs[3] = -8;
        recent_offs[4] = -8;
        recent_offs[5] = -8;
        last_offset = -8;

        while (cmd_stream < cmd_stream_end)
        {
            uint f = *cmd_stream++;
            uint litlen = f & 3;
            uint offs_index = f >> 6;
            uint matchlen = (f >> 2) & 0xF;

            uint next_long_length = (uint)*len_stream;
            int* next_len_stream = len_stream + 1;

            len_stream = litlen == 3 ? next_len_stream : len_stream;
            litlen = litlen == 3 ? next_long_length : litlen;
            recent_offs[6] = *offs_stream;

            Copy64Add(dst, lit_stream, &dst[last_offset]);
            if (litlen > 8)
            {
                Copy64Add(dst + 8, lit_stream + 8, &dst[last_offset + 8]);
                if (litlen > 16)
                {
                    Copy64Add(dst + 16, lit_stream + 16, &dst[last_offset + 16]);
                    if (litlen > 24)
                    {
                        do
                        {
                            Copy64Add(dst + 24, lit_stream + 24, &dst[last_offset + 24]);
                            litlen -= 8;
                            dst += 8;
                            lit_stream += 8;
                        } while (litlen > 24);
                    }
                }
            }
            dst += litlen;
            lit_stream += litlen;

            offset = recent_offs[offs_index + 3];
            recent_offs[offs_index + 3] = recent_offs[offs_index + 2];
            recent_offs[offs_index + 2] = recent_offs[offs_index + 1];
            recent_offs[offs_index + 1] = recent_offs[offs_index + 0];
            recent_offs[3] = offset;
            last_offset = offset;

            offs_stream = (int*)((byte*)offs_stream + ((offs_index + 1) & 4));

            if ((nuint)(nint)offset < (nuint)(dst_start - dst))
                return false; // offset out of bounds

            copyfrom = dst + offset;
            if (matchlen != 15)
            {
                Copy64(dst, copyfrom);
                Copy64(dst + 8, copyfrom + 8);
                dst += matchlen + 2;
            }
            else
            {
                matchlen = 14 + (uint)*len_stream++;
                if ((nuint)matchlen > (nuint)(dst_end - dst))
                    return false; // copy length out of bounds
                Copy64(dst, copyfrom);
                Copy64(dst + 8, copyfrom + 8);
                Copy64(dst + 16, copyfrom + 16);
                do
                {
                    Copy64(dst + 24, copyfrom + 24);
                    matchlen -= 8;
                    dst += 8;
                    copyfrom += 8;
                } while (matchlen > 24);
                dst += matchlen;
            }
        }

        // check for incorrect input
        if (offs_stream != offs_stream_end || len_stream != len_stream_end)
            return false;

        final_len = (uint)(dst_end - dst);
        if (final_len != lit_stream_end - lit_stream)
            return false;

        if (final_len >= 8)
        {
            do
            {
                Copy64Add(dst, lit_stream, &dst[last_offset]);
                dst += 8; lit_stream += 8; final_len -= 8;
            } while (final_len >= 8);
        }
        if (final_len > 0)
        {
            do
            {
                *dst = (byte)(*lit_stream++ + dst[last_offset]);
                dst++;
            } while (--final_len != 0);
        }
        return true;
    }

    private static bool Kraken_ProcessLzRuns_Type1(KrakenLzTable* lzt, byte* dst, byte* dst_end, byte* dst_start)
    {
        byte* cmd_stream = lzt->cmd_stream, cmd_stream_end = cmd_stream + lzt->cmd_stream_size;
        int* len_stream = lzt->len_stream;
        int* len_stream_end = lzt->len_stream + lzt->len_stream_size;
        byte* lit_stream = lzt->lit_stream;
        byte* lit_stream_end = lzt->lit_stream + lzt->lit_stream_size;
        int* offs_stream = lzt->offs_stream;
        int* offs_stream_end = lzt->offs_stream + lzt->offs_stream_size;
        byte* copyfrom;
        uint final_len;
        int offset;
        int* recent_offs = stackalloc int[7];

        recent_offs[3] = -8;
        recent_offs[4] = -8;
        recent_offs[5] = -8;

        while (cmd_stream < cmd_stream_end)
        {
            uint f = *cmd_stream++;
            uint litlen = f & 3;
            uint offs_index = f >> 6;
            uint matchlen = (f >> 2) & 0xF;

            uint next_long_length = (uint)*len_stream;
            int* next_len_stream = len_stream + 1;

            len_stream = litlen == 3 ? next_len_stream : len_stream;
            litlen = litlen == 3 ? next_long_length : litlen;
            recent_offs[6] = *offs_stream;

            Copy64(dst, lit_stream);
            if (litlen > 8)
            {
                Copy64(dst + 8, lit_stream + 8);
                if (litlen > 16)
                {
                    Copy64(dst + 16, lit_stream + 16);
                    if (litlen > 24)
                    {
                        do
                        {
                            Copy64(dst + 24, lit_stream + 24);
                            litlen -= 8;
                            dst += 8;
                            lit_stream += 8;
                        } while (litlen > 24);
                    }
                }
            }
            dst += litlen;
            lit_stream += litlen;

            offset = recent_offs[offs_index + 3];
            recent_offs[offs_index + 3] = recent_offs[offs_index + 2];
            recent_offs[offs_index + 2] = recent_offs[offs_index + 1];
            recent_offs[offs_index + 1] = recent_offs[offs_index + 0];
            recent_offs[3] = offset;

            offs_stream = (int*)((byte*)offs_stream + ((offs_index + 1) & 4));

            if ((nuint)(nint)offset < (nuint)(dst_start - dst))
                return false; // offset out of bounds

            copyfrom = dst + offset;
            if (matchlen != 15)
            {
                Copy64(dst, copyfrom);
                Copy64(dst + 8, copyfrom + 8);
                dst += matchlen + 2;
            }
            else
            {
                matchlen = 14 + (uint)*len_stream++;
                if ((nuint)matchlen > (nuint)(dst_end - dst))
                    return false; // copy length out of bounds
                Copy64(dst, copyfrom);
                Copy64(dst + 8, copyfrom + 8);
                Copy64(dst + 16, copyfrom + 16);
                do
                {
                    Copy64(dst + 24, copyfrom + 24);
                    matchlen -= 8;
                    dst += 8;
                    copyfrom += 8;
                } while (matchlen > 24);
                dst += matchlen;
            }
        }

        // check for incorrect input
        if (offs_stream != offs_stream_end || len_stream != len_stream_end)
            return false;

        final_len = (uint)(dst_end - dst);
        if (final_len != lit_stream_end - lit_stream)
            return false;

        if (final_len >= 64)
        {
            do
            {
                Copy64Bytes(dst, lit_stream);
                dst += 64; lit_stream += 64; final_len -= 64;
            } while (final_len >= 64);
        }
        if (final_len >= 8)
        {
            do
            {
                Copy64(dst, lit_stream);
                dst += 8; lit_stream += 8; final_len -= 8;
            } while (final_len >= 8);
        }
        if (final_len > 0)
        {
            do
            {
                *dst++ = *lit_stream++;
            } while (--final_len != 0);
        }
        return true;
    }

    private static bool Kraken_ProcessLzRuns(int mode, byte* dst, int dst_size, long offset, KrakenLzTable* lztable)
    {
        byte* dst_end = dst + dst_size;
        if (mode == 1)
            return Kraken_ProcessLzRuns_Type1(lztable, dst + (offset == 0 ? 8 : 0), dst_end, dst - offset);
        if (mode == 0)
            return Kraken_ProcessLzRuns_Type0(lztable, dst + (offset == 0 ? 8 : 0), dst_end, dst - offset);
        return false;
    }

    /// <summary>Decode one 256 KiB quantum: two 128 KiB blocks compressed separately with shared history.</summary>
    private static int Kraken_DecodeQuantum(byte* dst, byte* dst_end, byte* dst_start, byte* src, byte* src_end,
                                            byte* scratch, byte* scratch_end)
    {
        byte* src_in = src;
        int mode, chunkhdr, dst_count, src_used, written_bytes;

        while (dst_end - dst != 0)
        {
            dst_count = (int)(dst_end - dst);
            if (dst_count > 0x20000) dst_count = 0x20000;
            if (src_end - src < 4)
                return -1;
            chunkhdr = src[2] | src[1] << 8 | src[0] << 16;
            if ((chunkhdr & 0x800000) == 0)
            {
                // Stored as entropy without any match copying.
                byte* outp = dst;
                src_used = Kraken_DecodeBytes(&outp, src, src_end, &written_bytes, dst_count, false, scratch, scratch_end);
                if (src_used < 0 || written_bytes != dst_count)
                    return -1;
                // The original leaves the bytes where Kraken_DecodeBytes put them; a stored (type 0) chunk
                // points |outp| into the source instead of copying, so copy it into place here.
                if (outp != dst) MemMove(dst, outp, dst_count);
            }
            else
            {
                src += 3;
                src_used = chunkhdr & 0x7FFFF;
                mode = (chunkhdr >> 19) & 0xF;
                if (src_end - src < src_used)
                    return -1;
                if (src_used < dst_count)
                {
                    long scratch_usage = Min(Min(3 * dst_count + 32 + 0xd000, 0x6C000), scratch_end - scratch);
                    KrakenLzTable lzt;
                    if (!Kraken_ReadLzTable(mode, src, src + src_used, dst, dst_count, dst - dst_start,
                                            scratch, scratch + scratch_usage, &lzt))
                        return -1;
                    if (!Kraken_ProcessLzRuns(mode, dst, dst_count, dst - dst_start, &lzt))
                        return -1;
                }
                else if (src_used > dst_count || mode != 0)
                    return -1;
                else
                    MemMove(dst, src, dst_count);
            }
            src += src_used;
            dst += dst_count;
        }
        return (int)(src - src_in);
    }

    private static void Kraken_CopyWholeMatch(byte* dst, uint offset, long length)
    {
        long i = 0;
        byte* src = dst - offset;
        if (offset >= 8)
            for (; i + 8 <= length; i += 8)
                *(ulong*)(dst + i) = *(ulong*)(src + i);
        for (; i < length; i++)
            dst[i] = src[i];
    }

    // ------------------------------------------------------------------ driver

    private struct KrakenDecoder
    {
        public int src_used, dst_used;
        public byte* scratch;
        public long scratch_size;
        public KrakenHeader hdr;
    }

    private static bool Kraken_DecodeStep(KrakenDecoder* dec, byte* dst_start, int offset, long dst_bytes_left_in,
                                          byte* src, long src_bytes_left)
    {
        byte* src_in = src;
        byte* src_end = src + src_bytes_left;
        KrakenQuantumHeader qhdr = default;
        int n;

        if ((offset & 0x3FFFF) == 0)
        {
            src = Kraken_ParseHeader(&dec->hdr, src);
            if (src == null)
                return false;
        }

        // Only Kraken is ported. Mermaid, Selkie, Leviathan, LZNA and Bitknit are refused, not guessed at.
        if (dec->hdr.decoder_type != 6)
            throw new PackException($"Oodle codec {dec->hdr.decoder_type} is not supported; only Kraken (6) is");

        int dst_bytes_left = (int)Min(0x40000, dst_bytes_left_in);

        if (dec->hdr.uncompressed)
        {
            if (src_end - src < dst_bytes_left)
            {
                dec->src_used = dec->dst_used = 0;
                return true;
            }
            MemMove(dst_start + offset, src, dst_bytes_left);
            dec->src_used = (int)(src - src_in) + dst_bytes_left;
            dec->dst_used = dst_bytes_left;
            return true;
        }

        src = Kraken_ParseQuantumHeader(&qhdr, src, dec->hdr.use_checksums);

        if (src == null || src > src_end)
            return false;

        // Too few bytes in buffer to make any progress?
        if ((nuint)(src_end - src) < qhdr.compressed_size)
        {
            dec->src_used = dec->dst_used = 0;
            return true;
        }

        if (qhdr.compressed_size > (uint)dst_bytes_left)
            return false;

        if (qhdr.compressed_size == 0)
        {
            if (qhdr.whole_match_distance != 0)
            {
                if (qhdr.whole_match_distance > (uint)offset)
                    return false;
                Kraken_CopyWholeMatch(dst_start + offset, qhdr.whole_match_distance, dst_bytes_left);
            }
            else
                MemSet(dst_start + offset, (byte)qhdr.checksum, dst_bytes_left);
            dec->src_used = (int)(src - src_in);
            dec->dst_used = dst_bytes_left;
            return true;
        }

        // ooz never implemented the quantum checksum (Kraken_GetCrc is a stub). It is skipped here; the
        // callers check every chunk against the BLAKE3 hash in the .utoc instead.

        if (qhdr.compressed_size == dst_bytes_left)
        {
            MemMove(dst_start + offset, src, dst_bytes_left);
            dec->src_used = (int)(src - src_in) + dst_bytes_left;
            dec->dst_used = dst_bytes_left;
            return true;
        }

        n = Kraken_DecodeQuantum(dst_start + offset, dst_start + offset + dst_bytes_left, dst_start,
                                 src, src + qhdr.compressed_size, dec->scratch, dec->scratch + dec->scratch_size);

        if (n != qhdr.compressed_size)
            return false;

        dec->src_used = (int)(src - src_in) + n;
        dec->dst_used = dst_bytes_left;
        return true;
    }

    private static int DecompressCore(byte* src, long src_len, byte* dst, long dst_len, byte* scratch)
    {
        KrakenDecoder dec = default;
        dec.scratch = scratch;
        dec.scratch_size = ScratchSize;
        int offset = 0;
        while (dst_len != 0)
        {
            if (!Kraken_DecodeStep(&dec, dst, offset, dst_len, src, src_len))
                return -1;
            if (dec.src_used == 0)
                return -1;
            src += dec.src_used;
            src_len -= dec.src_used;
            dst_len -= dec.dst_used;
            offset += dec.dst_used;
        }
        if (src_len != 0)
            return -1;
        return offset;
    }
}
