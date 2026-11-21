using PicsDataCollector.Services;
using SteamKit2;
using Xunit;

namespace PicsDataCollector.Tests;

public sealed class PicsReplyTests
{
    [Fact]
    public void NoRepliesForRequestedApps_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            DepotMappingService.EnsureProductInfoReturnedApps([], 500));

        Assert.Contains("500 requested", ex.Message);
    }

    [Fact]
    public async Task PendingApps_SurviveSaveAndLoad()
    {
        var path = Path.GetTempFileName();
        var persistence = new DataPersistenceService(path);

        await persistence.SaveToJsonAsync(
            new Dictionary<uint, HashSet<uint>> { [11] = [10] },
            new Dictionary<uint, string> { [10] = "Kept" },
            lastChangeNumber: 42,
            pendingAppIds: [2767030, 3527290]);

        var (data, changeNumber) = await persistence.LoadExistingDataAsync();
        File.Delete(path);

        Assert.Equal(42u, changeNumber);
        Assert.Equal([2767030u, 3527290u], data!.Metadata!.PendingAppIds);
        Assert.True(data.DepotMappings!.ContainsKey("11"));
    }

    [Fact]
    public async Task FileWithoutPendingApps_LoadsAsEmpty()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, """{"metadata":{"lastChangeNumber":7},"depotMappings":{}}""");

        var (data, _) = await new DataPersistenceService(path).LoadExistingDataAsync();
        File.Delete(path);

        Assert.Empty(data!.Metadata!.PendingAppIds);
    }

    [Fact]
    public void Prune_DropsOnlyDepotsNotSeenThisRun()
    {
        var service = new DepotMappingService(new SteamConnectionService());
        service.LoadExistingMappings(
            new Dictionary<uint, HashSet<uint>> { [11] = [10], [21] = [20], [31] = [30] },
            new Dictionary<uint, string> { [10] = "Still listed", [20] = "Delisted", [30] = "Pending" });

        service.ProcessAppDepots(10, App("Still listed", 11));
        var removed = service.PruneUnrefreshed(new HashSet<uint> { 30 });

        Assert.Equal(1, removed);
        Assert.True(service.DepotMappings.ContainsKey(11));
        Assert.False(service.DepotMappings.ContainsKey(21));
        Assert.True(service.DepotMappings.ContainsKey(31));
    }

    [Fact]
    public void Prune_DropsDepotAnAppNoLongerLists()
    {
        var service = new DepotMappingService(new SteamConnectionService());
        service.ProcessAppDepots(10, App("Game", 11, 12));

        var rescan = new DepotMappingService(new SteamConnectionService());
        rescan.LoadExistingMappings(
            service.DepotMappings.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            new Dictionary<uint, string> { [10] = "Game" },
            relationships: service.DepotRelationships.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Values.ToList()));
        rescan.ProcessAppDepots(10, App("Game", 11));
        rescan.PruneUnrefreshed(new HashSet<uint>());

        Assert.True(rescan.DepotMappings.ContainsKey(11));
        Assert.False(rescan.DepotMappings.ContainsKey(12));
    }

    [Fact]
    public void KnownAppIds_IncludesEveryAppTheOldFileHolds()
    {
        var service = new DepotMappingService(new SteamConnectionService());
        service.LoadExistingMappings(
            new Dictionary<uint, HashSet<uint>> { [11] = [10], [21] = [20, 22] },
            new Dictionary<uint, string> { [10] = "Listed", [20] = "Unlisted DLC" });

        Assert.Equal([10u, 20u, 22u], service.KnownAppIds());
    }

    [Fact]
    public void Prune_DropsDepotsOfAnAppThatNowHasNone()
    {
        var service = new DepotMappingService(new SteamConnectionService());
        service.ProcessAppDepots(10, App("Game", 11));

        var rescan = new DepotMappingService(new SteamConnectionService());
        rescan.LoadExistingMappings(
            service.DepotMappings.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            new Dictionary<uint, string> { [10] = "Game" },
            relationships: service.DepotRelationships.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Values.ToList()));
        rescan.ProcessAppDepots(10, App("Game"));

        Assert.Equal(1, rescan.PruneUnrefreshed(new HashSet<uint>()));
    }

    [Fact]
    public void DlcKnownOnlyByName_IsQueuedOnce()
    {
        var service = new DepotMappingService(new SteamConnectionService());
        service.LoadExistingMappings([], new Dictionary<uint, string> { [20] = "Old DLC" });

        var app = App("Game");
        app["common"].Children.Add(new KeyValue("listofdlc", "20"));

        Assert.Equal([20u], service.ProcessAppDepots(10, app));
        Assert.Empty(service.ProcessAppDepots(11, app));
    }

    [Fact]
    public void EmptyReply_KeepsNameAndDepotsThroughPrune()
    {
        var service = new DepotMappingService(new SteamConnectionService());
        service.LoadExistingMappings(
            new Dictionary<uint, HashSet<uint>> { [11] = [10] },
            new Dictionary<uint, string> { [10] = "Locked Game" });

        service.ProcessAppDepots(10, new KeyValue("app"));

        Assert.Equal(0, service.PruneUnrefreshed(new HashSet<uint>()));
        Assert.Equal("Locked Game", service.AppNames[10]);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public void FullyKnownDlc_IsOnlyRefetchedOnFullRuns(bool refetchKnownDlc, int expectedQueued)
    {
        var service = new DepotMappingService(new SteamConnectionService()) { RefetchKnownDlc = refetchKnownDlc };
        service.LoadExistingMappings(
            [],
            new Dictionary<uint, string> { [20] = "Known DLC" },
            types: new Dictionary<uint, string> { [20] = "dlc" });

        var app = App("Game");
        app["common"].Children.Add(new KeyValue("listofdlc", "20"));

        Assert.Equal(expectedQueued, service.ProcessAppDepots(10, app).Count);
    }

    private static KeyValue App(string name, params uint[] depotIds)
    {
        var depots = new KeyValue("depots");
        foreach (var depotId in depotIds)
        {
            depots.Children.Add(new KeyValue(depotId.ToString())
            {
                Children =
                {
                    new KeyValue("manifests")
                    {
                        Children = { new KeyValue("public") { Children = { new KeyValue("gid", "1") } } }
                    }
                }
            });
        }

        return new KeyValue("app")
        {
            Children =
            {
                new KeyValue("common") { Children = { new KeyValue("name", name), new KeyValue("type", "game") } },
                depots
            }
        };
    }

    [Fact]
    public void NothingRequested_DoesNotThrow()
    {
        DepotMappingService.EnsureProductInfoReturnedApps(Array.Empty<SteamApps.PICSProductInfoCallback>(), 0);
    }
}
