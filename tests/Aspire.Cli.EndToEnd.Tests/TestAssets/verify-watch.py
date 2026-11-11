# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.

import json
import os
import pathlib
import signal
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.request


ROOT = pathlib.Path.cwd()
APPHOST = ROOT / ("Watch.AppHost/Watch.AppHost.csproj" if sys.argv[1] == "csharp" else "apphost.mts")
RECORDED = {}
UNRELATED_APPHOST = None
OWNED_CONTAINERS = set()


def identity(pid):
    try:
        # /proc/<pid>/stat: "123 (comm can contain spaces or ')') S 45 ... starttime".
        # Fields 5/6 are process-group/session IDs and field 22 is boot-relative start ticks.
        # Parse after the last ')' so names containing ')' cannot shift those fields.
        stat = pathlib.Path(f"/proc/{pid}/stat").read_text()
        fields = stat[stat.rindex(")") + 2:].split()
        return {"pid": int(pid), "started": fields[19], "state": fields[0], "parent": int(fields[1]),
                "group": int(fields[2]), "session": int(fields[3])}
    except (FileNotFoundError, ProcessLookupError):
        return None


def remember(pid, label):
    if isinstance(pid, int) and pid > 0:
        value = identity(pid)
        if value:
            RECORDED[(pid, value["started"])] = {**value, "label": label}
            return value
    return None


def identity_alive(original):
    current = identity(original["pid"])
    return current is not None and current["started"] == original["started"] and current["state"] != "Z"


def kill_recorded(original):
    assert original and identity_alive(original), f"Recorded identity is no longer alive: {original}"
    os.kill(original["pid"], signal.SIGKILL)


def pid_file_identity(path, label):
    try:
        text = path.read_text().strip()
    except FileNotFoundError:
        return None
    # File creation can precede the single decimal-PID write; retry the initially empty file.
    if not text:
        return None
    assert text.isdecimal(), f"Invalid PID file {path}: {text!r}"
    return remember(int(text), label)


def cli(*arguments, success=True, timeout=240, apphost=None, environment=None):
    process = subprocess.Popen(
        ["aspire", *arguments, "--apphost", str(apphost or APPHOST)],
        cwd=ROOT,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        env=environment,
    )
    try:
        stdout, stderr = process.communicate(timeout=timeout)
    except subprocess.TimeoutExpired:
        process.kill()
        process.communicate()
        raise
    result = subprocess.CompletedProcess(process.args, process.returncode, stdout, stderr)
    result.launcher_pid = process.pid
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
        raise AssertionError(
            f"Expected a JSON resource snapshot: {result.stdout}\n{result.stderr}"
        ) from error
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
        value = json.load(stream)
        remember(value.get("pid"), "application response")
        return value


def alive(pid):
    current = identity(pid)
    return current is not None and current["state"] != "Z"


def apphost_info(apphost=APPHOST):
    result = subprocess.run(["aspire", "ps", "--format", "json"], cwd=ROOT, capture_output=True, text=True, timeout=30)
    assert result.returncode == 0, (result.stdout, result.stderr)
    # ps emits [{"appHostPath":"/path/host.csproj","appHostPid":123,"cliPid":456}].
    for info in json.loads(result.stdout):
        if pathlib.Path(info["appHostPath"]).resolve() == apphost.resolve():
            assert info.get("cliPid") and info.get("appHostPid"), info
            return info
    raise ConnectionError(f"No AppHost backchannel for {apphost}: {result.stdout}")


def process_table():
    processes = {}
    for path in pathlib.Path("/proc").iterdir():
        if not path.name.isdigit():
            continue
        value = identity(int(path.name))
        if value is None:
            continue
        try:
            value["arguments"] = (path / "cmdline").read_bytes().decode().split("\0")
        except (FileNotFoundError, ProcessLookupError):
            continue
        processes[value["pid"]] = value
    return processes


def fork_scope(cli_pid):
    processes = process_table()
    for pid, process in processes.items():
        arguments = process["arguments"]
        if not all(argument in arguments for argument in ["fork-process", "--enable-child-cleanup", "--skip-descendants", "--monitor"]):
            continue
        if arguments[arguments.index("--monitor") + 1] != str(cli_pid):
            continue
        # The real watcher is "dotnet exec <watch.dll> host ..."; --version is only the probe.
        for child in processes.values():
            if child["parent"] == pid and "exec" in child["arguments"] and "host" in child["arguments"]:
                return {"helper": remember(pid, "DCP fork"), "watcher": remember(child["pid"], "SDK host watcher")}
    return None


def assert_private_watch_group(scope, apphost_pid):
    helper = scope["helper"]
    assert helper["pid"] > 1 and helper["pid"] == helper["group"] == helper["session"], scope
    processes = process_table()
    ancestors = set()
    pid = apphost_pid
    while pid != helper["pid"]:
        assert pid not in ancestors and pid in processes, (scope, apphost_pid, processes)
        process = processes[pid]
        assert process["group"] == process["session"] == helper["pid"], process
        ancestors.add(pid)
        pid = process["parent"]
    assert scope["watcher"]["pid"] in ancestors, (scope, apphost_pid, ancestors)


def owned_snapshot(cli_pid, apphost_pid, scope):
    processes = process_table()
    seeds = {cli_pid, apphost_pid, scope["helper"]["pid"], scope["watcher"]["pid"]}
    while True:
        children = set()
        for pid, process in processes.items():
            arguments = process["arguments"]
            # SDK compiler/build servers are reusable caches, not this run's workloads.
            if any("VBCSCompiler" in argument for argument in arguments) or (
                any("MSBuild.dll" in argument for argument in arguments)
                and any("nodemode:" in argument.lower() for argument in arguments)
            ):
                continue
            monitors_owned = "--monitor" in arguments and arguments[arguments.index("--monitor") + 1] in {str(seed) for seed in seeds}
            if process["parent"] in seeds or monitors_owned:
                children.add(pid)
        expanded = seeds | children
        if expanded == seeds:
            break
        seeds = expanded
    return [value for pid in seeds if (value := remember(pid, "owned runtime process")) is not None]


def container_exists(container_id):
    result = subprocess.run(["podman", "--remote", "container", "exists", container_id], capture_output=True, text=True, timeout=15)
    assert result.returncode in (0, 1), f"Container runtime unavailable: {result.stdout}\n{result.stderr}"
    return result.returncode == 0


def verify_ordinary_probe(value):
    assert value["hostingLoaded"] is True, value
    for name in ["watchOwner", "watchEnabled", "hostControl", "hostSession"]:
        assert value[name] is None, value


def endpoint(name, retry=False):
    resources = describe(name, retry=retry)
    if not resources and retry:
        raise ConnectionError(f"Resource {name} has not appeared in the replacement generation.")
    resource = resources[0]
    url = next((item["url"] for item in resource["urls"] if item["name"] == "http"), None)
    if not url and retry:
        raise ConnectionError(f"Resource {name} has not allocated its HTTP endpoint yet.")
    assert url, f"No HTTP endpoint for {name} in state {resource['state']}: {resource['urls']}"
    return url


def resource_response(name):
    # Model-change coalescing can retire a backchannel after Up; poll the current endpoint
    # and its real response instead of pinning a wait command to an intermediate generation.
    url = endpoint(name, retry=True)
    return url, response(url)


def transports(retry=False):
    processes = set()
    servers = 0
    for resource in describe(name=None, hidden=True, retry=retry):
        properties = resource["properties"]
        if properties.get("watch.mode") == "experimental":
            processes.add(properties["watch.transport.pid"])
        if resource.get("displayName", resource["name"]).startswith("__dotnet-watch-"):
            processes.add(properties["executable.pid"])
            servers += 1
    if not servers and retry:
        raise ConnectionError("The replacement generation has not published its watch server yet.")
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
    verify_ordinary_probe(ordinary)
    initial_host = apphost_info()
    remember(initial_host["cliPid"], "initial long-lived CLI")
    remember(initial_host["appHostPid"], "initial managed AppHost")
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
    verify_ordinary_probe(ordinary)
    current_host = apphost_info()
    assert (current_host["cliPid"], current_host["appHostPid"]) == (
        initial_host["cliPid"], initial_host["appHostPid"]), (initial_host, current_host)
    assert response(url)["pid"] == initial["pid"]
    assert response(worker_url)["pid"] == worker["pid"]
    print("WATCH_CHECK:ordinary-rebuild-isolation", flush=True)
    print("WATCH_CHECK:ordinary-hosting-generation-isolation", flush=True)

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
        resources = wait_for(
            lambda: describe(retry=True), "waiting for the restarted service resource snapshot"
        )
        assert resources[0]["properties"]["watch.transport.pid"] == initial_proxy
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
        late = wait_for(lambda: response(endpoint("late", retry=True)), "waiting for the newly added root")
        assert late["value"] == "after", late
        wait_for(
            lambda: all(not alive(pid) for pid in old_transports | {restarted["pid"], worker["pid"]}),
            "waiting for old-generation transport cleanup",
        )
        worker_url, worker = wait_for(lambda: resource_response("worker"), "waiting for the replacement worker endpoint")
        ordinary_url, ordinary = wait_for(lambda: resource_response("ordinary"), "waiting for the replacement ordinary endpoint")
        old_transports = wait_for(lambda: transports(retry=True), "waiting for the replacement watch transports")
        tracked_processes |= old_transports | {replacement["pid"], worker["pid"], late["pid"], ordinary["pid"]}
        print("WATCH_CHECK:added-root-generation", flush=True)
        apphost.write_text(apphost.read_text().replace(addition, "").replace(
            '"WATCH_MODEL_VALUE", "two"', '"WATCH_MODEL_VALUE", "three"'))
        url, replacement = wait_for(
            lambda: generation_response("three"), "waiting for the removed-root generation", timeout=240
        )
        resources = wait_for(lambda: describe(name=None, retry=True), "waiting for the removed-root resource snapshot")
        assert all(not resource.get("displayName", resource["name"]).startswith("late") for resource in resources)
        wait_for(
            lambda: all(not alive(pid) for pid in old_transports | {worker["pid"], late["pid"]}),
            "waiting for removed-root and old-server cleanup",
        )
        worker_url, worker = wait_for(lambda: resource_response("worker"), "waiting for the final worker endpoint")
        ordinary_url, ordinary = wait_for(lambda: resource_response("ordinary"), "waiting for the final ordinary endpoint")
        tracked_processes |= wait_for(lambda: transports(retry=True), "waiting for the final watch transports")
        tracked_processes |= {replacement["pid"], worker["pid"], ordinary["pid"]}
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
    if language == "csharp":
        verify_cli_owner_lifetimes()
    print(f"WATCH_E2E_OK:{language}", flush=True)


def verify_cli_owner_lifetimes():
    global UNRELATED_APPHOST
    unrelated = ROOT / "Unrelated.AppHost"
    unrelated.mkdir()
    UNRELATED_APPHOST = unrelated / "Unrelated.AppHost.csproj"
    shutil.copyfile(APPHOST, UNRELATED_APPHOST)
    (unrelated / "AppHost.cs").write_text("""
var builder = DistributedApplication.CreateBuilder(args);
builder.AddExecutable("independent", "/bin/sh", builder.AppHostDirectory,
    "-c", "echo $$ > independent.pid; exec sleep 600");
builder.Build().Run();
""")
    cli("run", "--detach", "--non-interactive", apphost=UNRELATED_APPHOST)
    independent = wait_for(lambda: apphost_info(UNRELATED_APPHOST), "waiting for the independent AppHost backchannel")
    independent_owner = remember(independent["cliPid"], "independent CLI")
    independent_pid_file = unrelated / "independent.pid"
    independent_process = wait_for(
        lambda: pid_file_identity(independent_pid_file, "independent workload"),
        "waiting for the independent workload",
    )

    environment = dict(os.environ, WATCH_VERIFY_WORKLOAD="true")
    launch = cli("run", "--watch", "--detach", "--non-interactive", environment=environment)
    for name in ["api", "worker", "ordinary", "watch-owned-workload"]:
        cli("wait", name, "--status", "up", "--timeout", "240")
    current = apphost_info()
    owner = remember(current["cliPid"], "ready detached CLI")
    assert current["cliPid"] != launch.launcher_pid and identity_alive(owner), current
    scope = wait_for(lambda: fork_scope(current["cliPid"]), "waiting for the detached SDK watcher")
    assert_private_watch_group(scope, current["appHostPid"])
    print("WATCH_CHECK:ready-private-watch-group", flush=True)
    workload = describe("watch-owned-workload")[0]
    container_id = workload["properties"]["container.id"]
    assert container_id and container_exists(container_id), workload
    OWNED_CONTAINERS.add(container_id)
    owned = owned_snapshot(current["cliPid"], current["appHostPid"], scope)
    (ROOT / "ready-owned-identities.json").write_text(json.dumps(owned, indent=2))
    kill_recorded(owner)

    wait_for(lambda: all(not identity_alive(record) for record in owned),
             "waiting for ready CLI/watch/AppHost/runtime cleanup", timeout=60)
    wait_for(lambda: not container_exists(container_id), "waiting for DCP-managed container cleanup", timeout=60)
    assert identity_alive(independent_owner) and identity_alive(independent_process)
    assert apphost_info(UNRELATED_APPHOST)["cliPid"] == independent["cliPid"]
    print("WATCH_CHECK:ready-cli-sigkill-and-workload-cleanup", flush=True)
    print("WATCH_CHECK:detached-owner-and-unrelated-run-isolation", flush=True)

    gate = ROOT / "startup-gate"
    environment = dict(os.environ, WATCH_STARTUP_GATE=str(gate))
    with (ROOT / "startup-owner.log").open("w") as output:
        startup = subprocess.Popen(
            ["aspire", "run", "--watch", "--non-interactive", "--apphost", str(APPHOST)],
            cwd=ROOT, stdout=output, stderr=subprocess.STDOUT, env=environment,
        )
        startup_owner = remember(startup.pid, "startup CLI")
        try:
            entered = pathlib.Path(str(gate) + ".entered")
            host = wait_for(lambda: pid_file_identity(entered, "starting managed AppHost"),
                            "waiting for managed AppHost before backchannel readiness", timeout=240)
            host_pid = host["pid"]
            scope = wait_for(lambda: fork_scope(startup.pid), "waiting for the actual guarded SDK watcher")
            assert_private_watch_group(scope, host_pid)
            print("WATCH_CHECK:startup-private-watch-group", flush=True)
            owned = owned_snapshot(startup.pid, host_pid, scope)
            (ROOT / "startup-owned-identities.json").write_text(json.dumps(owned, indent=2))
            kill_recorded(startup_owner)
            assert startup.wait(timeout=15) != 0
            # Observe native identities directly; aspire ps itself has orphan collection side effects.
            wait_for(lambda: all(not identity_alive(record) for record in owned),
                     "waiting for startup CLI/watch/AppHost cleanup", timeout=60)
            assert identity_alive(independent_owner) and identity_alive(independent_process)
            assert apphost_info(UNRELATED_APPHOST)["cliPid"] == independent["cliPid"]
            print("WATCH_CHECK:startup-cli-sigkill", flush=True)
        finally:
            if startup.poll() is None:
                kill_recorded(startup_owner)
                startup.wait(timeout=15)
            pathlib.Path(str(gate) + ".release").write_text("release")

    cli("stop", "--non-interactive", apphost=UNRELATED_APPHOST)
    wait_for(lambda: not identity_alive(independent_owner) and not identity_alive(independent_process),
             "waiting for independent-run cleanup", timeout=45)
    UNRELATED_APPHOST = None


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
        if UNRELATED_APPHOST is not None:
            cli("stop", "--non-interactive", apphost=UNRELATED_APPHOST, success=False)
        for container_id in OWNED_CONTAINERS:
            if container_exists(container_id):
                subprocess.run(["podman", "--remote", "rm", "--force", container_id], check=True, timeout=30)
        if not passed:
            for record in RECORDED.values():
                if identity_alive(record):
                    kill_recorded(record)
