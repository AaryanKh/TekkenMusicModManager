using System.Buffers.Binary;
using System.Text;

namespace Tmm.Core.Wem;

public sealed record Chunk(string Id, int Offset, int Size, int PayloadOffset);

/// <summary>Read stock and custom WEM headers. Sample-exact durations come from here, never the sheet.</summary>
public static class WemReader
{
    public static List<Chunk> ParseChunks(ReadOnlySpan<byte> blob)
    {
        if (blob.Length < 12 || !blob[..4].SequenceEqual("RIFF"u8) || !blob[8..12].SequenceEqual("WAVE"u8))
            throw new WemFormatException("not a RIFF/WAVE file");
        var chunks = new List<Chunk>();
        int pos = 12;
        while (pos + 8 <= blob.Length)
        {
            var id = Encoding.ASCII.GetString(blob.Slice(pos, 4));
            var size = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(pos + 4, 4));
            if (size > int.MaxValue) throw new WemFormatException($"chunk '{id}' size out of range");
            chunks.Add(new Chunk(id, pos, (int)size, pos + 8));
            long next = (long)pos + 8 + size + (size & 1);
            if (next > int.MaxValue) break;
            pos = (int)next;
        }
        return chunks;
    }

    public static Chunk? Find(List<Chunk> chunks, string id) => chunks.FirstOrDefault(c => c.Id == id);

    /// <summary>Only the leading bytes are needed; safe to call on a partially extracted file.</summary>
    public static WemInfo ReadHeader(string path, int? wemId = null, int headBytes = 4096)
    {
        byte[] blob;
        long size;
        using (var fs = File.OpenRead(path))
        {
            size = fs.Length;
            blob = new byte[(int)Math.Min(headBytes, size)];
            int read = 0;
            while (read < blob.Length)
            {
                int n = fs.Read(blob, read, blob.Length - read);
                if (n <= 0) break;
                read += n;
            }
        }
        return ReadHeader(blob, size, wemId ?? IdFromFilename(path), Path.GetFileName(path));
    }

    public static WemInfo ReadHeader(ReadOnlySpan<byte> blob, long fileSize, int wemId, string name = "<memory>")
    {
        var chunks = ParseChunks(blob);
        var fmt = Find(chunks, "fmt ");
        var data = Find(chunks, "data");
        if (fmt is null || data is null)
            throw new WemFormatException($"{name}: missing fmt or data chunk");
        if (fmt.PayloadOffset + 16 > blob.Length)
            throw new WemFormatException($"{name}: fmt chunk truncated");

        var f = blob[fmt.PayloadOffset..];
        int tag = BinaryPrimitives.ReadUInt16LittleEndian(f);
        int ch = BinaryPrimitives.ReadUInt16LittleEndian(f[2..]);
        int rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(f[4..]);
        // avg bytes/sec at 8..12 (unused)
        int block = BinaryPrimitives.ReadUInt16LittleEndian(f[12..]);
        // bits at 14..16 (unused)

        int frames;
        if (tag == WemConstants.FormatWwiseVorbis)
        {
            if (fmt.Size < WemConstants.VorbisFramesOffset + 4 || fmt.PayloadOffset + WemConstants.VorbisFramesOffset + 4 > blob.Length)
                throw new WemFormatException($"{name}: Vorbis fmt too short to hold frame count");
            frames = (int)BinaryPrimitives.ReadUInt32LittleEndian(f[WemConstants.VorbisFramesOffset..]);
        }
        else
        {
            if (block == 0) throw new WemFormatException($"{name}: block_align is 0");
            frames = data.Size / block;
        }

        return new WemInfo(wemId, frames, rate, ch, tag, fileSize);
    }

    /// <summary>"123456.wem" or "123456_anything.wem" -> 123456.</summary>
    public static int IdFromFilename(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        var head = stem.Split('_')[0];
        if (!int.TryParse(head, out var id))
            throw new WemFormatException($"{Path.GetFileName(path)}: cannot derive WEM id from filename");
        return id;
    }
}
