"""Tests for compute_pr_context.py.

Run from the repo root:

    python3 -m unittest discover -s .github/workflows/pr-docs-check -v
"""

from __future__ import annotations

import os
import sys
import unittest

# Allow `import compute_pr_context` when running this file directly.
_THIS_DIR = os.path.dirname(os.path.abspath(__file__))
if _THIS_DIR not in sys.path:
    sys.path.insert(0, _THIS_DIR)

import compute_pr_context  # noqa: E402


def _pr(**overrides) -> dict:
    pr = {
        "number": 16939,
        "title": "Add aspire logs --search flag",
        "body": "",
        "user": {"login": "octocat", "type": "User"},
        "base": {"ref": "main"},
        "milestone": {"title": "13.4"},
        "labels": [{"name": "area-cli"}, {"name": "breaking-change"}],
        "assignees": [],
    }
    pr.update(overrides)
    return pr


def _file(filename: str, status: str = "modified", additions: int = 1, deletions: int = 0) -> dict:
    return {"filename": filename, "status": status, "additions": additions, "deletions": deletions}


class LinkedIssueTests(unittest.TestCase):
    def test_extracts_keywords_and_dedupes(self) -> None:
        body = "Fixes #1\nfixes: #1\nCloses #2\nResolves microsoft/aspire#3"
        self.assertEqual(compute_pr_context.extract_linked_issues(body), [1, 2, 3])

    def test_ignores_cross_repo_refs(self) -> None:
        body = "Fixes dotnet/runtime#999 and closes #5"
        self.assertEqual(compute_pr_context.extract_linked_issues(body), [5])

    def test_no_links_returns_empty(self) -> None:
        self.assertEqual(compute_pr_context.extract_linked_issues("see #7 for context"), [])

    def test_empty_body(self) -> None:
        self.assertEqual(compute_pr_context.extract_linked_issues(""), [])


class BuildContextTests(unittest.TestCase):
    def test_projects_expected_fields(self) -> None:
        pr = _pr(body="Adds a flag. Fixes #42.")
        files = [
            _file("src/Aspire.Cli/Commands/LogsCommand.cs", status="added", additions=10, deletions=0),
            _file("docs/list-of-diagnostics.md", additions=2, deletions=1),
        ]
        ctx = compute_pr_context.build_context(pr, files)

        self.assertEqual(ctx["number"], 16939)
        self.assertEqual(ctx["title"], "Add aspire logs --search flag")
        self.assertEqual(ctx["author"], {"login": "octocat", "type": "User"})
        self.assertEqual(ctx["base_ref"], "main")
        self.assertEqual(ctx["milestone"], "13.4")
        self.assertEqual(ctx["labels"], ["area-cli", "breaking-change"])
        self.assertEqual(ctx["linked_issues"], [42])
        self.assertEqual(ctx["changed_file_count"], 2)
        self.assertEqual(ctx["changed_files"][0]["filename"], "src/Aspire.Cli/Commands/LogsCommand.cs")
        self.assertEqual(ctx["changed_files"][0]["status"], "added")
        self.assertEqual(ctx["changed_files"][0]["additions"], 10)

    def test_excludes_patch_field(self) -> None:
        files = [dict(_file("a.cs"), patch="@@ -0,0 +1 @@\n+x")]
        ctx = compute_pr_context.build_context(_pr(), files)
        self.assertNotIn("patch", ctx["changed_files"][0])

    def test_null_milestone_and_empty_collections(self) -> None:
        pr = _pr(milestone=None, labels=[], assignees=None, body=None)
        ctx = compute_pr_context.build_context(pr, [])
        self.assertIsNone(ctx["milestone"])
        self.assertEqual(ctx["labels"], [])
        self.assertEqual(ctx["assignees"], [])
        self.assertEqual(ctx["linked_issues"], [])
        self.assertEqual(ctx["changed_file_count"], 0)
        self.assertEqual(ctx["body"], "")

    def test_bot_author_type_preserved(self) -> None:
        pr = _pr(user={"login": "Copilot", "type": "Bot"}, assignees=[{"login": "humandev"}])
        ctx = compute_pr_context.build_context(pr, [])
        self.assertEqual(ctx["author"], {"login": "Copilot", "type": "Bot"})
        self.assertEqual(ctx["assignees"], ["humandev"])


if __name__ == "__main__":
    unittest.main()
