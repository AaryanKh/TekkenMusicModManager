namespace Tmm.Core.Audio;

/// <summary>
/// The WAV files the editor renders so a preview can be played (the loop on its own, the intro on its
/// own, or the assembled track). They are throwaway: the editor removes the previous one each time it
/// starts another, but the last one used to be left behind on exit and they piled up in the cache
/// folder. The app now clears them when it closes, and again when it starts, which also covers a crash
/// or a killed process.
///
/// Only files this class names are ever touched, so a cache folder that someone has put other things
/// in, or an app folder pointed somewhere unexpected, loses nothing else.
/// </summary>
public static class PreviewCache
{
    public const string Loop = "preview";
    public const string Intro = "intro";
    public const string Track = "track";

    private static readonly string[] Kinds = { Loop, Intro, Track };

    /// <summary>A fresh path for one preview file of the given kind. Creates the folder.</summary>
    public static string NewPath(string cacheDir, string kind)
    {
        Directory.CreateDirectory(cacheDir);
        return Path.Combine(cacheDir, $"{kind}_{Guid.NewGuid():N}.wav");
    }

    /// <summary>
    /// Delete every preview file, then the folder itself if that leaves it empty. Never throws: a file
    /// another running copy of the app is still playing stays where it is and is picked up next time.
    /// Returns how many files were removed.
    /// </summary>
    public static int Clear(string cacheDir)
    {
        if (!Directory.Exists(cacheDir)) return 0;

        int removed = 0;
        foreach (var kind in Kinds)
        {
            string[] files;
            try { files = Directory.GetFiles(cacheDir, kind + "_*.wav"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }

            foreach (var file in files)
            {
                // One attempt, no retry sleeps: this runs while the app is closing.
                try { FileOps.DeleteFile(file, attempts: 1); removed++; }
                catch (TmmException) { /* held open elsewhere */ }
            }
        }

        // Not recursive, so this only succeeds when nothing else is in there.
        try { Directory.Delete(cacheDir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return removed;
    }
}
