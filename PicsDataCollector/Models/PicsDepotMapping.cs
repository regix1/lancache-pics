namespace PicsDataCollector.Models;

public class PicsDepotMapping
{
    public uint? OwnerId { get; set; }  // The app that owns this depot (from depotfromapp PICS field)
    public List<uint>? AppIds { get; set; }
    public List<string>? AppNames { get; set; }
    public List<string>? AppTypes { get; set; }  // App types (game, dlc, demo, etc.)
    public List<string>? AppHeaderImages { get; set; }  // Header image URLs for each app
    public string Source { get; set; } = "SteamKit2-PICS";
    public DateTime DiscoveredAt { get; set; }
}
