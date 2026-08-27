#!/usr/bin/env bash

# Runs the opt-in live Platform SSO parser test against the current macOS user.
# The test keeps app-sso output in memory and reports only detection booleans and
# bounded failure codes/stages, never aliases, domains, tokens, or raw command output.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "Error: live Platform SSO validation requires macOS." >&2
  exit 1
fi

if [[ ! -x /usr/bin/app-sso ]]; then
  echo "Error: /usr/bin/app-sso is unavailable." >&2
  exit 1
fi

cd "$REPO_ROOT"
./restore.sh

ASPIRE_TEST_LIVE_MAC_PLATFORM_SSO=1 \
MSBUILDTERMINALLOGGER=false \
dotnet test --project tests/Aspire.Cli.Tests/Aspire.Cli.Tests.csproj \
  --no-launch-profile \
  -- \
  --filter-method "*.EvaluateMacPlatformSso_LiveManagedMac" \
  --filter-not-trait "quarantined=true" \
  --filter-not-trait "outerloop=true"
