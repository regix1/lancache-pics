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
        var allApps = new HashSet<uint>();
        uint since = 0;

        // Get current change number
        var initialJob = _connectionService.Apps.PICSGetChangesSince(0, false, false);
        var initialChanges = await WaitForCallbackAsync(initialJob);
        var currentChangeNumber = initialChanges.CurrentChangeNumber;

        // Use saved change number for incremental
        if (incrementalOnly && lastChangeNumberSeen > 0)
        {
            since = lastChangeNumberSeen;
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
            var job = _connectionService.Apps.PICSGetChangesSince(since, true, true);
            var changes = await WaitForCallbackAsync(job);

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
