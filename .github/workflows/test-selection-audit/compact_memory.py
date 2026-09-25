"""Compact the test-selection audit's rolling repo-memory ledgers.

The workflow keeps one immutable contribution per PR head in
``processed-runs.jsonl`` and a derived watchlist in ``watchlist.jsonl``.
Before each audit, this script drops expired raw contributions, recomputes
watch counters and examples from the retained identities, and preserves
settled dispositions even when their active count reaches zero.
"""

from __future__ import annotations

import datetime
import json
import os
import pathlib


def require_utc_date(value: object, context: str, audit_date: str) -> str:
    if not isinstance(value, str):
        raise ValueError(f"{context} must be a UTC date")
    try:
        parsed = datetime.date.fromisoformat(value)
    except ValueError as error:
        raise ValueError(f"{context} must be a real UTC date no later than {audit_date}") from error
    if parsed.isoformat() != value or value > audit_date:
        raise ValueError(f"{context} must be a real UTC date no later than {audit_date}")
    return value


def read_rows(memory_root: pathlib.Path, file_name: str) -> dict[str, object]:
    file_path = memory_root / file_name
    if not file_path.exists():
        return {"path": file_path, "exists": False, "rows": []}
    text = file_path.read_text(encoding="utf-8")
    if text and not text.endswith("\n"):
        raise ValueError(f"{file_name} must end with a newline")
    rows = [json.loads(line) for line in text.splitlines()] if text else []
    return {"path": file_path, "exists": True, "rows": rows}


def write_rows(file: dict[str, object], rows: list[dict[str, object]]) -> None:
    if not file["exists"] and not rows:
        return
    file_path = file["path"]
    assert isinstance(file_path, pathlib.Path)
    temporary_path = file_path.with_name(f"{file_path.name}.compact-{os.getpid()}")
    text = "".join(f"{json.dumps(row, separators=(',', ':'))}\n" for row in rows)
    temporary_path.write_text(text, encoding="utf-8")
    temporary_path.replace(file_path)


def contribution_key(kind: str, path: str, target: str | None = None) -> tuple[str, ...]:
    return (kind, path) if target is None else (kind, path, target)


def main() -> None:
    memory_root = pathlib.Path(os.environ["MEMORY_ROOT"])
    audit_date_path = pathlib.Path(os.environ["AUDIT_DATE_PATH"])
    retention_text = os.environ.get("RETENTION_DAYS", "14")
    if not retention_text.isascii() or not retention_text.isdigit():
        raise ValueError("retention days must be between 1 and 90")
    retention_days = int(retention_text)
    if retention_days < 1 or retention_days > 90:
        raise ValueError("retention days must be between 1 and 90")

    audit_day = datetime.datetime.now(datetime.timezone.utc).date()
    audit_date = audit_day.isoformat()
    cutoff = (audit_day - datetime.timedelta(days=retention_days)).isoformat()
    audit_date_path.parent.mkdir(parents=True, exist_ok=True)
    audit_date_path.write_text(f"{audit_date}\n", encoding="utf-8")
    audit_date_path.chmod(0o444)

    processed_file = read_rows(memory_root, "processed-runs.jsonl")
    processed_rows = processed_file["rows"]
    assert isinstance(processed_rows, list)
    for index, row in enumerate(processed_rows, 1):
        if (
            not isinstance(row, dict)
            or not isinstance(row.get("pr"), int)
            or not isinstance(row.get("sha"), str)
            or not isinstance(row.get("over_paths"), list)
            or not isinstance(row.get("miss_edges"), list)
        ):
            raise ValueError(f"processed-runs.jsonl:{index} has an invalid compaction shape")
        require_utc_date(row.get("seen"), f"processed-runs.jsonl:{index}.seen", audit_date)

    retained = [row for row in processed_rows if row["seen"] >= cutoff]

    # Watch counters are derived data. Rebuild them from distinct retained
    # PR-head identities so reruns and stale values cannot inflate a finding.
    contributions: dict[tuple[str, ...], dict[str, set[object]]] = {}

    def add_contribution(key: tuple[str, ...], row: dict[str, object]) -> None:
        value = contributions.setdefault(
            key,
            {"identities": set(), "prs": set(), "dates": set()},
        )
        value["identities"].add((row["pr"], row["sha"]))
        value["prs"].add(row["pr"])
        value["dates"].add(row["seen"])

    for row in retained:
        for path_value in row["over_paths"]:
            add_contribution(contribution_key("over", path_value), row)
        for edge in row["miss_edges"]:
            add_contribution(contribution_key("miss", edge["path"], edge["target"]), row)

    watch_file = read_rows(memory_root, "watchlist.jsonl")
    watch_rows = watch_file["rows"]
    assert isinstance(watch_rows, list)
    watch: list[dict[str, object]] = []
    for index, existing in enumerate(watch_rows, 1):
        if not isinstance(existing, dict):
            raise ValueError(f"watchlist.jsonl:{index} has an invalid compaction shape")
        row = dict(existing)
        if row.get("kind") not in ("over-selection", "under-selection"):
            raise ValueError(f"watchlist.jsonl:{index} has an invalid kind")
        if not isinstance(row.get("example_prs"), list):
            raise ValueError(f"watchlist.jsonl:{index} has an invalid compaction shape")
        first_seen = require_utc_date(
            row.get("first_seen"),
            f"watchlist.jsonl:{index}.first_seen",
            audit_date,
        )
        last_seen = require_utc_date(
            row.get("last_seen"),
            f"watchlist.jsonl:{index}.last_seen",
            audit_date,
        )
        if first_seen > last_seen:
            raise ValueError(f"watchlist.jsonl:{index}.first_seen is after last_seen")

        if row["kind"] == "over-selection":
            key = contribution_key("over", row["path"])
        else:
            key = contribution_key("miss", row["path"], row["target"])
        contribution = contributions.get(key)
        count = len(contribution["identities"]) if contribution else 0

        # Active watch rows expire with their evidence. Settled decisions remain
        # so the agent does not repeatedly re-investigate known cases.
        if count == 0 and row.get("verdict") == "watch":
            continue
        if row["kind"] == "over-selection":
            row["all_runs"] = count
        else:
            row["miss_runs"] = count

        contributing_prs = contribution["prs"] if contribution else set()
        prior_examples = [pr for pr in row["example_prs"] if pr in contributing_prs]
        remaining_examples = sorted(contributing_prs - set(prior_examples))
        row["example_prs"] = (prior_examples + remaining_examples)[:3]
        if contribution:
            dates = contribution["dates"]
            row["first_seen"] = min(dates)
            row["last_seen"] = max(dates)
        watch.append(row)

    write_rows(processed_file, retained)
    write_rows(watch_file, watch)
    print(
        f"Retained {len(retained)}/{len(processed_rows)} processed rows since {cutoff}."
    )


if __name__ == "__main__":
    main()
