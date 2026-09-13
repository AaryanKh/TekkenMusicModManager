; Inno Setup 6.3+ script for Tekken Music Mod Manager.
;
; Build with installer\build-installer.ps1 (publishes the app self-contained, then compiles this).
; Or by hand:  ISCC.exe /DPublishDir="..\src\Tmm.App\bin\publish\win-x64" TekkenMusicModManager.iss
;
; What the wizard does beyond copying files:
;   * detects ffmpeg.exe (PATH, winget links, common folders) and UnrealPak.exe (next to the
;     installer, Documents, UE engine installs) and lets the user browse for either;
;   * if ffmpeg is missing, offers to install it with winget (Gyan.FFmpeg);
;   * detects the TEKKEN 8 folder through Steam's registry key + libraryfolders.vdf and lets the
;     user pick it;
;   * writes install-hints.json next to the exe; the app applies those paths to its settings on
;     first run (see Tmm.App/Services/AppServices.cs) — nothing in %LOCALAPPDATA% is touched.
;
; Nothing third-party is bundled: ffmpeg builds are (L)GPL and UnrealPak is Epic's tool under the
; UE EULA, so both stay the user's own installs.

#define MyAppName "Tekken Music Mod Manager"
#define MyAppPublisher "Tekken Music Mod Manager"
#define MyAppExeName "TekkenMusicModManager.exe"
#define MyCliExeName "tmm.exe"
#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\src\Tmm.App\bin\publish\win-x64"
#endif

[Setup]
AppId={{7C3A9D4E-5B61-4F2A-9E0C-2D8B1A6F4C13}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; Per-user by default (no UAC prompt, lands in %LOCALAPPDATA%\Programs); the user may pick
; "for all users" from the dialog, which then needs elevation.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=Output
OutputBaseFilename=TekkenMusicModManager-Setup-{#MyAppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
SetupLogging=yes
ShowLanguageDialog=no
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Everything `dotnet publish` produced: the app, the CLI, the .NET runtime, data\jukebox_slots.csv.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: files; Name: "{app}\install-hints.json"
; Mods, catalog and settings in %LOCALAPPDATA%\TekkenMusicModManager are deliberately kept.

[Code]
var
  ToolsPage: TInputFileWizardPage;
  FfmpegOptPage: TInputOptionWizardPage;
  GamePage: TInputDirWizardPage;
  GameRootSkipped: Boolean;

// ---------------------------------------------------------------- helpers

function FirstFileMatching(const Dir, Pattern, RelFile: String): String;
// Returns Dir\<first subdir matching Pattern>\RelFile if it exists, else ''.
var
  FR: TFindRec;
begin
  Result := '';
  if (Dir = '') or not DirExists(Dir) then exit;
  if FindFirst(AddBackslash(Dir) + Pattern, FR) then
  begin
    try
      repeat
        if ((FR.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) and (FR.Name <> '.') and (FR.Name <> '..') then
          if FileExists(AddBackslash(Dir) + AddBackslash(FR.Name) + RelFile) then
          begin
            Result := AddBackslash(Dir) + AddBackslash(FR.Name) + RelFile;
            exit;
          end;
      until not FindNext(FR);
    finally
      FindClose(FR);
    end;
  end;
end;

function FirstDirMatching(const Dir, Pattern: String): String;
// Returns Dir\<first subdir matching Pattern> (no trailing backslash) or ''.
var
  FR: TFindRec;
begin
  Result := '';
  if not DirExists(Dir) then exit;
  if FindFirst(AddBackslash(Dir) + Pattern, FR) then
  begin
    try
      repeat
        if ((FR.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) and (FR.Name <> '.') and (FR.Name <> '..') then
        begin
          Result := AddBackslash(Dir) + FR.Name;
          exit;
        end;
      until not FindNext(FR);
    finally
      FindClose(FR);
    end;
  end;
end;

function FindOnPath(const Exe: String): String;
var
  PathVar, Dir: String;
  P: Integer;
begin
  Result := '';
  PathVar := GetEnv('PATH');
  while PathVar <> '' do
  begin
    P := Pos(';', PathVar);
    if P = 0 then
    begin
      Dir := PathVar;
      PathVar := '';
    end
    else
    begin
      Dir := Copy(PathVar, 1, P - 1);
      Delete(PathVar, 1, P);
    end;
    Dir := Trim(Dir);
    if (Dir <> '') and FileExists(AddBackslash(Dir) + Exe) then
    begin
      Result := AddBackslash(Dir) + Exe;
      exit;
    end;
  end;
end;

function WingetAvailable: Boolean;
begin
  Result := FileExists(ExpandConstant('{localappdata}\Microsoft\WindowsApps\winget.exe'))
         or (FindOnPath('winget.exe') <> '');
end;

function DetectFfmpeg: String;
var
  Candidates: array of String;
  I: Integer;
begin
  Result := FindOnPath('ffmpeg.exe');
  if Result <> '' then exit;

  SetArrayLength(Candidates, 5);
  Candidates[0] := ExpandConstant('{localappdata}\Microsoft\WinGet\Links\ffmpeg.exe');
  Candidates[1] := ExpandConstant('{src}\ffmpeg\bin\ffmpeg.exe');
  Candidates[2] := ExpandConstant('{src}\ffmpeg.exe');
  Candidates[3] := 'C:\ffmpeg\bin\ffmpeg.exe';
  Candidates[4] := ExpandConstant('{commonpf}\ffmpeg\bin\ffmpeg.exe');
  for I := 0 to GetArrayLength(Candidates) - 1 do
    if FileExists(Candidates[I]) then
    begin
      Result := Candidates[I];
      exit;
    end;

  // winget package folder: ...\Packages\Gyan.FFmpeg_<source>\ffmpeg-<ver>-full_build\bin\ffmpeg.exe
  Result := FirstFileMatching(
    FirstDirMatching(ExpandConstant('{localappdata}\Microsoft\WinGet\Packages'), 'Gyan.FFmpeg*'),
    'ffmpeg-*', 'bin\ffmpeg.exe');
end;

function DetectUnrealPak: String;
var
  Candidates: array of String;
  I: Integer;
begin
  Result := FindOnPath('UnrealPak.exe');
  if Result <> '' then exit;

  SetArrayLength(Candidates, 6);
  Candidates[0] := ExpandConstant('{src}\UnrealPak\UnrealPak.exe');
  Candidates[1] := ExpandConstant('{src}\UnrealPak.exe');
  Candidates[2] := ExpandConstant('{userdocs}\UnrealPak\UnrealPak.exe');
  Candidates[3] := ExpandConstant('{userdocs}\Tekken 8 Mods\UnrealPak\UnrealPak.exe');
  Candidates[4] := 'C:\UnrealPak\UnrealPak.exe';
  Candidates[5] := 'C:\Tools\UnrealPak\UnrealPak.exe';
  for I := 0 to GetArrayLength(Candidates) - 1 do
    if FileExists(Candidates[I]) then
    begin
      Result := Candidates[I];
      exit;
    end;

  // Epic engine installs, newest first is not guaranteed; any UnrealPak is better than none.
  Result := FirstFileMatching(ExpandConstant('{commonpf}\Epic Games'), 'UE_5.*', 'Engine\Binaries\Win64\UnrealPak.exe');
  if Result = '' then
    Result := FirstFileMatching(ExpandConstant('{commonpf}\Epic Games'), 'UE_4.*', 'Engine\Binaries\Win64\UnrealPak.exe');
end;

function GameRootValid(const Dir: String): Boolean;
begin
  Result := (Trim(Dir) <> '') and DirExists(AddBackslash(Trim(Dir)) + 'Polaris\Content\Paks');
end;

function UnquoteVdfValue(const Line: String): String;
// Given   "path"   "D:\\SteamLibrary"   returns D:\SteamLibrary
var
  S: String;
  P: Integer;
begin
  Result := '';
  S := Line;
  P := Pos('"path"', S);
  if P = 0 then exit;
  Delete(S, 1, P + Length('"path"') - 1);
  P := Pos('"', S);
  if P = 0 then exit;
  Delete(S, 1, P);
  P := Pos('"', S);
  if P = 0 then exit;
  Result := Copy(S, 1, P - 1);
  StringChangeEx(Result, '\\', '\', True);
end;

function DetectGameRoot: String;
var
  SteamPath, Vdf, Candidate: String;
  Lines: TArrayOfString;
  I: Integer;
begin
  Result := '';
  if not RegQueryStringValue(HKCU, 'Software\Valve\Steam', 'SteamPath', SteamPath) then
    if not RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Valve\Steam', 'InstallPath', SteamPath) then
      SteamPath := ExpandConstant('{commonpf32}\Steam');
  StringChangeEx(SteamPath, '/', '\', True);

  Candidate := AddBackslash(SteamPath) + 'steamapps\common\TEKKEN 8';
  if GameRootValid(Candidate) then
  begin
    Result := Candidate;
    exit;
  end;

  Vdf := AddBackslash(SteamPath) + 'steamapps\libraryfolders.vdf';
  if LoadStringsFromFile(Vdf, Lines) then
    for I := 0 to GetArrayLength(Lines) - 1 do
    begin
      Candidate := UnquoteVdfValue(Lines[I]);
      if Candidate = '' then continue;
      Candidate := AddBackslash(Candidate) + 'steamapps\common\TEKKEN 8';
      if GameRootValid(Candidate) then
      begin
        Result := Candidate;
        exit;
      end;
    end;
end;

function JsonEscape(const S: String): String;
begin
  Result := S;
  StringChangeEx(Result, '\', '\\', True);
  StringChangeEx(Result, '"', '\"', True);
end;

// ---------------------------------------------------------------- wizard

procedure InitializeWizard;
begin
  GameRootSkipped := False;

  ToolsPage := CreateInputFilePage(wpSelectDir,
    'External tools',
    'ffmpeg and UnrealPak',
    'The app needs two tools it does not bundle. Setup looked for them on this PC; correct the paths if it guessed wrong, or leave a field blank and set it later in the app''s Settings.');
  ToolsPage.Add('ffmpeg.exe - decodes your MP3 / FLAC / WAV files:',
    'ffmpeg.exe|ffmpeg.exe|Executables|*.exe|All files|*.*', '.exe');
  ToolsPage.Add('UnrealPak.exe - the community build that packs mods (from the Tekken modding tutorial):',
    'UnrealPak.exe|UnrealPak.exe|Executables|*.exe|All files|*.*', '.exe');
  ToolsPage.Values[0] := DetectFfmpeg;
  ToolsPage.Values[1] := DetectUnrealPak;

  FfmpegOptPage := CreateInputOptionPage(ToolsPage.ID,
    'ffmpeg', 'ffmpeg was not found',
    'ffmpeg is required to read your songs. Setup can install it now with winget (Microsoft''s package manager; needs an internet connection). You can also install it yourself later and point the app at it in Settings.',
    False, False);
  FfmpegOptPage.Add('Install ffmpeg now with winget (recommended)');
  FfmpegOptPage.Values[0] := True;

  GamePage := CreateInputDirPage(FfmpegOptPage.ID,
    'TEKKEN 8 folder',
    'Where is the game installed?',
    'Mods are enabled by copying them into <game>\Polaris\Content\Paks\~mods. This is usually ...\steamapps\common\TEKKEN 8. Setup checked your Steam libraries; correct it if needed.',
    False, '');
  GamePage.Add('');
  GamePage.Values[0] := DetectGameRoot;
  if GamePage.Values[0] = '' then
    GamePage.Values[0] := ExpandConstant('{commonpf32}\Steam\steamapps\common\TEKKEN 8');
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if PageID = FfmpegOptPage.ID then
    Result := (Trim(ToolsPage.Values[0]) <> '') or (not WingetAvailable);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  V: String;
begin
  Result := True;

  if CurPageID = ToolsPage.ID then
  begin
    V := Trim(ToolsPage.Values[0]);
    if (V <> '') and not FileExists(V) then
    begin
      MsgBox('ffmpeg.exe was not found at:' + #13#10 + V + #13#10#13#10 + 'Browse to it, or clear the field.', mbError, MB_OK);
      Result := False;
      exit;
    end;
    V := Trim(ToolsPage.Values[1]);
    if (V <> '') and not FileExists(V) then
    begin
      MsgBox('UnrealPak.exe was not found at:' + #13#10 + V + #13#10#13#10 + 'Browse to it, or clear the field.', mbError, MB_OK);
      Result := False;
      exit;
    end;
  end;

  if CurPageID = GamePage.ID then
  begin
    GameRootSkipped := False;
    V := Trim(GamePage.Values[0]);
    if not GameRootValid(V) then
      case MsgBox('This folder does not contain Polaris\Content\Paks, so it does not look like the TEKKEN 8 install:' + #13#10 + V + #13#10#13#10 +
                  'Yes = use it anyway' + #13#10 + 'No = skip, set the game folder later in the app' + #13#10 + 'Cancel = go back and choose another folder',
                  mbConfirmation, MB_YESNOCANCEL) of
        IDNO: GameRootSkipped := True;
        IDCANCEL: Result := False;
      end;
  end;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo, MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
var
  S, F, U, G: String;
begin
  S := MemoDirInfo + NewLine + NewLine;
  F := Trim(ToolsPage.Values[0]);
  U := Trim(ToolsPage.Values[1]);
  G := Trim(GamePage.Values[0]);
  if (F = '') and (not ShouldSkipPage(FfmpegOptPage.ID)) and FfmpegOptPage.Values[0] then
    F := '(install with winget)';
  if F = '' then F := '(not set - configure in the app)';
  if U = '' then U := '(not set - configure in the app)';
  if GameRootSkipped or (G = '') then G := '(not set - configure in the app)';
  S := S + 'External tools:' + NewLine + Space + 'ffmpeg: ' + F + NewLine + Space + 'UnrealPak: ' + U + NewLine + NewLine;
  S := S + 'TEKKEN 8 folder:' + NewLine + Space + G + NewLine;
  if MemoTasksInfo <> '' then
    S := S + NewLine + MemoTasksInfo + NewLine;
  Result := S;
end;

procedure WriteInstallHints(const Ffmpeg, UnrealPak, GameRoot: String);
var
  Lines: TArrayOfString;
  Path: String;
begin
  SetArrayLength(Lines, 6);
  Lines[0] := '{';
  Lines[1] := '  "FfmpegPath": "' + JsonEscape(Ffmpeg) + '",';
  Lines[2] := '  "UnrealPakPath": "' + JsonEscape(UnrealPak) + '",';
  Lines[3] := '  "GameRoot": "' + JsonEscape(GameRoot) + '",';
  Lines[4] := '  "WrittenBy": "installer {#MyAppVersion}"';
  Lines[5] := '}';
  Path := ExpandConstant('{app}\install-hints.json');
  if not SaveStringsToUTF8File(Path, Lines, False) then
    Log('Could not write ' + Path);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Ffmpeg, UnrealPak, GameRoot: String;
  ResultCode: Integer;
begin
  if CurStep <> ssPostInstall then exit;

  Ffmpeg := Trim(ToolsPage.Values[0]);
  UnrealPak := Trim(ToolsPage.Values[1]);
  if GameRootSkipped then GameRoot := '' else GameRoot := Trim(GamePage.Values[0]);

  if (Ffmpeg = '') and (not ShouldSkipPage(FfmpegOptPage.ID)) and FfmpegOptPage.Values[0] then
  begin
    WizardForm.StatusLabel.Caption := 'Installing ffmpeg with winget...';
    // Runs in a visible console so the user sees winget's own progress/prompts.
    if Exec(ExpandConstant('{cmd}'),
            '/C winget install --id Gyan.FFmpeg -e --accept-source-agreements --accept-package-agreements',
            '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then
      Log('winget exit code ' + IntToStr(ResultCode))
    else
      Log('winget could not be started');
    Ffmpeg := DetectFfmpeg;
    if Ffmpeg = '' then
      MsgBox('winget did not report an ffmpeg install this Setup could find. The app will look for "ffmpeg" on your PATH when it starts; if that fails, set the path in Settings.', mbInformation, MB_OK);
  end;

  WriteInstallHints(Ffmpeg, UnrealPak, GameRoot);
end;
