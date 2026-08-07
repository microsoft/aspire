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
    encode_workflow_command_data,
    load_payload,
    main,
    validate_outcome,
)


EXPECTED_SOURCE_PR_NUMBER = 18868
VALIDATOR_PATH = Path(__file__).with_name("validate_outcome.py")


def payload(
    result: object = "skipped",
    source_pr_number: object = EXPECTED_SOURCE_PR_NUMBER,
) -> dict:
    return {
        "items": [
            {
                "type": "notify_source_pr",
                "source_pr_number": source_pr_number,
                "result": result,
            }
        ]
    }


class ValidateOutcomeTests(unittest.TestCase):
    def test_drafted_with_created_pr_passes(self) -> None:
        message = validate_outcome(
            payload("drafted"),
            "https://github.com/microsoft/aspire.dev/pull/1447",
            EXPECTED_SOURCE_PR_NUMBER,
        )

        self.assertEqual(
            "Confirmed drafted documentation PR: https://github.com/microsoft/aspire.dev/pull/1447",
            message,
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
