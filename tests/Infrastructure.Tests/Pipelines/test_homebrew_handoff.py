#!/usr/bin/env python3
# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.

"""Exercise the release pipeline's provenance handoff without service writes."""

import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[3]
SCRIPT = ROOT / "eng/homebrew/release_handoff.py"


class HandoffTests(unittest.TestCase):
    def setUp(self):
        spec = importlib.util.spec_from_file_location("homebrew_handoff", SCRIPT)
        self.module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.module)
        self.handoff = self.module.create(123, "a" * 40, 456, "b" * 40, "13.6.5")
        self.metadata = {
            "id": 123, "definition": {"id": 1600}, "project": {"name": "internal"},
            "repository": {"url": "https://dev.azure.com/dnceng/internal/_git/microsoft-aspire"},
            "sourceVersion": "a" * 40, "status": "completed", "result": "succeeded",
        }

    def test_exact_original_source_and_distinct_release_run_survive_cli_roundtrip(self):
        with tempfile.TemporaryDirectory() as work:
            handoff = Path(work) / "handoff.json"
            metadata = Path(work) / "metadata.json"
            metadata.write_text(json.dumps({"build": self.metadata}))
            created = subprocess.run([
                sys.executable, str(SCRIPT), "create", "--release-run-id", "123",
                "--release-commit", "a" * 40, "--source-build-id", "456",
                "--source-commit", "b" * 40, "--version", "13.6.5", "--output", str(handoff),
            ], capture_output=True, text=True, timeout=5)
            self.assertEqual(0, created.returncode, created.stderr)
            resolved = subprocess.run([
                sys.executable, str(SCRIPT), "resolve", "--release-run-id", "123",
                "--handoff", str(handoff), "--release-metadata", str(metadata),
            ], capture_output=True, text=True, timeout=5)
            self.assertEqual(0, resolved.returncode, resolved.stderr)
            self.assertIn("variable=HomebrewSourceBuildId]456", resolved.stdout)
            self.assertIn("variable=HomebrewSourceCommit]" + "b" * 40, resolved.stdout)
            self.assertIn("variable=HomebrewReleaseVersion]13.6.5", resolved.stdout)
            self.assertEqual(self.handoff, json.loads(handoff.read_text()))

    def test_rejects_wrong_release_run_commit_definition_result_and_repository(self):
        cases = (
            ("id", 999), ("sourceVersion", "c" * 40), ("definition", {"id": 1602}),
            ("project", {"name": "public"}), ("status", "inProgress"), ("result", "failed"),
            ("repository", {"url": "https://example.invalid/repository"}),
        )
        for key, value in cases:
            with self.subTest(key=key), self.assertRaises(ValueError):
                self.module.resolve(self.handoff, {**self.metadata, key: value}, 123)
        with self.assertRaises(ValueError):
            self.module.resolve(self.handoff, self.metadata, 999)

    def test_rejects_ambiguous_or_nonstable_source_identity_before_handoff(self):
        for build, commit, version in (
            (0, "b" * 40, "13.6.5"), (True, "b" * 40, "13.6.5"),
            (456, "HEAD", "13.6.5"), (456, "b" * 40, "auto"),
            (456, "b" * 40, "13.6.5-preview.1"), (456, "b" * 40, "13.6.5\nbad"),
        ):
            with self.subTest(build=build, commit=commit, version=version), self.assertRaises(ValueError):
                self.module.create(123, "a" * 40, build, commit, version)

    def test_malformed_handoff_cli_fails_without_emitting_pipeline_variables(self):
        for value in (None, {}, {**self.handoff, "schema": True}, {**self.handoff, "extra": 1}):
            with self.subTest(value=value), tempfile.TemporaryDirectory() as work:
                handoff = Path(work) / "handoff.json"
                metadata = Path(work) / "metadata.json"
                handoff.write_text(json.dumps(value))
                metadata.write_text(json.dumps({"build": self.metadata}))
                result = subprocess.run([
                    sys.executable, str(SCRIPT), "resolve", "--release-run-id", "123",
                    "--handoff", str(handoff), "--release-metadata", str(metadata),
                ], capture_output=True, text=True, timeout=5)
                self.assertEqual(1, result.returncode)
                self.assertEqual("", result.stdout)
                self.assertIn("Homebrew handoff failed:", result.stderr)


if __name__ == "__main__":
    unittest.main()
