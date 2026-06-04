#!/usr/bin/env bash

# Simulate an official PRERELEASE-shaped staging build (e.g. 13.4.0-preview.*) and
# validate that the CLI resolves Aspire.* from its SHA-specific darc feed.
#
# This is the scenario from https://github.com/microsoft/aspire/issues/17744:
# a prerelease-shaped staging build must use the darc-pub-microsoft-aspire-<sha>
# feed (quality=Both), NOT the shared daily feed.
#
# See docs/cli-staging-validation.md for the full validation matrix.

set -euo pipefail

source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/debug-aspire-channel.sh"

run_debug_channel staging "debug-staging.sh" "$@"
