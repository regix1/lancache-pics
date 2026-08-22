using System.Text.Json;
using PicsDataCollector.Models;

namespace PicsDataCollector.Services;

public class DataPersistenceService
{
    private readonly string _outputFilePath;

    public DataPersistenceService(string? outputFilePath = null)
    {
        if (!string.IsNullOrWhiteSpace(outputFilePath))
        {
            _outputFilePath = outputFilePath;
            return;
        }

        // Save to output directory in repository root
        var baseDir = AppContext.BaseDirectory;
        var projectDir = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".."));
        var outputDir = Path.Combine(projectDir, "output");
        _outputFilePath = Path.Combine(outputDir, "pics_depot_mappings.json");
    }

    public async Task<(PicsJsonData? data, uint lastChangeNumber)> LoadExistingDataAsync()
    {
        try
        {
            if (!File.Exists(_outputFilePath))
            {
                Console.WriteLine($"No existing data file found at: {_outputFilePath}");
                return (null, 0);
            }

            var json = await File.ReadAllTextAsync(_outputFilePath);
            var data = JsonSerializer.Deserialize<PicsJsonData>(json, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            var lastChangeNumber = data?.Metadata?.LastChangeNumber ?? 0;

            if (data?.DepotMappings != null)
            {
                Console.WriteLine($"Loaded {data.Metadata?.TotalMappings ?? 0} existing mappings from {_outputFilePath}");
                Console.WriteLine($"Last change number: {lastChangeNumber}");
            }

            return (data, lastChangeNumber);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Failed to load existing data: {ex.Message}");
            return (null, 0);
        }
    }

    public async Task SaveToJsonAsync(
        Dictionary<uint, HashSet<uint>> depotMappings,
        Dictionary<uint, string> appNames,
        uint lastChangeNumber,
        Dictionary<uint, uint>? depotOwners = null,
        Dictionary<uint, string>? appTypes = null,
        Dictionary<uint, string>? appHeaderImages = null,
        string apiVersion = "PICS",
        Dictionary<uint, List<PicsDepotRelationship>>? depotRelationships = null)
    {
        Console.WriteLine();
        Console.WriteLine("Saving to JSON...");

        var picsData = BuildPicsData(
            depotMappings,
            appNames,
            lastChangeNumber,
            depotOwners,
            appTypes,
            appHeaderImages,
            apiVersion,
            depotRelationships);

        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        var jsonContent = JsonSerializer.Serialize(picsData, jsonOptions);

        // Ensure directory exists
        var directory = Path.GetDirectoryName(_outputFilePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(_outputFilePath, jsonContent);

        Console.WriteLine($"Saved to {_outputFilePath}");
        Console.WriteLine($"Total mappings: {picsData.Metadata!.TotalMappings}");
    }

    public PicsJsonData BuildPicsData(
        Dictionary<uint, HashSet<uint>> depotMappings,
        Dictionary<uint, string> appNames,
        uint lastChangeNumber,
        Dictionary<uint, uint>? depotOwners = null,
        Dictionary<uint, string>? appTypes = null,
        Dictionary<uint, string>? appHeaderImages = null,
        string apiVersion = "PICS",
        Dictionary<uint, List<PicsDepotRelationship>>? depotRelationships = null)
    {
        var picsData = new PicsJsonData
        {
            Metadata = new PicsMetadata
            {
                LastUpdated = DateTime.UtcNow,
                TotalMappings = depotMappings.Sum(kvp => kvp.Value.Count),
                Version = "1.1",
                NextUpdateDue = DateTime.UtcNow.AddDays(2),
                LastChangeNumber = lastChangeNumber
            },
            DepotMappings = new Dictionary<string, PicsDepotMapping>()
        };

        foreach (var (depotId, appIds) in depotMappings)
        {
            var appIdsList = new List<uint>();
            uint? ownerId = null;

            // A loaded file can carry an ownerId that is absent from its own appIds, and listing an app the depot
            // does not map to would invent a name, a type and a header image for it.
            if (depotOwners != null && depotOwners.TryGetValue(depotId, out var ownerAppId) && appIds.Contains(ownerAppId))
            {
                ownerId = ownerAppId;
                appIdsList.Add(ownerAppId);
                appIdsList.AddRange(appIds.Where(id => id != ownerAppId));
            }
            else
            {
                appIdsList = appIds.ToList();
            }

            var appNamesList = appIdsList.Select(appId =>
                appNames.TryGetValue(appId, out var name) ? name : $"App {appId}"
            ).ToList();

            var appTypesList = appIdsList.Select(appId =>
                appTypes != null && appTypes.TryGetValue(appId, out var type) ? type : "unknown"
            ).ToList();

            var appHeaderImagesList = appIdsList.Select(appId =>
                appHeaderImages != null && appHeaderImages.TryGetValue(appId, out var picsUrl)
                    ? picsUrl
                    : $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg"
            ).ToList();

            var source = apiVersion == "PICS" ? "SteamKit2-PICS" : $"SteamKit2-PICS-{apiVersion}";
            List<PicsDepotRelationship>? relationships = null;
            depotRelationships?.TryGetValue(depotId, out relationships);

            picsData.DepotMappings[depotId.ToString()] = new PicsDepotMapping
            {
                OwnerId = ownerId,
                AppIds = appIdsList,
                AppNames = appNamesList,
                AppTypes = appTypesList,
                AppHeaderImages = appHeaderImagesList,
                Relationships = relationships,
                Source = source,
                DiscoveredAt = DateTime.UtcNow
            };
        }

        return picsData;
    }

    public (Dictionary<uint, HashSet<uint>> depotMappings, Dictionary<uint, string> appNames, Dictionary<uint, uint> depotOwners, Dictionary<uint, string> appTypes, Dictionary<uint, string> appHeaderImages, Dictionary<uint, List<PicsDepotRelationship>> relationships) ExtractMappingsFromData(PicsJsonData? data)
    {
        var depotMappings = new Dictionary<uint, HashSet<uint>>();
        var appNames = new Dictionary<uint, string>();
        var depotOwners = new Dictionary<uint, uint>();
        var appTypes = new Dictionary<uint, string>();
        var appHeaderImages = new Dictionary<uint, string>();
        var relationships = new Dictionary<uint, List<PicsDepotRelationship>>();

        if (data?.DepotMappings == null)
        {
            return (depotMappings, appNames, depotOwners, appTypes, appHeaderImages, relationships);
        }

        foreach (var (depotIdStr, mapping) in data.DepotMappings)
        {
            if (!uint.TryParse(depotIdStr, out var depotId))
                continue;

            var set = new HashSet<uint>();
            if (mapping.AppIds != null)
            {
                foreach (var appId in mapping.AppIds)
                {
                    set.Add(appId);
                }
            }
            depotMappings[depotId] = set;

            // Extract owner ID if available. A file with no ownerId keeps none, so the next scan derives it
            // from the relationships instead of inheriting whatever app happened to be first in the list.
            if (mapping.OwnerId.HasValue)
            {
                depotOwners[depotId] = mapping.OwnerId.Value;
            }

            if (mapping.AppNames != null && mapping.AppIds != null)
            {
                for (int i = 0; i < Math.Min(mapping.AppIds.Count, mapping.AppNames.Count); i++)
                {
                    appNames.TryAdd(mapping.AppIds[i], mapping.AppNames[i]);
                }
            }

            // Extract app types if available
            if (mapping.AppTypes != null && mapping.AppIds != null)
            {
                for (int i = 0; i < Math.Min(mapping.AppIds.Count, mapping.AppTypes.Count); i++)
                {
                    appTypes.TryAdd(mapping.AppIds[i], mapping.AppTypes[i]);
                }
            }

            // Extract header images if available
            if (mapping.AppHeaderImages != null && mapping.AppIds != null)
            {
                for (int i = 0; i < Math.Min(mapping.AppIds.Count, mapping.AppHeaderImages.Count); i++)
                {
                    appHeaderImages.TryAdd(mapping.AppIds[i], mapping.AppHeaderImages[i]);
                }
            }

            if (mapping.Relationships is { Count: > 0 })
            {
                relationships[depotId] = mapping.Relationships;
            }
        }

        return (depotMappings, appNames, depotOwners, appTypes, appHeaderImages, relationships);
    }
}
