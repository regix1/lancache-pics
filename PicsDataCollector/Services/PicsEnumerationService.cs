using SteamKit2;

namespace PicsDataCollector.Services;

public class PicsEnumerationService
{
    private readonly SteamConnectionService _connectionService;

    public PicsEnumerationService(SteamConnectionService connectionService)
    {
        _connectionService = connectionService;
    }

    public async Task<List<uint>> EnumerateAllAppIdsAsync(bool incrementalOnly, uint lastChangeNumberSeen)
    {
        // For full update with no existing data, use Steam Web API
        if (!incrementalOnly && lastChangeNumberSeen == 0)
        {
            Console.WriteLine("Full update mode: Using Steam Web API to get all app IDs");
            var webApiApps = await GetAllAppIdsFromWebApiAsync();
            Console.WriteLine($"Retrieved {webApiApps.Count} app IDs from Steam Web API");
            return webApiApps;
        }

        // For TRUE incremental updates, use proper PICS change tracking
        if (incrementalOnly && lastChangeNumberSeen > 0)
        {
            Console.WriteLine($"True incremental update: Only fetching apps changed since #{lastChangeNumberSeen}");

            var job = _connectionService.Apps.PICSGetChangesSince(lastChangeNumberSeen, sendAppChangelist: true, sendPackageChangelist: false);
            var changes = await WaitForCallbackAsync(job);

            if (changes.RequiresFullUpdate || changes.RequiresFullAppUpdate)
            {
                Console.WriteLine($"⚠️  PICS requires full update - change delta too large!");
                Console.WriteLine($"   Falling back to Web API for full app list");
                return await GetAllAppIdsFromWebApiAsync();
            }

            // Return only the changed apps
            var changedAppIds = changes.AppChanges.Keys.ToList();
            Console.WriteLine($"Found {changedAppIds.Count} apps with changes since #{lastChangeNumberSeen}");
            Console.WriteLine($"Current change number: #{changes.CurrentChangeNumber}");

            return changedAppIds;
        }

        // For partial/fallback enumeration (shouldn't normally hit this)
        Console.WriteLine("Partial enumeration mode (fallback) - using Web API");
        return await GetAllAppIdsFromWebApiAsync();
    }

    private async Task<List<uint>> GetAllAppIdsFromWebApiAsync()
    {
        // Try v2 first (no API key required), fall back to v1 if it fails
        try
        {
            Console.WriteLine("Attempting to fetch app list via ISteamApps/GetAppList/v2 (no API key)...");
            return await GetAppListV2Async();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Web API v2 failed: {ex.Message}");
            Console.WriteLine("Falling back to IStoreService/GetAppList/v1 (requires API key)...");
            return await GetAppListV1Async();
        }
    }

    private async Task<List<uint>> GetAppListV2Async()
    {
        using (var steamApps = WebAPI.GetAsyncInterface("ISteamApps"))
        {
            // Call GetAppList v2 (no API key needed)
            var response = await steamApps.CallAsync(
                System.Net.Http.HttpMethod.Get,
                "GetAppList",
                version: 2
            );

            // Parse the response
            var appList = response["applist"]["apps"];
            var ids = new List<uint>(appList.Children.Count);

            foreach (var app in appList.Children)
            {
                uint appId = app["appid"].AsUnsignedInteger();
                if (appId > 0)
                {
                    ids.Add(appId);
                }
            }

            ids.Sort();
            Console.WriteLine($"Successfully retrieved {ids.Count} apps from ISteamApps/GetAppList/v2");
            return ids;
        }
    }

    private async Task<List<uint>> GetAppListV1Async()
    {
        // Get Steam API key from environment variable
        var apiKey = Environment.GetEnvironmentVariable("STEAM_API_KEY");
        if (string.IsNullOrEmpty(apiKey))
        {
            throw new InvalidOperationException("STEAM_API_KEY environment variable not set. Required for IStoreService/GetAppList/v1");
        }

        using (var storeService = WebAPI.GetAsyncInterface("IStoreService", apiKey))
        {
            var allIds = new HashSet<uint>();
            uint? lastAppId = null;
            bool haveMoreResults = true;
            int pageCount = 0;

            while (haveMoreResults)
            {
                pageCount++;
                Console.WriteLine($"Fetching page {pageCount} (starting from appid: {lastAppId?.ToString() ?? "0"})...");

                // Build parameters
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
                    if (appId > 0)
                    {
                        allIds.Add(appId);
                    }
                }

                // Check for more results
                haveMoreResults = response["have_more_results"].AsBoolean();
                if (haveMoreResults)
                {
                    lastAppId = response["last_appid"].AsUnsignedInteger();
                }

                Console.WriteLine($"Page {pageCount}: Retrieved {apps.Children.Count} apps (total so far: {allIds.Count})");
            }

            var ids = allIds.ToList();
            ids.Sort();
            Console.WriteLine($"Successfully retrieved {ids.Count} apps from IStoreService/GetAppList/v1");
            return ids;
        }
    }

    private async Task<T> WaitForCallbackAsync<T>(AsyncJob<T> job, TimeSpan? timeout = null) where T : CallbackMsg
    {
        var tcs = new TaskCompletionSource<T>();
        var jobId = job.JobID;

        Action<T>? handler = null;
        handler = callback =>
        {
            if (callback.JobID == jobId)
            {
                tcs.TrySetResult(callback);
            }
        };

        using var subscription = _connectionService.CallbackManager.Subscribe(handler!);
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(5));

        while (!tcs.Task.IsCompleted && !cts.Token.IsCancellationRequested)
        {
            _connectionService.CallbackManager.RunWaitCallbacks(TimeSpan.FromMilliseconds(50));
            await Task.Delay(10);
        }

        return await tcs.Task;
    }
}
