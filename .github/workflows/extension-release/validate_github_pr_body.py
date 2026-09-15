#!/usr/bin/env python3

from __future__ import annotations

import pathlib
import sys


MAX_GITHUB_PR_BODY_CHARS = 65_536


def main() -> int:
    if len(sys.argv) != 2:
        raise SystemExit("Usage: validate_github_pr_body.py <pr_body.md>")

    pr_body_path = pathlib.Path(sys.argv[1])
    body = pr_body_path.read_text(encoding="utf-8")
    body_length = len(body)

    if body_length > MAX_GITHUB_PR_BODY_CHARS:
        raise SystemExit(
            f"Pull request body at '{pr_body_path}' has {body_length} characters and exceeds GitHub's "
            f"{MAX_GITHUB_PR_BODY_CHARS}-character pull request body limit. Shorten the body before calling "
            "gh pr create/edit."
        )

    print(
        f"GitHub pull request body length for '{pr_body_path}': {body_length} characters "
        f"(limit {MAX_GITHUB_PR_BODY_CHARS})."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
