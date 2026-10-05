"""Build compact PR context for the pr-docs-check workflow.

This script is invoked by `.github/workflows/pr-docs-check.md` as a
pre-agent step, reusing the same `GET /repos/microsoft/aspire/pulls/{N}`
and `.../files` payloads the signal-computation step already fetches. It
writes `.pr-docs-check/pr.json`, a small curated projection of the PR
metadata the agent needs in Step 1 ("Gather PR Information").

Historically the agent gathered this itself with several GitHub tool
calls (get PR, list files, parse linked issues), each adding a turn and a
verbose API response that is then re-sent on every subsequent turn. That
work is fully deterministic, so doing it once here — from data already in
hand — removes those round-trips and shrinks the per-turn token cost.

Usage
-----

    python3 compute_pr_context.py <pr.json> <files.json> <out.json>

where `pr.json` is the body of `GET /repos/microsoft/aspire/pulls/{N}`
and `files.json` is the concatenated body of
`GET /repos/microsoft/aspire/pulls/{N}/files?per_page=100` (paginated
and JSON-arrayed).

Diff hunks (`patch`) are intentionally NOT included: they are only needed
on the rare doc-drafting path (Step 9), and embedding every patch here
would bloat the context the agent re-sends each turn. The agent fetches
the specific file patches it needs on demand when drafting.
"""

from __future__ import annotations

import json
import re
import sys

# GitHub's issue-closing keywords, case-insensitive, with an optional ':'
# after the keyword and an optional `owner/repo` before the `#N`. This is
# the SAME pattern the target-branch resolver uses in pr-docs-check.md so
# the linked-issue set the agent sees matches the one used to pick the
# docs target branch.
#
# Examples it must accept:
#   Fixes #123
#   Fixes: #123
#   Closes microsoft/aspire#456
#   Resolves: microsoft/aspire#789
#
# https://docs.github.com/en/issues/tracking-your-work-with-issues/linking-a-pull-request-to-an-issue#linking-a-pull-request-to-an-issue-using-a-keyword
_LINKED_ISSUE_RE = re.compile(
    r"\b(close[sd]?|fix(?:es|ed)?|resolve[sd]?)\s*:?\s+"
    r"(?:([A-Za-z0-9._-]+/[A-Za-z0-9._-]+))?#(\d+)\b",
    re.IGNORECASE,
)


def extract_linked_issues(body: str) -> list[int]:
    """Return same-repo (microsoft/aspire) linked issue numbers, in order, de-duplicated.

    Cross-repo references are dropped — they don't describe this repo's change.
    """
    seen: set[str] = set()
    out: list[int] = []
    for match in _LINKED_ISSUE_RE.finditer(body or ""):
        repo = (match.group(2) or "microsoft/aspire").lower()
        if repo != "microsoft/aspire":
            continue
        number = match.group(3)
        if number in seen:
            continue
        seen.add(number)
        out.append(int(number))
    return out


def build_context(pr: dict, files: list[dict]) -> dict:
    """Project the raw PR + files payloads into the compact pr.json shape."""
    user = pr.get("user") or {}
    milestone = pr.get("milestone") or {}
    base = pr.get("base") or {}

    changed_files = [
        {
            "filename": f.get("filename"),
            "status": f.get("status"),
            "additions": f.get("additions", 0),
            "deletions": f.get("deletions", 0),
        }
        for f in files
    ]

    return {
        "number": pr.get("number"),
        "title": pr.get("title") or "",
        "body": pr.get("body") or "",
        "author": {
            "login": user.get("login") or "",
            # "User" or "Bot" — Step 2 uses this to distinguish Copilot-authored PRs.
            "type": user.get("type") or "",
        },
        "base_ref": base.get("ref") or "",
        "milestone": milestone.get("title") or None,
        "labels": [lab.get("name") for lab in (pr.get("labels") or []) if lab.get("name")],
        "assignees": [a.get("login") for a in (pr.get("assignees") or []) if a.get("login")],
        "linked_issues": extract_linked_issues(pr.get("body") or ""),
        "changed_file_count": len(changed_files),
        "changed_files": changed_files,
    }


def main(argv: list[str]) -> int:
    if len(argv) != 4:
        print(f"usage: {argv[0]} <pr.json> <files.json> <out.json>", file=sys.stderr)
        return 2

    with open(argv[1], encoding="utf-8") as fh:
        pr = json.load(fh)
    with open(argv[2], encoding="utf-8") as fh:
        files = json.load(fh)
    if not isinstance(files, list):
        files = []

    context = build_context(pr, files)

    with open(argv[3], "w", encoding="utf-8") as fh:
        json.dump(context, fh, indent=2)
        fh.write("\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
