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

    /// <summary>
    /// What to search for when looking up a Tekken game's cover. The games themselves are not in a
    /// music catalogue, but their official soundtrack albums are, and that album cover is the same
    /// kind of square artwork the in-game jukebox shows.
    /// </summary>
    public static string GameSoundtrackQuery(string gameLabel)
    {
        var l = (gameLabel ?? "").Trim();
        if (l.Length == 0) return "";
        return l + " Original Soundtrack";
    }

    /// <summary>
    /// Queries to try in order, stopping at the first that returns anything. Tags are the best clue
    /// when they exist, but plenty of files have none or have an album nobody sells, so the ladder
    /// falls back to the mod's own suggested name — "[TTT]_YuGiOhDuelistsOfTheRoses_VsLancastrians"
    /// carries the game and the track, just welded together — and then to progressively looser
    /// fragments of it.
    /// </summary>
    public static IReadOnlyList<string> QueryLadder(SongTags tags, string? songPath, string? modName)
    {
        var ladder = new List<string>();
        void Add(string? q)
        {
            var t = (q ?? "").Trim();
            if (t.Length < 3) return;
            if (!ladder.Any(x => x.Equals(t, StringComparison.OrdinalIgnoreCase))) ladder.Add(t);
        }

        Add(BuildQuery(tags, songPath));
        Add(tags.Album);                                   // album alone, without a narrowing artist
        if (tags.Title.Length > 0 && tags.Artist.Length > 0 && !LooksGeneric(tags.Artist))
            Add(tags.Title + " " + tags.Artist);

        // The mod name: whole thing first, then the parts, widest to narrowest.
        var parts = ModNameParts(modName);
        if (parts.Count > 0)
        {
            Add(string.Join(' ', parts.Select(SplitWords)));
            foreach (var p in parts) Add(SplitWords(p));
        }

        Add(SplitWords(Path.GetFileNameWithoutExtension(songPath ?? "")));
        return ladder;
    }

    /// <summary>"[TTT]_YuGiOh_VsLancastrians" becomes ["YuGiOh", "VsLancastrians"]: the tag is ours,
    /// not the album's, so it would only poison the search.</summary>
    public static List<string> ModNameParts(string? modName)
    {
        var name = (modName ?? "").Trim();
        if (name.Length == 0) return new List<string>();
        name = System.Text.RegularExpressions.Regex.Replace(name, @"^\s*\[[^\]]*\]\s*_?", "");
        return name.Split('_', StringSplitOptions.RemoveEmptyEntries)
                   .Select(p => p.Trim())
                   .Where(p => p.Length > 1)
                   .ToList();
    }

    /// <summary>"YuGiOhDuelistsOfTheRoses" becomes "Yu Gi Oh Duelists Of The Roses". Splits where a
    /// lower letter meets an upper one, where an acronym ends, and at letter/digit boundaries, so
    /// "DBZBudokai3" comes out as "DBZ Budokai 3".</summary>
    public static string SplitWords(string text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) return "";
        t = t.Replace('_', ' ').Replace('-', ' ');
        var sb = new System.Text.StringBuilder(t.Length + 8);
        for (int i = 0; i < t.Length; i++)
        {
            char c = t[i];
            if (i > 0 && NeedsSpace(t, i)) sb.Append(' ');
            sb.Append(c);
        }
        return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    private static bool NeedsSpace(string t, int i)
    {
        char prev = t[i - 1], c = t[i];
        if (char.IsWhiteSpace(prev) || char.IsWhiteSpace(c)) return false;
        if (char.IsLower(prev) && char.IsUpper(c)) return true;                       // ...sOf -> s Of
        if (char.IsUpper(prev) && char.IsUpper(c) && i + 1 < t.Length && char.IsLower(t[i + 1]))
            return true;                                                              // DBZBud -> DBZ Bud
        if (char.IsLetter(prev) && char.IsDigit(c)) return true;                       // Budokai3 -> Budokai 3
        if (char.IsDigit(prev) && char.IsLetter(c)) return true;
        return false;
    }

    /// <summary>
    /// How well a result's title matches what was asked for, 0..1, by how many of the query's words it
    /// contains. Enough to float "Yu-Gi-Oh! The Duelists of the Roses" above an unrelated record that
    /// happened to come back with it, without pulling in a string-distance library.
    /// </summary>
    public static double Similarity(string query, string candidate)
    {
        var q = Words(query);
        if (q.Count == 0) return 0;
        var c = Words(candidate);
        if (c.Count == 0) return 0;
        int hit = q.Count(w => c.Contains(w));
        return (double)hit / q.Count;
    }

    private static HashSet<string> Words(string s) =>
        new(System.Text.RegularExpressions.Regex.Split(s.ToLowerInvariant(), @"[^a-z0-9]+")
                .Where(w => w.Length > 1), StringComparer.Ordinal);

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

    /// <summary>A result this close to the query is taken as the right record; below it, the ladder
    /// keeps looking. A long query like "Sonic Heroes Vs Team Battle" does return records, just not
    /// related ones, so "returned something" is not a good enough stopping rule.</summary>
    public const double GoodEnough = 0.5;

    /// <summary>
    /// Walk the ladder, scoring each query's results against it. Stops at the first query whose best
    /// result clears <see cref="GoodEnough"/>; otherwise it keeps widening and finally hands back the
    /// best set it saw, so the user still has something to choose from. Returns the query that won.
    /// </summary>
    public static async Task<(List<ArtCandidate> results, string query)> SearchLadderAsync(
        IReadOnlyList<string> ladder, int limit = 8, CancellationToken ct = default)
    {
        List<ArtCandidate> bestSet = new();
        string bestQuery = ladder.Count > 0 ? ladder[0] : "";
        double bestScore = -1;

        foreach (var q in ladder)
        {
            ct.ThrowIfCancellationRequested();
            var found = await SearchAsync(q, limit, ct).ConfigureAwait(false);
            if (found.Count == 0) continue;

            var ranked = Rank(q, found);
            double score = Similarity(q, ranked[0].Title + " " + ranked[0].Artist);
            if (score > bestScore) { bestScore = score; bestSet = ranked; bestQuery = q; }
            if (score >= GoodEnough) return (ranked, q);
        }
        return (bestSet, bestQuery);
    }

    /// <summary>Closest match first.</summary>
    public static List<ArtCandidate> Rank(string query, IEnumerable<ArtCandidate> candidates) =>
        candidates.OrderByDescending(c => Similarity(query, c.Title + " " + c.Artist)).ToList();

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
