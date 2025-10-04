namespace PicsDataCollector.Models;

public class PicsDepotMapping
{
    public List<uint>? AppIds { get; set; }
    public List<string>? AppNames { get; set; }
    public string Source { get; set; } = "SteamKit2-PICS";
    public DateTime DiscoveredAt { get; set; }
}
