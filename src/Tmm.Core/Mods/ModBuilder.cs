using Tmm.Core.Analysis;
using Tmm.Core.Audio;
using Tmm.Core.Pak;
using Tmm.Core.Render;

namespace Tmm.Core.Mods;

/// <summary>
/// Create a brand-new mod from (song, slot, plan): render -&gt; stage -&gt; pack -&gt; manifest.
/// Does NOT enable it; the dashboard does that so conflict checks run in one place.
/// Rebuild replays a manifest's plan end-to-end through the same code.
/// </summary>
public sealed class ModBuilder
{
    private readonly ModRegistry _reg;
    private readonly IStretcher _stretcher;
    private readonly IPacker _packer;

    public ModBuilder(ModRegistry reg, IStretcher stretcher, IPacker packer)
    {
        _reg = reg; _stretcher = stretcher; _packer = packer;
    }

    public ModManifest Build(string name, string songPath, PcmBuffer pcm, Slot slot, RenderPlan plan,
                             IProgress<(int done, int total, string what)>? progress = null)
    {
        var safe = PakLayout.SanitizeModName(name);
        var m = new ModManifest
        {
            Name = safe,
            SlotKey = slot.Key,
            SlotTitle = slot.Title,
            SongPath = Path.GetFullPath(songPath),
            SongFingerprint = plan.SongFingerprint,
            Plan = plan.Clone(),
            PakName = PakLayout.PakFilename(safe),
            WemIds = slot.WemIds.ToList(),
        };
        RenderAndPack(m, pcm, slot, progress);
        return m;
    }

    /// <summary>Replay a manifest's RenderPlan: decode -&gt; render -&gt; WEMs -&gt; pak -&gt; (re)enable.
    /// Used by the dashboard's Rebuild button and after a game patch invalidates the catalog.</summary>
    public ModManifest Rebuild(ModManifest m, Slot slot, string ffmpeg,
                               IProgress<(int done, int total, string what)>? progress = null)
    {
        if (!File.Exists(m.SongPath))
            throw new InstallException($"'{m.Name}': source song no longer exists at {m.SongPath}");
        progress?.Report((0, 4, "Decoding song"));
        var (_, pcm) = Decoder.Decode(m.SongPath, ffmpeg);
        bool wasEnabled = _reg.IsEnabled(m);
        RenderAndPack(m, pcm, slot, progress);
        if (wasEnabled) Installer.Enable(m, _reg, force: true);   // same WEM IDs as before; conflicts unchanged
        return m;
    }

    private void RenderAndPack(ModManifest m, PcmBuffer pcm, Slot slot, IProgress<(int, int, string)>? progress)
    {
        progress?.Report((1, 4, "Rendering"));
        var wemDir = _reg.WemDir(m);
        FileOps.DeleteDirectory(wemDir);
        var result = RenderPipeline.Render(pcm, slot, m.Plan, wemDir, _stretcher);

        progress?.Report((2, 4, "Staging"));
        var wems = new Dictionary<int, string> { [slot.Loop.WemId] = result.LoopWem };
        if (result.IntroWem is not null && slot.Intro is not null) wems[slot.Intro.WemId] = result.IntroWem;
        // Sanitised: a display name adopted from a renamed pak can carry characters the mod was never
        // built with. The folder is scratch only — the pak's real name comes from m.PakName below.
        var staged = PakLayout.Stage(PakLayout.SanitizeModName(m.Name), wems, Path.Combine(_reg.Settings.ScratchDir, m.ModId));

        progress?.Report((3, 4, $"Packing with {_packer.Name}"));
        _packer.Pack(staged, _reg.StorePak(m));

        m.SeamMetric = result.SeamMetric;
        m.Lufs = result.Lufs;
        m.Updated = DateTime.UtcNow;
        _reg.Save(m);
        // Manifest is written after the pak, so Updated <= pak mtime + epsilon and the state reads
        // Disabled/Enabled, not Stale. Touch the pak to make that unambiguous on coarse filesystems.
        File.SetLastWriteTimeUtc(_reg.StorePak(m), DateTime.UtcNow);
        progress?.Report((4, 4, "Done"));
    }
}
