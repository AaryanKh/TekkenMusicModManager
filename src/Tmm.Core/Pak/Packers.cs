using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Tmm.Core.Pak;

/// <summary>Pack a staged folder into a loose .pak. No compression, no IoStore (corpus: 22/22).</summary>
public interface IPacker
{
    string Name { get; }
    string Pack(string stagedRoot, string outPak);
    /// <summary>Internal file paths; used for conflict detection + sanity.</summary>
    IReadOnlyList<string> List(string pak);
}

/// <summary>
/// Wraps the community UnrealPak build (fluffyquack). Response-file invocation:
///   UnrealPak.exe &lt;out.pak&gt; -create=&lt;filelist.txt&gt;
/// where each line is  "&lt;abs path&gt;" "../../../Polaris/Content/WwiseAudio/Media/&lt;ID&gt;.wem"
/// </summary>
public sealed class UnrealPakPacker : IPacker
{
    private readonly string _exe;
    public string Name => "UnrealPak";
    public UnrealPakPacker(string exe) => _exe = exe;

    public string Pack(string stagedRoot, string outPak)
    {
        if (!File.Exists(_exe)) throw new PackException($"UnrealPak.exe not found at '{_exe}'. Set the path in Settings.");
        var media = Path.Combine(stagedRoot, Constants.WemMediaRelative);
        var files = Directory.Exists(media) ? Directory.GetFiles(media, "*.wem") : Array.Empty<string>();
        if (files.Length == 0) throw new PackException($"nothing to pack under {media}");

        var listPath = Path.Combine(stagedRoot, "filelist.txt");
        var sb = new StringBuilder();
        foreach (var f in files)
            sb.Append('"').Append(Path.GetFullPath(f)).Append("\" \"../../../")
              .Append(Constants.WemMediaPakPath).Append('/').Append(Path.GetFileName(f)).Append("\"\r\n");
        FileOps.PrepareWrite(listPath);
        File.WriteAllText(listPath, sb.ToString(), new UTF8Encoding(false));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPak))!);
        FileOps.DeleteFile(outPak);
        var (code, output) = Run(_exe, new[] { Path.GetFullPath(outPak), $"-create={listPath}" });
        if (code != 0 || !File.Exists(outPak))
            throw new PackException($"UnrealPak failed (exit {code}):\n{Tail(output)}");
        return outPak;
    }

    private static readonly Regex ListLine = new(@"""([^""]+\.wem)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public IReadOnlyList<string> List(string pak)
    {
        if (!File.Exists(_exe)) return PakScan.FindWemPaths(pak);
        var (code, output) = Run(_exe, new[] { Path.GetFullPath(pak), "-List" });
        if (code != 0) return PakScan.FindWemPaths(pak);
        var paths = new List<string>();
        foreach (Match m in ListLine.Matches(output)) paths.Add(m.Groups[1].Value.Replace('\\', '/'));
        return paths.Count > 0 ? paths : PakScan.FindWemPaths(pak);
    }

    internal static (int code, string output) Run(string exe, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi) ?? throw new PackException($"could not start {exe}");
            var errTask = p.StandardError.ReadToEndAsync();
            var outText = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode, outText + errTask.Result);
        }
        catch (Win32Exception e)
        {
            throw new PackException($"could not run '{exe}': {e.Message}", e);
        }
    }

    private static string Tail(string s)
    {
        var lines = s.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join('\n', lines.TakeLast(12));
    }
}

/// <summary>
/// repak (Rust, permissive licence) — embeddable, no Windows-only binary. Needs an in-game
/// confirmation that Tekken 8 mounts its output before it becomes the default.
///   repak pack --version V11 &lt;staged_root&gt; &lt;out.pak&gt;
/// </summary>
public sealed class RepakPacker : IPacker
{
    private readonly string _exe;
    public string Name => "repak";
    public RepakPacker(string exe) => _exe = exe;

    public string Pack(string stagedRoot, string outPak)
    {
        if (!File.Exists(_exe)) throw new PackException($"repak not found at '{_exe}'. Set the path in Settings.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPak))!);
        var (code, output) = UnrealPakPacker.Run(_exe, new[] { "pack", "--version", "V11", Path.GetFullPath(stagedRoot), Path.GetFullPath(outPak) });
        if (code != 0 || !File.Exists(outPak)) throw new PackException($"repak failed (exit {code}):\n{output}");
        return outPak;
    }

    public IReadOnlyList<string> List(string pak)
    {
        if (!File.Exists(_exe)) return PakScan.FindWemPaths(pak);
        var (code, output) = UnrealPakPacker.Run(_exe, new[] { "list", Path.GetFullPath(pak) });
        if (code != 0) return PakScan.FindWemPaths(pak);
        return output.Split('\n').Select(l => l.Trim().Replace('\\', '/')).Where(l => l.EndsWith(".wem", StringComparison.OrdinalIgnoreCase)).ToList();
    }
}

/// <summary>
/// Tool-free listing: scans a pak's bytes for "WwiseAudio/Media/&lt;id&gt;.wem". Loose mod paks are
/// unencrypted and uncompressed, so their index stores the paths as plain ASCII/UTF-16 strings.
/// Used for third-party paks in ~mods and whenever the packer executable is missing.
/// </summary>
public static class PakScan
{
    private static readonly Regex Ascii = new(@"WwiseAudio/Media/(\d+)\.wem", RegexOptions.Compiled);

    public static IReadOnlyList<string> FindWemPaths(string pak)
    {
        var ids = FindWemIds(pak);
        return ids.Select(i => $"{Constants.WemMediaPakPath}/{i}.wem").ToList();
    }

    public static IReadOnlyList<int> FindWemIds(string pak)
    {
        if (!File.Exists(pak)) return Array.Empty<int>();
        var bytes = File.ReadAllBytes(pak);
        var found = new SortedSet<int>();

        // ASCII / UTF-8 strings
        var ascii = Encoding.Latin1.GetString(bytes).Replace('\\', '/');
        foreach (Match m in Ascii.Matches(ascii))
            if (int.TryParse(m.Groups[1].Value, out var id)) found.Add(id);

        // UTF-16LE strings (UE writes non-ANSI names as UTF-16; cheap to check both)
        if ((bytes.Length & 1) == 0)
        {
            var utf16 = Encoding.Unicode.GetString(bytes).Replace('\\', '/');
            foreach (Match m in Ascii.Matches(utf16))
                if (int.TryParse(m.Groups[1].Value, out var id)) found.Add(id);
        }
        return found.ToList();
    }
}

public static class PackerFactory
{
    public static IPacker FromSettings(Settings s) =>
        s.Packer.Equals("repak", StringComparison.OrdinalIgnoreCase)
            ? new RepakPacker(s.RepakPath ?? "repak")
            : new UnrealPakPacker(s.UnrealPakPath ?? "UnrealPak.exe");
}
