"""Bounded terminal findings; rejected attempts never contribute test counts."""

import html
import json
import re

from contracts import REPOSITORY, text


def console_json(value):
    # Runner v2.338.0 recognizes legacy "##[error]..." anywhere in a line,
    # even inside prefixed JSON. Escape it losslessly; fixed frame prefixes
    # and JSON control escapes already prevent modern "::error::" commands.
    # https://github.com/actions/runner/blob/v2.338.0/src/Runner.Common/ActionCommand.cs
    return json.dumps(value).replace("##[", r"\u0023\u0023[")


def diagnostic(error):
    value = str(error)
    value = re.sub(r"(?:gh[pousr]_|github_pat_)[A-Za-z0-9_]*", "[REDACTED]", value)
    # Remove the whole signed-query field: even "?sig=[REDACTED]" is rejected
    # by the publication text boundary, including inside GitHubAPIError output.
    value = re.sub(r"(?i)[?&](?:sig|token|access_token)=[^&\s\"']*", "[REDACTED]", value)
    try:
        return text(value[:4000], 4000)
    except ValueError:
        return "Remote operation failed; diagnostic omitted because it did not satisfy safe text constraints."


def line(value, maximum=300):
    # Model/log text is data, not Markdown structure or an expandable evidence boundary.
    value = " ".join(value.split())
    return html.escape(value[:maximum] + ("..." if len(value) > maximum else ""), quote=False)


def run_link(run_id):
    return f"https://github.com/{REPOSITORY}/actions/runs/{run_id}"


def detail(value, maximum=2000):
    value = value[:maximum] + ("\n[truncated]" if len(value) > maximum else "")
    return "<pre>" + html.escape(value, quote=False) + "</pre>"


def fallback(state, controller_reason):
    outcome = state["outcome"]
    reasons = {
        "not-flaky": "The investigation did not establish a supported flaky-test defect.",
        "ambiguous": "The issue did not identify one supported test and failure.",
        "infrastructure": "Infrastructure prevented trustworthy target reproduction or verification.",
        "not-reproduced": "No matching flaky baseline was established; a speculative fix was not published.",
        "budget-exhausted": "The investigation reached its finite budget without a verified fix.",
        "cancelled": "A maintainer cancelled the investigation before a verified fix was published.",
        "blocked": "The investigation could not establish a verified fix.",
    }
    next_actions = {
        "not-flaky": "Have a maintainer confirm whether this is a deterministic defect or a non-test issue.",
        "ambiguous": "Provide the exact test, affected environment and original failure evidence for maintainer review.",
        "infrastructure": "Resolve the recorded infrastructure blocker before authorizing further execution.",
        "not-reproduced": "Review the issue's environment and historical failures before authorizing further reproduction.",
        "budget-exhausted": "Review the evidence and remaining hypothesis before authorizing any additional budget.",
        "cancelled": "Review the retained evidence; resume does not restart a terminal investigation.",
        "blocked": "Have a maintainer inspect the rejected attempt and checkpoint before authorizing further work.",
    }
    why = reasons.get(outcome, "No verified fix was established.")
    if controller_reason is None:
        why = "The agent ended without structured failure analysis; no verified fix was established."
    rejected = state.get("rejected_attempts", [])
    if rejected:
        latest = rejected[-1]
        why += f" {latest.get('action', latest['phase'])} attempt rejected: {latest['reason']}"
    elif controller_reason and outcome in {"blocked", "budget-exhausted", "cancelled"}:
        why += " " + controller_reason
    if outcome == "blocked" and controller_reason and "uncertain" in controller_reason.lower():
        next_actions["blocked"] = "Inspect the exact remote objects and checkpoint; do not blindly retry an uncertain write."
    return why, next_actions.get(outcome, next_actions["blocked"])


def findings(state, evidence, pr_url=""):
    verified = state["outcome"] == "verified"
    analysis = state.get("analysis")
    notes = state.get("notes", [])
    terminal_note = notes[-1] if notes and notes[-1]["action"] == "finish" else None
    if state.get("publication_uncertain"):
        title = "publication unconfirmed"
        summary = [
            "**Why:** Publication could not be confirmed; a remote PR or comment may already exist.",
            "**Next:** Inspect the exact remote objects and checkpoint; do not blindly retry publication.",
        ]
    elif verified:
        title = "verified"
        summary = [f"**Result:** Exact candidate reviewed; draft fix: {pr_url}."]
    else:
        title = f"{state['outcome']} - no verified fix PR"
        if analysis:
            summary = [
                "**Analysis source:** Model analysis (not independently established).",
                "**Why no verified fix:** " + line(analysis["why_no_fix"], 600),
                "**Tried (model report):** " + "; ".join(line(step, 240) for step in analysis["attempted"]),
                "**Next:** " + line(analysis["next_action"], 300),
            ]
        else:
            why, next_action = fallback(state, None if terminal_note else state["explanation"])
            used = state["used"]
            target = state.get("target")
            selected = (f"Selected {target['class_name']}.{target['method']}; " if target else "No validated test selected; ")
            summary = [
                "**Analysis source:** Trusted controller fallback. No valid terminal model analysis was received.",
                "**Why no verified fix:** " + line(why, 400),
                "**Tried:** " + line(selected + f"{used['models']} model phases started, "
                                     f"{used['workers']} worker rounds reserved, {used['fixes']} repair attempts reserved.", 350),
                "**Next:** " + next_action,
            ]
    history = state["history"]
    if history:
        summary.append(f"**Observed:** {sum(item['passed'] for item in history)} target passes, "
                       f"{sum(item['failed'] for item in history)} target failures; "
                       f"{sum(item['actual_tests'] for item in history)} actual tests in accepted evidence.")
    else:
        summary.append("**Observed:** No accepted test executions.")
    rejected = state.get("rejected_attempts", [])
    if rejected:
        links = [run_link(item["run_id"]) for item in rejected[-3:] if item.get("run_id")]
        summary.append("**Rejected evidence:** " + (", ".join(links) if links else "No run receipt was persisted") +
                       ". Rejected attempts are excluded from accepted counts.")
    details = [evidence]
    details.append("### Source identities\n" + detail(json.dumps({
        key: state.get(key) for key in ("base_sha", "control_sha", "agent_sha", "worker_sha")
    }, indent=2)))
    if state.get("candidate"):
        details.append("Candidate: `" + state["candidate"]["sha"] + "`")
    checkpointed = [state[key] for key in ("agent", "worker") if state.get(key)]
    if checkpointed:
        details.append("### Latest checkpointed run identities (not test results)")
        for owned in checkpointed:
            link = run_link(owned["id"]) if owned.get("id") else "No run receipt was persisted."
            details.append(link + "\n" + detail(json.dumps(owned, indent=2)))
    if rejected:
        details.append("### Rejected attempts (not accepted test counts)")
        if len(rejected) > 3:
            details.append(f"Showing the latest 3 of {len(rejected)} rejected attempts; all remain in the checkpoint.")
        for attempt in rejected[-3:]:
            link = run_link(attempt["run_id"]) if attempt.get("run_id") else "No run receipt was persisted."
            details.append(link + "\n" + detail(json.dumps(attempt, indent=2)))
    if terminal_note:
        label = "Model explanation and evidence references" if analysis or verified else "Legacy model explanation"
        details.append("### " + label + " (not independently established)")
        details.append(detail(json.dumps({
            "explanation": terminal_note["explanation"], "evidence_read": terminal_note["evidence_read"],
        }, indent=2)))
    else:
        details.append("### Recorded controller reason\n" + detail(state["explanation"]))
    marker = f"<!-- aspire-flaky-investigation:{state['issue']}:{state['base_sha']} -->"
    return (f"[automated] **Flaky-test investigation: {title}**\n\n" +
            "\n".join("- " + item for item in summary) +
            "\n\n<details>\n<summary>Evidence</summary>\n\n" + "\n\n".join(details) +
            "\n\n</details>\n\nNo merge, issue closure, or unquarantine was performed.\n\n" + marker)
