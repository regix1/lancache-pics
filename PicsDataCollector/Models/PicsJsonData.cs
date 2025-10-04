namespace PicsDataCollector.Models;

public class PicsJsonData
{
    public PicsMetadata? Metadata { get; set; }
    public Dictionary<string, PicsDepotMapping>? DepotMappings { get; set; }
}
