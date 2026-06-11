"""Tests for resolve_sme.py.

Run from the repo root:

    python3 -m unittest discover -s .github/workflows/pr-docs-check -v
"""

from __future__ import annotations

import os
import sys
import unittest

# Allow `import resolve_sme` when running this file directly.
_THIS_DIR = os.path.dirname(os.path.abspath(__file__))
if _THIS_DIR not in sys.path:
    sys.path.insert(0, _THIS_DIR)

import resolve_sme  # noqa: E402


def _review(login: str, state: str, submitted_at: str) -> dict:
    return {"user": {"login": login}, "state": state, "submitted_at": submitted_at}


class IsBotTests(unittest.TestCase):
    def test_known_bots(self) -> None:
        for login in ("Copilot", "copilot-swe-agent", "dependabot", "github-actions", "aspire-bot"):
            self.assertTrue(resolve_sme.is_bot(login), login)

    def test_bot_suffix(self) -> None:
        self.assertTrue(resolve_sme.is_bot("some-app[bot]"))
        self.assertTrue(resolve_sme.is_bot("copilot-swe-agent[bot]"))

    def test_humans_are_not_bots(self) -> None:
        for login in ("octocat", "ievangelist", "davidfowl"):
            self.assertFalse(resolve_sme.is_bot(login), login)

    def test_empty_is_treated_as_bot(self) -> None:
        self.assertTrue(resolve_sme.is_bot(""))
        self.assertTrue(resolve_sme.is_bot(None))


class IsCopilotAuthoredTests(unittest.TestCase):
    def test_copilot_bot(self) -> None:
        self.assertTrue(resolve_sme.is_copilot_authored({"login": "Copilot", "type": "Bot"}))
        self.assertTrue(resolve_sme.is_copilot_authored({"login": "copilot-swe-agent[bot]", "type": "Bot"}))

    def test_human_named_like_copilot_is_not(self) -> None:
        # Type must be Bot; a human is never the Copilot author even if named oddly.
        self.assertFalse(resolve_sme.is_copilot_authored({"login": "copilot-fan", "type": "User"}))

    def test_regular_bot_is_not_copilot(self) -> None:
        self.assertFalse(resolve_sme.is_copilot_authored({"login": "dependabot[bot]", "type": "Bot"}))


class CopilotAuthoredTests(unittest.TestCase):
    def _pr(self, assignees: list[str]) -> dict:
        return {"author": {"login": "Copilot", "type": "Bot"}, "assignees": assignees}

    def test_single_human_assignee_is_originator(self) -> None:
        result = resolve_sme.resolve_sme(self._pr(["Copilot", "humandev"]), [])
        self.assertEqual(result["sme_login"], "humandev")
        self.assertEqual(result["sme_source"], "copilot_originator")
        self.assertFalse(result["needs_codeowners_fallback"])

    def test_multiple_human_assignees_prefers_approver(self) -> None:
        pr = self._pr(["alice", "bob", "Copilot"])
        reviews = [_review("bob", "APPROVED", "2026-01-02T00:00:00Z")]
        result = resolve_sme.resolve_sme(pr, reviews)
        self.assertEqual(result["sme_login"], "bob")
        self.assertEqual(result["sme_source"], "copilot_originator_approved")

    def test_multiple_human_assignees_no_approver_picks_first(self) -> None:
        pr = self._pr(["alice", "bob"])
        result = resolve_sme.resolve_sme(pr, [])
        self.assertEqual(result["sme_login"], "alice")
        self.assertEqual(result["sme_source"], "copilot_originator")

    def test_no_human_assignee_falls_through_to_reviews(self) -> None:
        pr = self._pr(["Copilot", "dependabot[bot]"])
        reviews = [_review("carol", "APPROVED", "2026-01-03T00:00:00Z")]
        result = resolve_sme.resolve_sme(pr, reviews)
        self.assertEqual(result["sme_login"], "carol")
        self.assertEqual(result["sme_source"], "approved_reviewer")


class HumanAuthoredTests(unittest.TestCase):
    def _pr(self, assignees: list[str] | None = None) -> dict:
        return {"author": {"login": "author", "type": "User"}, "assignees": assignees or []}

    def test_prefers_most_recent_approver(self) -> None:
        reviews = [
            _review("alice", "APPROVED", "2026-01-01T00:00:00Z"),
            _review("bob", "APPROVED", "2026-01-05T00:00:00Z"),
        ]
        result = resolve_sme.resolve_sme(self._pr(), reviews)
        self.assertEqual(result["sme_login"], "bob")
        self.assertEqual(result["sme_source"], "approved_reviewer")

    def test_collapses_to_latest_review_per_reviewer(self) -> None:
        # alice commented, then approved later — her latest state is APPROVED.
        reviews = [
            _review("alice", "COMMENTED", "2026-01-01T00:00:00Z"),
            _review("alice", "APPROVED", "2026-01-04T00:00:00Z"),
        ]
        result = resolve_sme.resolve_sme(self._pr(), reviews)
        self.assertEqual(result["sme_login"], "alice")
        self.assertEqual(result["sme_source"], "approved_reviewer")

    def test_excludes_author_and_bots(self) -> None:
        reviews = [
            _review("author", "APPROVED", "2026-01-09T00:00:00Z"),
            _review("github-actions", "APPROVED", "2026-01-09T00:00:00Z"),
            _review("alice", "APPROVED", "2026-01-02T00:00:00Z"),
        ]
        result = resolve_sme.resolve_sme(self._pr(), reviews)
        self.assertEqual(result["sme_login"], "alice")

    def test_fallback_to_changes_requested(self) -> None:
        reviews = [
            _review("alice", "COMMENTED", "2026-01-01T00:00:00Z"),
            _review("bob", "CHANGES_REQUESTED", "2026-01-03T00:00:00Z"),
        ]
        result = resolve_sme.resolve_sme(self._pr(), reviews)
        self.assertEqual(result["sme_login"], "bob")
        self.assertEqual(result["sme_source"], "substantive_reviewer")

    def test_no_reviews_requests_codeowners_fallback(self) -> None:
        result = resolve_sme.resolve_sme(self._pr(), [])
        self.assertEqual(result["sme_login"], "")
        self.assertEqual(result["sme_source"], "none")
        self.assertTrue(result["needs_codeowners_fallback"])

    def test_only_commented_reviews_yields_empty_without_codeowners(self) -> None:
        # Per Step 2b, CODEOWNERS is only for "no reviews at all"; a lone
        # COMMENTED reviewer is not strong enough but also not zero signal.
        reviews = [_review("alice", "COMMENTED", "2026-01-01T00:00:00Z")]
        result = resolve_sme.resolve_sme(self._pr(), reviews)
        self.assertEqual(result["sme_login"], "")
        self.assertEqual(result["sme_source"], "none")
        self.assertFalse(result["needs_codeowners_fallback"])

    def test_candidates_listed_and_sorted(self) -> None:
        reviews = [
            _review("zoe", "APPROVED", "2026-01-05T00:00:00Z"),
            _review("alice", "COMMENTED", "2026-01-01T00:00:00Z"),
            _review("author", "APPROVED", "2026-01-06T00:00:00Z"),
        ]
        result = resolve_sme.resolve_sme(self._pr(), reviews)
        logins = [c["login"] for c in result["candidates"]]
        self.assertEqual(logins, ["alice", "zoe"])  # author excluded, sorted


if __name__ == "__main__":
    unittest.main()
