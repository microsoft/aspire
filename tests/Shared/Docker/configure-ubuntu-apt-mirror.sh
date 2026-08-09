#!/bin/sh
set -eu

ubuntu_apt_mirror="${1:-}"

if [ -z "$ubuntu_apt_mirror" ]; then
    exit 0
fi

if [ ! -f /etc/os-release ]; then
    echo "Skipping UBUNTU_APT_MIRROR because /etc/os-release was not found." >&2
    exit 0
fi

. /etc/os-release

if [ "${ID:-}" != "ubuntu" ]; then
    echo "Skipping UBUNTU_APT_MIRROR because current image ID is '${ID:-unknown}', not 'ubuntu'." >&2
    exit 0
fi

case "$ubuntu_apt_mirror" in
    *://*) ;;
    *)
        echo "UBUNTU_APT_MIRROR must be an absolute URI, got '$ubuntu_apt_mirror'." >&2
        exit 1
        ;;
esac

if [ -z "${VERSION_CODENAME:-}" ]; then
    echo "Could not determine the Ubuntu version codename from /etc/os-release." >&2
    exit 1
fi

ubuntu_apt_mirror="${ubuntu_apt_mirror%/}"
ubuntu_apt_components="${UBUNTU_APT_COMPONENTS:-main restricted universe multiverse}"
ubuntu_sources_file="/etc/apt/sources.list.d/ubuntu.sources"

mkdir -p "$(dirname "$ubuntu_sources_file")"

cat > "$ubuntu_sources_file" <<EOF
Types: deb
URIs: $ubuntu_apt_mirror
Suites: $VERSION_CODENAME $VERSION_CODENAME-updates $VERSION_CODENAME-backports $VERSION_CODENAME-security
Components: $ubuntu_apt_components
Signed-By: /usr/share/keyrings/ubuntu-archive-keyring.gpg
EOF

if [ -f /etc/apt/sources.list ]; then
    printf '# Ubuntu sources configured in %s\n' "$ubuntu_sources_file" > /etc/apt/sources.list
fi
