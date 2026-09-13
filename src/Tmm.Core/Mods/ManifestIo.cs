using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tmm.Core.Mods;

/// <summary>JSON (de)serialization of ModManifest. One file per mod under &lt;app_dir&gt;/mods/&lt;id&gt;/.</summary>
public static class ManifestIo
{
    public const string FileName = "manifest.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Save(ModManifest m, string modDir)
    {
        Directory.CreateDirectory(modDir);
        var path = Path.Combine(modDir, FileName);
        File.WriteAllText(path, JsonSerializer.Serialize(m, Options));
        return path;
    }

    public static ModManifest Load(string modDir)
    {
        var path = Path.Combine(modDir, FileName);
        try
        {
            return JsonSerializer.Deserialize<ModManifest>(File.ReadAllText(path), Options)
                   ?? throw new InstallException($"empty manifest: {path}");
        }
        catch (JsonException e)
        {
            throw new InstallException($"corrupt manifest {path}: {e.Message}");
        }
    }
}
