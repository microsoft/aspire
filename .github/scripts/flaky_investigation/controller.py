"""Durable issue-driven orchestration; candidate code never runs in this process."""

import copy
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import subprocess
import sys
import tarfile

from contracts import (
    AGENT, LIMITS, REPOSITORY, TERMINAL, WORKER, BudgetExceeded, expired, integer, issue_number, local_precheck,
    repair_scope, require, reserve, selection, sha, terminal_analysis, text, validate_candidate, validate_proposal,
)
from evidence import summarize
from github import GitHub, owned_run, state_branch
from reporting import console_json, detail, diagnostic, findings


ROOT = Path("/tmp/gh-aw/flaky-investigation")


def observe(state, revision, event, reason=""):
    history = state["history"]
    item = {
        "event": event, "issue": state["issue_url"], "phase": state["phase"], "checkpoint": revision,
        "base_sha": state["base_sha"], "control_sha": state["control_sha"],
        "reason": diagnostic(reason)[:600] if reason else "",
        "children": [],
        "accepted": {"batches": len(history), "passed": sum(value["passed"] for value in history),
                     "failed": sum(value["failed"] for value in history),
                     "actual_tests": sum(value["actual_tests"] for value in history)},
        "rejected_attempts": len(state.get("rejected_attempts", [])),
        "finding_url": state.get("finding_url"), "outcome": state.get("outcome"),
        "publication_uncertain": state.get("publication_uncertain", False),
    }
    if target := state.get("target"):
        item["selection"] = {
            "method": target["class_name"] + "." + target["method"],
            "project": target["project"], "oses": target["oses"],
        }
    for kind in ("agent", "worker"):
        if child := state.get(kind, {}).get("id"):
            identity = {
                "kind": kind, "status": "current" if state["phase"].removesuffix("-intent") == kind else "checkpointed",
                "run_id": child, "url": f"https://github.com/{REPOSITORY}/actions/runs/{child}",
            }
            if kind == "worker":
                identity.update({key: state[kind][key]
                                 for key in ("source_sha", "phase", "mode", "runners", "iterations")})
            item["children"].append(identity)
    if state["notes"]:
        note = state["notes"][-1]
        item["decision"] = note["action"]
        item["model_reason_not_independently_established"] = diagnostic(note["explanation"])[:240]
    print("flaky-investigation " + console_json(item), flush=True)
    if summary := os.environ.get("GITHUB_STEP_SUMMARY"):
        with Path(summary).open("a", encoding="utf-8") as output:
            output.write(f"\n### Flaky investigation #{state['issue']}: {event}\n\n" +
                         detail(json.dumps(item, indent=2), 6000) + "\n")


def validate_worker_target(target):
    require(target["project"] != "tests/Aspire.Cli.EndToEnd.Tests/Aspire.Cli.EndToEnd.Tests.csproj",
            "CLI E2E investigations require candidate-aware prebuilt images; use manual reproduction")


class Controller:
    def __init__(self, github, now=None):
        self.github = github
        self.now = now or (lambda: datetime.now(timezone.utc))

    def save(self, state, revision, reason=None):
        state["updated_at"] = self.now().isoformat()
        reasons = {
            "ready": "Validated inputs/evidence ready for the next decision.",
            "agent-intent": "Reserved fresh model phase; dispatch receipt not yet persisted.",
            "agent": "Fresh model dispatched; wait for the complete run and immutable proposal.",
            "worker-intent": "Reserved worker execution; dispatch receipt not yet persisted.",
            "worker": "Owned worker dispatched; original reports must pass independent validation.",
            "candidate-ready": "Validated candidate published; exact-source verification required.",
            "publication-intent": f"Terminal {state.get('outcome')} decision; publication not yet confirmed.",
            "terminal": "Publication unconfirmed; inspect manually." if state.get("publication_uncertain")
                        else "Original-issue finding confirmed; terminal resume is a no-op.",
            "cancellation-intent": "Cancellation of the exact owned run requested; response not yet confirmed.",
        }
        reason = reason or reasons.get(state["phase"], "Checkpoint persisted.")
        state.setdefault("transitions", []).append({
            "phase": state["phase"], "reason": reason, "at": state["updated_at"],
        })
        state["transitions"] = state["transitions"][-30:]
        saved = self.github.checkpoint(state, revision)
        observe(state, saved, "checkpoint", reason)
        return saved

    def start(self, url, control_sha, agent_workflow=AGENT, agent_sha=None, worker_sha=None):
        issue = issue_number(url)
        require(agent_workflow in {AGENT, WORKER}, "Only the registered reproduction bootstrap is permitted")
        state, revision = self.github.load(issue)
        if state:
            require(state["control_sha"] == control_sha and state["agent_workflow"] == agent_workflow
                    and state["agent_sha"] == (agent_sha or control_sha)
                    and state["worker_sha"] == (worker_sha or control_sha),
                    "Runtime identities differ from the existing investigation")
            return state, revision
        item = self.github.api(f"issues/{issue}")
        require(item.get("state") == "open" and "pull_request" not in item and item.get("html_url") == url,
                "Expected an open Aspire issue, not a pull request")
        base = self.github.api("git/ref/heads/main")["object"]["sha"]
        state = {
            "version": 1, "issue": issue, "issue_url": url, "phase": "ready",
            "created_at": self.now().isoformat(), "base_sha": sha(base),
            "control_sha": sha(control_sha), "agent_sha": sha(agent_sha or control_sha),
            "worker_sha": sha(worker_sha or control_sha),
            "agent_workflow": agent_workflow,
            "used": {key: 0 for key in LIMITS}, "history": [], "notes": [], "candidate": None,
            "target": None, "local_precheck": None, "outcome": None,
        }
        return state, self.save(state, None)

    def dispatch_agent(self, state, revision):
        reserve(state, "models")
        state["phase"] = "agent-intent"
        branch = f"ankj/flaky-test-{state['issue']}-agent-{state['used']['models']}"
        state["agent"] = {"sha": state["agent_sha"], "workflow": state["agent_workflow"], "branch": branch}
        intent = self.save(state, revision)
        # Commit the intent before dispatch. If the response/save is lost, recovery
        # reports the ambiguity; it never guesses the newest matching run.
        self.github.create_ref(branch, state["agent_sha"])
        run_id = self.github.dispatch(state["agent_workflow"], branch, {
            "issue_url": state["issue_url"], "issue_number": str(state["issue"]), "state_revision": intent,
        })
        state["agent"].update(id=run_id, revision=intent)
        state["phase"] = "agent"
        return self.save(state, intent)

    def dispatch_worker(self, state, revision, phase, runners, iterations, mode, reason=None):
        target = state["target"]
        validate_worker_target(target)
        integer(runners, 3)
        integer(iterations, 10)
        require(mode in {"single", "project"} and (mode != "project" or target["quarantined"]),
                "Project escalation requires a quarantined test")
        reserve(state, "workers")
        reserve(state, "runner_iterations", runners * iterations * len(target["oses"]))
        source_sha = state["candidate"]["sha"] if phase == "verify" else state["base_sha"]
        branch = f"ankj/flaky-test-{state['issue']}-run-{state['used']['workers']}"
        state["worker"] = {
            "sha": state["worker_sha"], "source_sha": source_sha, "workflow": WORKER,
            "phase": phase, "runners": runners, "iterations": iterations, "mode": mode, "branch": branch,
        }
        state["phase"] = "worker-intent"
        intent = self.save(state, revision, reason)
        # Only point a ref at already published trusted code. The job token cannot
        # create workflow-containing trees; candidate commits remain source-only.
        self.github.create_ref(branch, state["worker_sha"])
        run_id = self.github.dispatch(WORKER, branch, {
            "investigation_issue_url": state["issue_url"], "investigation_revision": intent,
        })
        state["worker"]["id"] = run_id
        state["phase"] = "worker"
        return self.save(state, intent)

    def baseline(self, state, revision, reason):
        rounds = [item for item in state["history"] if item["phase"] == "reproduce"]
        if len(rounds) >= 3:
            raise BudgetExceeded("Baseline round budget exhausted")
        mode = "project" if len(rounds) == 2 and state["target"]["quarantined"] else "single"
        return self.dispatch_worker(
            state, revision, "reproduce", 3, 3, mode, f"Baseline {len(rounds) + 1}/3 ({mode}): {reason}")

    def proposal(self, state):
        run = owned_run(self.github.api(f"actions/runs/{state['agent']['id']}"), state["agent"])
        if run["status"] != "completed":
            return None
        require(run["conclusion"] == "success", "Agent failed/cancelled; no proposal can authorize a write")
        # push_repo_memory uploads an immutable artifact before the whole workflow
        # finishes. Only consume it after safe outputs and memory persistence end.
        files = self.github.artifact(run["id"], "repo-memory-default", maximum=1000000)
        matches = [value for path, value in files.items() if path.endswith("/proposal.json") or path == "proposal.json"]
        require(len(matches) == 1 and len(matches[0]) <= 300000, "Missing/oversized/ambiguous proposal")
        proposal = json.loads(matches[0])
        validate_proposal(state, state["agent"]["revision"], proposal)
        return proposal

    def collect_worker(self, state):
        worker = state["worker"]
        run = owned_run(self.github.api(f"actions/runs/{worker['id']}"), worker)
        if run["status"] != "completed":
            return None
        require(run["conclusion"] in {"success", "failure"}, "Owned test run was cancelled or interrupted")
        artifacts = {}
        for os_name in state["target"]["oses"]:
            for index in range(1, worker["runners"] + 1):
                artifacts[(os_name, index)] = self.github.artifact(
                    worker["id"], f"flaky-evidence-{os_name}-{index}")
        result = summarize(artifacts, worker, state["target"])
        require(run["conclusion"] == ("failure" if result["failed"] else "success"),
                "Workflow failure is not a target test failure")
        return {**copy.deepcopy(worker), **result}

    def handle(self, state, revision, proposal):
        state["notes"].append({
            "run_id": state.get("agent", {}).get("id"), "action": proposal["action"],
            "explanation": proposal["explanation"], "evidence_read": proposal["evidence_read"],
        })
        action = proposal["action"]
        if action == "select" or "local_precheck" in proposal:
            state["notes"][-1]["local_precheck"] = local_precheck(proposal.get("local_precheck"))
        if action == "finish":
            outcome = proposal.get("outcome")
            require(isinstance(outcome, str) and outcome in TERMINAL - {"cancelled"}, "Unknown terminal finding")
            if outcome == "not-reproduced":
                rounds = [item for item in state["history"] if item["phase"] == "reproduce"]
                require(state["target"] and state["candidate"] is None and len(rounds) == 3 and all(
                    item["source_sha"] == state["base_sha"] and item["failed"] == 0
                    and item["passed"] >= 9 * len(state["target"]["oses"])
                    and item["runners"] == 3 and item["iterations"] == 3
                    and item["mode"] == ("project" if index == 2 and state["target"]["quarantined"] else "single")
                    for index, item in enumerate(rounds)), "not-reproduced requires three clean baseline policy rounds")
            if outcome == "verified":
                candidate = state["candidate"]
                require(candidate and proposal.get("reviewed_candidate_sha") == candidate["sha"],
                        "Final source review must name the exact candidate")
                baseline = [item for item in state["history"] if item["phase"] == "reproduce"]
                verified = [item for item in state["history"]
                            if item["phase"] == "verify" and item["source_sha"] == candidate["sha"]]
                require(sum(item["passed"] for item in baseline) > 0
                        and sum(item["failed"] for item in baseline) > 0,
                        "A flaky baseline requires both target passes and matching target failures")
                require(verified and verified[-1]["failed"] == 0
                        and verified[-1]["passed"] >= 9
                        and verified[-1]["mode"] == next(item["mode"] for item in reversed(baseline) if item["failed"]),
                        "Final candidate needs nine target passes in the reproduced mode")
            analysis = terminal_analysis(proposal["analysis"]) if "analysis" in proposal else None
            return self.finish(state, revision, outcome, proposal["explanation"], analysis)
        if action == "select":
            require(state["target"] is None, "Selection is immutable after enrollment")
            tree = self.github.tree(state["base_sha"])
            selected = proposal.get("selection", {})
            require(isinstance(selected, dict), "Selection must be an object")
            files = {key: "" for key in tree}
            path = selected.get("source")
            if isinstance(path, str) and path in tree:
                files[path] = self.github.file(path, state["base_sha"])
            issue = self.github.api(f"issues/{state['issue']}")
            state["target"] = selection(selected, issue, files)
            # Reject known incompatibilities before ready is durable, otherwise
            # recovery would mistake a deterministic blocker for interrupted work.
            validate_worker_target(state["target"])
            projects = {}
            pending = [state["target"]["project"]]
            # Fetch a bounded graph of explicit references from immutable source;
            # no MSBuild or candidate process is run with the controller token.
            import posixpath
            import xml.etree.ElementTree as ET
            while pending:
                project = pending.pop()
                if project in projects:
                    continue
                require(len(projects) < 150, "Project reference graph exceeds scope budget")
                projects[project] = self.github.file(project, state["base_sha"])
                for reference in ET.fromstring(projects[project]).iter("ProjectReference"):
                    path = reference.get("Include", "").replace("\\", "/")
                    resolved = posixpath.normpath(posixpath.join(posixpath.dirname(project), path))
                    if resolved in tree and resolved.endswith(".csproj"):
                        pending.append(resolved)
            state["repair_scope"] = repair_scope(state["target"]["project"], projects)
            # No arbitrary agent shell command is forwarded to a trusted runner.
            state["local_precheck"] = state["notes"][-1]["local_precheck"]
            state["phase"] = "ready"
            revision = self.save(state, revision, "Validated test selection and local precheck; start baseline directly.")
            return self.baseline(state, revision, "validated selection; no extra model needed to request reproduction.")
        require(state["target"] is not None, "A validated test must be selected first")
        if action == "reproduce":
            require(state["candidate"] is None, "Cannot restart baseline after publishing a candidate")
            return self.baseline(state, revision, "validated model request for further reproduction.")
        require(action == "repair", "Unsupported action")
        baseline = [item for item in state["history"] if item["phase"] == "reproduce"]
        require(sum(item["failed"] for item in baseline) > 0 and sum(item["passed"] for item in baseline) > 0,
                "Do not repair deterministic failures or an unreproduced test")
        text(proposal.get("diagnosis"), 12000)
        files = proposal.get("files")
        require(isinstance(files, dict), "Repair requires full proposed file contents")
        tree = self.github.tree(state["base_sha"])
        originals = {}
        for path in files:
            from contracts import source_path
            source_path(path)
            require(any(path.startswith(directory) for directory in state["repair_scope"]),
                    "Candidate source is outside the selected project's explicit reference graph")
            require(path in tree, "Candidate adds/deletes/renames a file or uses a nonregular path")
            originals[path] = self.github.file(path, state["base_sha"])
        validate_candidate(files, originals)
        reserve(state, "fixes")
        state["phase"] = "candidate-ready"
        state["candidate"] = {
            "sha": self.github.commit(state["base_sha"], files,
                                      f"fix: stabilize {state['target']['method']}"),
            "paths": sorted(files), "diagnosis": proposal["diagnosis"],
        }
        revision = self.save(state, revision)
        return self.advance(state, revision)

    def finish(self, state, revision, outcome, explanation, analysis=None):
        state.update(phase="publication-intent", outcome=outcome, explanation=text(explanation), analysis=analysis)
        revision = self.save(state, revision)
        return self.publish(state, revision, first=True)

    def publish(self, state, revision, first=False):
        issue = state["issue"]
        marker = f"<!-- aspire-flaky-investigation:{issue}:{state['base_sha']} -->"
        comments = self.github.pages(f"issues/{issue}/comments")
        existing = [comment for comment in comments if marker in comment.get("body", "")
                    and comment.get("user", {}).get("login") == "github-actions[bot]"
                    and comment.get("user", {}).get("type") == "Bot"]
        if not existing:
            if not first:
                state.update(phase="terminal", outcome="blocked", publication_uncertain=True,
                             explanation="Uncertain findings publication; inspect manually before retrying.")
                print(f"::error::{state['explanation']} Issue {issue}", file=sys.stderr)
                return self.save(state, revision)
            pr_url = ""
            if state["outcome"] == "verified":
                candidate = state["candidate"]
                branch = f"ankj/flaky-test-{issue}-fix"
                current = self.github.optional(f"git/ref/heads/{branch}")
                if current:
                    require(current["object"]["sha"] == candidate["sha"], "Fix branch has changed")
                else:
                    self.github.create_ref(branch, candidate["sha"])
                # Canonical template is retained in full, including its checklist.
                template = self.github.file(".github/pull_request_template.md", state["control_sha"])
                placeholder = "Please include a summary of the changes and the related issue. Please also include relevant motivation and context. List any dependencies that are required for this change."
                require(template.count(placeholder) == 1 and template.count("Fixes # (issue)") == 1,
                        "PR template changed; publisher must be updated before creating a PR")
                description = (
                    f"[automated] Stabilize `{state['target']['class_name']}.{state['target']['method']}`.\n\n"
                    f"Issue: {state['issue_url']} (kept open).\n\n"
                    f"{candidate['diagnosis']}\n\n{evidence_table(state)}\n\n"
                    "Generated using the fix-flaky-test skill. Quarantine is unchanged. "
                    "Human code walkthrough and security review are still required.\n\n" + marker)
                body = template.replace(placeholder, description).replace(
                    "Fixes # (issue)", f"Related issue: {state['issue_url']} (not closed)")
                body = body.replace("- [ ] No. Follow-up changes expected.", "- [x] No. Follow-up changes expected.")
                pulls = self.github.pages(f"pulls?state=all&head=microsoft:{branch}")
                require(len(pulls) <= 1, "Ambiguous existing fix PR")
                if pulls:
                    pr = pulls[0]
                    require(pr["head"]["sha"] == candidate["sha"] and pr["draft"]
                            and pr["state"] == "open" and pr["user"]["login"] == "github-actions[bot]",
                            "Existing PR is not the owned draft candidate")
                else:
                    pr = self.github.api("pulls", {
                        "title": f"[automated] Fix flakiness in {state['target']['method']}",
                        "body": body, "head": branch, "base": "main", "draft": True,
                    })
                pr_url = pr["html_url"]
            receipt = self.github.api(f"issues/{issue}/comments", {
                "body": findings(state, evidence_table(state), pr_url),
            })
            state["finding_url"] = receipt.get("html_url") or (
                f"{state['issue_url']}#issuecomment-{receipt['id']}")
        elif existing[0].get("html_url"):
            state["finding_url"] = existing[0]["html_url"]
        state["phase"] = "terminal"
        return self.save(state, revision)

    def advance(self, state, revision, cancel=False):
        if state["phase"] == "terminal":
            return revision
        # Cancellation cannot establish whether an earlier publication succeeded.
        if state["phase"] == "publication-intent":
            return self.publish(state, revision)
        if cancel:
            if state["phase"] in {"agent", "worker"}:
                owned = state[state["phase"]]
                run = owned_run(self.github.api(f"actions/runs/{owned['id']}"), owned)
                if run["status"] != "completed":
                    # Record the cancellation before invoking the exact owned run.
                    state["phase"] = "cancellation-intent"
                    revision = self.save(state, revision)
                    self.github.api(f"actions/runs/{owned['id']}/cancel", {}, "POST")
            return self.finish(state, revision, "cancelled", "Maintainer cancelled this investigation.")
        if state["phase"].endswith("-intent"):
            reason = ("Interrupted remote write has an uncertain outcome. Inspect the checkpoint intent; "
                      "no dispatch, candidate, cancellation, or publication was blindly retried.")
            record_rejection(state, reason)
            return self.finish(state, revision, "blocked",
                               reason)
        if expired(state, self.now()):
            return self.finish(state, revision, "budget-exhausted", "The twelve-hour investigation budget expired.")
        if state["phase"] == "candidate-ready":
            baseline = [item for item in state["history"] if item["phase"] == "reproduce"]
            mode = next(item["mode"] for item in reversed(baseline) if item["failed"])
            return self.dispatch_worker(state, revision, "verify", 3, 3, mode)
        if state["phase"] == "ready":
            rounds = [item for item in state["history"] if item["phase"] == "reproduce"]
            if state["target"] and state["candidate"] is None and len(rounds) < 3 \
                    and all(item["failed"] == 0 for item in rounds):
                return self.baseline(state, revision, "validated selection/clean baseline; continue without a model.")
            if state["used"]["models"] >= LIMITS["models"]:
                return self.finish(state, revision, "budget-exhausted", "Fresh model phase budget exhausted.")
            return self.dispatch_agent(state, revision)
        if state["phase"] == "agent":
            proposal = self.proposal(state)
            if proposal is None:
                return revision
            return self.handle(state, revision, proposal)
        require(state["phase"] == "worker", "Unknown checkpoint phase")
        result = self.collect_worker(state)
        if result is None:
            return revision
        state["history"].append(result)
        state["phase"] = "ready"
        revision = self.save(state, revision, f"Accepted {result['phase']}/{result['mode']} evidence: "
                             f"{result['passed']} target passes, {result['failed']} target failures; "
                             f"{result['actual_tests']} actual tests.")
        rounds = [item for item in state["history"] if item["phase"] == "reproduce"]
        if result["phase"] == "reproduce" and all(item["failed"] == 0 for item in rounds) and len(rounds) < 3:
            return self.baseline(state, revision, "previous clean baseline; continue the fixed policy without a model.")
        return revision


def evidence_table(state):
    lines = ["| Run | Mode | Requested runner-iterations | Target passed | Target failed | Actual tests |",
             "|-----|------|-----------------------------|---------------|---------------|--------------|"]
    for item in state["history"]:
        requested = item["runners"] * item["iterations"] * len(state["target"]["oses"])
        lines.append(f"| https://github.com/{REPOSITORY}/actions/runs/{item['id']} | {item['phase']}/{item['mode']} "
                     f"| {requested} | {item['passed']} | {item['failed']} | {item['actual_tests']} |")
    lines.append("\nProject-mode actual test counts are not capped by the runner-iteration budget.")
    if state["local_precheck"]:
        lines.append("\nLocal precheck:\n" + detail(json.dumps(state["local_precheck"], indent=2)))
    return "\n".join(lines)


def record_rejection(state, reason, action=None):
    rejection = {"phase": state["phase"], "reason": diagnostic(reason)}
    if action:
        rejection["action"] = action
    phase = state["phase"].removesuffix("-intent")
    owned = state.get(phase) if phase in {"agent", "worker"} else None
    if owned:
        rejection.update({key: owned[key] for key in ("source_sha", "sha", "mode", "branch") if key in owned})
        if "id" in owned:
            rejection["run_id"] = owned["id"]
    state.setdefault("rejected_attempts", []).append(rejection)


def stage_source(state, checkout):
    base = sha(state["base_sha"])
    ROOT.mkdir(parents=True, exist_ok=True)
    present = subprocess.run(["git", "--no-pager", "cat-file", "-e", base + "^{commit}"],
                             cwd=checkout, capture_output=True, check=False, timeout=30)
    if present.returncode:
        subprocess.run(["git", "--no-pager", "fetch", "--no-tags", "--depth=1", "origin", base],
                       cwd=checkout, check=True, timeout=120)
    source = ROOT / "source"
    source.mkdir()
    archive = ROOT / "source.tar"
    try:
        with archive.open("wb") as output:
            subprocess.run(["git", "--no-pager", "archive", "--format=tar", base],
                           cwd=checkout, stdout=output, check=True, timeout=120)
        with tarfile.open(archive) as contents:
            contents.extractall(source, filter="data")
    finally:
        archive.unlink(missing_ok=True)
    require((source / ".agents/skills/fix-flaky-test/SKILL.md").is_file(),
            "Frozen source is missing fix-flaky-test guidance; do not substitute wrapper HEAD")
    return source


def prepare(github, url, revision):
    issue = issue_number(url)
    state, _ = github.load(issue, sha(revision))
    live, _ = github.load(issue)
    require(state["phase"] == "agent-intent" and live["phase"] in {"agent-intent", "agent"}
            and live["used"]["models"] == state["used"]["models"], "Stale agent activation")
    if live["phase"] == "agent":
        require(live["agent"]["revision"] == revision
                and str(live["agent"]["id"]) == os.environ["GITHUB_RUN_ID"], "Agent does not own this checkpoint")
    source = stage_source(state, Path.cwd())
    context = {"checkpoint": state, "state_revision": revision,
               "source": {"sha": state["base_sha"], "path": str(source)},
               "historical_evidence": {
                   "fetched_at": datetime.now(timezone.utc).isoformat(), "logs_collected": False,
                   "limitation": "Issue/comment report counts are not current unless their own dates establish that. "
                                 "Historical quarantine logs have not been collected by preparation.",
               },
               "issue": github.api(f"issues/{issue}"),
               "comments": github.pages(f"issues/{issue}/comments")}
    if state["target"]:
        context["test_source"] = github.file(state["target"]["source"], state["base_sha"])
    if state["candidate"]:
        context["candidate_files"] = {
            path: github.file(path, state["candidate"]["sha"]) for path in state["candidate"]["paths"]
        }
    (ROOT / "context.json").write_text(json.dumps(context, indent=2), encoding="utf-8")
    observe(state, revision, "prepared", f"Frozen base source staged at {source}; wrapper HEAD is not the test source.")
    # Original reports and output remain available to fresh diagnosis/review phases.
    for worker in state["history"]:
        for os_name in state["target"]["oses"]:
            for index in range(1, worker["runners"] + 1):
                files = github.artifact(worker["id"], f"flaky-evidence-{os_name}-{index}")
                for path, content in files.items():
                    destination = ROOT / "evidence" / str(worker["id"]) / os_name / str(index) / path
                    destination.parent.mkdir(parents=True, exist_ok=True)
                    destination.write_bytes(content)


def authorize(github, actor, operation):
    from urllib.parse import quote
    # A trusted hosted recovery driver may dispatch only a resume with the job
    # token. Enrollment/cancellation still require a human maintainer permission.
    if actor == "github-actions[bot]" and operation == "resume":
        return
    permission = github.api(f"collaborators/{quote(actor, safe='')}/permission")
    require(permission.get("permission") in {"admin", "maintain", "write"},
            "Maintainer authorization required")


def main():
    require(os.environ.get("GITHUB_REPOSITORY") == REPOSITORY, "Canonical repository only")
    github = GitHub()
    command = sys.argv[1]
    if command == "prepare":
        require(os.environ["ISSUE_NUMBER"] == str(issue_number(os.environ["ISSUE_URL"])),
                "Proposal branch issue number must match the issue URL")
        prepare(github, os.environ["ISSUE_URL"], os.environ["STATE_REVISION"])
        return
    controller = Controller(github)
    operation = os.environ.get("OPERATION") or "resume"
    require(operation in {"start", "resume", "cancel"}, "Unknown controller operation")
    if os.environ.get("GITHUB_EVENT_NAME") == "workflow_dispatch":
        authorize(github, os.environ["GITHUB_ACTOR"], operation)
    url = os.environ.get("ISSUE_URL")
    if url:
        issues = [issue_number(url)]
    else:
        require(operation == "resume", "Recovery sweep cannot enroll or cancel issues")
        refs = github.optional("git/matching-refs/heads/ankj/flaky-test-") or []
        import re
        issues = [int(match[1]) for ref in refs if (match := re.fullmatch(
            r"refs/heads/ankj/flaky-test-([1-9][0-9]*)-state", ref["ref"]))]
    for issue in issues:
        state, revision = github.load(issue)
        if state is None:
            require(operation == "start", "Issue is not enrolled")
            state, revision = controller.start(
                url, os.environ["GITHUB_SHA"], os.environ.get("FLAKY_AGENT_WORKFLOW", AGENT),
                os.environ.get("FLAKY_AGENT_SHA") or None, os.environ.get("FLAKY_WORKER_SHA") or None)
        # Serialize all controllers through workflow concurrency; Git CAS is the
        # final guard against stale or independently resumed controllers.
        observe(state, revision, operation)
        try:
            current = controller.advance(state, revision, cancel=operation == "cancel")
            if current != revision and state["phase"] == "ready":
                current = controller.advance(state, current)
        except (ValueError, KeyError, subprocess.CalledProcessError) as error:
            live, current = github.load(issue)
            if current != revision or live["phase"] == "publication-intent":
                # A write may already have succeeded. Its persisted intent is the
                # recovery authority, not an in-memory copy or an automatic retry.
                print(f"::error::Investigation {issue} interrupted after checkpoint; resume required: "
                      f"{console_json(diagnostic(error))}", file=sys.stderr)
                raise
            action = state["notes"][-1]["action"] if len(state["notes"]) > len(live["notes"]) else None
            record_rejection(live, error, action)
            outcome = "budget-exhausted" if isinstance(error, BudgetExceeded) else "blocked"
            current = controller.finish(live, current, outcome, "Validation/remote operation blocked: " + diagnostic(error))
            state = live
        event = "changed" if current != revision else "terminal-noop" if state["phase"] == "terminal" else "waiting"
        reason = state.get("explanation") or state.get("transitions", [{}])[-1].get("reason", "")
        if state.get("publication_uncertain"):
            reason = "Publication unconfirmed; inspect remote objects and checkpoint before retrying."
        elif state["notes"] and state["notes"][-1]["action"] == "finish":
            reason = "Model finish accepted; its explanation is labelled as not independently established."
        observe(state, current, event, reason)


if __name__ == "__main__":
    main()
