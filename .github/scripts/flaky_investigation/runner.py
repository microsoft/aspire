"""Read-only repeated test execution retaining original reports and receipts."""

from concurrent.futures import ThreadPoolExecutor
import json
import os
from pathlib import Path
import subprocess
import sys
import threading

from contracts import require
from reporting import console_json, diagnostic


def console_tail(path, done):
    remaining = 64000
    pending = b""
    dropping = False
    end = None
    def emit(value):
        nonlocal remaining
        rendered = "flaky-test-output " + console_json({"text": diagnostic(value)})
        if len(rendered) > remaining:
            print('flaky-test-output {"notice":"Console limit reached; full bytes remain in output.log."}', flush=True)
            return False
        print(rendered, flush=True)
        remaining -= len(rendered) + 1
        return True
    # Tail the regular artifact file rather than a pipe. Descendants retaining
    # stdout cannot hold this reader open after the existing process timeout.
    with path.open("rb") as source:
        while True:
            if done.is_set() and end is None:
                end = path.stat().st_size
            size = 8192 if end is None else min(8192, end - source.tell())
            chunk = source.read(size)
            if not chunk:
                if end is not None:
                    break
                done.wait(.05)
                continue
            pending += chunk
            while b"\n" in pending:
                value, pending = pending.split(b"\n", 1)
                if dropping or len(value) > 8192:
                    value = b"[Overlong console line omitted; original bytes remain in output.log.]"
                dropping = False
                if value.strip() and not emit(value.decode("utf-8", errors="replace")):
                    return
            if len(pending) > 8192:
                pending = b""
                dropping = True
        if dropping:
            emit("[Overlong console line omitted; original bytes remain in output.log.]")
        elif pending.strip():
            emit(pending.decode("utf-8", errors="replace"))


def run_streamed(command, *, cwd, stdout, stderr, timeout, check):
    done = threading.Event()
    with ThreadPoolExecutor(max_workers=1) as reader:
        future = reader.submit(console_tail, Path(stdout.name), done)
        try:
            return subprocess.run(command, cwd=cwd, stdout=stdout, stderr=stderr, timeout=timeout, check=check)
        finally:
            done.set()
            future.result()


def execute(root, config, run=run_streamed):
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
        print("flaky-test-iteration " + console_json({
            "iteration": iteration, "source_sha": actual_sha, "mode": config["mode"],
            "results": str(directory), "target": receipt["method"],
        }), flush=True)
        with (directory / "output.log").open("wb") as log:
            try:
                result = run(command, cwd=root, stdout=log, stderr=subprocess.STDOUT, timeout=720, check=False)
                code = result.returncode
            except subprocess.TimeoutExpired:
                code = 124
        (directory / "result.json").write_text(json.dumps({"exit_code": code}), encoding="utf-8")
        print("flaky-test-iteration " + console_json({"iteration": iteration, "exit_code": code}), flush=True)
        # Zero tests, missing TRX, build/runner errors are rejected independently
        # by the controller even when the repository ignores MTP exit code 8.
        exit_code = max(exit_code, code if code >= 0 else 128 - code)
    return exit_code


if __name__ == "__main__":
    root = Path.cwd()
    config = json.loads(Path(os.environ["FLAKY_CONFIG"]).read_text(encoding="utf-8"))
    sys.exit(execute(root, config))
