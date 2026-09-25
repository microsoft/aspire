#!/usr/bin/env bash

# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.

set -euo pipefail

# Starting with .NET 11, `dotnet test` decides the zero-tests result for the whole run from
# aggregated results. `--ignore-exit-code 8` still makes direct test-module execution return 0,
# but an all-empty or all-skipped `dotnet test` run returns 8 from the orchestrator.
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
