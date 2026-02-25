# Quick-start script for running integration tests on Windows

param(
    [switch]$Help,
    [switch]$Build,
    [string]$Provider = "",
    [switch]$Release,
    [string]$Output = ""
)

$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Resolve-Path "$ScriptDir\..\.."

function Show-Usage {
    Write-Host @"
IPTV Integration Test Runner

Usage:
  .\run-tests.ps1 [OPTIONS]

Options:
  -Help     Show this help message
  -Provider FILE    Use provider credentials from FILE
  -Build            Rebuild the Docker image before running
  -Release          Use Release build configuration (default: Debug)
  -Output DIR       Write test outputs to DIR (default: test-output)

Examples:
  # Run with sample data
  .\run-tests.ps1

  # Run with your provider credentials
  .\run-tests.ps1 -Provider my-provider.env

  # Rebuild image and run with release build
  .\run-tests.ps1 -Build -Release

"@
}

if ($Help) {
    Show-Usage
    exit 0
}

$Config = if ($Release) { "Release" } else { "Debug" }
$OutputDir = if ($Output) { $Output } else { Join-Path $RepoRoot "test-output" }

# Build image if requested
if ($Build) {
    Write-Host "Building Docker image..." -ForegroundColor Cyan
    docker build -t iptv-integration-tests `
        -f "$RepoRoot\tests\Integration\Dockerfile" `
    $RepoRoot
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to build Docker image"
   exit 1
  }
}

# Check if image exists
$imageExists = docker image inspect iptv-integration-tests 2>$null
if (-not $imageExists) {
    Write-Host "Docker image not found. Building..." -ForegroundColor Yellow
    docker build -t iptv-integration-tests `
     -f "$RepoRoot\tests\Integration\Dockerfile" `
        $RepoRoot
    if ($LASTEXITCODE -ne 0) {
     Write-Error "Failed to build Docker image"
        exit 1
    }
}

# Prepare docker run command
$dockerArgs = @(
    "run", "--rm",
    "-v", "${RepoRoot}:/workspace",
    "-e", "DOTNET_CONFIGURATION=$Config",
    "-e", "TEST_OUTPUT_DIR=/workspace/test-output"
)

# Add provider env file if specified
if ($Provider) {
    if (-not (Test-Path $Provider)) {
      Write-Error "Provider env file not found: $Provider"
        exit 1
}
    Write-Host "Using provider credentials from: $Provider" -ForegroundColor Cyan
    $dockerArgs += "--env-file"
    $dockerArgs += $Provider
} else {
    Write-Host "Using sample test data (no provider credentials)" -ForegroundColor Yellow
}

# Add image name
$dockerArgs += "iptv-integration-tests"

# Run the tests
Write-Host ""
Write-Host "Running integration tests..." -ForegroundColor Cyan
Write-Host "Configuration: $Config"
Write-Host "Output directory: $OutputDir"
Write-Host ""

& docker $dockerArgs

if ($LASTEXITCODE -eq 0) {
    Write-Host ""
    Write-Host "? All tests passed!" -ForegroundColor Green
    Write-Host "Results saved to: $OutputDir"
} else {
    Write-Host ""
    Write-Host "? Some tests failed" -ForegroundColor Red
 Write-Host "Check logs in: $OutputDir\scenarios\"
    exit 1
}
