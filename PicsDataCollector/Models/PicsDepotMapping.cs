namespace PicsDataCollector.Models;

public class PicsDepotMapping
{
    public uint? OwnerId { get; set; }  // The app that owns this depot (from depotfromapp PICS field)
    public List<uint>? AppIds { get; set; }
    public List<string>? AppNames { get; set; }
    public string Source { get; set; } = "SteamKit2-PICS";
    public DateTime DiscoveredAt { get; set; }
}
