#!/usr/bin/env python3

import argparse
import json
import re
from pathlib import Path
from typing import Any, Sequence


class OutcomeValidationError(ValueError):
    pass


_TARGET_BRANCH_RE = re.compile(r"^(?:main|release/[0-9]+\.[0-9]+(?:\.[0-9]+)?)$")
_CREATED_PR_URL_RE = re.compile(
    r"^https://github\.com/microsoft/aspire\.dev/pull/[1-9][0-9]*$"
)


def encode_workflow_command_data(value: object) -> str:
    return str(value).replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")


def _require_pr_number(value: object, field_name: str) -> int:
    if (
        not isinstance(value, int)
        or isinstance(value, bool)
        or value <= 0
        or value > 10_000_000
    ):
        raise OutcomeValidationError(f"Invalid {field_name}: {value}.")

    return value


def _get_items(payload: Any) -> list[Any]:
    items = payload.get("items") if isinstance(payload, dict) else None
    return items if isinstance(items, list) else []


def _get_notification(payload: Any) -> dict[str, Any]:
    notifications = [
        item
        for item in _get_items(payload)
        if isinstance(item, dict) and item.get("type") == "notify_source_pr"
    ]
    if len(notifications) != 1:
        raise OutcomeValidationError(
            f"Expected exactly one notify_source_pr item, found {len(notifications)}."
        )

    return notifications[0]


def _validate_agent_association(
    payload: Any,
    expected_source_pr_number: object,
) -> dict[str, Any]:
    expected_source_pr_number = _require_pr_number(
        expected_source_pr_number,
        "expected source PR number",
    )
    item = _get_notification(payload)
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

    return item


def _has_create_pull_request(payload: Any) -> bool:
    return any(
        isinstance(item, dict) and item.get("type") == "create_pull_request"
        for item in _get_items(payload)
    )


def _get_create_pull_request(payload: Any) -> dict[str, Any]:
    create_pull_requests = [
        item
        for item in _get_items(payload)
        if isinstance(item, dict) and item.get("type") == "create_pull_request"
    ]
    if len(create_pull_requests) != 1:
        raise OutcomeValidationError(
            "Expected exactly one create_pull_request item for a drafted outcome, "
            f"found {len(create_pull_requests)}."
        )

    return create_pull_requests[0]


def _require_target_branch(value: object, field_name: str) -> str:
    if not isinstance(value, str) or _TARGET_BRANCH_RE.fullmatch(value) is None:
        raise OutcomeValidationError(f"Invalid {field_name}.")

    return value


def _get_create_pull_request_target(payload: Any) -> str:
    create_pull_request = _get_create_pull_request(payload)
    has_base = "base" in create_pull_request
    has_base_branch = "base_branch" in create_pull_request
    if not has_base and not has_base_branch:
        raise OutcomeValidationError(
            "Canonical create_pull_request target branch is missing."
        )

    if has_base and has_base_branch:
        base = _require_target_branch(
            create_pull_request.get("base"),
            "canonical create_pull_request base",
        )
        base_branch = _require_target_branch(
            create_pull_request.get("base_branch"),
            "canonical create_pull_request base_branch",
        )
        if base != base_branch:
            raise OutcomeValidationError(
                "Canonical create_pull_request base and base_branch disagree."
            )
        return base

    field_name = "base" if has_base else "base_branch"
    return _require_target_branch(
        create_pull_request.get(field_name),
        f"canonical create_pull_request {field_name}",
    )


def _validate_drafted_base_contract(
    payload: Any,
    notification: dict[str, Any],
    created_pr_url: str,
    created_pr_base: str,
) -> None:
    if _CREATED_PR_URL_RE.fullmatch(created_pr_url) is None:
        raise OutcomeValidationError(
            "Safe outputs returned an invalid microsoft/aspire.dev pull request URL."
        )

    canonical_base = _get_create_pull_request_target(payload)
    notification_target = _require_target_branch(
        notification.get("target_branch"),
        "notify_source_pr target_branch",
    )
    if notification_target != canonical_base:
        raise OutcomeValidationError(
            "Canonical create_pull_request target branch "
            f"{canonical_base} does not match notify_source_pr target_branch "
            f"{notification_target}."
        )

    actual_base = _require_target_branch(created_pr_base, "drafted PR base branch")
    if actual_base != canonical_base:
        raise OutcomeValidationError(
            f"Drafted PR base branch {actual_base} does not match canonical "
            f"create_pull_request target branch {canonical_base}."
        )


def _validate_outcome(
    payload: Any,
    created_pr_url: str,
    expected_source_pr_number: object,
    created_pr_base: str,
) -> str:
    item = _validate_agent_association(payload, expected_source_pr_number)

    result = str(item.get("result") or "").strip().lower()
    created_pr_url = created_pr_url.strip()
    if result == "drafted" and created_pr_url:
        _validate_drafted_base_contract(
            payload,
            item,
            created_pr_url,
            created_pr_base,
        )
        return f"Confirmed drafted documentation PR: {created_pr_url}"
    if result == "skipped" and _has_create_pull_request(payload):
        raise OutcomeValidationError(
            "The agent reported no documentation was needed, but also requested a docs PR."
        )
    if result == "skipped" and not created_pr_url:
        return "Confirmed that no documentation update is needed."
    if result == "draft_failed" and created_pr_url:
        raise OutcomeValidationError(
            f"The agent reported documentation drafting failed, but safe outputs created {created_pr_url}."
        )
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


def validate_outcome(
    payload: Any,
    created_pr_url: str,
    expected_source_pr_number: object,
    created_pr_base: str = "",
) -> str:
    return _validate_outcome(
        payload,
        created_pr_url,
        expected_source_pr_number,
        created_pr_base,
    )


def load_expected_source_pr_number(path: Path) -> int:
    event = load_payload(path)
    if not isinstance(event, dict):
        raise OutcomeValidationError("Workflow event payload must be a JSON object.")

    pull_request = event.get("pull_request")
    raw_number = (
        pull_request.get("number")
        if isinstance(pull_request, dict)
        else None
    )
    if raw_number is None:
        inputs = event.get("inputs")
        raw_number = inputs.get("pr_number") if isinstance(inputs, dict) else None
        if isinstance(raw_number, str):
            raw_number = raw_number.strip()
            if raw_number.isascii() and raw_number.isdigit():
                raw_number = int(raw_number)

    return _require_pr_number(raw_number, "triggering source PR number")


def build_side_effect_outcome(
    payload: Any,
    created_pr_url: str,
    expected_source_pr_number: int,
    created_pr_base: str,
) -> dict[str, Any]:
    base_outcome: dict[str, Any] = {
        "allow_comment": False,
        "allow_sme_review": False,
        "diagnostic": "",
        "render_kind": "invalid",
        "source_pr_number": expected_source_pr_number,
        "summary": "",
        "target_branch": "",
        "sme_login": "",
    }

    try:
        item = _validate_agent_association(payload, expected_source_pr_number)
    except OutcomeValidationError as error:
        notifications = [
            item
            for item in _get_items(payload)
            if isinstance(item, dict) and item.get("type") == "notify_source_pr"
        ]
        base_outcome["diagnostic"] = str(error)
        # Missing or duplicate notifications can safely render a generic warning on
        # the trusted event PR. A sole invalid/mismatched association cannot.
        base_outcome["allow_comment"] = len(notifications) != 1
        return base_outcome

    base_outcome.update(
        {
            "allow_comment": True,
            "summary": str(item.get("summary") or ""),
            "target_branch": str(item.get("target_branch") or ""),
            "sme_login": str(item.get("sme_login") or ""),
        }
    )

    result = str(item.get("result") or "").strip().lower()
    created_pr_url = created_pr_url.strip()
    try:
        _validate_outcome(
            payload,
            created_pr_url,
            expected_source_pr_number,
            created_pr_base,
        )
    except OutcomeValidationError as error:
        base_outcome["diagnostic"] = str(error)
        if result == "drafted" and not created_pr_url:
            base_outcome["render_kind"] = "drafted_missing_pr"
        elif result == "draft_failed" and not created_pr_url:
            base_outcome["render_kind"] = "draft_failed"
        return base_outcome

    base_outcome["render_kind"] = result
    base_outcome["allow_sme_review"] = result == "drafted"
    return base_outcome


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
    parser.add_argument("--created-pr-base", default="")
    parser.add_argument("--expected-source-pr-number", type=int)
    parser.add_argument("--github-event-path", type=Path)
    parser.add_argument("--write-side-effect-outcome", type=Path)
    args = parser.parse_args(argv)

    if args.write_side_effect_outcome is not None:
        if args.github_event_path is None:
            parser.error("--github-event-path is required with --write-side-effect-outcome")

        try:
            expected_source_pr_number = load_expected_source_pr_number(
                args.github_event_path
            )
            payload = load_payload(args.agent_output)
            outcome = build_side_effect_outcome(
                payload,
                args.created_pr_url,
                expected_source_pr_number,
                args.created_pr_base,
            )
        except OutcomeValidationError as error:
            outcome = {
                "allow_comment": False,
                "allow_sme_review": False,
                "diagnostic": str(error),
                "render_kind": "invalid",
            }

        args.write_side_effect_outcome.write_text(
            json.dumps(outcome),
            encoding="utf-8",
        )
        return 0

    if args.expected_source_pr_number is None:
        parser.error("--expected-source-pr-number is required")

    try:
        message = validate_outcome(
            load_payload(args.agent_output),
            args.created_pr_url,
            args.expected_source_pr_number,
            args.created_pr_base,
        )
    except OutcomeValidationError as error:
        print(f"::error::{encode_workflow_command_data(error)}")
        return 1

    print(message)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
