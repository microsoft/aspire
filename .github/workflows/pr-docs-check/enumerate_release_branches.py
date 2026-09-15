"""Enumerate release branches from microsoft/aspire.dev."""

from __future__ import annotations

from collections.abc import Callable, Sequence
import subprocess
import sys


class BranchEnumerationError(RuntimeError):
    """Raised when aspire.dev branches cannot be enumerated."""


def enumerate_release_branches(
    run: Callable[..., subprocess.CompletedProcess[str]] = subprocess.run,
) -> list[str]:
    result = run(
        [
            "gh",
            "api",
            "--paginate",
            "/repos/microsoft/aspire.dev/branches?per_page=100",
            "--jq",
            '.[].name | select(startswith("release/"))',
        ],
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        check=False,
    )
    if result.returncode != 0:
        detail = result.stderr.strip()
        raise BranchEnumerationError(
            detail or "Could not enumerate microsoft/aspire.dev branches."
        )

    return sorted(
        {
            branch
            for line in result.stdout.splitlines()
            if (branch := line.strip()).startswith("release/")
        }
    )


def main(argv: Sequence[str] | None = None) -> int:
    if argv:
        print("ERROR: No arguments are supported.", file=sys.stderr)
        return 2

    try:
        branches = enumerate_release_branches()
    except BranchEnumerationError as error:
        print(f"ERROR: {error}", file=sys.stderr)
        return 1

    for branch in branches:
        print(branch)
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
