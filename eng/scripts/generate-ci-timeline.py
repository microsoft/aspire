#!/usr/bin/env python3
"""
Generate a CI timeline summary for GitHub Actions step summaries.

Produces a colored HTML Gantt chart and summary statistics from workflow run data.
Can write to $GITHUB_STEP_SUMMARY in CI, or produce a standalone HTML file locally.

Usage (local):
    python3 generate-ci-timeline.py --run-id 23783420243
    python3 generate-ci-timeline.py --run-id 23783420243 -o report.html
    python3 generate-ci-timeline.py --json ci-timeline-12345.json

Usage (CI — auto-detects from environment):
    python3 generate-ci-timeline.py

Environment variables (auto-detected in GitHub Actions):
    GITHUB_RUN_ID        - Workflow run ID
    GITHUB_REPOSITORY    - Repository (owner/repo)
    GITHUB_STEP_SUMMARY  - Path to write summary output (if set, also writes there)
"""

import argparse
import html
import json
import os
import re
import subprocess
import sys
from dataclasses import dataclass, field
from datetime import datetime


# ── Data models ──────────────────────────────────────────────────────────────

@dataclass
class JobInfo:
    name: str
    status: str
    conclusion: str
    created_at: float  # seconds from workflow start
    started_at: float  # seconds from workflow start
    completed_at: float  # seconds from workflow start
    runner_name: str
    html_url: str
    labels: list[str] = field(default_factory=list)

    @property
    def queue_time(self) -> float:
        return max(0, self.started_at - self.created_at)

    @property
    def run_time(self) -> float:
        return max(0, self.completed_at - self.started_at)


@dataclass
class JobGroup:
    name: str
    jobs: list[JobInfo] = field(default_factory=list)

    @property
    def first_created(self) -> float:
        return min(j.created_at for j in self.jobs)

    @property
    def first_started(self) -> float:
        return min(j.started_at for j in self.jobs)

    @property
    def last_completed(self) -> float:
        return max(j.completed_at for j in self.jobs)

    @property
    def span(self) -> float:
        return self.last_completed - self.first_started

    @property
    def avg_queue(self) -> float:
        queues = [j.queue_time for j in self.jobs]
        return sum(queues) / len(queues) if queues else 0

    @property
    def max_queue(self) -> float:
        return max((j.queue_time for j in self.jobs), default=0)

    @property
    def conclusion(self) -> str:
        conclusions = {j.conclusion for j in self.jobs}
        if "failure" in conclusions:
            return "failure"
        if "cancelled" in conclusions:
            return "cancelled"
        if conclusions == {"skipped"}:
            return "skipped"
        return "success"

    @property
    def job_count(self) -> int:
        return len(self.jobs)


# ── Data fetching ────────────────────────────────────────────────────────────

def gh_api(endpoint: str) -> dict | list:
    """Call the GitHub API via gh CLI."""
    cmd = ["gh", "api", endpoint, "--paginate"]
    result = subprocess.run(cmd, capture_output=True, text=True)
    if result.returncode != 0:
        print(f"Error calling GitHub API: {result.stderr.strip()}", file=sys.stderr)
        sys.exit(1)

    text = result.stdout.strip()
    if not text:
        return {}

    decoder = json.JSONDecoder()
    objects, idx = [], 0
    while idx < len(text):
        if text[idx] in " \n\r\t":
            idx += 1
            continue
        obj, end = decoder.raw_decode(text, idx)
        objects.append(obj)
        idx = end

    if len(objects) == 1:
        return objects[0]

    if isinstance(objects[0], list):
        merged = []
        for o in objects:
            merged.extend(o if isinstance(o, list) else [o])
        return merged

    if isinstance(objects[0], dict):
        merged = {}
        for o in objects:
            for k, v in o.items():
                if isinstance(v, list) and isinstance(merged.get(k), list):
                    merged[k].extend(v)
                else:
                    merged[k] = v
        return merged

    return objects[0]


def parse_ts(ts: str | None) -> datetime | None:
    if not ts:
        return None
    return datetime.fromisoformat(ts.replace("Z", "+00:00"))


def fetch_run_data(repo: str, run_id: str) -> tuple[dict, list[dict]]:
    """Fetch workflow run info and jobs from GitHub API."""
    run_info = gh_api(f"/repos/{repo}/actions/runs/{run_id}")
    jobs_data = gh_api(f"/repos/{repo}/actions/runs/{run_id}/jobs")
    jobs = jobs_data.get("jobs", []) if isinstance(jobs_data, dict) else jobs_data
    return run_info, jobs


def load_json_data(path: str) -> tuple[dict, list[dict]]:
    """Load run data from a cached JSON file (ci-timeline format)."""
    with open(path, encoding="utf-8") as f:
        data = json.load(f)
    run_info = data.get("run_info", {})
    jobs = data.get("jobs", [])
    return run_info, jobs


# ── Job grouping ─────────────────────────────────────────────────────────────

def group_base_name(job_name: str) -> str:
    """Extract the base name, stripping matrix parameters in parens."""
    return re.sub(r"\s*\(.*\)", "", job_name).strip()


def short_name(group_name: str) -> str:
    """Shorten a group name for display."""
    name = group_name
    name = re.sub(r"^Tests\s*/\s*", "", name)
    parts = [p.strip() for p in name.split("/")]
    if len(parts) == 2 and parts[0] == parts[1]:
        name = parts[0]
    return name


def build_job_groups(jobs: list[dict], t0: datetime) -> list[JobGroup]:
    """Parse raw job data and group by base name."""
    groups: dict[str, JobGroup] = {}

    for raw in jobs:
        created = parse_ts(raw.get("created_at"))
        started = parse_ts(raw.get("started_at"))
        completed = parse_ts(raw.get("completed_at"))

        if not started or not completed:
            continue

        # Skip jobs from a previous attempt — their started_at predates the
        # workflow creation time, meaning they completed in an earlier run and
        # weren't actually re-executed.
        if created and started < created:
            continue

        job = JobInfo(
            name=raw["name"],
            status=raw.get("status", ""),
            conclusion=raw.get("conclusion", ""),
            created_at=(created - t0).total_seconds() if created else (started - t0).total_seconds(),
            started_at=(started - t0).total_seconds(),
            completed_at=(completed - t0).total_seconds(),
            runner_name=raw.get("runner_name", ""),
            html_url=raw.get("html_url", ""),
            labels=raw.get("labels", []),
        )

        base = group_base_name(raw["name"])
        if base not in groups:
            groups[base] = JobGroup(name=base)
        groups[base].jobs.append(job)

    return sorted(groups.values(), key=lambda g: g.first_started)


# ── Rendering helpers ────────────────────────────────────────────────────────

def fmt_duration(seconds: float) -> str:
    seconds = max(0, seconds)
    if seconds < 60:
        return f"{seconds:.0f}s"
    minutes = int(seconds // 60)
    secs = int(seconds % 60)
    if minutes < 60:
        return f"{minutes}m{secs:02d}s"
    hours = int(minutes // 60)
    mins = minutes % 60
    return f"{hours}h{mins:02d}m"


# Colors matching standard CI/CD pipeline reports
COLOR_SUCCESS = "#2da44e"   # green
COLOR_FAILURE = "#cf222e"   # red
COLOR_CANCELLED = "#6e7781" # gray
COLOR_SKIPPED = "#afb8c1"   # light gray
COLOR_QUEUE = "#54aeff"     # blue
COLOR_DEP_WAIT = "#d0d7de"  # light gray for dependency wait

STATUS_ICON = {
    "success": "✅",
    "failure": "❌",
    "cancelled": "⚪",
    "skipped": "⏭️",
}

RUN_COLORS = {
    "success": COLOR_SUCCESS,
    "failure": COLOR_FAILURE,
    "cancelled": COLOR_CANCELLED,
    "skipped": COLOR_SKIPPED,
}


RUNNER_EMOJI = {
    "ubuntu": "🐧",
    "windows": "🪟",
    "macos": "🍎",
}


def runner_icon(label: str) -> str:
    """Map a runner label to an emoji."""
    lower = label.lower()
    for key, emoji in RUNNER_EMOJI.items():
        if key in lower:
            # Mark large runners
            if "8-core" in lower or "16-core" in lower:
                return emoji + "⚡"
            return emoji
    return "🖥️"


# ── Phase classification ─────────────────────────────────────────────────────

def classify_phase(group_name: str) -> str:
    """Classify a job group into a high-level phase."""
    lower = group_name.lower()
    if "prepare" in lower or "setup" in lower:
        return "Setup"
    if "build" in lower and "template" not in lower:
        return "Build"
    if "template" in lower:
        return "Templates"
    if "polyglot" in lower or "sdk validation" in lower:
        return "Validation"
    if "cli starter" in lower:
        return "Validation"
    if "vs code extension" in lower or "extension tests" in lower:
        return "Validation"
    if "java sdk unit" in lower or "typescript sdk unit" in lower:
        return "Validation"
    if "final" in lower or "results" in lower:
        return "Results"
    return "Tests"


# ── HTML timeline chart ──────────────────────────────────────────────────────


def render_timeline_bars(groups: list[JobGroup], total_seconds: float,
                         min_total_seconds: float = 0) -> str:
    """Render an HTML table with individual jobs per runner, organized by phase."""
    if not groups or total_seconds <= 0:
        return "No job data to display.\n"

    # Classify each job into a phase, collect individual jobs
    phase_jobs: dict[str, list[JobInfo]] = {}
    for g in groups:
        phase = classify_phase(g.name)
        for j in g.jobs:
            if j.completed_at >= min_total_seconds and (j.run_time >= 60 or j.conclusion in ("failure", "cancelled")):
                phase_jobs.setdefault(phase, []).append(j)

    for phase in phase_jobs:
        phase_jobs[phase].sort(key=lambda j: j.completed_at, reverse=True)

    lines = []
    lines.append('<table>')
    lines.append(
        '<tr><th>Job</th>'
        '<th>Total</th><th>Deps</th><th>Queue</th><th>Run</th></tr>'
    )

    phase_order = ["Setup", "Build", "Tests", "Templates", "Validation"]
    for phase in phase_order:
        jobs_in_phase = phase_jobs.get(phase, [])
        if not jobs_in_phase:
            continue

        lines.append(
            f'<tr><td colspan="5"><b>📁 {phase}</b>'
            f' ({len(jobs_in_phase)} jobs)</td></tr>'
        )

        for j in jobs_in_phase:
            name = html.escape(short_name(group_base_name(j.name)))
            runner = j.labels[0] if j.labels else ""
            icon = STATUS_ICON.get(j.conclusion, "")
            ri = runner_icon(runner) + " " if runner else ""

            lines.append(
                f'<tr>'
                f'<td>{icon} {ri}<code>{name}</code></td>'
                f'<td><b>{fmt_duration(j.completed_at)}</b></td>'
                f'<td>{fmt_duration(j.created_at)}</td>'
                f'<td>{fmt_duration(j.queue_time)}</td>'
                f'<td>{fmt_duration(j.run_time)}</td>'
                f'</tr>'
            )

    lines.append('</table>')
    return "\n".join(lines)


# ── Critical path ────────────────────────────────────────────────────────────

def render_critical_path(groups: list[JobGroup], total_seconds: float,
                         min_total_seconds: float = 300) -> str:
    """Show individual jobs sorted by total pipeline time.

    Lists one row per job (per runner), filtered to total > min_total_seconds.
    Total pipeline time = time from workflow start to job completion.
    """
    if not groups:
        return ""

    # Exclude gate/results jobs
    groups = [g for g in groups if not _is_results_phase(g)]

    # Collect all individual jobs, apply filter, take top 30
    all_jobs = [j for g in groups for j in g.jobs]
    threshold = max(min_total_seconds, 300)  # at least 5 min
    all_jobs = [j for j in all_jobs if j.completed_at > threshold and (j.run_time >= 60 or j.conclusion in ("failure", "cancelled"))]
    all_jobs.sort(key=lambda j: j.completed_at, reverse=True)
    all_jobs = all_jobs[:30]

    if not all_jobs:
        return ""

    lines = []
    lines.append('<table>')
    lines.append(
        '<tr><th>#</th><th>Job</th>'
        '<th>Total</th><th>Deps</th><th>Queue</th><th>Run</th></tr>'
    )

    for i, j in enumerate(all_jobs, 1):
        name = html.escape(short_name(group_base_name(j.name)))
        icon = STATUS_ICON.get(j.conclusion, "")
        runner = j.labels[0] if j.labels else ""
        ri = runner_icon(runner) + " " if runner else ""

        lines.append(
            f'<tr>'
            f'<td>{i}</td>'
            f'<td>{icon} {ri}<code>{name}</code></td>'
            f'<td><b>{fmt_duration(j.completed_at)}</b></td>'
            f'<td>{fmt_duration(j.created_at)}</td>'
            f'<td>{fmt_duration(j.queue_time)}</td>'
            f'<td>{fmt_duration(j.run_time)}</td>'
            f'</tr>'
        )

    lines.append('</table>')
    return "\n".join(lines)


# ── Hotspots ─────────────────────────────────────────────────────────────────

def _is_results_phase(g: JobGroup) -> bool:
    return classify_phase(g.name) == "Results"


def render_hotspots(groups: list[JobGroup]) -> str:
    """Show top queue waits — one row per individual job."""
    # Exclude gate/results jobs
    groups = [g for g in groups if not _is_results_phase(g)]
    all_jobs = [j for g in groups for j in g.jobs]
    if not all_jobs:
        return ""

    # Sort individual jobs by queue time, take top 10 with > 30s queue and > 1min run
    all_jobs = [j for g in groups for j in g.jobs if j.run_time >= 60 or j.conclusion in ("failure", "cancelled")]
    worst_jobs = sorted(all_jobs, key=lambda j: j.queue_time, reverse=True)
    worst_jobs = [j for j in worst_jobs[:10] if j.queue_time > 30]

    if not worst_jobs:
        return ""

    lines = ['<table>']
    lines.append('<tr><th>Job</th><th>Queue</th></tr>')
    for j in worst_jobs:
        name = html.escape(short_name(group_base_name(j.name)))
        runner = j.labels[0] if j.labels else ""
        ri = runner_icon(runner) + " " if runner else ""
        lines.append(
            f'<tr>'
            f'<td>⏳ {ri}<code>{name}</code></td>'
            f'<td><b>{fmt_duration(j.queue_time)}</b></td>'
            f'</tr>'
        )
    lines.append('</table>')

    return "\n".join(lines)


# ── Summary stats ────────────────────────────────────────────────────────────

def render_summary_table(groups: list[JobGroup], total_seconds: float) -> str:
    """Render HTML summary statistics table."""
    all_jobs = [j for g in groups for j in g.jobs]
    if not all_jobs:
        return ""

    conclusions = {}
    for j in all_jobs:
        conclusions[j.conclusion] = conclusions.get(j.conclusion, 0) + 1

    runner_labels = {}
    for j in all_jobs:
        for label in j.labels:
            runner_labels[label] = runner_labels.get(label, 0) + 1

    status_parts = []
    for status in ["success", "failure", "cancelled", "skipped"]:
        count = conclusions.get(status, 0)
        if count:
            icon = STATUS_ICON.get(status, "")
            status_parts.append(f"{icon} {count} {status}")

    label_parts = []
    if runner_labels:
        label_parts = [
            f"<code>{html.escape(l)}</code>: {c}"
            for l, c in sorted(runner_labels.items(), key=lambda x: -x[1])[:5]
        ]

    rows = [
        ("Total wall time", f"<b>{fmt_duration(total_seconds)}</b>"),
        ("Jobs", f"{len(all_jobs)} ({len(groups)} unique)"),
        ("Status", " · ".join(status_parts)),
    ]
    if label_parts:
        rows.append(("Runners", " · ".join(label_parts)))

    lines = ['<table>']
    lines.append('<tr><th>Metric</th><th>Value</th></tr>')
    for metric, value in rows:
        lines.append(f'<tr><td>{metric}</td><td>{value}</td></tr>')
    lines.append('</table>')

    return "\n".join(lines)


# ── Main output assembly ─────────────────────────────────────────────────────

def generate_summary(run_info: dict, jobs: list[dict],
                     min_total_minutes: float = 0) -> str:
    """Generate the full HTML summary."""
    # Use min(created_at) as baseline. On re-runs, created_at is reset to the
    # re-run time for all jobs while started_at keeps the original value for jobs
    # that weren't re-executed — so created_at is the reliable epoch.
    all_created = [parse_ts(j.get("created_at")) for j in jobs if j.get("created_at")]
    all_completed = [parse_ts(j.get("completed_at")) for j in jobs if j.get("completed_at")]

    if not all_created:
        return "⚠️ No job data available for timeline.\n"

    t0 = min(all_created)

    total_seconds = 0
    if all_completed and t0:
        # Only count jobs that actually ran in this attempt
        actual_completed = [
            parse_ts(j.get("completed_at"))
            for j in jobs
            if j.get("completed_at") and j.get("started_at") and j.get("created_at")
            and parse_ts(j["started_at"]) >= parse_ts(j["created_at"])
        ]
        if actual_completed:
            total_seconds = (max(actual_completed) - t0).total_seconds()

    # For first attempts, fall back to run-level timestamps if job window is smaller.
    # Skip this for re-runs — the run keeps ticking until timeout even if the
    # re-executed jobs finish quickly, making run_updated unreliable.
    run_attempt = run_info.get("run_attempt", 1)
    if run_attempt <= 1:
        run_started = parse_ts(run_info.get("run_started_at"))
        run_updated = parse_ts(run_info.get("updated_at"))
        if run_started and run_updated:
            total_seconds = max(total_seconds, (run_updated - run_started).total_seconds())

    groups = build_job_groups(jobs, t0)

    conclusion = run_info.get("conclusion") or run_info.get("status") or ""
    conclusion_icon = STATUS_ICON.get(conclusion, "")

    lines = []

    # Header
    lines.append("<h2>⏱️ CI Timeline</h2>")
    conclusion_label = conclusion if conclusion else "in progress"
    attempt_note = f" (re-run attempt #{run_attempt})" if run_attempt > 1 else ""
    lines.append(
        f"<p>{conclusion_icon} <b>{conclusion_label}</b>"
        f" — Total: <b>{fmt_duration(total_seconds)}</b>{attempt_note}</p>"
    )

    # Summary table
    lines.append(render_summary_table(groups, total_seconds))

    min_total_secs = min_total_minutes * 60

    # Critical path — first, most important
    cp_threshold = max(min_total_secs, 300)
    cp_label = (
        "<summary><b>🐢 Critical path</b> — jobs with longest total pipeline time "
        f"(deps + queue + run, showing total &gt; {cp_threshold / 60:.0f}min)</summary>"
    )
    lines.append("<details open>")
    lines.append(cp_label)
    lines.append("")
    lines.append(render_critical_path(groups, total_seconds, min_total_secs))
    lines.append("")
    lines.append("</details>")

    # Queue wait hotspots
    hotspots = render_hotspots(groups)
    if hotspots:
        lines.append("<details open>")
        lines.append("<summary><b>⏳ Queue hotspots</b> — longest queue waits</summary>")
        lines.append("")
        lines.append(hotspots)
        lines.append("")
        lines.append("</details>")

    # Full timeline chart
    lines.append("<details>")
    label = "<b>📊 Full timeline</b> — all jobs by phase"
    if min_total_minutes > 0:
        label += f" (total &gt; {min_total_minutes:.0f}min)"
    lines.append(f"<summary>{label}</summary>")
    lines.append("")
    lines.append(render_timeline_bars(groups, total_seconds, min_total_secs))
    lines.append("")
    lines.append("</details>")
    lines.append("")

    return "\n".join(lines)


# ── HTML document wrapper ────────────────────────────────────────────────────

def wrap_html(markdown_body: str) -> str:
    """Wrap markdown/HTML summary in a standalone HTML document for local viewing."""
    return f"""<!DOCTYPE html>
<html><head><meta charset="utf-8"><title>CI Timeline</title>
<style>
body {{ font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
       max-width: 1200px; margin: 0 auto; padding: 20px; background: #fff;
       color: #24292f; line-height: 1.5; }}
table {{ border-collapse: collapse; margin: 16px 0; }}
th, td {{ border: 1px solid #d0d7de; padding: 6px 13px; text-align: left; }}
th {{ background: #f6f8fa; font-weight: 600; }}
code {{ font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
        font-size: 12px; background: #f6f8fa; padding: 2px 5px; border-radius: 4px; }}
details {{ margin: 8px 0; }}
summary {{ cursor: pointer; font-weight: 600; padding: 8px 0; }}
h2 {{ border-bottom: 1px solid #d0d7de; padding-bottom: 8px; }}
h3 {{ margin-top: 24px; }}
a {{ color: #0969da; }}
</style></head><body>
{markdown_body}
</body></html>"""


# ── CLI ──────────────────────────────────────────────────────────────────────

def main():
    parser = argparse.ArgumentParser(
        description="Generate CI timeline summary for GitHub Actions step summary.",
    )
    parser.add_argument("--run-id", help="Workflow run ID")
    parser.add_argument(
        "--repo", default="microsoft/aspire",
        help="Repository (owner/repo). Defaults to microsoft/aspire",
    )
    parser.add_argument("--json", help="Path to cached JSON data file (ci-timeline format)")
    parser.add_argument("--output", "-o", help="Write output to file (.html for standalone page)")
    parser.add_argument(
        "--min-total", type=float, default=0, metavar="MINUTES",
        help="Only show jobs with total pipeline time above N minutes (default: show all)",
    )
    parser.add_argument(
        "--open", action="store_true", default=None,
        help="Open the HTML output in the default browser",
    )

    args = parser.parse_args()

    if args.json:
        run_info, jobs = load_json_data(args.json)
    else:
        repo = args.repo or os.environ.get("GITHUB_REPOSITORY", "microsoft/aspire")
        run_id = args.run_id or os.environ.get("GITHUB_RUN_ID")
        if not run_id:
            print(
                "Error: --run-id is required (or set GITHUB_RUN_ID)",
                file=sys.stderr,
            )
            sys.exit(1)
        run_info, jobs = fetch_run_data(repo, run_id)

    summary = generate_summary(run_info, jobs, min_total_minutes=args.min_total)

    step_summary = os.environ.get("GITHUB_STEP_SUMMARY")
    is_ci = bool(step_summary)

    # Determine output path
    output_path = args.output
    if not output_path and not is_ci:
        # Local mode with no explicit output — write a temp HTML file
        import tempfile
        fd, output_path = tempfile.mkstemp(suffix=".html", prefix="ci-timeline-")
        os.close(fd)

    if output_path:
        is_html = output_path.endswith(".html")
        content = wrap_html(summary) if is_html else summary
        with open(output_path, "w", encoding="utf-8") as f:
            f.write(content)
        print(f"{'HTML' if is_html else 'Summary'} written to {output_path}", file=sys.stderr)

        # Auto-open in browser for local HTML output
        should_open = args.open if args.open is not None else (is_html and not is_ci)
        if should_open:
            import webbrowser
            webbrowser.open(f"file://{os.path.abspath(output_path)}")

    if is_ci:
        with open(step_summary, "a", encoding="utf-8") as f:
            f.write(summary)
            f.write("\n")
        print("Summary appended to $GITHUB_STEP_SUMMARY", file=sys.stderr)


if __name__ == "__main__":
    main()
