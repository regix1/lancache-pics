using PicsDataCollector.Services;

namespace PicsDataCollector;

class Program
{
    static async Task<int> Main(string[] args)
    {
        Console.WriteLine("Steam PICS Depot Mapping Collector");
        Console.WriteLine("===================================");
        Console.WriteLine();

        // Parse arguments
        bool incrementalOnly = args.Contains("--incremental");
        bool fullUpdate = args.Contains("--full");
        string? resolveDepotsArg = GetArgValue(args, "--resolve-depots");
        string? resolveDepotsFileArg = GetArgValue(args, "--resolve-depots-file");

        if (incrementalOnly && fullUpdate)
        {
            Console.WriteLine("Error: Cannot specify both --incremental and --full");
            return 1;
        }

        // Parse orphan depot IDs from --resolve-depots and/or --resolve-depots-file
        var orphanDepotIds = new List<uint>();

        if (resolveDepotsArg != null)
        {
            foreach (var part in resolveDepotsArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (uint.TryParse(part, out uint depotId))
                {
                    orphanDepotIds.Add(depotId);
                }
                else
                {
                    Console.WriteLine($"Warning: Ignoring invalid depot ID '{part}'");
                }
            }
        }

        if (resolveDepotsFileArg != null)
        {
            if (!File.Exists(resolveDepotsFileArg))
            {
                Console.WriteLine($"Error: Resolve depots file not found: {resolveDepotsFileArg}");
                return 1;
            }

            var lines = await File.ReadAllLinesAsync(resolveDepotsFileArg);
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
                    continue;

                if (uint.TryParse(trimmed, out uint depotId))
                {
                    orphanDepotIds.Add(depotId);
                }
                else
                {
                    Console.WriteLine($"Warning: Ignoring invalid depot ID '{trimmed}' in file");
                }
            }
        }

        if (orphanDepotIds.Count > 0)
        {
            orphanDepotIds = orphanDepotIds.Distinct().ToList();
            Console.WriteLine($"Orphan depots to resolve: {orphanDepotIds.Count}");
        }

        try
        {
            // Initialize services
            var persistenceService = new DataPersistenceService();
            var connectionService = new SteamConnectionService();
            var enumerationService = new PicsEnumerationService(connectionService);
            var mappingService = new DepotMappingService(connectionService);

            // Load existing data if appropriate
            uint lastChangeNumber = 0;
            if (incrementalOnly)
            {
                Console.WriteLine("Mode: Incremental update");
                var (data, changeNumber) = await persistenceService.LoadExistingDataAsync();
                lastChangeNumber = changeNumber;

                if (data != null)
                {
                    var (depotMappings, appNames, depotOwners, appTypes) = persistenceService.ExtractMappingsFromData(data);
                    mappingService.LoadExistingMappings(depotMappings, appNames, depotOwners, appTypes);
                }
            }
            else if (fullUpdate)
            {
                Console.WriteLine("Mode: Full update");
            }
            else
            {
                // Auto-detect based on existing file
                var (data, changeNumber) = await persistenceService.LoadExistingDataAsync();
                if (data != null)
                {
                    Console.WriteLine("Mode: Incremental update (auto-detected)");
                    lastChangeNumber = changeNumber;
                    var (depotMappings, appNames, depotOwners, appTypes) = persistenceService.ExtractMappingsFromData(data);
                    mappingService.LoadExistingMappings(depotMappings, appNames, depotOwners, appTypes);
                    incrementalOnly = true;
                }
                else
                {
                    Console.WriteLine("Mode: Full update (no existing data found)");
                }
            }

            Console.WriteLine();

            // Start callback handling
            bool isRunning = true;
            var callbackTask = Task.Run(async () =>
            {
                while (isRunning)
                {
                    connectionService.CallbackManager.RunWaitCallbacks(TimeSpan.FromMilliseconds(100));
                    await Task.Delay(10);
                }
            });

            // Connect to Steam
            await connectionService.ConnectAndLoginAsync();

            // Pre-flight check: Verify change number status if doing incremental update
            if (incrementalOnly && lastChangeNumber > 0)
            {
                Console.WriteLine("Pre-flight check: Verifying change number status...");
                var incrementalService = new IncrementalUpdateService(connectionService, mappingService);
                var status = await incrementalService.CheckChangeNumberStatusAsync(lastChangeNumber);

                Console.WriteLine($"Change Number Status: {status}");

                if (status.Status == ChangeNumberHealthStatus.Critical)
                {
                    Console.WriteLine();
                    Console.WriteLine("⚠️  WARNING: Database is significantly out of date!");
                    Console.WriteLine($"   You are {status.Delta:N0} changes behind.");
                    Console.WriteLine($"   Steam may require a FULL update instead of incremental.");
                    Console.WriteLine();
                    Console.WriteLine("Recommendation: Run with --full to rebuild from scratch");
                    Console.WriteLine("                or continue with incremental (may fall back to full)");
                    Console.WriteLine();
                }
                else if (status.Status == ChangeNumberHealthStatus.Warning)
                {
                    Console.WriteLine();
                    Console.WriteLine($"⚠️  Notice: Database is {status.Delta:N0} changes behind");
                    Console.WriteLine($"   Incremental update should work, but may take longer");
                    Console.WriteLine();
                }
            }

            Console.WriteLine();

            // Enumerate app IDs
            Console.WriteLine("Enumerating app IDs via PICS...");
            var appIds = await enumerationService.EnumerateAllAppIdsAsync(incrementalOnly, lastChangeNumber);
            Console.WriteLine($"Found {appIds.Count} app IDs to process");
            Console.WriteLine();

            // Build depot mappings (this collects app types from PICS)
            await mappingService.BuildDepotIndexAsync(appIds);

            // Resolve orphan depots if requested
            if (orphanDepotIds.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine($"Resolving {orphanDepotIds.Count} orphan depots from delisted games...");
                using var orphanCts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                int resolved = await mappingService.ResolveOrphanDepotsAsync(orphanDepotIds, orphanCts.Token);
                Console.WriteLine($"Orphan depot resolution: {resolved} of {orphanDepotIds.Count} depots resolved");
                Console.WriteLine();
            }

            // Save depot mappings to JSON (now includes app types and header images)
            var depotMappingsDict = mappingService.DepotMappings.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value
            );
            var appNamesDict = mappingService.AppNames.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value
            );
            var depotOwnersDict = mappingService.DepotOwners.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value
            );

            // Get the final change number from enumeration service
            var finalChangeNumber = await GetCurrentChangeNumberAsync(connectionService);

            // Convert AppTypes to Dictionary for persistence
            var appTypesDict = mappingService.AppTypes.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value
            );

            // Get the API version used for enumeration
            var apiVersion = enumerationService.LastApiVersionUsed;
            Console.WriteLine($"API Version Used: {apiVersion}");

            await persistenceService.SaveToJsonAsync(depotMappingsDict, appNamesDict, finalChangeNumber, depotOwnersDict, appTypesDict, apiVersion);

            Console.WriteLine();
            Console.WriteLine("Collection complete!");
            Console.WriteLine($"Total depot mappings: {depotMappingsDict.Count}");
            Console.WriteLine($"Total unique apps: {appNamesDict.Count}");

            // Cleanup
            isRunning = false;
            connectionService.Disconnect();

            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    private static string? GetArgValue(string[] args, string argName)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == argName && i + 1 < args.Length)
            {
                return args[i + 1];
            }

            if (args[i].StartsWith(argName + "=", StringComparison.Ordinal))
            {
                return args[i][(argName.Length + 1)..];
            }
        }
        return null;
    }

    private static async Task<uint> GetCurrentChangeNumberAsync(SteamConnectionService connectionService)
    {
        try
        {
            var job = connectionService.Apps.PICSGetChangesSince(0, false, false);
            var tcs = new TaskCompletionSource<uint>();

            Action<SteamKit2.SteamApps.PICSChangesCallback>? handler = null;
            handler = callback =>
            {
                if (callback.JobID == job.JobID)
                {
                    tcs.TrySetResult(callback.CurrentChangeNumber);
                }
            };

            using var subscription = connectionService.CallbackManager.Subscribe(handler!);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            while (!tcs.Task.IsCompleted && !cts.Token.IsCancellationRequested)
            {
                connectionService.CallbackManager.RunWaitCallbacks(TimeSpan.FromMilliseconds(50));
                await Task.Delay(10);
            }

            return await tcs.Task;
        }
        catch
        {
            return 0;
        }
    }
}
