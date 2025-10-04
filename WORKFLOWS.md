# GitHub Actions Workflows

This repository uses GitHub Actions for automated builds and data updates.

## Workflows

### 1. Build and Test (`.github/workflows/build.yml`)

**Triggers:**
- Push to `main` branch
- Pull requests to `main`
- Manual trigger via Actions tab

**What it does:**
- Validates code compiles
- Runs on every commit
- Ensures code quality

**Usage:**
```bash
# Automatically runs on push/PR
# Or manually trigger from Actions tab
```

### 2. Update PICS Depot Mappings (`.github/workflows/update-pics-data.yml`)

**Triggers:**
- **Schedule**: Every 2 days at 3 AM UTC
- **Manual**: Via Actions tab with mode selection

**What it does:**
1. Connects to Steam via SteamKit2
2. Downloads depot mappings (incremental or full)
3. Commits changes if data updated
4. Creates GitHub release with JSON file
5. Generates summary report

**Manual Trigger:**

1. Go to [Actions tab](https://github.com/regix1/lancache-pics/actions)
2. Select "Update PICS Depot Mappings"
3. Click "Run workflow"
4. Choose mode:
   - **incremental** (recommended): Only new changes, ~5-10 minutes
   - **full**: Complete re-download, ~60 minutes

**Release Creation:**

When data changes, automatically creates:
- Release tag: `v2025.10.04-150030` (timestamp)
- Release title: `PICS Data Update - 2025-10-04`
- Attached file: `pics_depot_mappings.json`
- Metadata in release notes

## Workflow Schedule

```yaml
# Every 2 days at 3 AM UTC
schedule:
  - cron: '0 3 */2 * *'
```

**Customize frequency:**
```yaml
# Daily at 3 AM UTC
- cron: '0 3 * * *'

# Every 3 days at 3 AM UTC
- cron: '0 3 */3 * *'

# Weekly on Sundays at 3 AM UTC
- cron: '0 3 * * 0'

# Twice daily at 3 AM and 3 PM UTC
- cron: '0 3,15 * * *'
```

## Permissions

Both workflows require:
- `contents: write` - To commit changes and create releases

## Environment

**Runners:** `ubuntu-latest`
**Runtime:** .NET 8.0.x
**Dependencies:** SteamKit2, System.Text.Json

## Monitoring

### Workflow Status

Check workflow runs:
- [Actions tab](https://github.com/regix1/lancache-pics/actions)
- Status badges available in README

### Release History

View all data releases:
- [Releases page](https://github.com/regix1/lancache-pics/releases)

### Troubleshooting

**Workflow fails with timeout:**
- Increase timeout in workflow file
- Current: 60min incremental, 120min full

**No changes committed:**
- Normal if Steam data unchanged
- Incremental mode only commits on changes

**Schedule disabled after 60 days:**
- GitHub disables inactive workflows
- Fix: Make any commit or manual trigger

## Accessing Workflow Results

### Latest Release

```bash
# Download latest JSON from release
curl -s https://api.github.com/repos/regix1/lancache-pics/releases/latest \
  | jq -r '.assets[0].browser_download_url' \
  | xargs curl -LO
```

### Raw File from Repo

```bash
# Download from main branch
curl -O https://raw.githubusercontent.com/regix1/lancache-pics/main/PicsDataCollector/pics_depot_mappings.json
```

### Workflow Artifacts

- JSON summary in workflow run
- Release notes with metadata
- Step-by-step execution logs

## Security

- Uses anonymous Steam login (no credentials)
- `github-actions[bot]` for commits
- GitHub token for releases (automatic)
- No secrets required
