#!/usr/bin/env bash

# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.
#
# Filters downloaded outerloop test artifacts before the scheduled failure
# reporter enumerates gating failures.
#
# Input: one directory containing downloaded artifacts in this shape:
#   <all-logs>/logs-*/testresults/ignore-test-failures.marker
# Output: removes each marked logs-* artifact directory and writes the removed
# paths to stdout. It does not create an output file.
# Failure: exits nonzero for invalid arguments, a missing input directory, or a
# marker outside the expected artifact layout. All markers are validated before
# any directory is removed so an unexpected layout cannot partially filter the
# results.

set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "Usage: $0 <all-logs-directory>" >&2
  exit 1
fi

all_logs_directory="${1%/}"

if [[ ! -d "$all_logs_directory" ]]; then
  echo "Test artifact directory does not exist: $all_logs_directory" >&2
  exit 1
fi

marker_list="$(mktemp)"
trap 'rm -f "$marker_list"' EXIT
find "$all_logs_directory" -type f -name ignore-test-failures.marker -print0 > "$marker_list"

# Validate every marker before deleting anything so an unexpected artifact
# layout fails closed without partially filtering the downloaded results.
while IFS= read -r -d '' marker; do
  artifact_directory="$(dirname "$(dirname "$marker")")"
  artifact_parent_directory="$(dirname "$artifact_directory")"
  artifact_name="$(basename "$artifact_directory")"
  expected_marker="$artifact_directory/testresults/ignore-test-failures.marker"
  if [[ "$artifact_parent_directory" != "$all_logs_directory" ||
        "$artifact_name" != logs-* ||
        "$marker" != "$expected_marker" ]]; then
    echo "Unexpected ignore marker location: $marker" >&2
    exit 1
  fi
done < "$marker_list"

while IFS= read -r -d '' marker; do
  artifact_directory="$(dirname "$(dirname "$marker")")"
  echo "Excluding non-gating test results from failure classification: $artifact_directory"
  rm -rf -- "$artifact_directory"
done < "$marker_list"
