# Steam PICS Depot Mappings

Automated collection of Steam depot-to-app mappings using the Product Information and Content System (PICS). Updated every 4 hours via GitHub Actions.

## What is this?

Steam organizes game content into "depots" (content packages) and "apps" (games/applications). This project maintains an up-to-date mapping between depot IDs and their associated app IDs, essential for lancache management tools, content distribution analysis, and tracking game updates.

## Data Format

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

## Quick Start

**Download latest data:**
```bash
curl -s https://api.github.com/repos/regix1/lancache-pics/releases/latest | jq -r '.assets[0].browser_download_url' | xargs curl -LO
```

Or visit [Releases](https://github.com/regix1/lancache-pics/releases/latest)

**Run locally:**
```bash
git clone https://github.com/regix1/lancache-pics.git
cd lancache-pics/PicsDataCollector
dotnet run              # Incremental update
dotnet run -- --full    # Full update
```

## How It Works

**Incremental Updates** (Every 4 hours)
- Queries PICS for changes since last update
- Fast: ~5-10 minutes

**Full Updates** (Every Sunday at 4 AM UTC)
- Downloads complete app list (~170k apps)
- Rebuilds entire dataset
- Slow: ~60-90 minutes

### Manual Updates

1. Go to [Actions](https://github.com/regix1/lancache-pics/actions)
2. Select **Update PICS Depot Mappings**
3. Click **Run workflow**
4. Choose `incremental` or `full`

## Configuration

**Change update frequency** in `.github/workflows/update-pics-data.yml`:
```yaml
schedule:
  - cron: '0 */4 * * *'  # Incremental: Every 4 hours
  - cron: '0 4 * * 0'    # Full: Every Sunday at 4 AM UTC
```

## Technical Details

- **Dependencies:** SteamKit2, System.Text.Json
- **Rate Limiting:** 150ms between batches, 200 apps per batch
- **Incremental Updates:** Tracks `lastChangeNumber` for efficiency

## Related Projects

- [SteamKit2](https://github.com/SteamRE/SteamKit)
- [SteamDatabase](https://steamdb.info/)
- [Lancache](https://lancache.net/)

## License

Provided as-is for community use. Steam data belongs to Valve Corporation.
