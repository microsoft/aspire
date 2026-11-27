"""Resolve who owns the documentation PR drafted by the pr-docs-check workflow.

`.github/workflows/pr-docs-check.md` runs this script twice:

* As a pre-agent step, so the agent can read `.pr-docs-check/people.json` for
  context (for example, to address open questions to the SME in its summary).
* In the trusted `notify-source-pr` safe-output job, which recomputes the
  result from the live source PR. Only that job's result is used for
  assignment, review requests, and mentions. The agent never supplies logins,
  because anything written by the agent job is untrusted.

Rules
-----

Assignees are the de-duplicated union, in priority order, of:

1. the source PR author (core team or community),
2. the user who merged the source PR (``merged_by``), only when the effective
   author is not on the Aspire team. A team author already owns the follow-up,
   so also assigning the merger is redundant,
3. the source PR assignees (for Copilot-authored PRs these are the humans who
   started the Copilot session).

The effective author is the human author, or, for bot-authored PRs (Copilot
coding agent), the human source assignees. "On the Aspire team" means write,
maintain, or admin permission on microsoft/aspire. The workflow looks that up
for the logins printed by ``team-candidates`` and passes the team members in;
a failed lookup counts as not on the team, so the merger is assigned rather
than nobody.

Bots are never assigned or mentioned. GitHub caps a pull request or issue at 10
assignees, so anyone past the cap is returned as ``overflow`` and mentioned
instead: https://docs.github.com/en/rest/issues/assignees#add-assignees-to-an-issue

The SME reviewer is the most recent approver who is not already an intended
assignee, else the most recent reviewer with another standing decision (for
example CHANGES_REQUESTED). A reviewer who only ever commented is not a strong
enough signal, so the SME is left empty and aspire.dev CODEOWNERS plus Copilot
code review cover the PR.

The only bot that is ever requested is Copilot code review, and only as a
reviewer; the notify job requests it directly, not through this script.

Usage
-----

    python3 resolve_people.py team-candidates <pr.json>
    python3 resolve_people.py resolve <pr.json> <reviews.json> <team.json> <people.json>
    python3 resolve_people.py footer <people.json> <assigned.json> <body.md> <out.md>

``pr.json`` is the raw body of ``GET /repos/microsoft/aspire/pulls/{N}`` and
``reviews.json`` is the paginated, JSON-arrayed body of
``GET /repos/microsoft/aspire/pulls/{N}/reviews``. ``team-candidates`` prints
the effective-author logins, one per line, and ``team.json`` is the JSON array
of those logins that are on the Aspire team. ``assigned.json`` is the JSON
array of logins that GitHub actually kept after the assign call, ``body.md`` is
the current docs PR or issue body, and ``out.md`` receives the updated body.
"""

from __future__ import annotations

import json
import re
import sys
from typing import Any

MAX_ASSIGNEES = 10

FOOTER_START = "<!-- pr-docs-check:people:start -->"
FOOTER_END = "<!-- pr-docs-check:people:end -->"

# GitHub usernames are 1-39 alphanumerics or single inner hyphens. Anything else
# (including `name[bot]` App identities) is rejected, so a login can never inject
# markdown into the footer or address an unexpected account.
_LOGIN_RE = re.compile(r"^[A-Za-z0-9](?:[A-Za-z0-9]|-(?=[A-Za-z0-9])){0,38}$")

# Backstop for automation accounts that GitHub reports with `type: "User"`.
# The primary check is the API's own `type == "Bot"`.
_BOT_LOGINS = {"copilot", "copilot-swe-agent", "dependabot", "github-actions", "aspire-bot"}

# Review states that represent a standing decision. A later COMMENTED review
# (which GitHub also emits for plain thread replies) must not override one of
# these, mirroring GitHub's own "latest decision wins" review-state rule.
_DECISION_STATES = frozenset({"APPROVED", "CHANGES_REQUESTED", "DISMISSED"})

_ROLE_LABELS = {"author": "author", "merged_by": "merged by"}


def is_bot(user: Any) -> bool:
    """Return True for bots, automation accounts, and unusable identities."""
    if isinstance(user, dict):
        if user.get("type") == "Bot":
            return True
        login = user.get("login")
    else:
        login = user
    candidate = (login or "").strip().lower() if isinstance(login, str) else ""
    if not candidate:
        return True
    if candidate.endswith("[bot]") or candidate in _BOT_LOGINS:
        return True
    # Any Copilot-family login is automation.
    return "copilot" in candidate


def _human_login(user: Any) -> str:
    """Return the login of a human GitHub user, or "" for bots and invalid data."""
    if not isinstance(user, dict) or is_bot(user):
        return ""
    login = user.get("login")
    if not isinstance(login, str) or not _LOGIN_RE.match(login):
        return ""
    return login


def _latest_review_by_reviewer(reviews: list[Any]) -> dict[str, dict]:
    """Collapse review events to each human reviewer's effective latest review.

    GitHub returns one entry per review event, so a reviewer can appear several
    times (for example COMMENTED then APPROVED, or APPROVED then COMMENTED). A
    bare COMMENTED event must not erase an earlier decision: GitHub emits one
    for ordinary thread replies ("thanks!"), and its own review state keeps the
    latest APPROVED or CHANGES_REQUESTED as the reviewer's standing. Without
    this, a lone approver who later comments would drop out of the approved set.
    ISO-8601 timestamps such as "2026-05-10T18:34:22Z" sort correctly as strings.
    """
    latest: dict[str, dict] = {}
    for review in reviews:
        if not isinstance(review, dict):
            continue
        login = _human_login(review.get("user"))
        if not login:
            continue
        candidate = {
            "login": login,
            "state": review.get("state") or "",
            "submitted_at": review.get("submitted_at") or "",
        }
        key = login.lower()
        existing = latest.get(key)
        if existing is None:
            latest[key] = candidate
            continue
        existing_is_decision = existing["state"] in _DECISION_STATES
        candidate_is_decision = candidate["state"] in _DECISION_STATES
        if existing_is_decision and not candidate_is_decision:
            continue
        if candidate_is_decision and not existing_is_decision:
            latest[key] = candidate
            continue
        if candidate["submitted_at"] >= existing["submitted_at"]:
            latest[key] = candidate
    return latest


def effective_authors(pr: dict) -> list[str]:
    """Return the human author, or the human assignees of a bot-authored PR."""
    author = _human_login(pr.get("user"))
    if author:
        return [author]
    logins: list[str] = []
    for assignee in pr.get("assignees") or []:
        login = _human_login(assignee)
        if login and login.lower() not in {existing.lower() for existing in logins}:
            logins.append(login)
    return logins


def resolve_people(pr: dict, reviews: list[Any], team: list[Any]) -> dict:
    """Resolve docs PR assignees and the SME reviewer from the raw source PR."""
    people: list[str] = []
    roles: dict[str, list[str]] = {}

    def add(user: Any, role: str) -> None:
        login = _human_login(user)
        if not login:
            return
        key = login.lower()
        if key not in roles:
            people.append(login)
            roles[key] = []
        if role not in roles[key]:
            roles[key].append(role)

    team_keys = {login.lower() for login in team if isinstance(login, str)}
    author_on_team = any(login.lower() in team_keys for login in effective_authors(pr))

    add(pr.get("user"), "author")
    if not author_on_team:
        add(pr.get("merged_by"), "merged_by")
    for assignee in pr.get("assignees") or []:
        add(assignee, "assignee")

    author = _human_login(pr.get("user"))
    merged_by = _human_login(pr.get("merged_by"))
    intended = {login.lower() for login in people}

    latest_reviews = _latest_review_by_reviewer(reviews)
    candidates = [
        {
            "login": info["login"],
            "state": info["state"],
            "is_assignee": key in intended,
        }
        for key, info in sorted(latest_reviews.items())
        if key != author.lower()
    ]
    eligible = [
        info
        for key, info in latest_reviews.items()
        if key not in intended and key != author.lower()
    ]

    sme_login = ""
    sme_source = "none"
    approved = [info for info in eligible if info["state"] == "APPROVED"]
    substantive = [info for info in eligible if info["state"] in _DECISION_STATES]
    if approved:
        sme_login = max(approved, key=lambda info: info["submitted_at"])["login"]
        sme_source = "approved_reviewer"
    elif substantive:
        sme_login = max(substantive, key=lambda info: info["submitted_at"])["login"]
        sme_source = "substantive_reviewer"

    return {
        "author": author,
        "author_on_team": author_on_team,
        # Empty when the merger is not assigned, so the footer does not mention them.
        "merged_by": "" if author_on_team else merged_by,
        "assignees": people[:MAX_ASSIGNEES],
        "overflow": people[MAX_ASSIGNEES:],
        "roles": {login: roles[login.lower()] for login in people},
        "sme_login": sme_login,
        "sme_source": sme_source,
        "candidates": candidates,
    }


def render_footer(people: dict, assigned: list[Any]) -> str:
    """Render the People footer for the docs PR or fallback issue body.

    Example (rendered as plain markdown; mentions are never placed in code
    spans or blocks, because GitHub does not notify mentions inside code):

        <!-- pr-docs-check:people:start -->

        ---

        **Source PR:** @alice (author, merged by)

        **cc:** @carol @dave

        <!-- pr-docs-check:people:end -->

    The source PR author is always mentioned, and so is the merger when they
    were assigned (non-team author). Every other intended
    assignee that GitHub did not keep (no access to the docs repository, or past
    the assignee cap) is mentioned on the cc line so they are still notified.
    Returns "" when there is nobody to mention.
    """
    assigned_keys = {
        login.lower() for login in assigned if isinstance(login, str) and _LOGIN_RE.match(login)
    }

    source: list[tuple[str, list[str]]] = []
    for role in ("author", "merged_by"):
        login = people.get(role) or ""
        if not isinstance(login, str) or not _LOGIN_RE.match(login):
            continue
        existing = next((entry for entry in source if entry[0].lower() == login.lower()), None)
        if existing is None:
            source.append((login, [role]))
        else:
            existing[1].append(role)
    mentioned = {login.lower() for login, _ in source}

    cc: list[str] = []
    for login in list(people.get("assignees") or []) + list(people.get("overflow") or []):
        if not isinstance(login, str) or not _LOGIN_RE.match(login):
            continue
        key = login.lower()
        if key in assigned_keys or key in mentioned:
            continue
        mentioned.add(key)
        cc.append(login)

    if not source and not cc:
        return ""

    lines = [FOOTER_START, "", "---", ""]
    if source:
        rendered = ", ".join(
            f"@{login} ({', '.join(_ROLE_LABELS[role] for role in roles)})"
            for login, roles in source
        )
        lines += [f"**Source PR:** {rendered}", ""]
    if cc:
        lines += ["**cc:** " + " ".join(f"@{login}" for login in cc), ""]
    lines.append(FOOTER_END)
    return "\n".join(lines)


def apply_footer(body: str, footer: str) -> str:
    """Replace any previous People footer in ``body`` (idempotent on reruns)."""
    body = body or ""
    start = body.find(FOOTER_START)
    end = body.find(FOOTER_END, start) if start != -1 else -1
    if start != -1 and end != -1:
        body = body[:start] + body[end + len(FOOTER_END):]
    body = body.rstrip()
    if not footer:
        return body + "\n" if body else ""
    return f"{body}\n\n{footer}\n" if body else f"{footer}\n"


def _load_json(path: str) -> Any:
    with open(path, encoding="utf-8") as fh:
        return json.load(fh)


def main(argv: list[str]) -> int:
    if len(argv) == 3 and argv[1] == "team-candidates":
        pr = _load_json(argv[2])
        for login in effective_authors(pr if isinstance(pr, dict) else {}):
            print(login)
        return 0

    if len(argv) == 6 and argv[1] == "resolve":
        pr = _load_json(argv[2])
        reviews = _load_json(argv[3])
        team = _load_json(argv[4])
        result = resolve_people(
            pr if isinstance(pr, dict) else {},
            reviews if isinstance(reviews, list) else [],
            team if isinstance(team, list) else [],
        )
        with open(argv[5], "w", encoding="utf-8") as fh:
            json.dump(result, fh, indent=2)
            fh.write("\n")
        return 0

    if len(argv) == 6 and argv[1] == "footer":
        people = _load_json(argv[2])
        assigned = _load_json(argv[3])
        with open(argv[4], encoding="utf-8") as fh:
            body = fh.read()
        footer = render_footer(
            people if isinstance(people, dict) else {},
            assigned if isinstance(assigned, list) else [],
        )
        with open(argv[5], "w", encoding="utf-8", newline="\n") as fh:
            fh.write(apply_footer(body, footer))
        return 0

    print(
        f"usage: {argv[0]} team-candidates <pr.json>\n"
        f"       {argv[0]} resolve <pr.json> <reviews.json> <team.json> <people.json>\n"
        f"       {argv[0]} footer <people.json> <assigned.json> <body.md> <out.md>",
        file=sys.stderr,
    )
    return 2


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
