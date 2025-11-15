using SteamKit2;

namespace PicsDataCollector.Services;

public class AuthenticationService
{
    private readonly SteamConnectionService _connectionService;

    public bool IsAuthenticated { get; private set; }
    public string? Username { get; private set; }

    public AuthenticationService(SteamConnectionService connectionService)
    {
        _connectionService = connectionService;
    }

    /// <summary>
    /// Login with username and password (supports Steam Guard)
    /// </summary>
    public async Task<bool> LoginWithCredentialsAsync(string username, string password)
    {
        Console.WriteLine($"Logging in as '{username}'...");

        // Start login (this is fire-and-forget, callbacks will handle the result)
        _connectionService.User.LogOn(new SteamUser.LogOnDetails
        {
            Username = username,
            Password = password,
            ShouldRememberPassword = true
        });

        // Handle callbacks for login
        var tcs = new TaskCompletionSource<bool>();
        Action<SteamUser.LoggedOnCallback>? loggedOnHandler = null;
        Action<SteamUser.LoggedOffCallback>? loggedOffHandler = null;

        loggedOnHandler = callback =>
        {
            if (callback.Result == EResult.OK)
            {
                Console.WriteLine("✅ Login successful!");
                Console.WriteLine($"   Steam ID: {callback.ClientSteamID}");
                Console.WriteLine($"   Cell ID: {callback.CellID}");

                IsAuthenticated = true;
                Username = username;
                tcs.TrySetResult(true);
            }
            else if (callback.Result == EResult.AccountLogonDenied)
            {
                Console.WriteLine("❌ Steam Guard required - email code needed");
                Console.Write("Enter Steam Guard code from email: ");
                var code = Console.ReadLine();

                // Retry with Steam Guard code
                _connectionService.User.LogOn(new SteamUser.LogOnDetails
                {
                    Username = username,
                    Password = password,
                    AuthCode = code,
                    ShouldRememberPassword = true
                });
            }
            else if (callback.Result == EResult.TwoFactorCodeMismatch || callback.Result == EResult.AccountLoginDeniedNeedTwoFactor)
            {
                Console.WriteLine("❌ Two-factor authentication required - mobile code needed");
                Console.Write("Enter 2FA code from mobile app: ");
                var code = Console.ReadLine();

                // Retry with 2FA code
                _connectionService.User.LogOn(new SteamUser.LogOnDetails
                {
                    Username = username,
                    Password = password,
                    TwoFactorCode = code,
                    ShouldRememberPassword = true
                });
            }
            else
            {
                Console.WriteLine($"❌ Login failed: {callback.Result}");
                Console.WriteLine($"   Extended result: {callback.ExtendedResult}");
                tcs.TrySetResult(false);
            }
        };

        loggedOffHandler = callback =>
        {
            Console.WriteLine($"❌ Logged off: {callback.Result}");
            IsAuthenticated = false;
            tcs.TrySetResult(false);
        };

        using var loggedOnSubscription = _connectionService.CallbackManager.Subscribe(loggedOnHandler);
        using var loggedOffSubscription = _connectionService.CallbackManager.Subscribe(loggedOffHandler);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        // Wait for login result
        while (!tcs.Task.IsCompleted && !cts.Token.IsCancellationRequested)
        {
            _connectionService.CallbackManager.RunWaitCallbacks(TimeSpan.FromMilliseconds(100));
            await Task.Delay(10);
        }

        return await tcs.Task;
    }

    /// <summary>
    /// Get user's owned app IDs (only works when authenticated)
    /// </summary>
    public async Task<List<uint>> GetOwnedAppIdsAsync()
    {
        if (!IsAuthenticated)
        {
            throw new InvalidOperationException("Must be authenticated to get owned apps");
        }

        Console.WriteLine("Fetching owned apps from licenses...");

        // Wait for license list
        var tcs = new TaskCompletionSource<List<uint>>();
        Action<SteamApps.LicenseListCallback>? handler = null;

        handler = callback =>
        {
            var ownedAppIds = new HashSet<uint>();

            Console.WriteLine($"Received {callback.LicenseList.Count} licenses");

            // Extract app IDs from licenses/packages
            foreach (var license in callback.LicenseList)
            {
                // Note: We'd need to query package info to get the actual app IDs
                // For now, we'll use the PackageID as an approximation
                // In a real implementation, we'd call PICSGetProductInfo for packages
                Console.WriteLine($"  License: Package {license.PackageID}, Type: {license.LicenseType}");
            }

            // For a complete implementation, we'd need to query package info
            // to get the actual app IDs within each package
            Console.WriteLine("Note: Complete owned app enumeration requires package info queries");
            Console.WriteLine("      (Not implemented in this version - would query each package)");

            tcs.TrySetResult(ownedAppIds.ToList());
        };

        using var subscription = _connectionService.CallbackManager.Subscribe(handler!);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Request license list (automatically sent on login, but we can wait for it)
        while (!tcs.Task.IsCompleted && !cts.Token.IsCancellationRequested)
        {
            _connectionService.CallbackManager.RunWaitCallbacks(TimeSpan.FromMilliseconds(100));
            await Task.Delay(10);
        }

        if (cts.Token.IsCancellationRequested)
        {
            Console.WriteLine("⚠️  Timeout waiting for license list");
            return [];
        }

        return await tcs.Task;
    }
}
