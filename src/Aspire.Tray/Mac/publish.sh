#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
case "${1:-$(uname -m)}" in
    arm64|osx-arm64) rid=osx-arm64 ;;
    x86_64|osx-x64) rid=osx-x64 ;;
    *) echo "Usage: bash src/Aspire.Tray/Mac/publish.sh [osx-arm64|osx-x64]" >&2; exit 1 ;;
esac

cd "$root"
./dotnet.sh msbuild eng/Bundle.proj -t:_PublishNativeTray \
    -p:Configuration=Release -p:TargetRid="$rid" --nologo

app="$root/artifacts/bin/Aspire.Tray.Mac/Release/net10.0/$rid/app/Aspire Tray.app"
file "$app/Contents/MacOS/aspire-tray"
printf '\nApplication: %s\n' "$app"
