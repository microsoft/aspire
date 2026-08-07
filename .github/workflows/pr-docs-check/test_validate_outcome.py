import json
import tempfile
import unittest
from pathlib import Path

from validate_outcome import OutcomeValidationError, load_payload, validate_outcome


def payload(result: str = "skipped", source_pr_number: object = 18868) -> dict:
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
        )

        self.assertEqual(
            "Confirmed drafted documentation PR: https://github.com/microsoft/aspire.dev/pull/1447",
            message,
        )

    def test_skipped_without_created_pr_passes(self) -> None:
        message = validate_outcome(payload(), "")

        self.assertEqual("Confirmed that no documentation update is needed.", message)

    def test_missing_notification_fails(self) -> None:
        with self.assertRaisesRegex(
            OutcomeValidationError,
            "Expected exactly one notify_source_pr item, found 0",
        ):
            validate_outcome({"items": []}, "")

    def test_duplicate_notifications_fail(self) -> None:
        duplicate_payload = payload()
        duplicate_payload["items"].append(duplicate_payload["items"][0].copy())

        with self.assertRaisesRegex(
            OutcomeValidationError,
            "Expected exactly one notify_source_pr item, found 2",
        ):
            validate_outcome(duplicate_payload, "")

    def test_draft_failed_fails(self) -> None:
        with self.assertRaisesRegex(
            OutcomeValidationError,
            "Documentation was required, but no docs PR was created",
        ):
            validate_outcome(payload("draft_failed"), "")

    def test_drafted_without_created_pr_fails(self) -> None:
        with self.assertRaisesRegex(
            OutcomeValidationError,
            "safe outputs did not create a docs PR",
        ):
            validate_outcome(payload("drafted"), "")

    def test_skipped_with_created_pr_fails(self) -> None:
        with self.assertRaisesRegex(
            OutcomeValidationError,
            "reported no documentation was needed",
        ):
            validate_outcome(
                payload(),
                "https://github.com/microsoft/aspire.dev/pull/1447",
            )

    def test_unknown_result_fails(self) -> None:
        with self.assertRaisesRegex(
            OutcomeValidationError,
            "unsupported documentation result",
        ):
            validate_outcome(payload("unknown"), "")

    def test_invalid_source_pr_number_fails(self) -> None:
        with self.assertRaisesRegex(
            OutcomeValidationError,
            "Invalid source_pr_number",
        ):
            validate_outcome(payload(source_pr_number=True), "")

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


if __name__ == "__main__":
    unittest.main()
