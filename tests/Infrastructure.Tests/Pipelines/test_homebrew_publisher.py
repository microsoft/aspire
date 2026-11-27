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
import signal
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

    def test_public_asset_wait_retries_only_unavailable_assets_and_is_bounded(self):
        release = {
            "tag_name": f"v{VERSION}", "draft": False, "prerelease": False,
            "assets": [{"name": f"aspire-cli-osx-{arch}-{VERSION}.tar.gz"} for arch in ("arm64", "x64")],
        }
        now = [0]
        def sleep(seconds):
            now[0] += seconds
        with patch.object(self.fake, "request", side_effect=[
            self.module.ApiError(404, "Not found"), {**release, "draft": True}, release,
        ]), contextlib.redirect_stdout(io.StringIO()):
            self.module.wait_for_public_assets(
                self.fake, VERSION, 30, sleep=sleep, clock=lambda: now[0]
            )
        self.assertEqual(20, now[0])
        now[0] = 0
        with patch.object(self.fake, "request", side_effect=self.module.ApiError(404, "Not found")), \
                contextlib.redirect_stdout(io.StringIO()), \
                self.assertRaisesRegex(self.module.PublicationError, "publish the draft.*rerun"):
            self.module.wait_for_public_assets(
                self.fake, VERSION, 11, sleep=sleep, clock=lambda: now[0]
            )
        self.assertEqual(11, now[0])
        with patch.object(self.fake, "request", side_effect=self.module.ApiError(403, "Forbidden")) as request, \
                self.assertRaises(self.module.ApiError):
            self.module.wait_for_public_assets(self.fake, VERSION, 30)
        self.assertEqual(1, request.call_count)
        with patch.object(self.fake, "request", return_value={**release, "prerelease": True}), \
                self.assertRaisesRegex(self.module.PublicationError, "stable"):
            self.module.wait_for_public_assets(self.fake, VERSION, 30)

    @unittest.skipUnless(os.name == "posix", "Publisher validation runs on macOS/POSIX")
    def test_validator_children_do_not_inherit_publication_credentials_or_msbuild_version(self):
        canaries = {
            "HOMEBREW_TAP_WRITE_TOKEN": "test-write-secret", "HOMEBREW_READ_TOKEN": "test-read-secret",
            "SYSTEM_ACCESSTOKEN": "test-azure-secret", "GH_TOKEN": "test-github-secret", "VERSION": "auto",
        }
        marker = "test-agent-orphan-cleanup"
        with patch.dict(os.environ, {**canaries, "VSTS_PROCESS_LOOKUP_ID": marker}), \
                contextlib.redirect_stdout(io.StringIO()):
            output = self.module.run([
                sys.executable, "-c",
                "import json, os; print(json.dumps({key: os.environ.get(key) "
                "for key in " + repr(list(canaries) + ["PATH", "VSTS_PROCESS_LOOKUP_ID"]) + "}))",
            ], cwd=Path(self.temp.name), timeout=5)
            actual = json.loads(output)
            self.assertEqual({key: None for key in canaries}, {key: actual[key] for key in canaries})
            self.assertEqual(os.environ["PATH"], actual["PATH"])
            self.assertEqual(marker, actual["VSTS_PROCESS_LOOKUP_ID"])
            self.assertEqual(canaries, {key: os.environ[key] for key in canaries})

    def test_public_asset_wait_cli_rejects_unbounded_values_before_network(self):
        with server_for(self.fake) as url:
            for value in ("-1", "601"):
                with self.subTest(value=value):
                    output = Path(self.temp.name) / f"wait-{value}"
                    process = subprocess.run([
                        sys.executable, str(SCRIPT), "--candidate", str(self.candidate),
                        "--version", VERSION, "--test-server", url, "--output-dir", str(output),
                        "--wait-for-release-seconds", value,
                    ], capture_output=True, text=True, timeout=5)
                    self.assertEqual(1, process.returncode)
                    result = json.loads((output / "result.json").read_text())
                    self.assertEqual("failed", result["outcome"])
                    self.assertIn("between 0 and 600", result["error"])
        self.assertEqual([], self.fake.calls)

    @unittest.skipUnless(os.name == "posix", "Publisher validation runs on macOS/POSIX")
    def test_timeout_allows_exit_trap_to_retain_assets_and_stops_owned_children(self):
        work = Path(self.temp.name)
        self.addCleanup(self.cleanup_owned_child, work / "child.pid")
        script = work / "validator.sh"
        script.write_text("""
set -eu
trap 'cp assets.json retained.json; echo retained-before-exit; exit 23' EXIT
printf '{"errors":["NU1105"]}\\n' > assets.json
sleep 60 &
echo $! > child.pid
echo validator-started
wait
""")
        log = work / "validation.log"
        with contextlib.redirect_stdout(io.StringIO()), self.assertRaisesRegex(
            self.module.PublicationError, "timed out"
        ):
            self.module.run(["bash", str(script)], log, cwd=work, timeout=1, terminate_grace=1)
        self.assertEqual('{"errors":["NU1105"]}\n', (work / "retained.json").read_text())
        self.assertIn("retained-before-exit", log.read_text())
        self.assertIn("validator-started", log.read_text())
        self.assert_process_stopped(int((work / "child.pid").read_text()))

    @unittest.skipUnless(os.name == "posix", "Publisher validation runs on macOS/POSIX")
    def test_timeout_hard_kills_uncooperative_group_without_waiting_for_inherited_stdout(self):
        work = Path(self.temp.name)
        self.addCleanup(self.cleanup_owned_child, work / "child.pid")
        script = work / "validator.py"
        script.write_text("""
import os, signal, time
from pathlib import Path
signal.signal(signal.SIGTERM, signal.SIG_IGN)
child = os.fork()
if child == 0:
    while True:
        time.sleep(1)
Path("child.pid").write_text(str(child))
print("before-hard-kill", flush=True)
while True:
    time.sleep(1)
""")
        started = time.monotonic()
        log = work / "validation.log"
        with contextlib.redirect_stdout(io.StringIO()), self.assertRaisesRegex(
            self.module.PublicationError, "timed out"
        ):
            self.module.run([sys.executable, str(script)], log, cwd=work, timeout=1, terminate_grace=0.2)
        self.assertLess(time.monotonic() - started, 5)
        self.assertIn("before-hard-kill", log.read_text())
        self.assertIn("Escalated owned process group to SIGKILL", log.read_text())
        self.assertIn("timed out", log.read_text())
        self.assert_process_stopped(int((work / "child.pid").read_text()))

    @unittest.skipUnless(os.name == "posix", "Publisher validation runs on macOS/POSIX")
    def test_parent_cancellation_cleans_owned_worker_retains_failure_and_restores_handlers(self):
        cases = (
            ("SIGTERM", False), ("SIGTERM", True), ("SIGINT", False),
        )
        for cancellation, uncooperative in cases:
            with self.subTest(cancellation=cancellation, uncooperative=uncooperative):
                work = Path(self.temp.name) / f"{cancellation}-{uncooperative}"
                work.mkdir()
                self.addCleanup(self.cleanup_owned_child, work / "child.pid")
                worker = work / "worker.py"
                worker.write_text("""
import os, signal, sys, time
from pathlib import Path
def terminate(signum, frame):
    Path("term-observed").touch()
    if sys.argv[1] == "False":
        Path("retained.json").write_text('{"cancelled":true}\\n')
        print("retained-before-exit", flush=True)
        raise SystemExit(23)
signal.signal(signal.SIGTERM, terminate)
Path("child.pid").write_text(str(os.getpid()))
print("worker-ready", flush=True)
Path("ready").touch()
while True:
    time.sleep(1)
""")
                parent = work / "parent.py"
                parent.write_text("""
import importlib.util, json, signal, sys
from pathlib import Path
spec = importlib.util.spec_from_file_location("publisher", sys.argv[1])
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
previous = {sig: signal.getsignal(sig) for sig in (signal.SIGTERM, signal.SIGINT)}
try:
    try:
        module.run([sys.executable, "worker.py", sys.argv[2]], Path("validation.log"),
                   cwd=Path.cwd(), timeout=60, terminate_grace=0.4)
    except module.PublicationError as error:
        print(str(error), flush=True)
        raise SystemExit(7)
    raise AssertionError("Cancellation returned success")
finally:
    Path("restored.json").write_text(json.dumps({
        signal.Signals(sig).name: signal.getsignal(sig) == handler
        for sig, handler in previous.items()
    }))
""")
                process = subprocess.Popen(
                    [sys.executable, str(parent), str(SCRIPT), str(uncooperative)],
                    cwd=work, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
                )
                try:
                    deadline = time.monotonic() + 10
                    while not (work / "ready").exists() and time.monotonic() < deadline:
                        if process.poll() is not None:
                            self.fail(f"Parent exited before worker readiness: {process.communicate()[0]}")
                        time.sleep(0.01)
                    self.assertTrue((work / "ready").exists(), "Owned worker did not become ready")
                    started = time.monotonic()
                    sig = getattr(signal, cancellation)
                    os.kill(process.pid, sig)
                    if uncooperative:
                        deadline = time.monotonic() + 3
                        while not (work / "term-observed").exists() and time.monotonic() < deadline:
                            if process.poll() is not None:
                                break
                            time.sleep(0.01)
                        if process.poll() is None:
                            # Repeated cancellation during grace must not bypass cleanup.
                            os.kill(process.pid, sig)
                    output = process.communicate(timeout=8)[0]
                    self.assertLess(time.monotonic() - started, 8)
                    self.assert_process_stopped(int((work / "child.pid").read_text()))
                    self.assertEqual(7 if cancellation == "SIGTERM" else -signal.SIGINT,
                                     process.returncode, output)
                    log = (work / "validation.log").read_text()
                    if cancellation == "SIGTERM":
                        self.assertIn("cancelled by SIGTERM", log)
                    self.assertIn("worker-ready", log)
                    self.assertEqual({"SIGTERM": True, "SIGINT": True},
                                     json.loads((work / "restored.json").read_text()))
                    if uncooperative:
                        self.assertIn("Escalated owned process group to SIGKILL", log)
                    else:
                        self.assertEqual('{"cancelled":true}\n', (work / "retained.json").read_text())
                        self.assertIn("retained-before-exit", log)
                finally:
                    if process.poll() is None:
                        process.kill()
                    process.communicate(timeout=5)

    @unittest.skipUnless(os.name == "posix", "Publisher validation runs on macOS/POSIX")
    def test_successful_leader_cannot_leave_owned_descendant_running(self):
        work = Path(self.temp.name)
        self.addCleanup(self.cleanup_owned_child, work / "child.pid")
        with contextlib.redirect_stdout(io.StringIO()), self.assertRaisesRegex(
            self.module.PublicationError, "descendants"
        ):
            self.module.run(
                ["bash", "-c", "sleep 60 & echo $! > child.pid; echo leader-finished"],
                work / "validation.log", cwd=work, timeout=1, terminate_grace=0.2,
            )
        self.assert_process_stopped(int((work / "child.pid").read_text()))
        self.assertIn("leader-finished", (work / "validation.log").read_text())

    def assert_process_stopped(self, pid):
        # A reparented killed child can briefly remain a zombie; kill(pid, 0)
        # alone would incorrectly classify that as a running validator worker.
        status = subprocess.run(
            ["ps", "-o", "stat=", "-p", str(pid)], capture_output=True, text=True, timeout=2
        ).stdout.strip()
        if status and not status.startswith("Z"):
            os.kill(pid, signal.SIGKILL)
            deadline = time.monotonic() + 2
            while time.monotonic() < deadline:
                after = subprocess.run(
                    ["ps", "-o", "stat=", "-p", str(pid)], capture_output=True, text=True, timeout=2
                ).stdout.strip()
                if not after or after.startswith("Z"):
                    self.fail(f"Owned child {pid} survived validator cleanup ({status}); "
                              "test cleanup killed and verified the exact owned child stopped")
                time.sleep(0.01)
            self.fail(f"Owned child {pid} survived validator and test cleanup ({status})")

    def cleanup_owned_child(self, path):
        if path.exists():
            try:
                os.kill(int(path.read_text()), signal.SIGKILL)
            except ProcessLookupError:
                pass

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

    @unittest.skipUnless(os.name == "posix", "Publisher validation runs on macOS/POSIX")
    def test_validation_timeout_retains_partial_diagnostics_and_fails(self):
        log = Path(self.temp.name) / "validation.log"
        with contextlib.redirect_stdout(io.StringIO()) as console:
            with self.assertRaisesRegex(self.module.PublicationError, "timed out after 1s"):
                self.module.run(
                    [sys.executable, "-c", "import time; print('last validator diagnostic', flush=True); time.sleep(60)"],
                    log, timeout=1, terminate_grace=0.1,
                )
        self.assertEqual("last validator diagnostic", log.read_text().splitlines()[0])
        self.assertIn("timed out", log.read_text())
        self.assertIn(log.read_text(), console.getvalue())

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

    def test_validation_adapter_rejects_unpublished_or_mismatched_release_before_downstream_work(self):
        output = Path(self.temp.name)
        release_path = f"/repos/microsoft/aspire/releases/tags/v{VERSION}"
        for field, value in (("draft", True), ("prerelease", True), ("tag_name", "v13.6.4")):
            with self.subTest(field=field):
                release = {"tag_name": f"v{VERSION}", "draft": False, "prerelease": False}
                release[field] = value
                with patch.object(self.module.platform, "system", return_value="Darwin"), \
                        patch.object(self.module, "run", return_value=self.module.VALIDATOR_REVISION) as run, \
                        patch.object(self.fake, "request", side_effect=[
                            release, self.module.PublicationError("Unexpected downstream release request")
                        ]) as request:
                    with self.assertRaisesRegex(
                        self.module.PublicationError,
                        "^Full validation requires an already-published stable release$",
                    ):
                        self.module.validate_release(self.candidate, VERSION, self.fake, output, output)
                request.assert_called_once_with("GET", release_path)
                self.assertEqual([
                    ["git", "--no-pager", "-C", str(output.resolve()), "rev-parse", "HEAD"],
                    ["git", "--no-pager", "-C", str(output.resolve()), "diff", "--exit-code", "HEAD"],
                ], [invocation.args[0] for invocation in run.call_args_list])

    def test_validation_adapter_checks_distinct_archive_metadata_before_running_validator(self):
        arm_url = (
            f"https://github.com/microsoft/aspire/releases/download/v{VERSION}/"
            f"aspire-cli-osx-arm64-{VERSION}.tar.gz"
        )
        intel_url = (
            f"https://github.com/microsoft/aspire/releases/download/v{VERSION}/"
            f"aspire-cli-osx-x64-{VERSION}.tar.gz"
        )
        archives = {arm_url: b"arm64 archive\n", intel_url: b"x64 archive\n"}
        template = (
            b'cask "aspire" do\n  version "${VERSION}"\n'
            b'  sha256 arm: "${SHA256_OSX_ARM64}",\n'
            b'         intel: "${SHA256_OSX_X64}"\nend\n'
        )
        output = Path(self.temp.name).resolve()
        tap = output / "tap"
        tap.mkdir()
        for checker_fails in (False, True):
            with self.subTest(checker_fails=checker_fails):
                checked_metadata = []
                validator_calls = []
                downloads = []

                def fixture_run(command, log=None, cwd=None, env=None):
                    if command[0] == "git":
                        return self.module.VALIDATOR_REVISION if "rev-parse" in command else ""
                    if command[0] == "curl":
                        url = command[-3]
                        downloads.append(url)
                        Path(command[-1]).write_bytes(archives[url])
                        return ""
                    if command[0] == "ruby":
                        self.assertEqual(
                            ["ruby", str(tap / "scripts/check-cask-consistency.rb"), str(self.candidate)],
                            command[:3],
                        )
                        self.assertEqual(5, len(command))
                        self.assertEqual(output / "consistency.log", log)
                        self.assertEqual(template, Path(command[4]).read_bytes())
                        checked_metadata.append(json.loads(Path(command[3]).read_text()))
                        if checker_fails:
                            raise self.module.PublicationError("fixture consistency checker failed")
                        return ""
                    if command[0] == "bash":
                        validator_calls.append((command, log, cwd))
                        return ""
                    self.fail(f"Unexpected validation command: {command}")

                release = {
                    "tag_name": f"v{VERSION}", "draft": False, "prerelease": False,
                    "assets": [
                        {"name": url.rsplit("/", 1)[1], "browser_download_url": url}
                        for url in archives
                    ],
                }
                with patch.object(self.module.platform, "system", return_value="Darwin"), \
                        patch.object(self.module, "run", side_effect=fixture_run), \
                        patch.object(self.fake, "request", side_effect=[
                            release, {"object": {"type": "commit", "sha": "c" * 40}},
                            {"content": base64.b64encode(template).decode()},
                        ]):
                    if checker_fails:
                        with self.assertRaisesRegex(
                            self.module.PublicationError, "^fixture consistency checker failed$"
                        ):
                            self.module.validate_release(self.candidate, VERSION, self.fake, tap, output)
                    else:
                        self.module.validate_release(self.candidate, VERSION, self.fake, tap, output)
                self.assertEqual([arm_url, intel_url], downloads)
                self.assertEqual([{"casks": [{
                    "version": VERSION,
                    "url": arm_url,
                    "sha256": "8df0e17c9f824ebd0bdba686fe0e5d400dc969843f61abed695c2543fed0808e",
                    "variations": {"intel": {
                        "url": intel_url,
                        "sha256": "d68a728f4601fc1ed038d2b9ad6a2855c0b465737b9a6bcc95d50da7d17f48c7",
                    }},
                }]}], checked_metadata)
                self.assertEqual([] if checker_fails else [(
                    ["bash", str(tap / "scripts/validate-cask.sh"), "--cask-file", str(self.candidate),
                     "--diagnostics-dir", str(output / "validator-diagnostics")],
                    output / "validation.log", tap,
                )], validator_calls)

    def test_validation_adapter_retains_fake_validator_raw_diagnostics_and_status_after_cleanup(self):
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
        # This fake validator boundary checks adapter retention, not the pinned
        # tap's collection behavior or a real Homebrew installation.
        validator = """
import argparse, json, os, shutil, sys
from pathlib import Path
parser = argparse.ArgumentParser()
parser.add_argument("--cask-file", required=True, type=Path)
parser.add_argument("--diagnostics-dir", required=True, type=Path)
args = parser.parse_args(sys.argv[3:])
assert os.environ["PATH"] == sys.argv[2]
status = int(sys.argv[1])
assert args.cask_file.is_file()
work = Path.cwd() / "workspace"
work.mkdir()
try:
    args.diagnostics_dir.mkdir()
    for name in ("cli-new.log", "cli-restore.log"):
        (args.diagnostics_dir / name).write_bytes(b"Authorization: Bearer fixture-canary\\r\\n\\xff\\n")
    assets = work / "smoke" / "SmokeApp.AppHost" / "obj" / "project.assets.json"
    assets.parent.mkdir(parents=True)
    assets.write_text(json.dumps({
        "version": 3,
        "targets": {"net10.0": {"Example/1.0": {"type": "package"}}},
        "libraries": {"Example/1.0": {"type": "package", "path": "example/1.0"}},
        "project": {"restore": {"sources": {"https://fixture.invalid/feed?token=raw-canary": {}}}},
        "logs": [{"level": "Error", "code": "NU1105", "message": "'auto' is not a valid version string"}]
    }, indent=2) + "\\n")
    if status:
        retained = args.diagnostics_dir / assets.relative_to(work)
        retained.parent.mkdir(parents=True)
        shutil.copyfile(assets, retained)
    print(Path.cwd())
finally:
    shutil.rmtree(work)
sys.exit(status)
"""
        for status in (0, 6):
            with self.subTest(status=status), tempfile.TemporaryDirectory(dir=self.temp.name) as temporary:
                output = Path(temporary).resolve()
                tap = output / "tap"
                tap.mkdir()

                def fixture_run(command, log=None, cwd=None, env=None):
                    if command[0] == "curl":
                        Path(command[-1]).write_bytes(b"archive")
                    if command[0] == "bash":
                        self.assertEqual("1", env.get("MSBUILDDISABLENODEREUSE"))
                        return actual_run(
                            [sys.executable, "-c", validator, str(status),
                             os.environ["PATH"], *command[2:]],
                            log, cwd=cwd, env=env,
                        )
                    return self.module.VALIDATOR_REVISION if "rev-parse" in command else ""

                with patch.object(self.module.platform, "system", return_value="Darwin"), \
                        patch.object(self.module, "run", side_effect=fixture_run), \
                        contextlib.redirect_stdout(io.StringIO()):
                    if status:
                        with self.assertRaisesRegex(self.module.PublicationError, "exit 6"):
                            self.module.validate_release(self.candidate, VERSION, ReleaseClient(), tap, output)
                    else:
                        self.module.validate_release(self.candidate, VERSION, ReleaseClient(), tap, output)
                self.assertEqual(str(tap), (output / "validation.log").read_text().strip())
                self.assertFalse((tap / "workspace").exists())
                diagnostics = output / "validator-diagnostics"
                for name in ("cli-new.log", "cli-restore.log"):
                    self.assertEqual(
                        b"Authorization: Bearer fixture-canary\r\n\xff\n", (diagnostics / name).read_bytes()
                    )
                expected_files = ["cli-new.log", "cli-restore.log"]
                if status:
                    assets_path = "smoke/SmokeApp.AppHost/obj/project.assets.json"
                    expected_files.append(assets_path)
                    self.assertEqual({
                        "version": 3,
                        "targets": {"net10.0": {"Example/1.0": {"type": "package"}}},
                        "libraries": {"Example/1.0": {"type": "package", "path": "example/1.0"}},
                        "project": {"restore": {"sources": {"https://fixture.invalid/feed?token=raw-canary": {}}}},
                        "logs": [{"level": "Error", "code": "NU1105",
                                  "message": "'auto' is not a valid version string"}],
                    }, json.loads((diagnostics / assets_path).read_text()))
                self.assertEqual(expected_files, sorted(
                    path.relative_to(diagnostics).as_posix()
                    for path in diagnostics.rglob("*") if path.is_file()
                ))
                self.assertEqual(cask(VERSION), self.candidate.read_bytes())


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
