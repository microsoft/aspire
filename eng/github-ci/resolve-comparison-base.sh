#!/usr/bin/env bash
# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.

# Print a comparison base only when complete history proves a reachable merge-base.
# Callers must check out full history. Exit 1 means an unsafe/unresolved comparison
# (selector must run ALL); exit 2 means missing endpoints (infrastructure error).
# Diagnostics go to stderr, never stdout.
set -euo pipefail

base="${1:?Expected event base SHA}"
head="${2:?Expected PR head SHA}"

# A shallow merge-base can succeed while hiding a newer common ancestor.
# https://git-scm.com/docs/shallow
if [ "$(git --no-pager rev-parse --is-shallow-repository)" = "true" ]; then
    echo "::warning::Comparison history is shallow; refusing a selective comparison." >&2
    exit 1
fi

if [ "${GITHUB_EVENT_NAME:-}" = "pull_request" ]; then
    if [[ ! "${GITHUB_REF:-}" =~ ^refs/pull/[0-9]+/merge$ ]] ||
        [ "$(git --no-pager rev-parse HEAD)" != "${GITHUB_SHA:-}" ]; then
        echo "::warning::Cannot verify the checked-out PR merge identity; refusing a selective comparison." >&2
        exit 1
    fi

    # rev-list prints "<commit> <first-parent> <second-parent>". Verify the
    # synthetic merge shape before using its first parent instead of stale base.sha.
    read -r -a parents <<< "$(git --no-pager rev-list --parents -n 1 HEAD)"
    if [ "${#parents[@]}" -ne 3 ] || [ "${parents[2]}" != "$head" ]; then
        echo "::warning::Checked-out merge must have exactly two parents with PR head $head second; refusing a selective comparison." >&2
        exit 1
    fi
    base=$(git --no-pager rev-parse HEAD^1)
    echo "Using pinned PR comparison base $base from checked-out merge ${GITHUB_SHA} (event base ${1})." >&2
fi

# Full-history checkout is responsible for making both pinned endpoints available.
if ! git --no-pager cat-file -e "${base}^{commit}" 2>/dev/null ||
    ! git --no-pager cat-file -e "${head}^{commit}" 2>/dev/null; then
    echo "::error::Comparison base $base or head $head is unavailable in the checkout." >&2
    exit 2
fi

if ! merge_bases=$(git --no-pager merge-base --all "$base" "$head"); then
    echo "::warning::No merge-base of base $base and head $head exists in complete history; refusing a selective comparison." >&2
    exit 1
fi
# Criss-cross merges can have several equally good ancestors. Three-dot diff
# chooses only one, so it can omit changes relative to the other ancestors.
# https://git-scm.com/docs/git-merge-base#_discussion
if [[ "$merge_bases" = *$'\n'* ]]; then
    echo "::warning::Multiple merge-bases of base $base and head $head exist; refusing an ambiguous selective comparison." >&2
    exit 1
fi

printf '%s\n' "$base"
