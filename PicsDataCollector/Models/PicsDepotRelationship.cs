namespace PicsDataCollector.Models;

public class PicsDepotRelationship
{
    public uint SourceAppId { get; set; }
    public uint? DepotFromAppId { get; set; }
    public uint? DlcAppId { get; set; }
    public bool HasPublicManifest { get; set; }
    public uint ManifestAppId { get; set; }
    public uint LicenseAppId { get; set; }
}
