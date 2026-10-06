#!/usr/bin/env python3
# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.
"""Exercise the published tray against a private watcher, menu client and fake CLI."""

import json
import fcntl
import os
from pathlib import Path
import shutil
import socket
import subprocess
import sys
import tempfile
import time

from gi.repository import Gio, GLib


WATCHER = "org.kde.StatusNotifierWatcher"
WATCHER_XML = """<node><interface name="org.kde.StatusNotifierWatcher">
<method name="RegisterStatusNotifierItem"><arg type="s" direction="in"/></method>
<property name="IsStatusNotifierHostRegistered" type="b" access="read"/>
<property name="ProtocolVersion" type="i" access="read"/>
<property name="RegisteredStatusNotifierItems" type="as" access="read"/>
<signal name="StatusNotifierHostRegistered"/>
</interface></node>"""


def wait(predicate, message, seconds=15):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        while GLib.MainContext.default().pending():
            GLib.MainContext.default().iteration(False)
        if predicate():
            return
        time.sleep(0.02)
    raise AssertionError(message)


def stop(process):
    if process.poll() is None:
        process.terminate()
        try:
            process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=5)


def run(publish):
    with tempfile.TemporaryDirectory(prefix="aspire-linux-tray-") as temporary:
        root = Path(temporary)
        home = root / "home"
        runtime = root / "runtime"
        home.mkdir()
        runtime.mkdir(mode=0o700)
        environment = dict(os.environ, HOME=str(home), XDG_CONFIG_HOME=str(home / ".config"),
                           XDG_STATE_HOME=str(home / ".local/state"), XDG_DATA_HOME=str(home / ".local/share"),
                           XDG_RUNTIME_DIR=str(runtime), ASPIRE_HOME=str(home / ".aspire"),
                           GDK_BACKEND="broadway", BROADWAY_DISPLAY=":1", NO_AT_BRIDGE="1",
                           GIO_USE_VFS="local", GSETTINGS_BACKEND="memory")
        environment.pop("DISPLAY", None)
        environment.pop("WAYLAND_DISPLAY", None)
        # No real desktop, home, CLI or AppHost participates in this smoke run.
        bundle = root / "bundle"
        payload = bundle / "tray"
        shutil.copytree(publish, payload)
        cli = root / "aspire-fixture"
        apphost = root / "Example.AppHost.csproj"
        apphost.write_text("<Project />")
        snapshot = root / "snapshot.json"
        def write_snapshot(contents):
            pending = snapshot.with_suffix(".pending")
            pending.write_text(contents)
            pending.replace(snapshot)
        message = {"version": 1, "type": "snapshot", "appHosts": [{
            "appHostPath": str(apphost), "appHostPid": 4242,
            "processStartTimeUnixMilliseconds": 1000, "health": "healthy",
            "dashboardUrl": "http://localhost:18888/login?t=SYNTHETIC-SECRET"}]}
        live_message = json.dumps(message)
        write_snapshot(json.dumps(message))
        cli.write_text(
            f"#!{sys.executable}\n"
            "import json, os, pathlib, sys, time\n"
            "root = pathlib.Path(__file__).parent\n"
            "with (root / 'calls.jsonl').open('a') as f: f.write(json.dumps(sys.argv[1:]) + '\\n')\n"
            "if sys.argv[1] == 'ps':\n"
            "  (root / 'watcher.pid').write_text(str(os.getpid()))\n"
            "  while True:\n"
            "    print((root / 'snapshot.json').read_text(), flush=True)\n"
            "    time.sleep(0.1)\n"
            "else:\n"
            "  print(json.dumps({'version':1, 'outcome':'stopped', 'exitCode':0}))\n")
        cli.chmod(0o700)
        executable = payload / "aspire-tray"
        log = (root / "smoke.log").open("w+")
        broadway = subprocess.Popen(["broadwayd", ":1", "--unixsocket", str(root / "http.sock")],
                                    env=environment, stdout=log, stderr=log)
        gui = None
        helper = None
        connection = Gio.bus_get_sync(Gio.BusType.SESSION, None)
        registered = []

        def call(destination, path, interface, method, parameters=None):
            return connection.call_sync(destination, path, interface, method, parameters, None,
                                        Gio.DBusCallFlags.NONE, 5000, None).unpack()

        def ownership(method):
            arguments = GLib.Variant("(su)", (WATCHER, 4)) if method == "RequestName" else GLib.Variant("(s)", (WATCHER,))
            return call("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", method, arguments)

        def on_method(bus, sender, path, interface, method, args, invocation):
            value = args.unpack()[0]
            registered.append((sender, value if value.startswith("/") else "/StatusNotifierItem"))
            invocation.return_value(None)

        def on_property(bus, sender, path, interface, property_name):
            return {
                "IsStatusNotifierHostRegistered": GLib.Variant("b", True),
                "ProtocolVersion": GLib.Variant("i", 0),
                "RegisteredStatusNotifierItems": GLib.Variant("as", [])
            }[property_name]

        info = Gio.DBusNodeInfo.new_for_xml(WATCHER_XML).interfaces[0]
        registration = connection.register_object("/StatusNotifierWatcher", info, on_method, on_property, None)
        try:
            assert ownership("RequestName") == (1,)
            def broadway_ready():
                # Broadway uses an abstract Unix socket on Linux, not a directory entry.
                with socket.socket(socket.AF_UNIX) as client:
                    try:
                        client.connect("\0" + str(runtime / "broadway2.socket"))
                        return True
                    except (ConnectionRefusedError, FileNotFoundError):
                        return False
            wait(broadway_ready, "Broadway did not become ready")
            # Start in the foreground first so all runtime output is captured.
            gui = subprocess.Popen([str(executable), "--cli", str(cli)], env=environment, stdout=log, stderr=log)
            wait(lambda: bool(registered) or gui.poll() is not None, "Tray did not register")
            assert gui.poll() is None, "Tray exited before registering"
            sender, path = registered[-1]
            properties = call(sender, path, "org.freedesktop.DBus.Properties", "GetAll",
                              GLib.Variant("(s)", ("org.kde.StatusNotifierItem",)))[0]
            assert properties["Status"] == "Active"
            menu_path = properties["Menu"]

            def layout():
                return call(sender, menu_path, "com.canonical.dbusmenu", "GetLayout",
                            GLib.Variant("(iias)", (0, -1, [])))[1]

            def rows(node):
                yield node
                for child in node[2]:
                    yield from rows(child)

            def find(label):
                return next((row for row in rows(layout()) if row[1].get("label") == label), None)

            def click(row):
                call(sender, menu_path, "com.canonical.dbusmenu", "Event",
                     GLib.Variant("(isvu)", (row[0], "clicked", GLib.Variant("i", 0), 0)))

            wait(lambda: find("Example - Healthy") is not None, "Healthy AppHost missing from D-Bus menu")
            assert "SYNTHETIC-SECRET" not in str(layout()), "Dashboard credentials leaked into menu"
            host_row = find("Example - Healthy")
            assert host_row[1]["children-display"] == "submenu"
            healthy_icon = host_row[1]["icon-data"]
            assert bytes(healthy_icon[:8]) == b"\x89PNG\r\n\x1a\n", "Health icon was not exported as PNG"
            assert find("Documentation")[1]["icon-name"] == "help-browser"
            assert find("Settings...")[1]["icon-name"] == "preferences-system"
            assert find("Open dashboard")[1].get("enabled", True)
            assert find("Stop AppHost...")[1].get("enabled", True)
            click(find("Pin AppHost"))
            wait(lambda: find("Unpin AppHost") is not None, "Pin action did not update menu")
            message["appHosts"][0]["health"] = "unhealthy"
            write_snapshot(json.dumps(message))
            wait(lambda: find("Example - Unhealthy") is not None, "Health update missing")
            assert find("Example - Unhealthy")[1]["icon-data"] != healthy_icon, "Health icon did not update"
            message["appHosts"] = []
            write_snapshot(json.dumps(message))
            wait(lambda: find("Start AppHost") is not None, "Offline pinned host missing")
            calls = [json.loads(line) for line in (root / "calls.jsonl").read_text().splitlines()]
            assert len(calls) == 1 and calls[0][0] == "ps", "Viewing menus unexpectedly launched a CLI action"
            assert "SYNTHETIC-SECRET" not in (home / ".aspire/tray/apphosts.json").read_text()

            # A restarted watcher must recover the item without starting a second CLI.
            before = len(registered)
            ownership("ReleaseName")
            wait(lambda: not call(sender, path, "org.freedesktop.DBus.Properties", "GetAll",
                                 GLib.Variant("(s)", ("org.kde.StatusNotifierItem",)))[0].get("invalid", False),
                 "Tray stopped serving properties")
            # Deliver NameOwnerChanged before reacquiring the name.
            for _ in range(20):
                GLib.MainContext.default().iteration(False)
                time.sleep(0.02)
            assert ownership("RequestName") == (1,)
            wait(lambda: len(registered) > before, "Tray failed to re-register after watcher restart")
            click(find("Quit Aspire"))
            wait(lambda: gui.poll() is not None, "Quit did not stop tray")
            assert gui.returncode == 0
            watcher_pid = int((root / "watcher.pid").read_text())
            wait(lambda: not Path(f"/proc/{watcher_pid}").exists(), "Discovery child survived Quit")

            # Exercise packaged detachment, readiness, singleton restore and exact stop.
            saved_path = home / ".aspire/tray/apphosts.json"
            saved = json.loads(saved_path.read_text())
            saved["confirmStop"] = False
            saved_path.write_text(json.dumps(saved))
            write_snapshot(live_message)
            start = [str(executable), "start", "--cli", str(cli), "--bundle-root", str(bundle)]
            before = len(registered)
            helper = subprocess.Popen(start, env=environment, stdout=log, stderr=log)
            wait(lambda: helper.poll() is not None, "Packaged start failed to acknowledge", seconds=20)
            assert helper.returncode == 0
            assert len(registered) > before
            sender, path = registered[-1]
            menu_path = call(sender, path, "org.freedesktop.DBus.Properties", "GetAll",
                             GLib.Variant("(s)", ("org.kde.StatusNotifierItem",)))[0]["Menu"]
            wait(lambda: find("Stop AppHost") is not None, "Saved confirmation preference was not applied")
            count = len((root / "calls.jsonl").read_text().splitlines())
            helper = subprocess.Popen(start, env=environment, stdout=log, stderr=log)
            wait(lambda: helper.poll() is not None, "Repeated start failed")
            assert helper.returncode == 0
            assert len((root / "calls.jsonl").read_text().splitlines()) == count
            click(find("Stop AppHost"))
            wait(lambda: len((root / "calls.jsonl").read_text().splitlines()) > count, "Stop did not invoke CLI")
            stop_call = json.loads((root / "calls.jsonl").read_text().splitlines()[-1])
            assert stop_call == ["stop", "--apphost", str(apphost), "--pid", "4242", "--started-at", "1000",
                                 "--format", "json", "--protocol-version", "1", "--non-interactive", "--nologo"]
            subprocess.run([str(executable), "stop"], env=environment, stdout=log, stderr=log, check=True, timeout=15)
            watcher_pid = int((root / "watcher.pid").read_text())
            wait(lambda: not Path(f"/proc/{watcher_pid}").exists(), "Discovery child survived packaged stop")

            ownership("ReleaseName")
            helper = subprocess.Popen(start, env=environment, stdout=log, stderr=log)
            wait(lambda: helper.poll() is not None, "Missing watcher did not fail startup", seconds=20)
            assert helper.returncode != 0, "An invisible tray reported startup success"
            watcher_pid = int((root / "watcher.pid").read_text())
            wait(lambda: not Path(f"/proc/{watcher_pid}").exists(), "Failed startup left discovery running")
            for lease in (bundle / ".leases").glob("*.lease"):
                # Crashed holders may leave files behind; ownership is the OS lock.
                with lease.open("rb") as file:
                    fcntl.flock(file, fcntl.LOCK_EX | fcntl.LOCK_NB)
            print("PASS: SNI registration, D-Bus actions, exact stop, health, pins, privacy, watcher recovery, lifecycle and missing-watcher cleanup.")
        except Exception:
            log.flush()
            log.seek(0)
            print(log.read(), file=sys.stderr)
            runtime_log = home / ".aspire/tray/runtime/aspire-tray.log"
            if runtime_log.exists():
                print(runtime_log.read_text(), file=sys.stderr)
            raise
        finally:
            if gui:
                stop(gui)
            if helper:
                stop(helper)
            # Idempotent shutdown targets only this isolated HOME.
            subprocess.run([str(executable), "stop"], env=environment, stdout=log, stderr=log, timeout=15)
            ownership("ReleaseName")
            connection.unregister_object(registration)
            stop(broadway)
            log.close()


if __name__ == "__main__":
    if len(sys.argv) == 3 and sys.argv[1] == "--private-bus":
        run(Path(sys.argv[2]).resolve())
    elif len(sys.argv) == 2:
        subprocess.run(["dbus-run-session", "--", sys.executable, __file__, "--private-bus",
                        str(Path(sys.argv[1]).resolve())], check=True, timeout=90)
    else:
        raise SystemExit("Usage: smoke-test.py <published Linux tray directory>")
