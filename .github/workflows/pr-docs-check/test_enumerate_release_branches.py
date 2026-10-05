"""Tests for enumerate_release_branches.py."""

from __future__ import annotations

import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

# Allow `import enumerate_release_branches` when running this file directly.
_THIS_DIR = os.path.dirname(os.path.abspath(__file__))
if _THIS_DIR not in sys.path:
    sys.path.insert(0, _THIS_DIR)

import enumerate_release_branches  # noqa: E402


class EnumerateReleaseBranchesTests(unittest.TestCase):
    def test_ignores_colliding_origin_release_ref_absent_from_aspire_dev(
        self,
    ) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            workspace = Path(temporary_directory)
            subprocess.run(
                ["git", "init", "--initial-branch=main", str(workspace)],
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                check=True,
            )
            subprocess.run(
                [
                    "git",
                    "-C",
                    str(workspace),
                    "-c",
                    "user.name=Branch Enumeration Tests",
                    "-c",
                    "user.email=branch-enumeration@example.test",
                    "commit",
                    "--allow-empty",
                    "-m",
                    "Create source branch",
                ],
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                check=True,
            )
            subprocess.run(
                [
                    "git",
                    "-C",
                    str(workspace),
                    "update-ref",
                    "refs/remotes/origin/release/99.9",
                    "HEAD",
                ],
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                check=True,
            )

            run = mock.Mock(
                return_value=subprocess.CompletedProcess(
                    args=[],
                    returncode=0,
                    stdout="",
                    stderr="",
                )
            )
            original_directory = Path.cwd()
            try:
                os.chdir(workspace)
                branches = enumerate_release_branches.enumerate_release_branches(run)
            finally:
                os.chdir(original_directory)

        self.assertEqual(branches, [])
        command = run.call_args.args[0]
        self.assertIn("/repos/microsoft/aspire.dev/branches?per_page=100", command)

    def test_sorts_and_deduplicates_api_release_branches(self) -> None:
        run = mock.Mock(
            return_value=subprocess.CompletedProcess(
                args=[],
                returncode=0,
                stdout="release/13.5\nrelease/13.4\nrelease/13.5\n",
                stderr="",
            )
        )

        branches = enumerate_release_branches.enumerate_release_branches(run)

        self.assertEqual(branches, ["release/13.4", "release/13.5"])

    def test_reports_api_failure(self) -> None:
        run = mock.Mock(
            return_value=subprocess.CompletedProcess(
                args=[],
                returncode=1,
                stdout="",
                stderr="API unavailable",
            )
        )

        with self.assertRaisesRegex(
            enumerate_release_branches.BranchEnumerationError,
            "API unavailable",
        ):
            enumerate_release_branches.enumerate_release_branches(run)


if __name__ == "__main__":
    unittest.main()
