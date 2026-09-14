using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Tmm.Core.Analysis;

/// <summary>One picture the user could choose. <see cref="FullUrl"/> may be a file:// URI for art
/// that is already on disk, such as the picture embedded in the song itself.</summary>
public sealed record ArtCandidate(string Title, string Artist, string ThumbUrl, string FullUrl, string Source);

/// <summary>
/// Looks album art up online. Uses the iTunes Search API because it needs no key, no account and no
/// library: a plain HTTPS GET returning JSON, which .NET handles by itself. So the feature adds no
/// installation dependency, only an optional network call the user triggers by hand.
/// </summary>
public static class AlbumArtSearch
{
    private static readonly HttpClient Http = MakeClient();

    private static HttpClient MakeClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TekkenMusicModManager", "0.1"));
        return c;
    }

    /// <summary>What to search for. Album plus artist beats the track title, which names one song
    /// rather than the record it came from; a bare filename is the last resort.</summary>
    public static string BuildQuery(SongTags tags, string? songPath)
    {
        var parts = new List<string>();
        if (tags.Album.Length > 0) parts.Add(tags.Album);
        else if (tags.Title.Length > 0) parts.Add(tags.Title);
        if (tags.Artist.Length > 0 && !LooksGeneric(tags.Artist)) parts.Add(tags.Artist);
        if (parts.Count == 0 && !string.IsNullOrWhiteSpace(songPath))
        {
            var stem = Path.GetFileNameWithoutExtension(songPath);
            stem = System.Text.RegularExpressions.Regex.Replace(stem, @"^\s*\d{1,3}([\-.]\d{1,3})?\s*[\-.)\]:]*\s+", "");
            parts.Add(System.Text.RegularExpressions.Regex.Replace(stem, @"[_\-]+", " ").Trim());
        }
        return string.Join(' ', parts).Trim();
    }

    private static bool LooksGeneric(string artist) =>
        artist.Equals("VA", StringComparison.OrdinalIgnoreCase) ||
        artist.Equals("Various Artists", StringComparison.OrdinalIgnoreCase) ||
        artist.Equals("Various", StringComparison.OrdinalIgnoreCase) ||
        artist.Equals("Unknown", StringComparison.OrdinalIgnoreCase);

    public static async Task<List<ArtCandidate>> SearchAsync(string query, int limit = 6, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return new List<ArtCandidate>();
        var url = "https://itunes.apple.com/search?media=music&entity=album&limit=" + limit + "&term=" + Uri.EscapeDataString(query);
        var json = await Http.GetStringAsync(url, ct).ConfigureAwait(false);
        return ParseItunes(json);
    }

    /// <summary>Pure: the shape of an iTunes response into candidates. Artwork links come back at
    /// 100 px; the same path with the size swapped serves a 600 px copy.</summary>
    public static List<ArtCandidate> ParseItunes(string json)
    {
        var list = new List<ArtCandidate>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return list;
        foreach (var r in results.EnumerateArray())
        {
            var thumb = Str(r, "artworkUrl100");
            if (thumb.Length == 0) continue;
            var full = thumb.Replace("100x100bb", "600x600bb", StringComparison.OrdinalIgnoreCase);
            list.Add(new ArtCandidate(Str(r, "collectionName"), Str(r, "artistName"), thumb, full, "iTunes"));
        }
        return list;
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>Fetch the picture bytes. A file:// candidate is read straight from disk.</summary>
    public static async Task<byte[]> DownloadAsync(ArtCandidate c, CancellationToken ct = default)
    {
        if (Uri.TryCreate(c.FullUrl, UriKind.Absolute, out var uri) && uri.IsFile)
            return await File.ReadAllBytesAsync(uri.LocalPath, ct).ConfigureAwait(false);
        return await Http.GetByteArrayAsync(c.FullUrl, ct).ConfigureAwait(false);
    }
}
