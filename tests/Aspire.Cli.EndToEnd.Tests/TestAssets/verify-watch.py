# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.

import json
import os
import pathlib
import signal
import subprocess
import sys
import time
import urllib.error
import urllib.request


ROOT = pathlib.Path.cwd()
APPHOST = ROOT / ("Watch.AppHost/Watch.AppHost.csproj" if sys.argv[1] == "csharp" else "apphost.mts")


def cli(*arguments, success=True, timeout=240):
    result = subprocess.run(
        ["aspire", *arguments, "--apphost", str(APPHOST)],
        cwd=ROOT,
        capture_output=True,
        text=True,
        timeout=timeout,
    )
    if success and result.returncode:
        raise AssertionError(
            f"aspire {arguments} failed ({result.returncode}):\n{result.stdout}\n{result.stderr}"
        )
    return result


def describe(name="api", hidden=False, retry=False):
    arguments = ["describe", name, "--format", "json"] if name else [
        "describe", "--format", "json"
    ]
    if hidden:
        arguments.append("--include-hidden")
    # CLI JSON: {"resources":[{"name":"api-<id>","properties":{"watch.mode":"experimental"},
    #                         "urls":[{"name":"http","url":"http://localhost:<port>"}]}]}.
    result = cli(*arguments, success=not retry)
    if result.returncode:
        raise ConnectionError(
            f"CLI describe unavailable during generation replacement: {result.stdout}\n{result.stderr}"
        )
    try:
        payload = json.loads(result.stdout)
    except json.JSONDecodeError as error:
        if retry:
            # With no running generation, describe can print informational text and
            # exit successfully instead of producing its {"resources":[...]} payload.
            raise ConnectionError(
                f"No JSON resource snapshot during generation replacement: {result.stdout}\n{result.stderr}"
            ) from error
        raise
    return payload["resources"]


def wait_for(predicate, description, timeout=150):
    deadline = time.monotonic() + timeout
    last_error = None
    while time.monotonic() < deadline:
        try:
            result = predicate()
            if result:
                return result
        except (urllib.error.URLError, TimeoutError, ConnectionError) as error:
            last_error = error
        time.sleep(0.25)
    raise AssertionError(f"Timed out {description}. Last connection error: {last_error}")


def response(url):
    with urllib.request.urlopen(url, timeout=3) as stream:
        return json.load(stream)


def alive(pid):
    try:
        os.kill(pid, 0)
        return True
    except ProcessLookupError:
        return False


def endpoint(name):
    resource = describe(name)[0]
    return next(item["url"] for item in resource["urls"] if item["name"] == "http")


def transports():
    processes = set()
    servers = 0
    for resource in describe(name=None, hidden=True):
        properties = resource["properties"]
        if properties.get("watch.mode") == "experimental":
            processes.add(properties["watch.transport.pid"])
        if resource.get("displayName", resource["name"]).startswith("__dotnet-watch-"):
            processes.add(properties["executable.pid"])
            servers += 1
    assert servers, "No watch server was present in the hidden resource model."
    return processes


def server_processes():
    return [
        resource["properties"]["executable.pid"]
        for resource in describe(name=None, hidden=True)
        if resource.get("displayName", resource["name"]).startswith("__dotnet-watch-")
    ]


def verify(language):
    cli("run", "--watch", "--detach", "--non-interactive")
    cli("wait", "api", "--status", "up", "--timeout", "240")
    cli("wait", "worker", "--status", "up", "--timeout", "240")
    cli("wait", "ordinary", "--status", "up", "--timeout", "240")
    resource = describe()[0]
    properties = resource["properties"]
    assert properties["watch.mode"] == "experimental", resource
    assert properties.get("executable.pid") is None, resource
    assert properties["watch.transport.pid"] > 0, resource
    url = endpoint("api")
    worker_url = endpoint("worker")
    ordinary_url = endpoint("ordinary")
    initial = wait_for(lambda: response(url), "waiting for the initial HTTP response")
    worker = wait_for(lambda: response(worker_url), "waiting for the shared-library consumer")
    ordinary = wait_for(lambda: response(ordinary_url), "waiting for the ordinary file app")
    ordinary_properties = describe("ordinary")[0]["properties"]
    assert ordinary_properties["watch.mode"] == "ordinary"
    assert ordinary_properties["watch.reason"]
    assert initial["value"] == "before" and initial["configured"] == "one", initial
    assert initial["shared"] == worker["shared"] == "shared-before"
    initial_proxy = properties["watch.transport.pid"]
    old_transports = transports()
    tracked_processes = old_transports | {initial["pid"], worker["pid"], ordinary["pid"]}
    print("WATCH_CHECK:initial-processes", flush=True)

    previous_ordinary_pid = ordinary["pid"]
    cli("resource", "ordinary", "rebuild", "--non-interactive")
    ordinary = wait_for(
        lambda: (value if (value := response(ordinary_url))["pid"] != previous_ordinary_pid else None),
        "waiting for the unwatched resource's ordinary Rebuild",
    )
    tracked_processes.add(ordinary["pid"])
    assert response(url)["pid"] == initial["pid"]
    assert response(worker_url)["pid"] == worker["pid"]
    print("WATCH_CHECK:ordinary-rebuild-isolation", flush=True)

    program = ROOT / "Api" / "Program.cs"
    source = program.read_text()
    assert source.count('=> "before"') == 1
    program.write_text(source.replace('=> "before"', '=> "after"'))
    updated = wait_for(
        lambda: (value if (value := response(url))["value"] == "after" else None),
        "waiting for an actual metadata update",
    )
    assert updated["pid"] == initial["pid"], (initial, updated)

    shared = ROOT / "Shared" / "SharedValue.cs"
    shared_source = shared.read_text()
    assert shared_source.count('=> "shared-before"') == 1
    shared_source = shared_source.replace('=> "shared-before"', '=> "shared-after"')
    shared.write_text(shared_source)
    for address in [url, worker_url]:
        wait_for(
            lambda: (value if (value := response(address))["shared"] == "shared-after" else None),
            "waiting for both shared-library consumers to hot reload",
        )
    assert response(url)["pid"] == initial["pid"]
    assert response(worker_url)["pid"] == worker["pid"]
    print("WATCH_CHECK:shared-library-hot-reload", flush=True)

    shared.write_text(shared_source.replace('=> "shared-after"', "=> ;"))
    wait_for(
        lambda: "CS1525" in cli("logs", "api", "--search", "CS1525", "--format", "json").stdout,
        "waiting for the invalid source edit to be diagnosed",
    )
    assert response(url)["pid"] == initial["pid"]
    shared.write_text(shared_source.replace('"shared-after"', '"shared-recovered"'))
    for address in [url, worker_url]:
        wait_for(
            lambda: (value if (value := response(address))["shared"] == "shared-recovered" else None),
            "waiting for recovery after correcting the source",
        )
    print("WATCH_CHECK:invalid-edit-recovery", flush=True)

    rude_source = program.read_text()
    assert rude_source.count("public class ValueSource\n") == 1
    program.write_text(rude_source.replace("public class ValueSource\n", "public class ValueSource : ReplacementBase\n"))
    rude = wait_for(
        lambda: (value if (value := response(url))["pid"] != initial["pid"] else None),
        "waiting for a rude edit to restart the affected service",
    )
    assert rude["value"] == "after", rude
    assert rude["pid"] != initial["pid"], (initial, rude)
    assert response(worker_url)["pid"] == worker["pid"]
    tracked_processes.add(rude["pid"])
    print("WATCH_CHECK:rude-edit-restart", flush=True)
    assert cli("resource", "api", "rebuild", "--non-interactive", success=False).returncode != 0
    assert response(url)["pid"] == rude["pid"]

    restarted = rude
    for _ in range(2):
        previous_pid = restarted["pid"]
        cli("resource", "api", "restart", "--non-interactive")
        restarted = wait_for(
            lambda: (value if (value := response(url))["pid"] != previous_pid else None),
            "waiting for restart-only control",
        )
        assert restarted["value"] == "after", restarted
        assert describe()[0]["properties"]["watch.transport.pid"] == initial_proxy
        assert response(worker_url)["pid"] == worker["pid"]
        tracked_processes.add(restarted["pid"])
    print("WATCH_CHECK:restart-only-control", flush=True)

    if language == "csharp":
        late_directory = ROOT / "Late"
        late_directory.mkdir()
        (late_directory / "Late.csproj").write_text((ROOT / "Api" / "Api.csproj").read_text())
        (late_directory / "Program.cs").write_text(program.read_text())
        addition = """
builder.AddDotnetProject("late", "../Late/Late.csproj")
    .WithHttpEndpoint(env: "ASPNETCORE_HTTP_PORTS")
    .WithHttpHealthCheck("/health");
"""
        apphost = ROOT / "Watch.AppHost" / "AppHost.cs"
        source = apphost.read_text()
        assert source.count('"WATCH_MODEL_VALUE", "one"') == 1
        assert source.count("builder.Build().Run();") == 1
        apphost.write_text(source.replace('"WATCH_MODEL_VALUE", "one"', '"WATCH_MODEL_VALUE", "two"')
                          .replace("builder.Build().Run();", addition + "builder.Build().Run();"))

        def generation_response(expected):
            resources = describe(retry=True)
            if not resources:
                return None
            addresses = resources[0].get("urls", [])
            for address in addresses:
                if address["name"] == "http":
                    value = response(address["url"])
                    if value["configured"] == expected:
                        return address["url"], value
            return None

        url, replacement = wait_for(
            lambda: generation_response("two"), "waiting for the replacement AppHost model", timeout=240
        )
        assert replacement["pid"] != restarted["pid"], (restarted, replacement)
        cli("wait", "late", "--status", "up", "--timeout", "240")
        late = wait_for(lambda: response(endpoint("late")), "waiting for the newly added root")
        assert late["value"] == "after", late
        wait_for(
            lambda: all(not alive(pid) for pid in old_transports | {restarted["pid"], worker["pid"]}),
            "waiting for old-generation transport cleanup",
        )
        cli("wait", "worker", "--status", "up", "--timeout", "240")
        cli("wait", "ordinary", "--status", "up", "--timeout", "240")
        worker = response(endpoint("worker"))
        ordinary = response(endpoint("ordinary"))
        old_transports = transports()
        tracked_processes |= old_transports | {replacement["pid"], worker["pid"], late["pid"], ordinary["pid"]}
        print("WATCH_CHECK:added-root-generation", flush=True)
        apphost.write_text(apphost.read_text().replace(addition, "").replace(
            '"WATCH_MODEL_VALUE", "two"', '"WATCH_MODEL_VALUE", "three"'))
        url, replacement = wait_for(
            lambda: generation_response("three"), "waiting for the removed-root generation", timeout=240
        )
        assert all(not resource.get("displayName", resource["name"]).startswith("late")
                   for resource in describe(name=None))
        wait_for(
            lambda: all(not alive(pid) for pid in old_transports | {worker["pid"], late["pid"]}),
            "waiting for removed-root and old-server cleanup",
        )
        cli("wait", "worker", "--status", "up", "--timeout", "240")
        cli("wait", "ordinary", "--status", "up", "--timeout", "240")
        worker = response(endpoint("worker"))
        ordinary = response(endpoint("ordinary"))
        tracked_processes |= transports() | {replacement["pid"], worker["pid"], ordinary["pid"]}
        worker_url = endpoint("worker")
        ordinary_url = endpoint("ordinary")
        restarted = replacement
        print("WATCH_CHECK:removed-root-generation", flush=True)

    cli("resource", "api", "stop", "--non-interactive")
    wait_for(lambda: not alive(restarted["pid"]), "waiting for requested application cleanup", timeout=45)
    assert cli("resource", "api", "start", "--non-interactive", success=False).returncode != 0
    assert response(worker_url)["pid"] == worker["pid"]
    server = server_processes()
    assert len(server) == 1, server
    # Inject a real crash; the SDK watcher can handle SIGTERM without exiting.
    os.kill(server[0], signal.SIGKILL)
    wait_for(lambda: not alive(server[0]), "waiting for the injected server crash", timeout=15)
    wait_for(
        lambda: describe("worker")[0]["state"] == "FailedToStart",
        "waiting for the active group failure to be reported",
    )
    wait_for(
        lambda: not alive(worker["pid"]),
        "waiting for failed-group application cleanup",
        timeout=45,
    )
    assert response(ordinary_url)["pid"] == ordinary["pid"]
    assert describe("worker")[0]["properties"]["watch.mode"] == "experimental"
    print("WATCH_CHECK:isolated-group-failure", flush=True)
    cli("stop", "--non-interactive")
    wait_for(
        lambda: all(not alive(pid) for pid in tracked_processes),
        "waiting for all watch-owned processes to terminate",
        timeout=45,
    )
    print(f"WATCH_E2E_OK:{language}", flush=True)


if __name__ == "__main__":
    passed = False
    try:
        verify(sys.argv[1])
        passed = True
    finally:
        if not passed:
            for label, arguments in [
                ("resources", ["describe", "--include-hidden", "--format", "json"]),
                ("api", ["logs", "api", "--tail", "300", "--format", "json"]),
                ("worker", ["logs", "worker", "--tail", "300", "--format", "json"]),
                ("ordinary", ["logs", "ordinary", "--tail", "300", "--format", "json"]),
            ]:
                try:
                    diagnostic = cli(*arguments, success=False, timeout=30)
                    (ROOT / f"watch-failure-{label}.log").write_text(
                        f"Exit code: {diagnostic.returncode}\n{diagnostic.stdout}\n{diagnostic.stderr}"
                    )
                except (OSError, subprocess.TimeoutExpired) as error:
                    print(f"Could not capture {label} diagnostics: {error}", file=sys.stderr)
            try:
                processes = subprocess.run(
                    ["ps", "-eo", "pid,ppid,state,args"], capture_output=True, text=True, timeout=10
                )
                (ROOT / "watch-failure-processes.log").write_text(
                    f"Exit code: {processes.returncode}\n{processes.stdout}\n{processes.stderr}"
                )
            except (OSError, subprocess.TimeoutExpired) as error:
                print(f"Could not capture process diagnostics: {error}", file=sys.stderr)
        cleanup = cli("stop", "--non-interactive", success=False)
        if cleanup.returncode:
            print(f"Watch cleanup: {cleanup.stdout}\n{cleanup.stderr}", file=sys.stderr)
