using System.Text.Json;
using PicsDataCollector.Models;

namespace PicsDataCollector.Services;

public class DataPersistenceService
{
    private readonly string _outputFilePath;

    public DataPersistenceService()
    {
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
        Dictionary<uint, uint>? depotOwners = null)
    {
        Console.WriteLine();
        Console.WriteLine("Saving to JSON...");

        var picsData = new PicsJsonData
        {
            Metadata = new PicsMetadata
            {
                LastUpdated = DateTime.UtcNow,
                TotalMappings = depotMappings.Sum(kvp => kvp.Value.Count),
                Version = "1.0",
                NextUpdateDue = DateTime.UtcNow.AddDays(2),
                LastChangeNumber = lastChangeNumber
            },
            DepotMappings = new Dictionary<string, PicsDepotMapping>()
        };

        foreach (var (depotId, appIds) in depotMappings)
        {
            // Ensure owner app is first in the list
            var appIdsList = new List<uint>();
            uint? ownerId = null;

            if (depotOwners != null && depotOwners.TryGetValue(depotId, out var ownerAppId) && appIds.Contains(ownerAppId))
            {
                ownerId = ownerAppId;
                // Add owner first
                appIdsList.Add(ownerAppId);
                // Add remaining apps
                appIdsList.AddRange(appIds.Where(id => id != ownerAppId));
            }
            else
            {
                // No owner tracked, just convert as-is
                appIdsList = appIds.ToList();
            }

            var appNamesList = appIdsList.Select(appId =>
                appNames.TryGetValue(appId, out var name) ? name : $"App {appId}"
            ).ToList();

            picsData.DepotMappings[depotId.ToString()] = new PicsDepotMapping
            {
                OwnerId = ownerId,
                AppIds = appIdsList,
                AppNames = appNamesList,
                Source = "SteamKit2-PICS",
                DiscoveredAt = DateTime.UtcNow
            };
        }

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
        Console.WriteLine($"Total mappings: {picsData.Metadata.TotalMappings}");
    }

    public (Dictionary<uint, HashSet<uint>> depotMappings, Dictionary<uint, string> appNames, Dictionary<uint, uint> depotOwners) ExtractMappingsFromData(PicsJsonData? data)
    {
        var depotMappings = new Dictionary<uint, HashSet<uint>>();
        var appNames = new Dictionary<uint, string>();
        var depotOwners = new Dictionary<uint, uint>();

        if (data?.DepotMappings == null)
        {
            return (depotMappings, appNames, depotOwners);
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

            // Extract owner ID if available
            if (mapping.OwnerId.HasValue)
            {
                depotOwners[depotId] = mapping.OwnerId.Value;
            }
            else if (mapping.AppIds != null && mapping.AppIds.Count > 0)
            {
                // Fallback: Use first app ID as owner if not explicitly set
                depotOwners[depotId] = mapping.AppIds[0];
            }

            if (mapping.AppNames != null && mapping.AppIds != null)
            {
                for (int i = 0; i < Math.Min(mapping.AppIds.Count, mapping.AppNames.Count); i++)
                {
                    appNames.TryAdd(mapping.AppIds[i], mapping.AppNames[i]);
                }
            }
        }

        return (depotMappings, appNames, depotOwners);
    }
}
