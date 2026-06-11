"""Resolve the subject-matter expert (SME) for the pr-docs-check workflow.

This script is invoked by `.github/workflows/pr-docs-check.md` as a pre-agent
step, mirroring the existing `target.json` / `signals.json` / `pr.json`
pattern. It writes `.pr-docs-check/sme.json` so the agent reads a single
resolved result in Step 2 instead of fetching reviews and running ~50 lines of
branching logic inside the (token-expensive, re-sent-every-turn) agent loop.

The SME is the human best placed to review the drafted documentation PR:

* If the source PR was authored by the GitHub Copilot Coding Agent, the SME is
  the human who *initiated* the session — recorded as a human assignee on the
  PR — not whoever approved the bot's output.
* Otherwise it is the reviewer who engaged most authoritatively with the change
  (an approver, else the most recent substantive reviewer).

The algorithm below is a faithful port of the Step 2 instructions in
pr-docs-check.md. The only piece deliberately left to the agent is the
CODEOWNERS hint (`sme_source == "none"` with `needs_codeowners_fallback ==
true`): matching CODEOWNERS glob patterns against changed files is fuzzy and
better handled with the agent's judgment, and it is a last-resort hint anyway.

Usage
-----

    python3 resolve_sme.py <pr.json> <reviews.json> <out.json>

where `pr.json` is the curated PR context produced by `compute_pr_context.py`
(it provides `author` and `assignees`) and `reviews.json` is the concatenated
body of `GET /repos/microsoft/aspire/pulls/{N}/reviews?per_page=100`
(paginated and JSON-arrayed).

Running the tests
-----------------

    python3 -m unittest discover -s .github/workflows/pr-docs-check -v
"""

from __future__ import annotations

import json
import sys

# Bot logins to exclude when looking for a human SME. Matches the exclusion
# list in pr-docs-check.md Step 2: GitHub Copilot variants, well-known
# automation accounts, and the GitHub App convention of a `[bot]` suffix.
#
# Examples it must treat as bots:
#   Copilot, copilot-swe-agent, some-app[bot], dependabot, github-actions,
#   aspire-bot
_BOT_LOGINS = {"copilot", "copilot-swe-agent", "dependabot", "github-actions", "aspire-bot"}


def is_bot(login: str) -> bool:
    """Return True if a login is a bot/automation account (never a human SME)."""
    candidate = (login or "").strip().lower()
    if not candidate:
        return True
    if candidate.endswith("[bot]"):
        return True
    if candidate in _BOT_LOGINS:
        return True
    # Any Copilot-family login (e.g. "copilot-swe-agent[bot]") is automation.
    if "copilot" in candidate:
        return True
    return False


def is_copilot_authored(author: dict) -> bool:
    """Return True if the PR was authored by the Copilot Coding Agent.

    The agent authors as a `Bot` whose login is in the Copilot family
    (`Copilot`, `copilot-swe-agent`, `...copilot...[bot]`).
    """
    login = (author.get("login") or "").lower()
    type_ = author.get("type") or ""
    return type_ == "Bot" and "copilot" in login


def _latest_review_by_reviewer(reviews: list[dict]) -> dict[str, dict]:
    """Collapse review events to each reviewer's most recent one.

    GitHub returns one entry per review *event*; a reviewer may appear several
    times (e.g. COMMENTED then APPROVED). We keep only the latest by
    `submitted_at`. ISO-8601 timestamps sort correctly as plain strings, e.g.
    "2026-05-10T18:34:22Z".
    """
    latest: dict[str, dict] = {}
    for review in reviews:
        user = review.get("user") or {}
        login = user.get("login") or ""
        if not login:
            continue
        submitted_at = review.get("submitted_at") or ""
        state = review.get("state") or ""
        existing = latest.get(login)
        if existing is None or submitted_at >= existing["submitted_at"]:
            latest[login] = {"state": state, "submitted_at": submitted_at}
    return latest


def _result(login: str, source: str, *, needs_codeowners_fallback: bool = False) -> dict:
    return {
        "sme_login": login or "",
        "sme_source": source,
        "needs_codeowners_fallback": needs_codeowners_fallback,
    }


def resolve_sme(pr: dict, reviews: list[dict]) -> dict:
    """Resolve the SME from curated PR context + raw reviews.

    Returns a dict with:
      * sme_login                 the chosen login (no '@'), or "" if undecided
      * sme_source                how it was chosen (for transparency / logs)
      * needs_codeowners_fallback True only when the agent should consult
                                  CODEOWNERS as a last-resort hint
      * candidates                eligible reviewers (login + latest state)
    """
    author = pr.get("author") or {}
    author_login = (author.get("login") or "").lower()
    assignees = [a for a in (pr.get("assignees") or []) if a]
    latest_reviews = _latest_review_by_reviewer(reviews)

    # --- Step 2a: Copilot-authored PRs --------------------------------------
    if is_copilot_authored(author):
        human_assignees = [a for a in assignees if not is_bot(a)]
        if len(human_assignees) == 1:
            return _add_candidates(_result(human_assignees[0], "copilot_originator"), latest_reviews, author_login)
        if len(human_assignees) > 1:
            # Prefer the assignee whose latest review is APPROVED; if still
            # ambiguous, the one appearing earliest in assignees[].
            approved = [a for a in human_assignees if latest_reviews.get(a, {}).get("state") == "APPROVED"]
            if approved:
                return _add_candidates(_result(approved[0], "copilot_originator_approved"), latest_reviews, author_login)
            return _add_candidates(_result(human_assignees[0], "copilot_originator"), latest_reviews, author_login)
        # No human assignees (unusual) — fall through to the reviewer logic.

    # --- Step 2b: human-authored PRs (or 2a fallthrough) --------------------
    eligible = {
        login: info
        for login, info in latest_reviews.items()
        if login.lower() != author_login and not is_bot(login)
    }

    # Prefer APPROVED reviewers; among them the most recent.
    approved = {login: info for login, info in eligible.items() if info["state"] == "APPROVED"}
    if approved:
        chosen = max(approved.items(), key=lambda kv: kv[1]["submitted_at"])[0]
        return _add_candidates(_result(chosen, "approved_reviewer"), latest_reviews, author_login)

    # Fallback A: a reviewer whose latest state is substantive but not a bare
    # COMMENTED (e.g. CHANGES_REQUESTED), most recent first.
    substantive = {login: info for login, info in eligible.items() if info["state"] not in ("", "COMMENTED")}
    if substantive:
        chosen = max(substantive.items(), key=lambda kv: kv[1]["submitted_at"])[0]
        return _add_candidates(_result(chosen, "substantive_reviewer"), latest_reviews, author_login)

    # Fallback B: no usable reviewer signal at all — let the agent consult
    # CODEOWNERS as a hint. (Glob matching against changed files is fuzzy, so
    # it intentionally stays in the agent rather than here.)
    if not eligible:
        return _add_candidates(_result("", "none", needs_codeowners_fallback=True), latest_reviews, author_login)

    # Reviewers exist but only ever COMMENTED: per Step 2b this is not a strong
    # enough signal to pick one, and CODEOWNERS is only for "no reviews at all".
    # Leave the SME empty; the workflow drafts without an explicit reviewer.
    return _add_candidates(_result("", "none"), latest_reviews, author_login)


def _add_candidates(result: dict, latest_reviews: dict[str, dict], author_login: str) -> dict:
    """Attach the eligible-reviewer list for transparency in the step log."""
    result["candidates"] = [
        {"login": login, "state": info["state"]}
        for login, info in sorted(latest_reviews.items())
        if login.lower() != author_login and not is_bot(login)
    ]
    return result


def main(argv: list[str]) -> int:
    if len(argv) != 4:
        print(f"usage: {argv[0]} <pr.json> <reviews.json> <out.json>", file=sys.stderr)
        return 2

    with open(argv[1], encoding="utf-8") as fh:
        pr = json.load(fh)
    with open(argv[2], encoding="utf-8") as fh:
        reviews = json.load(fh)
    if not isinstance(reviews, list):
        reviews = []

    result = resolve_sme(pr, reviews)

    with open(argv[3], "w", encoding="utf-8") as fh:
        json.dump(result, fh, indent=2)
        fh.write("\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
