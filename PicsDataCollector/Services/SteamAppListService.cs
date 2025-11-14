using SteamKit2;

namespace PicsDataCollector.Services;

public class SteamAppListService
{
    public async Task<List<SteamApp>> GetAllAppsAsync()
    {
        // Get Steam API key from environment variable
        var apiKey = Environment.GetEnvironmentVariable("STEAM_API_KEY")
                     ?? Environment.GetEnvironmentVariable("STEAM_KEY");

        if (string.IsNullOrEmpty(apiKey))
        {
            throw new InvalidOperationException("STEAM_API_KEY or STEAM_KEY environment variable not set. Required for IStoreService/GetAppList/v1");
        }

        using (var storeService = WebAPI.GetAsyncInterface("IStoreService", apiKey))
        {
            var allApps = new List<SteamApp>();
            uint? lastAppId = null;
            bool haveMoreResults = true;
            int pageCount = 0;

            Console.WriteLine("Fetching Steam app list from IStoreService/GetAppList/v1...");

            while (haveMoreResults)
            {
                pageCount++;
                Console.WriteLine($"  Fetching page {pageCount} (starting from appid: {lastAppId?.ToString() ?? "0"})...");

                // Build parameters - fetch ALL types (games, DLC, software, videos, hardware)
                var parameters = new Dictionary<string, object?>
                {
                    { "include_games", "true" },
                    { "include_dlc", "true" },
                    { "include_software", "true" },
                    { "include_videos", "true" },
                    { "include_hardware", "true" },
                    { "max_results", "50000" }
                };

                if (lastAppId.HasValue)
                {
                    parameters["last_appid"] = lastAppId.Value.ToString();
                }

                // Call GetAppList v1 with pagination
                var response = await storeService.CallAsync(
                    System.Net.Http.HttpMethod.Get,
                    "GetAppList",
                    version: 1,
                    args: parameters
                );

                // Parse response
                var apps = response["apps"];
                foreach (var app in apps.Children)
                {
                    uint appId = app["appid"].AsUnsignedInteger();
                    string name = app["name"].AsString() ?? "";
                    string type = app["type"].AsString() ?? "unknown";

                    if (appId > 0)
                    {
                        allApps.Add(new SteamApp
                        {
                            AppId = appId,
                            Name = name,
                            Type = type
                        });
                    }
                }

                // Check for more results
                haveMoreResults = response["have_more_results"].AsBoolean();
                if (haveMoreResults)
                {
                    lastAppId = response["last_appid"].AsUnsignedInteger();
                }

                Console.WriteLine($"  Page {pageCount}: Retrieved {apps.Children.Count} apps (total so far: {allApps.Count})");
            }

            Console.WriteLine($"Successfully retrieved {allApps.Count} apps from IStoreService/GetAppList/v1");
            return allApps;
        }
    }
}

public class SteamApp
{
    public uint AppId { get; set; }
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
}
