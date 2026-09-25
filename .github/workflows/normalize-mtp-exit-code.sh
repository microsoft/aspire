#!/usr/bin/env bash

# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.

set -euo pipefail

if [ "$#" -ne 1 ] || [[ ! "$1" =~ ^[0-9]+$ ]]; then
  echo "Usage: $0 <MTP exit code>" >&2
  exit 5
fi

if [ "$1" -eq 8 ]; then
  echo "All selected tests were skipped; treating MTP exit code 8 as success."
  exit 0
fi

exit "$1"
