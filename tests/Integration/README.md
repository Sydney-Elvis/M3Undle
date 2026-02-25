# Integration Tests

This directory contains Docker-based integration tests that run the IPTV Guide Dog CLI against **real IPTV providers** to validate end-to-end functionality.

## Overview

The integration test harness:

1. Builds the .NET CLI application
2. Downloads playlists and EPG data from real providers (or uses sample data)
3. Executes various CLI commands with different configurations
4. Validates output format, channel filtering, and group management
5. Can compare against a legacy Python baseline script (optional)

## Quick Start

### Build the Test Image

```bash
docker build -t iptv-integration-tests -f tests/Integration/Dockerfile .
```

### Run with Sample Data

```bash
docker run --rm \
  -v "$(pwd)":/workspace \
  iptv-integration-tests
```

### Run Against Real Provider

```bash
# Create a .env file with your provider credentials
cat > provider.env <<EOF
USER=your_username
PASS=your_password
PLAYLIST_URL=http://provider.com/get.php?username=%USER%&password=%PASS%&type=m3u_plus
EPG_URL=http://provider.com/xmltv.php?username=%USER%&password=%PASS%
EOF

# Run the tests
docker run --rm \
  -v "$(pwd)":/workspace \
  --env-file provider.env \
  iptv-integration-tests
```

## Test Scenarios

The test matrix includes:

1. **Basic Playlist Filtering** - Filter channels by groups using drop list
2. **EPG Download** - Fetch and process EPG data
3. **Live-Only Filter** - Exclude VOD/movies/series content
4. **Groups File Management** - Create and update groups files
5. **Config File Usage** - Test YAML configuration profiles
6. **URL Credential Substitution** - Validate environment variable replacement
7. **Output to Files** - Write playlist and EPG to specific paths
8. **Stdout Streaming** - Pipe output for integration with other tools

## Directory Structure

```
tests/Integration/
??? Dockerfile        # Test container definition
??? README.md     # This file
??? entrypoint.sh      # Test orchestration script
??? matrix.json   # Test scenario definitions
??? run_matrix.py  # Python test runner
??? data/        # Sample test data
?   ??? sample.m3u         # Sample playlist
?   ??? sample.epg.xml     # Sample EPG
??? fixtures/        # Test configuration templates
?   ??? test-config.yaml   # YAML config template
??? legacy/       # Optional baseline comparison
    ??? m3u-filter.py      # Legacy Python filter (for comparison)
 ??? sample_drop_list.txt
```

## Environment Variables

- `PLAYLIST_URL` - URL to fetch M3U playlist (supports `%USER%` and `%PASS%` placeholders)
- `EPG_URL` - URL to fetch EPG XML data
- `USER` - Provider username
- `PASS` - Provider password
- `TEST_OUTPUT_DIR` - Where to write test outputs (default: `/workspace/test-output`)
- `DOTNET_CONFIGURATION` - Build configuration (default: `Debug`, use `Release` for production)
- `MATRIX_PATH` - Path to custom test matrix JSON (default: `matrix.json`)
- `PROVIDER_ENV_FILES` - Comma-separated list of provider-specific env files (for multi-provider testing)

## Outputs

Test results are written to `test-output/` (or `$TEST_OUTPUT_DIR`):

- `artifacts/` - Raw downloaded playlist and EPG files
- `scenarios/<name>/` - Per-scenario outputs, stdout, stderr
- `diffs/` - Comparison diffs (if baseline comparison enabled)
- `runtime/` - Generated configuration files

## Extending Tests

### Add a New Scenario

Edit `matrix.json`:

```json
{
  "name": "my-test-scenario",
  "description": "Test description",
  "command": "run",
  "args": [
    "--playlist-url", "{playlist_url}",
    "--out-playlist", "{scenario_dir}/output.m3u",
    "--verbose"
  ],
  "expectSuccess": true,
  "validateOutputs": ["output.m3u"]
}
```

### Create Custom Configuration

Add a new template in `fixtures/` and reference it in your test scenario.

## CI/CD Integration

A GitHub Actions workflow can be added to run these tests:

- Weekly against sample data
- On-demand against real providers (using repository secrets)
- On pull requests (using sample data only)

## Troubleshooting

**Container won't start:**
- Ensure you've mounted the repository at `/workspace`
- Check that the Dockerfile builds successfully

**Tests fail with auth errors:**
- Verify your `.env` file contains correct `USER` and `PASS`
- Check that `PLAYLIST_URL` and `EPG_URL` are correctly formatted

**Output files missing:**
- Check permissions on `test-output/` directory
- Look at scenario stderr logs in `test-output/scenarios/<name>/stderr.txt`

**Comparison with legacy baseline fails:**
- Ensure `legacy/m3u-filter.py` exists (or remove baseline comparison from scenarios)
- This is optional and can be disabled

## Notes

- Integration tests require network access to download from providers
- Tests can take several minutes depending on playlist size
- Use sample data for CI to avoid hitting provider rate limits
- Store provider credentials as secrets, never commit them to the repository
