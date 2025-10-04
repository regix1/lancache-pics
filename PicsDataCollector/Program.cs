using SteamKit2;
using System.Collections.Concurrent;
using System.Text.Json;

namespace PicsDataCollector;

class Program
{
    private static SteamClient? _steamClient;
    private static CallbackManager? _callbackManager;
    private static SteamUser? _steamUser;
    private static SteamApps? _steamApps;
    private static bool _isRunning = true;
    private static bool _isLoggedOn = false;
    private static TaskCompletionSource? _connectedTcs;
    private static TaskCompletionSource? _loggedOnTcs;

    // Storage for depot mappings: depotId -> set of appIds
    private static readonly ConcurrentDictionary<uint, HashSet<uint>> _depotToAppMappings = new();
    private static readonly ConcurrentDictionary<uint, string> _appNames = new();
    private static uint _lastChangeNumberSeen = 0;

    // Configuration
    private const int AppBatchSize = 200;
    private const string OutputFileName = "pics_depot_mappings.json";

    static async Task<int> Main(string[] args)
    {
        Console.WriteLine("Steam PICS Depot Mapping Collector");
        Console.WriteLine("===================================");
        Console.WriteLine();

        // Parse arguments
        bool incrementalOnly = args.Contains("--incremental");
        bool fullUpdate = args.Contains("--full");

        if (incrementalOnly && fullUpdate)
        {
            Console.WriteLine("Error: Cannot specify both --incremental and --full");
            return 1;
        }

        // Load existing data if doing incremental update
        if (incrementalOnly)
        {
            Console.WriteLine("Mode: Incremental update");
            await LoadExistingDataAsync();
        }
        else if (fullUpdate)
        {
            Console.WriteLine("Mode: Full update");
        }
        else
        {
            // Auto-detect based on existing file
            if (File.Exists(OutputFileName))
            {
                Console.WriteLine("Mode: Incremental update (auto-detected)");
                await LoadExistingDataAsync();
                incrementalOnly = true;
            }
            else
            {
                Console.WriteLine("Mode: Full update (no existing data found)");
            }
        }

        Console.WriteLine();

        try
        {
            // Initialize SteamKit2
            Console.WriteLine("Initializing SteamKit2...");
            _steamClient = new SteamClient();
            _callbackManager = new CallbackManager(_steamClient);
            _steamUser = _steamClient.GetHandler<SteamUser>();
            _steamApps = _steamClient.GetHandler<SteamApps>();

            // Subscribe to callbacks
            _callbackManager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
            _callbackManager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
            _callbackManager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
            _callbackManager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);

            // Start callback handling
            var callbackTask = Task.Run(HandleCallbacks);

            // Connect to Steam
            await ConnectAndLoginAsync();

            // Build the depot index
            await BuildDepotIndexAsync(incrementalOnly);

            // Save to JSON
            await SaveToJsonAsync(incrementalOnly);

            Console.WriteLine();
            Console.WriteLine("Collection complete!");
            Console.WriteLine($"Output file: {OutputFileName}");
            Console.WriteLine($"Total depot mappings: {_depotToAppMappings.Count}");
            Console.WriteLine($"Total unique apps: {_appNames.Count}");

            // Disconnect
            _isRunning = false;
            if (_steamClient.IsConnected)
            {
                _steamUser?.LogOff();
                await Task.Delay(1000);
                _steamClient.Disconnect();
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    private static async Task HandleCallbacks()
    {
        while (_isRunning)
        {
            _callbackManager?.RunWaitCallbacks(TimeSpan.FromMilliseconds(100));
            await Task.Delay(10);
        }
    }

    private static async Task ConnectAndLoginAsync()
    {
        _connectedTcs = new TaskCompletionSource();
        _loggedOnTcs = new TaskCompletionSource();

        Console.WriteLine("Connecting to Steam...");
        _steamClient!.Connect();

        await WaitForTaskWithTimeout(_connectedTcs.Task, TimeSpan.FromSeconds(30));
        Console.WriteLine("Connected to Steam!");

        Console.WriteLine("Logging in anonymously...");
        _steamUser!.LogOnAnonymous();

        await WaitForTaskWithTimeout(_loggedOnTcs.Task, TimeSpan.FromSeconds(30));
        Console.WriteLine("Logged in successfully!");
        Console.WriteLine();
    }

    private static void OnConnected(SteamClient.ConnectedCallback callback)
    {
        _connectedTcs?.TrySetResult();
    }

    private static void OnDisconnected(SteamClient.DisconnectedCallback callback)
    {
        _isLoggedOn = false;
        if (!_connectedTcs?.Task.IsCompleted ?? false)
        {
            _connectedTcs?.TrySetException(new Exception("Disconnected during connect"));
        }
    }

    private static void OnLoggedOn(SteamUser.LoggedOnCallback callback)
    {
        if (callback.Result == EResult.OK)
        {
            _isLoggedOn = true;
            _loggedOnTcs?.TrySetResult();
        }
        else
        {
            _loggedOnTcs?.TrySetException(new Exception($"Logon failed: {callback.Result}"));
        }
    }

    private static void OnLoggedOff(SteamUser.LoggedOffCallback callback)
    {
        _isLoggedOn = false;
    }

    private static async Task BuildDepotIndexAsync(bool incrementalOnly)
    {
        Console.WriteLine("Enumerating app IDs via PICS...");
        var appIds = await EnumerateAllAppIdsAsync(incrementalOnly);
        Console.WriteLine($"Found {appIds.Count} app IDs to process");
        Console.WriteLine();

        var batches = appIds.Chunk(AppBatchSize).ToList();
        int processedBatches = 0;

        Console.WriteLine($"Processing {batches.Count} batches of apps...");

        foreach (var batch in batches)
        {
            try
            {
                // Get access tokens
                var tokensJob = _steamApps!.PICSGetAccessTokens(batch, Enumerable.Empty<uint>());
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
                var productJob = _steamApps.PICSGetProductInfo(appRequests, Enumerable.Empty<SteamApps.PICSRequest>());
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

    private static void ProcessAppDepots(SteamApps.PICSProductInfoCallback.PICSProductInfo app)
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

    private static async Task<List<uint>> EnumerateAllAppIdsAsync(bool incrementalOnly)
    {
        var allApps = new HashSet<uint>();
        uint since = 0;

        // Get current change number
        var initialJob = _steamApps!.PICSGetChangesSince(0, false, false);
        var initialChanges = await WaitForCallbackAsync(initialJob);
        var currentChangeNumber = initialChanges.CurrentChangeNumber;

        // Use saved change number for incremental
        if (incrementalOnly && _lastChangeNumberSeen > 0)
        {
            since = _lastChangeNumberSeen;
            Console.WriteLine($"Incremental update from change #{since} to #{currentChangeNumber}");
        }
        else
        {
            // Start from recent point
            since = Math.Max(0, currentChangeNumber - 50000);
            Console.WriteLine($"Enumerating from change #{since} to #{currentChangeNumber}");
        }

        int consecutiveFullUpdates = 0;
        const int maxFullUpdates = 3;

        while (since < currentChangeNumber && consecutiveFullUpdates < maxFullUpdates)
        {
            var job = _steamApps.PICSGetChangesSince(since, true, true);
            var changes = await WaitForCallbackAsync(job);
            _lastChangeNumberSeen = changes.CurrentChangeNumber;

            if (changes.RequiresFullUpdate || changes.RequiresFullAppUpdate)
            {
                consecutiveFullUpdates++;
                var skipAmount = Math.Min(12500, currentChangeNumber - since);
                since += skipAmount;
                await Task.Delay(2000);
                continue;
            }

            consecutiveFullUpdates = 0;

            foreach (var change in changes.AppChanges)
            {
                allApps.Add(change.Key);
            }

            var last = changes.LastChangeNumber;
            if (last <= since)
            {
                if (changes.AppChanges.Count == 0)
                {
                    since += 500;
                    await Task.Delay(100);
                    continue;
                }
                last = (uint)Math.Min((long)currentChangeNumber, (long)since + Math.Max(1, changes.AppChanges.Count));
            }

            since = last;

            if (allApps.Count >= 500000)
                break;

            await Task.Delay(100);
        }

        var list = allApps.ToList();
        list.Sort();
        return list;
    }

    private static async Task<T> WaitForCallbackAsync<T>(AsyncJob<T> job, TimeSpan? timeout = null) where T : CallbackMsg
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

        using var subscription = _callbackManager!.Subscribe(handler!);
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(5));

        while (!tcs.Task.IsCompleted && !cts.Token.IsCancellationRequested)
        {
            _callbackManager.RunWaitCallbacks(TimeSpan.FromMilliseconds(50));
            await Task.Delay(10);
        }

        return await tcs.Task;
    }

    private static async Task<IReadOnlyList<SteamApps.PICSProductInfoCallback>> WaitForAllProductInfoAsync(
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

        using var subscription = _callbackManager!.Subscribe(handler!);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));

        while (!isCompleted && !cts.Token.IsCancellationRequested)
        {
            _callbackManager.RunWaitCallbacks(TimeSpan.FromMilliseconds(100));
            await Task.Delay(10);
        }

        return callbacks.AsReadOnly();
    }

    private static async Task WaitForTaskWithTimeout(Task task, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(timeout);

        var completedTask = await Task.WhenAny(task, Task.Delay(Timeout.Infinite, cts.Token));

        if (completedTask != task)
        {
            throw new TimeoutException("Operation timed out");
        }

        await task;
    }

    private static async Task LoadExistingDataAsync()
    {
        try
        {
            if (!File.Exists(OutputFileName))
            {
                Console.WriteLine("No existing data file found.");
                return;
            }

            var json = await File.ReadAllTextAsync(OutputFileName);
            var data = JsonSerializer.Deserialize<PicsJsonData>(json, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            if (data?.DepotMappings != null)
            {
                foreach (var (depotIdStr, mapping) in data.DepotMappings)
                {
                    if (!uint.TryParse(depotIdStr, out var depotId))
                        continue;

                    var set = _depotToAppMappings.GetOrAdd(depotId, _ => new HashSet<uint>());
                    if (mapping.AppIds != null)
                    {
                        foreach (var appId in mapping.AppIds)
                        {
                            set.Add(appId);
                        }
                    }

                    if (mapping.AppNames != null && mapping.AppIds != null)
                    {
                        for (int i = 0; i < Math.Min(mapping.AppIds.Count, mapping.AppNames.Count); i++)
                        {
                            _appNames.TryAdd(mapping.AppIds[i], mapping.AppNames[i]);
                        }
                    }
                }

                if (data.Metadata?.LastChangeNumber > 0)
                {
                    _lastChangeNumberSeen = data.Metadata.LastChangeNumber;
                }

                Console.WriteLine($"Loaded {data.Metadata?.TotalMappings ?? 0} existing mappings from {OutputFileName}");
                Console.WriteLine($"Last change number: {_lastChangeNumberSeen}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Failed to load existing data: {ex.Message}");
        }
    }

    private static async Task SaveToJsonAsync(bool merge)
    {
        Console.WriteLine();
        Console.WriteLine("Saving to JSON...");

        var picsData = new PicsJsonData
        {
            Metadata = new PicsMetadata
            {
                LastUpdated = DateTime.UtcNow,
                TotalMappings = _depotToAppMappings.Sum(kvp => kvp.Value.Count),
                Version = "1.0",
                NextUpdateDue = DateTime.UtcNow.AddDays(2),
                LastChangeNumber = _lastChangeNumberSeen
            },
            DepotMappings = new Dictionary<string, PicsDepotMapping>()
        };

        foreach (var (depotId, appIds) in _depotToAppMappings)
        {
            var appIdsList = appIds.ToList();
            var appNamesList = appIdsList.Select(appId =>
                _appNames.TryGetValue(appId, out var name) ? name : $"App {appId}"
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
        await File.WriteAllTextAsync(OutputFileName, jsonContent);

        Console.WriteLine($"Saved to {OutputFileName}");
        Console.WriteLine($"Total mappings: {picsData.Metadata.TotalMappings}");
    }
}

// Data models
public class PicsJsonData
{
    public PicsMetadata? Metadata { get; set; }
    public Dictionary<string, PicsDepotMapping>? DepotMappings { get; set; }
}

public class PicsMetadata
{
    public DateTime LastUpdated { get; set; }
    public int TotalMappings { get; set; }
    public string Version { get; set; } = "1.0";
    public DateTime NextUpdateDue { get; set; }
    public uint LastChangeNumber { get; set; }
}

public class PicsDepotMapping
{
    public List<uint>? AppIds { get; set; }
    public List<string>? AppNames { get; set; }
    public string Source { get; set; } = "SteamKit2-PICS";
    public DateTime DiscoveredAt { get; set; }
}
