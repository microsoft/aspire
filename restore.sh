#!/usr/bin/env bash

source="${BASH_SOURCE[0]}"

function is_cygwin_or_mingw()
{
  case $(uname -s) in
    CYGWIN*)    return 0;;
    MINGW*)     return 0;;
    *)          return 1;;
  esac
}

# resolve $SOURCE until the file is no longer a symlink
while [[ -h $source ]]; do
  scriptroot="$( cd -P "$( dirname "$source" )" && pwd )"
  source="$(readlink "$source")"

  # if $source was a relative symlink, we need to resolve it relative to the path where the
  # symlink file was located
  [[ $source != /* ]] && source="$scriptroot/$source"
done

scriptroot="$( cd -P "$( dirname "$source" )" && pwd )"
"$scriptroot/eng/build.sh" --restore "$@"