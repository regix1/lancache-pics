# Setup Guide

## GitHub Repository Setup

1. **Create a new GitHub repository**
   - Go to https://github.com/new
   - Name it `lancache-pics` (or your preferred name)
   - Make it public (so the data is accessible)
   - **Do NOT** initialize with README, .gitignore, or license (we already have these)

2. **Push your local repository**
   ```bash
   cd H:\_git\lancache-pics
   git remote add origin https://github.com/YOUR_USERNAME/lancache-pics.git
   git branch -M main
   git push -u origin main
   ```

3. **Enable GitHub Actions**
   - Actions should be enabled by default
   - The workflow will run automatically every 2 days
   - First run should trigger shortly after push

## Initial Data Collection

You have two options to populate the initial data:

### Option 1: Manual Local Run (Recommended)

Run locally first to create the initial `pics_depot_mappings.json`:

```bash
cd H:\_git\lancache-pics\PicsDataCollector
dotnet run -- --full
```

This will:
- Connect to Steam anonymously
- Download depot mappings
- Create `pics_depot_mappings.json`
- Take 30-60 minutes depending on connection

Then commit and push:
```bash
git add PicsDataCollector/pics_depot_mappings.json
git commit -m "Initial PICS depot mappings data"
git push
```

### Option 2: Let GitHub Actions Do It

Manually trigger the workflow:
1. Go to your repository on GitHub
2. Click **Actions** tab
3. Select **Update PICS Depot Mappings**
4. Click **Run workflow**
5. Select **full** mode
6. Click **Run workflow**

## Verifying the Setup

### Check Workflow Runs
- Go to Actions tab in your repository
- You should see workflow runs
- Green checkmark = success
- Red X = failure (check logs)

### Check Data File
- Navigate to `PicsDataCollector/pics_depot_mappings.json` in your repo
- Should contain depot mappings
- Check the metadata section for update info

### Monitor Automated Updates
- Workflow runs every 2 days at 3 AM UTC
- Check commit history for automated commits
- Commits will have format: "Update PICS depot mappings - X mappings"

## Troubleshooting

### Workflow Fails with Timeout
- Increase timeout in `.github/workflows/update-pics-data.yml`
- Change `timeout-minutes: 60` to `120` or higher

### No Changes Being Committed
- This is normal if Steam data hasn't changed
- Incremental updates only commit when changes are detected

### Repository Disabled After 60 Days
- GitHub disables scheduled workflows after 60 days of repo inactivity
- Fix: Make any commit or manually trigger the workflow
- The workflow will resume its schedule

### Connection Issues
- GitHub Actions runners should have no issues
- If running locally, check firewall/proxy settings
- Steam connections use standard HTTPS

## Customization

### Change Update Frequency

Edit `.github/workflows/update-pics-data.yml`:

```yaml
schedule:
  - cron: '0 3 */2 * *'  # Every 2 days
```

Examples:
- Daily: `0 3 * * *`
- Every 3 days: `0 3 */3 * *`
- Weekly: `0 3 * * 0`
- Twice daily: `0 3,15 * * *`

### Adjust Rate Limiting

Edit `PicsDataCollector/Program.cs`:

```csharp
private const int AppBatchSize = 200;  // Increase for faster, decrease for safer
await Task.Delay(150); // Delay between batches in ms
```

## Accessing the Data

### Raw JSON URL
```
https://raw.githubusercontent.com/YOUR_USERNAME/lancache-pics/main/PicsDataCollector/pics_depot_mappings.json
```

### Using in Your Application

C# example:
```csharp
var json = await httpClient.GetStringAsync("https://raw.githubusercontent.com/YOUR_USERNAME/lancache-pics/main/PicsDataCollector/pics_depot_mappings.json");
var data = JsonSerializer.Deserialize<PicsJsonData>(json);
```

Python example:
```python
import requests
url = "https://raw.githubusercontent.com/YOUR_USERNAME/lancache-pics/main/PicsDataCollector/pics_depot_mappings.json"
data = requests.get(url).json()
```

JavaScript example:
```javascript
const response = await fetch('https://raw.githubusercontent.com/YOUR_USERNAME/lancache-pics/main/PicsDataCollector/pics_depot_mappings.json');
const data = await response.json();
```

## Next Steps

1. Create the GitHub repository
2. Push your code
3. Run initial data collection
4. Set up repository settings (optional):
   - Add topics: `steam`, `pics`, `depot-mappings`, `lancache`
   - Add description: "Automated Steam PICS depot mappings dataset"
5. Share the repository with your community
