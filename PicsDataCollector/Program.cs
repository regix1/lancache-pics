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

        if (incrementalOnly && fullUpdate)
        {
            Console.WriteLine("Error: Cannot specify both --incremental and --full");
            return 1;
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
                    var (depotMappings, appNames, depotOwners) = persistenceService.ExtractMappingsFromData(data);
                    mappingService.LoadExistingMappings(depotMappings, appNames, depotOwners);
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
                    var (depotMappings, appNames, depotOwners) = persistenceService.ExtractMappingsFromData(data);
                    mappingService.LoadExistingMappings(depotMappings, appNames, depotOwners);
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

            // Enumerate app IDs
            Console.WriteLine("Enumerating app IDs via PICS...");
            var appIds = await enumerationService.EnumerateAllAppIdsAsync(incrementalOnly, lastChangeNumber);
            Console.WriteLine($"Found {appIds.Count} app IDs to process");
            Console.WriteLine();

            // Build depot mappings
            await mappingService.BuildDepotIndexAsync(appIds);

            // Save to JSON
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

            await persistenceService.SaveToJsonAsync(depotMappingsDict, appNamesDict, finalChangeNumber, depotOwnersDict);

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
