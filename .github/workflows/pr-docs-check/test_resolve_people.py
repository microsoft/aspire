import json
import tempfile
import unittest
from pathlib import Path

from resolve_people import (
    FOOTER_END,
    FOOTER_START,
    apply_footer,
    effective_authors,
    main,
    render_footer,
    resolve_people,
)


def user(login: str, type_: str = "User") -> dict:
    return {"login": login, "type": type_}


def review(login: str, state: str, submitted_at: str, type_: str = "User") -> dict:
    return {"user": user(login, type_), "state": state, "submitted_at": submitted_at}


def resolve(source: dict, reviews: list, team: list | None = None) -> dict:
    return resolve_people(source, reviews, team or [])


def pr(author: dict, merged_by: dict | None = None, assignees: list[dict] | None = None) -> dict:
    return {"user": author, "merged_by": merged_by, "assignees": assignees or []}


class ResolveAssigneesTests(unittest.TestCase):
    def test_team_author_is_assigned_without_merger(self) -> None:
        people = resolve(
            pr(user("team-dev"), user("maintainer"), [user("helper")]),
            [],
            ["Team-Dev"],
        )

        self.assertEqual(["team-dev", "helper"], people["assignees"])
        self.assertTrue(people["author_on_team"])
        self.assertEqual("", people["merged_by"])

    def test_copilot_pr_with_team_originator_skips_merger(self) -> None:
        source = pr(user("Copilot", "Bot"), user("maintainer"), [user("originator")])

        people = resolve(source, [], ["originator"])

        self.assertEqual(["originator"], effective_authors(source))
        self.assertEqual(["originator"], people["assignees"])

    def test_copilot_pr_with_community_originator_assigns_merger(self) -> None:
        people = resolve(
            pr(user("Copilot", "Bot"), user("maintainer"), [user("originator")]),
            [],
            [],
        )

        self.assertEqual(["maintainer", "originator"], people["assignees"])
        self.assertFalse(people["author_on_team"])

    def test_skipped_merger_can_be_sme(self) -> None:
        people = resolve(
            pr(user("team-dev"), user("maintainer")),
            [review("maintainer", "APPROVED", "2026-05-01T00:00:00Z")],
            ["team-dev"],
        )

        self.assertEqual("maintainer", people["sme_login"])
        self.assertEqual("approved_reviewer", people["sme_source"])

    def test_community_author_merger_and_assignees_in_priority_order(self) -> None:
        people = resolve(
            pr(user("community-dev"), user("maintainer"), [user("helper"), user("Maintainer")]),
            [],
        )

        self.assertEqual(["community-dev", "maintainer", "helper"], people["assignees"])
        self.assertEqual([], people["overflow"])
        self.assertEqual("community-dev", people["author"])
        self.assertEqual("maintainer", people["merged_by"])
        self.assertEqual(
            {
                "community-dev": ["author"],
                "maintainer": ["merged_by", "assignee"],
                "helper": ["assignee"],
            },
            people["roles"],
        )

    def test_author_who_merged_is_listed_once(self) -> None:
        people = resolve(pr(user("alice"), user("alice")), [])

        self.assertEqual(["alice"], people["assignees"])
        self.assertEqual({"alice": ["author", "merged_by"]}, people["roles"])

    def test_bots_are_never_assigned(self) -> None:
        people = resolve(
            pr(
                user("Copilot", "Bot"),
                user("merge-queue-app", "Bot"),
                [
                    user("originator"),
                    user("dependabot"),
                    user("renovate[bot]"),
                    user("some-unlisted-automation", "Bot"),
                ],
            ),
            [],
        )

        self.assertEqual(["originator"], people["assignees"])
        self.assertEqual("", people["author"])
        self.assertEqual("", people["merged_by"])

    def test_assignees_past_github_cap_overflow(self) -> None:
        extra = [user(f"dev{index}") for index in range(12)]

        people = resolve(pr(user("author"), user("merger"), extra), [])

        self.assertEqual(
            ["author", "merger"] + [f"dev{index}" for index in range(8)],
            people["assignees"],
        )
        self.assertEqual(
            [f"dev{index}" for index in range(8, 12)],
            people["overflow"],
        )

    def test_invalid_logins_are_ignored(self) -> None:
        people = resolve(
            pr(user("alice"), user("bad login`"), [user("-leading"), {"type": "User"}]),
            [],
        )

        self.assertEqual(["alice"], people["assignees"])


class ResolveSmeTests(unittest.TestCase):
    def test_most_recent_approver_is_sme(self) -> None:
        people = resolve(
            pr(user("author"), user("merger")),
            [
                review("older-approver", "APPROVED", "2026-05-01T00:00:00Z"),
                review("newer-approver", "APPROVED", "2026-05-02T00:00:00Z"),
            ],
        )

        self.assertEqual("newer-approver", people["sme_login"])
        self.assertEqual("approved_reviewer", people["sme_source"])

    def test_sme_skips_reviewers_who_are_assignees(self) -> None:
        people = resolve(
            pr(user("author"), user("merger"), [user("assigned-reviewer")]),
            [
                review("merger", "APPROVED", "2026-05-03T00:00:00Z"),
                review("assigned-reviewer", "APPROVED", "2026-05-04T00:00:00Z"),
                review("other-approver", "APPROVED", "2026-05-01T00:00:00Z"),
            ],
        )

        self.assertEqual("other-approver", people["sme_login"])
        self.assertEqual(
            [
                {"login": "assigned-reviewer", "state": "APPROVED", "is_assignee": True},
                {"login": "merger", "state": "APPROVED", "is_assignee": True},
                {"login": "other-approver", "state": "APPROVED", "is_assignee": False},
            ],
            people["candidates"],
        )

    def test_no_sme_when_only_approver_is_assigned(self) -> None:
        people = resolve(
            pr(user("author"), user("merger")),
            [review("merger", "APPROVED", "2026-05-03T00:00:00Z")],
        )

        self.assertEqual("", people["sme_login"])
        self.assertEqual("none", people["sme_source"])

    def test_later_comment_does_not_erase_approval(self) -> None:
        people = resolve(
            pr(user("author"), user("merger")),
            [
                review("approver", "APPROVED", "2026-05-01T00:00:00Z"),
                review("approver", "COMMENTED", "2026-05-02T00:00:00Z"),
            ],
        )

        self.assertEqual("approver", people["sme_login"])

    def test_changes_requested_reviewer_is_fallback_sme(self) -> None:
        people = resolve(
            pr(user("author"), user("merger")),
            [
                review("commenter", "COMMENTED", "2026-05-03T00:00:00Z"),
                review("requester", "CHANGES_REQUESTED", "2026-05-01T00:00:00Z"),
            ],
        )

        self.assertEqual("requester", people["sme_login"])
        self.assertEqual("substantive_reviewer", people["sme_source"])

    def test_comment_only_reviewers_are_not_sme(self) -> None:
        people = resolve(
            pr(user("author"), user("merger")),
            [review("commenter", "COMMENTED", "2026-05-03T00:00:00Z")],
        )

        self.assertEqual("", people["sme_login"])

    def test_bot_reviewers_are_never_sme(self) -> None:
        people = resolve(
            pr(user("author"), user("merger")),
            [
                review("copilot-pull-request-reviewer[bot]", "APPROVED", "2026-05-03T00:00:00Z", "Bot"),
                review("unlisted-automation", "APPROVED", "2026-05-04T00:00:00Z", "Bot"),
            ],
        )

        self.assertEqual("", people["sme_login"])
        self.assertEqual([], people["candidates"])


class FooterTests(unittest.TestCase):
    def test_footer_mentions_source_people_and_unassignable_people(self) -> None:
        people = resolve(
            pr(user("community-dev"), user("maintainer"), [user("helper"), user("outsider")]),
            [],
        )

        footer = render_footer(people, ["maintainer", "helper"])

        self.assertEqual(
            "\n".join(
                [
                    FOOTER_START,
                    "",
                    "---",
                    "",
                    "**Source PR:** @community-dev (author), @maintainer (merged by)",
                    "",
                    "**cc:** @outsider",
                    "",
                    FOOTER_END,
                ]
            ),
            footer,
        )
        self.assertNotIn("`", footer)

    def test_footer_collapses_author_who_merged(self) -> None:
        people = resolve(pr(user("alice"), user("alice")), [])

        footer = render_footer(people, ["alice"])

        self.assertEqual(
            "\n".join(
                [
                    FOOTER_START,
                    "",
                    "---",
                    "",
                    "**Source PR:** @alice (author, merged by)",
                    "",
                    FOOTER_END,
                ]
            ),
            footer,
        )

    def test_footer_cc_includes_overflow(self) -> None:
        extra = [user(f"dev{index}") for index in range(10)]
        people = resolve(pr(user("author"), user("merger"), extra), [])

        footer = render_footer(people, people["assignees"])

        self.assertIn("**cc:** @dev8 @dev9\n", footer)

    def test_footer_is_empty_without_humans(self) -> None:
        people = resolve(pr(user("Copilot", "Bot"), user("merge-app", "Bot")), [])

        self.assertEqual("", render_footer(people, []))

    def test_apply_footer_replaces_previous_footer(self) -> None:
        people = resolve(pr(user("alice"), user("bob")), [])
        footer = render_footer(people, [])
        once = apply_footer("Docs body\n", footer)

        twice = apply_footer(once, footer)

        self.assertEqual(once, twice)
        self.assertEqual(f"Docs body\n\n{footer}\n", once)

    def test_footer_omits_merger_for_team_author(self) -> None:
        people = resolve(pr(user("team-dev"), user("maintainer")), [], ["team-dev"])

        footer = render_footer(people, ["team-dev"])

        self.assertIn("**Source PR:** @team-dev (author)\n", footer)
        self.assertNotIn("@maintainer", footer)

    def test_apply_footer_removes_footer_when_empty(self) -> None:
        body = f"Docs body\n\n{FOOTER_START}\nold\n{FOOTER_END}\n"

        self.assertEqual("Docs body\n", apply_footer(body, ""))


class CliTests(unittest.TestCase):
    def test_resolve_then_footer(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "pr.json").write_text(
                json.dumps(pr(user("alice"), user("bob"))), encoding="utf-8"
            )
            (root / "reviews.json").write_text("[]", encoding="utf-8")
            (root / "team.json").write_text("[]", encoding="utf-8")
            (root / "assigned.json").write_text('["bob"]', encoding="utf-8")
            (root / "body.md").write_text("Docs body", encoding="utf-8")

            resolve_exit = main(
                [
                    "resolve_people.py",
                    "resolve",
                    str(root / "pr.json"),
                    str(root / "reviews.json"),
                    str(root / "team.json"),
                    str(root / "people.json"),
                ]
            )
            footer_exit = main(
                [
                    "resolve_people.py",
                    "footer",
                    str(root / "people.json"),
                    str(root / "assigned.json"),
                    str(root / "body.md"),
                    str(root / "out.md"),
                ]
            )
            people = json.loads((root / "people.json").read_text(encoding="utf-8"))
            body = (root / "out.md").read_text(encoding="utf-8")

        self.assertEqual(0, resolve_exit)
        self.assertEqual(0, footer_exit)
        self.assertEqual(["alice", "bob"], people["assignees"])
        self.assertIn("**Source PR:** @alice (author), @bob (merged by)\n", body)
        self.assertNotIn("**cc:**", body)


if __name__ == "__main__":
    unittest.main()
