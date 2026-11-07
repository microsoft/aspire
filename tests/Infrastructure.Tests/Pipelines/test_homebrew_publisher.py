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
                    "head": {"ref": BRANCH, "repo": {"full_name": "microsoft/homebrew-aspire"}},
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
