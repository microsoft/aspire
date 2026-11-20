"""Read-only configuration for the fixed reproduction workflow."""

import json
import os
from pathlib import Path
import subprocess
import sys

from contracts import OSES, REPOSITORY, WORKER, integer, issue_number, require, sha, source_path
from github import GitHub, owned_run


def configuration(github, issue_url, revision, run_id, trusted_sha):
    issue = issue_number(issue_url)
    snapshot, _ = github.load(issue, sha(revision))
    live, _ = github.load(issue)
    require(snapshot["phase"] == "worker-intent" and live["phase"] in {"worker-intent", "worker"},
            "Stale reproduction activation")
    worker = snapshot["worker"]
    require(snapshot["worker_sha"] == trusted_sha == worker["sha"] and worker["workflow"] == WORKER,
            "Worker trusted source does not match the checkpoint")
    expected = {**worker, "id": run_id} if live["phase"] == "worker" else worker
    require(live["worker"] == expected, "Worker run does not own the checkpoint")
    owned_run(github.api(f"actions/runs/{run_id}"), {**worker, "id": run_id})
    target = snapshot["target"]
    require(target["project"] != "tests/Aspire.Cli.EndToEnd.Tests/Aspire.Cli.EndToEnd.Tests.csproj",
            "CLI E2E investigations require candidate-aware prebuilt images; use manual reproduction")
    source_path(target["source"])
    require(target["project"].startswith("tests/") and target["project"].endswith(".csproj"),
            "Invalid checkpoint test project")
    require(isinstance(target["oses"], list) and 1 <= len(target["oses"]) <= 3
            and all(item in OSES for item in target["oses"])
            and len(set(target["oses"])) == len(target["oses"]), "Invalid checkpoint OS selection")
    integer(worker["runners"], 3)
    integer(worker["iterations"], 10)
    require(worker["mode"] in {"single", "project"}
            and (worker["mode"] != "project" or target["quarantined"]), "Invalid reproduction mode")
    return {
        "target": target, "runners": worker["runners"], "iterations": worker["iterations"],
        "mode": worker["mode"], "source_sha": sha(worker["source_sha"]), "trusted_sha": sha(trusted_sha),
    }


def setup_outputs(config):
    return {
        "active": "true", "source_sha": config["source_sha"],
        "is_cli_e2e": str(config["target"]["project"] ==
                          "tests/Aspire.Cli.EndToEnd.Tests/Aspire.Cli.EndToEnd.Tests.csproj").lower(),
        "matrix": json.dumps({"include": [
            {"os": os_name, "index": index}
            for os_name in config["target"]["oses"] for index in range(1, config["runners"] + 1)
        ]}),
    }


def validate_checkout(root, config):
    actual = subprocess.check_output(
        ["git", "--no-pager", "rev-parse", "HEAD"], cwd=root, text=True).strip()
    require(actual == config["source_sha"], "Candidate checkout does not match checkpoint source")


def main():
    require(os.environ.get("GITHUB_REPOSITORY") == REPOSITORY, "Canonical repository only")
    config = configuration(
        GitHub(), os.environ["INVESTIGATION_ISSUE_URL"], os.environ["INVESTIGATION_REVISION"],
        int(os.environ["GITHUB_RUN_ID"]), os.environ["GITHUB_SHA"])
    if sys.argv[1] == "setup":
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
            for name, value in setup_outputs(config).items():
                output.write(f"{name}={value}\n")
    elif sys.argv[1] == "runner":
        candidate = Path(os.environ["GITHUB_WORKSPACE"]) / "candidate"
        validate_checkout(candidate, config)
        path = Path(os.environ["RUNNER_TEMP"]) / "flaky-worker.json"
        path.write_text(json.dumps(config), encoding="utf-8")
        project = candidate / config["target"]["project"]
        with open(os.environ["GITHUB_ENV"], "a", encoding="utf-8") as output:
            output.write(f"FLAKY_CONFIG={path}\nTEST_PROJECT_PATH={project}\n")
    else:
        raise ValueError("Unknown worker configuration command")


if __name__ == "__main__":
    main()
