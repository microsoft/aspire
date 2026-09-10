import json
import tempfile
import unittest
from pathlib import Path

from resolve_safe_output_target import (
    SafeOutputTargetError,
    load_payload,
    load_raw_safe_outputs,
    main,
    resolve_target_branch,
)


EXPECTED_SOURCE_PR_NUMBER = 17235
FIXTURES_PATH = Path(__file__).with_name("fixtures")


def create_item(**target_fields: object) -> dict:
    return {
        "type": "create_pull_request",
        "title": "Draft docs",
        "body": "Docs",
        **target_fields,
    }


def notification(
    *,
    result: object = "drafted",
    source_pr_number: object = EXPECTED_SOURCE_PR_NUMBER,
    target_branch: object = "release/13.5",
) -> dict:
    return {
        "type": "notify_source_pr",
        "result": result,
        "source_pr_number": source_pr_number,
        "target_branch": target_branch,
    }


def raw_create_item(**target_fields: object) -> dict:
    return {
        "type": "create_pull_request",
        "title": "Draft docs",
        "body": "Docs",
        "base_branch": "release/13.5",
        "base_commit": "aa2777825624f037a77160728939e36f7c788eff",
        **target_fields,
    }


def payload(create: list[dict], notifications: list[dict] | None = None) -> dict:
    return {
        "items": [
            *create,
            *(notifications if notifications is not None else [notification()]),
        ]
    }


class ResolveTargetBranchTests(unittest.TestCase):
    def test_current_base_passes(self) -> None:
        self.assertEqual(
            "release/13.5",
            resolve_target_branch(
                payload([create_item(base="release/13.5")]),
                EXPECTED_SOURCE_PR_NUMBER,
            ),
        )

    def test_legacy_base_branch_passes(self) -> None:
        self.assertEqual(
            "release/13.5",
            resolve_target_branch(
                payload([create_item(base_branch="release/13.5")]),
                EXPECTED_SOURCE_PR_NUMBER,
            ),
        )

    def test_recovery_incident_uses_raw_server_metadata(self) -> None:
        canonical_payload = load_payload(
            FIXTURES_PATH / "run-32112079288-agent_output.json"
        )
        raw_safe_outputs = load_raw_safe_outputs(
            FIXTURES_PATH / "run-32112079288-safeoutputs.jsonl"
        )

        self.assertEqual(
            "release/13.5",
            resolve_target_branch(
                canonical_payload,
                EXPECTED_SOURCE_PR_NUMBER,
                raw_safe_outputs,
            ),
        )

    def test_agreeing_canonical_raw_and_notification_pass(self) -> None:
        self.assertEqual(
            "release/13.5",
            resolve_target_branch(
                payload(
                    [
                        create_item(
                            base="release/13.5",
                            base_branch="release/13.5",
                        )
                    ]
                ),
                EXPECTED_SOURCE_PR_NUMBER,
                [raw_create_item()],
            ),
        )

    def test_conflicting_create_fields_fail(self) -> None:
        with self.assertRaisesRegex(
            SafeOutputTargetError,
            "base and base_branch disagree",
        ):
            resolve_target_branch(
                payload(
                    [
                        create_item(
                            base="release/13.5",
                            base_branch="main",
                        )
                    ]
                ),
                EXPECTED_SOURCE_PR_NUMBER,
            )

    def test_create_and_notification_conflict_fails(self) -> None:
        with self.assertRaisesRegex(
            SafeOutputTargetError,
            "Canonical create_pull_request target branch main "
            "does not match notify_source_pr target_branch release/13.5",
        ):
            resolve_target_branch(
                payload(
                    [create_item(base="main")],
                    [notification(target_branch="release/13.5")],
                ),
                EXPECTED_SOURCE_PR_NUMBER,
            )

    def test_canonical_and_raw_conflict_fails(self) -> None:
        with self.assertRaisesRegex(
            SafeOutputTargetError,
            "does not match raw create_pull_request target branch",
        ):
            resolve_target_branch(
                payload(
                    [create_item(base="main")],
                    [notification(target_branch="main")],
                ),
                EXPECTED_SOURCE_PR_NUMBER,
                [raw_create_item()],
            )

    def test_raw_and_notification_conflict_fails(self) -> None:
        with self.assertRaisesRegex(
            SafeOutputTargetError,
            "Raw create_pull_request target branch release/13.5 "
            "does not match notify_source_pr target_branch main",
        ):
            resolve_target_branch(
                payload(
                    [create_item()],
                    [notification(target_branch="main")],
                ),
                EXPECTED_SOURCE_PR_NUMBER,
                [raw_create_item()],
            )

    def test_missing_raw_metadata_fails_when_canonical_base_is_absent(self) -> None:
        with self.assertRaisesRegex(
            SafeOutputTargetError,
            "raw safe-output metadata is unavailable",
        ):
            resolve_target_branch(
                payload([create_item()]),
                EXPECTED_SOURCE_PR_NUMBER,
            )

    def test_malformed_raw_metadata_fails(self) -> None:
        cases = {
            "missing branch": {
                "type": "create_pull_request",
                "base_commit": "aa2777825624f037a77160728939e36f7c788eff",
            },
            "invalid base": raw_create_item(base="release/latest"),
            "invalid base_branch": raw_create_item(base_branch=13.5),
            "conflicting branches": raw_create_item(base="main"),
            "missing base commit": raw_create_item(),
            "invalid base commit": raw_create_item(base_commit="not-a-commit"),
        }
        cases["missing base commit"].pop("base_commit")

        for name, raw_create in cases.items():
            with self.subTest(name=name):
                with self.assertRaises(SafeOutputTargetError):
                    resolve_target_branch(
                        payload([create_item()]),
                        EXPECTED_SOURCE_PR_NUMBER,
                        [raw_create],
                    )

    def test_missing_and_duplicate_raw_create_items_fail(self) -> None:
        for name, raw_safe_outputs in {
            "missing": [notification()],
            "duplicate": [raw_create_item(), raw_create_item()],
        }.items():
            with self.subTest(name=name):
                with self.assertRaises(SafeOutputTargetError):
                    resolve_target_branch(
                        payload([create_item()]),
                        EXPECTED_SOURCE_PR_NUMBER,
                        raw_safe_outputs,
                    )

    def test_malformed_targets_fail(self) -> None:
        cases = {
            "current base wrong type": create_item(base=13.5),
            "invalid current base": create_item(base="release/latest"),
            "legacy base wrong type": create_item(base_branch=13.5),
            "invalid legacy base": create_item(base_branch="release/latest"),
        }
        for name, create in cases.items():
            with self.subTest(name=name):
                with self.assertRaises(SafeOutputTargetError):
                    resolve_target_branch(
                        payload([create]),
                        EXPECTED_SOURCE_PR_NUMBER,
                    )

        for name, notify in {
            "target wrong type": notification(target_branch=13.5),
            "invalid target": notification(target_branch="release/latest"),
            "result wrong type": notification(result=1),
            "wrong result": notification(result="skipped"),
            "source PR wrong type": notification(source_pr_number=17235.0),
            "wrong source PR": notification(source_pr_number=17236),
        }.items():
            with self.subTest(name=name):
                with self.assertRaises(SafeOutputTargetError):
                    resolve_target_branch(
                        payload([create_item()], [notify]),
                        EXPECTED_SOURCE_PR_NUMBER,
                    )

        for field_name in ("result", "source_pr_number", "target_branch"):
            with self.subTest(name=f"missing {field_name}"):
                notify = notification()
                notify.pop(field_name)
                with self.assertRaises(SafeOutputTargetError):
                    resolve_target_branch(
                        payload([create_item()], [notify]),
                        EXPECTED_SOURCE_PR_NUMBER,
                    )

    def test_missing_and_duplicate_items_fail(self) -> None:
        cases = {
            "missing create": payload([]),
            "duplicate create": payload([create_item(), create_item()]),
            "missing notification": payload([create_item()], []),
            "duplicate notification": payload(
                [create_item()],
                [notification(), notification()],
            ),
        }
        for name, case in cases.items():
            with self.subTest(name=name):
                with self.assertRaises(SafeOutputTargetError):
                    resolve_target_branch(case, EXPECTED_SOURCE_PR_NUMBER)

    def test_cli_writes_resolved_branch(self) -> None:
        with tempfile.TemporaryDirectory() as temp_directory:
            temp_path = Path(temp_directory)
            agent_output = temp_path / "agent_output.json"
            raw_safe_outputs = temp_path / "safeoutputs.jsonl"
            github_output = temp_path / "github_output.txt"
            agent_output.write_text(
                json.dumps(payload([create_item()])),
                encoding="utf-8",
            )
            raw_safe_outputs.write_text(
                json.dumps(raw_create_item()) + "\n",
                encoding="utf-8",
            )

            result = main(
                [
                    "--agent-output",
                    str(agent_output),
                    "--raw-safe-outputs",
                    str(raw_safe_outputs),
                    "--github-output",
                    str(github_output),
                    "--expected-source-pr-number",
                    str(EXPECTED_SOURCE_PR_NUMBER),
                ]
            )

            self.assertEqual(0, result)
            self.assertEqual(
                "branch=release/13.5\n",
                github_output.read_text(encoding="utf-8"),
            )


if __name__ == "__main__":
    unittest.main()
