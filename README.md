# Tekken Music Mod Manager

Drag a song in, get a TEKKEN 8 jukebox mod out.

Pick any track you own, choose which jukebox slot it should replace, trim the loop until it sounds
right, and the app builds the mod and installs it for you. A Windows desktop app for Windows 10 and 11.

---

## Install

1. Download the zip from [Releases](https://github.com/AaryanKh/TekkenMusicModManager/releases).
2. Unzip it somewhere you can write to — your Desktop or Documents, **not** inside `Program Files`.
3. Run `TekkenMusicModManager.exe`.

There is nothing to install and no .NET to set up; it is all inside the exe. The first launch takes a
second or two longer than later ones.

Windows will say **"Windows protected your PC"** the first time, because the app is not code-signed.
Click **More info → Run anyway**.

Everything the app saves lives in `UserData\` next to the exe, so copying that one folder takes your
settings and every mod you have built with it.

### Two tools you need to supply

These cannot be bundled for licensing reasons. The zip includes empty `tools\ffmpeg\` and
`tools\UnrealPak\` folders to drop them into. Pointing Settings at them is a one-time job.

**ffmpeg** — reads your music files (MP3, FLAC, WAV, M4A, OGG).

Easiest route is **Settings → External tools → Install with winget**, which fetches it for you.
Otherwise run `winget install Gyan.FFmpeg` yourself, or download a build from
[gyan.dev](https://www.gyan.dev/ffmpeg/builds/), put `ffmpeg.exe` into `tools\ffmpeg\`, and browse to
it in Settings.

**UnrealPak.exe** — packs the finished mod into the file the game loads.

This is the community build that comes with the usual TEKKEN 8 modding tutorials: a small bundle of
`UnrealPak.exe` plus some DLLs and `.bat` files. There is no official download, so use the one from
whichever tutorial you followed. Put the whole bundle into `tools\UnrealPak\` and browse to
`UnrealPak.exe` in Settings. Keep the DLLs beside it — it will not run without them.

### Everything else is handled

| | |
|---|---|
| **TEKKEN 8 folder** | Found automatically through Steam. Settings → **Auto-detect (Steam)** if it guesses wrong. |
| **Slot list** | All the jukebox slots ship with the app. Nothing to extract, and **FModel is not required**. |
| **.NET** | Built into the exe. |

Optional: if you have `rubberband.exe`, point Settings at it for pitch-preserving stretching. Without
it, tracks that need stretching also shift pitch slightly — at most about a semitone.

### Check you are ready

The Dashboard shows a single status line. When it reads **Ready.** you can build a mod. Until then it
tells you what is missing:

```
Not set up yet: ffmpeg, UnrealPak.exe — see Settings.
```

---

## Making a mod

1. **Drop a song** anywhere in the window. It is analysed for tempo and structure, and every loop it
   could naturally produce is worked out.
2. **Pick a slot.** All 443 jukebox tracks are ranked by how well your song fits. The percentage
   combines how much stretching is needed, how clean the loop point is, whether there is real material
   for the intro, and how much of your song gets used.
3. **Edit and preview.** Drag the loop start on the waveform, change its length in bars, choose how the
   intro is made, and set the volume. **Preview loop ×3** plays the loop back to back so you can hear
   the join. **Preview full track** plays it exactly as the game will: intro once, then the loop
   repeating. **Skip to the seam** jumps straight to the join instead of waiting for it.
4. **Build.** Both audio files are rendered to the exact length the game expects, packed, and saved.
5. **Enable** from the Dashboard. That copies the mod into the game's `~mods` folder. Disable removes
   it again and keeps the mod here, so you can switch it back on any time.

If two enabled mods would replace the same music, you are told before anything is installed. Mods other
people made that are already in `~mods` are checked too.

---

## The Dashboard

Every mod you have built, with its state: **Enabled**, **Disabled**, **Needs rebuild** (you changed
something since it was last built), or **Broken**.

Per mod you can Enable, Disable, Rebuild, Edit, or Delete. Deleting removes the mod entirely; disabling
only takes it out of the game.

### Tile view

The button at the bottom right switches between the table and a grid of tiles, and your choice is
remembered. Each tile shows the song's album art, with a green dot when the mod is enabled and a grey
one when it is not. Hovering or selecting a tile lifts the art and slides the TEKKEN game's cover out
from behind it. Click a tile for its details and actions; click anywhere else, or press Escape, to
deselect.

**Album art** comes from the song file itself when it has any. When it does not, you get a placeholder,
and two extra buttons appear:

- **Find art online** looks the album up from the song's tags. If the file has no useful tags it falls
  back to the mod's own name and progressively looser searches, so untagged files usually still find
  something. You choose from the results; nothing is applied on its own.
- **Remove art** goes back to the placeholder.

**TEKKEN covers**, the artwork behind each tile, start as simple drawn cards. **Settings → Tekken
covers** can look up a real cover for each game. The games themselves are not in any music catalogue,
but their official soundtrack albums are, and that cover is the same square artwork the in-game jukebox
uses. Again, you choose from the results.

Nothing is downloaded until you ask for it, and no artwork is bundled with the app.

---

## Getting a good-sounding loop

**Loop length in bars** is the main control. Fewer or more bars changes how much your song has to be
stretched to fit the slot exactly. Under about 3 % nobody notices; past 6 % it starts to sound wrong,
and the app warns you.

**The seam** is where the loop wraps back to its start. The seam score compares your loop point against
every other possible cut in the same song, so 97 % means only 3 % of cuts would sound better. The
crossfade slider smooths the join: 20 ms hides most clicks, and over 100 ms starts to smear drums.

**Intro** decides what plays once before the loop begins:

| | |
|---|---|
| **Real** | The music immediately before your loop start. Needs the loop to begin far enough into the song. |
| **Detached** | Intro taken from anywhere you like, independent of the loop. Use this to keep a song's real opening while looping a chorus from the middle. |
| **Fade in** | Whatever lead-in exists, padded with silence and faded up. |
| **Silence** | No intro; the loop starts cold. |

With **Detached** ticked you can drag the intro band on the waveform separately from the loop.

**Volume adjustment** turns the whole mod up or down, applied to the intro and loop together so nothing
jumps at the handover. It cannot distort: past a point it simply stops getting louder. Use it when one
of your mods sounds quieter than another in game — raise the quiet one. How much of it survives depends
on the song. A quiet track takes +12 dB cleanly; one already mastered loud has no room, and the preview
will tell you so.

---

## Questions you might have

**Is this safe for my game?** Mods are copied into `Polaris\Content\Paks\~mods` and nothing else is
touched. Disabling a mod deletes that one file. Removing the whole folder puts the game back exactly as
it was.

**Will it break when the game updates?** Usually not. If a patch changes the length of a track you have
replaced, rebuild that mod. Settings can re-measure the slot list from your own game files, but you only
need that in this situation.

**Which tracks can I replace?** Almost all of them. The Season 2 and collaboration tracks are the
exception: they live outside the game's base files and are not in the list that ships, so you would need
to re-measure the slot list in Settings to use those slots.

**Can I rename my mods?** Yes. Rename the `.pak` in `~mods` however you like, then press **Scan ~mods**
on the Dashboard and the app will recognise it and update itself to match.

**Why does my song sound slightly off-pitch?** Because it needed stretching to fit the slot, and without
`rubberband.exe` the pitch moves with it. Either install rubberband, or pick a loop length closer to the
slot's own.

**Where are my mods kept?** In `UserData\mods\` beside the exe. Each mod keeps its settings and its
built file there, which is why disabling never loses anything.

---

## Command line

The zip also contains `tmm.exe`, which does everything the app does without the window. Run it with no
arguments for the full list.

```
tmm analyze <song>                                  rank every slot for a song
tmm build <song> --slot <id> [--name <name>]        build a mod
tmm mods list | scan [--adopt] | enable <id> | disable <id> | rebuild <id> | delete <id>
tmm find-game                                       locate the TEKKEN 8 install
```

---

## Building it yourself

Needs the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```
dotnet build TekkenMusicModManager.sln
dotnet test  TekkenMusicModManager.sln
dotnet run --project src/Tmm.App
```

`installer\build-portable.ps1` produces the zip from Releases. `installer\build-installer.ps1` and
`installer\build-wix.ps1` produce setup executables instead.

The engine (`src/Tmm.Core`) has no dependencies outside .NET itself; only the interface needs Windows.

---

## Known limits

- Songs with an unsteady or unclear tempo may be analysed poorly. When confidence is low the app says
  so and points you at the manual editor.
- Without `rubberband.exe`, stretching shifts pitch.
- The volume control protects against distortion but will not make an already-loud song much louder.
- Season 2 and collaboration slots need the slot list re-measured from your own game files.

---

## Thanks

Built on the community's TEKKEN 8 audio modding work: the jukebox slot spreadsheet, and the UnrealPak
build the tutorials pass around.

TEKKEN is a trademark of Bandai Namco Entertainment. This is an unofficial fan tool, not affiliated with
or endorsed by them. No game files or artwork are distributed with it.
