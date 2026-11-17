#!/usr/bin/env bash

# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.

set -euo pipefail

# Starting with .NET 11, `dotnet test` decides the zero-tests result for the whole run from
# aggregated results. `--ignore-exit-code 8` still makes direct test-module execution return 0,
# but an all-empty or all-skipped `dotnet test` run returns 8 from the orchestrator.
# This temporarily works around https://github.com/dotnet/sdk/issues/56214. Remove it after Aspire
# upgrades to a .NET 11 SDK build containing https://github.com/dotnet/sdk/pull/56296 or the
# equivalent https://github.com/dotnet/sdk/pull/56219 fix. Cleanup is tracked by
# https://github.com/microsoft/aspire/issues/20565.
# https://learn.microsoft.com/dotnet/core/tools/dotnet-test-mtp#whole-run-and-per-module-minimums
if [ "$#" -ne 1 ] || [[ ! "$1" =~ ^[0-9]+$ ]]; then
  echo "Usage: $0 <MTP exit code>" >&2
  exit 5
fi

if [ "$1" -eq 8 ]; then
  echo "All selected tests were skipped; treating MTP exit code 8 as success."
  exit 0
fi

exit "$1"
