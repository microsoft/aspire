#!/usr/bin/env bash

# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.

set -euo pipefail

if [ "$#" -lt 2 ]; then
  echo "Usage: $0 <results directory> <test short name> [extra test arguments...]" >&2
  exit 5
fi

: "${GITHUB_OUTPUT:?GITHUB_OUTPUT must identify the workflow step output file}"

results_directory=$1
test_short_name=$2
shift 2

SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
dotnet_script=${DOTNET_SCRIPT:-./dotnet.sh}
test_exit_code=0

bash "$dotnet_script" test \
  --project tests/Aspire.Deployment.EndToEnd.Tests/Aspire.Deployment.EndToEnd.Tests.csproj \
  -c Release \
  --results-directory "$results_directory" \
  -- \
  --report-trx \
  --report-trx-filename "${test_short_name}.trx" \
  --filter-not-trait "quarantined=true" \
  "$@" || test_exit_code=$?

echo "Test run exit code: $test_exit_code"

normalized_exit_code=0
bash "$SCRIPT_DIR/normalize-mtp-exit-code.sh" "$test_exit_code" || normalized_exit_code=$?

if [ "$normalized_exit_code" -ne 0 ]; then
  echo "test_failed=true" >> "$GITHUB_OUTPUT"
fi
