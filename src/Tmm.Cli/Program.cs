using System.Globalization;
using Tmm.Core;
using Tmm.Core.Analysis;
using Tmm.Core.Catalog;
using Tmm.Core.LoopFit;
using Tmm.Core.Mods;
using Tmm.Core.Pak;
using Tmm.Core.Render;
using Tmm.Core.Steam;
using Tmm.Core.Wem;

// Phase-1 command line. Exercises the full pipeline without the UI so the core can be validated
// in-game before a single widget exists.
//
//   tmm catalog build --sheet data/jukebox_slots.csv --wems <folder>
//   tmm catalog status
//   tmm analyze <song> [--top 20] [--cap 0.06]
//   tmm build <song> --slot <loop_id> --name MySong [--bars N] [--start S] [--gain dB] [--intro-start S] [--no-pack]
//   tmm mods list | enable <id> | disable <id> | rebuild <id> | delete <id>
//   tmm wem dump <file.wem>
//   tmm find-game
//   (global) --app-dir <dir>

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
return Cli.Run(args);

static class Cli
{
    public static int Run(string[] argv)
    {
        var args = argv.ToList();
        string? appDir = Take(args, "--app-dir");
        var settings = SettingsStore.Load(appDir);
        if (args.Count == 0) { Usage(); return 2; }

        try
        {
            return args[0] switch
            {
                "catalog" => Catalog(args.Skip(1).ToList(), settings),
                "analyze" => Analyze(args.Skip(1).ToList(), settings),
                "build" => Build(args.Skip(1).ToList(), settings),
                "mods" => Mods(args.Skip(1).ToList(), settings),
                "wem" => Wem(args.Skip(1).ToList()),
                "find-game" => FindGame(),
                _ => Fail($"unknown command '{args[0]}'"),
            };
        }
        catch (TmmException e)
        {
            Console.Error.WriteLine($"error: {e.Message}");
            return 1;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or NotImplementedException)
        {
            Console.Error.WriteLine($"error: {e.GetType().Name}: {e.Message}");
            return 1;
        }
    }

    static void Usage() => Console.WriteLine(
        "tmm catalog build --sheet <csv> --wems <folder> [--strict]\n" +
        "tmm catalog status\n" +
        "tmm analyze <song> [--top N] [--cap 0.06]\n" +
        "tmm build <song> --slot <loop_id> --name <name> [--bars N] [--start S] [--gain dB] [--intro-start S] [--no-pack]\n" +
        "tmm mods list | scan [--adopt] | enable <id> | disable <id> | rebuild <id> | delete <id>\n" +
        "tmm wem dump <file.wem>\n" +
        "tmm find-game\n" +
        "global: --app-dir <dir>");

    static int Fail(string msg) { Console.Error.WriteLine("error: " + msg); Usage(); return 2; }

    static string? Take(List<string> args, string flag)
    {
        int i = args.IndexOf(flag);
        if (i < 0 || i + 1 >= args.Count) return null;
        var v = args[i + 1]; args.RemoveRange(i, 2); return v;
    }

    static bool Flag(List<string> args, string flag) => args.Remove(flag);

    static string? TrySheetPath(string? given)
    {
        if (given is not null) return given;
        foreach (var c in new[] { Path.Combine(AppContext.BaseDirectory, "data", "jukebox_slots.csv"), Path.Combine("data", "jukebox_slots.csv") })
            if (File.Exists(c)) return c;
        return null;
    }

    static string SheetPath(string? given) =>
        TrySheetPath(given) ?? throw new TmmException("no --sheet given and data/jukebox_slots.csv not found");

    // ------------------------------------------------------------------ commands

    static int Catalog(List<string> args, Settings s)
    {
        var store = new CatalogStore(s.CatalogPath);
        if (args.Count > 0 && args[0] == "status")
        {
            Console.WriteLine(store switch
            {
                { IsBundled: true } => $"catalog: {store.Count} slots, shipped with the app ({CatalogStore.BundledPath()}). Nothing to extract; rebuild only after a game patch.",
                { IsBuilt: true } => $"catalog: {store.Count} slots, built {store.BuiltAt:u} at {store.Path}",
                _ => $"catalog: not built ({store.Path})",
            });
            return 0;
        }
        if (args.Count == 0 || args[0] != "build") return Fail("catalog build|status");
        var sheet = SheetPath(Take(args, "--sheet"));
        var wems = Take(args, "--wems") ?? s.WemSourceFolder ?? throw new TmmException("--wems <folder of extracted stock .wem files> is required");
        bool strict = Flag(args, "--strict");
        var progress = new Progress<(int done, int total, string what)>(p => Console.Write($"\r  {p.done}/{p.total} {p.what,-60}"));
        var r = CatalogBuilder.Build(sheet, new PreExtractedFolderExtractor(wems, allowMissing: !strict), s.ScratchDir, store, progress);
        Console.WriteLine($"\ncatalog built: {r.Measured} of {r.Total} slots -> {store.Path}");
        if (r.Skipped.Count > 0)
        {
            Console.WriteLine($"{r.Skipped.Count} slot(s) skipped — their stock WEMs are not in {wems}.");
            Console.WriteLine("Season 2 and collab tracks live outside pakchunk0; export those chunks too and re-run to add them.");
            foreach (var id in r.Skipped) Console.WriteLine($"  #{id.No,-4} {id.Title}");
        }
        return 0;
    }

    static int Analyze(List<string> args, Settings s)
    {
        int top = int.Parse(Take(args, "--top") ?? "20");
        double cap = double.Parse(Take(args, "--cap") ?? s.StretchCap.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        var sheet = TrySheetPath(Take(args, "--sheet"));
        if (args.Count == 0) return Fail("analyze <song>");
        var song = args[0];

        var (slots, provisional) = CatalogBuilder.SlotsForRanking(new CatalogStore(s.CatalogPath), sheet);
        if (provisional) Console.WriteLine("note: catalog not built; ranking against spreadsheet whole-second lengths (provisional).");

        var analyzed = new SongAnalyzer(s.FfmpegExe).Analyze(song, new Progress<(int, int, string)>(p => Console.Error.WriteLine($"  {p.Item3}")));
        var g = analyzed.Analysis.Grid;
        Console.WriteLine($"{analyzed.Song.Title}: {analyzed.Song.DurationSec:0.0}s, {g.Bpm:0.0} BPM (confidence {g.Confidence:0.00}), {g.DownbeatsSec.Length} bars, {analyzed.Candidates.Count} loop candidates, {analyzed.Analysis.Segments.Count} sections");
        if (analyzed.LowConfidence) Console.WriteLine("warning: low beat confidence; the manual loop editor is the safer path for this track.");

        var w = new Weights(StretchCap: cap);
        var ranked = Ranking.Rank(slots, analyzed.Candidates, analyzed.Song.DurationSec, w);
        var byKey = slots.ToDictionary(x => x.Key);
        Console.WriteLine($"\n{"%",5}  {"loop_id",-10} {"title",-52} explain");
        foreach (var r in ranked.Take(top))
            Console.WriteLine($"{r.Headline,5:0.0}  {r.SlotKey,-10} {Trunc(byKey[r.SlotKey].Title, 52),-52} {r.Explain()}");
        if (ranked.Count == 0) Console.WriteLine("no slot fits inside the stretch cap; raise --cap or use the editor.");
        return 0;
    }

    static int Build(List<string> args, Settings s)
    {
        int slotKey = int.Parse(Take(args, "--slot") ?? throw new TmmException("--slot <loop_id> is required"));
        string name = Take(args, "--name") ?? throw new TmmException("--name is required");
        int? bars = Take(args, "--bars") is string b ? int.Parse(b) : null;
        double? start = Take(args, "--start") is string st ? double.Parse(st, CultureInfo.InvariantCulture) : null;
        bool noPack = Flag(args, "--no-pack");
        double? gainDb = Take(args, "--gain") is string g ? double.Parse(g, CultureInfo.InvariantCulture) : null;
        double? introStart = Take(args, "--intro-start") is string istr ? double.Parse(istr, CultureInfo.InvariantCulture) : null;
        if (args.Count == 0) return Fail("build <song> --slot <loop_id> --name <name>");
        var songPath = args[0];

        var store = new CatalogStore(s.CatalogPath);
        var slot = store.Get(slotKey) ?? throw new TmmException($"slot {slotKey} not in the catalog (is it built?)");
        var analyzed = new SongAnalyzer(s.FfmpegExe).Analyze(songPath);
        var w = Weights.FromSettings(s);

        RenderPlan plan;
        if (bars is null && start is null)
        {
            var score = Scoring.ScoreSlot(slot, analyzed.Candidates, analyzed.Song.DurationSec, w)
                        ?? throw new NoFitFoundException($"no candidate fits slot {slotKey} within ±{w.StretchCap:P0}; pass --bars/--start");
            plan = PlanFactory.FromScore(slot, score, analyzed.Analysis, s.TargetLufs);
            Console.WriteLine($"auto plan: {score.Explain()}");
        }
        else
        {
            var grid = analyzed.Analysis.Grid;
            double st0 = grid.SnapToDownbeat(start ?? grid.DownbeatsSec.FirstOrDefault());
            int nb = bars ?? Math.Max(Constants.MinBars, (int)Math.Round(slot.LoopSeconds / grid.BarSec));
            var (strategy, trim) = PlanFactory.ChooseIntro(slot, st0);
            plan = new RenderPlan
            {
                SlotKey = slot.Key, SongFingerprint = analyzed.Song.Fingerprint, LoopStartSec = st0, LoopBars = nb,
                Rho = PlanFactory.RhoFor(slot, nb, grid.BarSec), IntroStrategy = strategy, IntroTrimSec = trim,
                TargetLufs = s.TargetLufs, ManualOverrides = { ["cli"] = "true" },
            };
            Console.WriteLine($"manual plan: start {st0:0.00}s, {nb} bars, rho {plan.Rho:0.0000}");
        }

        if (gainDb is double gd)
        {
            plan.GainDb = gd;
            plan.ManualOverrides["gain_db"] = gd.ToString("0.0", CultureInfo.InvariantCulture);
            Console.WriteLine($"volume trim: {gd:+0.0;-0.0} dB (peak limiter keeps it under the ceiling)");
        }
        if (introStart is double isec)
        {
            if (!slot.HasIntro) throw new TmmException($"slot {slotKey} has no intro WEM, so --intro-start has nothing to fill");
            plan.IntroStrategy = IntroStrategy.Detached;
            plan.IntroStartSec = isec;
            plan.ManualOverrides["intro_start"] = isec.ToString("0.###", CultureInfo.InvariantCulture);
            Console.WriteLine($"detached intro: cut from {isec:0.00}s, independent of the loop start");
        }

        var stretcher = StretcherFactory.FromSettings(s);
        if (noPack)
        {
            var outDir = Path.Combine(s.ScratchDir, "render", PakLayout.SanitizeModName(name));
            var r = RenderPipeline.Render(analyzed.Pcm, slot, plan, outDir, stretcher);
            Console.WriteLine($"rendered ({stretcher.Name}): loop {r.LoopWem} ({r.LoopFrames} frames){(r.IntroWem is null ? "" : $", intro {r.IntroWem} ({r.IntroFrames} frames)")}, seam {r.SeamMetric:0.000}, peak {r.PeakDbfs:0.0} dBFS, {r.Lufs:0.0} LUFS{(r.LimiterReductionDb > 0.05 ? $", limiter -{r.LimiterReductionDb:0.0} dB" : "")}");
            return 0;
        }
        var reg = new ModRegistry(s);
        var builder = new ModBuilder(reg, stretcher, PackerFactory.FromSettings(s));
        var m = builder.Build(name, songPath, analyzed.Pcm, slot, plan, new Progress<(int, int, string)>(p => Console.Error.WriteLine($"  {p.Item3}")));
        Console.WriteLine($"built mod {m.ModId} ({m.Name}) -> {reg.StorePak(m)}. Enable with: tmm mods enable {m.ModId}");
        return 0;
    }

    static int Mods(List<string> args, Settings s)
    {
        var reg = new ModRegistry(s);
        if (args.Count == 0) return Fail("mods list|scan|enable|disable|rebuild|delete");
        if (args[0] == "list")
        {
            var all = reg.All();
            if (all.Count == 0) { Console.WriteLine("no mods built yet"); return 0; }
            foreach (var m in all)
                Console.WriteLine($"{m.ModId}  {reg.StateOf(m),-8}  {m.Name,-24}  slot {m.SlotKey} ({Trunc(m.SlotTitle, 40)})  {m.Updated:u}");
            var tp = Conflicts.ScanThirdParty(reg, s.GameModsDir);
            foreach (var t in tp) Console.WriteLine($"third-party: {Path.GetFileName(t.Path)} overrides {t.WemIds.Count} WEM(s)");
            return 0;
        }
        if (args[0] == "scan")
        {
            bool adopt = Flag(args, "--adopt");
            var renamed = Reconcile.FindRenamed(reg, s.GameModsDir);
            if (renamed.Count == 0)
            {
                Console.WriteLine("no renamed paks found; every mod matches the name its manifest records");
                foreach (var line in Reconcile.SyncNames(reg)) Console.WriteLine("  " + line);
            }
            else
            {
                Console.WriteLine($"{renamed.Count} pak(s) in ~mods look like renamed copies of your mods:");
                foreach (var r in renamed)
                    Console.WriteLine($"  {r.Mod.Name}: '{r.ExpectedName}' -> '{r.FoundName}'  ({r.Evidence})");
                if (adopt)
                    foreach (var line in Reconcile.Adopt(reg, renamed)) Console.WriteLine("  " + line);
                else
                    Console.WriteLine("re-run with --adopt to point the manifests at the new names");
            }
            var tp = Conflicts.ScanThirdParty(reg, s.GameModsDir);
            foreach (var t in tp) Console.WriteLine($"third-party: {Path.GetFileName(t.Path)} overrides {t.WemIds.Count} WEM(s)");
            return 0;
        }
        if (args.Count < 2) return Fail($"mods {args[0]} <mod_id>");
        var mod = reg.Get(args[1]);
        switch (args[0])
        {
            case "enable": Installer.Enable(mod, reg, force: Flag(args, "--force")); Console.WriteLine($"enabled -> {reg.InstalledPak(mod)}"); return 0;
            case "disable": Installer.Disable(mod, reg); Console.WriteLine("disabled"); return 0;
            case "delete": Installer.Delete(mod, reg); Console.WriteLine("deleted"); return 0;
            case "rebuild":
            {
                var slot = new CatalogStore(s.CatalogPath).Get(mod.SlotKey) ?? throw new CatalogNotBuiltException($"slot {mod.SlotKey} not in catalog");
                var builder = new ModBuilder(reg, StretcherFactory.FromSettings(s), PackerFactory.FromSettings(s));
                builder.Rebuild(mod, slot, s.FfmpegExe, new Progress<(int, int, string)>(p => Console.Error.WriteLine($"  {p.Item3}")));
                Console.WriteLine($"rebuilt; state {reg.StateOf(mod)}");
                return 0;
            }
            default: return Fail($"unknown mods verb '{args[0]}'");
        }
    }

    static int Wem(List<string> args)
    {
        if (args.Count < 2 || args[0] != "dump") return Fail("wem dump <file.wem>");
        var path = args[1];
        var blob = File.ReadAllBytes(path);
        foreach (var c in WemReader.ParseChunks(blob))
            Console.WriteLine($"  {c.Id,-5} @ {c.Offset,8}  size {c.Size,10}  payload @ {c.PayloadOffset}");
        var info = WemReader.ReadHeader(path);
        Console.WriteLine($"id {info.WemId}  tag 0x{info.FormatTag:X4}  {info.Channels} ch  {info.SampleRate} Hz  {info.Frames} frames = {info.Seconds:0.000}s  {info.SizeBytes} bytes");
        return 0;
    }

    static int FindGame()
    {
        var root = SteamLocator.FindGameRoot();
        Console.WriteLine(root ?? "TEKKEN 8 not found in any Steam library");
        return root is null ? 1 : 0;
    }

    static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
}
