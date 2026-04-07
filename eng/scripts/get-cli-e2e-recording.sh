#!/bin/bash
# Downloads and plays asciinema recordings from CLI E2E test runs.
#
# Usage:
#   ./get-cli-e2e-recording.sh [options]
#
# Options:
#   -r, --run-id <id>      Specific GitHub Actions run ID (default: latest on current branch)
#   -t, --test <name>      Test class name (default: SmokeTests)
#   -o, --output <dir>     Output directory (default: /tmp/cli-e2e-recordings)
#   -p, --play             Play the recording after download (requires asciinema)
#   -l, --list             List available recordings without downloading
#   -b, --branch <name>    Branch name (default: current branch)
#   -h, --help             Show this help message
#
# Examples:
#   ./get-cli-e2e-recording.sh                           # Download SmokeTests from latest run on current branch
#   ./get-cli-e2e-recording.sh -p                        # Download and play
#   ./get-cli-e2e-recording.sh -t SmokeTests -p          # Download specific test and play
#   ./get-cli-e2e-recording.sh -r 20944531393 -p         # Download from specific run
#   ./get-cli-e2e-recording.sh -l                        # List available test recordings

set -euo pipefail

# Default values
RUN_ID=""
TEST_NAME="SmokeTests"
OUTPUT_DIR="/tmp/cli-e2e-recordings"
PLAY_RECORDING=false
LIST_ONLY=false
BRANCH=""
REPO="microsoft/aspire"

# Show help
show_help() {
    sed -n '2,21p' "$0" | sed 's/^# //' | sed 's/^#//'
    exit 0
}

# Parse command line arguments
while [[ $# -gt 0 ]]; do
    case $1 in
        -r|--run-id)
            RUN_ID="$2"
            shift 2
            ;;
        -t|--test)
            TEST_NAME="$2"
            shift 2
            ;;
        -o|--output)
            OUTPUT_DIR="$2"
            shift 2
            ;;
        -p|--play)
            PLAY_RECORDING=true
            shift
            ;;
        -l|--list)
            LIST_ONLY=true
            shift
            ;;
        -b|--branch)
            BRANCH="$2"
            shift 2
            ;;
        -h|--help)
            show_help
            ;;
        *)
            echo "Unknown option: $1"
            exit 1
            ;;
    esac
done

# Check for gh CLI
if ! command -v gh &> /dev/null; then
    echo "Error: GitHub CLI (gh) is required. Install from https://cli.github.com/"
    exit 1
fi

# Get branch name if not specified
if [[ -z "$BRANCH" ]]; then
    BRANCH=$(git branch --show-current 2>/dev/null || echo "")
    if [[ -z "$BRANCH" ]]; then
        echo "Error: Could not determine current branch. Use -b to specify."
        exit 1
    fi
fi

echo "Branch: $BRANCH"

# Get run ID if not specified
if [[ -z "$RUN_ID" ]]; then
    echo "Finding latest CI run..."
    RUN_ID=$(gh run list --branch "$BRANCH" --workflow CI --limit 1 --json databaseId --jq '.[0].databaseId' -R "$REPO" 2>/dev/null || echo "")
    
    if [[ -z "$RUN_ID" ]]; then
        echo "Error: No CI runs found for branch '$BRANCH'"
        exit 1
    fi
fi

echo "Run ID: $RUN_ID"
echo "Run URL: https://github.com/$REPO/actions/runs/$RUN_ID"

# List available artifacts
echo ""
echo "Fetching available CLI E2E test artifacts..."
ARTIFACTS=$(gh api --paginate "repos/$REPO/actions/runs/$RUN_ID/artifacts" --jq '.artifacts[].name' 2>/dev/null | grep -E "^logs-.*-ubuntu-latest$" | grep -i "smoke\|e2e\|cli" | sort || echo "")

if [[ -z "$ARTIFACTS" ]]; then
    # Fallback: list all test logs
    ARTIFACTS=$(gh api --paginate "repos/$REPO/actions/runs/$RUN_ID/artifacts" --jq '.artifacts[].name' 2>/dev/null | grep -E "^logs-.*-ubuntu-latest$" | sort || echo "")
fi

if [[ "$LIST_ONLY" == true ]]; then
    echo ""
    echo "Available test artifacts:"
    echo "$ARTIFACTS" | while read -r artifact; do
        echo "  - $artifact"
    done
    
    echo ""
    echo "To download a specific test, run:"
    echo "  $0 -r $RUN_ID -t <TestClassName>"
    exit 0
fi

# Find the artifact for the requested test
ARTIFACT_NAME="logs-${TEST_NAME}-ubuntu-latest"

# Check if artifact exists
if ! echo "$ARTIFACTS" | grep -q "^${ARTIFACT_NAME}$"; then
    echo ""
    echo "Error: Artifact '$ARTIFACT_NAME' not found."
    echo ""
    echo "Available artifacts:"
    echo "$ARTIFACTS" | head -20 | while read -r artifact; do
        echo "  - $artifact"
    done
    exit 1
fi

echo "Artifact: $ARTIFACT_NAME"

# Create output directory
DOWNLOAD_DIR="$OUTPUT_DIR/$RUN_ID/$TEST_NAME"
rm -rf "$DOWNLOAD_DIR"
mkdir -p "$DOWNLOAD_DIR"

echo ""
echo "Downloading to: $DOWNLOAD_DIR"
gh run download "$RUN_ID" -n "$ARTIFACT_NAME" -D "$DOWNLOAD_DIR" -R "$REPO"

# Find recordings
RECORDINGS_DIR="$DOWNLOAD_DIR/testresults/recordings"
if [[ -d "$RECORDINGS_DIR" ]]; then
    echo ""
    echo "Available recordings:"
    find "$RECORDINGS_DIR" -name "*.cast" -print | while read -r recording; do
        echo "  - $(basename "$recording")"
    done
    
    # Get the first recording for playback
    FIRST_RECORDING=$(find "$RECORDINGS_DIR" -name "*.cast" | head -1)
    
    if [[ "$PLAY_RECORDING" == true ]] && [[ -n "$FIRST_RECORDING" ]]; then
        echo ""
        if command -v asciinema &> /dev/null; then
            echo "Playing: $FIRST_RECORDING"
            echo "Press 'q' to quit, space to pause, +/- to adjust speed"
            echo ""
            asciinema play "$FIRST_RECORDING"
        else
            echo "asciinema not installed. To view recordings:"
            echo "  1. Install asciinema: pip install asciinema"
            echo "  2. Run: asciinema play $FIRST_RECORDING"
            echo ""
            echo "Or view raw content:"
            echo "  head -50 $FIRST_RECORDING"
        fi
    fi
else
    echo ""
    echo "No recordings found in artifact."
    echo "Contents:"
    find "$DOWNLOAD_DIR" -type f | head -20
fi

echo ""
echo "Download complete: $DOWNLOAD_DIR"
