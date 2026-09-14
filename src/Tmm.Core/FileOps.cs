namespace Tmm.Core;

/// <summary>
/// Filesystem helpers that survive a cloud-synced app folder.
///
/// OneDrive (and Dropbox, and Google Drive) mark synced files and folders ReadOnly and turn them
/// into reparse points. <see cref="Directory.Delete(string, bool)"/> refuses to remove a directory
/// carrying the ReadOnly attribute and reports "Access to the path ... is denied", which is why a
/// rebuild of a mod stored under OneDrive failed while the same operation worked elsewhere. The
/// attribute has to be cleared first — on the directory, and on everything inside it.
///
/// Sync clients also hold brief handles on files they are uploading, so each operation is retried a
/// few times before giving up. Portable installs live wherever the user unzipped them, which for
/// most people is inside OneDrive, so this is the normal case rather than an edge case.
/// </summary>
public static class FileOps
{
    private const int DefaultAttempts = 4;

    /// <summary>Recursive delete that clears ReadOnly first and retries a held handle.</summary>
    public static void DeleteDirectory(string path, int attempts = DefaultAttempts)
        => Retry(() =>
        {
            if (!Directory.Exists(path)) return;
            ClearReadOnly(new DirectoryInfo(path));
            Directory.Delete(path, recursive: true);
        }, attempts, path);

    /// <summary>Delete that clears ReadOnly first and retries a held handle.</summary>
    public static void DeleteFile(string path, int attempts = DefaultAttempts)
        => Retry(() =>
        {
            if (!File.Exists(path)) return;
            ClearReadOnly(new FileInfo(path));
            File.Delete(path);
        }, attempts, path);

    /// <summary>Make <paramref name="path"/> writable if it already exists, so an overwrite of a
    /// synced file does not fail. Call before any File.Create / WriteAllText / Copy(overwrite).</summary>
    public static void PrepareWrite(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        try
        {
            var fi = new FileInfo(path);
            if (fi.Exists) ClearReadOnly(fi);
        }
        catch (IOException) { /* nothing there to unlock */ }
        catch (UnauthorizedAccessException) { /* the write itself will report it properly */ }
    }

    public static void WriteAllText(string path, string contents)
        => Retry(() => { PrepareWrite(path); File.WriteAllText(path, contents); }, DefaultAttempts, path);

    public static void Copy(string source, string dest, bool overwrite = true)
        => Retry(() => { if (overwrite) PrepareWrite(dest); File.Copy(source, dest, overwrite); }, DefaultAttempts, dest);

    /// <summary>Rename or move, clearing ReadOnly on an existing destination first.</summary>
    public static void Move(string source, string dest)
        => Retry(() => { PrepareWrite(dest); File.Move(source, dest, overwrite: true); }, DefaultAttempts, dest);

    /// <summary>Create a file for writing, clearing ReadOnly on an existing one first.</summary>
    public static FileStream Create(string path)
    {
        PrepareWrite(path);
        return File.Create(path);
    }

    /// <summary>Turn a raw filesystem exception into something a user can act on. The framework's own
    /// message names the path but not the cause, and on a synced folder the cause is almost always a
    /// sync client holding or write-protecting the file.</summary>
    public static string Explain(Exception e) => e switch
    {
        TmmException t => t.Message,
        UnauthorizedAccessException =>
            $"{e.Message}\n\nIf the app folder is inside OneDrive or another sync client, pause syncing and try " +
            "again, or move the app somewhere that is not synced.",
        IOException =>
            $"{e.Message}\n\nSomething else is holding that file. Close the game, Explorer windows on the mods " +
            "folder, and any sync client, then try again.",
        _ => e.Message,
    };

    private static void ClearReadOnly(FileInfo f)
    {
        if ((f.Attributes & FileAttributes.ReadOnly) != 0) f.Attributes &= ~FileAttributes.ReadOnly;
    }

    private static void ClearReadOnly(DirectoryInfo d)
    {
        if ((d.Attributes & FileAttributes.ReadOnly) != 0) d.Attributes &= ~FileAttributes.ReadOnly;
        // Enumerate without following the reparse points the sync client leaves on its placeholders.
        foreach (var sub in d.EnumerateDirectories())
        {
            if ((sub.Attributes & FileAttributes.ReparsePoint) != 0 && (sub.Attributes & FileAttributes.Directory) == 0) continue;
            ClearReadOnly(sub);
        }
        foreach (var f in d.EnumerateFiles()) ClearReadOnly(f);
    }

    private static void Retry(Action act, int attempts, string path)
    {
        for (int i = 1; ; i++)
        {
            try { act(); return; }
            catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && i < attempts)
            {
                // A sync client or virus scanner usually lets go within a few hundred milliseconds.
                Thread.Sleep(100 * i);
            }
            catch (UnauthorizedAccessException e)
            {
                throw new TmmException(
                    $"Access to '{path}' is denied. If the app folder is inside OneDrive or another sync client, " +
                    "pause syncing and try again, or move the app somewhere that is not synced.", e);
            }
            catch (IOException e)
            {
                throw new TmmException(
                    $"'{path}' is in use and could not be replaced. Close anything using it — the game, Explorer, " +
                    "or a sync client — and try again.", e);
            }
        }
    }
}
