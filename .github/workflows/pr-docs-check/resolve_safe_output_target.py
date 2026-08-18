#!/usr/bin/env python3

import argparse
import json
import re
import sys
from pathlib import Path
from typing import Any, Sequence


class SafeOutputTargetError(ValueError):
    pass


_TARGET_BRANCH_RE = re.compile(r"^(?:main|release/[0-9]+\.[0-9]+(?:\.[0-9]+)?)$")
_COMMIT_RE = re.compile(r"^(?:[0-9a-f]{40}|[0-9a-f]{64})$")


def _get_items(payload: Any) -> list[Any]:
    items = payload.get("items") if isinstance(payload, dict) else None
    return items if isinstance(items, list) else []


def _require_pr_number(value: object, field_name: str) -> int:
    if (
        not isinstance(value, int)
        or isinstance(value, bool)
        or value <= 0
        or value > 10_000_000
    ):
        raise SafeOutputTargetError(f"Invalid {field_name}: {value}.")

    return value


def require_target_branch(value: object, field_name: str) -> str:
    if not isinstance(value, str) or _TARGET_BRANCH_RE.fullmatch(value) is None:
        raise SafeOutputTargetError(f"Invalid {field_name}.")

    return value


def _get_branch_candidates(
    item: dict[str, Any],
    source_name: str,
) -> list[tuple[str, str]]:
    candidates = []
    for field_name in ("base", "base_branch"):
        if field_name in item:
            candidates.append(
                (
                    field_name,
                    require_target_branch(
                        item.get(field_name),
                        f"{source_name} {field_name}",
                    ),
                )
            )

    if len(candidates) == 2 and candidates[0][1] != candidates[1][1]:
        raise SafeOutputTargetError(
            f"{source_name.capitalize()} base and base_branch disagree."
        )

    return candidates


def _get_raw_target(raw_safe_outputs: Sequence[Any]) -> str:
    raw_create_items = [
        item
        for item in raw_safe_outputs
        if isinstance(item, dict) and item.get("type") == "create_pull_request"
    ]
    if len(raw_create_items) != 1:
        raise SafeOutputTargetError(
            "Expected exactly one raw create_pull_request item, "
            f"found {len(raw_create_items)}."
        )

    raw_create_item = raw_create_items[0]
    base_commit = raw_create_item.get("base_commit")
    if not isinstance(base_commit, str) or _COMMIT_RE.fullmatch(base_commit) is None:
        raise SafeOutputTargetError(
            "Raw create_pull_request base_commit is invalid."
        )

    candidates = _get_branch_candidates(
        raw_create_item,
        "raw create_pull_request",
    )
    if not candidates:
        raise SafeOutputTargetError(
            "Raw create_pull_request target branch is missing."
        )

    return candidates[0][1]


def resolve_target_branch(
    payload: Any,
    expected_source_pr_number: object,
    raw_safe_outputs: Sequence[Any] | None = None,
) -> str:
    expected_source_pr_number = _require_pr_number(
        expected_source_pr_number,
        "expected source PR number",
    )
    create_items = [
        item
        for item in _get_items(payload)
        if isinstance(item, dict) and item.get("type") == "create_pull_request"
    ]
    if len(create_items) != 1:
        raise SafeOutputTargetError(
            "Expected exactly one canonical create_pull_request item, "
            f"found {len(create_items)}."
        )

    notifications = [
        item
        for item in _get_items(payload)
        if isinstance(item, dict) and item.get("type") == "notify_source_pr"
    ]
    if len(notifications) != 1:
        raise SafeOutputTargetError(
            "Expected exactly one canonical notify_source_pr item for a drafted "
            f"outcome, found {len(notifications)}."
        )

    notification = notifications[0]
    if notification.get("result") != "drafted":
        raise SafeOutputTargetError(
            "Canonical notify_source_pr result must be drafted when creating a pull request."
        )

    source_pr_number = _require_pr_number(
        notification.get("source_pr_number"),
        "source_pr_number from canonical notify_source_pr",
    )
    if source_pr_number != expected_source_pr_number:
        raise SafeOutputTargetError(
            "Canonical notify_source_pr source_pr_number "
            f"{source_pr_number} does not match triggering source PR "
            f"{expected_source_pr_number}."
        )

    notification_target = require_target_branch(
        notification.get("target_branch"),
        "canonical notify_source_pr target_branch",
    )

    # gh-aw canonical output normally records a supported base field as:
    #   {"type":"create_pull_request","base":"release/13.5",...}
    # In v0.86.2, canonicalization can strip the safe-output server's injected
    # base metadata. The raw JSONL retains that validated base and base commit:
    #   {"type":"create_pull_request","base_branch":"release/13.5",
    #    "base_commit":"aa2777825624f037a77160728939e36f7c788eff",...}
    create_item = create_items[0]
    canonical_candidates = _get_branch_candidates(
        create_item,
        "canonical create_pull_request",
    )
    raw_target = (
        _get_raw_target(raw_safe_outputs)
        if raw_safe_outputs is not None
        else None
    )
    if not canonical_candidates and raw_target is None:
        raise SafeOutputTargetError(
            "Canonical create_pull_request target branch is missing and raw "
            "safe-output metadata is unavailable."
        )

    canonical_target = (
        canonical_candidates[0][1] if canonical_candidates else None
    )
    if (
        canonical_target is not None
        and raw_target is not None
        and canonical_target != raw_target
    ):
        raise SafeOutputTargetError(
            "Canonical create_pull_request target branch "
            f"{canonical_target} does not match raw create_pull_request target "
            f"branch {raw_target}."
        )

    target_branch = canonical_target or raw_target
    target_source = (
        "canonical create_pull_request"
        if canonical_target is not None
        else "raw create_pull_request"
    )
    if target_branch != notification_target:
        raise SafeOutputTargetError(
            f"{target_source.capitalize()} target branch "
            f"{target_branch} does not match notify_source_pr target_branch "
            f"{notification_target}."
        )

    return target_branch


def load_payload(path: Path) -> Any:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise SafeOutputTargetError(
            f"Failed to read canonical agent output: {error}"
        ) from error


def load_raw_safe_outputs(path: Path) -> list[Any]:
    try:
        lines = path.read_text(encoding="utf-8").splitlines()
    except OSError as error:
        raise SafeOutputTargetError(
            f"Failed to read raw safe outputs: {error}"
        ) from error

    items = []
    for line_number, line in enumerate(lines, start=1):
        if not line.strip():
            continue
        try:
            items.append(json.loads(line))
        except json.JSONDecodeError as error:
            raise SafeOutputTargetError(
                f"Failed to parse raw safe outputs at line {line_number}: {error}"
            ) from error

    return items


def load_expected_source_pr_number(value: str) -> int:
    try:
        parsed = int(value)
    except ValueError as error:
        raise SafeOutputTargetError(
            f"Invalid expected source PR number: {value}."
        ) from error

    return _require_pr_number(parsed, "expected source PR number")


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--agent-output", required=True, type=Path)
    parser.add_argument("--raw-safe-outputs", required=True, type=Path)
    parser.add_argument("--github-output", required=True, type=Path)
    parser.add_argument("--expected-source-pr-number", required=True)
    args = parser.parse_args(argv)

    try:
        payload = load_payload(args.agent_output)
        raw_safe_outputs = load_raw_safe_outputs(args.raw_safe_outputs)
        expected_source_pr_number = load_expected_source_pr_number(
            args.expected_source_pr_number
        )
        target_branch = resolve_target_branch(
            payload,
            expected_source_pr_number,
            raw_safe_outputs,
        )
        with args.github_output.open("a", encoding="utf-8") as github_output:
            github_output.write(f"branch={target_branch}\n")
    except SafeOutputTargetError as error:
        print(f"ERROR: {error}", file=sys.stderr)
        return 1

    return 0


if __name__ == "__main__":
    sys.exit(main())
