using System.Text;

namespace Tmm.Core.Catalog;

/// <summary>
/// Import the community Jukebox WEM List (as normalized CSV) into SlotIdentity rows.
///
/// The sheet is an ID/title index. Its lengths are whole-second and crowd-sourced; they are kept
/// only so the UI can show something before the catalog has been measured locally.
/// </summary>
public static class SheetLoader
{
    public static List<SlotIdentity> Load(string csvPath)
    {
        using var reader = new StreamReader(csvPath, Encoding.UTF8);
        return Parse(reader);
    }

    public static List<SlotIdentity> Parse(TextReader reader)
    {
        var rows = new List<SlotIdentity>();
        string[]? header = null;
        foreach (var fields in Csv.ReadRecords(reader))
        {
            if (header is null) { header = fields; continue; }
            if (fields.Length == 1 && fields[0].Length == 0) continue;   // blank line
            var r = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < header.Length && i < fields.Length; i++) r[header[i].Trim()] = fields[i];
            try
            {
                // Two sheet rows ("Ancient Powers", Normal/Climax) have no number; fall back to position.
                rows.Add(new SlotIdentity(
                    No: OptInt(r, "no") ?? rows.Count + 1,
                    Title: r["title"].Trim(),
                    Game: r["game"].Trim(),
                    IntroId: OptInt(r, "intro_id"),
                    LoopId: OptInt(r, "loop_id") ?? throw new FormatException("loop_id is blank"),
                    IntroSecSheet: OptInt(r, "intro_sec"),
                    LoopSecSheet: OptInt(r, "loop_sec") ?? throw new FormatException("loop_sec is blank")));
            }
            catch (Exception e) when (e is FormatException or KeyNotFoundException)
            {
                throw new TmmException($"jukebox sheet row {rows.Count + 2} ('{(r.TryGetValue("title", out var t) ? t : "?")}'): {e.Message}");
            }
        }
        return rows;
    }

    private static int? OptInt(Dictionary<string, string> r, string key)
        => r.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? int.Parse(v.Trim()) : null;
}

/// <summary>RFC 4180 CSV: quoted fields, doubled quotes, embedded newlines.</summary>
public static class Csv
{
    public static IEnumerable<string[]> ReadRecords(TextReader reader)
    {
        var fields = new List<string>();
        var cur = new StringBuilder();
        bool inQuotes = false, any = false;
        int c;
        while ((c = reader.Read()) != -1)
        {
            char ch = (char)c;
            any = true;
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (reader.Peek() == '"') { reader.Read(); cur.Append('"'); }
                    else inQuotes = false;
                }
                else cur.Append(ch);
            }
            else if (ch == '"') inQuotes = true;
            else if (ch == ',') { fields.Add(cur.ToString()); cur.Clear(); }
            else if (ch == '\r') { /* swallow; \n ends the record */ }
            else if (ch == '\n')
            {
                fields.Add(cur.ToString()); cur.Clear();
                yield return fields.ToArray();
                fields.Clear(); any = false;
            }
            else cur.Append(ch);
        }
        if (any || fields.Count > 0)
        {
            fields.Add(cur.ToString());
            yield return fields.ToArray();
        }
    }
}
