#!/usr/bin/env bash
set -euo pipefail

source="${BASH_SOURCE[0]}"
# resolve $SOURCE until the file is no longer a symlink
while [[ -h $source ]]; do
  scriptroot="$( cd -P "$( dirname "$source" )" && pwd )"
  source="$(readlink "$source")"

  # if $source was a relative symlink, we need to resolve it relative to the path where the
  # symlink file was located
  [[ $source != /* ]] && source="$scriptroot/$source"
done
scriptroot="$( cd -P "$( dirname "$source" )" && pwd )"

exec "$scriptroot/dotnet.sh" run --project "$scriptroot/src/Aspire.Cli/Aspire.Cli.csproj" --no-launch-profile -- "$@"
