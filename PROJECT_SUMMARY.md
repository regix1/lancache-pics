# Project Summary

## What Was Built

A fully automated Steam PICS (Product Information and Content System) depot mapping collector that runs as a GitHub Actions workflow.

## Repository Structure

```
lancache-pics/
├── .github/
│   └── workflows/
│       └── update-pics-data.yml    # GitHub Actions workflow
├── PicsDataCollector/
│   ├── Program.cs                  # Main collector application
│   ├── PicsDataCollector.csproj    # .NET project file
│   ├── run-test.bat               # Windows test script
│   ├── run-test.sh                # Linux/Mac test script
│   └── pics_depot_mappings.json   # Output data (generated)
├── .gitignore
├── LICENSE
├── README.md
├── SETUP.md
└── PROJECT_SUMMARY.md             # This file
```

## How It Works

### Data Collection Process

1. **Connects to Steam** - Anonymous login via SteamKit2
2. **Queries PICS** - Gets app changes since last update using change numbers
3. **Fetches Depot Info** - Downloads depot information for changed apps
4. **Extracts Mappings** - Maps depot IDs to app IDs and names
5. **Saves JSON** - Outputs structured JSON file
6. **Commits Changes** - GitHub Actions commits if data changed

### Update Modes

**Incremental (Default)**
- Uses `lastChangeNumber` to track position
- Only processes apps that changed since last run
- Fast and efficient (minutes)
- Runs automatically every 2 days

**Full**
- Downloads all available data
- Starts from recent changelist position
- Slower but comprehensive (30-60 minutes)
- Manual trigger only

### Automation

**GitHub Actions Workflow**
- Scheduled: Every 2 days at 3 AM UTC
- Manual trigger: Via Actions tab with mode selection
- Auto-commits: Only when data changes
- Timeout protection: 60min incremental, 120min full

## Key Features

✅ **Fully Automated** - Runs on schedule without intervention
✅ **Incremental Updates** - Efficient change tracking
✅ **Version Control** - Full history of data changes
✅ **Public Access** - Data available via GitHub raw URLs
✅ **No Auth Required** - Anonymous Steam login
✅ **Rate Limited** - Respectful of Steam's API
✅ **Error Handling** - Graceful failure recovery
✅ **Progress Tracking** - Console output during collection

## Data Format

### Output: `pics_depot_mappings.json`

```json
{
  "metadata": {
    "lastUpdated": "2025-10-04T03:00:22Z",
    "totalMappings": 294314,
    "version": "1.0",
    "nextUpdateDue": "2025-10-06T03:00:22Z",
    "lastChangeNumber": 31475616
  },
  "depotMappings": {
    "1": {
      "appIds": [70],
      "appNames": ["Half-Life"],
      "source": "SteamKit2-PICS",
      "discoveredAt": "2025-10-04T03:00:22Z"
    }
  }
}
```

### Key Fields

- **lastChangeNumber** - Steam PICS position for incremental updates
- **totalMappings** - Count of depot-to-app relationships
- **depotMappings** - Dictionary of depot IDs to app information
- **appIds** - Array because depots can be shared across apps

## Technologies Used

- **.NET 8.0** - Runtime platform
- **SteamKit2** - Steam network communication
- **System.Text.Json** - JSON serialization
- **GitHub Actions** - Automation platform
- **Git** - Version control and data distribution

## Usage Examples

### Download Latest Data

```bash
curl -O https://raw.githubusercontent.com/USERNAME/lancache-pics/main/PicsDataCollector/pics_depot_mappings.json
```

### Query in Code

**C#**
```csharp
var data = await JsonSerializer.DeserializeAsync<PicsJsonData>(stream);
var depot1Apps = data.DepotMappings["1"].AppIds;
```

**Python**
```python
data = requests.get(raw_url).json()
apps = data['depotMappings']['1']['appIds']
```

**JavaScript**
```javascript
const data = await fetch(rawUrl).then(r => r.json());
const apps = data.depotMappings['1'].appIds;
```

## Deployment Steps

1. Create GitHub repository (public)
2. Push this code
3. Run initial collection (locally or via Actions)
4. Enable Actions workflow
5. Data updates automatically every 2 days

See `SETUP.md` for detailed instructions.

## Performance

- **Incremental Update**: ~5-10 minutes (depends on changes)
- **Full Update**: ~30-60 minutes (depends on connection)
- **Rate Limiting**: 150ms between batches
- **Batch Size**: 200 apps per batch
- **API Calls**: Minimal (change tracking + product info)

## Limitations

- **No Authentication** - Uses anonymous Steam login only
- **Public Data Only** - Cannot access private/restricted apps
- **Rate Limited** - Deliberately slow to respect Steam
- **GitHub Schedule** - Min 5 minute intervals (we use 2 days)
- **Workflow Timeout** - Max 6 hours (we use 2 hours)

## Future Enhancements

Possible improvements:
- [ ] Manifest size calculation
- [ ] App metadata enrichment (genres, release dates)
- [ ] Change detection notifications
- [ ] Historical data analysis
- [ ] Database export option
- [ ] API endpoint wrapper

## Credits

- **SteamKit2** by SteamRE team
- **Inspired by** SteamDatabase's data collection
- **Built for** the Lancache community

## License

MIT License - See LICENSE file
Steam data belongs to Valve Corporation
