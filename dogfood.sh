if [ -z "$ZSH_VERSION" ]; then
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
else
  # :A will resolve all symlinks, :h will truncate last path component leaving you with a directory name
  scriptroot=${0:A:h}
fi

REPO_ROOT=$(cd "${scriptroot}";pwd)
SDK_PATH=$REPO_ROOT/.dotnet
if [ ! -x "$SDK_PATH/dotnet" ]; then
    echo "Error: Could not find dotnet at $SDK_PATH/dotnet"
    echo "Run ./restore.sh first to install the repo-local SDK."
    return
fi

CONFIG=$1
if [ -n "$CONFIG" ]; then
    PKG_DIR=$REPO_ROOT/artifacts/packages/$CONFIG/Shipping
    if [ ! -d "$PKG_DIR" ]; then
        echo "Error: Could not find packages path $PKG_DIR for CONFIG=$CONFIG"
        return
    fi
else
    PKG_DIR=$REPO_ROOT/artifacts/packages/Release/Shipping
    if [ ! -d "$PKG_DIR" ]; then
      PKG_DIR=$REPO_ROOT/artifacts/packages/Debug/Shipping
    fi
    if [ ! -d "$PKG_DIR" ]; then
        echo "Error: Could not find packages path in $REPO_ROOT/artifacts/packages for Release, or Debug configurations"
        return
    fi
fi

echo "Adding $SDK_PATH to \$PATH"
export PATH=$SDK_PATH:$PATH
echo Setting BUILT_NUGETS_PATH="$PKG_DIR" to resolve locally built packages
export BUILT_NUGETS_PATH=$PKG_DIR
echo
echo "Use $REPO_ROOT/tests/Shared/TemplatesTesting/data/nuget8.config as your local nuget.config to ensure the packages can be restored."
