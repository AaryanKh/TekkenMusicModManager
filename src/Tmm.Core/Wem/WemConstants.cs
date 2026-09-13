namespace Tmm.Core.Wem;

/// <summary>Byte-level facts about Tekken 8 WEMs. Every value traces to a Spike B test.</summary>
public static class WemConstants
{
    public const int FormatPcm = 0x0001;
    /// <summary>REQUIRED for PCM output (Test 4b silent vs 4c plays).</summary>
    public const int FormatExtensible = 0xFFFE;
    /// <summary>What stock files use; we only read these.</summary>
    public const int FormatWwiseVorbis = 0xFFFF;

    // Corpus header layout (22 mods / 44 files, 44/44 agreement):
    //   RIFF(12) + fmt(8+24) + hash(8+16) + smpl(8+60) + data(8) = 144 bytes
    public const int FmtPayloadLen = 24;      // WAVEFORMATEX(16) + cbSize(2) + validBits(2) + channelMask(4)
    public const int HashPayloadLen = 16;     // Wwise cache GUID. Inert (Tests 2+3). Any 16 bytes work.
    public const int SmplPayloadLen = 60;     // 36-byte header + one 24-byte loop record
    public const int CorpusHeaderLen = 144;
    public const int MinimalHeaderLen = 52;   // RIFF + fmt only (Test 4c). Works, but we ship the corpus layout.

    /// <summary>Where the stock Vorbis fmt extension stores total sample count (length audit, 8/8 slots).</summary>
    public const int VorbisFramesOffset = 24;

    public const uint ChannelMaskStereo = 0x3;
    public const uint ChannelMaskMono = 0x4;

    // smpl quirk copied from the corpus: samplePeriod holds the sample rate, not nanoseconds.
    // The runtime doesn't care (Test 7 showed smpl is inert), but matching 44 known-good files
    // removes a class of question from every future failure. Keep it.
    public const bool SmplSamplePeriodIsRate = true;
}
