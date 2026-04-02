# Steam PICS Depot Mappings

Automated collection of Steam depot-to-app mappings using the Product Information and Content System (PICS) via SteamKit2. Updated hourly via GitHub Actions and published to [Releases](https://github.com/regix1/lancache-pics/releases/latest).

## What is this?

Steam organizes game content into "depots" (content packages) and "apps" (games/applications). This project maintains a comprehensive depot-to-app mapping dataset including app names, types, header images, and ownership data. It's built for lancache management tools, content distribution analysis, and Steam platform research.

## Download

```bash
curl -LO https://github.com/regix1/lancache-pics/releases/latest/download/pics_depot_mappings.json
```

Or visit the [latest release](https://github.com/regix1/lancache-pics/releases/latest).

## Data Format

### `pics_depot_mappings.json`

```json
{
  "metadata": {
    "lastUpdated": "2025-10-05T14:47:46Z",
    "totalMappings": 299599,
    "version": "1.0",
    "nextUpdateDue": "2025-10-07T14:47:46Z",
    "lastChangeNumber": 31491124
  },
  "depotMappings": {
    "1": {
      "ownerId": 70,
      "appIds": [70],
      "appNames": ["Half-Life"],
      "appTypes": ["game"],
      "appHeaderImages": ["https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/70/header.jpg"],
      "source": "SteamKit2-PICS",
      "discoveredAt": "2025-10-05T14:47:46Z"
    }
  }
}
```

### Fields

| Field | Description |
|-------|-------------|
| `ownerId` | Primary app that owns this depot (from PICS `depotfromapp`) |
| `appIds` | All apps that reference this depot |
| `appNames` | Corresponding app names |
| `appTypes` | App types: `game`, `dlc`, `demo`, `software`, `video`, `hardware` |
| `appHeaderImages` | Steam CDN header image URLs for each app |
| `source` | Data source identifier |
| `discoveredAt` | Timestamp when the depot mapping was first collected |

### Metadata

| Field | Description |
|-------|-------------|
| `lastUpdated` | When the dataset was last updated |
| `totalMappings` | Number of depot mappings in the file |
| `lastChangeNumber` | PICS change number for incremental tracking |
| `nextUpdateDue` | Scheduled next update time |

## How It Works

The collector connects to Steam anonymously via SteamKit2 and queries the PICS protocol for app and depot data.

### Collection Process

1. **App list retrieval** - Fetches the complete Steam app list via `ISteamApps/GetAppList/v2` (no API key needed), falling back to `IStoreService/GetAppList/v1` (requires API key)
2. **PICS enumeration** - Queries `PICSGetProductInfo` in batches of 200 apps with 150ms delay between batches
3. **Data extraction** - For each app, extracts depot IDs, depot ownership (`depotfromapp`), app type, app name, and header image URL from PICS KeyValue data
4. **DLC discovery** - Reads `listofdlc` from PICS data to discover and include DLC depots
5. **Output** - Writes `pics_depot_mappings.json` to `output/` and uploads to GitHub Releases

### Update Modes

| Mode | Schedule | Description |
|------|----------|-------------|
| **Incremental** | Every hour | Fetches only changes since last `lastChangeNumber` |
| **Full** | Sundays at 04:00 UTC | Rebuilds the entire dataset from all active apps |

Only creates a new release when the data has actually changed (SHA-256 hash comparison against the latest release).

## Run Locally

```bash
git clone https://github.com/regix1/lancache-pics.git
cd lancache-pics/PicsDataCollector

# Optional: set Steam API key for app list fallback
export STEAM_API_KEY="YOUR_KEY_HERE"

dotnet run                                    # auto-detect mode
dotnet run -- --incremental                   # incremental update
dotnet run -- --full                          # full rebuild
dotnet run -- --resolve-depots 123,456        # re-resolve specific depots
dotnet run -- --resolve-depots-file depots.txt # re-resolve depots from file
```

### CLI Arguments

| Argument | Description |
|----------|-------------|
| `--incremental` | Only fetch changes since last PICS change number |
| `--full` | Full enumeration of all active apps |
| `--resolve-depots <ids>` | Comma-separated depot IDs to re-resolve |
| `--resolve-depots-file <path>` | File with one depot ID per line to re-resolve |

No arguments defaults to auto-detection: incremental if existing data is found, full otherwise.

## GitHub Actions Setup

1. Add your Steam API key as a repository secret:
   - **Settings** > **Secrets and variables** > **Actions**
   - Add secret: `STEAM_KEY` = your Steam API key (from [steamcommunity.com/dev/apikey](https://steamcommunity.com/dev/apikey))

2. The workflow runs automatically on schedule. To trigger manually:
   - Go to [Actions](https://github.com/regix1/lancache-pics/actions)
   - Select **Update PICS Depot Mappings**
   - Click **Run workflow** and choose `incremental` or `full`

### Workflow Details

- **Concurrency**: queued (does not cancel in-progress runs)
- **Timeouts**: 60 minutes (incremental), 240 minutes (full)
- **Change detection**: SHA-256 hash comparison against latest release asset
- **Releases**: tagged `v{YYYY.MM.DD-HHmmss}`, only created when data changes

## Technical Details

- **Runtime**: .NET 8.0
- **Steam connection**: Anonymous login via SteamKit2 (no credentials required for PICS data)
- **Batch size**: 200 apps per PICS request, 150ms between batches
- **Header images**: Resolves `header_image` from PICS `common` section, validates URLs against multiple Steam CDN domains (`shared.akamai.steamstatic.com`, `shared.fastly.steamstatic.com`) and picks the first that responds
- **Dependencies**: [SteamKit2](https://github.com/SteamRE/SteamKit)

## Related Projects

- [SteamKit2](https://github.com/SteamRE/SteamKit) - .NET library for Valve's Steam network
- [SteamDatabase](https://steamdb.info/) - Steam database tracking
- [Lancache](https://lancache.net/) - LAN cache for game downloads

## License

Provided as-is for community use. Steam data belongs to Valve Corporation.
