"""Read-only repeated test execution retaining original reports and receipts."""

import json
import os
from pathlib import Path
import subprocess
import sys

from contracts import require


def execute(root, config, run=subprocess.run):
    actual_sha = subprocess.check_output(
        ["git", "--no-pager", "rev-parse", "HEAD"], cwd=root, text=True).strip()
    require(actual_sha == config["source_sha"], "Candidate checkout does not match checkpoint source")
    output = root / "flaky-evidence"
    output.mkdir()
    receipt = {
        "run_id": int(os.environ["GITHUB_RUN_ID"]),
        "run_attempt": int(os.environ["GITHUB_RUN_ATTEMPT"]),
        "sha": actual_sha, "trusted_sha": config["trusted_sha"],
        "os": os.environ["FLAKY_RUNNER_OS"], "index": int(os.environ["FLAKY_RUNNER_INDEX"]),
        "iterations": config["iterations"], "project": config["target"]["project"],
        "method": config["target"]["class_name"] + "." + config["target"]["method"], "mode": config["mode"],
    }
    (output / "receipt.json").write_text(json.dumps(receipt), encoding="utf-8")
    dotnet = str(root / ("dotnet.cmd" if os.name == "nt" else "dotnet.sh"))
    target = config["target"]
    selector = (["--filter-trait", "quarantined=true"] if config["mode"] == "project" else
                ["--filter-method", target["class_name"] + "." + target["method"]])
    exit_code = 0
    # Each process has a distinct result directory. Keep original TRX from passing
    # iterations too; merged reports cannot prove execution counts or identity.
    for iteration in range(1, config["iterations"] + 1):
        directory = output / f"iteration-{iteration}"
        directory.mkdir()
        command = [dotnet, "test", "--project", target["project"], "--no-restore", "--no-build",
                   "--no-launch-profile", "--",
                   "--timeout", "10m", "--results-directory", str(directory.resolve()),
                   "--report-trx", "--filter-not-trait", "outerloop=true", *selector]
        with (directory / "output.log").open("w", encoding="utf-8") as log:
            try:
                result = run(command, cwd=root, stdout=log, stderr=subprocess.STDOUT, timeout=720, check=False)
                code = result.returncode
            except subprocess.TimeoutExpired:
                code = 124
        (directory / "result.json").write_text(json.dumps({"exit_code": code}), encoding="utf-8")
        # Zero tests, missing TRX, build/runner errors are rejected independently
        # by the controller even when the repository ignores MTP exit code 8.
        exit_code = max(exit_code, code if code >= 0 else 128 - code)
    return exit_code


if __name__ == "__main__":
    root = Path.cwd()
    config = json.loads(Path(os.environ["FLAKY_CONFIG"]).read_text(encoding="utf-8"))
    sys.exit(execute(root, config))
