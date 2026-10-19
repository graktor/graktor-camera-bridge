using System.Text.Json;

namespace ZendoriaCameraBridge;

/// <summary>
/// Everything this installation needs to remember between runs, stored next to the exe as
/// plain JSON. AgentKey is the one genuinely sensitive value here -- it's this specific
/// PC's own credential for its own branch's cameras, nothing more, so plain-text-on-disk is
/// an acceptable tradeoff for a program that has no other secure-storage story on Windows
/// without a much bigger dependency (DPAPI works, but ties the file to one Windows user
/// profile, which would break a "run as a scheduled task under a service account" setup).
/// </summary>
public class BridgeConfig
{
    public string ApiBaseUrl { get; set; } = "https://app.graktor.com";
    public int AgentId { get; set; }
    public string AgentKey { get; set; } = "";

    private static string PathOnDisk => Path.Combine(AppContext.BaseDirectory, "bridge-config.json");

    public static BridgeConfig? Load()
    {
        if (!File.Exists(PathOnDisk)) return null;
        try
        {
            return JsonSerializer.Deserialize<BridgeConfig>(File.ReadAllText(PathOnDisk));
        }
        catch
        {
            return null;
        }
    }

    public void Save()
    {
        File.WriteAllText(PathOnDisk, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public bool IsPaired => AgentId > 0 && !string.IsNullOrWhiteSpace(AgentKey);
}
