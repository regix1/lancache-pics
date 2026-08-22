using SteamKit2;

namespace PicsDataCollector.Services;

public class IncrementalUpdateService
{
    private readonly SteamConnectionService _connectionService;
    private readonly DepotMappingService _mappingService;

    public IncrementalUpdateService(
        SteamConnectionService connectionService,
        DepotMappingService mappingService)
    {
        _connectionService = connectionService;
        _mappingService = mappingService;
    }

    /// <summary>
    /// Perform an incremental PICS update from the last change number
    /// </summary>
    /// <param name="lastChangeNumber">Last change number we processed</param>
    /// <param name="authenticated">Whether we're authenticated (can access private apps)</param>
    /// <returns>New change number and list of changed app IDs</returns>
    public async Task<IncrementalUpdateResult> PerformIncrementalUpdateAsync(
        uint lastChangeNumber,
        bool authenticated = false)
    {
        Console.WriteLine($"Performing incremental PICS update from change #{lastChangeNumber}...");
        Console.WriteLine($"Authentication: {(authenticated ? "Yes (can access private apps)" : "No (anonymous, public apps only)")}");

        // Get changes since last update
        var changesJob = _connectionService.Apps.PICSGetChangesSince(lastChangeNumber, sendAppChangelist: true, sendPackageChangelist: false);
        var changesCallback = await WaitForCallbackAsync(changesJob);

        var currentChangeNumber = changesCallback.CurrentChangeNumber;
        var changeNumberDelta = currentChangeNumber - lastChangeNumber;

        Console.WriteLine($"Current change number: #{currentChangeNumber} (delta: {changeNumberDelta})");

        // Check if we need a full update
        if (changesCallback.RequiresFullUpdate || changesCallback.RequiresFullAppUpdate)
        {
            Console.WriteLine("⚠️  PICS indicates a FULL UPDATE is required!");
            Console.WriteLine($"   This happens when the change delta is too large (>{changeNumberDelta})");
            Console.WriteLine($"   Reason: {(changesCallback.RequiresFullUpdate ? "RequiresFullUpdate" : "RequiresFullAppUpdate")}");

            return new IncrementalUpdateResult
            {
                Success = false,
                RequiresFullUpdate = true,
                CurrentChangeNumber = currentChangeNumber,
                ChangedAppIds = [],
                ChangeNumberDelta = changeNumberDelta
            };
        }

        // Get list of changed apps
        var changedAppIds = changesCallback.AppChanges.Keys.ToList();

        Console.WriteLine($"Found {changedAppIds.Count} apps with changes");

        if (changedAppIds.Count == 0)
        {
            Console.WriteLine("No changes detected - database is up to date!");
            return new IncrementalUpdateResult
            {
                Success = true,
                RequiresFullUpdate = false,
                CurrentChangeNumber = currentChangeNumber,
                ChangedAppIds = [],
                ChangeNumberDelta = changeNumberDelta
            };
        }

        // Show some details about the changes
        if (changedAppIds.Count <= 20)
        {
            Console.WriteLine("Changed apps:");
            foreach (var appId in changedAppIds.Take(20))
            {
                if (changesCallback.AppChanges.TryGetValue(appId, out var changeData))
                {
                    Console.WriteLine($"  - App {appId}: Change #{changeData.ChangeNumber}");
                }
            }
        }
        else
        {
            Console.WriteLine($"Changed apps: {changedAppIds.Count} total (showing first 10)");
            foreach (var appId in changedAppIds.Take(10))
            {
                if (changesCallback.AppChanges.TryGetValue(appId, out var changeData))
                {
                    Console.WriteLine($"  - App {appId}: Change #{changeData.ChangeNumber}");
                }
            }
            Console.WriteLine($"  ... and {changedAppIds.Count - 10} more");
        }

        return new IncrementalUpdateResult
        {
            Success = true,
            RequiresFullUpdate = false,
            CurrentChangeNumber = currentChangeNumber,
            ChangedAppIds = changedAppIds,
            ChangeNumberDelta = changeNumberDelta
        };
    }

    /// <summary>
    /// Check if we're getting too far behind and might trigger a full update requirement
    /// </summary>
    public async Task<uint> GetCurrentChangeNumberAsync()
    {
        var changesJob = _connectionService.Apps.PICSGetChangesSince(0, sendAppChangelist: false, sendPackageChangelist: false);
        var changesCallback = await WaitForCallbackAsync(changesJob);
        return changesCallback.CurrentChangeNumber;
    }

    /// <summary>
    /// Calculate how far behind we are
    /// </summary>
    public async Task<ChangeNumberStatus> CheckChangeNumberStatusAsync(uint lastChangeNumber)
    {
        var currentChangeNumber = await GetCurrentChangeNumberAsync();
        var delta = currentChangeNumber - lastChangeNumber;

        // Empirical thresholds (based on Steam's behavior)
        const uint WARNING_THRESHOLD = 1_000_000;   // ~1M changes behind (warn user)
        const uint DANGER_THRESHOLD = 5_000_000;    // ~5M changes behind (likely to require full update)

        var status = delta switch
        {
            0 => ChangeNumberHealthStatus.Current,
            <= WARNING_THRESHOLD => ChangeNumberHealthStatus.Good,
            <= DANGER_THRESHOLD => ChangeNumberHealthStatus.Warning,
            _ => ChangeNumberHealthStatus.Critical
        };

        return new ChangeNumberStatus
        {
            LastChangeNumber = lastChangeNumber,
            CurrentChangeNumber = currentChangeNumber,
            Delta = delta,
            Status = status
        };
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
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(2));

        while (!tcs.Task.IsCompleted && !cts.Token.IsCancellationRequested)
        {
            _connectionService.CallbackManager.RunWaitCallbacks(TimeSpan.FromMilliseconds(50));
            await Task.Delay(10);
        }

        return await tcs.Task;
    }
}

public class IncrementalUpdateResult
{
    public bool Success { get; set; }
    public bool RequiresFullUpdate { get; set; }
    public uint CurrentChangeNumber { get; set; }
    public List<uint> ChangedAppIds { get; set; } = [];
    public uint ChangeNumberDelta { get; set; }
}

public class ChangeNumberStatus
{
    public uint LastChangeNumber { get; set; }
    public uint CurrentChangeNumber { get; set; }
    public uint Delta { get; set; }
    public ChangeNumberHealthStatus Status { get; set; }

    public override string ToString()
    {
        var statusIcon = Status switch
        {
            ChangeNumberHealthStatus.Current => "✅",
            ChangeNumberHealthStatus.Good => "✅",
            ChangeNumberHealthStatus.Warning => "⚠️ ",
            ChangeNumberHealthStatus.Critical => "❌",
            _ => "❓"
        };

        return $"{statusIcon} Last: #{LastChangeNumber}, Current: #{CurrentChangeNumber}, Delta: {Delta:N0} ({Status})";
    }
}

public enum ChangeNumberHealthStatus
{
    Current,    // Exactly up to date
    Good,       // < 1M changes behind
    Warning,    // 1M-5M changes behind
    Critical    // > 5M changes behind (likely to require full update)
}
