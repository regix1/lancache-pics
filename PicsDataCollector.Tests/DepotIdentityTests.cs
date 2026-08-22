using PicsDataCollector.Models;
using PicsDataCollector.Services;
using SteamKit2;
using Xunit;

namespace PicsDataCollector.Tests;

public sealed class DepotIdentityTests
{
    [Fact]
    public void VtolDlcDepot_UsesDlcAppForManifestAndLicense()
    {
        var service = new DepotMappingService(new SteamConnectionService());
        service.ProcessAppDepots(667970, VtolGame(listsDlcDepot: false));
        service.ProcessAppDepots(1770480, VtolDlc());

        var relationship = service.DepotRelationships[1770480][1770480];
        Assert.Equal(1770480u, relationship.ManifestAppId);
        Assert.Equal(1770480u, relationship.LicenseAppId);

        service.RebuildDerivedOwners();
        Assert.Equal(1770480u, service.DepotOwners[1770480]);
    }

    [Fact]
    public void DlcAlreadyNamed_IsNotQueuedAndKeepsItsSourceIdentity()
    {
        var service = new DepotMappingService(new SteamConnectionService());
        service.LoadExistingMappings(
            new Dictionary<uint, HashSet<uint>>(),
            new Dictionary<uint, string> { [1770480] = "AH-94" });

        var queued = service.ProcessAppDepots(667970, VtolGame(listsDlcDepot: false));
        service.ProcessAppDepots(1770480, VtolDlc());

        Assert.DoesNotContain(1770480u, queued);
        Assert.Equal(1770480u, service.DepotRelationships[1770480][1770480].ManifestAppId);
    }

    [Fact]
    public void StoredRelationshipUsingParentApp_IsNormalizedOnLoad()
    {
        var service = new DepotMappingService(new SteamConnectionService());
        service.LoadExistingMappings(
            new Dictionary<uint, HashSet<uint>> { [1770481] = [1770480] },
            new Dictionary<uint, string> { [1770480] = "AH-94", [667970] = "VTOL VR" },
            relationships: new Dictionary<uint, List<PicsDepotRelationship>>
            {
                [1770481] =
                [
                    new PicsDepotRelationship
                    {
                        SourceAppId = 1770480,
                        DlcAppId = 1770480,
                        HasPublicManifest = true,
                        ManifestAppId = 667970,
                        LicenseAppId = 1770480
                    }
                ]
            });

        Assert.Equal(1770480u, service.DepotRelationships[1770481][1770480].ManifestAppId);
    }

    [Fact]
    public void DlcScanOrderDoesNotChangeManifestIdentity()
    {
        var service = new DepotMappingService(new SteamConnectionService());
        service.ProcessAppDepots(1770480, VtolDlc(selfLinkedDepotFromApp: true));
        service.ProcessAppDepots(667970, VtolGame(listsDlcDepot: false));

        var relationship = service.DepotRelationships[1770480][1770480];
        Assert.Equal(1770480u, relationship.ManifestAppId);
        Assert.Equal(1770480u, relationship.LicenseAppId);
    }

    [Fact]
    public void ExtendedListOfDlc_QueuesUnknownDlc()
    {
        var service = new DepotMappingService(new SteamConnectionService());
        var game = new KeyValue("667970")
        {
            Children =
            {
                new KeyValue("common")
                {
                    Children =
                    {
                        new KeyValue("name", "VTOL VR"),
                        new KeyValue("type", "game")
                    }
                },
                new KeyValue("extended")
                {
                    Children = { new KeyValue("listofdlc", "1770480") }
                }
            }
        };

        var queued = service.ProcessAppDepots(667970, game);

        Assert.Contains(1770480u, queued);
    }

    [Fact]
    public void ManifestlessSelfLinkedDepot_IsInvalid()
    {
        Assert.True(DepotIdentity.IsInvalidDepot(false, 4399490, 4399490));
        Assert.True(DepotIdentity.IsInvalidDepot(false, 123, null));
    }

    [Fact]
    public void ManifestlessDlcDepot_IsSkippedByTheScan()
    {
        var service = new DepotMappingService(new SteamConnectionService());

        var dlc = new KeyValue("4399490")
        {
            Children =
            {
                new KeyValue("common")
                {
                    Children =
                    {
                        new KeyValue("name", "Delisted Content"),
                        new KeyValue("type", "dlc")
                    }
                },
                new KeyValue("depots")
                {
                    Children =
                    {
                        new KeyValue("4399490")
                        {
                            Children = { new KeyValue("depotfromapp", "4399490") }
                        }
                    }
                }
            }
        };

        service.ProcessAppDepots(4399490, dlc);

        Assert.False(service.DepotRelationships.ContainsKey(4399490));
        Assert.False(service.DepotMappings.ContainsKey(4399490));
    }

    [Fact]
    public void SharedDepot_UsesLinkedAppForManifest()
    {
        var relationship = DepotIdentity.CreateRelationship(222, 123, 333, 444, true);

        Assert.Equal(333u, relationship.ManifestAppId);
        Assert.Equal(444u, relationship.LicenseAppId);
    }

    [Fact]
    public void OwnerSelection_PrefersDepotFromAppThenGameThenStableId()
    {
        var types = new Dictionary<uint, string>
        {
            [1770480] = "dlc",
            [667970] = "game",
            [70] = "game"
        };

        var vtolOwner = DepotIdentity.SelectOwnerId(
            [
                new PicsDepotRelationship { SourceAppId = 1770480, DlcAppId = 1770480 }
            ],
            types);
        Assert.Equal(1770480u, vtolOwner);

        var gamePreferred = DepotIdentity.SelectOwnerId(
            [
                new PicsDepotRelationship { SourceAppId = 1770480, DlcAppId = 1770480 },
                new PicsDepotRelationship { SourceAppId = 667970, DlcAppId = 1770480 }
            ],
            types);
        Assert.Equal(667970u, gamePreferred);

        var sharedOwner = DepotIdentity.SelectOwnerId(
            [
                new PicsDepotRelationship { SourceAppId = 222, DepotFromAppId = 333 }
            ],
            types);
        Assert.Equal(333u, sharedOwner);
    }

    [Fact]
    public void HasPublicManifest_ReadsGidAndLegacyValues()
    {
        var withGid = new KeyValue("1770480")
        {
            Children =
            {
                new KeyValue("manifests")
                {
                    Children =
                    {
                        new KeyValue("public")
                        {
                            Children = { new KeyValue("gid", "2836902461265788005") }
                        }
                    }
                }
            }
        };
        Assert.True(DepotMappingService.HasPublicManifest(withGid));

        var selfLinked = new KeyValue("4399490")
        {
            Children = { new KeyValue("depotfromapp", "4399490") }
        };
        Assert.False(DepotMappingService.HasPublicManifest(selfLinked));
    }

    [Fact]
    public void ExplicitlyEmptyPublicManifest_IsSkippedButContentDepotRemains()
    {
        var dlc = new KeyValue("1770480")
        {
            Children =
            {
                new KeyValue("common")
                {
                    Children =
                    {
                        new KeyValue("name", "AH-94"),
                        new KeyValue("type", "dlc")
                    }
                },
                new KeyValue("depots")
                {
                    Children =
                    {
                        DepotWithManifest(1770480, 2836902461265788005, 0, 0),
                        DepotWithManifest(1770481, 4625979481897414804, 57120063, 57716032)
                    }
                }
            }
        };
        var service = new DepotMappingService(new SteamConnectionService());

        service.ProcessAppDepots(1770480, dlc);

        Assert.False(service.DepotMappings.ContainsKey(1770480));
        Assert.True(service.DepotMappings.ContainsKey(1770481));
        Assert.Equal(1770480u, service.DepotRelationships[1770481][1770480].ManifestAppId);
    }

    [Fact]
    public void Schema11_RoundTripsRelationships_AndV1JsonStillLoads()
    {
        var service = new DepotMappingService(new SteamConnectionService());
        service.ProcessAppDepots(667970, VtolGame(listsDlcDepot: true));
        service.ProcessAppDepots(1770480, VtolDlc());
        service.RebuildDerivedOwners();

        var persistence = new DataPersistenceService(Path.GetTempFileName());
        var data = persistence.BuildPicsData(
            service.DepotMappings.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            service.AppNames.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            10,
            service.DepotOwners.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            service.AppTypes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            depotRelationships: service.DepotRelationships.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.Values.ToList()));

        var mapping = data.DepotMappings!["1770480"];
        Assert.Equal("1.1", data.Metadata?.Version);
        Assert.Equal(667970u, mapping.OwnerId);
        Assert.Equal(667970u, mapping.AppIds![0]);
        Assert.Equal(2, mapping.Relationships!.Count);
        Assert.Equal(667970u, mapping.Relationships.Single(r => r.SourceAppId == 667970).ManifestAppId);
        Assert.Equal(1770480u, mapping.Relationships.Single(r => r.SourceAppId == 1770480).ManifestAppId);

        var v1Json = """
            {
              "metadata": { "version": "1.0", "lastChangeNumber": 3 },
              "depotMappings": {
                "70": {
                  "ownerId": 70,
                  "appIds": [70],
                  "appNames": ["Half-Life"],
                  "appTypes": ["game"]
                }
              }
            }
            """;
        var extracted = persistence.ExtractMappingsFromData(DeserializeV1(v1Json));

        Assert.Equal(70u, extracted.depotOwners[70]);
        Assert.Empty(extracted.relationships);
    }

    [Fact]
    public void V1MappingWithoutOwnerId_LeavesOwnerUnset()
    {
        var persistence = new DataPersistenceService(Path.GetTempFileName());
        var v1Json = """
            {
              "metadata": { "version": "1.0", "lastChangeNumber": 3 },
              "depotMappings": {
                "1770480": {
                  "appIds": [1770480, 667970],
                  "appNames": ["AH-94", "VTOL VR"],
                  "appTypes": ["dlc", "game"]
                }
              }
            }
            """;

        var extracted = persistence.ExtractMappingsFromData(DeserializeV1(v1Json));

        Assert.Empty(extracted.depotOwners);
        Assert.Contains(667970u, extracted.depotMappings[1770480]);
    }

    [Fact]
    public void OwnerOutsideAppIds_IsNotAddedToThePublishedLists()
    {
        var persistence = new DataPersistenceService(Path.GetTempFileName());

        var data = persistence.BuildPicsData(
            new Dictionary<uint, HashSet<uint>> { [1770480] = [1770480] },
            new Dictionary<uint, string> { [1770480] = "AH-94" },
            10,
            new Dictionary<uint, uint> { [1770480] = 667970 },
            new Dictionary<uint, string> { [1770480] = "dlc" });

        var mapping = data.DepotMappings!["1770480"];
        Assert.Null(mapping.OwnerId);
        Assert.DoesNotContain(667970u, mapping.AppIds!);
        Assert.Single(mapping.AppIds!);
        Assert.Single(mapping.AppNames!);
        Assert.Single(mapping.AppTypes!);
        Assert.Single(mapping.AppHeaderImages!);
    }

    private static PicsJsonData? DeserializeV1(string json)
    {
        return System.Text.Json.JsonSerializer.Deserialize<PicsJsonData>(
            json,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
    }

    private static KeyValue VtolGame(bool listsDlcDepot)
    {
        var game = new KeyValue("667970")
        {
            Children =
            {
                new KeyValue("common")
                {
                    Children =
                    {
                        new KeyValue("name", "VTOL VR"),
                        new KeyValue("type", "game"),
                        new KeyValue("listofdlc", "1770480")
                    }
                }
            }
        };

        if (listsDlcDepot)
        {
            game.Children.Add(new KeyValue("depots")
            {
                Children =
                {
                    new KeyValue("1770480")
                    {
                        Children =
                        {
                            new KeyValue("dlcappid", "1770480"),
                            PublicManifest()
                        }
                    }
                }
            });
        }

        return game;
    }

    private static KeyValue VtolDlc(bool selfLinkedDepotFromApp = false)
    {
        KeyValue depotKey;
        if (selfLinkedDepotFromApp)
        {
            depotKey = new KeyValue("1770480")
            {
                Children =
                {
                    new KeyValue("depotfromapp", "1770480"),
                    PublicManifest()
                }
            };
        }
        else
        {
            depotKey = new KeyValue("1770480")
            {
                Children = { PublicManifest() }
            };
        }

        return new KeyValue("1770480")
        {
            Children =
            {
                new KeyValue("common")
                {
                    Children =
                    {
                        new KeyValue("name", "AH-94"),
                        new KeyValue("type", "dlc")
                    }
                },
                new KeyValue("depots")
                {
                    Children = { depotKey }
                }
            }
        };
    }

    private static KeyValue DepotWithManifest(uint depotId, ulong gid, ulong size, ulong download)
    {
        return new KeyValue(depotId.ToString())
        {
            Children =
            {
                new KeyValue("dlcappid", "1770480"),
                new KeyValue("manifests")
                {
                    Children =
                    {
                        new KeyValue("public")
                        {
                            Children =
                            {
                                new KeyValue("gid", gid.ToString()),
                                new KeyValue("size", size.ToString()),
                                new KeyValue("download", download.ToString())
                            }
                        }
                    }
                }
            }
        };
    }

    private static KeyValue PublicManifest()
    {
        return new KeyValue("manifests")
        {
            Children =
            {
                new KeyValue("public")
                {
                    Children = { new KeyValue("gid", "2836902461265788005") }
                }
            }
        };
    }
}
