#!/usr/bin/env bash

# Quick-start script for running integration tests

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"

print_usage() {
  cat <<EOF
IPTV Integration Test Runner

Usage:
  $(basename "$0") [OPTIONS]

Options:
  -h, --help        Show this help message
  -p, --provider FILE Use provider credentials from FILE
  -b, --build         Rebuild the Docker image before running
  -r, --release       Use Release build configuration (default: Debug)
  -o, --output DIR    Write test outputs to DIR (default: test-output)

Examples:
  # Run with sample data
  ./run-tests.sh

  # Run with your provider credentials
  ./run-tests.sh --provider my-provider.env

  # Rebuild image and run with release build
  ./run-tests.sh --build --release

EOF
}

# Default values
BUILD_IMAGE=0
PROVIDER_ENV=""
CONFIG="Debug"
OUTPUT_DIR="${REPO_ROOT}/test-output"

# Parse arguments
while [[ $# -gt 0 ]]; do
  case $1 in
    -h|--help)
      print_usage
    exit 0
      ;;
    -b|--build)
   BUILD_IMAGE=1
shift
      ;;
    -p|--provider)
      PROVIDER_ENV="$2"
      shift 2
  ;;
    -r|--release)
      CONFIG="Release"
      shift
      ;;
    -o|--output)
      OUTPUT_DIR="$2"
      shift 2
 ;;
    *)
   echo "Unknown option: $1"
      print_usage
      exit 1
      ;;
  esac
done

# Build image if requested
if [[ $BUILD_IMAGE -eq 1 ]]; then
  echo "Building Docker image..."
  docker build -t iptv-integration-tests \
    -f "${REPO_ROOT}/tests/Integration/Dockerfile" \
    "${REPO_ROOT}"
fi

# Check if image exists
if ! docker image inspect iptv-integration-tests > /dev/null 2>&1; then
  echo "Docker image not found. Building..."
  docker build -t iptv-integration-tests \
    -f "${REPO_ROOT}/tests/Integration/Dockerfile" \
    "${REPO_ROOT}"
fi

# Prepare docker run command
DOCKER_CMD=(
  docker run --rm
  -v "${REPO_ROOT}:/workspace"
  -e DOTNET_CONFIGURATION="${CONFIG}"
  -e TEST_OUTPUT_DIR="/workspace/test-output"
)

# Add provider env file if specified
if [[ -n "${PROVIDER_ENV}" ]]; then
  if [[ ! -f "${PROVIDER_ENV}" ]]; then
    echo "Error: Provider env file not found: ${PROVIDER_ENV}"
    exit 1
  fi
  echo "Using provider credentials from: ${PROVIDER_ENV}"
  DOCKER_CMD+=(--env-file "${PROVIDER_ENV}")
else
  echo "Using sample test data (no provider credentials)"
fi

# Add image name
DOCKER_CMD+=(iptv-integration-tests)

# Run the tests
echo "Running integration tests..."
echo "Configuration: ${CONFIG}"
echo "Output directory: ${OUTPUT_DIR}"
echo ""

"${DOCKER_CMD[@]}"

# Show results
if [[ $? -eq 0 ]]; then
  echo ""
  echo "? All tests passed!"
  echo "Results saved to: ${OUTPUT_DIR}"
else
  echo ""
  echo "? Some tests failed"
  echo "Check logs in: ${OUTPUT_DIR}/scenarios/"
  exit 1
fi
