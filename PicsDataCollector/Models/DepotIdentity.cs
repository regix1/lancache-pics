namespace PicsDataCollector.Models;

public static class DepotIdentity
{
    private static uint GetManifestAppId(uint sourceAppId, uint depotId, uint? depotFromAppId, uint? dlcAppId)
    {
        if (IsUsableDepotFromApp(depotId, depotFromAppId, dlcAppId) && depotFromAppId.HasValue)
        {
            return depotFromAppId.Value;
        }

        return sourceAppId;
    }

    private static uint GetLicenseAppId(uint manifestAppId, uint? dlcAppId)
    {
        return dlcAppId ?? manifestAppId;
    }

    public static bool IsInvalidDepot(bool hasPublicManifest, uint depotId, uint? depotFromAppId)
    {
        return !hasPublicManifest && (depotFromAppId == null || depotFromAppId.Value == depotId);
    }

    public static bool IsUsableDepotFromApp(uint depotId, uint? depotFromAppId, uint? dlcAppId)
    {
        return depotFromAppId is uint fromApp
               && fromApp != depotId
               && fromApp != dlcAppId;
    }

    public static uint SelectOwnerId(
        IReadOnlyCollection<PicsDepotRelationship> relationships,
        IReadOnlyDictionary<uint, string> appTypes)
    {
        var explicitOwner = relationships
            .Where(r => r.DepotFromAppId is uint fromApp && fromApp != r.SourceAppId)
            .Select(r => r.DepotFromAppId!.Value)
            .Distinct()
            .OrderBy(id => id)
            .Cast<uint?>()
            .FirstOrDefault();
        if (explicitOwner.HasValue)
        {
            return explicitOwner.Value;
        }

        var gameSource = relationships
            .Select(r => r.SourceAppId)
            .Where(id => appTypes.TryGetValue(id, out var type) &&
                         string.Equals(type, "game", StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .OrderBy(id => id)
            .Cast<uint?>()
            .FirstOrDefault();
        if (gameSource.HasValue)
        {
            return gameSource.Value;
        }

        return relationships.Select(r => r.SourceAppId).DefaultIfEmpty().Min();
    }

    public static PicsDepotRelationship CreateRelationship(
        uint sourceAppId,
        uint depotId,
        uint? depotFromAppId,
        uint? dlcAppId,
        bool hasPublicManifest)
    {
        var manifestAppId = GetManifestAppId(sourceAppId, depotId, depotFromAppId, dlcAppId);
        return new PicsDepotRelationship
        {
            SourceAppId = sourceAppId,
            DepotFromAppId = depotFromAppId,
            DlcAppId = dlcAppId,
            HasPublicManifest = hasPublicManifest,
            ManifestAppId = manifestAppId,
            LicenseAppId = GetLicenseAppId(manifestAppId, dlcAppId)
        };
    }
}
