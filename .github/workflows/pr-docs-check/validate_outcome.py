#!/usr/bin/env python3

import argparse
import json
from pathlib import Path
from typing import Any, Sequence


class OutcomeValidationError(ValueError):
    pass


def _require_pr_number(value: object, field_name: str) -> int:
    if (
        not isinstance(value, int)
        or isinstance(value, bool)
        or value <= 0
        or value > 10_000_000
    ):
        raise OutcomeValidationError(f"Invalid {field_name}: {value}.")

    return value


def validate_outcome(
    payload: Any,
    created_pr_url: str,
    expected_source_pr_number: object,
) -> str:
    expected_source_pr_number = _require_pr_number(
        expected_source_pr_number,
        "expected source PR number",
    )

    items = payload.get("items") if isinstance(payload, dict) else None
    if not isinstance(items, list):
        items = []

    notifications = [
        item
        for item in items
        if isinstance(item, dict) and item.get("type") == "notify_source_pr"
    ]
    if len(notifications) != 1:
        raise OutcomeValidationError(
            f"Expected exactly one notify_source_pr item, found {len(notifications)}."
        )

    item = notifications[0]
    source_pr_number = _require_pr_number(
        item.get("source_pr_number"),
        "source_pr_number from agent",
    )
    if source_pr_number != expected_source_pr_number:
        raise OutcomeValidationError(
            "Agent source_pr_number "
            f"{source_pr_number} does not match triggering source PR "
            f"{expected_source_pr_number}."
        )

    result = str(item.get("result") or "").strip().lower()
    created_pr_url = created_pr_url.strip()
    if result == "drafted" and created_pr_url:
        return f"Confirmed drafted documentation PR: {created_pr_url}"
    if result == "skipped" and not created_pr_url:
        return "Confirmed that no documentation update is needed."
    if result == "draft_failed":
        raise OutcomeValidationError(
            "Documentation was required, but no docs PR was created."
        )
    if result == "drafted":
        raise OutcomeValidationError(
            "The agent reported documentation as drafted, but safe outputs did not create a docs PR."
        )
    if result == "skipped":
        raise OutcomeValidationError(
            f"The agent reported no documentation was needed, but safe outputs created {created_pr_url}."
        )

    raise OutcomeValidationError(
        f"Agent returned unsupported documentation result: {result or '(empty)'}."
    )


def load_payload(path: Path) -> Any:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError as error:
        raise OutcomeValidationError(f"Agent output file not found at {path}.") from error
    except json.JSONDecodeError as error:
        raise OutcomeValidationError(f"Failed to parse agent output: {error}.") from error


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--agent-output", required=True, type=Path)
    parser.add_argument("--created-pr-url", default="")
    parser.add_argument("--expected-source-pr-number", required=True, type=int)
    args = parser.parse_args(argv)

    try:
        message = validate_outcome(
            load_payload(args.agent_output),
            args.created_pr_url,
            args.expected_source_pr_number,
        )
    except OutcomeValidationError as error:
        print(f"::error::{error}")
        return 1

    print(message)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
