# Steam PICS Depot Mappings

Automated collection of Steam depot-to-app mappings using the Product Information and Content System (PICS).

This repository uses GitHub Actions to automatically download and update Steam depot mappings every 2 days, providing a publicly accessible dataset for developers working with Steam content delivery.

## What is this?

Steam organizes game content into "depots" (content packages) and "apps" (games/applications). This project maintains an up-to-date mapping between depot IDs and their associated app IDs, which is essential for:

- Building lancache management tools
- Analyzing Steam content distribution
- Tracking game updates and changes
- Understanding Steam's content delivery network

## Data Format

The data is stored in `PicsDataCollector/pics_depot_mappings.json` with the following structure:

```json
{
  "metadata": {
    "lastUpdated": "2025-10-04T03:00:22.590218Z",
    "totalMappings": 294314,
    "version": "1.0",
    "nextUpdateDue": "2025-10-05T03:00:22.5965417Z",
    "lastChangeNumber": 31475616
  },
  "depotMappings": {
    "1": {
      "appIds": [70],
      "appNames": ["Half-Life"],
      "source": "SteamKit2-PICS",
      "discoveredAt": "2025-10-04T03:00:22.4805394Z"
    }
  }
}
```

### Fields

- **metadata.totalMappings**: Total number of depot-to-app relationships
- **metadata.lastChangeNumber**: Steam PICS change number (used for incremental updates)
- **depotMappings**: Dictionary of depot IDs to app information
  - **appIds**: Array of app IDs that use this depot
  - **appNames**: Corresponding names for the apps
  - **source**: Data collection method (SteamKit2-PICS)
  - **discoveredAt**: When this mapping was discovered

## How It Works

### Automated Updates

GitHub Actions runs on two schedules:

**Incremental Updates** (Every 2 days at 3 AM UTC):
1. Queries PICS for app changes since last update
2. Downloads depot information for changed apps only
3. Merges with existing data
4. Fast: ~5-10 minutes

**Full Updates** (Every Sunday at 4 AM UTC):
1. Downloads complete app list from Steam Web API (~170k apps)
2. Fetches depot information for all apps
3. Rebuilds entire dataset
4. Slow: ~60-90 minutes

### Update Modes

- **Incremental**: Only downloads changes since last update (scheduled every 2 days)
- **Full**: Downloads all data from scratch (scheduled weekly on Sundays)

## Usage

### Accessing the Data

**Option 1: Download from Latest Release (Recommended)**

Every time the data updates, a new release is automatically created:

```bash
# Get the latest release download URL
curl -s https://api.github.com/repos/regix1/lancache-pics/releases/latest | jq -r '.assets[0].browser_download_url' | xargs curl -LO
```

Or visit the [Releases page](https://github.com/regix1/lancache-pics/releases/latest) to download manually.

**Option 2: Download from Repository**

```bash
curl -O https://raw.githubusercontent.com/regix1/lancache-pics/main/output/pics_depot_mappings.json
```

**Option 3: Clone the Repository**

```bash
git clone https://github.com/regix1/lancache-pics.git
cd lancache-pics/output
cat pics_depot_mappings.json
```

### Running Locally

Prerequisites:
- .NET 8.0 SDK or later

```bash
# Clone the repository
git clone https://github.com/regix1/lancache-pics.git
cd lancache-pics/PicsDataCollector

# Restore dependencies
dotnet restore

# Run incremental update (default if file exists)
dotnet run

# Force full update
dotnet run -- --full

# Force incremental update
dotnet run -- --incremental
```

### Triggering Manual Updates

You can manually trigger an update via GitHub Actions:

1. Go to the [Actions tab](https://github.com/regix1/lancache-pics/actions)
2. Select **Update PICS Depot Mappings** workflow
3. Click **Run workflow**
4. Choose update mode:
   - **incremental**: Only process changes since last update (fast)
   - **full**: Complete re-download of all data (slow, ~60 minutes)

### Releases

Every time the data changes, the workflow automatically:
- Creates a new release with timestamp tag (e.g., `v2025.10.04-150030`)
- Attaches the `pics_depot_mappings.json` file to the release
- Provides download URL and metadata in release notes

Browse all releases: [github.com/regix1/lancache-pics/releases](https://github.com/regix1/lancache-pics/releases)

## Configuration

### Changing Update Frequency

Edit `.github/workflows/update-pics-data.yml`:

```yaml
schedule:
  - cron: '0 3 */2 * *'  # Every 2 days at 3 AM UTC
```

Cron syntax examples:
- `0 3 * * *` - Daily at 3 AM UTC
- `0 3 */3 * *` - Every 3 days at 3 AM UTC
- `0 3 * * 0` - Weekly on Sundays at 3 AM UTC

### Timeout Adjustments

The workflow has timeouts to prevent hanging:
- Incremental updates: 60 minutes
- Full updates: 120 minutes

Adjust in the workflow file if needed.

## Technical Details

### Dependencies

- **SteamKit2**: .NET library for Steam network interaction
- **System.Text.Json**: JSON serialization

### Rate Limiting

The collector includes rate limiting to be respectful of Steam's API:
- 150ms delay between app batches
- 100ms delay between PICS changelist queries
- 200 apps per batch (prevents timeout issues)

### Incremental Updates

The system tracks Steam's PICS change number (`lastChangeNumber`) to enable efficient incremental updates. Only apps that changed since the last update are processed.

## Contributing

Contributions are welcome! Feel free to:

- Report issues with the data
- Suggest improvements to the collection process
- Submit pull requests

## License

This project is provided as-is for use by the community. The Steam data belongs to Valve Corporation.

## Related Projects

- **SteamKit2**: https://github.com/SteamRE/SteamKit
- **SteamDatabase**: https://steamdb.info/
- **Lancache**: https://lancache.net/

## Acknowledgments

- Built using [SteamKit2](https://github.com/SteamRE/SteamKit) by the SteamRE team
- Inspired by SteamDatabase's data collection efforts
- Created for the Lancache community
