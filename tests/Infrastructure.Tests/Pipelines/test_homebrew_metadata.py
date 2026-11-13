#!/usr/bin/env python3
# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.

"""Run the production metadata reader with service-boundary fixtures."""

import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

SCRIPT = Path(__file__).resolve().parents[3] / "eng/pipelines/scripts/Get-HomebrewBuildMetadata.ps1"


@unittest.skipUnless(shutil.which("pwsh"), "Requires PowerShell")
class MetadataTests(unittest.TestCase):
    def test_metadata_reader_preserves_identity_without_retaining_download_tickets(self):
        fixture = {
            "build": {
                "id": 456, "definition": {"id": 1602}, "project": {"name": "internal"},
                "repository": {"url": "https://dev.azure.com/dnceng/internal/_git/microsoft-aspire"},
                "sourceVersion": "b" * 40, "sourceBranch": "refs/heads/release/13.6",
                "status": "completed", "result": "succeeded", "tags": ["release-version - 13.6.5"],
            },
            "artifact": {
                "id": 789, "name": "homebrew-cask-stable",
                "resource": {"type": "Container", "data": "#/123/homebrew-cask-stable"},
            },
        }
        with tempfile.TemporaryDirectory() as work:
            root = Path(work)
            (root / "fixture.json").write_text(json.dumps(fixture))
            wrapper = root / "reader.ps1"
            wrapper.write_text("""
$ErrorActionPreference = 'Stop'
$fixture = Get-Content -Raw "$env:WORK/fixture.json" | ConvertFrom-Json
function Invoke-RestMethod {
    param($Uri, $Headers, $ConnectionTimeoutSeconds, $OperationTimeoutSeconds,
          $MaximumRetryCount, $RetryIntervalSec, $MaximumRedirection)
    if ($Headers.Authorization -cne 'Bearer fixture-token') { throw 'Incorrect read credential' }
    if ($Uri -ceq 'https://dev.azure.com/dnceng/internal/_apis/build/builds/456?api-version=7.1') {
        return $fixture.build
    }
    if ($Uri -ceq 'https://dev.azure.com/dnceng/internal/_apis/build/builds/456/artifacts?artifactName=homebrew-cask-stable&api-version=7.1') {
        $fixture.artifact.resource | Add-Member -NotePropertyName downloadUrl -NotePropertyValue 'sensitive-ticket'
        return $fixture.artifact
    }
    throw "Unexpected metadata request: $Uri"
}
& $env:READER -BuildId 456 -ArtifactName homebrew-cask-stable -OutputPath "$env:WORK/metadata.json"
""")
            process = subprocess.run(
                ["pwsh", "-NoLogo", "-NoProfile", "-File", str(wrapper)],
                env={**os.environ, "WORK": work, "READER": str(SCRIPT), "SYSTEM_ACCESSTOKEN": "fixture-token"},
                capture_output=True, text=True, timeout=15,
            )
            self.assertEqual(0, process.returncode, process.stderr)
            self.assertEqual(fixture, json.loads((root / "metadata.json").read_text()))
            self.assertEqual("Selected run 456, commit " + "b" * 40
                             + ", artifact 789 (homebrew-cask-stable).\n", process.stdout)

    def test_missing_read_credential_fails_without_emitting_metadata(self):
        with tempfile.TemporaryDirectory() as work:
            output = Path(work) / "metadata.json"
            process = subprocess.run([
                "pwsh", "-NoLogo", "-NoProfile", "-File", str(SCRIPT), "-BuildId", "456",
                "-ArtifactName", "homebrew-cask-stable", "-OutputPath", str(output),
            ], env={**os.environ, "SYSTEM_ACCESSTOKEN": ""}, capture_output=True, text=True, timeout=15)
            self.assertNotEqual(0, process.returncode)
            self.assertFalse(output.exists())
            self.assertIn("System.AccessToken is required", process.stderr)


if __name__ == "__main__":
    unittest.main()
