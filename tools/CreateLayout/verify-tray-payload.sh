#!/usr/bin/env bash
set -euo pipefail

if [[ "$(uname -s)" != Darwin ]]; then
    echo "macOS tray payload verification requires macOS codesign." >&2
    exit 1
fi
if [[ $# -ne 2 ]]; then
    echo "Usage: bash tools/CreateLayout/verify-tray-payload.sh <payload.tar.gz> <osx-arm64|osx-x64>" >&2
    exit 1
fi

archive="$1"
rid="$2"
case "$rid" in
    osx-arm64) expected_arch=arm64 ;;
    osx-x64) expected_arch=x86_64 ;;
    *) echo "Unsupported macOS tray RID: $rid" >&2; exit 1 ;;
esac
if [[ ! -f "$archive" ]]; then
    echo "Bundle payload archive not found: $archive" >&2
    exit 1
fi

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
verification_dir="$root/artifacts/tray-payload-verification/$rid"
# Only this verifier owns this fixed, RID-scoped directory. A clean extraction
# prevents stale files from making an incomplete new archive appear valid.
rm -rf "$verification_dir"
mkdir -p "$verification_dir"

app_entry="$rid/tray/Aspire Tray.app"
tar -xzf "$archive" -C "$verification_dir" "$app_entry"
app="$verification_dir/$app_entry"
for relative_path in Contents/MacOS/aspire-tray Contents/Info.plist Contents/Resources/Aspire.icns Contents/_CodeSignature/CodeResources; do
    if [[ ! -f "$app/$relative_path" || -L "$app/$relative_path" ]]; then
        echo "Required regular file missing from tray payload: $relative_path" >&2
        exit 1
    fi
done

executable="$app/Contents/MacOS/aspire-tray"
mode="$(stat -f '%Lp' "$executable")"
if (( (8#$mode & 0111) != 0111 )); then
    echo "Tray executable lost execute permissions in payload (mode $mode)." >&2
    exit 1
fi
# lipo understands both thin and universal Mach-O files. Require a slice for the
# requested RID rather than assuming that the archive directory identifies it.
if ! /usr/bin/lipo "$executable" -verify_arch "$expected_arch"; then
    echo "Tray executable does not contain the required $expected_arch architecture for $rid." >&2
    exit 1
fi
if [[ "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleExecutable' "$app/Contents/Info.plist")" != aspire-tray ]]; then
    echo "Tray Info.plist does not identify the bundled executable." >&2
    exit 1
fi
plutil -lint "$app/Contents/Info.plist"
codesign --verify --strict "$app"
echo "Verified tray payload: $archive ($app_entry, executable mode $mode)"
