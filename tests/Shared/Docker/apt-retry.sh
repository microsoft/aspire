#!/bin/sh
set -eu

if [ "$#" -lt 2 ] || [ "$1" != "install" ]; then
  echo "Usage: apt-retry install <packages...>" >&2
  exit 64
fi

shift

for attempt in 1 2 3 4 5; do
  apt-get -o Acquire::Retries=5 -o APT::Update::Error-Mode=any update -qq &&
    apt-get -o Acquire::Retries=5 -o Dpkg::Use-Pty=0 install -y --no-install-recommends "$@" &&
    exit 0
  status=$?

  if [ "$attempt" = "5" ]; then
    exit "$status"
  fi

  echo "apt install failed on attempt $attempt/5; retrying..." >&2
  rm -rf /var/lib/apt/lists/*
  sleep 10
done
