# Steam PICS Data Collection

Automated collection of Steam application data and depot-to-app mappings using Steam Web API and the Product Information and Content System (PICS). Updated every 4 hours via GitHub Actions.

## What is this?

Steam organizes game content into "depots" (content packages) and "apps" (games/applications). This project maintains two comprehensive datasets:

1. **Complete Steam App List** - All Steam applications (games, DLC, software, videos, hardware)
2. **Depot-to-App Mappings** - Detailed depot ownership and relationships

These datasets are essential for lancache management tools, content distribution analysis, game update tracking, and Steam platform research.

## Data Files

### 1. Steam Apps (`steam_apps.json`)

Complete list of all Steam applications with metadata.

**Download:**
```bash
curl -LO https://github.com/regix1/lancache-pics/releases/latest/download/steam_apps.json
```

**Format:**
```json
{
  "metadata": {
    "lastUpdated": "2025-11-14T22:00:00Z",
    "totalApps": 205101,
    "version": "1.0",
    "source": "IStoreService/GetAppList/v1"
  },
  "apps": [
    {
      "appId": 10,
      "name": "Counter-Strike",
      "type": "game"
    },
    {
      "appId": 220,
      "name": "Half-Life 2",
      "type": "game"
    },
    {
      "appId": 228980,
      "name": "Steamworks Common Redistributables",
      "type": "dlc"
    }
  ]
}
```

**Types:** `game`, `dlc`, `software`, `video`, `hardware`

### 2. Depot Mappings (`pics_depot_mappings.json`)

Detailed depot-to-app relationships collected via PICS.

```json
{
  "metadata": {
    "lastUpdated": "2025-10-05T14:47:46.0312519Z",
    "totalMappings": 299599,
    "version": "1.0",
    "nextUpdateDue": "2025-10-07T14:47:46.0587563Z",
    "lastChangeNumber": 31491124
  },
  "depotMappings": {
    "1": {
      "ownerId": 70,
      "appIds": [70],
      "appNames": ["Half-Life"],
      "source": "SteamKit2-PICS",
      "discoveredAt": "2025-10-05T14:47:46.0622456Z"
    }
  }
}
```

**Fields:**
- `ownerId` - Primary app that owns this depot
- `appIds` - All apps that use this depot
- `appNames` - Corresponding app names
- `lastChangeNumber` - PICS change number for incremental updates

**Download:**
```bash
curl -LO https://github.com/regix1/lancache-pics/releases/latest/download/pics_depot_mappings.json
```

## Quick Start

**Download both datasets:**
```bash
# Steam app list
curl -LO https://github.com/regix1/lancache-pics/releases/latest/download/steam_apps.json

# Depot mappings
curl -LO https://github.com/regix1/lancache-pics/releases/latest/download/pics_depot_mappings.json
```

Or visit [Releases](https://github.com/regix1/lancache-pics/releases/latest)

**Run locally:**
```bash
git clone https://github.com/regix1/lancache-pics.git
cd lancache-pics/PicsDataCollector

# Set Steam API key (get one at https://steamcommunity.com/dev/apikey)
export STEAM_API_KEY="YOUR_KEY_HERE"

dotnet run              # Incremental update
dotnet run -- --full    # Full update
```

## How It Works

The collector runs two parallel operations:

**1. Steam App List Collection**
- Fetches complete app list via `IStoreService/GetAppList/v1`
- Requires Steam API key
- Includes games, DLC, software, videos, hardware
- Paginated: ~50,000 apps per request
- Fast: ~2-5 minutes for ~205k apps

**2. PICS Depot Mapping**

**Incremental Updates** (Every 4 hours)
- Queries PICS for changes since last update
- Updates only modified apps/depots
- Fast: ~5-10 minutes

**Full Updates** (Every Sunday at 4 AM UTC)
- Rebuilds entire depot mapping dataset
- Queries all ~170k active apps
- Slow: ~60-90 minutes

### Manual Updates

1. Go to [Actions](https://github.com/regix1/lancache-pics/actions)
2. Select **Update PICS Depot Mappings**
3. Click **Run workflow**
4. Choose `incremental` or `full`

## Configuration

**GitHub Actions Setup:**

1. Add Steam API key as repository secret:
   - Go to repository **Settings** → **Secrets and variables** → **Actions**
   - Add secret: `STEAM_KEY` = Your Steam API key

2. **Change update frequency** in `.github/workflows/update-pics-data.yml`:
```yaml
schedule:
  - cron: '0 */4 * * *'  # Incremental: Every 4 hours
  - cron: '0 4 * * 0'    # Full: Every Sunday at 4 AM UTC
```

## Technical Details

**Data Sources:**
- Steam App List: `IStoreService/GetAppList/v1` (Steam Web API with key)
- Depot Mappings: SteamKit2 PICS protocol (anonymous connection)

**Features:**
- Dual-JSON output system
- API fallback: tries v2 (no key), falls back to v1 (with key)
- Rate limiting: 150ms between batches, 200 apps per batch
- Incremental updates: tracks `lastChangeNumber` for efficiency
- Automatic releases with both datasets

**Dependencies:**
- SteamKit2 (PICS + WebAPI wrapper)
- System.Text.Json

## Related Projects

- [SteamKit2](https://github.com/SteamRE/SteamKit)
- [SteamDatabase](https://steamdb.info/)
- [Lancache](https://lancache.net/)

## License

Provided as-is for community use. Steam data belongs to Valve Corporation.
