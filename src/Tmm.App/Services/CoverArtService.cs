using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Tmm.Core;
using Tmm.Core.Analysis;
using Tmm.Core.Mods;

namespace Tmm.App.Services;

/// <summary>
/// Images for the tile view: a song's own album art, the Tekken game it replaces, and a placeholder
/// when the song carries no picture.
///
/// Song art is extracted once with ffmpeg and cached beside the manifest. A song with no art is
/// remembered too, so a refresh does not re-run ffmpeg for every mod. Game covers come from
/// <c>data\covers\&lt;tag&gt;.png</c> next to the exe when the user has dropped some in, and are drawn
/// at runtime otherwise: no box art is shipped, so nothing copyrighted travels with the app.
/// </summary>
public sealed class CoverArtService
{
    private readonly AppServices _app;
    private readonly Dictionary<string, ImageSource> _gameCovers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ImageSource, ImageSource> _gray = new();
    private ImageSource? _placeholder;

    public CoverArtService(AppServices app) => _app = app;

    /// <summary>Square size everything is rendered or decoded at. Tiles are smaller; the headroom
    /// keeps the game cover crisp when it peeks out behind at a slight angle.</summary>
    public const int Size = 320;

    // ------------------------------------------------------------------ song art

    private string CoverPath(ModManifest m) => Path.Combine(_app.Registry.ModDir(m), CoverArt.FileName);
    private string NonePath(ModManifest m) => Path.Combine(_app.Registry.ModDir(m), CoverArt.NoneMarker);

    /// <summary>True when the answer is already on disk, one way or the other.</summary>
    public bool IsCached(ModManifest m) => File.Exists(CoverPath(m)) || File.Exists(NonePath(m));

    /// <summary>Run ffmpeg if needed. Safe on a worker thread; touches only files.</summary>
    public void EnsureExtracted(ModManifest m)
    {
        if (IsCached(m)) return;
        var dir = _app.Registry.ModDir(m);
        if (!Directory.Exists(dir)) return;
        if (!CoverArt.Extract(m.SongPath, _app.Settings.FfmpegExe, CoverPath(m)))
        {
            try { File.WriteAllBytes(NonePath(m), Array.Empty<byte>()); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>The cached picture, or null when the song has none. Never runs ffmpeg.</summary>
    public ImageSource? LoadSongCover(ModManifest m)
    {
        var path = CoverPath(m);
        if (!File.Exists(path)) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;     // release the file straight away
            bmp.DecodePixelWidth = Size;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception e) when (e is NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return null;    // a corrupt picture reads as "no picture", not as a crash on the dashboard
        }
    }

    /// <summary>Use a picture the user chose. Replaces whatever was cached and forgets any
    /// "no art" marker, so the tile picks it up on the next refresh.</summary>
    public void SetCover(ModManifest m, byte[] jpegOrPng)
    {
        var dir = _app.Registry.ModDir(m);
        Directory.CreateDirectory(dir);
        FileOps.DeleteFile(NonePath(m));
        var path = CoverPath(m);
        FileOps.PrepareWrite(path);
        File.WriteAllBytes(path, jpegOrPng);
    }

    /// <summary>Back to the placeholder. Remembered, so the file's own embedded art is not pulled
    /// straight back in on the next refresh; "Find art" offers it again as a candidate.</summary>
    public void RemoveCover(ModManifest m)
    {
        FileOps.DeleteFile(CoverPath(m));
        var dir = _app.Registry.ModDir(m);
        if (!Directory.Exists(dir)) return;
        try { File.WriteAllBytes(NonePath(m), Array.Empty<byte>()); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>The picture embedded in the song, extracted to a scratch file so it can sit in the
    /// chooser next to online results. Null when the file carries none.</summary>
    public ArtCandidate? EmbeddedCandidate(ModManifest m)
    {
        var scratch = Path.Combine(_app.Settings.ScratchDir, "art", m.ModId + ".jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(scratch)!);
        if (!CoverArt.Extract(m.SongPath, _app.Settings.FfmpegExe, scratch)) return null;
        var uri = new Uri(scratch, UriKind.Absolute).AbsoluteUri;
        return new ArtCandidate("Picture inside the song file", Path.GetFileName(m.SongPath), uri, uri, "embedded");
    }

    // ------------------------------------------------------------------ Tekken game covers

    /// <summary>
    /// Where chosen game covers live. Under the app dir, not next to the exe: an installed build sits
    /// in Program Files, which is not writable. A folder beside the exe is still read as a fallback so
    /// anything pre-placed there keeps working.
    /// </summary>
    public string GameCoversDir => Path.Combine(_app.Settings.AppDir, "covers");
    private static string ShippedCoversDir => Path.Combine(AppContext.BaseDirectory, "data", "covers");

    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg" };

    /// <summary>The file holding this game's chosen cover, or null when it is still the drawn card.</summary>
    public string? CustomGameCoverPath(string tag)
    {
        foreach (var dir in new[] { GameCoversDir, ShippedCoversDir })
            foreach (var ext in ImageExtensions)
            {
                var p = Path.Combine(dir, tag + ext);
                if (File.Exists(p)) return p;
            }
        return null;
    }

    public bool HasCustomGameCover(string tag) => CustomGameCoverPath(tag) is not null;

    /// <summary>Use a picture for this game. Replaces whatever was there under any extension.</summary>
    public void SetGameCover(string tag, byte[] imageBytes)
    {
        Directory.CreateDirectory(GameCoversDir);
        foreach (var ext in ImageExtensions) FileOps.DeleteFile(Path.Combine(GameCoversDir, tag + ext));
        var dest = Path.Combine(GameCoversDir, tag + ".jpg");
        FileOps.PrepareWrite(dest);
        File.WriteAllBytes(dest, imageBytes);
        _gameCovers.Remove(tag);
    }

    /// <summary>Back to the drawn card. Only touches the writable folder.</summary>
    public void RemoveGameCover(string tag)
    {
        foreach (var ext in ImageExtensions) FileOps.DeleteFile(Path.Combine(GameCoversDir, tag + ext));
        _gameCovers.Remove(tag);
    }

    /// <summary>Forget the loaded game cards so the next request reloads them from disk.</summary>
    public void ForgetGameCovers() => _gameCovers.Clear();

    // ------------------------------------------------------------------ derived

    /// <summary>Desaturated copy for a disabled mod. Cached per source.</summary>
    public ImageSource Gray(ImageSource source)
    {
        if (_gray.TryGetValue(source, out var g)) return g;
        if (source is not BitmapSource bs) return source;
        var conv = new FormatConvertedBitmap(bs, PixelFormats.Gray8, null, 0);
        conv.Freeze();
        _gray[source] = conv;
        return conv;
    }

    /// <summary>A dark tile with a question mark, for songs without art.</summary>
    public ImageSource Placeholder => _placeholder ??= Render(dc =>
    {
        Fill(dc, ThemeColor("PanelColor"));
        Text(dc, "?", 150, ThemeColor("MutedColor"), Size / 2, Size / 2, bold: true);
    });

    // ------------------------------------------------------------------ game covers

    /// <summary>The cover for the Tekken game a slot belongs to: the chosen picture if there is one,
    /// else the drawn card.</summary>
    public ImageSource GameCover(string gameLabel) => GameCoverFor(NameSuggester.TagFor(gameLabel), gameLabel);

    public ImageSource GameCoverFor(string tag, string gameLabel)
    {
        if (_gameCovers.TryGetValue(tag, out var cached)) return cached;
        var img = LoadUserGameCover(tag) ?? DrawGameCover(tag, gameLabel);
        _gameCovers[tag] = img;
        return img;
    }

    private ImageSource? LoadUserGameCover(string tag)
    {
        var p = CustomGameCoverPath(tag);
        if (p is null) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = Size;
            bmp.UriSource = new Uri(p, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception e) when (e is NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return null;    // unreadable picture falls back to the drawn card
        }
    }

    /// <summary>"TEKKEN 7" as a square card in the app's palette, with the tag in the corner.</summary>
    private ImageSource DrawGameCover(string tag, string label)
    {
        var l = (label ?? "").Trim();
        var suffix = l.StartsWith("TEKKEN", StringComparison.OrdinalIgnoreCase) ? l[6..].Trim() : l;
        return Render(dc =>
        {
            var bg = ThemeColor("Panel2Color");
            var accent = ThemeColor("AccentColor");
            Fill(dc, bg);
            // A diagonal band gives the card a direction, so it still reads as art when only a corner
            // is peeking out from behind the song's own cover.
            var band = new StreamGeometry();
            using (var g = band.Open())
            {
                g.BeginFigure(new Point(0, Size), true, true);
                g.LineTo(new Point(Size, 0), false, false);
                g.LineTo(new Point(Size, Size * 0.42), false, false);
                g.LineTo(new Point(Size * 0.42, Size), false, false);
            }
            band.Freeze();
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(0x5A, accent.R, accent.G, accent.B)), null, band);

            var text = ThemeColor("TextColor");
            Text(dc, "TEKKEN", 44, text, Size / 2, Size * 0.40, bold: true);
            if (suffix.Length > 0)
                Text(dc, suffix, suffix.Length > 4 ? 30 : 76, accent, Size / 2, Size * 0.62, bold: true);
            Text(dc, $"[{tag}]", 18, ThemeColor("MutedColor"), Size - 34, Size - 20, bold: false);
        });
    }

    // ------------------------------------------------------------------ drawing helpers

    private static Color ThemeColor(string key)
        => Application.Current?.TryFindResource(key) is Color c ? c : Color.FromRgb(0x22, 0x26, 0x2F);

    private static void Fill(DrawingContext dc, Color c)
        => dc.DrawRoundedRectangle(new SolidColorBrush(c), null, new Rect(0, 0, Size, Size), 12, 12);

    private static void Text(DrawingContext dc, string s, double size, Color color, double cx, double cy, bool bold)
    {
        var ft = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal),
            size, new SolidColorBrush(color), 96);
        ft.MaxTextWidth = Size - 24;
        ft.TextAlignment = TextAlignment.Center;
        dc.DrawText(ft, new Point(cx - ft.MaxTextWidth / 2, cy - ft.Height / 2));
    }

    private static ImageSource Render(Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) draw(dc);
        var rtb = new RenderTargetBitmap(Size, Size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }
}
