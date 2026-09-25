#!/usr/bin/env bash

# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.

set -euo pipefail

: "${GITHUB_OUTPUT:?GITHUB_OUTPUT must identify the workflow step output file}"

SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
normalized_exit_code=0
bash "$SCRIPT_DIR/normalize-mtp-exit-code.sh" "$1" || normalized_exit_code=$?

if [ "$normalized_exit_code" -ne 0 ]; then
  echo "test_failed=true" >> "$GITHUB_OUTPUT"
fi
