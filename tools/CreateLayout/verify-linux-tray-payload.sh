#!/usr/bin/env bash
# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.
set -euo pipefail

archive="${1:?Archive path is required}"
rid="${2:?RID is required}"
case "$rid" in
    linux-x64) machine="3e00" ;;
    linux-arm64) machine="b700" ;;
    *) echo "Unsupported Linux tray RID: $rid" >&2; exit 1 ;;
esac

scratch="$(mktemp -d)"
trap 'rm -rf -- "$scratch"' EXIT
tar -xzf "$archive" -C "$scratch" "$rid/tray/aspire-tray" "$rid/tray/Aspire.png"
exe="$scratch/$rid/tray/aspire-tray"
test -x "$exe"
# ELF e_ident = 7f454c46 02 01 01; e_machine at offset 18 is little-endian.
# https://refspecs.linuxfoundation.org/elf/gabi4+/ch4.eheader.html
test "$(od -An -tx1 -N7 "$exe" | tr -d ' \n')" = "7f454c46020101"
test "$(od -An -tx1 -j18 -N2 "$exe" | tr -d ' \n')" = "$machine"
test "$(od -An -tx1 -N8 "$scratch/$rid/tray/Aspire.png" | tr -d ' \n')" = "89504e470d0a1a0a"
echo "Verified Linux tray payload for $rid."
