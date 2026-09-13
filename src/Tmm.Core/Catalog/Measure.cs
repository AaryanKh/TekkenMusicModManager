using Tmm.Core.Wem;

namespace Tmm.Core.Catalog;

/// <summary>Turn extracted stock WEMs into measured Slots (sample-exact frames, rate, channels).</summary>
public static class Measure
{
    public static Slot MeasureSlot(SlotIdentity identity, IReadOnlyDictionary<int, string> wemPaths, bool measureLufs = false)
    {
        var loop = WemReader.ReadHeader(wemPaths[identity.LoopId], identity.LoopId);
        WemInfo? intro = null;
        if (identity.IntroId is int iid)
            intro = WemReader.ReadHeader(wemPaths[iid], iid);
        double? lufs = measureLufs ? MeasureLufs(wemPaths[identity.LoopId]) : null;
        return new Slot(identity, intro, loop, lufs, Measured: true);
    }

    /// <summary>Decode with vgmstream, integrate LUFS. Slow; meant for a sample of ~30 slots to derive
    /// the global loudness target, not for all 443. Needs a Wwise-Vorbis decoder we do not ship.</summary>
    private static double MeasureLufs(string wemPath)
        => throw new NotImplementedException("Stock-loop LUFS measurement needs vgmstream; not wired up.");
}
