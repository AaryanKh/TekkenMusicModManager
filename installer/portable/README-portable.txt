Tekken Music Mod Manager - portable edition
============================================

No installation. Unzip anywhere you can write to (NOT inside Program Files) and run
TekkenMusicModManager.exe. The .NET runtime is built into the exe. The first start takes a second
or two longer while it unpacks its runtime libraries to your temp folder.

Windows SmartScreen may show "Windows protected your PC" the first time because the exe is not
code-signed: click "More info" > "Run anyway".

TWO TOOLS YOU HAVE TO SUPPLY
----------------------------
Neither is bundled - see THIRD-PARTY-NOTICES.txt. Empty folders are provided for both, and each
one has a README.txt with the details.

  ffmpeg        Decodes your MP3/FLAC/WAV/M4A/OGG.
                Easiest: Settings > External tools > "Install with winget".
                Or put ffmpeg.exe in tools\ffmpeg\ and Browse to it in Settings.

  UnrealPak.exe Packs the finished mod into the <name>_P.pak the game loads.
                The community build from the Tekken 8 modding tutorials. Put the whole bundle
                (UnrealPak.exe plus its DLLs) in tools\UnrealPak\ and Browse to it in Settings.
                Keep the DLLs next to the exe; it will not run without them.

The Dashboard shows one status line. When it says "Ready." you can build a mod; until then it
names what is still missing.

EVERYTHING ELSE IS ALREADY DONE
-------------------------------
  Slot catalog  Ships with the app: 432 slots, measured from the stock WEM headers. There is
                nothing to extract and FModel is NOT needed. Settings > Build catalog is only
                worth running if a game patch changes a track's length.
  Tekken 8      Auto-detected through Steam. Settings > Auto-detect if it guessed wrong.
  .NET          Built into the exe.

YOUR FILES
----------
Everything the app saves goes into the UserData folder next to the exe:
  UserData\settings.json   your settings
  UserData\catalog.json    only exists if you rebuilt the catalog yourself
  UserData\mods\           every mod you build (manifest + WEMs + pak)
Copy the whole folder to move or back up your work. Delete portable.txt to use %LOCALAPPDATA%
instead.

tmm.exe is the command-line version (run "tmm" in this folder for usage). It shares UserData.
