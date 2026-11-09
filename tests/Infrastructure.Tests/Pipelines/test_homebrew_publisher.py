#!/usr/bin/env python3
# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.

"""Behavioral tests of the production publisher; no GitHub mutations."""

import base64
import contextlib
import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from unittest.mock import patch
from concurrent.futures import ThreadPoolExecutor
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

ROOT = Path(__file__).resolve().parents[3]
SCRIPT = ROOT / "eng/homebrew/publish-cask.py"
VERSION = "13.6.5"
BRANCH = "homebrew/aspire-13.6.5"
PREFIX = "/repos/microsoft/homebrew-aspire"
CONTENT = '/contents/Casks/a/aspire.rb'


def cask(version, arm="a" * 64, intel="b" * 64):
    return (
        f'cask "aspire" do\n  version "{version}"\n'
        f'  sha256 arm: "{arm}",\n         intel: "{intel}"\nend\n'
    ).encode()


class FakeGitHub:
    """Atomic GitHub ref/content operations, not a publisher implementation."""

    def __init__(self):
        self.current = cask("13.6.4")
        self.base = "1" * 40
        self.branch = None
        self.pulls = []
        self.calls = []
        self.failures = {}
        self.after_write = None
        self.lock = threading.RLock()
        self.module = None
        self.deadline = None
        self.truncate = None
        self.drip = False
        self.history = {}

    def begin_phase(self, deadline=None):
        self.deadline = deadline

    def request(self, method, path, body=None):
        with self.lock:
            self.calls.append((method, path, body))
            if method in self.failures:
                raise self.failures.pop(method)
            relative = path.removeprefix(PREFIX)
            if method == "GET":
                if relative == "":
                    return {"default_branch": "main"}
                if relative.startswith("/git/ref/heads/main"):
                    return {"object": {"sha": self.base}}
                if relative.startswith("/git/ref/heads/homebrew/"):
                    if self.branch is None:
                        raise self.module.ApiError(404, "Not found")
                    return {"object": {"sha": "2" * 40}}
                if relative.startswith(CONTENT):
                    content = self.branch if "ref=homebrew" in relative else self.current
                    for revision, historical in self.history.items():
                        if f"ref={revision}" in relative:
                            content = historical
                    return {"content": base64.b64encode(content).decode(), "encoding": "base64", "sha": "3" * 40}
                if relative.startswith("/pulls"):
                    return self.pulls
            elif method == "POST" and relative == "/git/refs":
                if self.branch is not None:
                    raise self.module.ApiError(422, "Reference already exists")
                self.branch = self.current
                result = {"object": {"sha": self.base}}
            elif method == "PUT" and relative == CONTENT:
                if body["sha"] != "3" * 40:
                    raise self.module.ApiError(409, "SHA mismatch")
                self.branch = base64.b64decode(body["content"])
                result = {"commit": {"sha": "4" * 40}}
            elif method == "POST" and relative == "/pulls":
                result = {
                    "state": "open", "merged_at": None, "html_url": "https://github.com/microsoft/homebrew-aspire/pull/99",
                    "head": {"ref": body["head"], "repo": {"full_name": "microsoft/homebrew-aspire"}},
                    "base": {"ref": "main"},
                }
                self.pulls.append(result)
            else:
                raise AssertionError(f"Unexpected request: {method} {path}")
            if self.after_write:
                self.after_write(method, relative)
            return result

    @property
    def writes(self):
        return [(method, path) for method, path, _ in self.calls if method != "GET"]


@contextlib.contextmanager
def server_for(fake):
    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *_):
            pass

        def handle_request(self):
            body = None
            if self.command != "GET":
                body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
            try:
                if not self.headers.get("User-Agent"):
                    raise fake.module.ApiError(403, "User-Agent required")
                value = fake.request(self.command, self.path, body)
                status = 200
            except fake.module.ApiError as error:
                status, value = error.status, {"message": str(error)}
            payload = json.dumps(value).encode()
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(payload)))
            if status == 429:
                self.send_header("Retry-After", "1")
            self.end_headers()
            if fake.truncate == (self.command, self.path):
                fake.truncate = None
                self.wfile.write(payload[:1])
                self.wfile.flush()
                self.close_connection = True
            elif fake.drip:
                try:
                    for byte in payload:
                        self.wfile.write(bytes([byte]))
                        self.wfile.flush()
                        time.sleep(0.02)
                except (BrokenPipeError, ConnectionResetError):
                    pass
            else:
                self.wfile.write(payload)

        do_GET = handle_request
        do_POST = handle_request
        do_PUT = handle_request

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        yield f"http://127.0.0.1:{server.server_port}"
    finally:
        server.shutdown()
        server.server_close()
        thread.join()


class PublisherTests(unittest.TestCase):
    def setUp(self):
        self.assertTrue(SCRIPT.is_file(), "Production Homebrew publisher is missing")
        spec = importlib.util.spec_from_file_location("homebrew_publisher", SCRIPT)
        self.module = importlib.util.module_from_spec(spec)
        sys.modules[spec.name] = self.module
        spec.loader.exec_module(self.module)
        self.fake = FakeGitHub()
        self.fake.module = self.module
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.candidate = Path(self.temp.name) / "aspire.rb"
        self.candidate.write_bytes(cask(VERSION))
        self.validation_calls = []
        self.token_calls = []

    def run_publisher(self, publish=False, validate=None):
        def validation(path, version):
            self.validation_calls.append(path.read_bytes())
            if validate:
                validate(path, version)

        def writer():
            self.token_calls.append(True)
            return self.fake

        with contextlib.redirect_stdout(io.StringIO()) as output:
            result = self.module.publish_cask(
                self.candidate, VERSION, self.fake, validation, publish, writer
            )
        return result, output.getvalue()

    def test_dry_run_validates_without_any_writes_or_write_credentials(self):
        result, _ = self.run_publisher()
        self.assertEqual("planned", result["outcome"])
        self.assertEqual([cask(VERSION)], self.validation_calls)
        self.assertEqual([], self.fake.writes)
        self.assertEqual([], self.token_calls)

    def source_metadata(self):
        return {
            "build": {
                "id": 3115399, "definition": {"id": 1602},
                "project": {"name": "internal"},
                "repository": {"url": "https://dev.azure.com/dnceng/internal/_git/microsoft-aspire"},
                "sourceVersion": "c" * 40, "sourceBranch": "refs/heads/release/13.6",
                "status": "completed", "result": "succeeded",
                "tags": [f"release-version - {VERSION}"],
            },
            "artifact": {
                "id": 77459436, "name": "homebrew-cask-stable",
                "resource": {"type": "Container", "data": "#/75481081/homebrew-cask-stable"},
            },
        }

    def test_source_artifact_provenance_and_publication_preserve_downloaded_bytes(self):
        downloaded = self.candidate.read_bytes()
        provenance = self.module.verify_source_artifact(
            downloaded, self.source_metadata(), 3115399, "c" * 40, VERSION
        )
        self.assertEqual(3115399, provenance["build_id"])
        self.assertEqual(77459436, provenance["artifact_id"])
        self.assertEqual(VERSION, provenance["version"])
        self.run_publisher(True)
        self.assertEqual(downloaded, self.fake.branch)
        self.assertEqual(downloaded, self.candidate.read_bytes())

    def test_source_artifact_rejects_wrong_run_commit_version_and_artifact(self):
        cases = (
            ("build", "id", 3115398),
            ("build", "sourceVersion", "d" * 40),
            ("build", "definition", {"id": 1600}),
            ("build", "project", {"name": "public"}),
            ("build", "repository", {"url": "https://dev.azure.com/dnceng/internal/_git/another"}),
            ("build", "result", "failed"),
            ("build", "status", "inProgress"),
            ("build", "tags", ["release-version - 13.6.4"]),
            ("build", "tags", [f"release-version - {VERSION}", "release-version - 13.6.4"]),
            ("artifact", "name", "homebrew-cask-prerelease"),
            ("artifact", "resource", {"type": "PipelineArtifact", "data": "anything"}),
            ("artifact", "resource", {"type": "Container", "data": "#/75481081/other"}),
        )
        for section, key, value in cases:
            with self.subTest(section=section, key=key, value=value):
                metadata = self.source_metadata()
                metadata[section][key] = value
                with self.assertRaises(self.module.PublicationError):
                    self.module.verify_source_artifact(
                        self.candidate.read_bytes(), metadata, 3115399, "c" * 40, VERSION
                    )
        self.assertEqual([], self.fake.writes)

    def test_source_artifact_cli_checks_identity_without_network_or_writes(self):
        metadata = Path(self.temp.name) / "source.json"
        metadata.write_text(json.dumps(self.source_metadata()))
        output = Path(self.temp.name) / "output"
        process = subprocess.run([
            sys.executable, str(SCRIPT), "--candidate", str(self.candidate),
            "--version", "auto", "--output-dir", str(output), "--artifact-check-only",
            "--source-build-metadata", str(metadata), "--source-build-id", "3115399",
            "--source-build-commit", "c" * 40,
        ], capture_output=True, text=True, timeout=10)
        self.assertEqual(0, process.returncode, process.stderr)
        result = json.loads((output / "result.json").read_text())
        self.assertEqual("artifact_verified", result["outcome"])
        self.assertEqual(3115399, result["source"]["build_id"])
        self.assertEqual(VERSION, result["version"])
        self.assertFalse(result["publish"])
        self.assertEqual(cask(VERSION), self.candidate.read_bytes())
        self.assertEqual(cask(VERSION), (output / "source-candidate.rb").read_bytes())

    def test_source_artifact_cli_rejects_malformed_provenance_with_diagnostics(self):
        malformed = [
            {}, {"build": None, "artifact": {}},
        ]
        for tags in (None, f"release-version - {VERSION}", {f"release-version - {VERSION}": True}, [42]):
            metadata = self.source_metadata()
            metadata["build"]["tags"] = tags
            malformed.append(metadata)
        metadata = self.source_metadata()
        metadata["artifact"]["id"] = True
        malformed.append(metadata)
        for index, value in enumerate(malformed):
            with self.subTest(metadata=value):
                metadata = Path(self.temp.name) / "source.json"
                metadata.write_text(json.dumps(value))
                output = Path(self.temp.name) / f"output-{index}"
                process = subprocess.run([
                    sys.executable, str(SCRIPT), "--candidate", str(self.candidate),
                    "--version", "auto", "--output-dir", str(output), "--artifact-check-only",
                    "--source-build-metadata", str(metadata), "--source-build-id", "3115399",
                    "--source-build-commit", "c" * 40,
                ], capture_output=True, text=True, timeout=10)
                self.assertEqual(1, process.returncode)
                result = json.loads((output / "result.json").read_text())
                self.assertEqual("failed", result["outcome"])
                self.assertTrue(result["error"])
                self.assertIn("Homebrew publication failed:", process.stderr)
                self.assertFalse((output / "source-candidate.rb").exists())

    def test_source_artifact_public_release_commit_must_match_source_run(self):
        class ReleaseClient:
            def begin_phase(self):
                pass

            def request(self, method, path):
                if "/releases/" in path:
                    return {"tag_name": f"v{VERSION}", "draft": False, "prerelease": False}
                return {"object": {"type": "commit", "sha": "d" * 40}}

        with patch.object(self.module.platform, "system", return_value="Darwin"), \
                patch.object(self.module, "run", return_value=self.module.VALIDATOR_REVISION):
            with self.assertRaisesRegex(self.module.PublicationError, "selected source build commit"):
                self.module.validate_release(
                    self.candidate, VERSION, ReleaseClient(), Path(self.temp.name),
                    Path(self.temp.name), "c" * 40,
                )

    def test_publication_writes_once_and_rerun_skips(self):
        first, _ = self.run_publisher(True)
        second, warning = self.run_publisher(True)
        self.assertEqual("submitted", first["outcome"])
        self.assertEqual("skipped", second["outcome"])
        self.assertEqual(
            [("POST", PREFIX + "/git/refs"), ("PUT", PREFIX + CONTENT), ("POST", PREFIX + "/pulls")],
            self.fake.writes,
        )
        self.assertEqual(1, len(self.token_calls))
        self.assertIn("##vso[task.logissue type=warning;", warning)
        self.assertIn("/pull/99", warning)

    def test_already_current_and_inflight_skip_before_validation(self):
        for state in ("current", "inflight"):
            with self.subTest(state=state):
                self.fake.current = cask(VERSION) if state == "current" else cask("13.6.4")
                self.fake.branch = self.fake.current if state == "inflight" else None
                result, warning = self.run_publisher(True)
                self.assertEqual("skipped", result["outcome"])
                self.assertIn("type=warning", warning)
                self.assertEqual([], self.validation_calls)
                self.assertEqual([], self.token_calls)
                self.assertEqual([], self.fake.writes)

    def test_conflicting_current_hash_and_rollback_fail(self):
        for current in (cask(VERSION, "c" * 64), cask("13.6.6")):
            self.fake.current = current
            with self.assertRaises(self.module.PublicationError):
                self.run_publisher(True)
        self.assertEqual([], self.fake.writes)

    def test_closed_unmerged_pr_needs_operator_action(self):
        self.run_publisher(True)
        self.fake.pulls[0]["state"] = "closed"
        with self.assertRaisesRegex(self.module.PublicationError, "closed"):
            self.run_publisher(True)
        self.assertEqual(3, len(self.fake.writes))

    def test_merged_version_skips_after_branch_deletion_and_later_release(self):
        self.run_publisher(True)
        pull = self.fake.pulls[0]
        pull.update(state="closed", merged_at="2026-01-01T00:00:00Z", merge_commit_sha="6" * 40)
        self.fake.history["6" * 40] = self.fake.branch
        self.fake.branch = None
        self.fake.current = cask("13.6.6")
        result, warning = self.run_publisher(True)
        self.assertEqual("skipped", result["outcome"])
        self.assertIn("/pull/99", warning)
        self.assertEqual(3, len(self.fake.writes))

    def test_validation_failure_never_requests_writer(self):
        def fail(*_):
            raise self.module.PublicationError("checksum mismatch")
        with self.assertRaisesRegex(self.module.PublicationError, "checksum"):
            self.run_publisher(True, fail)
        self.assertEqual([], self.fake.writes)
        self.assertEqual([], self.token_calls)

    def test_target_advancing_during_validation_prevents_stale_submission(self):
        def advance(*_):
            self.fake.base = "5" * 40
            self.fake.current = cask("13.6.6")
        with self.assertRaisesRegex(self.module.PublicationError, "rollback"):
            self.run_publisher(True, advance)
        self.assertEqual([], self.fake.writes)

    def test_target_base_change_during_validation_requires_replan(self):
        def advance(*_):
            self.fake.base = "5" * 40
        with self.assertRaisesRegex(self.module.PublicationError, "changed"):
            self.run_publisher(True, advance)
        self.assertEqual([], self.fake.writes)

    def test_validated_snapshot_not_mutable_input_is_submitted(self):
        def change_input(*_):
            self.candidate.write_bytes(cask("99.0.0"))
        self.run_publisher(True, change_input)
        self.assertEqual(cask(VERSION), self.fake.branch)

    def test_lost_write_responses_reconcile_without_repeating_mutation(self):
        for operation in ("branch", "contents", "pull"):
            with self.subTest(operation=operation):
                self.fake = FakeGitHub()
                self.fake.module = self.module
                target = {"branch": "/git/refs", "contents": CONTENT, "pull": "/pulls"}[operation]
                def lose(method, path):
                    if path == target:
                        self.fake.after_write = None
                        raise self.module.ApiError(503, "response lost")
                self.fake.after_write = lose
                result, _ = self.run_publisher(True)
                self.assertEqual("skipped" if operation == "branch" else "submitted", result["outcome"])
                self.assertEqual(1, sum(path == PREFIX + target for _, path in self.fake.writes))

    def test_inconclusive_write_reconciliation_stops(self):
        def lose(method, path):
            if path == "/pulls":
                self.fake.pulls.clear()
                raise self.module.ApiError(503, "response lost")
        self.fake.after_write = lose
        with self.assertRaisesRegex(self.module.PublicationError, "uncertain"):
            self.run_publisher(True)
        self.assertEqual(1, sum(path == PREFIX + "/pulls" for _, path in self.fake.writes))

    def test_competing_publishers_create_at_most_one_pr(self):
        barrier = threading.Barrier(2)
        def validate(*_):
            barrier.wait(timeout=10)
        def execute():
            return self.module.publish_cask(
                self.candidate, VERSION, self.fake, validate, True, lambda: self.fake
            )["outcome"]
        with ThreadPoolExecutor(max_workers=2) as pool:
            results = list(pool.map(lambda _: execute(), range(2)))
        self.assertEqual(["skipped", "submitted"], sorted(results))
        self.assertEqual(1, len(self.fake.pulls))

    def test_invalid_candidate_fails_before_external_operations(self):
        for content in (cask(VERSION, "0" * 64), cask("13.6.5-preview.1"), b"invalid"):
            self.candidate.write_bytes(content)
            with self.assertRaises(self.module.PublicationError):
                self.run_publisher(True)
        self.assertEqual([], self.fake.calls)

    def test_real_cli_dry_run_ignores_ambient_write_token_and_emits_warning(self):
        self.fake.current = cask(VERSION)
        with server_for(self.fake) as url:
            result = subprocess.run(
                [sys.executable, str(SCRIPT), "--candidate", str(self.candidate),
                 "--version", VERSION, "--test-server", url, "--output-dir", self.temp.name],
                env={**os.environ, "HOMEBREW_TAP_WRITE_TOKEN": "must-not-be-used"},
                text=True, capture_output=True, timeout=15,
            )
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("type=warning", result.stdout)
        self.assertEqual("skipped", json.loads((Path(self.temp.name) / "result.json").read_text())["outcome"])
        self.assertEqual([], self.fake.writes)

    def test_http_requests_satisfy_github_user_agent_requirement(self):
        with server_for(self.fake) as url:
            client = self.module.GitHubClient(api_url=url)
            self.assertEqual({"default_branch": "main"}, client.request("GET", PREFIX))

    def test_http_retries_only_transient_reads_and_honors_retry_after(self):
        for status in (503, 429, 401, 403, 404):
            with self.subTest(status=status):
                self.fake.calls.clear()
                self.fake.failures["GET"] = self.module.ApiError(status, "fixture")
                delays = []
                with server_for(self.fake) as url:
                    client = self.module.GitHubClient(api_url=url, sleep=delays.append)
                    if status in (503, 429):
                        self.assertEqual({"default_branch": "main"}, client.request("GET", PREFIX))
                        self.assertEqual(2, len(self.fake.calls))
                        self.assertEqual([1], delays)
                    else:
                        with self.assertRaises(self.module.ApiError):
                            client.request("GET", PREFIX)
                        self.assertEqual(1, len(self.fake.calls))
                        self.assertEqual([], delays)

    def test_http_retry_exhaustion_and_shared_deadline_are_bounded(self):
        self.fake.failures["GET"] = self.module.ApiError(503, "fixture")
        def unavailable(method, path, body=None):
            self.fake.calls.append((method, path, body))
            raise self.module.ApiError(503, "fixture")
        self.fake.request = unavailable
        with server_for(self.fake) as url:
            clock = [0]
            def sleep(delay):
                clock[0] += delay
            client = self.module.GitHubClient(api_url=url, sleep=sleep, clock=lambda: clock[0])
            with self.assertRaises(self.module.ApiError):
                client.request("GET", PREFIX)
            self.assertEqual(3, len(self.fake.calls))
            self.assertEqual(3, clock[0])
            clock[0] = 121
            with self.assertRaisesRegex(self.module.PublicationError, "deadline"):
                client.request("GET", PREFIX)
            self.assertEqual(3, len(self.fake.calls))

    def test_http_writes_are_never_blindly_retried(self):
        self.fake.failures["POST"] = self.module.ApiError(503, "fixture")
        with server_for(self.fake) as url:
            client = self.module.GitHubClient(api_url=url, sleep=lambda _: self.fail("Write retried"))
            with self.assertRaises(self.module.ApiError):
                client.request("POST", PREFIX + "/git/refs", {"ref": BRANCH, "sha": "1" * 40})
        self.assertEqual(1, len(self.fake.calls))

    def test_real_truncated_mutation_responses_reconcile_once(self):
        for target in (CONTENT, "/pulls"):
            with self.subTest(target=target):
                fake = FakeGitHub()
                fake.module = self.module
                fake.truncate = ("PUT" if target == CONTENT else "POST", PREFIX + target)
                with server_for(fake) as url:
                    client = self.module.GitHubClient(api_url=url)
                    with contextlib.redirect_stdout(io.StringIO()):
                        result = self.module.publish_cask(
                            self.candidate, VERSION, client, lambda *_: None, True, lambda: client
                        )
                self.assertEqual("submitted", result["outcome"])
                self.assertEqual(1, sum(path == PREFIX + target for _, path in fake.writes))

    def test_continuously_streaming_response_cannot_outlive_deadline(self):
        self.fake.drip = True
        with server_for(self.fake) as url:
            client = self.module.GitHubClient(api_url=url)
            client.begin_phase(time.monotonic() + 0.1)
            started = time.monotonic()
            with self.assertRaisesRegex(self.module.PublicationError, "deadline"):
                client.request("GET", PREFIX)
            self.assertLess(time.monotonic() - started, 2)
        self.assertEqual(1, len(self.fake.calls))

    def test_validation_timeout_retains_partial_diagnostics_and_fails(self):
        log = Path(self.temp.name) / "validation.log"
        timeout = subprocess.TimeoutExpired(["bash", "validator"], 1800, output=b"last validator diagnostic\n")
        with patch.object(self.module.subprocess, "run", side_effect=timeout), \
                contextlib.redirect_stdout(io.StringIO()) as console:
            with self.assertRaisesRegex(self.module.PublicationError, "timed out after 1800s"):
                self.module.run(["bash", "validator"], log)
        self.assertEqual("last validator diagnostic\n", log.read_text())
        self.assertEqual(log.read_text(), console.getvalue())

    def test_validation_adapter_rejects_non_macos_and_wrong_revision_before_download(self):
        output = Path(self.temp.name)
        with patch.object(self.module.platform, "system", return_value="Linux"):
            with self.assertRaisesRegex(self.module.PublicationError, "macOS"):
                self.module.validate_release(self.candidate, VERSION, self.fake, output, output)
        with patch.object(self.module.platform, "system", return_value="Darwin"), \
                patch.object(self.module, "run", return_value="7" * 40):
            with self.assertRaisesRegex(self.module.PublicationError, "pinned"):
                self.module.validate_release(self.candidate, VERSION, self.fake, output, output)
        self.assertEqual([], self.fake.calls)

    def test_validation_runs_from_tap_not_checkout_sdk_context(self):
        output = Path(self.temp.name)
        tap = output / "tap"
        tap.mkdir()
        version = VERSION

        class ReleaseClient:
            def begin_phase(self):
                pass

            def request(self, method, path):
                if "/releases/" in path:
                    return {
                        "tag_name": f"v{version}", "draft": False, "prerelease": False,
                        "assets": [
                            {
                                "name": f"aspire-cli-osx-{arch}-{version}.tar.gz",
                                "browser_download_url": (
                                    f"https://github.com/microsoft/aspire/releases/download/v{version}/"
                                    f"aspire-cli-osx-{arch}-{version}.tar.gz"
                                ),
                            } for arch in ("arm64", "x64")
                        ],
                    }
                if "/git/ref/" in path:
                    return {"object": {"type": "commit", "sha": "c" * 40}}
                return {"content": base64.b64encode(b"template").decode()}

        actual_run = self.module.run

        def fixture_run(command, log=None, cwd=None, env=None):
            if command[0] == "curl":
                Path(command[-1]).write_bytes(b"archive")
            if command[0] == "bash":
                return actual_run(
                    [sys.executable, "-c", "from pathlib import Path; print(Path.cwd())"],
                    log, cwd=cwd,
                )
            return self.module.VALIDATOR_REVISION if "rev-parse" in command else ""

        with patch.object(self.module.platform, "system", return_value="Darwin"), \
                patch.object(self.module, "run", side_effect=fixture_run), \
                patch.object(self.module.shutil, "which", return_value=sys.executable), \
                contextlib.redirect_stdout(io.StringIO()):
            self.module.validate_release(self.candidate, VERSION, ReleaseClient(), tap, output)
        self.assertEqual(str(tap.resolve()), (output / "validation.log").read_text().strip())
        self.assertEqual(cask(VERSION), self.candidate.read_bytes())

    @unittest.skipIf(os.name == "nt", "The macOS validator's PATH wrapper requires a POSIX shell")
    def test_sdk_failure_diagnostics_survive_validator_home_cleanup(self):
        output = Path(self.temp.name)
        with tempfile.TemporaryDirectory() as work:
            work = Path(work)
            home = work / "home"
            home.mkdir()
            fake_sdk = work / "sdk"
            fake_sdk.write_text(
                f"#!{sys.executable}\n"
                "import os, sys\n"
                "print('restore progress', flush=True)\n"
                "print('error NU1301: failed source https://user:password@feed.test/index?sig=signed-secret', file=sys.stderr)\n"
                "print('token: ' + os.environ['TEST_SECRET_TOKEN'], file=sys.stderr)\n"
                "sys.exit(17)\n"
            )
            fake_sdk.chmod(0o700)
            with patch.object(self.module.shutil, "which", return_value=str(fake_sdk)):
                env = self.module.dotnet_diagnostic_environment(work, output)
            env.update(HOME=str(home), TEST_SECRET_TOKEN="fixture-credential")
            # The validator exits with its original status after deleting HOME.
            result = subprocess.run(
                [sys.executable, "-c",
                 "import shutil, subprocess, sys; "
                 "status = subprocess.run(['dotnet', 'restore']).returncode; "
                 f"shutil.rmtree({str(home)!r}); sys.exit(status)"],
                env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, timeout=15,
            )
            self.assertEqual(17, result.returncode)
            self.assertFalse(home.exists())
        logs = list((output / "dotnet-failures").glob("*.log"))
        self.assertEqual(1, len(logs))
        self.assertEqual(
            "dotnet restore failed (exit 17).\n"
            "restore progress\n"
            "error NU1301: failed source https://[REDACTED]@feed.test/index?sig=[REDACTED]\n"
            "token: [REDACTED]\n",
            logs[0].read_text(),
        )
        self.assertIn("SDK failure diagnostics:", result.stdout)

    @unittest.skipIf(os.name == "nt", "The macOS validator's PATH wrapper requires a POSIX shell")
    def test_failed_restore_assets_summary_survives_home_cleanup_without_credentials(self):
        output = Path(self.temp.name)
        with tempfile.TemporaryDirectory() as work:
            work = Path(work)
            home = work / "home"
            project = home / "smoke" / "AppHost"
            project.mkdir(parents=True)
            for name in ("AppHost", "ApiService"):
                assets = home / "smoke" / name / "obj" / "project.assets.json"
                assets.parent.mkdir(parents=True)
                assets.write_text(json.dumps({
                    "version": 3,
                    "project": {"restore": {"sources": {
                        "https://user:password@feed.test/v3/index.json?custom=private#secret": {},
                        "https://api.nuget.org/v3/index.json": {},
                        "/private/package/cache": {},
                    }}},
                    "logs": [{
                        "code": "NU1301", "level": "Error",
                        "message": "Source https://user:password@feed.test/v3/index.json?custom=private "
                                   "failed: fixture-credential",
                    }],
                    "libraries": {"must-not-be-retained": {}},
                }))
            sdk = work / "sdk"
            invocations = work / "invocations"
            sdk.write_text(
                f"#!{sys.executable}\n"
                "import sys\n"
                "from pathlib import Path\n"
                f"with Path({str(invocations)!r}).open('a') as log: log.write(repr(sys.argv[1:]) + '\\n')\n"
                "print('MSB4181: RestoreTask returned false')\n"
                "sys.exit(17)\n"
            )
            sdk.chmod(0o700)
            with patch.object(self.module.shutil, "which", return_value=str(sdk)):
                env = self.module.dotnet_diagnostic_environment(work, output)
            env.update(HOME=str(home), TEST_SECRET_TOKEN="fixture-credential")
            result = subprocess.run(
                [sys.executable, "-c",
                 "import shutil, subprocess, sys; "
                 "status = subprocess.run(['dotnet', 'restore', 'AppHost.csproj']).returncode; "
                 f"shutil.rmtree({str(home)!r}); sys.exit(status)"],
                cwd=project, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                text=True, timeout=15,
            )
            self.assertEqual(17, result.returncode)
            self.assertFalse(home.exists())
            self.assertEqual("['restore', 'AppHost.csproj']\n", invocations.read_text())
        summaries = list((output / "dotnet-failures").glob("*.json"))
        self.assertEqual(1, len(summaries))
        self.assertEqual({
            "projects": [{
                "project": name,
                "sources": [
                    "[non-HTTP source omitted]",
                    "https://api.nuget.org/v3/index.json",
                    "https://feed.test/v3/index.json",
                ],
                "logs": [{
                    "code": "NU1301", "level": "Error",
                    "message": "Source https://[REDACTED]@feed.test/v3/index.json "
                               "failed: [REDACTED]",
                }],
            } for name in ("ApiService", "AppHost")],
        }, json.loads(summaries[0].read_text()))
        self.assertIn("NuGet failure details:", result.stdout)

    def test_failed_restore_assets_invalid_or_oversized_input_preserves_sdk_status(self):
        output = Path(self.temp.name)
        with tempfile.TemporaryDirectory() as work:
            project = Path(work) / "AppHost"
            assets = project / "obj" / "project.assets.json"
            assets.parent.mkdir(parents=True)
            for label, content in (("malformed", "{"), ("oversized", "x" * (8 * 1024 * 1024 + 1))):
                with self.subTest(label=label):
                    assets.write_text(content)
                    result = subprocess.run(
                        [sys.executable, str(SCRIPT.with_name("capture-dotnet.py")),
                         sys.executable, str(output / label),
                         "restore"],
                        cwd=project, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                        text=True, timeout=15,
                    )
                    self.assertEqual(2, result.returncode)
                    summaries = list((output / label).glob("*.json"))
                    self.assertEqual(1, len(summaries))
                    summary = summaries[0]
                    details = json.loads(summary.read_text())
                    self.assertEqual("AppHost", details["projects"][0]["project"])
                    self.assertTrue(details["projects"][0]["diagnostic_error"])
                    self.assertLess(summary.stat().st_size, 4096)

    @unittest.skipIf(os.name == "nt", "Assets collection runs in the POSIX macOS validator")
    def test_failed_restore_assets_summary_bounds_projects_sources_and_messages(self):
        output = Path(self.temp.name)
        with tempfile.TemporaryDirectory() as work:
            for index in range(20):
                assets = Path(work) / f"Project{index:02}" / "obj" / "project.assets.json"
                assets.parent.mkdir(parents=True)
                assets.write_text(json.dumps({
                    "project": {"restore": {"sources": {
                        f"https://feed{source}.test/index.json?custom=credential": {}
                        for source in range(20)
                    }}},
                    "logs": [{"code": "NU1301", "level": "Error", "message": "x" * 10000}] * 40,
                }))
            result = subprocess.run(
                [sys.executable, str(SCRIPT.with_name("capture-dotnet.py")),
                 sys.executable, str(output / "bounded"), "restore"],
                cwd=Path(work) / "Project00", stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                text=True, timeout=15,
            )
        self.assertEqual(2, result.returncode)
        summary = next((output / "bounded").glob("*.json"))
        self.assertLessEqual(summary.stat().st_size, 1024 * 1024)
        details = json.loads(summary.read_text())
        self.assertTrue(details["truncated"])
        self.assertTrue(0 < len(details["projects"]) <= 16)
        for project in details["projects"]:
            self.assertTrue(project["truncated"])
            self.assertEqual(16, len(project["sources"]))
            self.assertTrue(all(source.endswith("/index.json") for source in project["sources"]))
            self.assertEqual(32, len(project["logs"]))
            self.assertTrue(all(len(item["message"]) == 2048 for item in project["logs"]))

    def test_sdk_diagnostics_bound_output_and_do_not_mask_failure_when_log_write_fails(self):
        output = Path(self.temp.name)
        capture = SCRIPT.with_name("capture-dotnet.py")
        command = [sys.executable, str(capture), sys.executable, str(output / "logs"),
                   "-c", "import sys; print('x' * (2 * 1024 * 1024)); print('original failure'); sys.exit(23)"]
        result = subprocess.run(command, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE, text=True, timeout=15)
        self.assertEqual(23, result.returncode)
        log = next((output / "logs").glob("*.log")).read_text()
        self.assertLess(len(log), 1024 * 1024 + 200)
        self.assertTrue(log.endswith("original failure\n"))
        self.assertIn("Output truncated to the last", log)
        blocked = output / "blocked"
        blocked.write_text("not a directory")
        command[3] = str(blocked)
        result = subprocess.run(command, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE, text=True, timeout=15)
        self.assertEqual(23, result.returncode)
        self.assertIn("Could not retain SDK failure diagnostics:", result.stderr)

    def test_successful_sdk_command_creates_no_failure_diagnostic(self):
        output = Path(self.temp.name) / "success"
        result = subprocess.run(
            [sys.executable, str(SCRIPT.with_name("capture-dotnet.py")), sys.executable, str(output),
             "-c", "print('successful SDK operation')"],
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, timeout=15,
        )
        self.assertEqual(0, result.returncode)
        self.assertEqual("successful SDK operation\n", result.stdout)
        self.assertFalse(output.exists())


if __name__ == "__main__":
    if "--emit-skip-warning" in sys.argv:
        import argparse
        parser = argparse.ArgumentParser()
        parser.add_argument("--emit-skip-warning", action="store_true")
        parser.add_argument("--output-dir", type=Path, required=True)
        args = parser.parse_args()
        spec = importlib.util.spec_from_file_location("homebrew_publisher", SCRIPT)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        fake = FakeGitHub()
        fake.module = module
        fake.current = cask(VERSION)
        args.output_dir.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory() as work, server_for(fake) as url:
            candidate = Path(work) / "aspire.rb"
            candidate.write_bytes(cask(VERSION))
            subprocess.run(
                [sys.executable, str(SCRIPT), "--candidate", str(candidate),
                 "--version", VERSION, "--test-server", url,
                 "--output-dir", str(args.output_dir)], check=True, timeout=15,
            )
            if fake.writes:
                raise AssertionError("Warning scenario made a remote write")
    else:
        unittest.main()
