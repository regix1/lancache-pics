using SteamKit2;
using System.Collections.Concurrent;

namespace PicsDataCollector.Services;

public class DepotMappingService
{
    private readonly SteamConnectionService _connectionService;
    private readonly ConcurrentDictionary<uint, HashSet<uint>> _depotToAppMappings = new();
    private readonly ConcurrentDictionary<uint, string> _appNames = new();

    private const int AppBatchSize = 200;

    public IReadOnlyDictionary<uint, HashSet<uint>> DepotMappings => _depotToAppMappings;
    public IReadOnlyDictionary<uint, string> AppNames => _appNames;

    public DepotMappingService(SteamConnectionService connectionService)
    {
        _connectionService = connectionService;
    }

    public void LoadExistingMappings(Dictionary<uint, HashSet<uint>> mappings, Dictionary<uint, string> names)
    {
        foreach (var (depotId, appIds) in mappings)
        {
            var set = _depotToAppMappings.GetOrAdd(depotId, _ => new HashSet<uint>());
            foreach (var appId in appIds)
            {
                set.Add(appId);
            }
        }

        foreach (var (appId, name) in names)
        {
            _appNames.TryAdd(appId, name);
        }
    }

    public async Task BuildDepotIndexAsync(List<uint> appIds)
    {
        var batches = appIds.Chunk(AppBatchSize).ToList();
        int processedBatches = 0;

        Console.WriteLine($"Processing {batches.Count} batches of apps...");

        foreach (var batch in batches)
        {
            try
            {
                // Get access tokens
                var tokensJob = _connectionService.Apps.PICSGetAccessTokens(batch, Enumerable.Empty<uint>());
                var tokens = await WaitForCallbackAsync(tokensJob);

                // Prepare product info requests
                var appRequests = new List<SteamApps.PICSRequest>();
                foreach (var appId in batch)
                {
                    var request = new SteamApps.PICSRequest(appId);
                    if (tokens.AppTokens.TryGetValue(appId, out var token))
                    {
                        request.AccessToken = token;
                    }
                    appRequests.Add(request);
                }

                // Get product info
                var productJob = _connectionService.Apps.PICSGetProductInfo(appRequests, Enumerable.Empty<SteamApps.PICSRequest>());
                var productCallbacks = await WaitForAllProductInfoAsync(productJob);

                // Process apps
                foreach (var cb in productCallbacks)
                {
                    foreach (var app in cb.Apps.Values)
                    {
                        ProcessAppDepots(app);
                    }
                }

                processedBatches++;
                if (processedBatches % 10 == 0)
                {
                    var percent = (processedBatches * 100.0 / batches.Count);
                    Console.WriteLine($"Progress: {processedBatches}/{batches.Count} batches ({percent:F1}%) - {_depotToAppMappings.Count} depot mappings found");
                }

                await Task.Delay(150); // Rate limiting
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Failed to process batch {processedBatches + 1}: {ex.Message}");
            }
        }

        Console.WriteLine($"Completed! Found {_depotToAppMappings.Count} depot mappings");
    }

    private void ProcessAppDepots(SteamApps.PICSProductInfoCallback.PICSProductInfo app)
    {
        try
        {
            var appId = app.ID;
            var kv = app.KeyValues;

            var appinfo = kv["appinfo"];
            var common = appinfo != KeyValue.Invalid ? appinfo["common"] : kv["common"];
            var depots = appinfo != KeyValue.Invalid ? appinfo["depots"] : kv["depots"];

            var appName = common?["name"]?.AsString() ?? $"App {appId}";
            _appNames[appId] = appName;

            if (depots == KeyValue.Invalid)
            {
                return;
            }

            foreach (var child in depots.Children)
            {
                if (!uint.TryParse(child.Name, out var depotId))
                    continue;

                var ownerFromPics = AsUInt(child["depotfromapp"]);
                var ownerAppId = ownerFromPics ?? appId;

                // Skip suspicious self-mappings
                if (depotId == ownerAppId)
                    continue;

                var set = _depotToAppMappings.GetOrAdd(depotId, _ => new HashSet<uint>());
                set.Add(ownerAppId);

                // Store owner app name
                if (ownerFromPics.HasValue && !_appNames.ContainsKey(ownerAppId))
                {
                    _appNames[ownerAppId] = $"App {ownerAppId}";
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Error processing app {app.ID}: {ex.Message}");
        }
    }

    private static uint? AsUInt(KeyValue kv)
    {
        if (kv == KeyValue.Invalid || kv.Value == null) return null;
        if (uint.TryParse(kv.AsString() ?? string.Empty, out var v)) return v;
        return null;
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

    private async Task<IReadOnlyList<SteamApps.PICSProductInfoCallback>> WaitForAllProductInfoAsync(
        AsyncJobMultiple<SteamApps.PICSProductInfoCallback> job)
    {
        var callbacks = new List<SteamApps.PICSProductInfoCallback>();
        var jobId = job.JobID;
        var isCompleted = false;

        Action<SteamApps.PICSProductInfoCallback>? handler = null;
        handler = callback =>
        {
            if (callback.JobID == jobId)
            {
                callbacks.Add(callback);
                if (!callback.ResponsePending)
                {
                    isCompleted = true;
                }
            }
        };

        using var subscription = _connectionService.CallbackManager.Subscribe(handler!);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));

        while (!isCompleted && !cts.Token.IsCancellationRequested)
        {
            _connectionService.CallbackManager.RunWaitCallbacks(TimeSpan.FromMilliseconds(100));
            await Task.Delay(10);
        }

        return callbacks.AsReadOnly();
    }
}
