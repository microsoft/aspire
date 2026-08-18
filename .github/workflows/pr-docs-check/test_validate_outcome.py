import contextlib
import io
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

from validate_outcome import (
    OutcomeValidationError,
    build_side_effect_outcome,
    encode_workflow_command_data,
    load_expected_source_pr_number,
    load_payload,
    main,
    validate_outcome,
)


EXPECTED_SOURCE_PR_NUMBER = 18868
VALIDATOR_PATH = Path(__file__).with_name("validate_outcome.py")


def payload(
    result: object = "skipped",
    source_pr_number: object = EXPECTED_SOURCE_PR_NUMBER,
    target_branch: object = "",
) -> dict:
    return {
        "items": [
            {
                "type": "notify_source_pr",
                "source_pr_number": source_pr_number,
                "result": result,
                "target_branch": target_branch,
            }
        ]
    }


def create_pull_request_item(**target_fields: object) -> dict:
    item = {
        "type": "create_pull_request",
        "title": "Draft docs",
        "body": "Docs",
    }
    item.update(target_fields or {"base": "release/13.5"})
    return item


class ValidateOutcomeTests(unittest.TestCase):
    def test_drafted_with_current_base_passes(self) -> None:
        drafted_payload = payload("drafted", target_branch="release/13.5")
        drafted_payload["items"].append(create_pull_request_item())

        message = validate_outcome(
            drafted_payload,
            "https://github.com/microsoft/aspire.dev/pull/1447",
            EXPECTED_SOURCE_PR_NUMBER,
            "release/13.5",
        )

        self.assertEqual(
            "Confirmed drafted documentation PR: https://github.com/microsoft/aspire.dev/pull/1447",
            message,
        )

    def test_drafted_with_legacy_base_branch_passes(self) -> None:
        drafted_payload = payload("drafted", target_branch="release/13.5")
        drafted_payload["items"].append(
            create_pull_request_item(base_branch="release/13.5")
        )

        message = validate_outcome(
            drafted_payload,
            "https://github.com/microsoft/aspire.dev/pull/1447",
            EXPECTED_SOURCE_PR_NUMBER,
            "release/13.5",
        )

        self.assertIn("Confirmed drafted documentation PR", message)

    def test_drafted_with_agreeing_base_fields_passes(self) -> None:
        drafted_payload = payload("drafted", target_branch="release/13.5")
        drafted_payload["items"].append(
            create_pull_request_item(
                base="release/13.5",
                base_branch="release/13.5",
            )
        )

        message = validate_outcome(
            drafted_payload,
            "https://github.com/microsoft/aspire.dev/pull/1447",
            EXPECTED_SOURCE_PR_NUMBER,
            "release/13.5",
        )

        self.assertIn("Confirmed drafted documentation PR", message)

    def test_drafted_with_disagreeing_base_fields_fails(self) -> None:
        drafted_payload = payload("drafted", target_branch="release/13.5")
        drafted_payload["items"].append(
            create_pull_request_item(
                base="release/13.5",
                base_branch="main",
            )
        )

        with self.assertRaisesRegex(
            OutcomeValidationError,
            "Canonical create_pull_request base and base_branch disagree",
        ):
            validate_outcome(
                drafted_payload,
                "https://github.com/microsoft/aspire.dev/pull/1447",
                EXPECTED_SOURCE_PR_NUMBER,
                "release/13.5",
            )

    def test_drafted_with_invalid_canonical_target_fails(self) -> None:
        cases = {
            "missing": {},
            "invalid current base": {"base": "release/latest"},
            "current base wrong type": {"base": 13.5},
            "invalid legacy base": {"base_branch": "release/latest"},
            "legacy base wrong type": {"base_branch": 13.5},
            "invalid current base with valid legacy base": {
                "base": "release/latest",
                "base_branch": "release/13.5",
            },
            "valid current base with invalid legacy base": {
                "base": "release/13.5",
                "base_branch": "release/latest",
            },
        }
        for name, target_fields in cases.items():
            with self.subTest(name=name):
                drafted_payload = payload("drafted", target_branch="release/13.5")
                create_item = create_pull_request_item()
                create_item.pop("base")
                create_item.update(target_fields)
                drafted_payload["items"].append(create_item)

                with self.assertRaises(OutcomeValidationError):
                    validate_outcome(
                        drafted_payload,
                        "https://github.com/microsoft/aspire.dev/pull/1447",
                        EXPECTED_SOURCE_PR_NUMBER,
                        "release/13.5",
                    )

    def test_drafted_with_duplicate_create_items_fails(self) -> None:
        drafted_payload = payload("drafted", target_branch="release/13.5")
        drafted_payload["items"].extend(
            [create_pull_request_item(), create_pull_request_item()]
        )

        with self.assertRaisesRegex(
            OutcomeValidationError,
            "Expected exactly one create_pull_request item for a drafted outcome, found 2",
        ):
            validate_outcome(
                drafted_payload,
                "https://github.com/microsoft/aspire.dev/pull/1447",
                EXPECTED_SOURCE_PR_NUMBER,
                "release/13.5",
            )

    def test_drafted_with_actual_base_mismatch_fails(self) -> None:
        drafted_payload = payload("drafted", target_branch="release/13.5")
        drafted_payload["items"].append(create_pull_request_item())

        with self.assertRaisesRegex(
            OutcomeValidationError,
            "Drafted PR base branch main does not match canonical "
            "create_pull_request target branch release/13.5",
        ):
            validate_outcome(
                drafted_payload,
                "https://github.com/microsoft/aspire.dev/pull/1447",
                EXPECTED_SOURCE_PR_NUMBER,
                "main",
            )

    def test_drafted_with_notification_target_mismatch_fails(self) -> None:
        drafted_payload = payload("drafted", target_branch="main")
        drafted_payload["items"].append(create_pull_request_item())

        with self.assertRaisesRegex(
            OutcomeValidationError,
            "Canonical create_pull_request target branch release/13.5 does not match "
            "notify_source_pr target_branch main",
        ):
            validate_outcome(
                drafted_payload,
                "https://github.com/microsoft/aspire.dev/pull/1447",
                EXPECTED_SOURCE_PR_NUMBER,
                "release/13.5",
            )

    def test_skipped_without_created_pr_passes(self) -> None:
        message = validate_outcome(
            payload(),
            "",
            EXPECTED_SOURCE_PR_NUMBER,
        )

        self.assertEqual("Confirmed that no documentation update is needed.", message)

    def test_missing_notification_fails(self) -> None:
        with self.assertRaisesRegex(
            OutcomeValidationError,
            "Expected exactly one notify_source_pr item, found 0",
        ):
            validate_outcome({"items": []}, "", EXPECTED_SOURCE_PR_NUMBER)

    def test_malformed_items_fails(self) -> None:
        with self.assertRaisesRegex(
            OutcomeValidationError,
            "Expected exactly one notify_source_pr item, found 0",
        ):
            validate_outcome({"items": "not-a-list"}, "", EXPECTED_SOURCE_PR_NUMBER)

    def test_duplicate_notifications_fail(self) -> None:
        duplicate_payload = payload()
        duplicate_payload["items"].append(duplicate_payload["items"][0].copy())

        with self.assertRaisesRegex(
            OutcomeValidationError,
            "Expected exactly one notify_source_pr item, found 2",
        ):
            validate_outcome(duplicate_payload, "", EXPECTED_SOURCE_PR_NUMBER)

    def test_integral_float_source_pr_number_fails(self) -> None:
        with self.assertRaisesRegex(
            OutcomeValidationError,
            "Invalid source_pr_number from agent",
        ):
            validate_outcome(
                payload(source_pr_number=18868.0),
                "",
                EXPECTED_SOURCE_PR_NUMBER,
            )

    def test_draft_failed_fails(self) -> None:
        with self.assertRaisesRegex(
            OutcomeValidationError,
            "Documentation was required, but no docs PR was created",
        ):
            validate_outcome(
                payload("draft_failed"),
                "",
                EXPECTED_SOURCE_PR_NUMBER,
            )

    def test_draft_failed_with_created_pr_reports_contradiction(self) -> None:
        created_pr_url = "https://github.com/microsoft/aspire.dev/pull/1447"

        with self.assertRaises(OutcomeValidationError) as context:
            validate_outcome(
                payload("draft_failed"),
                created_pr_url,
                EXPECTED_SOURCE_PR_NUMBER,
            )

        self.assertEqual(
            "The agent reported documentation drafting failed, but safe outputs "
            f"created {created_pr_url}.",
            str(context.exception),
        )

    def test_drafted_without_created_pr_fails(self) -> None:
        with self.assertRaisesRegex(
            OutcomeValidationError,
            "safe outputs did not create a docs PR",
        ):
            validate_outcome(payload("drafted"), "", EXPECTED_SOURCE_PR_NUMBER)

    def test_skipped_with_created_pr_fails(self) -> None:
        with self.assertRaisesRegex(
            OutcomeValidationError,
            "reported no documentation was needed",
        ):
            validate_outcome(
                payload(),
                "https://github.com/microsoft/aspire.dev/pull/1447",
                EXPECTED_SOURCE_PR_NUMBER,
            )

    def test_skipped_with_create_pull_request_item_fails_without_created_url(
        self,
    ) -> None:
        contradictory_payload = payload()
        contradictory_payload["items"].append(create_pull_request_item())

        with self.assertRaisesRegex(
            OutcomeValidationError,
            "also requested a docs PR",
        ):
            validate_outcome(
                contradictory_payload,
                "",
                EXPECTED_SOURCE_PR_NUMBER,
            )

    def test_unknown_result_fails(self) -> None:
        with self.assertRaisesRegex(
            OutcomeValidationError,
            "unsupported documentation result",
        ):
            validate_outcome(payload("unknown"), "", EXPECTED_SOURCE_PR_NUMBER)

    def test_empty_result_fails(self) -> None:
        with self.assertRaisesRegex(
            OutcomeValidationError,
            r"unsupported documentation result: \(empty\)",
        ):
            validate_outcome(payload(""), "", EXPECTED_SOURCE_PR_NUMBER)

    def test_invalid_source_pr_number_fails(self) -> None:
        with self.assertRaisesRegex(
            OutcomeValidationError,
            "Invalid source_pr_number from agent",
        ):
            validate_outcome(
                payload(source_pr_number=True),
                "",
                EXPECTED_SOURCE_PR_NUMBER,
            )

    def test_mismatched_source_pr_number_fails(self) -> None:
        with self.assertRaisesRegex(
            OutcomeValidationError,
            "does not match triggering source PR",
        ):
            validate_outcome(payload(), "", EXPECTED_SOURCE_PR_NUMBER + 1)

    def test_invalid_expected_source_pr_number_fails(self) -> None:
        with self.assertRaisesRegex(
            OutcomeValidationError,
            "Invalid expected source PR number",
        ):
            validate_outcome(payload(), "", None)

    def test_load_payload_reports_missing_file(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            missing_path = Path(directory) / "missing.json"

            with self.assertRaisesRegex(
                OutcomeValidationError,
                "Agent output file not found",
            ):
                load_payload(missing_path)

    def test_load_payload_reports_malformed_json(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            output_path = Path(directory) / "agent_output.json"
            output_path.write_text("{", encoding="utf-8")

            with self.assertRaisesRegex(
                OutcomeValidationError,
                "Failed to parse agent output",
            ):
                load_payload(output_path)

    def test_load_payload_reads_valid_json(self) -> None:
        expected = payload()
        with tempfile.TemporaryDirectory() as directory:
            output_path = Path(directory) / "agent_output.json"
            output_path.write_text(json.dumps(expected), encoding="utf-8")

            self.assertEqual(expected, load_payload(output_path))

    def test_load_expected_source_pr_number_reads_pull_request_event(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            event_path = Path(directory) / "event.json"
            event_path.write_text(
                json.dumps({"pull_request": {"number": EXPECTED_SOURCE_PR_NUMBER}}),
                encoding="utf-8",
            )

            self.assertEqual(
                EXPECTED_SOURCE_PR_NUMBER,
                load_expected_source_pr_number(event_path),
            )

    def test_load_expected_source_pr_number_reads_dispatch_event(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            event_path = Path(directory) / "event.json"
            event_path.write_text(
                json.dumps(
                    {"inputs": {"pr_number": f" {EXPECTED_SOURCE_PR_NUMBER} "}}
                ),
                encoding="utf-8",
            )

            self.assertEqual(
                EXPECTED_SOURCE_PR_NUMBER,
                load_expected_source_pr_number(event_path),
            )


class SideEffectOutcomeTests(unittest.TestCase):
    def test_valid_draft_allows_comment_and_sme_review(self) -> None:
        drafted_payload = payload("drafted", target_branch="release/13.5")
        drafted_payload["items"].append(create_pull_request_item())

        outcome = build_side_effect_outcome(
            drafted_payload,
            "https://github.com/microsoft/aspire.dev/pull/1447",
            EXPECTED_SOURCE_PR_NUMBER,
            "release/13.5",
        )

        self.assertTrue(outcome["allow_comment"])
        self.assertTrue(outcome["allow_sme_review"])
        self.assertEqual("drafted", outcome["render_kind"])
        self.assertEqual(EXPECTED_SOURCE_PR_NUMBER, outcome["source_pr_number"])

    def test_wrong_base_draft_allows_only_generic_warning(self) -> None:
        drafted_payload = payload("drafted", target_branch="release/13.5")
        drafted_payload["items"].append(create_pull_request_item())

        outcome = build_side_effect_outcome(
            drafted_payload,
            "https://github.com/microsoft/aspire.dev/pull/1447",
            EXPECTED_SOURCE_PR_NUMBER,
            "main",
        )

        self.assertTrue(outcome["allow_comment"])
        self.assertFalse(outcome["allow_sme_review"])
        self.assertEqual("invalid", outcome["render_kind"])
        self.assertIn("does not match canonical", outcome["diagnostic"])

    def test_skipped_without_pr_allows_success_comment(self) -> None:
        outcome = build_side_effect_outcome(
            payload(),
            "",
            EXPECTED_SOURCE_PR_NUMBER,
            "",
        )

        self.assertTrue(outcome["allow_comment"])
        self.assertFalse(outcome["allow_sme_review"])
        self.assertEqual("skipped", outcome["render_kind"])

    def test_duplicate_notifications_allow_only_generic_warning(self) -> None:
        duplicate_payload = payload("drafted")
        duplicate_payload["items"].append(duplicate_payload["items"][0].copy())

        outcome = build_side_effect_outcome(
            duplicate_payload,
            "https://github.com/microsoft/aspire.dev/pull/1447",
            EXPECTED_SOURCE_PR_NUMBER,
            "release/13.5",
        )

        self.assertTrue(outcome["allow_comment"])
        self.assertFalse(outcome["allow_sme_review"])
        self.assertEqual("invalid", outcome["render_kind"])

    def test_mismatched_source_identity_allows_no_side_effects(self) -> None:
        outcome = build_side_effect_outcome(
            payload(source_pr_number=EXPECTED_SOURCE_PR_NUMBER + 1),
            "https://github.com/microsoft/aspire.dev/pull/1447",
            EXPECTED_SOURCE_PR_NUMBER,
            "release/13.5",
        )

        self.assertFalse(outcome["allow_comment"])
        self.assertFalse(outcome["allow_sme_review"])

    def test_integral_float_source_identity_allows_no_side_effects(self) -> None:
        outcome = build_side_effect_outcome(
            payload(source_pr_number=18868.0),
            "https://github.com/microsoft/aspire.dev/pull/1447",
            EXPECTED_SOURCE_PR_NUMBER,
            "release/13.5",
        )

        self.assertFalse(outcome["allow_comment"])
        self.assertFalse(outcome["allow_sme_review"])

    def test_skipped_create_request_allows_only_generic_warning(self) -> None:
        contradictory_payload = payload()
        contradictory_payload["items"].append(create_pull_request_item())

        outcome = build_side_effect_outcome(
            contradictory_payload,
            "",
            EXPECTED_SOURCE_PR_NUMBER,
            "",
        )

        self.assertTrue(outcome["allow_comment"])
        self.assertFalse(outcome["allow_sme_review"])
        self.assertEqual("invalid", outcome["render_kind"])

    def test_draft_failed_with_created_pr_allows_only_generic_warning(self) -> None:
        outcome = build_side_effect_outcome(
            payload("draft_failed"),
            "https://github.com/microsoft/aspire.dev/pull/1447",
            EXPECTED_SOURCE_PR_NUMBER,
            "release/13.5",
        )

        self.assertTrue(outcome["allow_comment"])
        self.assertFalse(outcome["allow_sme_review"])
        self.assertEqual("invalid", outcome["render_kind"])


class ValidatorCliTests(unittest.TestCase):
    def _write_payload(self, directory: str, value: dict) -> Path:
        output_path = Path(directory) / "agent_output.json"
        output_path.write_text(json.dumps(value), encoding="utf-8")
        return output_path

    def test_main_returns_zero_for_success(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            output_path = self._write_payload(directory, payload())
            with contextlib.redirect_stdout(io.StringIO()):
                exit_code = main(
                    [
                        "--agent-output",
                        str(output_path),
                        "--expected-source-pr-number",
                        str(EXPECTED_SOURCE_PR_NUMBER),
                    ]
                )

        self.assertEqual(0, exit_code)

    def test_main_returns_nonzero_for_failure(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            output_path = self._write_payload(directory, payload("draft_failed"))
            with contextlib.redirect_stdout(io.StringIO()):
                exit_code = main(
                    [
                        "--agent-output",
                        str(output_path),
                        "--expected-source-pr-number",
                        str(EXPECTED_SOURCE_PR_NUMBER),
                    ]
                )

        self.assertNotEqual(0, exit_code)

    def test_side_effect_main_rejects_wrong_actual_base(self) -> None:
        drafted_payload = payload("drafted", target_branch="release/13.5")
        drafted_payload["items"].append(create_pull_request_item())
        with tempfile.TemporaryDirectory() as directory:
            output_path = self._write_payload(directory, drafted_payload)
            event_path = Path(directory) / "event.json"
            event_path.write_text(
                json.dumps({"pull_request": {"number": EXPECTED_SOURCE_PR_NUMBER}}),
                encoding="utf-8",
            )
            side_effect_path = Path(directory) / "side-effect.json"

            exit_code = main(
                [
                    "--agent-output",
                    str(output_path),
                    "--created-pr-url",
                    "https://github.com/microsoft/aspire.dev/pull/1447",
                    "--created-pr-base",
                    "main",
                    "--github-event-path",
                    str(event_path),
                    "--write-side-effect-outcome",
                    str(side_effect_path),
                ]
            )
            outcome = json.loads(side_effect_path.read_text(encoding="utf-8"))

        self.assertEqual(0, exit_code)
        self.assertTrue(outcome["allow_comment"])
        self.assertFalse(outcome["allow_sme_review"])
        self.assertEqual("invalid", outcome["render_kind"])

    def test_process_exits_zero_for_success(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            output_path = self._write_payload(directory, payload())
            completed = subprocess.run(
                [
                    sys.executable,
                    str(VALIDATOR_PATH),
                    "--agent-output",
                    str(output_path),
                    "--expected-source-pr-number",
                    str(EXPECTED_SOURCE_PR_NUMBER),
                ],
                check=False,
                capture_output=True,
                text=True,
            )

        self.assertEqual(0, completed.returncode, completed.stdout + completed.stderr)

    def test_process_exits_nonzero_for_failure(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            output_path = self._write_payload(directory, payload("draft_failed"))
            completed = subprocess.run(
                [
                    sys.executable,
                    str(VALIDATOR_PATH),
                    "--agent-output",
                    str(output_path),
                    "--expected-source-pr-number",
                    str(EXPECTED_SOURCE_PR_NUMBER),
                ],
                check=False,
                capture_output=True,
                text=True,
            )

        self.assertNotEqual(0, completed.returncode)

    def test_process_encodes_workflow_command_data(self) -> None:
        malicious_result = "invalid%\r\n::warning::injected"
        with tempfile.TemporaryDirectory() as directory:
            output_path = self._write_payload(directory, payload(malicious_result))
            completed = subprocess.run(
                [
                    sys.executable,
                    str(VALIDATOR_PATH),
                    "--agent-output",
                    str(output_path),
                    "--expected-source-pr-number",
                    str(EXPECTED_SOURCE_PR_NUMBER),
                ],
                check=False,
                capture_output=True,
                text=True,
            )

        self.assertNotEqual(0, completed.returncode)
        self.assertEqual(
            "::error::Agent returned unsupported documentation result: "
            "invalid%25%0D%0A::warning::injected.\n",
            completed.stdout,
        )
        self.assertEqual("", completed.stderr)
        self.assertEqual(
            "invalid%25%0D%0Avalue",
            encode_workflow_command_data("invalid%\r\nvalue"),
        )


if __name__ == "__main__":
    unittest.main()
