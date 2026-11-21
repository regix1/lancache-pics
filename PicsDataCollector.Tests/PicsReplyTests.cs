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
    public void NothingRequested_DoesNotThrow()
    {
        DepotMappingService.EnsureProductInfoReturnedApps(Array.Empty<SteamApps.PICSProductInfoCallback>(), 0);
    }
}
