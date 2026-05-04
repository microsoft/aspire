#!/bin/sh
set -eu

if [ "$#" -lt 2 ] || [ "$1" != "install" ]; then
  echo "Usage: apt-retry install <packages...>" >&2
  exit 64
fi

shift

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
