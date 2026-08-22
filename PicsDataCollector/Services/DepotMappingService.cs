using PicsDataCollector.Models;
using SteamKit2;
using System.Collections.Concurrent;

namespace PicsDataCollector.Services;

public class DepotMappingService
{
    private readonly SteamConnectionService _connectionService;
    private readonly ConcurrentDictionary<uint, HashSet<uint>> _depotToAppMappings = new();
    private readonly ConcurrentDictionary<uint, uint> _depotOwners = new();
    private readonly ConcurrentDictionary<uint, ConcurrentDictionary<uint, PicsDepotRelationship>> _depotRelationships = new();
    private readonly ConcurrentDictionary<uint, string> _appNames = new();
    private readonly ConcurrentDictionary<uint, string> _appTypes = new();
    private readonly ConcurrentDictionary<uint, string> _appHeaderImages = new();
    private readonly ConcurrentDictionary<uint, byte> _scannedApps = new();  // Track scanned apps to avoid rescanning

    private const int AppBatchSize = 500;

    private static readonly HttpClient HeaderImageHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    private static readonly string[] CdnDomains =
    [
        "shared.akamai.steamstatic.com",
        "shared.fastly.steamstatic.com"
    ];

    private const string CdnBasePath = "/store_item_assets/steam/apps";

    public IReadOnlyDictionary<uint, HashSet<uint>> DepotMappings => _depotToAppMappings;
    public IReadOnlyDictionary<uint, uint> DepotOwners => _depotOwners;
    public IReadOnlyDictionary<uint, Dictionary<uint, PicsDepotRelationship>> DepotRelationships =>
        _depotRelationships.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.ToDictionary(inner => inner.Key, inner => inner.Value));
    public IReadOnlyDictionary<uint, string> AppNames => _appNames;
    public IReadOnlyDictionary<uint, string> AppTypes => _appTypes;
    public IReadOnlyDictionary<uint, string> AppHeaderImages => _appHeaderImages;

    public DepotMappingService(SteamConnectionService connectionService)
    {
        _connectionService = connectionService;
    }

    public void LoadExistingMappings(
        Dictionary<uint, HashSet<uint>> mappings,
        Dictionary<uint, string> names,
        Dictionary<uint, uint>? depotOwners = null,
        Dictionary<uint, string>? types = null,
        Dictionary<uint, string>? headerImages = null,
        Dictionary<uint, List<PicsDepotRelationship>>? relationships = null)
    {
        foreach (var (depotId, appIds) in mappings)
        {
            foreach (var appId in appIds)
            {
                AddAppToDepot(depotId, appId);
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

        if (headerImages != null)
        {
            foreach (var (appId, url) in headerImages)
            {
                _appHeaderImages.TryAdd(appId, url);
            }
        }

        if (relationships != null)
        {
            foreach (var (depotId, depotRelationships) in relationships)
            {
                var bySource = _depotRelationships.GetOrAdd(depotId, _ => new ConcurrentDictionary<uint, PicsDepotRelationship>());
                foreach (var relationship in depotRelationships)
                {
                    // Recompute derived IDs when loading so files written by older identity rules heal immediately.
                    bySource[relationship.SourceAppId] = DepotIdentity.CreateRelationship(
                        relationship.SourceAppId,
                        depotId,
                        relationship.DepotFromAppId,
                        relationship.DlcAppId,
                        relationship.HasPublicManifest);
                }
            }
        }

        RebuildDerivedOwners();
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

        RebuildDerivedOwners();
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
                if (candidate > 0 && !_scannedApps.ContainsKey(candidate))
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
                        ProcessAppDepots(app.ID, app.KeyValues);
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

        RebuildDerivedOwners();
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
                var dlcList = ProcessAppDepots(app.ID, app.KeyValues);
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
                            ProcessAppDepots(dlcApp.ID, dlcApp.KeyValues);
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

    internal List<uint> ProcessAppDepots(uint appId, KeyValue kv)
    {
        var dlcAppIdsToScan = new List<uint>();

        try
        {
            var appinfo = kv["appinfo"];
            var common = appinfo != KeyValue.Invalid ? appinfo["common"] : kv["common"];
            var depots = appinfo != KeyValue.Invalid ? appinfo["depots"] : kv["depots"];

            var appName = common?["name"]?.AsString() ?? $"App {appId}";
            var appType = common?["type"]?.AsString()?.ToLower() ?? "unknown";
            _appNames[appId] = appName;
            _appTypes[appId] = appType;

            // Read header_image from PICS — newer games use hash-based paths
            // e.g. "31bac6b2eccf09b368f5e95ce510bae2baf3cfcd/header.jpg" or just "header.jpg"
            var headerImage = common?["header_image"]?.AsString();
            if (!string.IsNullOrEmpty(headerImage))
            {
                var imageUrl = $"https://{CdnDomains[0]}{CdnBasePath}/{appId}/{headerImage}";
                _appHeaderImages[appId] = imageUrl;
            }

            QueueDlcApps(common?["listofdlc"], dlcAppIdsToScan);
            var extended = appinfo != KeyValue.Invalid ? appinfo["extended"] : kv["extended"];
            QueueDlcApps(extended?["listofdlc"], dlcAppIdsToScan);

            if (depots == KeyValue.Invalid || depots.Children == null)
            {
                return dlcAppIdsToScan;
            }

            foreach (var child in depots.Children)
                {
                if (!uint.TryParse(child.Name, out var depotId))
                    continue;

                var depotFromApp = AsUInt(child["depotfromapp"]);
                var dlcAppId = AsUInt(child["dlcappid"]);
                if (dlcAppId == null && appType == "dlc")
                    {
                    dlcAppId = appId;
                }

                var hasPublicManifest = HasPublicManifest(child);
                if (HasExplicitlyEmptyPublicManifest(child) ||
                    DepotIdentity.IsInvalidDepot(hasPublicManifest, depotId, depotFromApp))
                        {
                    continue;
                        }

                if (depotId == appId && appType == "dlc")
                {
                    Console.WriteLine($"  Found DLC depot {depotId} for DLC app {appId} ({appName})");
                    }

                var relationship = DepotIdentity.CreateRelationship(
                    appId,
                    depotId,
                    depotFromApp,
                    dlcAppId,
                    hasPublicManifest);
                var bySource = _depotRelationships.GetOrAdd(depotId, _ => new ConcurrentDictionary<uint, PicsDepotRelationship>());
                bySource[appId] = relationship;

                AddAppToDepot(depotId, appId);

                if (depotFromApp.HasValue && !_appNames.ContainsKey(depotFromApp.Value) && !_scannedApps.ContainsKey(depotFromApp.Value))
                {
                    dlcAppIdsToScan.Add(depotFromApp.Value);
                    _appNames[depotFromApp.Value] = $"App {depotFromApp.Value}";
                }
            }

            _scannedApps.TryAdd(appId, 0);
        }
        catch (Exception ex)
            {
            Console.WriteLine($"Warning: Error processing app {appId}: {ex.Message}");
        }

                return dlcAppIdsToScan;
            }

    public void RebuildDerivedOwners()
    {
        foreach (var (depotId, bySource) in _depotRelationships)
        {
            if (bySource.IsEmpty)
            {
                    continue;
            }

            var ownerId = DepotIdentity.SelectOwnerId(bySource.Values.ToList(), _appTypes);
            _depotOwners[depotId] = ownerId;
            AddAppToDepot(depotId, ownerId);
        }
    }

    private void AddAppToDepot(uint depotId, uint appId)
                {
        var set = _depotToAppMappings.GetOrAdd(depotId, _ => new HashSet<uint>());
        // App batches are scanned in parallel and unrelated apps share depots, so the set behind the key needs the lock.
        lock (set)
        {
            set.Add(appId);
        }
                }

    private void QueueDlcApps(KeyValue? listOfDlc, List<uint> dlcAppIdsToScan)
                {
        if (listOfDlc == null || listOfDlc == KeyValue.Invalid)
        {
            return;
                }

        if (listOfDlc.Children != null)
        {
            foreach (var dlcChild in listOfDlc.Children)
            {
                QueueDlcAppId(dlcChild.AsString(), dlcAppIdsToScan);
            }
        }

        var raw = listOfDlc.AsString();
        if (!string.IsNullOrWhiteSpace(raw))
        {
            foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                QueueDlcAppId(part, dlcAppIdsToScan);
            }
        }
    }

    private void QueueDlcAppId(string? value, List<uint> dlcAppIdsToScan)
    {
        if (!uint.TryParse(value, out var dlcAppId))
        {
            return;
        }

        if (!_appNames.ContainsKey(dlcAppId))
                {
            dlcAppIdsToScan.Add(dlcAppId);
                }
            }

    public static bool HasPublicManifest(KeyValue depotKey)
    {
        var manifests = depotKey["manifests"];
        if (manifests == KeyValue.Invalid)
        {
            return false;
        }

        var publicManifest = manifests["public"];
        if (publicManifest == KeyValue.Invalid)
        {
            return false;
        }

        return AsUInt64(publicManifest["gid"]) != null || AsUInt64(publicManifest) != null;
    }

    public static bool HasExplicitlyEmptyPublicManifest(KeyValue depotKey)
    {
        var publicManifest = depotKey["manifests"]["public"];
        return publicManifest != KeyValue.Invalid &&
               AsUInt64(publicManifest["gid"]) != null &&
               AsUInt64(publicManifest["size"]) == 0 &&
               AsUInt64(publicManifest["download"]) == 0;
    }

    private static uint? AsUInt(KeyValue kv)
    {
        if (kv == KeyValue.Invalid || kv.Value == null) return null;
        if (uint.TryParse(kv.AsString() ?? string.Empty, out var v)) return v;
        return null;
    }

    private static ulong? AsUInt64(KeyValue kv)
    {
        if (kv == KeyValue.Invalid || kv.Value == null) return null;
        if (ulong.TryParse(kv.AsString() ?? string.Empty, out var v)) return v;
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

    /// <summary>
    /// Tests header image URLs against all CDN domains and picks the first that responds.
    /// For apps without PICS header_image data, tries hashless URLs on each domain.
    /// </summary>
    public async Task ValidateHeaderImagesAsync()
    {
        var preferredDomain = await DeterminePreferredCdnDomainAsync();
        Console.WriteLine($"Preferred CDN domain: {preferredDomain}");

        // Update all existing URLs to use the preferred domain
        var keysToUpdate = _appHeaderImages.Keys.ToList();
        foreach (var appId in keysToUpdate)
        {
            if (_appHeaderImages.TryGetValue(appId, out var url))
            {
                _appHeaderImages[appId] = ReplaceCdnDomain(url, preferredDomain);
            }
        }

        // For apps without header images, try to find a working URL
        var appsWithoutImages = _appNames.Keys.Except(_appHeaderImages.Keys).ToList();
        if (appsWithoutImages.Count == 0)
        {
            Console.WriteLine("All apps have PICS header image data — no fallback testing needed.");
            return;
        }

        Console.WriteLine($"Testing header images for {appsWithoutImages.Count} apps without PICS data...");
        var semaphore = new SemaphoreSlim(50);
        int found = 0;

        var tasks = appsWithoutImages.Select(appId => Task.Run(async () =>
        {
            await semaphore.WaitAsync();
            try
            {
                var resolvedUrl = await TestHeaderImageOnAllDomainsAsync(appId);
                if (resolvedUrl != null)
                {
                    _appHeaderImages[appId] = resolvedUrl;
                    Interlocked.Increment(ref found);
                }
            }
            finally
            {
                semaphore.Release();
            }
        })).ToList();

        await Task.WhenAll(tasks);
        Console.WriteLine($"Header image validation: {found} found out of {appsWithoutImages.Count} tested");
    }

    private async Task<string> DeterminePreferredCdnDomainAsync()
    {
        // Test a sample of existing URLs on each domain to pick the best one
        var sampleApps = _appHeaderImages.Take(10).ToList();
        if (sampleApps.Count == 0)
            return CdnDomains[0];

        var domainScores = new ConcurrentDictionary<string, int>();
        foreach (var domain in CdnDomains)
            domainScores[domain] = 0;

        var tasks = sampleApps.SelectMany(kvp =>
            CdnDomains.Select(domain => Task.Run(async () =>
            {
                var testUrl = ReplaceCdnDomain(kvp.Value, domain);
                if (await TestUrlAsync(testUrl))
                    domainScores.AddOrUpdate(domain, 1, (_, count) => count + 1);
            }))
        ).ToList();

        await Task.WhenAll(tasks);

        var best = domainScores.OrderByDescending(kvp => kvp.Value).First();
        Console.WriteLine($"CDN domain test results: {string.Join(", ", domainScores.Select(d => $"{d.Key}={d.Value}/{sampleApps.Count}"))}");
        return best.Key;
    }

    private async Task<string?> TestHeaderImageOnAllDomainsAsync(uint appId)
    {
        foreach (var domain in CdnDomains)
        {
            var url = $"https://{domain}{CdnBasePath}/{appId}/header.jpg";
            if (await TestUrlAsync(url))
                return url;
        }
        return null;
    }

    private static async Task<bool> TestUrlAsync(string url)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await HeaderImageHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static string ReplaceCdnDomain(string url, string newDomain)
    {
        foreach (var domain in CdnDomains)
        {
            if (url.Contains(domain))
                return url.Replace(domain, newDomain);
        }
        return url;
    }
}
