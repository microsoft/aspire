"""Collect bounded, selection-time evidence for the weekly CI audit.

The agent receives this output read-only instead of enumerating Actions data
itself. The collector correlates each PR head with its latest CI selection job,
validates the selection artifact at that exact run attempt, and snapshots the
pre-agent memory ledgers so later validation can reject unsupported edits.
Fork-authored artifacts are parsed only to classify the data gap; they are never
trusted as evidence or allowed to authorize persistent memory changes.
"""

import concurrent.futures
import datetime
import http.client
import io
import json
import os
import pathlib
import re
import time
import unicodedata
import urllib.error
import urllib.parse
import urllib.request
import zipfile

API_ROOT = "https://api.github.com"
ARTIFACT_NAME = "select-tests-selection-Linux"
ARTIFACT_MEMBER = "select-tests-selection.json"
MAX_COMPRESSED_BYTES = 10 * 1024 * 1024
MAX_EXPANDED_BYTES = 1024 * 1024
MAX_PRS = 1000
MAX_RETRY_DELAY_SECONDS = 5 * 60
SECONDARY_RATE_LIMIT_DELAY_SECONDS = 60
TEST_PROJECT_NAME_PATTERN = re.compile(r"^[A-Za-z0-9._-]+$")
JOB_NAME_PATTERN = re.compile(r"^job:[A-Za-z0-9._-]+$")
SHA_PATTERN = re.compile(r"^[0-9a-f]{40}$")
TRANSIENT_NETWORK_ERRORS = (
    urllib.error.URLError,
    http.client.IncompleteRead,
    http.client.RemoteDisconnected,
    ConnectionResetError,
    TimeoutError,
)
TRANSIENT_API_ERRORS = TRANSIENT_NETWORK_ERRORS + (json.JSONDecodeError,)
COLLECTOR_RECORD_ERRORS = (ValueError,) + TRANSIENT_NETWORK_ERRORS

authorization_prefix = bytes((66, 101, 97, 114, 101, 114, 32)).decode("ascii")


# GitHub API transport and bounded retry behavior.


def parse_timestamp(value):
    if not value:
        return None
    return datetime.datetime.fromisoformat(value.replace("Z", "+00:00"))

def parse_nonnegative_integer(value):
    if not isinstance(value, str) or len(value) > 20 or not re.fullmatch(r"[0-9]+", value):
        return None
    return int(value)

def http_retry_delay(error, attempt):
    # Follow GitHub's documented rate-limit precedence without retrying early:
    # https://docs.github.com/rest/using-the-rest-api/rate-limits-for-the-rest-api#exceeding-the-rate-limit
    if error.code >= 500:
        return 2 ** attempt
    if error.code not in (403, 429):
        return None

    headers = error.headers or {}
    retry_after_value = headers.get("Retry-After")
    remaining = headers.get("X-RateLimit-Remaining")
    message_indicates_rate_limit = False
    if error.code == 403 and retry_after_value is None and remaining != "0":
        try:
            message = error.read(8192).lower()
        except OSError:
            message = b""
        message_indicates_rate_limit = b"rate limit" in message or b"abuse detection" in message
    if (
        error.code != 429
        and retry_after_value is None
        and remaining != "0"
        and not message_indicates_rate_limit
    ):
        return None

    delays = []
    retry_after = parse_nonnegative_integer(retry_after_value)
    if retry_after is not None:
        delays.append(max(1, retry_after))
    if remaining == "0":
        reset = parse_nonnegative_integer(headers.get("X-RateLimit-Reset"))
        if reset is not None:
            delays.append(max(1, reset - int(time.time()) + 1))
    if not delays:
        delays.append(SECONDARY_RATE_LIMIT_DELAY_SECONDS * (2 ** attempt))
    delay = max(delays)
    return delay if delay <= MAX_RETRY_DELAY_SECONDS else None

def request(path, parameters=None):
    url = f"{API_ROOT}{path}"
    if parameters:
        url += "?" + urllib.parse.urlencode(parameters)
    for attempt in range(3):
        request_value = urllib.request.Request(
            url,
            headers={
                "Accept": "application/vnd.github+json",
                "Authorization": authorization_prefix + token,
                "X-GitHub-Api-Version": "2022-11-28",
                "User-Agent": "aspire-audit",
            },
        )
        try:
            with urllib.request.urlopen(request_value, timeout=30) as response:
                return json.loads(response.read())
        except urllib.error.HTTPError as error:
            try:
                delay = http_retry_delay(error, attempt)
            finally:
                error.close()
            if delay is None or attempt == 2:
                raise
        except TRANSIENT_API_ERRORS:
            if attempt == 2:
                raise
            delay = 2 ** attempt
        time.sleep(delay)
    raise RuntimeError("unreachable")

def paginate(path, parameters=None, key=None, max_pages=20):
    result = []
    parameters = dict(parameters or {})
    parameters["per_page"] = 100
    for page in range(1, max_pages + 1):
        parameters["page"] = page
        payload = request(path, parameters)
        values = payload[key] if key else payload
        if not isinstance(values, list):
            raise ValueError(f"{path} did not return a list")
        result.extend(values)
        if len(values) < 100:
            return result, False
    return result, True


# Selection artifact validation. Paths and target names are normalized here so
# later agent and memory-validation steps operate on one bounded schema.


def require_safe_string(value, pattern, context, maximum=400):
    if not isinstance(value, str) or not value or len(value) > maximum or not pattern.fullmatch(value):
        raise ValueError(f"{context} is invalid")
    return value

def require_safe_repo_path(value, context, maximum=400):
    invalid_character = (
        isinstance(value, str)
        and any(
            unicodedata.category(character) in ("Cc", "Cf", "Cs", "Zl", "Zp")
            for character in value
        )
    )
    # SelectTests consumes line-delimited git output. Git still C-quotes quotes
    # and backslashes, and the reader trims outer whitespace, so those forms
    # cannot round-trip as literal selector evidence.
    if (
        not isinstance(value, str)
        or not value
        or invalid_character
        or len(value.encode("utf-16-le")) // 2 > maximum
        or value != value.strip()
        or value.startswith("/")
        or '"' in value
        or "\\" in value
        or any(segment in ("", ".", "..") for segment in value.split("/"))
    ):
        raise ValueError(f"{context} is invalid")
    return value

def normalize_path_list(value, context, maximum_items=5000):
    if not isinstance(value, list) or len(value) > maximum_items:
        raise ValueError(f"{context} is not a bounded array")
    result = []
    seen = set()
    for index, item in enumerate(value):
        item = require_safe_repo_path(item, f"{context}[{index}]")
        if item in seen:
            raise ValueError(f"{context} contains duplicate {item}")
        seen.add(item)
        result.append(item)
    return sorted(result)

def normalize_named_items(value, pattern, context):
    if not isinstance(value, list) or len(value) > 5000:
        raise ValueError(f"{context} is not a bounded array")
    result = []
    seen = set()
    for index, item in enumerate(value):
        if not isinstance(item, dict):
            raise ValueError(f"{context}[{index}] is not an object")
        name = require_safe_string(
            item.get("name"), pattern, f"{context}[{index}].name", 200
        )
        if name in seen:
            raise ValueError(f"{context} contains duplicate {name}")
        seen.add(name)
        result.append(name)
    return sorted(result)

def normalize_selection(payload, include_reason):
    if not isinstance(payload, dict) or payload.get("schemaVersion") != 1:
        raise ValueError("selection artifact has an unsupported schema")
    # Audit mode reports the selection that would have been enforced, but the
    # workflow actually runs every test and job. Treating that advisory result
    # as enforced evidence could manufacture false under-selection findings.
    if payload.get("mode") != "enforcing":
        raise ValueError("selection artifact was not produced in enforcing mode")
    inputs = payload.get("inputs")
    if not isinstance(inputs, dict) or not isinstance(inputs.get("changeSource"), str):
        raise ValueError("selection artifact inputs are invalid")
    change_source = inputs["changeSource"]
    diff_match = re.fullmatch(r"git diff ([0-9a-f]{40})\.\.([0-9a-f]{40})", change_source)
    if diff_match:
        source_base_sha, source_head_sha = diff_match.groups()
    elif change_source == "(none -- force-all or unset)":
        source_base_sha = None
        source_head_sha = None
    else:
        raise ValueError("selection artifact changeSource is unsupported")
    selects_all = payload.get("selectsAll")
    if not isinstance(selects_all, bool):
        raise ValueError("selection artifact selectsAll is not boolean")
    reason = None
    if include_reason and payload.get("escalationReason") is not None:
        if not isinstance(payload["escalationReason"], str):
            raise ValueError("selection artifact escalationReason is not a string")
        reason = payload["escalationReason"]
        if len(reason) > 500 or any(ord(character) < 32 for character in reason):
            raise ValueError("selection artifact escalationReason is invalid")
    return {
        "selectsAll": selects_all,
        "sourceHasDiff": diff_match is not None,
        "sourceBaseSha": source_base_sha,
        "sourceHeadSha": source_head_sha,
        "escalationReason": reason,
        "changedFiles": normalize_path_list(payload.get("changedFiles"), "changedFiles"),
        "excludedFiles": normalize_path_list(payload.get("excludedFiles"), "excludedFiles"),
        "unattributedFiles": normalize_path_list(payload.get("unattributedFiles"), "unattributedFiles"),
        "testProjects": normalize_named_items(
            payload.get("testProjects"), TEST_PROJECT_NAME_PATTERN, "testProjects"
        ),
        "jobs": normalize_named_items(payload.get("jobs"), JOB_NAME_PATTERN, "jobs"),
    }

def url_origin(url):
    parts = urllib.parse.urlsplit(url)
    try:
        port = parts.port
    except ValueError:
        return None
    scheme = parts.scheme.lower()
    if port is None:
        port = 443 if scheme == "https" else 80 if scheme == "http" else None
    return scheme, (parts.hostname or "").lower(), port

class ArtifactRedirectHandler(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request_value, file_pointer, code, message, headers, new_url):
        redirected = super().redirect_request(
            request_value, file_pointer, code, message, headers, new_url
        )
        if redirected and url_origin(new_url) != url_origin(API_ROOT):
            redirected.remove_header("Authorization")
        return redirected

def download_selection(artifact_id, include_reason):
    path = f"/repos/{repository}/actions/artifacts/{artifact_id}/zip"
    compressed = None
    for attempt in range(3):
        request_value = urllib.request.Request(
            f"{API_ROOT}{path}",
            headers={
                "Accept": "application/vnd.github+json",
                "Authorization": authorization_prefix + token,
                "X-GitHub-Api-Version": "2022-11-28",
                "User-Agent": "aspire-audit",
            },
        )
        try:
            current = bytearray()
            opener = urllib.request.build_opener(ArtifactRedirectHandler())
            with opener.open(request_value, timeout=30) as response:
                while True:
                    chunk = response.read(65536)
                    if not chunk:
                        break
                    current.extend(chunk)
                    if len(current) > MAX_COMPRESSED_BYTES:
                        raise ValueError("selection artifact exceeds the compressed-byte limit")
            compressed = current
            break
        except urllib.error.HTTPError as error:
            try:
                delay = http_retry_delay(error, attempt)
            finally:
                error.close()
            if delay is None or attempt == 2:
                raise
        except TRANSIENT_NETWORK_ERRORS:
            if attempt == 2:
                raise
            delay = 2 ** attempt
        time.sleep(delay)
    if compressed is None:
        raise RuntimeError("unreachable")
    with zipfile.ZipFile(io.BytesIO(compressed)) as archive:
        members = [entry for entry in archive.infolist() if entry.filename == ARTIFACT_MEMBER]
        if len(members) != 1:
            raise ValueError(f"selection artifact contains {len(members)} exact members")
        member = members[0]
        if member.is_dir() or member.file_size > MAX_EXPANDED_BYTES:
            raise ValueError("selection artifact member exceeds the expanded-byte limit")
        with archive.open(member) as stream:
            expanded = stream.read(MAX_EXPANDED_BYTES + 1)
        if len(expanded) > MAX_EXPANDED_BYTES:
            raise ValueError("selection artifact member exceeded the expanded-byte limit while reading")
    try:
        payload = json.loads(expanded)
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise ValueError("selection artifact member is not valid UTF-8 JSON") from error
    return normalize_selection(payload, include_reason)


# PR scope and pre-agent memory snapshots.


def parse_lookback_days():
    lookback_text = os.environ.get("LOOKBACK_DAYS", "").strip() or "14"
    if not re.fullmatch(r"[1-9][0-9]*", lookback_text):
        raise ValueError("lookback_days must be a positive integer")
    lookback_days = int(lookback_text)
    if lookback_days > 90:
        raise ValueError("lookback_days must not exceed 90")
    return lookback_days


def list_pull_requests(cutoff):
    explicit_text = os.environ.get("PR_NUMBERS", "").strip()
    if explicit_text:
        numbers = []
        for item in explicit_text.split(","):
            item = item.strip()
            if not re.fullmatch(r"[1-9][0-9]*", item):
                raise ValueError(f"invalid PR number {item!r}")
            number = int(item)
            if number not in numbers:
                numbers.append(number)
        if len(numbers) > MAX_PRS:
            raise ValueError(f"pr_numbers must contain at most {MAX_PRS} unique values")
        pull_requests = []
        for number in numbers:
            try:
                pull_requests.append(request(f"/repos/{repository}/pulls/{number}"))
            except Exception as error:
                pull_requests.append({"number": number, "_collectorError": type(error).__name__})
        return pull_requests, False

    if cutoff is None:
        raise ValueError("lookback cutoff is required when pr_numbers is empty")
    pull_requests = []
    truncated = False
    for page in range(1, 11):
        values = request(
            f"/repos/{repository}/pulls",
            {
                "state": "all",
                "sort": "updated",
                "direction": "desc",
                "per_page": 100,
                "page": page,
            },
        )
        if not isinstance(values, list):
            raise ValueError("pull request listing did not return an array")
        for pull_request in values:
            if pull_request.get("state") == "open":
                scope_timestamp = parse_timestamp(pull_request.get("updated_at"))
            else:
                scope_timestamp = max(
                    timestamp
                    for timestamp in (
                        parse_timestamp(pull_request.get("created_at")),
                        parse_timestamp(pull_request.get("closed_at")),
                        parse_timestamp(pull_request.get("merged_at")),
                    )
                    if timestamp is not None
                )
            if scope_timestamp >= cutoff:
                pull_requests.append(pull_request)
        if len(values) < 100:
            break
        if parse_timestamp(values[-1].get("updated_at")) < cutoff:
            break
    else:
        truncated = True
    if len(pull_requests) > MAX_PRS:
        pull_requests = pull_requests[:MAX_PRS]
        truncated = True
    return pull_requests, truncated

def snapshot_file(source_path, destination_path):
    destination_path.parent.mkdir(parents=True, exist_ok=True)
    raw = source_path.read_bytes() if source_path.exists() else b""
    if len(raw) > 2 * 1024 * 1024:
        raise ValueError(f"{source_path.name} exceeds the configured memory limit")
    destination_path.write_bytes(raw)
    destination_path.chmod(0o444)
    return raw

def load_processed_index():
    processed_path = pathlib.Path(os.environ["PROCESSED_RUNS_PATH"])
    raw = snapshot_file(processed_path, baseline_path)
    index = {}
    for line_number, line in enumerate(raw.decode("utf-8").splitlines(), 1):
        if not line:
            continue
        row = json.loads(line)
        if (
            not isinstance(row, dict)
            or not isinstance(row.get("pr"), int)
            or not isinstance(row.get("sha"), str)
            or not SHA_PATTERN.fullmatch(row["sha"])
            or not isinstance(row.get("run"), int)
            or not isinstance(row.get("attempt"), int)
        ):
            raise ValueError(f"processed-runs.jsonl:{line_number} has an invalid identity")
        key = (row["pr"], row["sha"])
        if key in index:
            raise ValueError(f"processed-runs.jsonl:{line_number} duplicates {row['pr']}:{row['sha']}")
        index[key] = (row["run"], row["attempt"])
    return index

def list_changed_files(number):
    values, truncated = paginate(
        f"/repos/{repository}/pulls/{number}/files",
        max_pages=30,
    )
    paths = []
    for index, value in enumerate(values):
        paths.append(require_safe_repo_path(value.get("filename"), f"PR {number} file {index}"))
    return sorted(set(paths)), truncated


# Correlate one PR head with its latest CI run, selection job, and exact
# selection artifact. Every ambiguity becomes an explicit non-creditable status
# rather than a best-effort fallback.


def find_selection_record(pull_request, selection_cutoff=None):
    number = pull_request["number"]
    if "_collectorError" in pull_request:
        return {
            "pr": number,
            "headSha": None,
            "isFork": None,
            "selection": {"status": "collector-error", "error": pull_request["_collectorError"]},
        }
    head_sha = require_safe_string(pull_request["head"]["sha"], SHA_PATTERN, f"PR {number} head SHA", 40)
    head_repo = (pull_request.get("head", {}).get("repo") or {}).get("full_name")
    head_ref = pull_request.get("head", {}).get("ref")
    is_fork = head_repo != repository
    record = {
        "pr": number,
        "headSha": head_sha,
        "isFork": is_fork,
        "selection": {"status": "unresolved"},
    }
    runs, runs_truncated = paginate(
        f"/repos/{repository}/actions/runs",
        {"event": "pull_request", "head_sha": head_sha},
        key="workflow_runs",
        max_pages=5,
    )
    relevant_runs = [
        run for run in runs
        if run.get("path") == ".github/workflows/ci.yml"
        and run.get("head_sha") == head_sha
        and (run.get("head_repository") or {}).get("full_name") == head_repo
        and run.get("head_branch") == head_ref
    ]
    relevant_runs.sort(key=lambda run: (run.get("created_at") or "", run.get("run_attempt") or 0), reverse=True)
    if not relevant_runs:
        record["selection"] = {"status": "no-ci-run", "runsTruncated": runs_truncated}
        return record

    run = relevant_runs[0]
    selection = {
        "status": "unresolved",
        "run": run["id"],
        "attempt": run.get("run_attempt"),
        "runsTruncated": runs_truncated,
    }
    record["selection"] = selection
    if run.get("status") != "completed":
        selection["status"] = "pending"
        return record
    if run.get("conclusion") == "action_required":
        selection["status"] = "action-required"
        return record
    if processed_index.get((number, head_sha)) == (run["id"], run.get("run_attempt")):
        selection["status"] = "recorded"
        return record

    associated, associations_truncated = paginate(
        f"/repos/{repository}/commits/{head_sha}/pulls",
        max_pages=3,
    )
    matching_prs = [
        value for value in associated
        if value.get("head", {}).get("sha") == head_sha
        and value.get("head", {}).get("ref") == head_ref
        and (value.get("head", {}).get("repo") or {}).get("full_name") == head_repo
    ]
    if associations_truncated or len(matching_prs) != 1 or matching_prs[0].get("number") != number:
        selection["status"] = "pr-attribution-ambiguous"
        return record

    changed_files, files_truncated = list_changed_files(number)
    record["changedFilesFromGitHub"] = changed_files
    record["filesTruncated"] = files_truncated
    jobs, jobs_truncated = paginate(
        f"/repos/{repository}/actions/runs/{run['id']}/jobs",
        {"filter": "latest"},
        key="jobs",
        max_pages=10,
    )
    selection_jobs = [
        job for job in jobs
        if job.get("name", "").endswith("Tests / Setup for tests")
        and job.get("run_attempt") == run.get("run_attempt")
    ]
    if len(selection_jobs) != 1:
        selection["status"] = "selection-job-ambiguous" if selection_jobs else "no-selection-job"
        selection["jobsTruncated"] = jobs_truncated
        return record
    job = selection_jobs[0]
    selection["jobsTruncated"] = jobs_truncated
    if job.get("status") != "completed":
        selection["status"] = "pending"
        return record
    steps = [step for step in job.get("steps", []) if step.get("name") == "Select relevant tests"]
    if len(steps) != 1 or steps[0].get("conclusion") != "success":
        selection["status"] = "selection-step-failed"
        return record
    select_started = parse_timestamp(steps[0].get("started_at"))
    job_completed = parse_timestamp(job.get("completed_at"))

    artifacts, artifacts_truncated = paginate(
        f"/repos/{repository}/actions/runs/{run['id']}/artifacts",
        key="artifacts",
        max_pages=20,
    )
    candidates = []
    for artifact in artifacts:
        workflow_run = artifact.get("workflow_run") or {}
        created_at = parse_timestamp(artifact.get("created_at"))
        if (
            artifact.get("name") == ARTIFACT_NAME
            and workflow_run.get("id") == run["id"]
            and workflow_run.get("head_sha") == head_sha
            and created_at is not None
            and select_started is not None
            and job_completed is not None
            and select_started <= created_at <= job_completed
        ):
            candidates.append(artifact)
    selection["artifactsTruncated"] = artifacts_truncated
    if len(candidates) != 1:
        selection["status"] = "artifact-ambiguous" if candidates else "artifact-missing"
        return record
    artifact = candidates[0]
    artifact_created_at = parse_timestamp(artifact.get("created_at"))
    selection["artifactCreatedAt"] = artifact_created_at.isoformat()
    if selection_cutoff is not None and artifact_created_at < selection_cutoff:
        selection["status"] = "selection-outside-lookback"
        return record
    if artifact.get("expired"):
        selection["status"] = "artifact-expired"
        return record
    try:
        normalized = download_selection(artifact["id"], include_reason=not is_fork)
    except Exception as error:
        selection["status"] = "artifact-invalid"
        selection["error"] = type(error).__name__
        return record

    source_head_matches = (
        normalized["sourceHeadSha"] == head_sha
        or (
            normalized["sourceHeadSha"] is None
            and normalized["selectsAll"]
            and not normalized["sourceHasDiff"]
        )
    )
    selection["creditable"] = not is_fork and source_head_matches
    if is_fork:
        selection["status"] = "untrusted-fork-artifact"
        return record
    elif not source_head_matches:
        selection["status"] = "artifact-head-mismatch"
    else:
        selection["status"] = "resolved"
    selection["result"] = normalized
    return record

def collect_selection_record(pull_request, selection_cutoff=None):
    try:
        return find_selection_record(pull_request, selection_cutoff)
    except COLLECTOR_RECORD_ERRORS as error:
        head = pull_request.get("head") or {}
        head_repo = (head.get("repo") or {}).get("full_name")
        return {
            "pr": pull_request.get("number"),
            "headSha": head.get("sha") if SHA_PATTERN.fullmatch(head.get("sha") or "") else None,
            "isFork": head_repo != repository,
            "selection": {
                "status": "collector-error",
                "error": type(error).__name__,
            },
        }


# Entrypoint: collect heads concurrently, then publish one immutable evidence
# file that the agent can read but cannot modify.


def main():
    global token
    global repository
    global output_path
    global baseline_path
    global watchlist_baseline_path
    global processed_index

    token = os.environ["GH_TOKEN"]
    repository = os.environ["REPOSITORY"]
    output_path = pathlib.Path(os.environ["OUTPUT_PATH"])
    baseline_path = pathlib.Path(os.environ["PROCESSED_BASELINE_PATH"])
    watchlist_baseline_path = pathlib.Path(os.environ["WATCHLIST_BASELINE_PATH"])

    audit_date = pathlib.Path(os.environ["AUDIT_DATE_PATH"]).read_text(encoding="utf-8").strip()
    explicit_scope = bool(os.environ.get("PR_NUMBERS", "").strip())
    if explicit_scope:
        selection_cutoff = None
    else:
        lookback_days = parse_lookback_days()
        selection_cutoff = (
            datetime.datetime.fromisoformat(audit_date).replace(tzinfo=datetime.timezone.utc)
            - datetime.timedelta(days=lookback_days)
        )

    pull_requests, enumeration_truncated = list_pull_requests(selection_cutoff)
    processed_index = load_processed_index()
    snapshot_file(pathlib.Path(os.environ["WATCHLIST_PATH"]), watchlist_baseline_path)
    with concurrent.futures.ThreadPoolExecutor(max_workers=8) as executor:
        records = list(
            executor.map(
                lambda pull_request: collect_selection_record(pull_request, selection_cutoff),
                pull_requests,
            )
        )
    output = {
        "schemaVersion": 1,
        "auditDate": audit_date,
        "generatedAt": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "repository": repository,
        "enumerationTruncated": enumeration_truncated,
        "records": records,
    }
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text(json.dumps(output, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    output_path.chmod(0o444)
    print(f"Wrote {len(records)} records to {output_path}")


if __name__ == "__main__":
    main()
