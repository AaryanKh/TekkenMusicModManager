# Tekken Music Mod Manager (.NET)

Drag a song in, get a Tekken 8 jukebox mod out. A Windows desktop app (WPF, .NET 8) plus the engine
behind it, ported from the Python scaffold described in `ARCHITECTURE.md` of the original repo.

```
TekkenMusicModManager.sln
├── src/Tmm.Core        engine: WEM I/O, catalog, analysis, loop fitting, render, pak, mods (no UI, no NuGet)
├── src/Tmm.App         WPF GUI (net8.0-windows): Create wizard, Dashboard, Settings
├── src/Tmm.Cli         `tmm` command line — the full pipeline without the GUI
├── tests/Tmm.Core.Tests  xunit: WEM round-trip, scoring, registry state, analyzer on synthetic audio
├── data/jukebox_slots.csv  the community sheet, normalized (IDs + titles + whole-second lengths)
└── data/stock_catalog.json the measured slot catalog that ships with the app (432 slots)
```

## Install

Grab the portable zip from [Releases](https://github.com/AaryanKh/TekkenMusicModManager/releases) and
unzip it somewhere you can write to — your Desktop or Documents, **not** inside `Program Files`. Run
`TekkenMusicModManager.exe`. There is no installer to run and no .NET to install; the runtime is inside
the exe, which is why the first launch takes a second or two longer than later ones.

Windows SmartScreen will say "Windows protected your PC" because the exe is not code-signed. Click
**More info → Run anyway**.

Everything the app saves stays in `UserData\` next to the exe, so moving or backing up that one folder
takes your settings and every mod with it.

### Two tools you have to supply

Neither is bundled, for licensing reasons. The zip ships empty `tools\ffmpeg\` and `tools\UnrealPak\`
folders to drop them into, and pointing Settings at them is a one-time step.

**ffmpeg** — decodes whatever you feed the app (MP3, FLAC, WAV, M4A, OGG). Easiest route is
**Settings → External tools → Install with winget**, which does it for you. Otherwise run
`winget install Gyan.FFmpeg` yourself, or download a build from
[gyan.dev](https://www.gyan.dev/ffmpeg/builds/), put `ffmpeg.exe` in `tools\ffmpeg\`, and Browse to it
in Settings.

**UnrealPak.exe** — packs the finished mod into the `<name>_P.pak` the game loads. This is the community
build that comes with the usual Tekken 8 modding tutorials, normally a small bundle of `UnrealPak.exe`
plus its DLLs and a few `.bat` files. It is not on winget and there is no official download, so use the
one from whichever tutorial you followed. Put the whole bundle in `tools\UnrealPak\` and Browse to
`UnrealPak.exe` in Settings. Keep the DLLs next to it; it will not run without them.

### Everything else is automatic

| | |
|---|---|
| **Tekken 8 folder** | Auto-detected through Steam's `libraryfolders.vdf`. Settings → **Auto-detect (Steam)** if it guessed wrong. |
| **Slot catalog** | Ships with the app — 432 measured slots. Nothing to extract, **no FModel**. See [The shipped catalog](#the-shipped-catalog). |
| **.NET** | Built into the exe. |
| **Wwise** | Not used at all (Spike B). |

Optional: set a `rubberband.exe` path in Settings for pitch-preserving time-stretch. Without it the
built-in resampler stretches and shifts pitch by the same ratio, up to about a semitone at the 6 % cap.

### Check it took

The Dashboard shows one status line. When it reads **Ready.** you can build a mod. Until then it names
what is still missing:

```
Not set up yet: ffmpeg, UnrealPak.exe — see Settings.
```

From a terminal, `tmm.exe find-game` and `tmm.exe catalog status` answer the same questions.

### Installer instead of the zip

`installer\build-wix.ps1` and `installer\build-installer.ps1` produce a setup exe if you would rather
install than unzip. The Inno wizard adds pages the zip has no equivalent for: detected ffmpeg and
UnrealPak paths with Browse, an offer to install ffmpeg with winget, and the Tekken 8 folder. It writes
`install-hints.json`, which the app folds into its settings on first start. See
[Distribution builds](#distribution-builds).

## Build from source

Requires the .NET 8 SDK (`winget install Microsoft.DotNet.SDK.8`). Visual Studio 2022 17.8+ or Rider both
open the solution; from a terminal:

```
dotnet build TekkenMusicModManager.sln
dotnet test  TekkenMusicModManager.sln
dotnet run --project src/Tmm.App
dotnet run --project src/Tmm.Cli -- --help
```

`Tmm.Core`, `Tmm.Cli` and the tests are plain `net8.0`. Only `Tmm.App` needs Windows (WPF).

Building from source still needs ffmpeg and UnrealPak at runtime — see [Install](#install).

## The shipped catalog

`data\stock_catalog.json` holds the measured facts for 432 jukebox slots: WEM id, exact frame count,
sample rate and channel count, read from the stock WEM headers. It is bundled into every build, so a
fresh install can rank and render immediately with nothing extracted.

The renderer needs the *exact* frame count of the WEM it replaces, and the spreadsheet only carries
whole seconds — off by up to half a second, which is why sheet-derived slots are marked provisional and
refused. Measuring those numbers once and shipping them is what removes the FModel step for everyone
else; the file is integers and titles, no game audio.

`CatalogStore` prefers a catalog the user built themselves and falls back to the shipped one, so
Settings → Build catalog still overrides it — the reason to do that is a game patch that changes a
track's length. The 11 Season 2 and collab slots absent from `pakchunk0` are not in the shipped file;
export the other pakchunks and rebuild to add them.

## Flow

1. **Settings** — set the game folder (Auto-detect) and the UnrealPak path. The slot catalog is
   already there: 432 measured slots ship with the app, so there is nothing to extract. Rebuilding it
   from your own install is only needed after a game patch changes a track's length. A rebuild stores
   *measured* frame counts from each stock WEM header (Vorbis fmt extension @24); slots whose WEMs are
   absent from the export are skipped and listed rather than failing the build — Season 2 and collab
   tracks live outside `pakchunk0`, so a pakchunk0-only export yields 432 of 443. `--strict` restores
   hard failure.
2. **Create → Import** — drop a song. It is decoded to 48 kHz stereo, beat-tracked, and every
   `(downbeat, bar count)` loop it can naturally produce is generated once.
3. **Create → Pick a slot** — all 443 slots ranked by compatibility %. The stretch-cap slider re-ranks
   live (sorted lookup over the candidates, no re-analysis). Every row shows the four components.
4. **Create → Edit** — waveform with intro/loop overlay. Drag the loop start (snaps to bars), change bar
   count, intro strategy, crossfade, loudness, volume trim. **Preview loop ×3** plays the loop back to
   back so the wrap is audible; **Preview full track** assembles the intro plus repeated loops exactly
   as the game plays them and draws the result, which is the only way to hear the intro-to-loop
   handover. Build renders both WEMs sample-exact, packs, and stores the mod.
5. **Dashboard** — Enable copies the pak into `Polaris\Content\Paks\~mods` (never `Mods`), Disable
   removes it, Rebuild replays the stored plan, Edit reopens it. Conflicts with other enabled mods and
   with third-party paks already in `~mods` are reported before install.

State on the dashboard is derived from the filesystem every time; nothing is cached. Each mod's row
shows the loudness of its last render and any volume trim, so two mods that sit at different levels in
game can be compared without rebuilding either.

## Tile view and album art

The dashboard has a second layout, toggled at the bottom right and remembered across launches. Each
mod is a tile showing the song's album art, with a dot that is green when enabled and grey otherwise.
Hovering or selecting a tile lifts the art on a tilted plane and slides the Tekken game's card out from
behind it; clicking anywhere that is not a tile, or pressing Escape, clears the selection. The selected
tile's details and every table action sit beneath the grid.

**Where the art comes from.** Song art is pulled from the file itself with ffmpeg and cached beside
the manifest. A file with no picture shows a question mark. In the tile view's action panel:

- **Find art online…** reads the song's tags with `ffmpeg -f ffmetadata` (no `ffprobe` needed), asks
  the iTunes Search API, and shows the results next to the picture embedded in the file, if any. You
  pick; nothing is applied on its own. This is an optional network call using .NET's built-in HTTP
  client — it adds no installation dependency.
- **Remove art** goes back to the placeholder and is remembered.

**Tekken covers.** The game's own jukebox artwork is not shipped and not fetched, since it is Bandai
Namco's. The card behind each tile is drawn at runtime from the game's name. To use the real covers,
export them from your own copy of the game with FModel (search for `jukebox`), then press **Import
Tekken covers…** and point at the folder. Filenames are matched to games loosely — `Tekken7.png`,
`tk7.png`, `Tekken Tag 2.png` and `TTT2.jpg` all land in the right place — and the pictures are copied
into `data\covers\` next to the exe, one per tag. Anything already there is used in preference to the
drawn card.

## Levels

Three separate stages decide how loud a mod ends up, in this order:

| Stage | Control | What it does |
|---|---|---|
| Loudness match | "Match to &lt;N&gt; LUFS" | Measures integrated loudness (BS.1770-4) and moves the render toward the target. Off by default. |
| Volume trim | `RenderPlan.GainDb`, the slider, `--gain` | Flat ±dB on top, applied to the intro and the loop equally so the handover does not step. |
| Peak limiter | always on | Look-ahead limiter that keeps the result under −1 dBFS. |

The limiter runs on every render, not only when a trim was asked for. Without it anything above full
scale is hard-clipped by the int16 conversion, and loud masters do reach that on their own once
resampling overshoot is added.

How much of a trim survives depends entirely on the source's headroom:

- A **quiet** source has room, so the trim arrives intact. A track at −25.5 LUFS peaking at −17.6 dBFS
  takes `--gain 12` and lands at −13.5 LUFS with no limiting at all. This is the case worth using.
- A source **already mastered near full scale** has no room, so the limiter takes the gain straight
  back off. A track at −8.1 LUFS peaking over 0 dBFS takes `--gain 3` and gains under 1 LU while the
  limiter works 4 dB. The editor says so after a preview rather than letting it look like nothing
  happened.

So the fix for one mod being quieter than another is to raise the quiet one, not to push the loud one
further.

## What is ported from the Python scaffold, and what changed

Everything the scaffold had implemented is ported 1:1 with its tests: `wem.reader`/`wem.writer`
(144-byte corpus header, `0xFFFE` extensible tag, hard length gate), `loopfit.scoring`/`ranking`
(geometric mean, bisect window), `render.plan`/`intro`/`verify`, `pak.layout`, `catalog.sheet`,
`config`, `core.models`.

Everything the scaffold had stubbed is implemented here: catalog build + store, mod manifest/registry/
installer/conflicts/build/rebuild, UnrealPak + repak wrappers, seams (equal-power wrap crossfade using
the pre-roll before the loop head), BS.1770 loudness (K-weighting, gating), a CUE4Parse-free pak scanner
for third-party conflict detection, Steam auto-detect, and the full UI.

Deliberate deviations, so they don't get relitigated by accident:

- **Analysis is a baseline C# implementation, not librosa.** Spectral-flux onsets, autocorrelation
  tempo with a log-normal prior and a joint period/phase refinement, fixed 4/4 grid, bass-weighted
  downbeat choice, chroma+MFCC seam distance, checkerboard structure segmentation. It recovers the tempo
  of synthetic click tracks to <0.5 % (tests) but is untested on real music; constant tempo only. It sits
  behind `IBeatTracker`/`ISongAnalyzer` so a better tracker (or a Python/librosa sidecar) can replace it
  without touching the UI. `BeatGrid.Confidence < 0.5` routes the user to the manual editor.
- **Time-stretch:** `RubberBandCliStretcher` wraps the rubberband CLI when configured; otherwise
  `ResampleStretcher` (Hermite resampling) is used, which shifts pitch. Both are behind `IStretcher`.
- **Loudness normalization is a peak-safe gain.** If hitting the target would push the sample peak
  above −1 dBFS the gain is capped and the achieved LUFS is reported. A look-ahead peak limiter then
  runs on every render as a backstop, so nothing reaches the int16 conversion above the ceiling. See
  [Levels](#levels).
- **Catalog is a JSON file, not SQLite.** 443 rows, read once at startup, zero native dependencies.
- **Provisional slots.** Before the catalog is built, ranking runs against the sheet's whole-second
  lengths so the UI is not empty. Rendering against a provisional slot throws — the exact-length rule
  is never applied to transcribed numbers.
- **`Slot.Measured`** and the extra manifest fields (`SeamMetric`, `Lufs`) are additions to the models.
- **Settings.json** lives in `%LOCALAPPDATA%\TekkenMusicModManager` alongside `catalog.json`,
  `mods\<id>\` (manifest + WEMs + pak) and scratch. Pass `--app-dir <dir>` to relocate (both the GUI
  and the CLI accept it).

## Distribution builds

Three ways to ship it, all scripted under `installer\`. Pick by audience:

| Build | Script | Output | .NET runtime | Best for |
|---|---|---|---|---|
| **Setup exe (WiX, Visual Studio)** | `installer\build-wix.ps1` or Build in `installer\wix\TekkenMusicModManager.Installer.sln` | `TekkenMusicModManager-Setup-<ver>.exe` (+ `.msi`) | installed by the bootstrapper if missing | end users; the classic "download, double-click, Next" install |
| **Setup exe (Inno Setup)** | `installer\build-installer.ps1` | `TekkenMusicModManager-Setup-<ver>.exe` | bundled (self-contained) | same audience, with guided tool/game-folder detection pages |
| **Portable zip** | `installer\build-portable.ps1` | `TekkenMusicModManager-<ver>-portable-win-x64.zip` | bundled in the exe | no-install use; everything stays in one folder |

All three need the .NET 8 SDK on the build machine. None bundle ffmpeg or UnrealPak (licensing — see
`installer\THIRD-PARTY-NOTICES.txt`); the app detects them, and Settings has an "Install with winget"
button for ffmpeg. The game folder is auto-detected through Steam on first start.

### Setup exe from Visual Studio (WiX v5)

`installer\wix\` holds two SDK-style WiX projects that Visual Studio 2022 opens (install the free
*HeatWave for VS2022* extension from FireGiant for editor support; plain `dotnet build` works without
it). Open `installer\wix\TekkenMusicModManager.Installer.sln`, set Release, and build **Tmm.Bundle**:

1. `Tmm.Msi` publishes the app + CLI framework-dependent (win-x64, ReadyToRun) and packs every
   published file into a per-machine MSI with Start-menu and desktop shortcuts and a Programs &
   Features entry. Major upgrades are handled (installing a newer version replaces the old one).
2. `Tmm.Bundle` downloads the .NET 8 Desktop Runtime installer once (from Microsoft's stable
   `aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe` link into `installer\wix\redist\`),
   embeds it, and emits `TekkenMusicModManager-Setup.exe`: on the user's PC it checks for an x64
   .NET Desktop Runtime ≥ 8.0 (`DotNetCoreSearch`), installs it if missing, then runs the MSI. The
   runtime is marked permanent, so uninstalling the app leaves .NET in place.

`installer\build-wix.ps1` does the same from a terminal and copies the results to `installer\Output\`.
The MSI alone supports silent deployment: `msiexec /i TekkenMusicModManager-0.1.0.msi /qn`.

### Setup exe with Inno Setup

`installer\build-installer.ps1` publishes self-contained and compiles `installer\TekkenMusicModManager.iss`
(Inno Setup 6.3+, offered via winget if missing). Its wizard adds pages the MSI does not have: detected
ffmpeg/UnrealPak paths with Browse, an offer to install ffmpeg with winget, and the TEKKEN 8 folder
(from Steam's registry key and `libraryfolders.vdf`). It writes `install-hints.json` next to the exe,
which the app folds into its settings on first start. Per-user install by default, no UAC.

### Portable zip

`installer\build-portable.ps1` publishes a single-file, self-contained `TekkenMusicModManager.exe`
(and `tmm.exe`) with `data\jukebox_slots.csv`, `portable.txt` and an empty `UserData\` folder, then
zips it. `portable.txt` next to the exe switches the app into portable mode: settings, catalog, mods
and scratch live in `UserData\` instead of `%LOCALAPPDATA%` (`Constants.IsPortable`). Delete the
marker to go back. First launch is a second or two slower while the single-file host unpacks its
native libraries to `%TEMP%`.

### Code signing

None of the outputs are signed, so Windows SmartScreen shows "unrecognized app" on first run. Sign the
exe/MSI with a code-signing certificate or Azure Trusted Signing before distribution:
`signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /a <file>`.

## CLI

```
tmm catalog build --sheet data/jukebox_slots.csv --wems <folder> [--strict]
tmm catalog status
tmm analyze <song> [--top 20] [--cap 0.06]
tmm build <song> --slot <loop_id> --name <name> [--bars N] [--start S] [--gain dB] [--intro-start S] [--no-pack]
tmm mods list | enable <id> [--force] | disable <id> | rebuild <id> | delete <id>
tmm wem dump <file.wem>
tmm find-game
```

`--no-pack` renders the two WEMs without UnrealPak — useful for checking a render in `wem dump`
before the packer is set up. `--gain` is the volume trim in dB; the render line reports how much the
limiter had to take back. `--intro-start` switches the intro to `Detached` and cuts it from that point
in the song instead of from the material before the loop.

### Intro strategies

| Strategy | Where the intro comes from |
|---|---|
| `Real` | The material immediately before the loop start. Needs the loop to start at least an intro's length into the song. |
| `Detached` | An explicit point in the song, set independently of the loop. Keeps the song's real opening while looping a later section. |
| `FadeIn` | Whatever lead-in exists, left-padded with silence and faded. |
| `Silence` | Nothing. |
| `None` | The slot has no intro WEM. |

`Detached` is the one to reach for when the loop you want is a chorus halfway through: point the intro
at 0 s and the loop wherever it sounds best, and the two stop being tied together.

## Known gaps / next steps

- Analyzer quality on real music is unmeasured; the Phase-2 exit criterion from `ARCHITECTURE.md`
  (headline correlates with blind listener ratings on the 20-song set) still stands.
- `Cue4ParseExtractor` and `FModelCliExtractor` are contracts only; the catalog build wants a folder
  the user extracted with FModel.
- `repak` output has not been confirmed to mount in Tekken 8; UnrealPak is the default.
- The limiter is a peak limiter, not a loudness maximizer: it protects the ceiling but will not make
  an already-loud master meaningfully louder. There is no true-peak (inter-sample) detection.
- Stock-loop LUFS measurement (to derive the game's own loudness target) needs a Wwise-Vorbis decoder
  (vgmstream) and is not wired; the target is a user setting for now.
- No LLM features (explanations, mood matching) — per the architecture, not v1.
