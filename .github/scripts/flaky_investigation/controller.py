"""Durable issue-driven orchestration; candidate code never runs in this process."""

import copy
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import subprocess
import sys

from contracts import (
    AGENT, LIMITS, REPOSITORY, TERMINAL, WORKER, expired, integer, issue_number, local_precheck,
    repair_scope, require, reserve, selection, sha, text, validate_candidate, validate_proposal,
)
from evidence import summarize
from github import GitHub, owned_run, state_branch


ROOT = Path("/tmp/gh-aw/flaky-investigation")


class Controller:
    def __init__(self, github, now=None):
        self.github = github
        self.now = now or (lambda: datetime.now(timezone.utc))

    def save(self, state, revision):
        state["updated_at"] = self.now().isoformat()
        return self.github.checkpoint(state, revision)

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

    def dispatch_worker(self, state, revision, phase, runners, iterations, mode):
        target = state["target"]
        require(target["project"] != "tests/Aspire.Cli.EndToEnd.Tests/Aspire.Cli.EndToEnd.Tests.csproj",
                "CLI E2E investigations require candidate-aware prebuilt images; use manual reproduction")
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
        intent = self.save(state, revision)
        # Only point a ref at already published trusted code. The job token cannot
        # create workflow-containing trees; candidate commits remain source-only.
        self.github.create_ref(branch, state["worker_sha"])
        run_id = self.github.dispatch(WORKER, branch, {
            "investigation_issue_url": state["issue_url"], "investigation_revision": intent,
        })
        state["worker"]["id"] = run_id
        state["phase"] = "worker"
        return self.save(state, intent)

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
        if action == "finish":
            outcome = proposal.get("outcome")
            require(isinstance(outcome, str) and outcome in TERMINAL - {"cancelled"}, "Unknown terminal finding")
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
            return self.finish(state, revision, outcome, proposal["explanation"])
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
            state["local_precheck"] = local_precheck(proposal.get("local_precheck"))
            state["phase"] = "ready"
            return self.save(state, revision)
        require(state["target"] is not None, "A validated test must be selected first")
        if action == "reproduce":
            require(state["candidate"] is None, "Cannot restart baseline after publishing a candidate")
            rounds = [item for item in state["history"] if item["phase"] == "reproduce"]
            require(len(rounds) < 3, "Baseline round budget exhausted")
            mode = "project" if len(rounds) == 2 and state["target"]["quarantined"] else "single"
            return self.dispatch_worker(state, revision, "reproduce", 3, 3, mode)
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

    def finish(self, state, revision, outcome, explanation):
        state.update(phase="publication-intent", outcome=outcome, explanation=text(explanation))
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
                pr_url = "\n\nDraft fix: " + pr["html_url"]
            self.github.api(f"issues/{issue}/comments", {
                "body": f"[automated] **Flaky-test investigation: {state['outcome']}**\n\n"
                        f"{state['explanation']}\n\n{evidence_table(state)}{pr_url}\n\n"
                        "No merge, issue closure, or unquarantine was performed.\n\n" + marker,
            })
        state["phase"] = "terminal"
        return self.save(state, revision)

    def advance(self, state, revision, cancel=False):
        if state["phase"] == "terminal":
            return revision
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
        if state["phase"] == "publication-intent":
            return self.publish(state, revision)
        if state["phase"].endswith("-intent"):
            return self.finish(state, revision, "blocked",
                               "Interrupted remote write has an uncertain outcome. Inspect the checkpoint intent; "
                               "no dispatch, candidate, cancellation, or publication was blindly retried.")
        if expired(state, self.now()):
            return self.finish(state, revision, "budget-exhausted", "The twelve-hour investigation budget expired.")
        if state["phase"] == "candidate-ready":
            baseline = [item for item in state["history"] if item["phase"] == "reproduce"]
            mode = next(item["mode"] for item in reversed(baseline) if item["failed"])
            return self.dispatch_worker(state, revision, "verify", 3, 3, mode)
        if state["phase"] == "ready":
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
        return self.save(state, revision)


def evidence_table(state):
    lines = ["| Run | Mode | Requested runner-iterations | Target passed | Target failed | Actual tests |",
             "|-----|------|-----------------------------|---------------|---------------|--------------|"]
    for item in state["history"]:
        requested = item["runners"] * item["iterations"] * len(state["target"]["oses"])
        lines.append(f"| https://github.com/{REPOSITORY}/actions/runs/{item['id']} | {item['phase']}/{item['mode']} "
                     f"| {requested} | {item['passed']} | {item['failed']} | {item['actual_tests']} |")
    lines.append("\nProject-mode actual test counts are not capped by the runner-iteration budget.")
    if state["local_precheck"]:
        lines.append("\nLocal precheck: " + json.dumps(state["local_precheck"]))
    return "\n".join(lines)


def prepare(github, url, revision):
    issue = issue_number(url)
    state, _ = github.load(issue, sha(revision))
    live, _ = github.load(issue)
    require(state["phase"] == "agent-intent" and live["phase"] in {"agent-intent", "agent"}
            and live["used"]["models"] == state["used"]["models"], "Stale agent activation")
    if live["phase"] == "agent":
        require(live["agent"]["revision"] == revision
                and str(live["agent"]["id"]) == os.environ["GITHUB_RUN_ID"], "Agent does not own this checkpoint")
    ROOT.mkdir(parents=True, exist_ok=True)
    context = {"checkpoint": state, "state_revision": revision,
               "issue": github.api(f"issues/{issue}"),
               "comments": github.pages(f"issues/{issue}/comments")}
    if state["target"]:
        context["test_source"] = github.file(state["target"]["source"], state["base_sha"])
    if state["candidate"]:
        context["candidate_files"] = {
            path: github.file(path, state["candidate"]["sha"]) for path in state["candidate"]["paths"]
        }
    (ROOT / "context.json").write_text(json.dumps(context, indent=2), encoding="utf-8")
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
        try:
            controller.advance(state, revision, cancel=operation == "cancel")
        except (ValueError, KeyError, subprocess.CalledProcessError) as error:
            live, current = github.load(issue)
            if current != revision:
                # A write may already have succeeded. Its persisted intent is the
                # recovery authority, not an in-memory copy or an automatic retry.
                print(f"::error::Investigation {issue} interrupted after checkpoint; resume required: {error}", file=sys.stderr)
                raise
            controller.finish(live, current, "blocked", f"Validation/remote operation blocked: {error}")


if __name__ == "__main__":
    main()
