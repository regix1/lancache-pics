using SteamKit2;
using System.Collections.Concurrent;

namespace PicsDataCollector.Services;

public class DepotMappingService
{
    private readonly SteamConnectionService _connectionService;
    private readonly ConcurrentDictionary<uint, HashSet<uint>> _depotToAppMappings = new();
    private readonly ConcurrentDictionary<uint, uint> _depotOwners = new();
    private readonly ConcurrentDictionary<uint, string> _appNames = new();
    private readonly ConcurrentDictionary<uint, string> _appTypes = new();
    private readonly HashSet<uint> _scannedApps = new();  // Track scanned apps to avoid rescanning

    private const int AppBatchSize = 500;

    public IReadOnlyDictionary<uint, HashSet<uint>> DepotMappings => _depotToAppMappings;
    public IReadOnlyDictionary<uint, uint> DepotOwners => _depotOwners;
    public IReadOnlyDictionary<uint, string> AppNames => _appNames;
    public IReadOnlyDictionary<uint, string> AppTypes => _appTypes;

    public DepotMappingService(SteamConnectionService connectionService)
    {
        _connectionService = connectionService;
    }

    public void LoadExistingMappings(Dictionary<uint, HashSet<uint>> mappings, Dictionary<uint, string> names, Dictionary<uint, uint>? depotOwners = null, Dictionary<uint, string>? types = null)
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

        if (depotOwners != null)
        {
            foreach (var (depotId, ownerId) in depotOwners)
            {
                _depotOwners.TryAdd(depotId, ownerId);
            }
        }

        if (types != null)
        {
            foreach (var (appId, type) in types)
            {
                _appTypes.TryAdd(appId, type);
            }
        }
    }

    public async Task BuildDepotIndexAsync(List<uint> appIds)
    {
        var allAppIds = new HashSet<uint>(appIds); // For fast DLC dedup
        var batches = appIds.Chunk(AppBatchSize).ToList();
        int processedBatches = 0;
        var semaphore = new SemaphoreSlim(3, 3); // Process 3 batches concurrently

        Console.WriteLine($"Processing {batches.Count} batches of apps (concurrency: 3)...");

        var tasks = batches.Select(async (batch, index) =>
        {
            await semaphore.WaitAsync();
            try
            {
                await ProcessBatchAsync(batch, allAppIds, index + 1);
                var completed = Interlocked.Increment(ref processedBatches);
                if (completed % 10 == 0)
                {
                    var percent = (completed * 100.0 / batches.Count);
                    Console.WriteLine($"Progress: {completed}/{batches.Count} batches ({percent:F1}%) - {_depotToAppMappings.Count} depot mappings found");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Failed to process batch {index + 1}: {ex.Message}");
            }
            finally
            {
                semaphore.Release();
                await Task.Delay(50); // Small rate limit
            }
        });

        await Task.WhenAll(tasks);

        Console.WriteLine($"Completed! Found {_depotToAppMappings.Count} depot mappings");
    }

    public async Task<int> ResolveOrphanDepotsAsync(List<uint> orphanDepotIds, CancellationToken ct)
    {
        // Filter out depot IDs that are already resolved
        var unresolvedDepots = orphanDepotIds
            .Where(depotId => !_depotOwners.ContainsKey(depotId) && !_depotToAppMappings.ContainsKey(depotId))
            .Distinct()
            .ToList();

        if (unresolvedDepots.Count == 0)
        {
            Console.WriteLine("No unresolved orphan depots to process.");
            return 0;
        }

        Console.WriteLine($"Attempting to resolve {unresolvedDepots.Count} orphan depots...");

        // Generate candidate parent app IDs for each orphan depot
        var candidateAppIds = new HashSet<uint>();
        foreach (var depotId in unresolvedDepots)
        {
            // Common Steam patterns: depot is often appId+1, same as appId, or appId+2
            var candidates = new uint[] { depotId - 1, depotId, depotId - 2 };
            foreach (var candidate in candidates)
            {
                if (candidate > 0 && !_scannedApps.Contains(candidate))
                {
                    candidateAppIds.Add(candidate);
                }
            }
        }

        if (candidateAppIds.Count == 0)
        {
            Console.WriteLine("All candidate apps already scanned, no new apps to query.");
            return 0;
        }

        Console.WriteLine($"Querying PICS for {candidateAppIds.Count} candidate parent apps...");

        var resolvedBefore = unresolvedDepots.Count(d => _depotOwners.ContainsKey(d) || _depotToAppMappings.ContainsKey(d));
        var batches = candidateAppIds.Chunk(AppBatchSize).ToList();
        int processedBatches = 0;

        foreach (var batch in batches)
        {
            ct.ThrowIfCancellationRequested();

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

                foreach (var cb in productCallbacks)
                {
                    foreach (var app in cb.Apps.Values)
                    {
                        ProcessAppDepots(app);
                    }
                }

                processedBatches++;
                Console.WriteLine($"  Orphan resolution batch {processedBatches}/{batches.Count} complete");
                await Task.Delay(50, ct); // Small rate limit
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Warning: Failed to process orphan resolution batch {processedBatches + 1}: {ex.Message}");
            }
        }

        var resolvedAfter = unresolvedDepots.Count(d => _depotOwners.ContainsKey(d) || _depotToAppMappings.ContainsKey(d));
        var newlyResolved = resolvedAfter - resolvedBefore;

        Console.WriteLine($"Orphan resolution complete: {newlyResolved}/{unresolvedDepots.Count} depots resolved");
        return newlyResolved;
    }

    private async Task ProcessBatchAsync(uint[] batch, HashSet<uint> allAppIds, int batchNumber)
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

        // Process apps and collect DLC apps to scan
        var dlcAppsToScan = new List<uint>();
        foreach (var cb in productCallbacks)
        {
            foreach (var app in cb.Apps.Values)
            {
                var dlcList = ProcessAppDepots(app);
                // Only scan DLC apps that aren't already in the main processing list
                dlcAppsToScan.AddRange(dlcList.Where(dlcId => !allAppIds.Contains(dlcId)));
            }
        }

        // Process DLC apps found in this batch
        if (dlcAppsToScan.Count > 0)
        {
            Console.WriteLine($"  Found {dlcAppsToScan.Count} DLC apps to scan in batch {batchNumber}");

            // Process DLC apps in sub-batches
            var dlcBatches = dlcAppsToScan.Distinct().Chunk(100).ToList();
            foreach (var dlcBatch in dlcBatches)
            {
                try
                {
                    var dlcTokensJob = _connectionService.Apps.PICSGetAccessTokens(dlcBatch, Enumerable.Empty<uint>());
                    var dlcTokens = await WaitForCallbackAsync(dlcTokensJob);

                    var dlcAppRequests = new List<SteamApps.PICSRequest>();
                    foreach (var dlcAppId in dlcBatch)
                    {
                        var request = new SteamApps.PICSRequest(dlcAppId);
                        if (dlcTokens.AppTokens.TryGetValue(dlcAppId, out var token))
                        {
                            request.AccessToken = token;
                        }
                        dlcAppRequests.Add(request);
                    }

                    var dlcProductJob = _connectionService.Apps.PICSGetProductInfo(dlcAppRequests, Enumerable.Empty<SteamApps.PICSRequest>());
                    var dlcProductCallbacks = await WaitForAllProductInfoAsync(dlcProductJob);

                    foreach (var dlcCb in dlcProductCallbacks)
                    {
                        foreach (var dlcApp in dlcCb.Apps.Values)
                        {
                            ProcessAppDepots(dlcApp);
                        }
                    }

                    await Task.Delay(25); // Small delay between DLC batches
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  Warning: Failed to process DLC batch: {ex.Message}");
                }
            }
        }
    }

    private List<uint> ProcessAppDepots(SteamApps.PICSProductInfoCallback.PICSProductInfo app)
    {
        var dlcAppIdsToScan = new List<uint>();

        try
        {
            var appId = app.ID;
            var kv = app.KeyValues;

            var appinfo = kv["appinfo"];
            var common = appinfo != KeyValue.Invalid ? appinfo["common"] : kv["common"];
            var depots = appinfo != KeyValue.Invalid ? appinfo["depots"] : kv["depots"];

            var appName = common?["name"]?.AsString() ?? $"App {appId}";
            var appType = common?["type"]?.AsString()?.ToLower() ?? "unknown";
            _appNames[appId] = appName;
            _appTypes[appId] = appType;

            // Extract DLC list for DLC depot discovery
            var listofdlc = common?["listofdlc"];
            if (listofdlc != KeyValue.Invalid && listofdlc?.Children != null)
            {
                foreach (var dlcChild in listofdlc.Children)
                {
                    if (uint.TryParse(dlcChild.AsString(), out var dlcAppId))
                    {
                        // Add DLC to scan list if not already processed
                        if (!_appNames.ContainsKey(dlcAppId))
                        {
                            dlcAppIdsToScan.Add(dlcAppId);
                        }
                    }
                }
            }

            if (depots == KeyValue.Invalid || depots.Children == null)
            {
                return dlcAppIdsToScan;
            }

            foreach (var child in depots.Children)
            {
                if (!uint.TryParse(child.Name, out var depotId))
                    continue;

                var ownerFromPics = AsUInt(child["depotfromapp"]);
                var ownerAppId = ownerFromPics ?? appId;

                // FIXED: DLC depots use their App ID as Depot ID - this is normal Steam behavior
                // Only skip if it's a base game/app (not DLC) with self-referencing depot
                if (depotId == ownerAppId && appType != "dlc" && !ownerFromPics.HasValue)
                {
                    continue;
                }

                // For DLCs, depot ID == app ID is expected and valid
                if (depotId == ownerAppId && appType == "dlc")
                {
                    Console.WriteLine($"  Found DLC depot {depotId} for DLC app {appId} ({appName})");
                }

                var set = _depotToAppMappings.GetOrAdd(depotId, _ => new HashSet<uint>());
                set.Add(ownerAppId);

                // Store the owner app for this depot
                _depotOwners.TryAdd(depotId, ownerAppId);

                // Queue owner app for scanning if we don't have its name yet
                // This handles redistributables/launchers (e.g., EA App, Ubisoft Connect, Rockstar Launcher)
                if (ownerFromPics.HasValue && !_appNames.ContainsKey(ownerAppId) && !_scannedApps.Contains(ownerAppId))
                {
                    dlcAppIdsToScan.Add(ownerAppId);
                    _appNames[ownerAppId] = $"App {ownerAppId}"; // Temporary placeholder until scanned
                }
            }

            _scannedApps.Add(appId);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Error processing app {app.ID}: {ex.Message}");
        }

        return dlcAppIdsToScan;
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
