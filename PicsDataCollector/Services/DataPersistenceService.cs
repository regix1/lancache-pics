using System.Text.Json;
using PicsDataCollector.Models;

namespace PicsDataCollector.Services;

public class DataPersistenceService
{
    private readonly string _outputFilePath;

    public DataPersistenceService()
    {
        // Save to PicsDataCollector directory regardless of where app runs from
        var baseDir = AppContext.BaseDirectory;
        var projectDir = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".."));
        _outputFilePath = Path.Combine(projectDir, "PicsDataCollector", "pics_depot_mappings.json");
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
        uint lastChangeNumber)
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
            var appIdsList = appIds.ToList();
            var appNamesList = appIdsList.Select(appId =>
                appNames.TryGetValue(appId, out var name) ? name : $"App {appId}"
            ).ToList();

            picsData.DepotMappings[depotId.ToString()] = new PicsDepotMapping
            {
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

    public (Dictionary<uint, HashSet<uint>> depotMappings, Dictionary<uint, string> appNames) ExtractMappingsFromData(PicsJsonData? data)
    {
        var depotMappings = new Dictionary<uint, HashSet<uint>>();
        var appNames = new Dictionary<uint, string>();

        if (data?.DepotMappings == null)
        {
            return (depotMappings, appNames);
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

            if (mapping.AppNames != null && mapping.AppIds != null)
            {
                for (int i = 0; i < Math.Min(mapping.AppIds.Count, mapping.AppNames.Count); i++)
                {
                    appNames.TryAdd(mapping.AppIds[i], mapping.AppNames[i]);
                }
            }
        }

        return (depotMappings, appNames);
    }
}
