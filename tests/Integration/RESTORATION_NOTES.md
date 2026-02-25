# Integration Tests - Restoration Summary

## What Was Restored

Your integration tests were accidentally deleted during the project restructure in commit `f641b5c` (Oct 28, 2025).

I've restored and modernized the integration test suite with the following improvements:

### Files Created

```
tests/Integration/
??? Dockerfile              # Docker container for running tests
??? README.md     # Complete documentation
??? entrypoint.sh           # Test orchestration script
??? run_matrix.py     # Python test runner
??? matrix.json # Test scenario definitions
??? .gitignore     # Ignore test outputs and credentials
??? run-tests.sh # Quick-start script (Linux/Mac)
??? run-tests.ps1    # Quick-start script (Windows)
??? sample.env     # Template for provider credentials
??? data/
??? sample.m3u          # Sample playlist for offline testing
    ??? sample.epg.xml      # Sample EPG data
    ??? sample_drop_list.txt # Sample groups filter
```

### Updated Files

- `.github/workflows/integration-tests.yml` - Updated paths for new structure

## What These Tests Do

The integration tests run your **actual CLI application** against **real IPTV providers** to validate:

? Playlist downloading and parsing  
? EPG data fetching  
? Channel filtering by groups  
? Live-only content filtering (excluding VOD/movies/series)  
? Groups file management  
? Configuration file handling  
? URL credential substitution  
? Error handling with invalid inputs  

## Quick Start

### Run with Sample Data

```bash
# Linux/Mac
cd tests/Integration
./run-tests.sh

# Windows
cd tests\Integration
.\run-tests.ps1
```

### Run Against Your Provider

1. Copy the template:
   ```bash
   cp tests/Integration/sample.env my-provider.env
   ```

2. Edit `my-provider.env` with your credentials:
   ```
   USER=your_username
   PASS=your_password
   PLAYLIST_URL=http://provider.com/get.php?username=%USER%&password=%PASS%&type=m3u_plus
   EPG_URL=http://provider.com/xmltv.php?username=%USER%&password=%PASS%
   ```

3. Run the tests:
   ```bash
   ./run-tests.sh --provider my-provider.env
   ```

## Docker Usage

You can also run the tests directly with Docker:

```bash
# Build the image
docker build -t iptv-integration-tests -f tests/Integration/Dockerfile .

# Run with sample data
docker run --rm -v "$(pwd)":/workspace iptv-integration-tests

# Run with your provider
docker run --rm -v "$(pwd)":/workspace --env-file my-provider.env iptv-integration-tests
```

## Test Scenarios

The test matrix (`matrix.json`) includes:

1. **basic-playlist-filter** - Download and filter by groups
2. **epg-download** - Fetch both playlist and EPG
3. **live-only-filter** - Exclude VOD content
4. **groups-command** - Generate groups file
5. **verbose-output** - Test verbose logging
6. **stdout-streaming** - Output to stdout
7. **invalid-url** - Error handling
8. **missing-required-arg** - Validation errors

## Output Location

Test results are written to `test-output/`:

- `artifacts/` - Raw downloaded files
- `scenarios/<name>/` - Per-scenario outputs and logs
- `runtime/` - Generated configuration files

## GitHub Actions

The workflow `.github/workflows/integration-tests.yml` will:

- Run automatically on PRs that touch CLI or integration test files
- Run weekly on Sundays at 2 AM UTC
- Can be triggered manually via "Run workflow" button

## Next Steps

1. **Test locally first:**
   ```bash
   cd tests/Integration
   ./run-tests.sh
```

2. **Verify all tests pass** with sample data

3. **Optional:** Test with your real provider:
   ```bash
   ./run-tests.sh --provider my-provider.env
   ```

4. **Commit the restored tests:**
   ```bash
   git add tests/Integration/
   git add .github/workflows/integration-tests.yml
   git commit -m "Restore integration tests for testing against live providers"
   ```

## Differences from Original

The restored integration tests have been modernized:

- ? Updated paths for new solution structure (`src/` instead of `cli/`)
- ? Simplified Python test runner (removed legacy comparison code)
- ? Added Windows PowerShell script for easier Windows usage
- ? Improved documentation
- ? Better error messages and logging
- ? GitHub Actions workflow updated for new paths

## Troubleshooting

**Tests fail to run:**
- Make sure Docker is running
- Check that you're in the repository root when running commands

**"Image not found" error:**
- Run with `--build` flag: `./run-tests.sh --build`

**Authentication errors:**
- Verify credentials in your `.env` file
- Check that URL placeholders are correct (`%USER%` and `%PASS%`)

**Can't see test outputs:**
- Check `test-output/scenarios/<test-name>/stderr.txt` for errors
- Run with `--release` for cleaner output

## Security Note

?? **Never commit your provider credentials!**

The `.gitignore` file is configured to ignore `*.env` files (except `sample.env`).  
Keep your provider credentials in a local `.env` file that is never committed to Git.

---

Your integration tests are back! ??
