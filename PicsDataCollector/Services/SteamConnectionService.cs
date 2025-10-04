using SteamKit2;

namespace PicsDataCollector.Services;

public class SteamConnectionService
{
    private readonly SteamClient _steamClient;
    private readonly CallbackManager _callbackManager;
    private readonly SteamUser _steamUser;
    private readonly SteamApps _steamApps;

    private bool _isLoggedOn;
    private TaskCompletionSource? _connectedTcs;
    private TaskCompletionSource? _loggedOnTcs;

    public SteamClient Client => _steamClient;
    public CallbackManager CallbackManager => _callbackManager;
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
        _connectedTcs = new TaskCompletionSource();
        _loggedOnTcs = new TaskCompletionSource();

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

    public void Disconnect()
    {
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
