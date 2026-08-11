"""Tests for checkout_target.py."""

from __future__ import annotations

import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

# Allow `import checkout_target` when running this file directly.
_THIS_DIR = os.path.dirname(os.path.abspath(__file__))
if _THIS_DIR not in sys.path:
    sys.path.insert(0, _THIS_DIR)

import checkout_target  # noqa: E402


def _git(
    working_directory: Path,
    *arguments: str,
    check: bool = True,
) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        ["git", "-C", str(working_directory), *arguments],
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        check=check,
    )


class CheckoutTargetTests(unittest.TestCase):
    def setUp(self) -> None:
        self._temporary_directory = tempfile.TemporaryDirectory()
        self.root = Path(self._temporary_directory.name)
        self.remote = self.root / "aspire.dev.git"
        self.seed = self.root / "seed"

        subprocess.run(
            ["git", "init", "--bare", "--initial-branch=main", str(self.remote)],
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            check=True,
        )
        subprocess.run(
            ["git", "init", "--initial-branch=main", str(self.seed)],
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            check=True,
        )
        _git(self.seed, "config", "user.name", "Checkout Target Tests")
        _git(self.seed, "config", "user.email", "checkout-target@example.test")

        self._write(self.seed / ".agents" / "source.md", "main agent\n")
        self._write(self.seed / ".github" / "workflow.yml", "main workflow\n")
        self._write(self.seed / ".mcp.json", '{"source":"main"}\n')
        self._write(self.seed / "src" / "content" / "docs" / "page.md", "main docs\n")
        _git(self.seed, "add", "-A")
        _git(self.seed, "commit", "-m", "Create main")
        _git(self.seed, "remote", "add", "origin", self.remote.as_uri())
        _git(self.seed, "push", "-u", "origin", "main")

        _git(self.seed, "checkout", "-b", "release/13.5")
        self._write(self.seed / ".agents" / "source.md", "release agent\n")
        self._write(
            self.seed / "src" / "content" / "docs" / "page.md",
            "release docs\n",
        )
        _git(self.seed, "add", "-A")
        _git(self.seed, "commit", "-m", "Create release branch")
        _git(self.seed, "push", "-u", "origin", "release/13.5")

    def tearDown(self) -> None:
        self._temporary_directory.cleanup()

    @staticmethod
    def _write(path: Path, content: str) -> None:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content, encoding="utf-8")

    def _clone(self, name: str, *arguments: str) -> Path:
        workspace = self.root / name
        subprocess.run(
            ["git", "clone", *arguments, self.remote.as_uri(), str(workspace)],
            text=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            check=True,
        )
        return workspace

    def _restore_trusted_configuration(self, workspace: Path) -> None:
        shutil.rmtree(workspace / ".agents", ignore_errors=True)
        shutil.rmtree(workspace / ".github", ignore_errors=True)
        (workspace / ".mcp.json").unlink(missing_ok=True)
        self._write(workspace / ".agents" / "trusted.md", "trusted agent\n")
        self._write(
            workspace / ".github" / "agents" / "trusted.agent.md",
            "trusted inline agent\n",
        )
        self._write(
            workspace / ".github" / "skills" / "trusted" / "SKILL.md",
            "trusted inline skill\n",
        )
        self._write(workspace / "AGENTS.md", "trusted instructions\n")

    def _prepare(
        self,
        workspace: Path,
        effective_target: str = "release/13.5",
        docs_work_branch: str = "docs/pr-123-456-1",
        repository_url: str | None = None,
    ) -> None:
        checkout_target.prepare_workspace(
            workspace,
            effective_target,
            docs_work_branch,
            repository_url or self.remote.as_uri(),
            self._restore_trusted_configuration,
        )

    def test_fetches_missing_target_in_shallow_clone_and_keeps_runtime_clean(self) -> None:
        workspace = self._clone(
            "shallow",
            "--depth=1",
            "--single-branch",
            "--branch",
            "main",
        )
        self._write(workspace / ".pr-docs-check" / "target.json", "{}\n")
        self._write(workspace / "_repos" / "aspire" / "helper.py", "# helper\n")

        self._prepare(workspace)

        self.assertEqual(
            _git(workspace, "branch", "--show-current").stdout.strip(),
            "docs/pr-123-456-1",
        )
        self.assertEqual(
            _git(workspace, "rev-parse", "HEAD").stdout.strip(),
            _git(
                workspace,
                "rev-parse",
                "refs/remotes/gh-aw-target/release/13.5",
            ).stdout.strip(),
        )
        self.assertEqual(
            _git(workspace, "rev-parse", "--is-shallow-repository").stdout.strip(),
            "true",
        )
        self.assertFalse((workspace / ".mcp.json").exists())
        self.assertEqual(
            (workspace / ".agents" / "trusted.md").read_text(encoding="utf-8"),
            "trusted agent\n",
        )
        self.assertEqual(_git(workspace, "status", "--porcelain").stdout, "")

        self._write(
            workspace / "src" / "content" / "docs" / "page.md",
            "updated docs\n",
        )
        _git(workspace, "add", "-A")
        self.assertEqual(
            _git(workspace, "diff", "--cached", "--name-only").stdout.splitlines(),
            ["src/content/docs/page.md"],
        )

    def test_fetch_preserves_full_clone_history(self) -> None:
        workspace = self._clone(
            "full-single-branch",
            "--single-branch",
            "--branch",
            "main",
        )
        self.assertNotEqual(
            _git(
                workspace,
                "rev-parse",
                "--verify",
                "origin/release/13.5",
                check=False,
            ).returncode,
            0,
        )

        self._prepare(workspace)

        self.assertEqual(
            _git(workspace, "rev-parse", "--is-shallow-repository").stdout.strip(),
            "false",
        )
        self.assertEqual(
            _git(workspace, "rev-list", "--count", "HEAD").stdout.strip(),
            "2",
        )

    def test_ignores_colliding_origin_ref_from_source_repository(self) -> None:
        workspace = self._clone("existing")
        source_commit = _git(workspace, "rev-parse", "origin/main").stdout.strip()
        target_commit = _git(
            self.seed,
            "rev-parse",
            "release/13.5",
        ).stdout.strip()
        _git(
            workspace,
            "update-ref",
            "refs/remotes/origin/release/13.5",
            source_commit,
        )

        self._prepare(workspace)

        self.assertEqual(
            _git(workspace, "rev-parse", "HEAD").stdout.strip(),
            target_commit,
        )
        self.assertEqual(
            _git(
                workspace,
                "rev-parse",
                "refs/remotes/gh-aw-target/release/13.5",
            ).stdout.strip(),
            target_commit,
        )
        self.assertEqual(
            _git(
                workspace,
                "rev-parse",
                "refs/remotes/origin/release/13.5",
            ).stdout.strip(),
            target_commit,
        )

    def test_missing_target_reports_clear_error(self) -> None:
        workspace = self._clone(
            "missing",
            "--depth=1",
            "--single-branch",
            "--branch",
            "main",
        )

        with self.assertRaisesRegex(
            checkout_target.CheckoutError,
            "Resolved target branch 'release/99.9' could not be fetched",
        ):
            self._prepare(workspace, effective_target="release/99.9")

    def test_restore_uses_generated_gh_aw_script_contract(self) -> None:
        workspace = self.root / "restore-workspace"
        workspace.mkdir()
        runner_temp = self.root / "runner-temp"
        actions_dir = runner_temp / "gh-aw" / "actions"
        script_names = (
            "restore_base_github_folders.sh",
            "restore_inline_sub_agents.sh",
            "restore_inline_skills.sh",
        )
        for script_name in script_names:
            self._write(actions_dir / script_name, "# dummy\n")

        calls: list[tuple[list[str], dict]] = []

        def record_run(command: list[str], **kwargs: object) -> subprocess.CompletedProcess:
            calls.append((command, kwargs))
            return subprocess.CompletedProcess(command, 0, b"", b"")

        with mock.patch.dict(os.environ, {"RUNNER_TEMP": str(runner_temp)}):
            with mock.patch.object(
                checkout_target.subprocess,
                "run",
                side_effect=record_run,
            ):
                checkout_target._restore_trusted_configuration(workspace)

        self.assertEqual(
            [Path(command[1]).name for command, _ in calls],
            list(script_names),
        )
        self.assertTrue(all(kwargs["cwd"] == workspace for _, kwargs in calls))
        self.assertEqual(
            calls[0][1]["env"]["GH_AW_AGENT_FOLDERS"],
            ".agents .github",
        )
        self.assertEqual(calls[0][1]["env"]["GH_AW_AGENT_FILES"], "AGENTS.md")
        self.assertEqual(
            calls[1][1]["env"]["GH_AW_SUB_AGENT_DIR"],
            ".github/agents",
        )
        self.assertEqual(calls[1][1]["env"]["GH_AW_SUB_AGENT_EXT"], ".agent.md")
        self.assertEqual(
            calls[2][1]["env"]["GH_AW_SKILL_DIR"],
            ".github/skills",
        )
