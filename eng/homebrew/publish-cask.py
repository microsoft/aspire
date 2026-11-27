#!/usr/bin/env python3
# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.

"""Validate and propose one stable cask update; dry run is the default."""

import argparse
import base64
import difflib
import email.utils
import hashlib
import http.client
import ipaddress
import json
import os
from pathlib import Path
import platform
import re
import signal
import socket
import subprocess
import tempfile
import time
import sys
import threading
import urllib.parse

TAP = "microsoft/homebrew-aspire"
SOURCE = "microsoft/aspire"
CASK = "Casks/a/aspire.rb"
VALIDATOR_REVISION = "89a2b248e4c7ab883d072ecb2d10874e04c2d6b1"
PREFIX = f"/repos/{TAP}"
SUBPROCESS_ENVIRONMENT = (
    "PATH", "HOME", "USER", "LOGNAME", "SHELL", "TMPDIR", "TMP", "TEMP",
    "LANG", "LC_ALL", "LC_CTYPE", "TERM", "CI", "DOTNET_ROOT", "DOTNET_ROOT_ARM64",
    "DOTNET_ROOT_X64", "DOTNET_CLI_HOME", "DOTNET_CLI_TELEMETRY_OPTOUT",
    "DOTNET_SKIP_FIRST_TIME_EXPERIENCE", "HOMEBREW_NO_AUTO_UPDATE",
    "HOMEBREW_NO_INSTALL_CLEANUP", "HOMEBREW_NO_ANALYTICS", "VSTS_PROCESS_LOOKUP_ID",
)


class PublicationError(RuntimeError):
    pass


class ApiError(PublicationError):
    def __init__(self, status, message, retry_after=None):
        super().__init__(message)
        self.status = status
        self.retry_after = retry_after

    @property
    def transient(self):
        return self.status in (0, 408, 429, 500, 502, 503, 504) or (
            self.status == 403 and self.retry_after is not None
        )


class GitHubClient:
    def __init__(self, token="", api_url="https://api.github.com", sleep=time.sleep, clock=time.monotonic):
        self.token = token
        self.api_url = api_url
        self.sleep = sleep
        self.clock = clock
        self.deadline = None

    def begin_phase(self, deadline=None):
        self.deadline = self.clock() + 120 if deadline is None else deadline

    def request_once(self, method, path, body, remaining):
        url = urllib.parse.urlsplit(self.api_url)
        connection_type = http.client.HTTPSConnection if url.scheme == "https" else http.client.HTTPConnection
        connection = connection_type(url.hostname, url.port, timeout=min(30, remaining))
        timer = None
        response = None
        try:
            connection.connect()
            transport = connection.sock
            def expire():
                # A socket inactivity timeout alone cannot bound a slow,
                # continuously streaming body. Interrupt this request at its
                # absolute deadline; a completed/closed socket needs no action.
                try:
                    transport.shutdown(socket.SHUT_RDWR)
                except OSError:
                    pass
            remaining = min(30, self.deadline - self.clock())
            if remaining <= 0:
                raise ApiError(0, "Request deadline exceeded before send")
            timer = threading.Timer(remaining, expire)
            timer.daemon = True
            timer.start()
            # GitHub rejects requests without a valid application User-Agent.
            # https://docs.github.com/en/rest/using-the-rest-api/getting-started-with-the-rest-api#user-agent
            headers = {
                "Accept": "application/vnd.github+json",
                "X-GitHub-Api-Version": "2022-11-28",
                "User-Agent": "Aspire-Homebrew-Publisher",
            }
            if self.token:
                headers["Authorization"] = f"Bearer {self.token}"
            data = None if body is None else json.dumps(body).encode()
            if data is not None:
                headers["Content-Type"] = "application/json"
            connection.request(method, path, body=data, headers=headers)
            response = connection.getresponse()
            if not 200 <= response.status < 300:
                retry_after = response.getheader("Retry-After")
                if retry_after is None and response.getheader("X-RateLimit-Remaining") == "0":
                    reset = response.getheader("X-RateLimit-Reset")
                    if reset and reset.isdigit():
                        retry_after = max(0, int(reset) - time.time())
                try:
                    retry_after = None if retry_after is None else float(retry_after)
                except ValueError:
                    try:
                        retry_after = max(0, email.utils.parsedate_to_datetime(retry_after).timestamp() - time.time())
                    except (TypeError, ValueError):
                        retry_after = None
                raise ApiError(response.status, f"{method} {path.split('?')[0]} failed (HTTP {response.status})", retry_after)
            payload = bytearray()
            while True:
                remaining = min(30, self.deadline - self.clock())
                if remaining <= 0:
                    raise ApiError(0, "Absolute response deadline exceeded")
                transport.settimeout(remaining)
                chunk = response.read1(64 * 1024)
                if not chunk:
                    break
                payload.extend(chunk)
                if len(payload) > 4 * 1024 * 1024:
                    raise ApiError(503, "GitHub response exceeded the 4 MiB limit")
                if response.length == 0:
                    break
            # read1 can return EOF without raising IncompleteRead.
            if response.length not in (None, 0):
                raise ApiError(0, "Truncated GitHub response")
            if self.clock() >= self.deadline:
                raise ApiError(0, "Absolute response deadline exceeded")
            return json.loads(payload)
        finally:
            if timer:
                timer.cancel()
                timer.join()
            if response:
                response.close()
            connection.close()

    def request(self, method, path, body=None):
        # GitHub ref, contents and PR contracts:
        # https://docs.github.com/en/rest/git/refs
        # https://docs.github.com/en/rest/repos/contents
        # https://docs.github.com/en/rest/pulls/pulls
        if self.deadline is None:
            self.begin_phase()
        for attempt in range(3):
            remaining = self.deadline - self.clock()
            if remaining <= 0:
                raise PublicationError(f"Network/reconciliation deadline exceeded for {method} {path.split('?')[0]}")
            try:
                return self.request_once(method, path, body, remaining)
            except ApiError as error:
                failure = error
            except (OSError, http.client.HTTPException) as error:
                failure = ApiError(0, f"{method} {path.split('?')[0]} failed ({type(error).__name__})")
            except json.JSONDecodeError as error:
                failure = ApiError(503, f"{method} {path.split('?')[0]} returned an invalid JSON response")
            # A server can accept a mutation and lose its response. Never retry
            # writes here: each operation below reconciles its own evidence.
            if method != "GET" or not failure.transient or attempt == 2:
                raise failure
            delay = failure.retry_after if failure.retry_after is not None else 2 ** attempt
            if delay < 0 or self.clock() + delay >= self.deadline:
                raise PublicationError(f"Retry deadline exceeded: {failure}") from failure
            print(f"Retrying {method} {path.split('?')[0]} after {delay:g}s (attempt {attempt + 2}/3).")
            self.sleep(delay)
        raise AssertionError("Retry loop exhausted")


def optional_get(client, path):
    try:
        return client.request("GET", path)
    except ApiError as error:
        if error.status == 404:
            return None
        raise


def contents(client, repo, ref):
    value = client.request("GET", f"/repos/{repo}/contents/{CASK}?{urllib.parse.urlencode({'ref': ref})}")
    if value.get("encoding") != "base64":
        raise PublicationError(f"Unsupported cask content encoding in {repo}")
    try:
        data = base64.b64decode(value["content"], validate=False)
    except (KeyError, ValueError) as error:
        raise PublicationError(f"Invalid cask contents in {repo}") from error
    return data, value["sha"]


def identity(data):
    # Read only the generated metadata shape; never execute candidate Ruby:
    #   version "13.6.4"
    #   sha256 arm: "<64 hex>", intel: "<64 hex>"
    # Full Ruby/template equivalence is checked by the pinned Ripper checker.
    text = data.decode("utf-8")
    versions = re.findall(r'^\s*version\s+"(\d+\.\d+\.\d+)"\s*$', text, re.MULTILINE)
    hashes = re.findall(r'^\s*sha256\s+arm:\s*"([0-9a-f]{64})",\s*\n?\s*intel:\s*"([0-9a-f]{64})"', text, re.MULTILINE)
    if len(versions) != 1 or len(hashes) != 1 or "0" * 64 in hashes[0]:
        raise PublicationError("Cask requires one stable version and non-placeholder arm64/x64 SHA256 values")
    return versions[0], hashes[0]


def matching_pulls(client, branch, base):
    matches = []
    for page in range(1, 11):
        query = urllib.parse.urlencode({"state": "all", "head": f"microsoft:{branch}", "base": base, "per_page": 100, "page": page})
        pulls = client.request("GET", f"{PREFIX}/pulls?{query}")
        for pull in pulls:
            if (pull["head"]["ref"] == branch and pull["base"]["ref"] == base
                    and pull["head"]["repo"] and pull["head"]["repo"]["full_name"] == TAP):
                matches.append(pull)
        if len(pulls) < 100:
            return matches
    raise PublicationError("PR reconciliation exceeded 1000 results; cannot safely determine existing submission")


def skip(version, branch, reason, pr_url=None):
    result = {"outcome": "skipped", "version": version, "branch": branch, "reason": reason, "pr_url": pr_url}
    # Azure logging commands must reach agent stdout, not only an uploaded log.
    # https://learn.microsoft.com/en-us/azure/devops/pipelines/scripts/logging-commands
    message = f"Skipping Homebrew {version}: {reason}" + (f" ({pr_url})" if pr_url else "")
    print("##vso[task.logissue type=warning;code=HomebrewAlreadySubmitted;]" + message)
    return result


def reconcile(client, version, candidate_identity, branch, base):
    current, sha = contents(client, TAP, base)
    current_version, current_hashes = identity(current)
    if current_version == version:
        if current_hashes != candidate_identity[1]:
            raise PublicationError(f"Already-current version {version} has conflicting archive hashes")
        return skip(version, branch, "already current/merged"), current, sha
    pulls = matching_pulls(client, branch, base)
    if len(pulls) > 1:
        raise PublicationError(f"Multiple matching PRs for {branch}; operator reconciliation required")
    if pulls:
        pull = pulls[0]
        if pull["state"] == "closed" and not pull["merged_at"]:
            raise PublicationError(f"Matching PR is closed without merge: {pull['html_url']}")
        # Merged version branches may have been deleted, and the tap may
        # already contain a newer release. Reconcile against the merge commit.
        proposed, _ = contents(client, TAP, pull["merge_commit_sha"] if pull["merged_at"] else branch)
        if identity(proposed) != candidate_identity:
            raise PublicationError(f"Matching PR has conflicting version/hashes: {pull['html_url']}")
        return skip(version, branch, "PR already submitted/merged", pull["html_url"]), current, sha
    if optional_get(client, f"{PREFIX}/git/ref/heads/{branch}") is not None:
        return skip(version, branch, "submission in flight; existing branch requires operator recovery if abandoned"), current, sha
    if tuple(map(int, current_version.split("."))) > tuple(map(int, version.split("."))):
        raise PublicationError(f"Refusing rollback from {current_version} to {version}")
    return None, current, sha


def publish_cask(candidate, version, client, validate, publish, writer):
    if not re.fullmatch(r"\d+\.\d+\.\d+", version):
        raise PublicationError("Only stable versions may be proposed to the production tap")
    data = candidate.read_bytes()
    candidate_identity = identity(data)
    if candidate_identity[0] != version:
        raise PublicationError("Candidate version does not match requested version")
    branch = f"homebrew/aspire-{version}"
    client.begin_phase()
    base = client.request("GET", PREFIX)["default_branch"]
    result, current, current_sha = reconcile(client, version, candidate_identity, branch, base)
    if result:
        return result
    base_commit = client.request("GET", f"{PREFIX}/git/ref/heads/{base}")["object"]["sha"]
    with tempfile.TemporaryDirectory(prefix="aspire-homebrew-") as work:
        snapshot = Path(work) / "aspire.rb"
        snapshot.write_bytes(data)
        validate(snapshot, version)
        if snapshot.read_bytes() != data:
            raise PublicationError("Validator changed the candidate; refusing to submit unvalidated bytes")
    client.begin_phase()
    result, fresh, fresh_sha = reconcile(client, version, candidate_identity, branch, base)
    if result:
        return result
    fresh_commit = client.request("GET", f"{PREFIX}/git/ref/heads/{base}")["object"]["sha"]
    if fresh != current or fresh_sha != current_sha or fresh_commit != base_commit:
        raise PublicationError("Target changed during validation; rerun to validate against the new base")
    diff = "".join(difflib.unified_diff(current.decode().splitlines(True), data.decode().splitlines(True), fromfile=CASK, tofile=CASK))
    print(diff, end="")
    result = {"outcome": "planned", "version": version, "branch": branch, "base": base, "diff": diff}
    if not publish:
        print("DryRun: true; no write credential requested, branch/contents/PR writes skipped.")
        return result
    client.begin_phase()
    write_client = writer()
    write_client.begin_phase(client.deadline)
    try:
        write_client.request("POST", f"{PREFIX}/git/refs", {"ref": f"refs/heads/{branch}", "sha": base_commit})
    except ApiError as error:
        if error.transient or error.status == 422:
            if optional_get(client, f"{PREFIX}/git/ref/heads/{branch}") is not None:
                return skip(version, branch, "submission in flight; branch ownership is uncertain")
            raise PublicationError(f"Branch creation uncertain for {branch}; inspect remote state before rerunning") from error
        raise
    try:
        write_client.request("PUT", f"{PREFIX}/contents/{CASK}", {
            "message": f"Update Aspire CLI to {version}", "branch": branch,
            "sha": current_sha, "content": base64.b64encode(data).decode(),
        })
    except ApiError as error:
        if not error.transient:
            raise
        actual, _ = contents(client, TAP, branch)
        if actual != data:
            raise PublicationError(f"Contents update uncertain for {branch}; inspect branch before recovery") from error
    try:
        pull = write_client.request("POST", f"{PREFIX}/pulls", {
            "title": f"Update Aspire CLI to {version}", "head": branch, "base": base,
            "body": f"[automated] Update the stable Aspire CLI cask to {version}.\n\nValidated against the published release with the pinned tap validator.",
            "draft": True,
        })
    except ApiError as error:
        if not (error.transient or error.status == 422):
            raise
        pulls = matching_pulls(client, branch, base)
        actual, _ = contents(client, TAP, branch)
        if len(pulls) != 1 or pulls[0]["state"] != "open" or actual != data:
            raise PublicationError(f"PR creation uncertain for {branch}; inspect PR/branch before recovery, do not resubmit blindly") from error
        pull = pulls[0]
    return {**result, "outcome": "submitted", "pr_url": pull["html_url"]}


def run(command, log=None, cwd=None, env=None, timeout=1800, terminate_grace=10):
    if os.name != "posix":
        raise PublicationError("Publisher subprocess isolation requires POSIX process groups")

    def signal_group(process, sig):
        try:
            os.killpg(process.pid, sig)
            return True
        except ProcessLookupError:
            return False
        except PermissionError:
            # macOS can return EPERM for signal 0 while a just-terminated
            # group leader is awaiting reap. Keep the bounded grace period;
            # permission errors on an actual termination remain failures.
            if sig == 0:
                return True
            raise

    # A pipe can remain open in a descendant after its parent exits. Capture to
    # a file instead, so neither timeout nor final output collection waits for
    # inherited stdout. Each command owns a fresh session/process group.
    with tempfile.TemporaryFile(dir=log.parent if log else cwd or Path.cwd()) as capture:
        failure = None
        diagnostics = []
        cancelled = None
        handle_sigterm = threading.current_thread() is threading.main_thread()
        previous_handler = signal.getsignal(signal.SIGTERM) if handle_sigterm else None

        def request_cancellation(signum, frame):
            nonlocal cancelled
            cancelled = cancelled or signal.Signals(signum).name

        # Install before spawning: raising from a handler could interrupt Popen
        # before it returns the owned PID, or bypass cleanup on a second signal.
        # Record cancellation instead and observe it through a bounded wait.
        # Python only permits process-global handler changes on the main thread.
        # SIGINT keeps its ordinary KeyboardInterrupt/finally cleanup behavior.
        try:
            if handle_sigterm:
                signal.signal(signal.SIGTERM, request_cancellation)
            process = subprocess.Popen(
                command, cwd=cwd, env=subprocess_environment() if env is None else env,
                stdout=capture, stderr=subprocess.STDOUT,
                start_new_session=True,
            )
            try:
                try:
                    deadline = time.monotonic() + timeout
                    while process.poll() is None and cancelled is None:
                        remaining = deadline - time.monotonic()
                        if remaining <= 0:
                            failure = f"timed out after {timeout:g}s"
                            break
                        try:
                            process.wait(timeout=min(0.1, remaining))
                        except subprocess.TimeoutExpired:
                            continue
                finally:
                    # TERM lets the shell's EXIT trap retain assets and clean
                    # its workspace. Also clean after cancellation or a
                    # successful shell that left background children.
                    process.poll()
                    if signal_group(process, signal.SIGTERM):
                        if failure is None and cancelled is None:
                            failure = "left running descendants"
                        deadline = time.monotonic() + terminate_grace
                        while time.monotonic() < deadline:
                            process.poll()
                            if not signal_group(process, 0):
                                break
                            time.sleep(min(0.05, max(0, deadline - time.monotonic())))
                        if signal_group(process, signal.SIGKILL):
                            diagnostics.append("Escalated owned process group to SIGKILL.")
                    process.wait(timeout=5)
            except (OSError, subprocess.TimeoutExpired) as error:
                failure = f"{failure or 'cleanup failed'}; owned process group cleanup failed: {error}"
        finally:
            if handle_sigterm:
                signal.signal(signal.SIGTERM, previous_handler)
            if cancelled:
                failure = f"cancelled by {cancelled}" + (f"; {failure}" if failure else "")
            capture.seek(0)
            output = capture.read().decode(errors="replace")
            if failure:
                diagnostics.append(f"{Path(command[0]).name} {failure}.")
            if diagnostics:
                output += "\n" + "\n".join(diagnostics) + "\n"
            if log:
                log.write_text(output)
            print(output, end="")
        if failure:
            raise PublicationError(f"{Path(command[0]).name} {failure}; see validation logs")
        if process.returncode:
            raise PublicationError(f"{Path(command[0]).name} failed (exit {process.returncode}); see validation logs")
        return output.strip()


def subprocess_environment():
    # Validators retain raw diagnostics internally. Do not pass publisher,
    # GitHub or Azure credentials (or ambient MSBuild Version) to their children.
    # Keep Azure's nonsecret marker for its final orphan-cleanup fallback:
    # https://github.com/microsoft/azure-pipelines-agent/blob/f1a37fb588a921d80b151448151374f5d92dd2a1/src/Agent.Worker/JobExtension.cs#L556-L568
    return {name: os.environ[name] for name in SUBPROCESS_ENVIRONMENT if name in os.environ}


def wait_for_public_assets(client, version, timeout, sleep=time.sleep, clock=time.monotonic):
    deadline = clock() + timeout
    expected = {f"aspire-cli-osx-{arch}-{version}.tar.gz" for arch in ("arm64", "x64")}
    while True:
        remaining = deadline - clock()
        if remaining <= 0:
            raise PublicationError(
                f"Public assets for v{version} are unavailable; publish the draft and verify both macOS "
                "archives, then rerun the Homebrew-only continuation with the same original release run ID"
            )
        client.begin_phase(min(deadline, clock() + 120))
        release = optional_get(client, f"/repos/{SOURCE}/releases/tags/v{version}")
        if release is not None:
            if release["tag_name"] != f"v{version}" or release["prerelease"]:
                raise PublicationError("Homebrew continuation requires a stable release")
            if not release["draft"] and expected <= {asset["name"] for asset in release["assets"]}:
                return
        remaining = deadline - clock()
        print(f"Waiting for public stable release assets for v{version} ({remaining:.0f}s remaining).")
        sleep(max(0, min(10, remaining)))


def verify_source_artifact(data, metadata, build_id, source_commit, version):
    build = metadata["build"]
    artifact = metadata["artifact"]
    candidate_version, _ = identity(data)
    if version != "auto" and candidate_version != version:
        raise PublicationError("Downloaded cask does not match requested release version")
    if build_id <= 0 or not re.fullmatch(r"[0-9a-f]{40}|[0-9a-f]{64}", source_commit):
        raise PublicationError("Exact source build ID and commit are required")
    if build["id"] != build_id or build["sourceVersion"] != source_commit:
        raise PublicationError("Downloaded artifact metadata does not match the selected source run/commit")
    if (build["definition"]["id"] != 1602 or build["project"]["name"] != "internal"
            or build["repository"]["url"] != "https://dev.azure.com/dnceng/internal/_git/microsoft-aspire"):
        raise PublicationError("Homebrew artifact must come from the official microsoft-aspire source build")
    if build["status"] != "completed" or build["result"] not in ("succeeded", "partiallySucceeded"):
        raise PublicationError("Selected Homebrew source build has not completed successfully")
    # Source tags use "release-version - 13.6.4"; multiple release tags are
    # ambiguous even when one happens to match the downloaded cask.
    if not isinstance(build["tags"], list) or any(not isinstance(tag, str) for tag in build["tags"]):
        raise PublicationError("Source build tags must be a list of strings")
    tags = [tag for tag in build["tags"] if tag.startswith("release-version - ")]
    if tags != [f"release-version - {candidate_version}"]:
        raise PublicationError("Source build release tag does not uniquely match the downloaded cask")
    resource = artifact["resource"]
    # 1ES.PublishBuildArtifacts returns Container metadata such as
    # "#/75481081/homebrew-cask-stable", not a PipelineArtifact manifest.
    if (artifact["name"] != "homebrew-cask-stable"
            or type(artifact["id"]) is not int or artifact["id"] <= 0
            or resource["type"].lower() != "container"
            or not re.fullmatch(r"#/[1-9][0-9]*/homebrew-cask-stable", resource["data"])):
        raise PublicationError("Expected the selected build's homebrew-cask-stable container artifact")
    return {
        "build_id": build_id, "definition_id": build["definition"]["id"],
        "source_commit": source_commit, "source_branch": build["sourceBranch"],
        "build_result": build["result"], "artifact_id": artifact["id"],
        "artifact_name": artifact["name"], "container": resource["data"],
        "version": candidate_version,
    }


def validate_release(candidate, version, client, tap_checkout, output, source_commit=None):
    if platform.system() != "Darwin":
        raise PublicationError("Full cask validation requires disposable macOS equipment")
    if not tap_checkout:
        raise PublicationError("--tap-checkout is required for full validation")
    tap = Path(tap_checkout).resolve()
    revision = run(["git", "--no-pager", "-C", str(tap), "rev-parse", "HEAD"])
    if revision != VALIDATOR_REVISION:
        raise PublicationError(f"Tap validator must be pinned to {VALIDATOR_REVISION}")
    run(["git", "--no-pager", "-C", str(tap), "diff", "--exit-code", "HEAD"])
    client.begin_phase()
    release = client.request("GET", f"/repos/{SOURCE}/releases/tags/v{version}")
    if release["tag_name"] != f"v{version}" or release["draft"] or release["prerelease"]:
        raise PublicationError("Full validation requires an already-published stable release")
    reference = client.request("GET", f"/repos/{SOURCE}/git/ref/tags/v{version}")["object"]
    if reference["type"] == "tag":
        reference = client.request("GET", f"/repos/{SOURCE}/git/tags/{reference['sha']}")["object"]
    if reference["type"] != "commit":
        raise PublicationError("Release tag does not resolve to a commit")
    if source_commit is not None and reference["sha"] != source_commit:
        raise PublicationError("Public release tag does not match the selected source build commit")
    template = client.request("GET", f"/repos/{SOURCE}/contents/eng/homebrew/aspire.rb.template?ref={reference['sha']}")
    with tempfile.TemporaryDirectory(prefix="aspire-homebrew-validation-") as work:
        work = Path(work)
        template_file = work / "aspire.rb.template"
        template_file.write_bytes(base64.b64decode(template["content"]))
        variants = []
        for arch in ("arm64", "x64"):
            name = f"aspire-cli-osx-{arch}-{version}.tar.gz"
            url = f"https://github.com/{SOURCE}/releases/download/v{version}/{name}"
            if not any(asset["name"] == name and asset["browser_download_url"] == url for asset in release["assets"]):
                raise PublicationError(f"Published release is missing {name}")
            archive = work / name
            run(["curl", "--fail", "--silent", "--show-error", "--location", "--retry", "2",
                 "--retry-max-time", "120", "--max-time", "60", url, "--output", str(archive)])
            digest = hashlib.sha256()
            with archive.open("rb") as stream:
                for chunk in iter(lambda: stream.read(1024 * 1024), b""):
                    digest.update(chunk)
            variants.append({"url": url, "sha256": digest.hexdigest()})
        metadata = {"casks": [{"version": version, **variants[0], "variations": {"intel": variants[1]}}]}
        metadata_file = work / "metadata.json"
        metadata_file.write_text(json.dumps(metadata))
        # Ripper compares source without executing Ruby. Only a canonical
        # candidate reaches Homebrew, which evaluates the cask DSL.
        run(["ruby", str(tap / "scripts/check-cask-consistency.rb"), str(candidate),
             str(metadata_file), str(template_file)], output / "consistency.log")
        # The released CLI smoke test must not inherit Aspire's global.json:
        # it pins a development SDK that disposable release validators may lack.
        # Retain tap-owned restore evidence outside its disposable workspace.
        run(["bash", str(tap / "scripts/validate-cask.sh"), "--cask-file", str(candidate),
             "--diagnostics-dir", str((output / "validator-diagnostics").resolve())],
            output / "validation.log", cwd=tap,
            env={**subprocess_environment(), "MSBUILDDISABLENODEREUSE": "1"})


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--candidate", required=True, type=Path)
    parser.add_argument("--version", required=True)
    parser.add_argument("--tap-checkout", type=Path)
    parser.add_argument("--output-dir", required=True, type=Path)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--publish", action="store_true", help="Explicit live tap-write opt-in; never used in test pipeline")
    mode.add_argument("--validate-only", action="store_true", help="Fully validate a published fixture, without target reconciliation or writes")
    mode.add_argument("--artifact-check-only", action="store_true", help="Verify downloaded source-build artifact identity without network or writes")
    parser.add_argument("--source-build-metadata", type=Path, help="Build and container-artifact metadata fetched from the selected Azure run")
    parser.add_argument("--source-build-id", type=int)
    parser.add_argument("--source-build-commit")
    parser.add_argument("--wait-for-release-seconds", type=int, default=0,
                        help="Bounded public-asset wait for the release-pipeline continuation (0-600)")
    parser.add_argument("--test-server", help="Loopback-only HTTP fixture; ignores all credentials and never validates a real release")
    args = parser.parse_args(argv)
    args.output_dir.mkdir(parents=True, exist_ok=True)
    source = None
    try:
        if not 0 <= args.wait_for_release_seconds <= 600:
            raise PublicationError("--wait-for-release-seconds must be between 0 and 600")
        source_data = None
        if any(value is not None for value in (args.source_build_metadata, args.source_build_id, args.source_build_commit)):
            if not all(value is not None for value in (args.source_build_metadata, args.source_build_id, args.source_build_commit)):
                raise PublicationError("Source metadata, exact build ID and source commit must be supplied together")
            source_data = args.candidate.read_bytes()
            try:
                source = verify_source_artifact(
                    source_data, json.loads(args.source_build_metadata.read_text()),
                    args.source_build_id, args.source_build_commit, args.version,
                )
            except (KeyError, TypeError, AttributeError) as error:
                raise PublicationError("Malformed source-build provenance; fetch metadata from the selected run again") from error
            args.version = source["version"]
            # Retain the exact downloaded bytes in diagnostics and use this
            # snapshot, not a path another step might subsequently rewrite.
            args.candidate = args.output_dir / "source-candidate.rb"
            args.candidate.write_bytes(source_data)
        if args.artifact_check_only and source is None:
            raise PublicationError("--artifact-check-only requires selected source-build provenance")
        if args.test_server:
            parsed = urllib.parse.urlsplit(args.test_server)
            if parsed.scheme != "http" or not ipaddress.ip_address(parsed.hostname).is_loopback or parsed.path:
                raise PublicationError("--test-server must be an HTTP loopback fixture")
            client = GitHubClient(api_url=args.test_server)
            def validation(*_):
                raise PublicationError("Full validation is not available through a test server; use the callable harness")
            writer = lambda: client
        else:
            client = GitHubClient(os.environ.get("HOMEBREW_READ_TOKEN", ""))
            def validation(candidate, version):
                if source_data is not None and candidate.read_bytes() != source_data:
                    raise PublicationError("Candidate changed after source-artifact verification")
                if args.wait_for_release_seconds:
                    wait_for_public_assets(client, version, args.wait_for_release_seconds)
                validate_release(candidate, version, client, args.tap_checkout, args.output_dir, args.source_build_commit)
            def writer():
                token = os.environ.get("HOMEBREW_TAP_WRITE_TOKEN", "")
                if not token:
                    raise PublicationError("HOMEBREW_TAP_WRITE_TOKEN is required after validation for --publish")
                return GitHubClient(token)
        if args.artifact_check_only:
            result = {"outcome": "artifact_verified", "version": args.version, "publish": False}
        elif args.validate_only:
            with tempfile.TemporaryDirectory(prefix="aspire-homebrew-fixture-") as work:
                snapshot = Path(work) / "aspire.rb"
                data = args.candidate.read_bytes()
                snapshot.write_bytes(data)
                candidate_version, _ = identity(data)
                version = candidate_version if args.version == "auto" else args.version
                if candidate_version != version:
                    raise PublicationError("Published fixture does not match requested version")
                validation(snapshot, version)
                if snapshot.read_bytes() != data:
                    raise PublicationError("Validator changed fixture bytes")
                result = {"outcome": "validated", "version": version, "publish": False}
        else:
            result = publish_cask(args.candidate, args.version, client, validation, args.publish, writer)
        if source is not None:
            result["source"] = source
        (args.output_dir / "result.json").write_text(json.dumps(result, indent=2) + "\n")
        print(json.dumps(result))
        return 0
    except (PublicationError, OSError, ValueError, KeyError, subprocess.TimeoutExpired) as error:
        message = str(error)
        result = {"outcome": "failed", "error": message}
        if source is not None:
            result["source"] = source
        (args.output_dir / "result.json").write_text(json.dumps(result, indent=2) + "\n")
        print(f"Homebrew publication failed: {message}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
