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
            var allApps = new Dictionary<uint, SteamApp>();
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

                // Parse response and inspect what fields are available
                var apps = response["apps"];
                foreach (var app in apps.Children)
                {
                    uint appId = app["appid"].AsUnsignedInteger();
                    string name = app["name"].AsString() ?? "";

                    // Check all available fields in the response
                    string type = "unknown";

                    // Try different possible field names for type
                    if (app["type"] != null && !string.IsNullOrEmpty(app["type"].AsString()))
                    {
                        type = app["type"].AsString() ?? "unknown";
                    }
                    else if (app["app_type"] != null && !string.IsNullOrEmpty(app["app_type"].AsString()))
                    {
                        type = app["app_type"].AsString() ?? "unknown";
                    }
                    else if (app["application_type"] != null && !string.IsNullOrEmpty(app["application_type"].AsString()))
                    {
                        type = app["application_type"].AsString() ?? "unknown";
                    }

                    if (appId > 0)
                    {
                        // Use dictionary to avoid duplicates - keep first occurrence
                        if (!allApps.ContainsKey(appId))
                        {
                            allApps[appId] = new SteamApp
                            {
                                AppId = appId,
                                Name = name,
                                Type = type
                            };
                        }
                    }
                }

                // Check for more results
                haveMoreResults = response["have_more_results"].AsBoolean();
                if (haveMoreResults)
                {
                    lastAppId = response["last_appid"].AsUnsignedInteger();
                }

                Console.WriteLine($"  Page {pageCount}: Retrieved {apps.Children.Count} apps (total unique so far: {allApps.Count})");
            }

            var result = allApps.Values.ToList();
            Console.WriteLine($"Successfully retrieved {result.Count} unique apps from IStoreService/GetAppList/v1");
            return result;
        }
    }
}

public class SteamApp
{
    public uint AppId { get; set; }
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
}
