namespace PicsDataCollector.Models;

public class PicsDepotMapping
{
    public uint? OwnerId { get; set; }  // Deterministic owner: depotfromapp, else a game source, else a stable AppID
    public List<uint>? AppIds { get; set; }
    public List<string>? AppNames { get; set; }
    public List<string>? AppTypes { get; set; }  // App types (game, dlc, demo, etc.)
    public List<string>? AppHeaderImages { get; set; }  // Header image URLs for each app
    public List<PicsDepotRelationship>? Relationships { get; set; }
    public string Source { get; set; } = "SteamKit2-PICS";
    public DateTime DiscoveredAt { get; set; }
}
