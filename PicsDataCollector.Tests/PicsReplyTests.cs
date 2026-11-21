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
    public void NothingRequested_DoesNotThrow()
    {
        DepotMappingService.EnsureProductInfoReturnedApps(Array.Empty<SteamApps.PICSProductInfoCallback>(), 0);
    }
}
