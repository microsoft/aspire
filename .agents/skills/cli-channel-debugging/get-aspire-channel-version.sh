#!/usr/bin/env bash
#
# get-aspire-channel-version.sh — resolve the latest Aspire package version for a
# CLI-emulation channel, so it can feed ASPIRE_CLI_VERSION (see this skill's SKILL.md).
#
# Each value maps to the feed the Aspire CLI's built-in package channels resolve
# `Aspire*` from (see src/Aspire.Cli/Packaging/PackagingService.cs):
#   stable   -> nuget.org                              (https://api.nuget.org/v3-flatcontainer)
#   daily    -> dnceng/public "dotnet9" feed           (.../_packaging/dotnet9/nuget/v3/flat2)
#   staging  -> dnceng/public "darc-pub-microsoft-aspire-<sha8>" feed (needs --commit)
#
# The version of every Aspire.* package tracks the product version, so the value printed
# here is the version the emulated CLI should claim. Aspire.Hosting.AppHost is probed by
# default because it exists on all three feeds.
#
# Usage:
#   get-aspire-channel-version.sh stable
#   get-aspire-channel-version.sh daily
#   get-aspire-channel-version.sh staging --commit <sha>
#
# Options:
#   --package <id>   NuGet package id to probe (default: Aspire.Hosting.AppHost).
#   --commit <sha>   Required for staging; the staging build's source commit.
#   --stable-only    For daily/staging, restrict to stable-shaped (non-prerelease) versions.
#   --prerelease     For stable, allow prerelease versions from nuget.org.
#   -h | --help      Show this help.
#
# Only the resolved version is written to stdout; diagnostics go to stderr, so:
#   export ASPIRE_CLI_VERSION="$(get-aspire-channel-version.sh daily)"

set -euo pipefail

channel=""
package="Aspire.Hosting.AppHost"
commit=""
stable_only="false"
allow_prerelease="false"

die() { echo "error: $*" >&2; exit 1; }

usage() { sed -n '2,40p' "$0" | sed 's/^# \{0,1\}//'; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    stable|daily|staging) channel="$1"; shift ;;
    --package) package="${2:-}"; shift 2 ;;
    --commit) commit="${2:-}"; shift 2 ;;
    --stable-only) stable_only="true"; shift ;;
    --prerelease) allow_prerelease="true"; shift ;;
    -h|--help) usage; exit 0 ;;
    *) die "unrecognized argument: $1 (try --help)" ;;
  esac
done

[[ -n "$channel" ]] || die "a channel is required: stable | daily | staging (try --help)"

# Feed selection mirrors PackageChannel construction in PackagingService.GetChannelsAsync.
pkg_lower="$(printf '%s' "$package" | tr '[:upper:]' '[:lower:]')"
case "$channel" in
  stable)
    feed_url="https://api.nuget.org/v3-flatcontainer/${pkg_lower}/index.json"
    # nuget.org defaults to stable-only unless --prerelease is passed.
    mode=$([[ "$allow_prerelease" == "true" ]] && echo "latest-any" || echo "latest-stable")
    ;;
  daily)
    feed_url="https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet9/nuget/v3/flat2/${pkg_lower}/index.json"
    mode=$([[ "$stable_only" == "true" ]] && echo "latest-stable" || echo "latest-any")
    ;;
  staging)
    [[ -n "$commit" ]] || die "staging requires --commit <sha> (the staging build's commit; see docs/cli-staging-validation.md)"
    # The CLI truncates the commit to 8 lowercase hex chars to build the darc feed name.
    sha8="$(printf '%s' "$commit" | tr '[:upper:]' '[:lower:]' | cut -c1-8)"
    feed_url="https://pkgs.dev.azure.com/dnceng/public/_packaging/darc-pub-microsoft-aspire-${sha8}/nuget/v3/flat2/${pkg_lower}/index.json"
    mode=$([[ "$stable_only" == "true" ]] && echo "latest-stable" || echo "latest-any")
    ;;
esac

echo "Resolving '$package' on '$channel' feed ($mode):" >&2
echo "  $feed_url" >&2

# A NuGet v3 flat-container/flat2 "index.json" returns { "versions": [ ... ] }. The array
# is NOT reliably sorted (the dnceng feeds interleave old 9.x and current 13.x builds), so
# we parse and sort by SemVer ourselves. Example versions seen on the daily feed:
#   "13.5.0-preview.1.26311.9", "13.5.0-preview.1.26311.5", "9.0.0-alpha.1.24072.4"
python3 - "$feed_url" "$mode" <<'PY'
import json, re, sys, urllib.request

url, mode = sys.argv[1], sys.argv[2]

try:
    with urllib.request.urlopen(url, timeout=30) as resp:
        data = json.load(resp)
except urllib.error.HTTPError as e:
    if e.code == 404:
        sys.exit("error: feed returned 404 — package or feed does not exist (check id/commit)")
    sys.exit(f"error: HTTP {e.code} fetching feed")
except Exception as e:  # noqa: BLE001 - surface any network/parse failure to the caller
    sys.exit(f"error: failed to fetch/parse feed: {e}")

versions = data.get("versions", [])
if not versions:
    sys.exit("error: feed returned no versions for this package")

# Parse "MAJOR.MINOR.PATCH[-prerelease]" into a sortable key. Stable (no prerelease) sorts
# ABOVE a prerelease with the same MAJOR.MINOR.PATCH, per SemVer precedence rules.
def key(v):
    m = re.match(r"^(\d+)\.(\d+)\.(\d+)(?:-(.+?))?(?:\+.*)?$", v)
    if not m:
        return None
    major, minor, patch, pre = m.groups()
    ver_tuple = (int(major), int(minor), int(patch))
    if pre is None:
        return ver_tuple + (1, ())  # stable
    ids = tuple((0, int(p)) if p.isdigit() else (1, p) for p in pre.split("."))
    return ver_tuple + (0, ids)

parsed = [(key(v), v) for v in versions]
parsed = [(k, v) for k, v in parsed if k is not None]
if mode == "latest-stable":
    parsed = [(k, v) for k, v in parsed if k[3] == 1]
if not parsed:
    sys.exit("error: no versions matched the requested filter")

parsed.sort(key=lambda kv: kv[0])
print(parsed[-1][1])
PY
