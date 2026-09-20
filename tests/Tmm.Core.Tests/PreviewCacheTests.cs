using Tmm.Core.Audio;
using Xunit;

namespace Tmm.Core.Tests;

/// <summary>The audio track cache is cleared on exit; these pin what that may and may not delete.</summary>
public class PreviewCacheTests : IDisposable
{
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "tmm-cache-" + Guid.NewGuid().ToString("N"), "cache");

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_cache)!, true); } catch { }
    }

    private string Touch(string name)
    {
        Directory.CreateDirectory(_cache);
        var p = Path.Combine(_cache, name);
        File.WriteAllBytes(p, new byte[] { 1, 2, 3 });
        return p;
    }

    [Fact]
    public void NewPathUsesTheNamesClearLooksFor()
    {
        foreach (var kind in new[] { PreviewCache.Loop, PreviewCache.Intro, PreviewCache.Track })
        {
            var p = PreviewCache.NewPath(_cache, kind);
            File.WriteAllBytes(p, new byte[] { 0 });
            Assert.StartsWith(kind + "_", Path.GetFileName(p));
            Assert.EndsWith(".wav", p);
        }
        Assert.Equal(3, Directory.GetFiles(_cache).Length);
        Assert.Equal(3, PreviewCache.Clear(_cache));
        Assert.False(Directory.Exists(_cache));   // emptied, so the folder goes too
    }

    [Fact]
    public void ClearRemovesLeftoversFromEarlierVersions()
    {
        // The exact shape found in a real UserData\cache before this existed.
        Touch("track_a2dd5519e8d14656a817ce6f5ea3feb7.wav");
        Assert.Equal(1, PreviewCache.Clear(_cache));
        Assert.False(Directory.Exists(_cache));
    }

    [Fact]
    public void ClearLeavesEverythingElseAlone()
    {
        var mine = Touch("preview_0123.wav");
        var notes = Touch("notes.txt");
        var other = Touch("cover_preview.wav");           // a .wav, but not one of ours
        var kept = Touch("preview_0123.wav.bak");

        Assert.Equal(1, PreviewCache.Clear(_cache));

        Assert.False(File.Exists(mine));
        Assert.True(File.Exists(notes));
        Assert.True(File.Exists(other));
        Assert.True(File.Exists(kept));
        Assert.True(Directory.Exists(_cache));            // not empty, so the folder stays
    }

    [Fact]
    public void ClearOnAFolderThatDoesNotExistIsANoOp()
        => Assert.Equal(0, PreviewCache.Clear(Path.Combine(_cache, "missing")));

    [Fact]
    public void ClearRemovesAReadOnlyFile()
    {
        // OneDrive and friends leave synced files read-only.
        var p = Touch("intro_ro.wav");
        File.SetAttributes(p, FileAttributes.ReadOnly);
        Assert.Equal(1, PreviewCache.Clear(_cache));
        Assert.False(File.Exists(p));
    }

    [Fact]
    public void AFileHeldOpenIsSkippedNotFatal()
    {
        var free = Touch("preview_free.wav");
        var held = Touch("track_held.wav");
        using var lockIt = new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None);   // another copy of the app playing it

        var removed = PreviewCache.Clear(_cache);           // must not throw, and must not stall on retries

        Assert.Equal(1, removed);
        Assert.False(File.Exists(free));
        Assert.True(File.Exists(held));
        Assert.True(Directory.Exists(_cache));
    }
}
