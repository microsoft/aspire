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
VALIDATOR_REVISION = "f17f51b14acbf17f6a29db1cae91a8f92fe63369"
PREFIX = f"/repos/{TAP}"


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
            headers = {"Accept": "application/vnd.github+json", "X-GitHub-Api-Version": "2022-11-28"}
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


def run(command, log=None):
    process = subprocess.run(command, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=1800)
    if log:
        log.write_text(process.stdout)
    print(process.stdout, end="")
    if process.returncode:
        raise PublicationError(f"{Path(command[0]).name} failed (exit {process.returncode}); see validation logs")
    return process.stdout.strip()


def validate_release(candidate, version, client, tap_checkout, output):
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
        run(["bash", str(tap / "scripts/validate-cask.sh"), "--cask-file", str(candidate)], output / "validation.log")


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--candidate", required=True, type=Path)
    parser.add_argument("--version", required=True)
    parser.add_argument("--tap-checkout", type=Path)
    parser.add_argument("--output-dir", required=True, type=Path)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--publish", action="store_true", help="Explicit live tap-write opt-in; never used in test pipeline")
    mode.add_argument("--validate-only", action="store_true", help="Fully validate a published fixture, without target reconciliation or writes")
    parser.add_argument("--test-server", help="Loopback-only HTTP fixture; ignores all credentials and never validates a real release")
    args = parser.parse_args(argv)
    args.output_dir.mkdir(parents=True, exist_ok=True)
    try:
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
            validation = lambda candidate, version: validate_release(candidate, version, client, args.tap_checkout, args.output_dir)
            def writer():
                token = os.environ.get("HOMEBREW_TAP_WRITE_TOKEN", "")
                if not token:
                    raise PublicationError("HOMEBREW_TAP_WRITE_TOKEN is required after validation for --publish")
                return GitHubClient(token)
        if args.validate_only:
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
        (args.output_dir / "result.json").write_text(json.dumps(result, indent=2) + "\n")
        print(json.dumps(result))
        return 0
    except (PublicationError, OSError, ValueError, KeyError, subprocess.TimeoutExpired) as error:
        message = str(error)
        (args.output_dir / "result.json").write_text(json.dumps({"outcome": "failed", "error": message}, indent=2) + "\n")
        print(f"Homebrew publication failed: {message}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
