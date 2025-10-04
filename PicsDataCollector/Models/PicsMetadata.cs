namespace PicsDataCollector.Models;

public class PicsMetadata
{
    public DateTime LastUpdated { get; set; }
    public int TotalMappings { get; set; }
    public string Version { get; set; } = "1.0";
    public DateTime NextUpdateDue { get; set; }
    public uint LastChangeNumber { get; set; }
}
