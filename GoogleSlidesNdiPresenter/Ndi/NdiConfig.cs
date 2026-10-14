using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GoogleSlidesNdiPresenter.Ndi;

/// <summary>
/// Points this app's NDI senders at a discovery server.
/// The NDI runtime reads its settings from ndi-config.v1.json; NDI_CONFIG_DIR makes it read ours
/// instead of the machine-wide one. Ours starts from the machine-wide config (as set in NDI Access
/// Manager), so groups, adapters, multicast and so on are kept, and only the discovery server changes.
/// Other NDI apps on the PC aren't affected.
/// </summary>
public static partial class NdiConfig
{
    private const string FileName = "ndi-config.v1.json";

    [GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9\-\.]*[A-Za-z0-9])?(:\d{1,5})?$")]
    private static partial Regex ServerRegex();

    /// <summary>Normalises "a, b ,c" to "a,b,c"; returns null if any entry isn't a host or host:port.</summary>
    public static string? Normalise(string? servers)
    {
        var parts = (servers ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            if (!ServerRegex().IsMatch(part)) return null;
            var colon = part.LastIndexOf(':');
            if (colon > 0 && int.Parse(part[(colon + 1)..]) is < 1 or > 65535) return null;
        }
        return string.Join(',', parts);
    }

    /// <summary>
    /// Must run before NDI is initialised. With no discovery server set, nothing is changed and NDI
    /// uses the machine-wide settings (normally automatic mDNS discovery).
    /// </summary>
    public static void Apply(string? discoveryServers)
    {
        var servers = Normalise(discoveryServers) ?? "";
        if (servers.Length == 0) return;

        var config = LoadMachineConfig() ?? new JsonObject();
        var ndi = config["ndi"] as JsonObject ?? (JsonObject)(config["ndi"] = new JsonObject());
        var networks = ndi["networks"] as JsonObject ?? (JsonObject)(ndi["networks"] = new JsonObject());
        networks["discovery"] = servers;

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GoogleSlidesNdiPresenter", "ndi");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, FileName), config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Environment.SetEnvironmentVariable("NDI_CONFIG_DIR", dir);
    }

    private static JsonObject? LoadMachineConfig()
    {
        // The per-user file wins over the machine one, as it does for the NDI runtime itself.
        string?[] candidates =
        [
            Environment.GetEnvironmentVariable("NDI_CONFIG_DIR") is { Length: > 0 } d ? Path.Combine(d, FileName) : null,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NDI", FileName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NDI", FileName),
        ];
        foreach (var path in candidates)
        {
            try
            {
                if (path != null && File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject obj)
                    return obj;
            }
            catch
            {
                // Unreadable config: fall through to the next one, or start empty.
            }
        }
        return null;
    }
}
