#!/usr/bin/env bash
# Run a macOS stapler staple/validate operation with bounded connection-loss retries.
# Both operations require internet access (see `man stapler`). Retry only the
# observed Apple CloudKit diagnostic on stdout or stderr:
#   error is Error Domain=NSURLErrorDomain Code=-1005 "The network connection was lost." UserInfo={...}
# The prefix and UserInfo suffix may vary; the domain/code must match exactly,
# followed by whitespace or end of line. Use four attempts with 5/15/30s delays.
# Exit 68 alone is not retryable: it also covers missing notarization or an
# unexpected ticket-service response. Other failures and exhaustion fail closed.
set -euo pipefail

if [[ $# -ne 2 || ( "$1" != staple && "$1" != validate ) ]]; then
    echo "Usage: bash macos-stapler-with-retry.sh <staple|validate> <app-path>" >&2
    exit 1
fi

log_dir="$(mktemp -d "${TMPDIR:-/tmp}/aspire-stapler.XXXXXX")"
trap 'rm -f "$log_dir/output"; rmdir "$log_dir"' EXIT
delays=(5 15 30)

for attempt in 1 2 3 4; do
    if xcrun stapler "$1" "$2" >"$log_dir/output" 2>&1; then
        cat "$log_dir/output"
        exit 0
    else
        status=$?
    fi
    cat "$log_dir/output"

    if ! grep -Eq 'Error Domain=NSURLErrorDomain Code=-1005([[:space:]]|$)' "$log_dir/output"; then
        exit "$status"
    fi
    if [[ "$attempt" -eq 4 ]]; then
        echo "stapler $1 failed after $attempt attempts; connection-loss retries exhausted." >&2
        exit "$status"
    fi

    delay="${delays[$((attempt - 1))]}"
    echo "stapler $1 attempt $attempt failed with connection loss (exit $status); retrying in ${delay}s." >&2
    sleep "$delay"
done
