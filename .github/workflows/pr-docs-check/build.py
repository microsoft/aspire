#!/usr/bin/env python3
"""Recompile the pr-docs-check agentic workflow and run its unit tests.

Repeatable local maintenance helper for ``.github/workflows/pr-docs-check.md``.
After editing the workflow source, run this to regenerate the GENERATED lock
file (``pr-docs-check.lock.yml`` -- never hand-edit it) and run the
``compute_signals`` unit tests. Always commit ``pr-docs-check.md`` and the
regenerated ``pr-docs-check.lock.yml`` together, and review the lock diff first.

The same checks run in CI via ``.github/workflows/pr-docs-check-verify.yml``;
this script lets you reproduce them locally before pushing.

Cross-platform: requires Python 3 and the ``gh aw`` extension (github/gh-aw)
pinned to the version in ``.github/aw/actions-lock.json`` (currently v0.77.5):

    gh extension install github/gh-aw --pin v0.77.5

Examples:
    python .github/workflows/pr-docs-check/build.py
    python .github/workflows/pr-docs-check/build.py --verify-parity --skip-tests
"""
from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
from pathlib import Path

WORKFLOW_NAME = "pr-docs-check"
WORKFLOW_DIR = Path(__file__).resolve().parent              # .github/workflows/pr-docs-check
REPO_ROOT = WORKFLOW_DIR.parents[2]                         # repo root (two levels up)
LOCK_REL_PATH = ".github/workflows/pr-docs-check.lock.yml"
ACTIONS_LOCK = REPO_ROOT / ".github" / "aw" / "actions-lock.json"


def _run(cmd: list[str], **kwargs) -> subprocess.CompletedProcess:
    # gh aw resolves workflows relative to .github/workflows, so commands run from REPO_ROOT.
    return subprocess.run(cmd, cwd=REPO_ROOT, **kwargs)


def pinned_gh_aw_version() -> str | None:
    """Return the gh-aw version pinned in actions-lock.json, e.g. 'v0.77.5'."""
    if not ACTIONS_LOCK.exists():
        return None
    data = json.loads(ACTIONS_LOCK.read_text(encoding="utf-8"))
    for key, value in data.get("entries", {}).items():
        if key.startswith("github/gh-aw-actions/setup@"):
            return value.get("version")
    return None


def assert_gh_aw_version() -> None:
    """Warn if the installed gh-aw differs from the actions-lock.json pin.

    A different compiler version emits a different lock file, so catching the
    mismatch up front avoids a confusing diff later.
    """
    pinned = pinned_gh_aw_version()
    if not pinned:
        return
    try:
        # `gh aw version` prints e.g. "gh aw version v0.77.5"; it may also emit a
        # "new release available" notice, so pull the first vN.N.N token.
        out = subprocess.run(
            ["gh", "aw", "version"],
            cwd=REPO_ROOT,
            capture_output=True,
            text=True,
        )
    except FileNotFoundError:
        print("WARNING: 'gh' CLI not found on PATH; skipping gh-aw version check.", file=sys.stderr)
        return
    match = re.search(r"v[0-9][\w.\-]*", (out.stdout or "") + (out.stderr or ""))
    installed = match.group(0) if match else None
    if not installed:
        return
    if installed != pinned:
        print(
            f"WARNING: installed 'gh aw' is {installed} but actions-lock.json pins {pinned}. "
            f"The lock file may differ. To match CI, run: "
            f"gh extension install github/gh-aw --pin {pinned} --force",
            file=sys.stderr,
        )
    else:
        print(f"==> gh aw version {installed} matches pin {pinned}.")


def compile_workflow() -> None:
    assert_gh_aw_version()
    print(f"==> Compiling {WORKFLOW_NAME} (gh aw compile) ...")
    result = _run(["gh", "aw", "compile", WORKFLOW_NAME])
    if result.returncode != 0:
        raise SystemExit(f"gh aw compile failed (exit {result.returncode})")


def verify_parity() -> None:
    """Assert the recompiled lock matches the committed lock byte-for-byte.

    With the source committed, recompiling with the correct tooling must
    reproduce the committed lock. A non-empty diff means the lock was
    hand-edited, the .md changed without recompiling, or the gh-aw version
    is wrong.
    """
    print("==> Verifying lock parity (recompiled lock must match committed lock) ...")
    result = _run(["git", "diff", "--exit-code", "--", LOCK_REL_PATH])
    if result.returncode != 0:
        raise SystemExit(
            f"{LOCK_REL_PATH} differs from the committed version after recompiling. "
            "Commit the regenerated lock, or confirm the 'gh aw' version matches the "
            "pin in actions-lock.json."
        )
    print("    Lock is in sync with the committed source.")


def run_tests() -> None:
    print(f"==> Running compute_signals tests ({sys.executable}) ...")
    # Reuse this interpreter so there is no python/python3 resolution ambiguity.
    result = _run([sys.executable, "-m", "unittest", "discover", "-s", str(WORKFLOW_DIR), "-v"])
    if result.returncode != 0:
        raise SystemExit(f"compute_signals tests failed (exit {result.returncode})")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--skip-compile", action="store_true", help="Skip the gh aw compile step (run tests only).")
    parser.add_argument("--skip-tests", action="store_true", help="Skip the unit tests (compile only).")
    parser.add_argument(
        "--verify-parity",
        action="store_true",
        help="After compiling, assert pr-docs-check.lock.yml has no pending git diff.",
    )
    args = parser.parse_args(argv)

    if not args.skip_compile:
        compile_workflow()
    if args.verify_parity:
        verify_parity()
    if not args.skip_tests:
        run_tests()

    print("==> Done.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
