#!/usr/bin/env python3

import copy
import datetime
import json
import math
import pathlib
import re
import stat
import sys
import urllib.parse
import zipfile


sys.stdout.reconfigure(encoding="utf-8")
sys.stderr.reconfigure(encoding="utf-8")


VALID_VERDICTS = {
    "transient-infra",
    "flaky-test",
    "code-issue",
    "pr-test-failure",
    "mixed",
    "unknown",
}
RETRYABLE_VERDICTS = {"transient-infra", "flaky-test", "mixed", "unknown"}
MAX_TEST_RESULTS_ARTIFACT_BYTES = 100 * 1024 * 1024
MAX_FILES = 200
MAX_FILE_BYTES = 50 * 1024 * 1024
MAX_TOTAL_BYTES = 500 * 1024 * 1024
CHUNK_BYTES = 1024 * 1024

REDACTED_FIELD_LIMITS = {
    "error": 1000,
    "stack_trace": 2000,
    "standard_output": 4000,
    "standard_error": 4000,
}


class ExtractionLimitExceeded(Exception):
    pass


def escape_html(value):
    return (
        str(value if value is not None else "")
        .replace("&", "&amp;")
        .replace("<", "&lt;")
        .replace(">", "&gt;")
        .replace('"', "&quot;")
        .replace("'", "&#39;")
    )


def redact_sensitive_data(value):
    text = str(value if value is not None else "")
    substitutions = [
        (
            re.compile(
                r"-----BEGIN ([A-Z ]*PRIVATE KEY)-----[\s\S]*?(?:-----END \1-----|$)"
            ),
            "[REDACTED]",
        ),
        (
            re.compile(
                r"\b(authorization|proxy-authorization)(\s*:\s*)(basic|bearer)\s+[^\s,;]+",
                re.IGNORECASE,
            ),
            r"\1\2\3 [REDACTED]",
        ),
        (
            re.compile(
                r"\b(x-api-key|api-key|access-token|client-secret)(\s*:\s*)[^\s,;]+",
                re.IGNORECASE,
            ),
            r"\1\2[REDACTED]",
        ),
        (
            re.compile(r"\b([A-Za-z][A-Za-z0-9+.-]*://)[^\s/:@]*:[^\s/@]+@"),
            r"\1[REDACTED]:[REDACTED]@",
        ),
        (
            re.compile(r"\b([A-Za-z][A-Za-z0-9+.-]*://)[^\s/:@]+@"),
            r"\1[REDACTED]@",
        ),
        (
            re.compile(
                r"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b"
            ),
            "[REDACTED]",
        ),
        (
            re.compile(
                r"\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|AKIA[A-Z0-9]{16}|(?:npm|pypi)-[A-Za-z0-9_-]{20,})\b"
            ),
            "[REDACTED]",
        ),
        (
            re.compile(
                r"([?&](?:sig|signature|token|access_token|api[_-]?key|password|secret|client_secret)=)[^&\s]+",
                re.IGNORECASE,
            ),
            r"\1[REDACTED]",
        ),
        (
            re.compile(
                r"([\"'](?:password|passwd|pwd|token|pgpassword|_?authToken|_?auth|accessToken|refreshToken|api[_-]?key|access[_-]?key|account[_-]?key|secret|client[_-]?secret|connection[_-]?string)[\"']\s*:\s*)([\"'])(?:\\[^\r\n]|(?!\2)[^\\\r\n])*\2",
                re.IGNORECASE,
            ),
            r"\1\2[REDACTED]\2",
        ),
        (
            re.compile(
                r"((?:^|\s)--(?:password|passwd|pwd|token|auth[_-]?token|access[_-]?token|refresh[_-]?token|api[_-]?key|access[_-]?key|account[_-]?key|secret|client[_-]?secret|connection[_-]?string|sharedaccesskey|sharedaccesssignature|signature|private[_-]?key)\s+)([\"'])(?:\\[^\r\n]|(?!\2)[^\\\r\n])*\2",
                re.IGNORECASE | re.MULTILINE,
            ),
            r"\1\2[REDACTED]\2",
        ),
        (
            re.compile(
                r"((?:^|[^\S\r\n])--(?:password|passwd|pwd|token|auth[_-]?token|access[_-]?token|refresh[_-]?token|api[_-]?key|access[_-]?key|account[_-]?key|secret|client[_-]?secret|connection[_-]?string|sharedaccesskey|sharedaccesssignature|signature|private[_-]?key)[^\S\r\n]+)(?![\"'])([^\s]+)",
                re.IGNORECASE | re.MULTILINE,
            ),
            r"\1[REDACTED]",
        ),
        (
            re.compile(
                r"\b((?:[A-Za-z][A-Za-z0-9_.-]*[_-])?(?:password|passwd|pwd|token|api[_-]?key|access[_-]?key|account[_-]?key|secret|client[_-]?secret|sharedaccesskey|sharedaccesssignature|signature|private[_-]?key)|pgpassword|_?authToken|_?auth|accessToken|refreshToken)(\s*[:=]\s*)([\"'])(?:\\[^\r\n]|(?!\3)[^\\\r\n])*\3",
                re.IGNORECASE,
            ),
            r"\1\2\3[REDACTED]\3",
        ),
        (
            re.compile(
                r"\b((?:[A-Za-z][A-Za-z0-9_.-]*[_-])?(?:password|passwd|pwd|token|api[_-]?key|access[_-]?key|account[_-]?key|secret|client[_-]?secret|sharedaccesskey|sharedaccesssignature|signature|private[_-]?key)|pgpassword|_?authToken|_?auth|accessToken|refreshToken)(\s*[:=]\s*)(?![\"'])([^;\r\n]+)",
                re.IGNORECASE,
            ),
            r"\1\2[REDACTED]",
        ),
    ]

    for pattern, replacement in substitutions:
        text = pattern.sub(replacement, text)

    return text


def redact_json(value, property_name=None):
    if isinstance(value, str):
        redacted = redact_sensitive_data(value)
        limit = REDACTED_FIELD_LIMITS.get(property_name)
        return redacted[:limit] if limit else redacted
    if isinstance(value, list):
        return [redact_json(item, property_name) for item in value]
    if isinstance(value, dict):
        return {key: redact_json(item, key) for key, item in value.items()}
    return value


def _to_number(value):
    if value is None:
        return 0
    if isinstance(value, bool):
        return int(value)
    try:
        return float(value)
    except (TypeError, ValueError):
        return math.nan


def _to_integer(value):
    number = _to_number(value)
    return int(number) if math.isfinite(number) and number.is_integer() else None


def _json_deep_equal(left, right):
    if type(left) is not type(right):
        return False
    if isinstance(left, dict):
        return left.keys() == right.keys() and all(
            _json_deep_equal(left[key], right[key]) for key in left
        )
    if isinstance(left, list):
        return len(left) == len(right) and all(
            _json_deep_equal(left_item, right_item)
            for left_item, right_item in zip(left, right)
        )
    return left == right


def validate_publication(analysis, trusted_context, trusted_evidence):
    analysis_pr = analysis.get("pr") if isinstance(analysis.get("pr"), dict) else {}
    analysis_rerun = (
        analysis.get("rerun") if isinstance(analysis.get("rerun"), dict) else {}
    )
    manifest = {
        "run_id": _to_integer(trusted_context.get("run_id")),
        "run_attempt": _to_integer(trusted_context.get("run_attempt")),
        "run_url": trusted_context.get("run_url"),
        "run_event": trusted_context.get("run_event"),
        "analyzed_commit_sha": trusted_context.get("analyzed_commit_sha"),
        "pr_number": _to_integer(trusted_context.get("pr_number")),
        "pr_state": trusted_context.get("pr_state"),
        "dry_run": trusted_context.get("dry_run") is True,
        "verdict": analysis.get("verdict"),
        "rerun_eligible": analysis_rerun.get("eligible"),
    }

    if (
        manifest["run_id"] is None
        or manifest["run_attempt"] is None
        or manifest["pr_number"] is None
        or _to_number(analysis.get("run_id")) != manifest["run_id"]
        or _to_number(analysis.get("run_attempt")) != manifest["run_attempt"]
        or analysis.get("run_url") != manifest["run_url"]
        or analysis.get("run_event") != manifest["run_event"]
        or analysis.get("analyzed_commit_sha") != manifest["analyzed_commit_sha"]
        or _to_number(analysis_pr.get("number")) != manifest["pr_number"]
        or analysis_pr.get("state") != manifest["pr_state"]
        or not _json_deep_equal(analysis.get("evidence"), trusted_evidence)
    ):
        raise ValueError(
            "Agent analysis does not match the collected run, analyzed commit, or evidence metadata."
        )

    if manifest["verdict"] not in VALID_VERDICTS:
        raise ValueError(f"Invalid analysis verdict '{manifest['verdict']}'.")
    if not isinstance(manifest["rerun_eligible"], bool):
        raise ValueError("Analysis result must contain a boolean rerun.eligible decision.")

    expected_rerun_eligible = (
        manifest["run_event"] == "pull_request"
        and manifest["verdict"] in RETRYABLE_VERDICTS
        and manifest["run_attempt"] <= 3
        and manifest["pr_state"] == "open"
    )
    if manifest["rerun_eligible"] != expected_rerun_eligible:
        raise ValueError(
            f"Rerun decision conflicts with run event '{manifest['run_event']}' "
            f"and verdict '{manifest['verdict']}'."
        )

    return manifest


def _parse_timestamp(value):
    try:
        return datetime.datetime.fromisoformat(str(value).replace("Z", "+00:00")).timestamp()
    except (TypeError, ValueError):
        return math.nan


def select_test_results_artifact(artifacts):
    if not isinstance(artifacts, list):
        return None

    candidates = [
        artifact
        for artifact in artifacts
        if isinstance(artifact, dict)
        and artifact.get("name") == "All-TestResults"
        and artifact.get("expired") is not True
    ]
    candidates.sort(
        key=lambda artifact: (
            _parse_timestamp(artifact.get("created_at")),
            _to_number(artifact.get("id")),
        ),
        reverse=True,
    )
    selected = candidates[0] if candidates else None
    return (
        selected
        if selected
        and _to_number(selected.get("size_in_bytes"))
        <= MAX_TEST_RESULTS_ARTIFACT_BYTES
        else None
    )


def validate_rerun_request(analysis, trusted_context, trusted_evidence, request):
    manifest = validate_publication(analysis, trusted_context, trusted_evidence)
    requested_run_id = _to_number(request.get("run_id"))
    requested_pr_numbers = [
        number
        for value in str(request.get("pr_numbers") or "").split(",")
        if (number := _to_number(value)) > 0
    ]
    requested_reason = str(request.get("reason") or "")

    if (
        requested_run_id != manifest["run_id"]
        or len(requested_pr_numbers) != 1
        or requested_pr_numbers[0] != manifest["pr_number"]
    ):
        raise ValueError("Rerun request does not match the analyzed run and pull request.")

    analysis_rerun = (
        analysis.get("rerun") if isinstance(analysis.get("rerun"), dict) else {}
    )
    rerun_reason = analysis_rerun.get("reason")
    if (
        not manifest["rerun_eligible"]
        or not isinstance(rerun_reason, str)
        or not rerun_reason
        or requested_reason != rerun_reason
        or manifest["verdict"] not in RETRYABLE_VERDICTS
    ):
        raise ValueError(
            f"Rerun request conflicts with analysis verdict '{manifest['verdict']}'."
        )

    return manifest


def _get_trx_text(value):
    if value is None:
        return ""
    if isinstance(value, dict):
        return str(value.get("+content") or "")
    return str(value)


def extract_test_failures(trx):
    test_run = trx.get("TestRun") if isinstance(trx, dict) else None
    results = test_run.get("Results") if isinstance(test_run, dict) else None
    unit_test_results = (
        results.get("UnitTestResult") if isinstance(results, dict) else []
    )
    if unit_test_results is None:
        unit_test_results = []
    result_list = (
        unit_test_results if isinstance(unit_test_results, list) else [unit_test_results]
    )

    failures = []
    for result in result_list:
        if not isinstance(result, dict) or result.get("+@outcome") != "Failed":
            continue
        output = result.get("Output") if isinstance(result.get("Output"), dict) else {}
        error_info = (
            output.get("ErrorInfo")
            if isinstance(output.get("ErrorInfo"), dict)
            else {}
        )
        failures.append(
            {
                "test": str(result.get("+@testName") or ""),
                "error": _get_trx_text(error_info.get("Message")),
                "stack_trace": _get_trx_text(error_info.get("StackTrace")),
                "standard_output": _get_trx_text(output.get("StdOut")),
                "standard_error": _get_trx_text(output.get("StdErr")),
            }
        )
    return failures


def extract_mocha_failures(report, job_name):
    failures = report.get("failures") if isinstance(report, dict) else []
    failures = failures if isinstance(failures, list) else []
    extracted = []
    for failure in failures:
        error = failure.get("err") if isinstance(failure.get("err"), dict) else {}
        item = {
            "test": str(failure.get("fullTitle") or failure.get("title") or ""),
            "error": str(error.get("message") or ""),
            "stack_trace": str(error.get("stack") or ""),
            "standard_output": "",
            "standard_error": "",
        }
        if job_name:
            item["job"] = job_name
        extracted.append(item)
    return extracted


def to_inline_code(value):
    normalized = re.sub(r"\r\n?|\n", " ", str(value))
    longest_run = max((len(run) for run in re.findall(r"`+", normalized)), default=0)
    fence = "`" * (longest_run + 1)
    padding = " " if normalized.startswith("`") or normalized.endswith("`") else ""
    return f"{fence}{padding}{normalized}{padding}{fence}"


def to_code_block(value):
    content = str(value if value is not None else "")
    longest_run = max((len(run) for run in re.findall(r"`+", content)), default=0)
    fence = "`" * max(3, longest_run + 1)
    trailing_newline = "" if content.endswith("\n") else "\n"
    return f"{fence}\n{content}{trailing_newline}{fence}"


def format_test_failures(failures):
    output = []
    for failure in failures:
        sections = [f"### {to_inline_code(failure.get('test', ''))}"]
        if failure.get("job"):
            sections.extend(["", f"**Job:** {to_inline_code(failure['job'])}"])
        sections.extend(["", "**Error:**", to_code_block(failure.get("error"))])
        for label, key in [
            ("Stack Trace", "stack_trace"),
            ("Standard Output (sensitive values redacted)", "standard_output"),
            ("Standard Error (sensitive values redacted)", "standard_error"),
        ]:
            if failure.get(key):
                sections.extend([f"**{label}:**", to_code_block(failure[key])])
        output.append("\n".join(sections))
    return "\n".join(output) + ("\n" if output else "")


def get_cause_job_name(analysis, cause):
    failed_jobs = analysis.get("failed_jobs") or []
    first_job = failed_jobs[0] if failed_jobs else {}
    return cause.get("job_name") or first_job.get("name") or "unknown"


def get_observed_at(analysis):
    analyzed_at = analysis.get("analyzed_at")
    if isinstance(analyzed_at, str) and math.isfinite(_parse_timestamp(analyzed_at)):
        return analyzed_at
    return datetime.datetime.now(datetime.timezone.utc).isoformat(timespec="milliseconds").replace(
        "+00:00", "Z"
    )


def build_occurrence(analysis, cause):
    pull_request = analysis.get("pr") if isinstance(analysis.get("pr"), dict) else {}
    return {
        "run_id": analysis.get("run_id"),
        "run_url": analysis.get("run_url") or "",
        "job": get_cause_job_name(analysis, cause),
        "pr_number": pull_request.get("number") or 0,
        "observed_at": get_observed_at(analysis),
    }


def add_occurrence(analysis, cause):
    result = copy.deepcopy(cause)
    result["occurrences"] = [build_occurrence(analysis, cause)]
    return redact_json(result)


def build_occurrence_row(analysis, cause):
    occurrence = build_occurrence(analysis, cause)
    date = occurrence["observed_at"].split("T", 1)[0]
    return (
        f"| {date} | [{occurrence['run_id']}]({occurrence['run_url']}) | "
        f"{occurrence['job']} | #{occurrence['pr_number']} |"
    )


def normalize_issue_title(value):
    return re.sub(r"\r\n?|\n", " ", redact_sensitive_data(value)).strip()


def _get_validated_job_url(run_url, job_url):
    try:
        run = urllib.parse.urlsplit(run_url)
        job = urllib.parse.urlsplit(job_url)
        if (
            run.scheme == "https"
            and run.hostname == "github.com"
            and run.username is None
            and run.password is None
            and job.scheme == run.scheme
            and job.netloc == run.netloc
            and job.username is None
            and job.password is None
            and not job.query
            and not job.fragment
            and re.fullmatch(re.escape(run.path) + r"/job/\d+", job.path)
        ):
            return urllib.parse.urlunsplit(job)
    except (TypeError, ValueError):
        pass
    return ""


def _build_job_list(analysis, classification=None):
    jobs = []
    for job in analysis.get("failed_jobs") or []:
        if classification and job.get("classification") != classification:
            continue
        if not classification:
            jobs.append(
                f"- {to_inline_code(job.get('name'))} — {job.get('reason') or ''} "
                f"({job.get('classification') or ''})"
            )
            continue
        validated_job_url = _get_validated_job_url(
            analysis.get("run_url"), job.get("url")
        )
        job_link = f" ([job]({validated_job_url}))" if validated_job_url else ""
        jobs.append(
            f"- {to_inline_code(job.get('name'))}{job_link}\n"
            f"  - **Why likely flaky**: {job.get('reason') or ''}"
        )
    return "\n".join(jobs)


def _build_flaky_test_list(analysis):
    tests = []
    for test in analysis.get("failed_tests") or []:
        if test.get("classification") != "flaky":
            continue
        stack_trace = ""
        if test.get("stack_trace"):
            frames = "\n".join(test["stack_trace"].split("\n")[:5])
            stack_trace = f"\n  - **Stack Trace** (first frames):\n{to_code_block(frames)}"
        tests.append(
            f"- {to_inline_code(test.get('name'))} in job {to_inline_code(test.get('job'))}\n"
            f"  - **Error**: {test.get('error') or ''}{stack_trace}\n"
            f"  - **Why likely flaky**: {test.get('reason') or ''}"
        )
    return "\n".join(tests)


def _build_evidence_note(analysis):
    evidence = analysis.get("evidence") if isinstance(analysis.get("evidence"), dict) else {}
    if evidence.get("completeness") != "partial":
        return ""
    gaps = evidence.get("gaps")
    gap_list = (
        "\n".join(f"- {to_inline_code(gap)}" for gap in gaps)
        if isinstance(gaps, list)
        else ""
    )
    return f"\n\n**Evidence completeness:** Partial{f'{chr(10)}{gap_list}' if gap_list else ''}"


def _build_rerun_note(analysis, dry_run):
    rerun = analysis.get("rerun") if isinstance(analysis.get("rerun"), dict) else {}
    if rerun.get("eligible") is True:
        if dry_run:
            return "\n\nThe analysis requested an automatic rerun, but this manual dry run suppressed the rerun request."
        return "\n\nThe analysis requested an automatic rerun of the failed CI jobs."
    return "\n\nThe CI will not be automatically rerun."


def build_pr_comment(analysis, dry_run=False):
    marker = "<!-- analyze-ci-failure -->"
    run_url = analysis.get("run_url") or ""
    all_jobs = _build_job_list(analysis)
    evidence_note = _build_evidence_note(analysis)
    rerun_note = _build_rerun_note(analysis, dry_run)
    verdict = analysis.get("verdict")

    if verdict == "transient-infra":
        return f"{marker}\n🔍 **CI Failure Analysis: Transient Infrastructure Failure**\n\nThe CI build failed due to transient infrastructure issues.\n\n**Failed jobs:**\n{all_jobs}{evidence_note}{rerun_note}\n\n[View the workflow run]({run_url}).\n"
    if verdict == "flaky-test":
        flaky_tests = _build_flaky_test_list(analysis)
        has_flaky_tests = bool(flaky_tests)
        heading = "Suspected flaky test(s)" if has_flaky_tests else "Suspected flaky failure(s)"
        failures = flaky_tests if has_flaky_tests else _build_job_list(analysis, "flaky-test")
        return f"{marker}\n⚠️ **CI Failure Analysis: Possible Flaky Test(s)**\n\nThe CI build failed due to test failure(s) that appear unrelated to the PR changes. These may be flaky tests.\n\n**{heading}:**\n{failures}{evidence_note}{rerun_note}\n\n**Suggested actions:**\n- If the test continues to fail, consider [quarantining it](https://github.com/microsoft/aspire/blob/main/docs/quarantined-tests.md) using `/quarantine-test <test name> <issue URL>`\n- Search [existing issues](https://github.com/microsoft/aspire/issues?q=is%3Aissue+label%3Atest-failure) to see if this test is already known to be flaky\n\n[View the workflow run]({run_url}).\n"
    if verdict == "code-issue":
        return f"{marker}\n❌ **CI Failure Analysis: Code Issue Detected**\n\nThe CI build failed due to issue(s) caused by changes in this PR.\n\n**Failed jobs:**\n{all_jobs}{evidence_note}\n\nThe CI will not be automatically rerun. Please fix the issue and push an updated commit.\n"
    if verdict == "pr-test-failure":
        return f"{marker}\n❌ **CI Failure Analysis: Test Regression Detected**\n\nThe CI build contains test failure(s) caused by changes in this PR.\n\n**Failed jobs:**\n{all_jobs}{evidence_note}\n\nThe CI will not be automatically rerun. Please fix the failing behavior or update the affected test, then push an updated commit.\n"
    if verdict == "unknown":
        return f"{marker}\n❓ **CI Failure Analysis: Unable to Classify**\n\nThe available evidence was insufficient to determine whether this failure is transient or caused by the PR.\n\n**Failed jobs:**\n{all_jobs}{evidence_note}{rerun_note}\n\nReview the [workflow run logs]({run_url}) if the next attempt fails.\n"
    return f"{marker}\n⚠️ **CI Failure Analysis: Mixed Failures**\n\nThe CI build contains both transient and non-transient failures.\n\n**Failed jobs:**\n{all_jobs}{evidence_note}{rerun_note}\n\nPlease review the failures above.\n"


def _get_failure_information(analysis, cause):
    job_name = get_cause_job_name(analysis, cause)
    if cause.get("type") == "flaky-test":
        failed_test = next(
            (
                test
                for test in analysis.get("failed_tests") or []
                if test.get("name") == cause.get("test_name")
                and test.get("job") == job_name
            ),
            {},
        )
        details = "\n\n".join(
            f"{label}:\n{failed_test[key]}"
            for label, key in [
                ("Error", "error"),
                ("Stack Trace", "stack_trace"),
                ("Standard Output", "standard_output"),
                ("Standard Error", "standard_error"),
            ]
            if failed_test.get(key)
        )
        return {
            "classification_analysis": failed_test.get("reason")
            or cause.get("analysis")
            or "",
            "details": details
            or cause.get("failure_details")
            or cause.get("error_pattern")
            or "",
        }

    failed_job = next(
        (
            job
            for job in analysis.get("failed_jobs") or []
            if job.get("name") == job_name
        ),
        {},
    )
    return {
        "classification_analysis": failed_job.get("reason")
        or cause.get("analysis")
        or "",
        "details": cause.get("failure_details") or cause.get("error_pattern") or "",
    }


def build_issue_body(analysis, cause, marker):
    job_name = get_cause_job_name(analysis, cause)
    failure_information = _get_failure_information(analysis, cause)
    title = normalize_issue_title(cause.get("title"))
    test_name = cause.get("test_name") or ""
    output_summary = "Test output" if cause.get("type") == "flaky-test" else "Job output snippet"
    build_error = (
        f"Build error leg or test failing: {job_name} / {to_inline_code(test_name)}"
        if test_name
        else f"Build error leg: {job_name}"
    )
    pull_request = analysis.get("pr") if isinstance(analysis.get("pr"), dict) else {}
    return f"""{marker}

## Build Information

Build: {analysis.get('run_url') or ''}
{build_error}
Pull request: #{pull_request.get('number') or 0}

## Classification Analysis

<pre>
{escape_html(redact_sensitive_data(failure_information['classification_analysis']))}
</pre>

## Failure Information

<details>
<summary>{output_summary}</summary>

<pre>
{escape_html(redact_sensitive_data(failure_information['details']))}
</pre>

</details>

## Description

<pre>
{escape_html(title)}
</pre>

**Type**: {cause.get('type')}

## Occurrences

| Date | Build | Job | PR |
|------|-------|-----|----|
{build_occurrence_row(analysis, cause)}
"""


def _is_unsafe_entry(entry):
    relative_path = pathlib.PurePosixPath(entry.filename)
    unix_mode = entry.external_attr >> 16
    return (
        relative_path.is_absolute()
        or ".." in relative_path.parts
        or "\\" in entry.filename
        or stat.S_ISLNK(unix_mode)
    )


def _copy_bounded(source, output, max_file_bytes, max_total_bytes):
    written = 0
    while chunk := source.read(min(CHUNK_BYTES, max_file_bytes + 1)):
        written += len(chunk)
        if written > max_file_bytes or written > max_total_bytes:
            raise ExtractionLimitExceeded
        output.write(chunk)
    return written


def _format_size(byte_count):
    megabyte = 1024 * 1024
    if byte_count % megabyte == 0:
        return f"{byte_count // megabyte} MB"
    return f"{byte_count} bytes"


def extract_test_results(
    archive_path,
    destination_path,
    evidence_gaps_path,
    max_files=MAX_FILES,
    max_file_bytes=MAX_FILE_BYTES,
    max_total_bytes=MAX_TOTAL_BYTES,
):
    destination = pathlib.Path(destination_path).resolve()
    destination.mkdir(parents=True, exist_ok=True)
    total_bytes = 0

    with zipfile.ZipFile(archive_path) as archive:
        trx_entries = sorted(
            (
                entry
                for entry in archive.infolist()
                if not entry.is_dir() and entry.filename.lower().endswith(".trx")
            ),
            key=lambda entry: entry.filename,
        )

        with open(evidence_gaps_path, "a", encoding="utf-8") as evidence_gaps:
            if len(trx_entries) > max_files:
                evidence_gaps.write(
                    f"Test results artifact contained {len(trx_entries)} TRX files; "
                    f"processing only the first {max_files}\n"
                )

            for entry in trx_entries[:max_files]:
                relative_path = pathlib.PurePosixPath(entry.filename)
                if _is_unsafe_entry(entry):
                    evidence_gaps.write(
                        f"Skipped unsafe test result path: {entry.filename}\n"
                    )
                    continue
                if entry.file_size > max_file_bytes:
                    evidence_gaps.write(
                        f"Skipped test result larger than {_format_size(max_file_bytes)}: "
                        f"{relative_path.name}\n"
                    )
                    continue
                if total_bytes + entry.file_size > max_total_bytes:
                    evidence_gaps.write(
                        "Stopped extracting test results after reaching the "
                        f"{_format_size(max_total_bytes)} aggregate limit\n"
                    )
                    break

                target = (destination / pathlib.Path(*relative_path.parts)).resolve()
                if destination not in target.parents:
                    evidence_gaps.write(
                        f"Skipped unsafe test result path: {entry.filename}\n"
                    )
                    continue

                target.parent.mkdir(parents=True, exist_ok=True)
                try:
                    with archive.open(entry) as source, open(target, "wb") as output:
                        written = _copy_bounded(
                            source,
                            output,
                            max_file_bytes,
                            max_total_bytes - total_bytes,
                        )
                except ExtractionLimitExceeded:
                    target.unlink(missing_ok=True)
                    evidence_gaps.write(
                        "Stopped extracting test results after reaching an extraction size limit\n"
                    )
                    break
                total_bytes += written


def _read_json(path):
    if path == "-":
        return json.load(sys.stdin)
    with open(path, encoding="utf-8") as file:
        return json.load(file)


def _write_json(value):
    json.dump(value, sys.stdout, ensure_ascii=False, separators=(",", ":"))


def main(args):
    operation = args[0] if args else ""
    if operation == "extract-test-results":
        if len(args) != 4:
            raise ValueError(
                "Usage: analyze_ci_failure.py extract-test-results "
                "<archive.zip> <destination> <evidence-gaps.txt>"
            )
        extract_test_results(*args[1:])
        return

    analysis_path = args[1] if len(args) > 1 else None
    cause_path = args[2] if len(args) > 2 else None
    marker = args[3] if len(args) > 3 else None
    analysis = _read_json(analysis_path)

    if operation == "redact":
        _write_json(redact_json(analysis))
    elif operation == "extract-test-failures":
        for failure in extract_test_failures(analysis):
            _write_json(failure)
            sys.stdout.write("\n")
    elif operation == "extract-mocha-failures":
        for failure in extract_mocha_failures(analysis, cause_path):
            _write_json(failure)
            sys.stdout.write("\n")
    elif operation == "format-test-failures":
        sys.stdout.write(format_test_failures(analysis))
    elif operation == "pr-comment":
        context = _read_json(cause_path) if cause_path else {}
        sys.stdout.write(build_pr_comment(analysis, context.get("dry_run") is True))
    elif operation == "validate-publication":
        _write_json(
            validate_publication(analysis, _read_json(cause_path), _read_json(marker))
        )
    elif operation == "validate-rerun-request":
        _write_json(
            validate_rerun_request(
                analysis,
                _read_json(cause_path),
                _read_json(marker),
                _read_json(args[4]),
            )
        )
    elif operation == "select-test-results-artifact":
        _write_json(select_test_results_artifact(analysis))
    else:
        cause = _read_json(cause_path)
        if operation == "job-name":
            sys.stdout.write(get_cause_job_name(analysis, cause))
        elif operation == "add-occurrence":
            _write_json(add_occurrence(analysis, cause))
        elif operation == "occurrence-row":
            sys.stdout.write(build_occurrence_row(analysis, cause))
        elif operation == "issue-body":
            sys.stdout.write(build_issue_body(analysis, cause, marker))
        elif operation == "issue-title":
            sys.stdout.write(f"[CI Failure] {normalize_issue_title(cause.get('title'))}")
        else:
            raise ValueError(f"Unsupported operation '{operation}'.")


if __name__ == "__main__":
    main(sys.argv[1:])