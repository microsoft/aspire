#!/bin/sh
set -eu

if [ "$#" -lt 2 ] || [ "$1" != "install" ]; then
  echo "Usage: apt-retry install <packages...>" >&2
  exit 64
fi

shift

ubuntu_mirror=${APT_RETRY_UBUNTU_MIRROR-http://azure.archive.ubuntu.com/ubuntu}
if [ -n "$ubuntu_mirror" ]; then
  ubuntu_mirror=${ubuntu_mirror%/}
  ubuntu_sources_configured=false

  for source_file in /etc/apt/sources.list /etc/apt/sources.list.d/*.list /etc/apt/sources.list.d/*.sources; do
    [ -f "$source_file" ] || continue

    if grep -Eq 'http://(azure\.)?archive\.ubuntu\.com/ubuntu|http://security\.ubuntu\.com/ubuntu' "$source_file"; then
      ubuntu_sources_configured=true
    fi

    sed -i \
      -e "s#http://azure.archive.ubuntu.com/ubuntu#$ubuntu_mirror#g" \
      -e "s#http://archive.ubuntu.com/ubuntu#$ubuntu_mirror#g" \
      -e "s#http://security.ubuntu.com/ubuntu#$ubuntu_mirror#g" \
      "$source_file"
  done

  if [ "$ubuntu_sources_configured" = "true" ]; then
    echo "using Ubuntu apt mirror: $ubuntu_mirror" >&2
  fi
fi

for attempt in 1 2 3; do
  echo "apt install attempt $attempt/3: $*" >&2
  rm -rf /var/lib/apt/lists/*

  apt-get \
    -o Acquire::Retries=2 \
    -o Acquire::http::Timeout=20 \
    -o Acquire::https::Timeout=20 \
    -o APT::Update::Error-Mode=any \
    update -qq &&
    apt-get \
      -o Acquire::Retries=2 \
      -o Acquire::http::Timeout=20 \
      -o Acquire::https::Timeout=20 \
      -o Dpkg::Use-Pty=0 \
      install -y --no-install-recommends "$@" &&
    exit 0
  status=$?

  if [ "$attempt" = "3" ]; then
    exit "$status"
  fi

  echo "apt install failed on attempt $attempt/3; retrying..." >&2
  sleep 5
done
