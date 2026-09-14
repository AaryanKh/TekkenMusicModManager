using Tmm.Core;
using Xunit;

namespace Tmm.Core.Tests;

/// <summary>
/// The read-only-directory case that broke rebuilding a mod stored under OneDrive. A sync client
/// leaves ReadOnly on the folders it manages, and Directory.Delete refuses to remove one, reporting
/// "Access to the path ... is denied" with no hint about the attribute.
/// </summary>
public class FileOpsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tmm-fileops-{Guid.NewGuid():N}");

    public FileOpsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { FileOps.DeleteDirectory(_root); } catch { /* best effort */ }
    }

    private string Dir(string name)
    {
        var d = Path.Combine(_root, name);
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void PlainDirectoryDeleteFailsOnAReadOnlyFolder()
    {
        // Pins the behaviour being worked around. If a future runtime stops throwing here, the
        // workaround is no longer load bearing and this test says so.
        var d = Dir("plain");
        File.WriteAllText(Path.Combine(d, "a.txt"), "x");
        new DirectoryInfo(d).Attributes |= FileAttributes.ReadOnly;
        try
        {
            Assert.ThrowsAny<Exception>(() => Directory.Delete(d, recursive: true));
        }
        finally
        {
            new DirectoryInfo(d).Attributes &= ~FileAttributes.ReadOnly;
        }
    }

    [Fact]
    public void DeleteDirectoryRemovesAReadOnlyFolder()
    {
        var d = Dir("readonly-dir");
        File.WriteAllText(Path.Combine(d, "a.txt"), "x");
        new DirectoryInfo(d).Attributes |= FileAttributes.ReadOnly;

        FileOps.DeleteDirectory(d);
        Assert.False(Directory.Exists(d));
    }

    [Fact]
    public void DeleteDirectoryRemovesReadOnlyContentAtEveryDepth()
    {
        var d = Dir("nested");
        var inner = Path.Combine(d, "wems", "deeper");
        Directory.CreateDirectory(inner);
        var f = Path.Combine(inner, "822411253.wem");
        File.WriteAllText(f, "x");
        new FileInfo(f).Attributes |= FileAttributes.ReadOnly;
        new DirectoryInfo(inner).Attributes |= FileAttributes.ReadOnly;
        new DirectoryInfo(Path.Combine(d, "wems")).Attributes |= FileAttributes.ReadOnly;
        new DirectoryInfo(d).Attributes |= FileAttributes.ReadOnly;

        FileOps.DeleteDirectory(d);
        Assert.False(Directory.Exists(d));
    }

    [Fact]
    public void DeleteFileRemovesAReadOnlyFile()
    {
        var f = Path.Combine(_root, "readonly.pak");
        File.WriteAllText(f, "x");
        new FileInfo(f).Attributes |= FileAttributes.ReadOnly;

        FileOps.DeleteFile(f);
        Assert.False(File.Exists(f));
    }

    [Fact]
    public void WriteAllTextOverwritesAReadOnlyFile()
    {
        // manifest.json and settings.json are rewritten in place on every save.
        var f = Path.Combine(_root, "manifest.json");
        File.WriteAllText(f, "old");
        new FileInfo(f).Attributes |= FileAttributes.ReadOnly;

        FileOps.WriteAllText(f, "new");
        Assert.Equal("new", File.ReadAllText(f));
    }

    [Fact]
    public void CopyOverwritesAReadOnlyDestination()
    {
        var src = Path.Combine(_root, "src.pak");
        var dst = Path.Combine(_root, "dst.pak");
        File.WriteAllText(src, "fresh");
        File.WriteAllText(dst, "stale");
        new FileInfo(dst).Attributes |= FileAttributes.ReadOnly;

        FileOps.Copy(src, dst);
        Assert.Equal("fresh", File.ReadAllText(dst));
    }

    [Fact]
    public void CreateTruncatesAReadOnlyFile()
    {
        var f = Path.Combine(_root, "out.wem");
        File.WriteAllText(f, "stale bytes");
        new FileInfo(f).Attributes |= FileAttributes.ReadOnly;

        using (var fs = FileOps.Create(f)) fs.WriteByte(0x52);
        Assert.Equal(1, new FileInfo(f).Length);
    }

    [Fact]
    public void DeletingSomethingAbsentIsNotAnError()
    {
        FileOps.DeleteDirectory(Path.Combine(_root, "never-existed"));
        FileOps.DeleteFile(Path.Combine(_root, "never-existed.txt"));
    }

    [Fact]
    public void AHeldFileReportsSomethingActionable()
    {
        var f = Path.Combine(_root, "locked.pak");
        File.WriteAllText(f, "x");
        using var hold = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.None);

        var e = Assert.Throws<TmmException>(() => FileOps.DeleteFile(f, attempts: 2));
        Assert.Contains("in use", e.Message);
    }
}
