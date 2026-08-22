using SteamKit2;

namespace PicsDataCollector.Services;

public class SteamConnectionService
{
    private readonly SteamClient _steamClient;
    private readonly CallbackManager _callbackManager;
    private readonly SteamUser _steamUser;
    private readonly SteamApps _steamApps;

    private bool _isLoggedOn;
    private bool _isRunning;
    private TaskCompletionSource? _connectedTcs;
    private TaskCompletionSource? _loggedOnTcs;
    private Task? _callbackTask;
    private CancellationTokenSource? _callbackCts;

    // Retry configuration
    private const int MaxRetries = 5;
    private const int InitialRetryDelaySeconds = 2;
    private const int MaxRetryDelaySeconds = 30;

    public SteamClient Client => _steamClient;
    public CallbackManager CallbackManager => _callbackManager;
    public SteamUser User => _steamUser;
    public SteamApps Apps => _steamApps;
    public bool IsLoggedOn => _isLoggedOn;

    public SteamConnectionService()
    {
        _steamClient = new SteamClient();
        _callbackManager = new CallbackManager(_steamClient);
        _steamUser = _steamClient.GetHandler<SteamUser>()!;
        _steamApps = _steamClient.GetHandler<SteamApps>()!;

        // Subscribe to callbacks
        _callbackManager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
        _callbackManager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
        _callbackManager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
        _callbackManager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);
    }

    public async Task ConnectAndLoginAsync()
    {
        Exception? lastException = null;

        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                await AttemptConnectAndLoginAsync();
                return; // Success!
            }
            catch (Exception ex)
            {
                lastException = ex;

                if (attempt < MaxRetries)
                {
                    // Exponential backoff with jitter
                    var delaySeconds = Math.Min(InitialRetryDelaySeconds * Math.Pow(2, attempt - 1), MaxRetryDelaySeconds);
                    var jitter = Random.Shared.NextDouble() * 2; // 0-2 seconds jitter
                    var totalDelay = TimeSpan.FromSeconds(delaySeconds + jitter);

                    Console.WriteLine($"Connection attempt {attempt} failed: {ex.Message}");
                    Console.WriteLine($"Retrying in {totalDelay.TotalSeconds:F1} seconds... ({MaxRetries - attempt} attempts remaining)");

                    // Reset state for next attempt
                    StopCallbackLoop();
                    if (_steamClient.IsConnected)
                    {
                        _steamClient.Disconnect();
                    }

                    await Task.Delay(totalDelay);
                }
            }
        }

        throw new Exception($"Failed to connect after {MaxRetries} attempts. Last error: {lastException?.Message}", lastException);
    }

    private async Task AttemptConnectAndLoginAsync()
    {
        _connectedTcs = new TaskCompletionSource();
        _loggedOnTcs = new TaskCompletionSource();
        _isLoggedOn = false;

        // Start callback processing loop
        StartCallbackLoop();

        Console.WriteLine("Connecting to Steam...");
        _steamClient.Connect();

        await WaitForTaskWithTimeout(_connectedTcs.Task, TimeSpan.FromSeconds(30));
        Console.WriteLine("Connected to Steam!");

        Console.WriteLine("Logging in anonymously...");
        _steamUser.LogOnAnonymous();

        await WaitForTaskWithTimeout(_loggedOnTcs.Task, TimeSpan.FromSeconds(30));
        Console.WriteLine("Logged in successfully!");
        Console.WriteLine();
    }

    private void StartCallbackLoop()
    {
        if (_isRunning) return;

        _isRunning = true;
        _callbackCts = new CancellationTokenSource();
        _callbackTask = Task.Run(async () =>
        {
            while (_isRunning && !_callbackCts.Token.IsCancellationRequested)
            {
                _callbackManager.RunWaitCallbacks(TimeSpan.FromMilliseconds(50));
                await Task.Delay(10);
            }
        });
    }

    private void StopCallbackLoop()
    {
        _isRunning = false;
        _callbackCts?.Cancel();
        try
        {
            _callbackTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch { /* Ignore */ }
        _callbackCts?.Dispose();
        _callbackCts = null;
    }

    public void Disconnect()
    {
        StopCallbackLoop();

        if (_steamClient.IsConnected)
        {
            _steamUser.LogOff();
            Task.Delay(1000).Wait();
            _steamClient.Disconnect();
        }
    }

    private void OnConnected(SteamClient.ConnectedCallback callback)
    {
        _connectedTcs?.TrySetResult();
    }

    private void OnDisconnected(SteamClient.DisconnectedCallback callback)
    {
        _isLoggedOn = false;
        if (!_connectedTcs?.Task.IsCompleted ?? false)
        {
            _connectedTcs?.TrySetException(new Exception("Disconnected during connect"));
        }
        if (!_loggedOnTcs?.Task.IsCompleted ?? false)
        {
            _loggedOnTcs?.TrySetException(new Exception("Disconnected during login"));
        }
    }

    private void OnLoggedOn(SteamUser.LoggedOnCallback callback)
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

    private void OnLoggedOff(SteamUser.LoggedOffCallback callback)
    {
        _isLoggedOn = false;
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
}
