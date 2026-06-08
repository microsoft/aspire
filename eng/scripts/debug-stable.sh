#!/usr/bin/env bash

# Simulate an official STABLE-shaped staging build (e.g. 13.4.0) and validate that
# the CLI resolves Aspire.* from its SHA-specific darc feed.
#
# This is the scenario from https://github.com/microsoft/aspire/issues/17527:
# a stable-shaped release-branch build still resolves from its own darc feed
# (quality=Stable), not nuget.org.
#
# See docs/cli-staging-validation.md for the full validation matrix.

set -euo pipefail

source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/debug-aspire-channel.sh"

run_debug_channel stable "debug-stable.sh" "$@"
