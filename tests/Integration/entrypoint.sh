#!/usr/bin/env bash

set -euo pipefail

: "${WORKSPACE:=/workspace}"
: "${DOTNET_CONFIGURATION:=Release}"
: "${TEST_OUTPUT_DIR:=${WORKSPACE}/test-output}"
: "${MATRIX_PATH:=/opt/integration/matrix.json}"

log() {
  printf '[integration] %s\n' "$*" >&2
}

die() {
  log "ERROR: $1"
  exit 1
}

log "====== IPTV Integration Test Suite ======"
log "Workspace: ${WORKSPACE}"
log "Output directory: ${TEST_OUTPUT_DIR}"
log "Configuration: ${DOTNET_CONFIGURATION}"

# Ensure workspace is mounted
[[ -d "${WORKSPACE}/src" ]] || die "Workspace not mounted correctly. Mount repo at /workspace"

# Create output directories
mkdir -p "${TEST_OUTPUT_DIR}"/{artifacts,scenarios,diffs,runtime}

# Build the CLI
log "Building IPTV CLI..."
cd "${WORKSPACE}"
dotnet build src/IPTVGuideDog.Cli/IPTVGuideDog.Cli.csproj \
  -c "${DOTNET_CONFIGURATION}" \
  --nologo \
  -v quiet

# Get CLI path
CLI_BINARY="${WORKSPACE}/src/IPTVGuideDog.Cli/bin/${DOTNET_CONFIGURATION}/net10.0/iptv"

if [[ ! -f "${CLI_BINARY}" ]]; then
  die "CLI binary not found at ${CLI_BINARY}"
fi

log "CLI built successfully: ${CLI_BINARY}"

# Check if we have real provider URLs or use sample data
if [[ -n "${PLAYLIST_URL:-}" ]]; then
  log "Using provider playlist: ${PLAYLIST_URL}"
  
  # Substitute credentials
  PLAYLIST_URL_EXPANDED=$(echo "${PLAYLIST_URL}" | sed "s/%USER%/${USER:-}/g" | sed "s/%PASS%/${PASS:-}/g")
  EPG_URL_EXPANDED=$(echo "${EPG_URL:-}" | sed "s/%USER%/${USER:-}/g" | sed "s/%PASS%/${PASS:-}/g")
  
  # Download artifacts
  log "Downloading playlist..."
  curl -sSL "${PLAYLIST_URL_EXPANDED}" > "${TEST_OUTPUT_DIR}/artifacts/raw.m3u" || die "Failed to download playlist"
  
  if [[ -n "${EPG_URL_EXPANDED:-}" ]]; then
    log "Downloading EPG..."
    curl -sSL "${EPG_URL_EXPANDED}" > "${TEST_OUTPUT_DIR}/artifacts/raw.epg.xml" || die "Failed to download EPG"
  fi
  
  PLAYLIST_SOURCE="${TEST_OUTPUT_DIR}/artifacts/raw.m3u"
  EPG_SOURCE="${TEST_OUTPUT_DIR}/artifacts/raw.epg.xml"
else
  log "Using sample data"
  PLAYLIST_SOURCE="/opt/integration/data/sample.m3u"
  EPG_SOURCE="/opt/integration/data/sample.epg.xml"
fi

# Run test matrix
log "Running test scenarios..."
python3 /opt/integration/run_matrix.py \
  --cli "${CLI_BINARY}" \
  --playlist "${PLAYLIST_SOURCE}" \
  --epg "${EPG_SOURCE}" \
  --output-dir "${TEST_OUTPUT_DIR}" \
  --matrix "${MATRIX_PATH}"

EXIT_CODE=$?

if [[ $EXIT_CODE -eq 0 ]]; then
  log "====== ALL TESTS PASSED ======"
else
  log "====== TESTS FAILED ======"
  log "Check logs in: ${TEST_OUTPUT_DIR}/scenarios/"
fi

exit $EXIT_CODE
