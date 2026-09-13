Tekken Music Mod Manager - portable edition
============================================

No installation. Unzip anywhere (a folder you can write to - not inside Program Files) and run
TekkenMusicModManager.exe. The .NET runtime is built into the exe. The first start takes a second
or two longer while it unpacks its runtime libraries to your temp folder.

Everything the app saves goes into the UserData folder next to the exe:
  UserData\settings.json   your settings
  UserData\catalog.json    the measured slot catalog
  UserData\mods\           every mod you build (manifest + WEMs + pak)
Copy the whole folder to move or back up your work. Delete portable.txt to use %LOCALAPPDATA%
instead.

You still need two external tools (they are not bundled, see THIRD-PARTY-NOTICES.txt):
  * ffmpeg   - Settings > External tools > "Install with winget", or install it yourself and Browse.
  * UnrealPak.exe (the community build from the Tekken 8 modding tutorials) - Browse to it.
And a one-time slot catalog build from the game's stock WEMs (Settings > Slot catalog).

tmm.exe is the command-line version (run "tmm" in this folder for usage). It shares UserData.

Windows SmartScreen may show "Windows protected your PC" the first time because the exe is not
code-signed: click "More info" > "Run anyway".
