"""Tests for collect_evidence.py."""

from __future__ import annotations

import datetime
import io
import json
import os
import pathlib
import sys
import unittest
import urllib.error
import urllib.request
import warnings
import zipfile
from unittest import mock

_REPO_ROOT = pathlib.Path(__file__).resolve().parents[4]
_WORKFLOW_DIR = _REPO_ROOT / ".github" / "workflows" / "test-selection-audit"
if str(_WORKFLOW_DIR) not in sys.path:
    sys.path.insert(0, str(_WORKFLOW_DIR))

import collect_evidence  # noqa: E402


_BASE_SHA = "1" * 40
_HEAD_SHA = "2" * 40


def _payload(change_source: str) -> dict:
    return {
        "schemaVersion": 1,
        "mode": "enforcing",
        "inputs": {"changeSource": change_source},
        "selectsAll": False,
        "escalationReason": "test reason",
        "changedFiles": ["z/file.cs", "a/file.cs"],
        "excludedFiles": [],
        "unattributedFiles": [],
        "testProjects": [{"name": "Z.Tests"}, {"name": "A.Tests"}],
        "jobs": [{"name": "job:z-job"}, {"name": "job:a-job"}],
    }


def _scope_pull_request(
    number: int,
    *,
    state: str,
    updated_at: str,
    created_at: str = "2026-08-01T00:00:00Z",
    closed_at: str | None = None,
    merged_at: str | None = None,
) -> dict:
    return {
        "number": number,
        "state": state,
        "updated_at": updated_at,
        "created_at": created_at,
        "closed_at": closed_at,
        "merged_at": merged_at,
    }


def _http_error(code: int, headers: dict[str, str] | None = None, message: str = ""):
    return urllib.error.HTTPError(
        "https://api.github.com/test",
        code,
        "test error",
        headers or {},
        io.BytesIO(json.dumps({"message": message}).encode()),
    )


class NormalizeSelectionTests(unittest.TestCase):
    def test_normalizes_diff_source_and_sorts_collections(self) -> None:
        result = collect_evidence.normalize_selection(
            _payload(f"git diff {_BASE_SHA}..{_HEAD_SHA}"),
            include_reason=True,
        )

        self.assertTrue(result["sourceHasDiff"])
        self.assertEqual(result["sourceBaseSha"], _BASE_SHA)
        self.assertEqual(result["sourceHeadSha"], _HEAD_SHA)
        self.assertEqual(result["changedFiles"], ["a/file.cs", "z/file.cs"])
        self.assertEqual(result["testProjects"], ["A.Tests", "Z.Tests"])
        self.assertEqual(result["jobs"], ["job:a-job", "job:z-job"])
        self.assertEqual(result["escalationReason"], "test reason")

    def test_normalizes_force_all_source_without_trusting_reason(self) -> None:
        payload = _payload("(none -- force-all or unset)")
        payload["selectsAll"] = True

        result = collect_evidence.normalize_selection(payload, include_reason=False)

        self.assertFalse(result["sourceHasDiff"])
        self.assertIsNone(result["sourceBaseSha"])
        self.assertIsNone(result["sourceHeadSha"])
        self.assertIsNone(result["escalationReason"])

    def test_rejects_unsupported_change_source(self) -> None:
        payload = _payload(f"git diff {_BASE_SHA}...{_HEAD_SHA}")

        with self.assertRaisesRegex(ValueError, "changeSource is unsupported"):
            collect_evidence.normalize_selection(payload, include_reason=True)

    def test_rejects_unsupported_schema(self) -> None:
        payload = _payload(f"git diff {_BASE_SHA}..{_HEAD_SHA}")
        payload["schemaVersion"] = 2

        with self.assertRaisesRegex(ValueError, "unsupported schema"):
            collect_evidence.normalize_selection(payload, include_reason=True)

    def test_rejects_non_enforcing_mode(self) -> None:
        for mode in ("audit", "unknown", None):
            with self.subTest(mode=mode):
                payload = _payload(f"git diff {_BASE_SHA}..{_HEAD_SHA}")
                payload["mode"] = mode

                with self.assertRaisesRegex(ValueError, "not produced in enforcing mode"):
                    collect_evidence.normalize_selection(payload, include_reason=True)

    def test_rejects_duplicate_paths(self) -> None:
        payload = _payload(f"git diff {_BASE_SHA}..{_HEAD_SHA}")
        payload["changedFiles"] = ["src/a.cs", "src/a.cs"]

        with self.assertRaisesRegex(ValueError, "contains duplicate"):
            collect_evidence.normalize_selection(payload, include_reason=True)

    def test_rejects_duplicate_named_items(self) -> None:
        payload = _payload(f"git diff {_BASE_SHA}..{_HEAD_SHA}")
        payload["jobs"] = [{"name": "job:build"}, {"name": "job:build"}]

        with self.assertRaisesRegex(ValueError, "contains duplicate"):
            collect_evidence.normalize_selection(payload, include_reason=True)

    def test_rejects_prefixed_test_project_names(self) -> None:
        for name in ("test:A.Tests", "job:extension-e2e"):
            with self.subTest(name=name):
                payload = _payload(f"git diff {_BASE_SHA}..{_HEAD_SHA}")
                payload["testProjects"] = [{"name": name}]

                with self.assertRaisesRegex(ValueError, "testProjects\\[0\\]\\.name is invalid"):
                    collect_evidence.normalize_selection(payload, include_reason=True)

    def test_rejects_non_job_tokens(self) -> None:
        for name in ("extension-e2e", "test:A.Tests", "job:", "job:job:extension-e2e"):
            with self.subTest(name=name):
                payload = _payload(f"git diff {_BASE_SHA}..{_HEAD_SHA}")
                payload["jobs"] = [{"name": name}]

                with self.assertRaisesRegex(ValueError, "jobs\\[0\\]\\.name is invalid"):
                    collect_evidence.normalize_selection(payload, include_reason=True)

    def test_accepts_printable_unicode_and_spaces_in_repo_paths(self) -> None:
        payload = _payload(f"git diff {_BASE_SHA}..{_HEAD_SHA}")
        payload["changedFiles"] = ["src/café file.cs"]

        result = collect_evidence.normalize_selection(payload, include_reason=True)

        self.assertEqual(result["changedFiles"], ["src/café file.cs"])

    def test_rejects_paths_the_selector_cannot_represent_safely(self) -> None:
        invalid_paths = [
            "/absolute.cs",
            "../outside.cs",
            "src/../outside.cs",
            "src//file.cs",
            "./file.cs",
            " src/file.cs",
            "src/file.cs ",
            'src/"quoted".cs',
            "src/back\\slash.cs",
            "src/control\u0001.cs",
            "src/format\u202e.cs",
            "src/line\u2028separator.cs",
        ]

        for invalid_path in invalid_paths:
            with self.subTest(path=invalid_path):
                payload = _payload(f"git diff {_BASE_SHA}..{_HEAD_SHA}")
                payload["changedFiles"] = [invalid_path]

                with self.assertRaisesRegex(ValueError, "changedFiles\\[0\\] is invalid"):
                    collect_evidence.normalize_selection(payload, include_reason=True)


class ArtifactDownloadTests(unittest.TestCase):
    def setUp(self) -> None:
        collect_evidence.repository = "microsoft/aspire"
        collect_evidence.token = "test-token"

    def test_redirect_handler_preserves_authorization_only_for_api_origin(self) -> None:
        cases = [
            ("https://api.github.com/redirected", True),
            ("https://api.github.com:443/redirected", True),
            ("https://objects.githubusercontent.com/artifact", False),
            ("http://api.github.com/redirected", False),
            ("https://api.github.com:8443/redirected", False),
        ]

        for redirected_url, expect_authorization in cases:
            with self.subTest(url=redirected_url):
                request = urllib.request.Request(
                    f"{collect_evidence.API_ROOT}/artifact",
                    headers={"Authorization": "test-value"},
                )

                redirected = collect_evidence.ArtifactRedirectHandler().redirect_request(
                    request,
                    None,
                    302,
                    "Found",
                    {},
                    redirected_url,
                )

                self.assertIsNotNone(redirected)
                self.assertEqual(
                    redirected.get_header("Authorization") is not None,
                    expect_authorization,
                )

    def test_downloads_and_normalizes_selection_from_zip(self) -> None:
        archive = self._zip_bytes([
            (
                collect_evidence.ARTIFACT_MEMBER,
                json.dumps(_payload(f"git diff {_BASE_SHA}..{_HEAD_SHA}")).encode(),
            ),
        ])

        result = self._download(archive)

        self.assertEqual(result["sourceHeadSha"], _HEAD_SHA)
        self.assertEqual(result["changedFiles"], ["a/file.cs", "z/file.cs"])

    def test_rejects_compressed_artifact_over_limit_without_retrying(self) -> None:
        opener = mock.Mock()
        opener.open.return_value = io.BytesIO(b"12345")
        with (
            mock.patch.object(collect_evidence, "MAX_COMPRESSED_BYTES", 4),
            mock.patch.object(urllib.request, "build_opener", return_value=opener),
        ):
            with self.assertRaisesRegex(ValueError, "compressed-byte limit"):
                collect_evidence.download_selection(789, include_reason=True)

        opener.open.assert_called_once()

    def test_rejects_expanded_member_over_limit(self) -> None:
        archive = self._zip_bytes([
            (collect_evidence.ARTIFACT_MEMBER, b"12345"),
        ])

        with mock.patch.object(collect_evidence, "MAX_EXPANDED_BYTES", 4):
            with self.assertRaisesRegex(ValueError, "expanded-byte limit"):
                self._download(archive)

    def test_requires_one_exact_artifact_member(self) -> None:
        cases = [
            (
                [(f"nested/{collect_evidence.ARTIFACT_MEMBER}", b"{}")],
                "contains 0 exact members",
            ),
            (
                [
                    (collect_evidence.ARTIFACT_MEMBER, b"{}"),
                    (collect_evidence.ARTIFACT_MEMBER, b"{}"),
                ],
                "contains 2 exact members",
            ),
        ]

        for entries, expected_error in cases:
            with self.subTest(error=expected_error), warnings.catch_warnings():
                warnings.simplefilter("ignore", UserWarning)
                archive = self._zip_bytes(entries)

                with self.assertRaisesRegex(ValueError, expected_error):
                    self._download(archive)

    def test_rejects_invalid_utf8_json_member(self) -> None:
        archive = self._zip_bytes([
            (collect_evidence.ARTIFACT_MEMBER, b"\xff"),
        ])

        with self.assertRaisesRegex(ValueError, "not valid UTF-8 JSON"):
            self._download(archive)

    def test_download_honors_shared_rate_limit_delay(self) -> None:
        archive = self._zip_bytes([
            (
                collect_evidence.ARTIFACT_MEMBER,
                json.dumps(_payload(f"git diff {_BASE_SHA}..{_HEAD_SHA}")).encode(),
            ),
        ])
        opener = mock.Mock()
        opener.open.side_effect = [
            _http_error(429, {"Retry-After": "3"}),
            io.BytesIO(archive),
        ]
        with (
            mock.patch.object(urllib.request, "build_opener", return_value=opener),
            mock.patch.object(collect_evidence.time, "sleep") as sleep,
        ):
            result = collect_evidence.download_selection(789, include_reason=True)

        self.assertEqual(result["sourceHeadSha"], _HEAD_SHA)
        sleep.assert_called_once_with(3)
        self.assertEqual(opener.open.call_count, 2)

    @staticmethod
    def _zip_bytes(entries: list[tuple[str, bytes]]) -> bytes:
        stream = io.BytesIO()
        with zipfile.ZipFile(stream, "w", zipfile.ZIP_DEFLATED) as archive:
            for name, value in entries:
                archive.writestr(name, value)
        return stream.getvalue()

    @staticmethod
    def _download(archive: bytes) -> dict:
        opener = mock.Mock()
        opener.open.return_value = io.BytesIO(archive)
        with mock.patch.object(urllib.request, "build_opener", return_value=opener):
            return collect_evidence.download_selection(789, include_reason=True)


class RequestRetryTests(unittest.TestCase):
    def setUp(self) -> None:
        collect_evidence.token = "test-token"

    def test_primary_rate_limit_waits_until_reset_then_retries(self) -> None:
        error = _http_error(
            403,
            {
                "X-RateLimit-Remaining": "0",
                "X-RateLimit-Reset": "107",
            },
        )
        with (
            mock.patch.object(
                urllib.request,
                "urlopen",
                side_effect=[error, io.BytesIO(b"{}")],
            ) as urlopen,
            mock.patch.object(collect_evidence.time, "time", return_value=100),
            mock.patch.object(collect_evidence.time, "sleep") as sleep,
        ):
            result = collect_evidence.request("/repos/microsoft/aspire")

        self.assertEqual(result, {})
        sleep.assert_called_once_with(8)
        self.assertEqual(urlopen.call_count, 2)

    def test_retry_after_uses_later_rate_limit_deadline(self) -> None:
        error = _http_error(
            429,
            {
                "Retry-After": "7",
                "X-RateLimit-Remaining": "0",
                "X-RateLimit-Reset": "105",
            },
        )
        with (
            mock.patch.object(
                urllib.request,
                "urlopen",
                side_effect=[error, io.BytesIO(b"{}")],
            ),
            mock.patch.object(collect_evidence.time, "time", return_value=100),
            mock.patch.object(collect_evidence.time, "sleep") as sleep,
        ):
            collect_evidence.request("/repos/microsoft/aspire")

        sleep.assert_called_once_with(7)

    def test_secondary_rate_limit_message_uses_bounded_backoff(self) -> None:
        error = _http_error(403, message="You have exceeded a secondary rate limit.")
        with (
            mock.patch.object(
                urllib.request,
                "urlopen",
                side_effect=[error, io.BytesIO(b"{}")],
            ),
            mock.patch.object(collect_evidence.time, "sleep") as sleep,
        ):
            collect_evidence.request("/repos/microsoft/aspire")

        sleep.assert_called_once_with(60)

    def test_malformed_rate_limit_headers_use_secondary_backoff(self) -> None:
        error = _http_error(
            429,
            {
                "Retry-After": "later",
                "X-RateLimit-Remaining": "0",
                "X-RateLimit-Reset": "9" * 100,
            },
        )
        with (
            mock.patch.object(
                urllib.request,
                "urlopen",
                side_effect=[error, io.BytesIO(b"{}")],
            ),
            mock.patch.object(collect_evidence.time, "sleep") as sleep,
        ):
            collect_evidence.request("/repos/microsoft/aspire")

        sleep.assert_called_once_with(60)

    def test_server_error_keeps_short_backoff(self) -> None:
        error = _http_error(500)
        with (
            mock.patch.object(
                urllib.request,
                "urlopen",
                side_effect=[error, io.BytesIO(b"{}")],
            ),
            mock.patch.object(collect_evidence.time, "sleep") as sleep,
        ):
            collect_evidence.request("/repos/microsoft/aspire")

        sleep.assert_called_once_with(1)

    def test_ordinary_forbidden_response_is_not_retried(self) -> None:
        error = _http_error(403, message="Resource not accessible by integration")
        with (
            mock.patch.object(urllib.request, "urlopen", side_effect=error) as urlopen,
            mock.patch.object(collect_evidence.time, "sleep") as sleep,
        ):
            with self.assertRaises(urllib.error.HTTPError):
                collect_evidence.request("/repos/microsoft/aspire")

        urlopen.assert_called_once()
        sleep.assert_not_called()

    def test_rate_limit_delay_beyond_cap_is_not_retried_early(self) -> None:
        error = _http_error(
            403,
            {
                "X-RateLimit-Remaining": "0",
                "X-RateLimit-Reset": "1000",
            },
        )
        with (
            mock.patch.object(urllib.request, "urlopen", side_effect=error) as urlopen,
            mock.patch.object(collect_evidence.time, "time", return_value=100),
            mock.patch.object(collect_evidence.time, "sleep") as sleep,
        ):
            with self.assertRaises(urllib.error.HTTPError):
                collect_evidence.request("/repos/microsoft/aspire")

        urlopen.assert_called_once()
        sleep.assert_not_called()


class SelectionStatusTests(unittest.TestCase):
    def setUp(self) -> None:
        collect_evidence.repository = "microsoft/aspire"
        collect_evidence.processed_index = {}

    def test_same_repository_artifact_is_creditable(self) -> None:
        record = self._find_record("microsoft/aspire", normalized_head=_HEAD_SHA)

        self.assertEqual(record["selection"]["status"], "resolved")
        self.assertTrue(record["selection"]["creditable"])

    def test_fork_artifact_is_untrusted(self) -> None:
        record = self._find_record("radical/aspire", normalized_head=_HEAD_SHA)

        self.assertEqual(record["selection"]["status"], "untrusted-fork-artifact")
        self.assertFalse(record["selection"]["creditable"])

    def test_mismatched_artifact_head_is_not_creditable(self) -> None:
        record = self._find_record("microsoft/aspire", normalized_head="3" * 40)

        self.assertEqual(record["selection"]["status"], "artifact-head-mismatch")
        self.assertFalse(record["selection"]["creditable"])

    def test_selection_before_lookback_is_not_creditable(self) -> None:
        cutoff = datetime.datetime(2026, 9, 23, tzinfo=datetime.timezone.utc)

        record = self._find_record(
            "microsoft/aspire",
            normalized_head=_HEAD_SHA,
            selection_cutoff=cutoff,
        )

        self.assertEqual(record["selection"]["status"], "selection-outside-lookback")
        self.assertNotIn("creditable", record["selection"])
        self.assertNotIn("result", record["selection"])

    def test_selection_on_lookback_boundary_is_creditable(self) -> None:
        cutoff = datetime.datetime(2026, 9, 22, 10, 2, tzinfo=datetime.timezone.utc)

        record = self._find_record(
            "microsoft/aspire",
            normalized_head=_HEAD_SHA,
            selection_cutoff=cutoff,
        )

        self.assertEqual(record["selection"]["status"], "resolved")
        self.assertTrue(record["selection"]["creditable"])

    def test_exact_processed_attempt_is_recorded(self) -> None:
        collect_evidence.processed_index = {(123, _HEAD_SHA): (456, 2)}
        pull_request = self._pull_request("microsoft/aspire")
        runs = [self._run("microsoft/aspire")]

        with mock.patch.object(
            collect_evidence,
            "paginate",
            return_value=(runs, False),
        ):
            record = collect_evidence.find_selection_record(pull_request)

        self.assertEqual(record["selection"]["status"], "recorded")

    def _find_record(
        self,
        head_repository: str,
        normalized_head: str,
        selection_cutoff: datetime.datetime | None = None,
    ) -> dict:
        pull_request = self._pull_request(head_repository)
        run = self._run(head_repository)
        associated = [pull_request]
        jobs = [{
            "name": "CI / Tests / Setup for tests",
            "run_attempt": 2,
            "status": "completed",
            "completed_at": "2026-09-22T10:05:00Z",
            "steps": [{
                "name": "Select relevant tests",
                "conclusion": "success",
                "started_at": "2026-09-22T10:00:00Z",
            }],
        }]
        artifacts = [{
            "id": 789,
            "name": collect_evidence.ARTIFACT_NAME,
            "created_at": "2026-09-22T10:02:00Z",
            "expired": False,
            "workflow_run": {"id": 456, "head_sha": _HEAD_SHA},
        }]

        def paginate(path, parameters=None, key=None, max_pages=20):
            if path.endswith("/actions/runs"):
                return [run], False
            if path.endswith(f"/commits/{_HEAD_SHA}/pulls"):
                return associated, False
            if path.endswith("/jobs"):
                return jobs, False
            if path.endswith("/artifacts"):
                return artifacts, False
            self.fail(f"Unexpected pagination path: {path}")

        normalized = collect_evidence.normalize_selection(
            _payload(f"git diff {_BASE_SHA}..{normalized_head}"),
            include_reason=head_repository == collect_evidence.repository,
        )
        with (
            mock.patch.object(collect_evidence, "paginate", side_effect=paginate),
            mock.patch.object(collect_evidence, "list_changed_files", return_value=(["src/a.cs"], False)),
            mock.patch.object(
                collect_evidence,
                "download_selection",
                return_value=normalized,
            ) as download_selection,
        ):
            record = collect_evidence.find_selection_record(pull_request, selection_cutoff)

        artifact_created_at = datetime.datetime(2026, 9, 22, 10, 2, tzinfo=datetime.timezone.utc)
        if selection_cutoff is not None and artifact_created_at < selection_cutoff:
            download_selection.assert_not_called()
        else:
            download_selection.assert_called_once()
        return record

    @staticmethod
    def _pull_request(head_repository: str) -> dict:
        return {
            "number": 123,
            "head": {
                "sha": _HEAD_SHA,
                "ref": "feature",
                "repo": {"full_name": head_repository},
            },
        }

    @staticmethod
    def _run(head_repository: str) -> dict:
        return {
            "id": 456,
            "path": ".github/workflows/ci.yml",
            "head_sha": _HEAD_SHA,
            "head_branch": "feature",
            "head_repository": {"full_name": head_repository},
            "created_at": "2026-09-22T09:59:00Z",
            "run_attempt": 2,
            "status": "completed",
            "conclusion": "success",
        }


class PullRequestScopeTests(unittest.TestCase):
    def setUp(self) -> None:
        collect_evidence.repository = "microsoft/aspire"

    def test_default_scope_filters_by_state_timestamp_and_stops_at_cutoff(self) -> None:
        cutoff = datetime.datetime(2026, 9, 10, tzinfo=datetime.timezone.utc)
        page_one = [
            _scope_pull_request(
                1,
                state="open",
                updated_at="2026-09-12T00:00:00Z",
            ),
            _scope_pull_request(
                2,
                state="closed",
                updated_at="2026-09-12T00:00:00Z",
                closed_at="2026-09-01T00:00:00Z",
            ),
            _scope_pull_request(
                3,
                state="closed",
                updated_at="2026-09-12T00:00:00Z",
                closed_at="2026-09-11T00:00:00Z",
            ),
            _scope_pull_request(
                4,
                state="closed",
                updated_at="2026-09-12T00:00:00Z",
                closed_at="2026-09-09T00:00:00Z",
                merged_at="2026-09-10T00:00:00Z",
            ),
            _scope_pull_request(
                5,
                state="open",
                updated_at="2026-09-10T00:00:00Z",
            ),
        ]
        page_one.extend(
            _scope_pull_request(
                number,
                state="open",
                updated_at="2026-09-10T00:00:00Z",
            )
            for number in range(1000, 1095)
        )
        page_two = [
            _scope_pull_request(
                6,
                state="open",
                updated_at="2026-09-10T00:00:00Z",
            ),
            _scope_pull_request(
                7,
                state="closed",
                updated_at="2026-09-10T00:00:00Z",
                closed_at="2026-09-09T00:00:00Z",
            ),
        ]
        page_two.extend(
            _scope_pull_request(
                number,
                state="open",
                updated_at="2026-09-09T00:00:00Z",
            )
            for number in range(2000, 2098)
        )
        pages = {1: page_one, 2: page_two}

        with (
            mock.patch.dict(os.environ, {"PR_NUMBERS": ""}, clear=False),
            mock.patch.object(
                collect_evidence,
                "request",
                side_effect=lambda _path, params: pages[params["page"]],
            ) as request,
        ):
            pull_requests, truncated = collect_evidence.list_pull_requests(cutoff)

        numbers = {pull_request["number"] for pull_request in pull_requests}
        self.assertEqual(request.call_count, 2)
        self.assertEqual([call.args[1]["page"] for call in request.call_args_list], [1, 2])
        self.assertEqual(len(numbers), 100)
        self.assertTrue({1, 3, 4, 5, 6}.issubset(numbers))
        self.assertTrue({2, 7, *range(2000, 2098)}.isdisjoint(numbers))
        self.assertFalse(truncated)

    def test_default_scope_stops_on_short_page_without_truncating(self) -> None:
        cutoff = datetime.datetime(2026, 9, 10, tzinfo=datetime.timezone.utc)
        page = [
            _scope_pull_request(
                1,
                state="open",
                updated_at="2026-09-11T00:00:00Z",
            )
        ]

        with (
            mock.patch.dict(os.environ, {"PR_NUMBERS": ""}, clear=False),
            mock.patch.object(collect_evidence, "request", return_value=page) as request,
        ):
            pull_requests, truncated = collect_evidence.list_pull_requests(cutoff)

        self.assertEqual([pull_request["number"] for pull_request in pull_requests], [1])
        request.assert_called_once()
        self.assertFalse(truncated)

    def test_default_scope_marks_full_page_limit_as_truncated(self) -> None:
        cutoff = datetime.datetime(2026, 9, 10, tzinfo=datetime.timezone.utc)

        def request_page(_path, params):
            page = params["page"]
            return [
                _scope_pull_request(
                    page * 100 + index,
                    state="open",
                    updated_at="2026-09-11T00:00:00Z",
                )
                for index in range(100)
            ]

        with (
            mock.patch.dict(os.environ, {"PR_NUMBERS": ""}, clear=False),
            mock.patch.object(
                collect_evidence,
                "request",
                side_effect=request_page,
            ) as request,
        ):
            pull_requests, truncated = collect_evidence.list_pull_requests(cutoff)

        self.assertEqual(len(pull_requests), 1000)
        self.assertEqual(request.call_count, 10)
        self.assertTrue(truncated)

    def test_default_scope_marks_pr_limit_as_truncated(self) -> None:
        cutoff = datetime.datetime(2026, 9, 10, tzinfo=datetime.timezone.utc)
        page = [
            _scope_pull_request(
                number,
                state="open",
                updated_at="2026-09-11T00:00:00Z",
            )
            for number in (1, 2, 3)
        ]

        with (
            mock.patch.dict(os.environ, {"PR_NUMBERS": ""}, clear=False),
            mock.patch.object(collect_evidence, "MAX_PRS", 2),
            mock.patch.object(collect_evidence, "request", return_value=page),
        ):
            pull_requests, truncated = collect_evidence.list_pull_requests(cutoff)

        self.assertEqual([pull_request["number"] for pull_request in pull_requests], [1, 2])
        self.assertTrue(truncated)

    def test_explicit_scope_rejects_excess_unique_numbers_before_requests(self) -> None:
        with (
            mock.patch.dict(os.environ, {"PR_NUMBERS": "1,2,3"}, clear=False),
            mock.patch.object(collect_evidence, "MAX_PRS", 2),
            mock.patch.object(collect_evidence, "request") as request,
        ):
            with self.assertRaisesRegex(ValueError, "at most 2 unique values"):
                collect_evidence.list_pull_requests(None)

        request.assert_not_called()

    def test_explicit_scope_deduplicates_before_applying_limit(self) -> None:
        with (
            mock.patch.dict(os.environ, {"PR_NUMBERS": "1,2,1"}, clear=False),
            mock.patch.object(collect_evidence, "MAX_PRS", 2),
            mock.patch.object(
                collect_evidence,
                "request",
                side_effect=lambda path: {"number": int(path.rsplit("/", 1)[1])},
            ) as request,
        ):
            pull_requests, truncated = collect_evidence.list_pull_requests(None)

        self.assertEqual([pull_request["number"] for pull_request in pull_requests], [1, 2])
        self.assertFalse(truncated)
        self.assertEqual(request.call_count, 2)

    def test_changed_files_accepts_printable_unicode_and_spaces(self) -> None:
        with mock.patch.object(
            collect_evidence,
            "paginate",
            return_value=([{"filename": "src/café file.cs"}], False),
        ):
            paths, truncated = collect_evidence.list_changed_files(123)

        self.assertEqual(paths, ["src/café file.cs"])
        self.assertFalse(truncated)


if __name__ == "__main__":
    unittest.main()
