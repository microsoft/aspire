# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.

"""Assembled watch checks; no Python packages, external services, or nested containers."""

import argparse
import concurrent.futures
import dataclasses
import errno
import fcntl
import json
import os
import pathlib
import pty
import re
import shutil
import signal
import sqlite3
import ssl
import subprocess
import sys
import tempfile
import termios
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
import xml.etree.ElementTree as ET
import zipfile


SDK_VERSION = "10.0.403"
WATCH_TOOL = "Microsoft.DotNet.HotReload.Watch.Aspire.dll"
WATCH_HOST = "Aspire.Hosting.Dotnet.WatchHost.dll"
RESOURCE_SUPPRESSION = "ASPIRE_RESOURCE_WATCH_SUPPRESS"
CLEANUP_MARKER = "ASPIRE_DOTNET_WATCH_CLEANUP_MARKER"
PRIVATE_ENVIRONMENT = (
    "ASPIRE_DOTNET_WATCH_OWNER",
    "ASPIRE_DOTNET_WATCH_HOST_CONTROL",
    "ASPIRE_DOTNET_WATCH_HOST_SESSION",
    RESOURCE_SUPPRESSION,
    CLEANUP_MARKER,
    "DEBUG_SESSION_PORT",
    "DEBUG_SESSION_TOKEN",
    "DEBUG_SESSION_INFO",
    "DEBUG_SESSION_SERVER_URI",
    "DEBUG_SESSION_SERVER_CERTIFICATE",
)
WATCH_ENABLED = "AppHost__Run__WatchEnabled"
RUN_TAG = "WATCH_TEST_RUN_ID"
POLL_INTERVAL = 0.25


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def write(path, content):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(content.rstrip() + "\n", encoding="utf-8")


def replace_once(path, before, after):
    source = path.read_text(encoding="utf-8")
    require(source.count(before) == 1, f"Expected one edit anchor {before!r} in {path}.")
    write(path, source.replace(before, after))


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8"))


def wait_for(predicate, description, timeout=150):
    deadline = time.monotonic() + timeout
    last_error = None
    while time.monotonic() < deadline:
        try:
            value = predicate()
            if value:
                return value
        except (urllib.error.URLError, TimeoutError, ConnectionError, FileNotFoundError, ProcessLookupError) as error:
            last_error = error
        time.sleep(min(POLL_INTERVAL, max(0, deadline - time.monotonic())))
    raise AssertionError(f"Timed out {description}; last transient error: {last_error}")


def stays_true(predicate, description, seconds=3):
    # Negative assertions need an observation window after a positive settled event,
    # not a sleep used as a substitute for readiness.
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        require(predicate(), f"Observation changed while {description}.")
        time.sleep(POLL_INTERVAL)


def request_json(url, context=None):
    request = urllib.request.Request(url, headers={"Connection": "close"})
    with urllib.request.urlopen(request, timeout=3, context=context) as response:
        return json.load(response)


@dataclasses.dataclass(frozen=True)
class ProcessIdentity:
    pid: int
    started: str
    arguments: tuple

    @staticmethod
    def read(pid):
        directory = pathlib.Path("/proc") / str(pid)
        try:
            # Linux /proc/<pid>/stat: "pid (comm with spaces) state ppid ... starttime ...".
            # starttime is field 22, or offset 19 after the final ')' and following state.
            tail = (directory / "stat").read_text().rsplit(")", 1)[1].split()
            if tail[0] in ("Z", "X"):
                return None
            arguments = tuple(
                part.decode("utf-8", errors="replace")
                for part in (directory / "cmdline").read_bytes().split(b"\0") if part
            )
            return ProcessIdentity(int(pid), tail[19], arguments)
        except (FileNotFoundError, ProcessLookupError, PermissionError):
            return None

    def alive(self):
        current = self.read(self.pid)
        return current is not None and current.started == self.started

    def environment(self):
        if not self.alive():
            raise ProcessLookupError(f"Process identity {self.pid}/{self.started} is no longer live.")
        # Read only this fixture's tagged process; never serialize the inherited environment.
        raw = (pathlib.Path("/proc") / str(self.pid) / "environ").read_bytes()
        return {
            key.decode(errors="replace"): value.decode(errors="replace")
            for part in raw.split(b"\0") if b"=" in part
            for key, value in [part.split(b"=", 1)]
        }

    def signal(self, signum, tag):
        try:
            descriptor = os.pidfd_open(self.pid)
        except ProcessLookupError:
            return
        try:
            if not self.alive():
                return
            try:
                environment = self.environment()
            except (FileNotFoundError, ProcessLookupError):
                return
            require(environment.get(RUN_TAG) == tag, f"Refusing to signal unowned PID {self.pid}.")
            # The pidfd closes the check/kill PID-reuse race. A departed identity
            # must never turn a fixture failure injection into a signal to a peer.
            signal.pidfd_send_signal(descriptor, signum)
        except ProcessLookupError:
            pass
        finally:
            os.close(descriptor)

    def evidence(self):
        return {"pid": self.pid, "linuxStartTime": self.started, "arguments": list(self.arguments)}


class Evidence:
    def __init__(self, root, scenario):
        self.path = root / ".aspire-diagnostics" / "watch" / f"{scenario}.json"
        self.values = {"scenario": scenario, "checks": [], "passed": False}

    def check(self, name, **values):
        self.values["checks"].append({"name": name, **values})
        self.save()
        print(f"WATCH_CHECK:{name}", flush=True)

    def save(self):
        write(self.path, json.dumps(self.values, indent=2))


def package_metadata(path):
    with zipfile.ZipFile(path) as package:
        nuspec = next(name for name in package.namelist() if name.endswith(".nuspec"))
        metadata = ET.fromstring(package.read(nuspec))
        return {
            element.tag.rsplit("}", 1)[-1]: element.text
            for element in metadata.iter()
            if element.tag.rsplit("}", 1)[-1] in ("id", "version")
        }


class PackageHive:
    def __init__(self, explicit=None):
        candidates = []
        configured = explicit or os.environ.get("ASPIRE_CLI_PACKAGES")
        if configured:
            candidates.append(pathlib.Path(configured).resolve())
        else:
            hives = pathlib.Path.home() / ".aspire" / "hives"
            if hives.is_dir():
                candidates.extend(sorted(hives.glob("*/packages")))

        found = []
        for directory in candidates:
            for package in directory.glob("Aspire.Hosting.Dotnet.*.nupkg"):
                if ".symbols." not in package.name:
                    metadata = package_metadata(package)
                    if metadata["id"] == "Aspire.Hosting.Dotnet":
                        found.append((directory.resolve(), metadata["version"]))
        require(
            len(found) == 1,
            "Exactly one current packed Aspire.Hosting.Dotnet is required. "
            f"Set ASPIRE_CLI_PACKAGES (or --packages) to the fresh local hive; found {found}.",
        )
        self.directory, self.version = found[0]
        self.package("Aspire.AppHost.Sdk")
        self.package("Aspire.Hosting.AppHost")

    def package(self, name):
        matches = [
            path for path in self.directory.glob(f"{name}.*.nupkg")
            if ".symbols." not in path.name
            and (metadata := package_metadata(path))["id"] == name
            and metadata["version"] == self.version
        ]
        require(len(matches) == 1, f"Missing fresh {name}/{self.version} in {self.directory}.")
        return matches[0]


def installed_sdks(environment):
    muxers = []
    configured = environment.get("WATCH_DOTNET10_ROOT")
    if configured:
        muxers.append(pathlib.Path(configured) / "dotnet")
    for item in environment.get("PATH", "").split(os.pathsep):
        muxer = pathlib.Path(item) / "dotnet"
        if muxer.is_file():
            muxers.append(muxer)
    if environment.get("DOTNET_ROOT"):
        muxers.append(pathlib.Path(environment["DOTNET_ROOT"]) / "dotnet")
    # The runner's repo SDK need not list the system SDK. This is the normal
    # Linux installation root, not a watcher installer/cache or roll-forward.
    muxers.append(pathlib.Path("/usr/share/dotnet/dotnet"))
    clean = dict(environment)
    for name in ("MSBuildSDKsPath", "MSBuildExtensionsPath", "MSBUILD_EXE_PATH", "DOTNET_HOST_PATH"):
        clean.pop(name, None)
    found = {}
    for muxer in dict.fromkeys(muxers):
        if not muxer.is_file():
            continue
        result = subprocess.run(
            [str(muxer), "--list-sdks"], capture_output=True, text=True, timeout=15, env=clean
        )
        require(result.returncode == 0, f"Could not list SDKs with {muxer}: {result.stderr}")
        # dotnet --list-sdks emits "10.0.403 [/usr/share/dotnet/sdk]"; the bracketed
        # path, including spaces, is authoritative. Never substitute the runner's SDK.
        for line in result.stdout.splitlines():
            match = re.fullmatch(r"(\S+) \[(.+)\]", line.strip())
            if match:
                version, sdk_directory = match.groups()
                found.setdefault(version, pathlib.Path(sdk_directory).parent.resolve())
    require(SDK_VERSION in found, f"The bundled watcher scenarios require installed SDK {SDK_VERSION}; found {found}.")
    return found


def configure_feeds(root, hive, base):
    require(base is not None and base.is_file(),
            "Supply the approved NuGet configuration through --nuget-config or WATCH_NUGET_CONFIG.")
    parser = ET.XMLParser(target=ET.TreeBuilder(insert_comments=True))
    configuration = ET.parse(base, parser=parser).getroot()
    sources = configuration.find("packageSources")
    require(sources is not None, "The supplied approved configuration has no packageSources.")
    declared = sources.findall("add")
    disabled_section = configuration.find("disabledPackageSources")
    disabled = {
        item.attrib["key"].casefold()
        for item in disabled_section.findall("add") if item.attrib.get("value", "").casefold() == "true"
    } if disabled_section is not None else set()
    mapping = configuration.find("packageSourceMapping")
    wildcard_keys = {
        source.attrib["key"].casefold() for source in mapping.findall("packageSource")
        if any(package.attrib.get("pattern") == "*" for package in source.findall("package"))
    } if mapping is not None else {source.attrib["key"].casefold() for source in declared}
    # The caller's supplied configuration is the approved source inventory. Use
    # its first enabled HTTPS wildcard feed for tools instead of adding a fallback
    # endpoint or weakening the existing source mappings/audit configuration.
    tool_source = next((
        source.attrib["value"] for source in declared
        if source.attrib["key"].casefold() not in disabled
        and source.attrib["key"].casefold() in wildcard_keys
        and urllib.parse.urlparse(source.attrib["value"]).scheme == "https"
    ), None)
    require(tool_source is not None,
            "The approved NuGet configuration needs an enabled HTTPS wildcard source for tool packages.")
    if mapping is None:
        # No source mapping means all declared sources were implicitly eligible.
        # Keep that behavior when adding the more-specific fresh Aspire mapping.
        mapping = ET.SubElement(configuration, "packageSourceMapping")
        for source in declared:
            preserved = ET.SubElement(mapping, "packageSource", key=source.attrib["key"])
            ET.SubElement(preserved, "package", pattern="*")
    source_keys = {source.attrib["key"].casefold() for source in declared}
    local_key = "watch-fixture-local"
    suffix = 1
    while local_key.casefold() in source_keys:
        suffix += 1
        local_key = f"watch-fixture-local-{suffix}"
    ET.SubElement(sources, "add", key=local_key, value=str(hive.directory))
    local = ET.SubElement(mapping, "packageSource", key=local_key)
    ET.SubElement(local, "package", pattern="Aspire.*")
    ET.indent(configuration, space="  ")
    ET.ElementTree(configuration).write(root / "NuGet.config", encoding="utf-8", xml_declaration=True)
    return tool_source


def npm_registry(environment, cwd):
    registry = environment.get("npm_config_registry") or environment.get("NPM_CONFIG_REGISTRY")
    if not registry:
        result = subprocess.run(
            ["npm", "config", "get", "registry"], cwd=cwd, env=environment,
            capture_output=True, text=True, timeout=15,
        )
        require(result.returncode == 0, f"Could not read the caller's npm registry: {result.stderr}")
        registry = result.stdout.strip()
    parsed = urllib.parse.urlparse(registry)
    require(parsed.scheme in ("http", "https") and parsed.netloc,
            "The caller's npm registry must be an HTTP(S) URL.")
    return registry.rstrip("/") + "/"


def service_source(shared=False):
    shared_value = "WatchShared.SharedValue.Read()" if shared else '"no-shared-library"'
    private_names = ", ".join(json.dumps(name) for name in PRIVATE_ENVIRONMENT)
    return """
using System.Reflection;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.MapGet("/health", () => "ok");
app.MapGet("/", () => new
{
    value = ValueSource.Read(),
    shared = SHARED_VALUE,
    imported = ValueSource.Imported(),
    instance = WatchProcess.Instance,
    pid = Environment.ProcessId,
    configured = Environment.GetEnvironmentVariable("WATCH_MODEL_VALUE"),
    profile = Environment.GetEnvironmentVariable("WATCH_PROFILE_VALUE"),
    otel = Environment.GetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES"),
    arguments = args,
    configuration = Assembly.GetEntryAssembly()!.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration,
    workingDirectory = Environment.CurrentDirectory,
    workingToken = File.Exists("watch-working-token.txt") ? File.ReadAllText("watch-working-token.txt").Trim() : null,
    temporaryDirectory = Path.GetTempPath(),
    trustEnvironment = new[] { "SSL_CERT_DIR", "SSL_CERT_FILE" }
        .Where(name => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
        .ToDictionary(name => name, name => Environment.GetEnvironmentVariable(name)!),
    privateEnvironment = WatchProcess.PrivateEnvironment
});
app.MapGet("/trust", async () =>
{
    using var client = new HttpClient();
    return await client.GetStringAsync(Environment.GetEnvironmentVariable("WATCH_TRUST_URL")!);
});
app.Run();

public class ValueSource
{
    public static string Read() => "before";
    public static string Imported()
    {
#if WATCH_IMPORTED
        return "imported";
#else
        return "no-import";
#endif
    }
}

public class ReplacementBase { }

public static class WatchProcess
{
    public static string Instance { get; } = Guid.NewGuid().ToString("N");
    public static Dictionary<string, string> PrivateEnvironment => new[] { PRIVATE_NAMES }
        .Where(name => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
        .ToDictionary(name => name, name => Environment.GetEnvironmentVariable(name)!);
}
""".replace("SHARED_VALUE", shared_value).replace("PRIVATE_NAMES", private_names)


class Fixture:
    def __init__(self, context, name, language="csharp", extra_packages=()):
        self.context = context
        self.root = context.root / name
        require(not self.root.exists(), f"Use a new fixture directory; {self.root} already exists.")
        self.root.mkdir(parents=True)
        self.hive = context.hive
        self.tag = uuid.uuid4().hex
        self.language = language
        self.environment = dict(os.environ)
        for key in (
            "MSBuildSDKsPath", "MSBuildExtensionsPath", "MSBUILD_EXE_PATH", "DOTNET_HOST_PATH",
            "DOTNET_ROLL_FORWARD", "DOTNET_ROLL_FORWARD_TO_PRERELEASE",
            *PRIVATE_ENVIRONMENT, WATCH_ENABLED,
        ):
            self.environment.pop(key, None)
        sdk_root = context.sdks[SDK_VERSION]
        self.dotnet = str(sdk_root / "dotnet")
        # The watcher's agent appends CoreFxPipe_<32 hex digits>, and multi-root
        # builds add another SDK-pinned directory. Keep the runner's short temp
        # base instead of nesting it under the descriptive fixture directory.
        scratch = pathlib.Path(tempfile.gettempdir())
        self.environment.update({
            RUN_TAG: self.tag,
            "DOTNET_ROOT": str(sdk_root),
            "PATH": str(sdk_root) + os.pathsep + self.environment.get("PATH", ""),
            "DOTNET_CLI_HOME": str(self.root / ".cli-home"),
            "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
            "DOTNET_NOLOGO": "1",
            "DOTNET_GENERATE_ASPNET_CERTIFICATE": "false",
            "DOTNET_CLI_USE_MSBUILD_SERVER": "0",
            "MSBUILDDISABLENODEREUSE": "1",
            "MSBUILDTERMINALLOGGER": "false",
            "TMPDIR": str(scratch),
            "TMP": str(scratch),
            "TEMP": str(scratch),
            "ASPIRE_CLI_CHANNEL": "local",
            "ASPIRE_CLI_VERSION": self.hive.version,
            "ASPIRE_CLI_PACKAGES": str(self.hive.directory),
            "ASPIRE_CLI_START_TIMEOUT": "240",
            "ASPIRE_PLAYGROUND": "true",
        })
        self.environment.setdefault("npm_config_cache", str(self.root / ".npm-cache"))
        if language == "typescript":
            self.npm_registry = npm_registry(self.environment, self.root)
            self.environment["npm_config_registry"] = self.npm_registry
        self.pin_sdk(self.root)
        selected = self.command([self.dotnet, "--version"])
        require(selected.stdout.strip() == SDK_VERSION,
                f"Fixture global.json paths did not select SDK {SDK_VERSION}: {selected.stdout!r}")
        # Fixtures under wv must not inherit Arcade/central package/build settings
        # from the repository. This is fixture configuration, never a root edit.
        write(self.root / "Directory.Build.props", """
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
    <UseSharedCompilation>false</UseSharedCompilation>
  </PropertyGroup>
</Project>
""")
        write(self.root / "Directory.Build.targets", "<Project />")
        write(self.root / "Directory.Packages.props", "<Project />")
        self.tool_source = configure_feeds(self.root, self.hive, context.feeds)
        self.config = {
            "channel": "local",
            "sdk": {"version": self.hive.version},
            "features": {"defaultWatchEnabled": False, "appHostServerV2": False},
        }
        self.save_config()
        self.tracked = set()
        self.foreground = None
        self.owner_pid = None
        self.log_handles = []
        self.terminal_master = None
        self.terminal_reader = None
        self.terminal_error = None
        self.run_directories = set()
        self.expected_preservations = {}
        self.context.fixtures.append(self)
        if language == "csharp":
            self.apphost = self.root / "Watch.AppHost" / "Watch.AppHost.csproj"
            references = "".join(
                f'    <PackageReference Include="{name}" Version="{self.hive.version}" />\n'
                for name in ["Aspire.Hosting.AppHost", "Aspire.Hosting.Dotnet", *extra_packages]
            )
            for package in extra_packages:
                self.hive.package(package)
            write(self.apphost, f"""
<Project Sdk="Aspire.AppHost.Sdk/{self.hive.version}">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <IsAspireHost>true</IsAspireHost>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <NoWarn>$(NoWarn);ASPIREPROJECTS001;ASPIREWATCH001;ASPIREDOTNETTOOL</NoWarn>
  </PropertyGroup>
  <ItemGroup>
{references.rstrip()}
  </ItemGroup>
</Project>
""")
        else:
            self.apphost = self.root / "apphost.mts"

    def pin_sdk(self, directory, version=SDK_VERSION):
        write(directory / "global.json", json.dumps({
            "sdk": {
                "version": version,
                "rollForward": "disable",
                "allowPrerelease": "-" in version,
                "paths": [str(self.context.sdks[version])],
            }
        }, indent=2))

    def save_config(self):
        write(self.root / "aspire.config.json", json.dumps(self.config, indent=2))

    def command(self, arguments, success=True, timeout=240, environment=None, cwd=None):
        result = subprocess.run(
            [str(argument) for argument in arguments],
            cwd=cwd or self.root, env=environment or self.environment,
            capture_output=True, text=True, timeout=timeout,
        )
        if success:
            require(
                result.returncode == 0,
                f"{arguments} failed ({result.returncode}):\n{result.stdout}\n{result.stderr}",
            )
        return result

    def cli(self, *arguments, scoped=True, **kwargs):
        command = ["aspire", *arguments]
        if scoped:
            command += ["--apphost", str(self.apphost)]
        return self.command(command, **kwargs)

    def dotnet_command(self, *arguments, **kwargs):
        return self.command([self.dotnet, *arguments], **kwargs)

    def service(self, name="Api", shared=False, references=(), properties="", imports="", packages=()):
        directory = self.root / name
        project = directory / f"{directory.name}.csproj"
        items = "".join(f'    <ProjectReference Include="{reference}" />\n' for reference in references)
        if shared:
            items += '    <ProjectReference Include="../Shared/Shared.csproj" />\n'
        items += "".join(
            f'    <PackageReference Include="{package}" Version="{version}" />\n'
            for package, version in packages
        )
        write(project, f"""
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
{properties}
  </PropertyGroup>
  <ItemGroup>
{items.rstrip()}
  </ItemGroup>
{imports}
</Project>
""")
        write(directory / "Program.cs", service_source(shared))
        return project

    def shared(self, name="Shared", value="shared-before", properties=""):
        directory = self.root / name
        write(directory / f"{directory.name}.csproj", f"""
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
{properties}
  </PropertyGroup>
</Project>
""")
        write(directory / "SharedValue.cs", f"""
namespace WatchShared;
public static class SharedValue
{{
    public static string Read() => "{value}";
}}
""")

    def csharp_model(self, registrations, value="one"):
        write(self.apphost.parent / "AppHost.cs", """
using System.Text.Json;
using Aspire.Hosting.ApplicationModel;

var builder = DistributedApplication.CreateBuilder(args);
File.AppendAllText(
    Path.Combine(builder.AppHostDirectory, "..", "apphost-instances.jsonl"),
    JsonSerializer.Serialize(new
    {
        instance = Guid.NewGuid().ToString("N"),
        pid = Environment.ProcessId,
        backchannel = Environment.GetEnvironmentVariable("ASPIRE_BACKCHANNEL_PATH"),
        owner = Environment.GetEnvironmentVariable("ASPIRE_DOTNET_WATCH_OWNER"),
        session = Environment.GetEnvironmentVariable("ASPIRE_DOTNET_WATCH_HOST_SESSION"),
        control = Environment.GetEnvironmentVariable("ASPIRE_DOTNET_WATCH_HOST_CONTROL"),
        arguments = args
    }) + "\\n");
if (WatchModel.ShouldFail())
{
    throw new InvalidOperationException("WATCH_TEST_MODEL_FAILURE");
}
WatchModel.Apply(builder);
builder.Build().Run();

public class WatchModel
{
    public static string Value() => "MODEL_VALUE";
    public static bool ShouldFail() => false;
    public static void Apply(IDistributedApplicationBuilder builder)
    {
REGISTRATIONS
    }
}

public class ReplacementModelBase { }
""".replace("MODEL_VALUE", value).replace("REGISTRATIONS", registrations))

    def registration(self, name, path, extra="", options=""):
        relative = pathlib.Path(os.path.relpath(self.root / path, self.apphost.parent)).as_posix()
        configured = f", options => {{ {options} }}" if options else ""
        return f"""
        builder.AddDotnetProject("{name}", "{relative}"{configured})
            .WithHttpEndpoint(env: "ASPNETCORE_HTTP_PORTS")
            .WithHttpHealthCheck("/health")
            .WithOtlpExporter()
            .WithEnvironment("{RUN_TAG}", "{self.tag}")
            .WithEnvironment("WATCH_MODEL_VALUE", WatchModel.Value()){extra};
"""

    def describe(self, name=None, hidden=False, retry=False):
        arguments = ["describe"]
        if name:
            arguments.append(name)
        arguments += ["--format", "json", "--include-disabled-commands"]
        if hidden:
            arguments.append("--include-hidden")
        result = self.cli(*arguments, success=not retry, timeout=30)
        if result.returncode:
            raise ConnectionError(f"describe unavailable during replacement: {result.stdout}\n{result.stderr}")
        try:
            # Actual CLI JSON includes disabled commands on request:
            # {"resources":[{"name":"api-abc","properties":{"dotnet-watch.scope":"application"},
            #                "commands":{"rebuild":{"state":"Disabled"}},"urls":[...]}]}.
            return json.loads(result.stdout)["resources"]
        except json.JSONDecodeError as error:
            if retry:
                # With no active backchannel, informational stdout can accompany exit 0.
                raise ConnectionError(f"No current JSON snapshot: {result.stdout}\n{result.stderr}") from error
            raise

    def resource(self, name="api", retry=False):
        resources = self.describe(name, retry=retry)
        if retry and not resources:
            raise ConnectionError(f"The replacement model has not published '{name}' yet.")
        require(len(resources) == 1, f"Expected one '{name}' resource, got {resources}.")
        return resources[0]

    def endpoint(self, name="api", scheme="http", retry=False):
        resource = self.resource(name, retry)
        if self.config["features"].get("appHostServerV2"):
            # Native v2 publishes its sole allocated URL as "default".
            # Resolve by scheme, but never silently select an ambiguous address.
            urls = [
                item["url"] for item in resource.get("urls", [])
                if item["url"].startswith(scheme + "://")
            ]
            require(len(urls) <= 1, f"The native resource '{name}' has ambiguous {scheme} URLs.")
            return urls[0] if urls else None
        return next(
            (item["url"] for item in resource.get("urls", [])
             if item.get("name") == scheme and item["url"].startswith(scheme + "://")),
            None,
        )

    def response(self, name="api", predicate=lambda value: True, scheme="http", context=None, timeout=150):
        def ready():
            require(self.foreground is None or self.foreground.poll() is None,
                    f"The foreground CLI exited before {name}'s HTTP evidence; see {self.root / 'watch-owner.log'}.")
            url = self.endpoint(name, scheme=scheme, retry=True)
            if not url:
                return None
            value = request_json(url, context)
            if predicate(value):
                self.track_application(value)
                return value
            return None
        return wait_for(ready, f"waiting for {name}'s {scheme.upper()} evidence", timeout)

    def track(self, pid):
        identity = ProcessIdentity.read(pid)
        require(identity is not None, f"Evidence PID {pid} exited before its identity could be captured.")
        require(identity.environment().get(RUN_TAG) == self.tag, f"Evidence PID {pid} is not fixture-owned.")
        self.tracked.add(identity)
        self.track_run_directories(identity)
        return identity

    def track_run_directories(self, identity):
        # Owned watch-process arguments contain resource/control sockets such as:
        #   --server /tmp/aspw-<generation>/res0.sock
        # Remember only paths observed on this fixture's tagged process, never a global /tmp scan.
        observed = set()
        for argument in identity.arguments:
            for match in re.finditer(r"(/[^\s]*?/aspw-[a-zA-Z0-9]+)(?:/|$)", argument):
                observed.add(pathlib.Path(match.group(1)))
        self.run_directories |= observed
        return observed

    def kill_watch_host(self, owner):
        require(any(pathlib.Path(argument).name == WATCH_HOST for argument in owner.arguments)
                and "server" in owner.arguments, "Only an observed WatchHost server may use hard-kill preservation.")
        environment = owner.environment()
        require(environment.get(RUN_TAG) == self.tag, "The killed WatchHost is not fixture-owned.")
        marker = pathlib.Path(environment[CLEANUP_MARKER])
        observed = self.track_run_directories(owner)
        require(marker.is_absolute() and marker.parent in observed,
                "The cleanup marker is not beneath the killed owner's observed private run directory.")
        require(not marker.exists(), "The observed WatchHost had already acknowledged cleanup before the hard kill.")
        # SIGKILL prevents the host from emitting its post-containment ACK. Bind this
        # permitted preservation to its exact identity and paths, not every failed group.
        for directory in observed:
            self.expected_preservations[directory] = {"owner": owner.evidence(), "cleanupMarker": str(marker)}
        self.tracked.add(owner)
        owner.signal(signal.SIGKILL, self.tag)

    def track_application(self, value):
        require(value.get("pid", 0) > 0, f"No real application PID in HTTP response: {value}")
        require(re.fullmatch("[a-f0-9]{32}", value.get("instance", "")), f"No instance GUID in response: {value}")
        require(value.get("privateEnvironment") == {}, f"Private watch state leaked into the service: {value}")
        return self.track(value["pid"])

    def servers(self):
        servers = []
        for resource in self.describe(hidden=True, retry=True):
            if resource.get("displayName", resource["name"]).startswith("dotnet-watch"):
                pid = resource.get("properties", {}).get("executable.pid")
                if isinstance(pid, int) and pid > 0:
                    identity = ProcessIdentity.read(pid)
                    if identity is None:
                        continue
                    require(identity.environment().get(RUN_TAG) == self.tag,
                            f"Watch owner identity {identity.pid}/{identity.started} is not fixture-owned.")
                    self.tracked.add(identity)
                    require(WATCH_HOST in " ".join(identity.arguments), f"Not the bundled WatchHost server: {identity}")
                    servers.append((resource, identity))
        return servers

    def server_for(self, project):
        project = str((self.root / project).resolve())
        matching = [item for item in self.servers() if project in item[1].arguments]
        if not matching:
            raise ConnectionError(f"The watch server for {project} is replacing or has stopped.")
        require(len(matching) == 1, f"No unambiguous server for {project}: {matching}")
        return matching[0]

    def application_snapshot(self, name="api", scope="application"):
        def ready():
            resource = self.resource(name, retry=True)
            properties = resource.get("properties", {})
            require(properties.get("dotnet-watch.enabled") is True, f"{name} is not effectively watched: {resource}")
            require(properties.get("dotnet-watch.scope") == scope, f"Wrong attribution scope: {resource}")
            require(properties.get("dotnet-watch.coverage"), f"Missing coverage limitations: {resource}")
            require("executable.pid" not in properties, f"Proxy PID was published as an application PID: {resource}")
            require(properties.get("dotnet-watch.instance") == resource["name"], f"Wrong transport instance identity: {resource}")
            require(resource.get("startTimestamp") is None and resource.get("stopTimestamp") is None,
                    f"Proxy times were published as application observations: {resource}")
            pid = properties.get("dotnet-watch.transport.pid")
            if not isinstance(pid, int) or pid <= 0:
                return None
            identity = ProcessIdentity.read(pid)
            if identity is None:
                return None
            require(identity.environment().get(RUN_TAG) == self.tag,
                    f"Transport identity {identity.pid}/{identity.started} is not fixture-owned.")
            self.tracked.add(identity)
            return resource
        # Guarded refresh can retire the transport after the CLI publishes its
        # snapshot. Require a live, tagged identity in the same readiness check.
        return wait_for(ready, f"waiting for {name}'s owned current transport snapshot", timeout=45)

    def command_state(self, name, command, expected):
        snapshot = self.resource(name)
        require(snapshot.get("commands", {}).get(command, {}).get("state") == expected,
                f"{name}/{command} is not {expected}: {snapshot}")
        return snapshot

    def rejected(self, name, command):
        self.command_state(name, command, "Disabled")
        result = self.cli("resource", name, command, "--non-interactive", success=False, timeout=30)
        require(result.returncode != 0, f"{name}/{command} unexpectedly succeeded: {result.stdout}")
        return result

    def owned_processes(self):
        processes = set()
        for entry in pathlib.Path("/proc").iterdir():
            if not entry.name.isdecimal():
                continue
            identity = ProcessIdentity.read(int(entry.name))
            if identity is not None:
                try:
                    if identity.environment().get(RUN_TAG) == self.tag:
                        processes.add(identity)
                except (FileNotFoundError, ProcessLookupError, PermissionError, AssertionError):
                    pass
        return processes

    def capture_generation(self):
        processes = self.owned_processes()
        self.tracked |= processes
        for process in processes:
            self.track_run_directories(process)
        # The CLI owns the generation loop and survives complete AppHost replacement.
        # Its identity is checked on cancellation/final Stop, not generation teardown.
        return {process for process in processes if process.pid != self.owner_pid}

    def host_info(self):
        result = self.cli("ps", "--format", "json", scoped=False, timeout=30)
        hosts = [
            host for host in json.loads(result.stdout)
            if pathlib.Path(host["appHostPath"]).resolve() == self.apphost.resolve()
            and host.get("status", "running") == "running"
        ]
        require(len(hosts) == 1, f"Expected one active AppHost for {self.apphost}, got {hosts}.")
        self.track(hosts[0]["appHostPid"])
        if hosts[0].get("cliPid"):
            self.track(hosts[0]["cliPid"])
            self.owner_pid = hosts[0]["cliPid"]
        return hosts[0]

    def host_instance(self):
        host = self.host_info()
        # The fixture writes one JSONL record for each real AppHost process startup.
        # A stale record alone is not readiness: match it to the live backchannel's PID.
        instances = [
            json.loads(line) for line in (self.root / "apphost-instances.jsonl").read_text().splitlines()
        ]
        records = [instance for instance in instances if instance["pid"] == host["appHostPid"]]
        require(records, f"No actual host instance matched the live backchannel: {host}")
        return records[-1]

    def start(self, watch=None, detached=True, application_args=(), success=True, interactive=False, no_build=False, profile_path=None):
        require(not interactive or not detached, "A real foreground terminal cannot use --detach.")
        arguments = ["aspire", "run", "--apphost", str(self.apphost)]
        if not interactive:
            arguments.append("--non-interactive")
        if no_build:
            arguments.append("--no-build")
        if profile_path is not None:
            require(not detached, "Startup profiling requires the foreground owner.")
            arguments += ["--capture-profile", "--capture-profile-output", str(profile_path),
                          "--capture-profile-delay", "60"]
        if watch is not None:
            arguments += ["--watch", str(watch).lower()]
        if detached:
            arguments.append("--detach")
        else:
            arguments += ["--log-file", str(self.root / "watch-owner.log")]
        if application_args:
            arguments += ["--", *application_args]
        if detached:
            return self.command(arguments, success=success)
        if interactive:
            master, slave = pty.openpty()
            fcntl.ioctl(slave, termios.TIOCSWINSZ, bytes([40, 0, 160, 0, 0, 0, 0, 0]))
            try:
                # Popen's new session alone does not acquire a controlling terminal. Use a fresh
                # single-threaded child, not preexec_fn in this multithreaded verifier, to acquire
                # the PTY before exec. Ctrl+C bytes then reach the actual foreground CLI group.
                bootstrap = (
                    "import fcntl,os,sys,termios;"
                    "fcntl.ioctl(0,termios.TIOCSCTTY,0);"
                    "os.execvpe(sys.argv[1],sys.argv[1:],os.environ)"
                )
                self.foreground = subprocess.Popen(
                    [sys.executable, "-c", bootstrap, *arguments],
                    cwd=self.root, env=self.environment, stdin=slave, stdout=slave, stderr=slave,
                    start_new_session=True,
                )
            except BaseException:
                os.close(master)
                raise
            finally:
                os.close(slave)
            self.terminal_master = master
            self.terminal_error = None
            self.terminal_reader = threading.Thread(target=self.read_terminal, daemon=True)
            self.terminal_reader.start()
        else:
            handle = (self.root / "watch-run.log").open("w", encoding="utf-8")
            self.log_handles.append(handle)
            self.foreground = subprocess.Popen(
                arguments, cwd=self.root, env=self.environment, stdin=subprocess.DEVNULL,
                stdout=handle, stderr=subprocess.STDOUT, start_new_session=True,
            )
        self.owner_pid = self.foreground.pid
        def foreground_ready():
            require(self.foreground.poll() is None,
                    f"The foreground CLI exited before its ownership could be captured; see {self.root / 'watch-run.log'}.")
            identity = ProcessIdentity.read(self.foreground.pid)
            if identity is None or identity.environment().get(RUN_TAG) != self.tag:
                return None
            self.tracked.add(identity)
            return identity
        # Linux can briefly expose empty cmdline/environ files while the new
        # child execs. Require the still-live Popen child and its fixture tag
        # before accepting its identity, rather than weakening ownership checks.
        wait_for(foreground_ready, "capturing the foreground CLI's owned process identity", timeout=10)
        if interactive:
            wait_for(lambda: os.tcgetpgrp(master) == self.foreground.pid,
                     "checking the actual CLI owns its controlling terminal", timeout=10)
            require(all(os.readlink(f"/proc/{self.foreground.pid}/fd/{descriptor}").startswith("/dev/pts/")
                        for descriptor in (0, 1, 2)),
                    "The foreground CLI's standard streams were not attached to a real PTY.")
        return self.foreground

    def read_terminal(self):
        try:
            with (self.root / "watch-run.log").open("wb") as output:
                while True:
                    try:
                        chunk = os.read(self.terminal_master, 16384)
                    except OSError as error:
                        if error.errno == errno.EIO:
                            break  # Linux reports PTY EOF as EIO after every slave closes.
                        raise
                    if not chunk:
                        break
                    output.write(chunk)
                    output.flush()
        except OSError as error:
            self.terminal_error = error

    def terminal_text(self):
        require(self.terminal_error is None, f"Terminal capture failed: {self.terminal_error}")
        # Strip terminal CSI/OSC controls only for assertions; the original byte transcript is retained.
        # Examples: ESC[31m (color), ESC[2K (line clear), ESC]8;;<url>ESC\\ (hyperlink).
        raw = (self.root / "watch-run.log").read_text(encoding="utf-8", errors="replace")
        return re.sub(r"\x1b(?:\[[0-?]*[ -/]*[@-~]|\][^\x07]*?(?:\x07|\x1b\\))", "", raw).replace("\r", "\n")

    def finish_terminal(self):
        if self.terminal_reader is not None:
            self.terminal_reader.join(timeout=10)
            require(not self.terminal_reader.is_alive(), "The owned PTY reader did not observe process EOF.")
            require(self.terminal_error is None, f"Terminal capture failed: {self.terminal_error}")
            os.close(self.terminal_master)
            self.terminal_master = None
            self.terminal_reader = None

    def ctrl_c(self):
        require(self.foreground is not None and self.terminal_master is not None, "No foreground PTY run exists.")
        self.capture_generation()
        os.write(self.terminal_master, b"\x03")
        result = self.foreground.wait(timeout=45)
        self.foreground = None
        self.finish_terminal()
        require(result == 0, f"Foreground Ctrl+C returned {result}.")
        self.wait_cleanup(self.tracked)
        self.assert_run_directories_clean()

    def assert_run_directories_clean(self):
        leaked = [
            str(directory) for directory in self.run_directories
            if directory.exists() and directory not in self.expected_preservations
        ]
        require(not leaked, f"Owned watch run directories survived teardown: {leaked}")
        preserved = []
        for directory, evidence in self.expected_preservations.items():
            if directory.exists():
                require(not self.owned_processes(), "Intentional directory preservation cannot hide an owned live process.")
                marker = pathlib.Path(evidence["cleanupMarker"])
                require(not marker.exists() or marker.read_text() != "contained-cleanup-complete",
                        "An acknowledged run directory survived cleanup.")
                preserved.append({"directory": str(directory), "cleanupAcknowledged": False, **evidence})
        return preserved

    def stop(self):
        self.capture_generation()
        result = self.cli("stop", "--non-interactive", success=False, timeout=45)
        require(result.returncode == 0, f"Watch Stop failed: {result.stdout}\n{result.stderr}")
        if self.foreground is not None:
            self.foreground.wait(timeout=45)
            self.foreground = None
        self.finish_terminal()
        self.wait_cleanup(self.tracked)
        self.assert_run_directories_clean()

    def wait_cleanup(self, processes, timeout=45):
        wait_for(lambda: all(not process.alive() for process in processes),
                 "waiting for the captured real process identities to terminate", timeout)

    def diagnostics(self):
        directory = self.context.root / ".aspire-diagnostics" / "watch" / self.root.name
        directory.mkdir(parents=True, exist_ok=True)
        for label, arguments in [
            ("resources", ["describe", "--include-hidden", "--include-disabled-commands", "--format", "json"]),
            ("logs", ["logs", "--include-hidden", "--tail", "200", "--format", "json"]),
        ]:
            try:
                result = self.cli(*arguments, success=False, timeout=15)
                write(directory / f"{label}.log", f"Exit: {result.returncode}\n{result.stdout}\n{result.stderr}")
            except (OSError, subprocess.TimeoutExpired) as error:
                write(directory / f"{label}.log", str(error))
        self.preserve_artifacts()
        write(directory / "owned-processes.json", json.dumps(
            [process.evidence() for process in self.owned_processes()], indent=2))

    def preserve_artifacts(self):
        # Successful workspaces are deleted after TerminalRun copies this diagnostics tree.
        directory = self.context.evidence.path.parent / self.root.name
        directory.mkdir(parents=True, exist_ok=True)
        for name in ("watch-run.log", "watch-owner.log", "startup-profile.zip"):
            if (self.root / name).is_file():
                shutil.copyfile(self.root / name, directory / name)

    def cleanup_after_failure(self):
        try:
            self.cli("stop", "--non-interactive", success=False, timeout=30)
        except (OSError, subprocess.TimeoutExpired) as error:
            print(f"Fixture cleanup command failed: {error}", file=sys.stderr)
        owned = self.owned_processes()
        for process in owned:
            process.signal(signal.SIGTERM, self.tag)
        try:
            self.wait_cleanup(owned, timeout=10)
        except AssertionError:
            for process in owned:
                process.signal(signal.SIGKILL, self.tag)
            self.wait_cleanup(owned, timeout=10)
        if self.foreground is not None:
            self.foreground.wait(timeout=10)
            self.foreground = None

    def close(self):
        self.finish_terminal()
        for handle in self.log_handles:
            handle.close()


class Context:
    def __init__(self, root, scenario, packages=None, feeds=None):
        self.root = root
        self.evidence = Evidence(root, scenario)
        self.hive = PackageHive(packages)
        self.sdks = installed_sdks(os.environ)
        self.feeds = feeds
        self.fixtures = []
        self.evidence.check("fresh-package-hive-and-installed-sdk",
                            version=self.hive.version, packages=str(self.hive.directory),
                            sdk=SDK_VERSION, sdkRoot=str(self.sdks[SDK_VERSION]))


# Scenario functions below deliberately share the CLI, SDK, package, and process
# infrastructure; each has its own model/workspace and positive readiness evidence.


def verify_same_application(before, after, description):
    require((before["pid"], before["instance"]) == (after["pid"], after["instance"]),
            f"{description} replaced a real application: {before} -> {after}")


def verify_new_application(before, after, description):
    require(before["pid"] != after["pid"] and before["instance"] != after["instance"],
            f"{description} did not replace the application instance: {before} -> {after}")


def hot_reload(fixture, path, before, after, resources, *, expected, field="value"):
    initial = {name: fixture.response(name) for name in resources}
    replace_once(path, before, after)
    with concurrent.futures.ThreadPoolExecutor(max_workers=len(resources)) as executor:
        futures = {
            name: executor.submit(fixture.response, name, lambda value: value[field] == expected)
            for name in resources
        }
        updated = {name: future.result() for name, future in futures.items()}
    for name in resources:
        verify_same_application(initial[name], updated[name], f"{name}'s metadata update")
    return updated


def verify_generation(fixture, old, previous_host, expected, names=("api",)):
    current = fixture.response("api", lambda value: value["configured"] == expected, timeout=240)
    host = fixture.host_instance()
    require(host["instance"] != previous_host["instance"] and host["pid"] != previous_host["pid"],
            f"Model reconstruction reused an AppHost process: {previous_host} -> {host}")
    require(host["backchannel"] and host["backchannel"] != previous_host["backchannel"],
            f"Model reconstruction reused the old backchannel: {previous_host} -> {host}")
    require(host["owner"] == "cli" and re.fullmatch("[a-f0-9]{32}", host.get("session", "")),
            f"The new AppHost is not privately CLI-owned: {host}")
    for name in names:
        fixture.response(name)
        fixture.application_snapshot(name)
    fixture.wait_cleanup(old)
    require(not pathlib.Path(previous_host["backchannel"]).exists(),
            f"The old generation's backchannel remains: {previous_host['backchannel']}")
    fixture.capture_generation()
    return current, host


def verify_csharp(context):
    fixture = Fixture(context, "csharp-generations")
    fixture.shared()
    fixture.service("Api", shared=True)
    fixture.service("Worker", shared=True)
    fixture.service("Parked")
    api_registration = fixture.registration("api", "Api/Api.csproj")
    worker_registration = fixture.registration("worker", "Worker/Worker.csproj")
    parked_registration = fixture.registration("parked", "Parked/Parked.csproj", ".WithExplicitStart()")
    fixture.csharp_model(api_registration + worker_registration + parked_registration)
    fixture.start(watch=True, detached=False, interactive=True)
    first = fixture.response()
    worker = fixture.response("worker")
    fixture.application_snapshot()
    previous_host = fixture.host_instance()
    require(previous_host["owner"] == "cli" and previous_host["control"] and previous_host["session"],
            f"AppHost watch did not establish private generation ownership: {previous_host}")
    require(fixture.resource("parked")["state"] in ("NotStarted", "Waiting"),
            "ExplicitStart was ignored in the first model.")
    old = fixture.capture_generation()

    apphost_source = fixture.apphost.parent / "AppHost.cs"
    replace_once(apphost_source, 'Value() => "one"', 'Value() => "two"')
    second, previous_host = verify_generation(fixture, old, previous_host, "two", ("api", "worker"))
    verify_new_application(first, second, "AppHost method-body environment change")
    require(fixture.resource("parked")["state"] in ("NotStarted", "Waiting"),
            "A reconstructed model ignored fresh ExplicitStart.")
    context.evidence.check("csharp-method-body-model-and-fresh-backchannel", apphost=previous_host, response=second)
    assert_terminal_boundary(fixture)

    fixture.service("Late", shared=True)
    late_registration = fixture.registration("late", "Late/Late.csproj")
    old = fixture.capture_generation()
    replace_once(apphost_source, api_registration, api_registration + late_registration)
    replace_once(apphost_source, 'Value() => "two"', 'Value() => "three"')
    third, previous_host = verify_generation(fixture, old, previous_host, "three", ("api", "worker", "late"))
    late = fixture.response("late")
    require(late["shared"] == "shared-before", f"The added physical root was not built: {late}")
    context.evidence.check("csharp-added-root-and-old-generation-cleanup", response=third, added=late)
    assert_terminal_boundary(fixture)

    old = fixture.capture_generation()
    replace_once(apphost_source, late_registration, "")
    replace_once(apphost_source, worker_registration, "")
    replace_once(apphost_source, 'Value() => "three"', 'Value() => "four"')
    fourth, previous_host = verify_generation(fixture, old, previous_host, "four")
    active = fixture.describe()
    require(all(resource.get("displayName", resource["name"]) not in ("worker", "late") for resource in active),
            f"Removed roots survived complete model reconstruction: {active}")
    context.evidence.check("csharp-removed-roots-and-process-cleanup", response=fourth)
    assert_terminal_boundary(fixture)

    old = fixture.capture_generation()
    replace_once(apphost_source, "public class WatchModel\n", "public class WatchModel : ReplacementModelBase\n")
    replace_once(apphost_source, 'Value() => "four"', 'Value() => "five"')
    fifth, previous_host = verify_generation(fixture, old, previous_host, "five")
    verify_new_application(fourth, fifth, "AppHost rude-edit replacement")
    context.evidence.check("csharp-rude-edit-real-host-instance", apphost=previous_host, response=fifth)
    assert_terminal_boundary(fixture)

    old = fixture.capture_generation()
    replace_once(apphost_source, 'Value() => "five";', "Value() => ;")
    # The compiler emits e.g. "AppHost.cs(28,37): error CS1525: Invalid expression term ';'".
    # This is failure diagnosis, not log-based readiness or an application exit observation.
    # AppHost subprocess output is retained in its owning CLI log, not the readiness console.
    wait_for(lambda: any("CS1525" in line and str(apphost_source) in line
                         for line in (fixture.root / "watch-owner.log").read_text().splitlines()),
             "waiting for the invalid AppHost edit's compiler diagnostic")
    invalid_response = fixture.response()
    verify_same_application(fifth, invalid_response, "An invalid AppHost source edit")
    require(fixture.host_instance()["instance"] == previous_host["instance"],
            "A source build failure invalidated the last successful AppHost model.")
    assert_terminal_boundary(fixture)
    replace_once(apphost_source, "Value() => ;", 'Value() => "recovered";')
    recovered, previous_host = verify_generation(fixture, old, previous_host, "recovered")
    context.evidence.check("csharp-invalid-edit-recovery", response=recovered)
    assert_terminal_boundary(fixture)

    fixture.capture_generation()
    owner = fixture.track(fixture.foreground.pid)
    replace_once(apphost_source, 'Value() => "recovered"', 'Value() => "canceled-edit"')
    fixture.ctrl_c()
    replace_once(apphost_source, 'Value() => "canceled-edit"', 'Value() => "must-not-revive"')
    stays_true(lambda: not fixture.owned_processes(), "checking cancellation cannot rearm a departed generation")
    context.evidence.check("csharp-cancellation-without-revival", owner=owner.evidence(),
                          **assert_terminal_boundary(fixture))

    verify_csharp_startup_failure(context)


def verify_csharp_startup_failure(context):
    failure = Fixture(context, "csharp-model-startup-failure")
    failure.service()
    failure.csharp_model(failure.registration("api", "Api/Api.csproj"))
    source = failure.apphost.parent / "AppHost.cs"
    replace_once(source, "ShouldFail() => false", "ShouldFail() => true")
    startup_timeout = failure.environment["ASPIRE_CLI_START_TIMEOUT"]
    # The CLI's startup budget begins after preparation/build. Make this
    # deliberately failing model exhaust a short budget inside the separate
    # whole-process watchdog, rather than race two equal 240-second deadlines.
    failure.environment["ASPIRE_CLI_START_TIMEOUT"] = "15"
    run = failure.start(watch=True, detached=False)
    result = run.wait(timeout=120)
    require(result != 0, f"A failing initial AppHost model returned success: {result}")
    failure.foreground = None
    require("WATCH_TEST_MODEL_FAILURE" in (failure.root / "watch-owner.log").read_text(),
            "The initial model failure was not diagnosed.")
    failure.wait_cleanup(failure.capture_generation())
    replace_once(source, "ShouldFail() => true", "ShouldFail() => false")
    failure.environment["ASPIRE_CLI_START_TIMEOUT"] = startup_timeout
    failure.start(watch=True)
    healthy = failure.response()
    failure.application_snapshot()
    failure.stop()
    context.evidence.check("csharp-startup-model-failure-full-run-recovery", response=healthy)


def install_startup_probe(fixture):
    directory = fixture.root / "Probe"
    write(directory / "Probe.csproj", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
""")
    write(directory / "StartupHook.cs", """
using System.Text.Json;

public static class StartupHook
{
    public static void Initialize()
    {
        var path = Environment.GetEnvironmentVariable("WATCH_PROBE_DIRECTORY");
        if (string.IsNullOrEmpty(path))
        {
            return;
        }
        // Separate records prevent overlapping SDK/server startups from competing
        // for one append handle. The observation hook must not change startup.
        var record = Path.Combine(path, $"{Environment.ProcessId}-{Guid.NewGuid():N}.json");
        File.WriteAllText(record + ".pending", JsonSerializer.Serialize(new
        {
            pid = Environment.ProcessId,
            entry = Environment.GetCommandLineArgs()[0],
            assembly = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name,
            owner = Environment.GetEnvironmentVariable("ASPIRE_DOTNET_WATCH_OWNER"),
            control = Environment.GetEnvironmentVariable("ASPIRE_DOTNET_WATCH_HOST_CONTROL"),
            session = Environment.GetEnvironmentVariable("ASPIRE_DOTNET_WATCH_HOST_SESSION"),
            enabled = Environment.GetEnvironmentVariable("AppHost__Run__WatchEnabled"),
            suppression = Environment.GetEnvironmentVariable("ASPIRE_RESOURCE_WATCH_SUPPRESS"),
            backchannel = Environment.GetEnvironmentVariable("ASPIRE_BACKCHANNEL_PATH")
        }));
        File.Move(record + ".pending", record);
    }
}
""")
    fixture.dotnet_command("build", directory / "Probe.csproj", "--nologo", "-v:quiet")
    hook = directory / "bin" / "Debug" / "net10.0" / "Probe.dll"
    require(hook.is_file(), "The fixture's managed startup observation hook was not built.")
    fixture.environment["DOTNET_STARTUP_HOOKS"] = str(hook)
    directory = fixture.root / "managed-startups"
    directory.mkdir()
    fixture.environment["WATCH_PROBE_DIRECTORY"] = str(directory)


def managed_probe_records(fixture):
    directory = fixture.root / "managed-startups"
    if not directory.is_dir():
        return []
    return [
        record for path in directory.glob("*.json")
        if (record := read_json(path))["entry"]
        and (record.get("assembly") in ("aspire-managed", "Aspire.Hosting.RemoteHost", "Aspire.Hosting.AppHostServer")
             or "aspire-managed" in pathlib.Path(record["entry"]).name)
    ]


def initialize_typescript(fixture, native=False):
    fixture.hive.package("Aspire.Hosting.CodeGeneration.TypeScript")
    # An emulated build identity need not be a registered package channel.
    # Let init persist only a channel it actually resolves from this install.
    fixture.config.pop("channel", None)
    # Init includes the language's managed JavaScript integration. Bootstrap
    # that scaffold with the managed server, then select the native backend
    # with only its supported core contracts before any actual native run.
    fixture.config["features"]["appHostServerV2"] = False
    fixture.save_config()
    fixture.command([
        "aspire", "init", "--language", "typescript", "--non-interactive", "--suppress-agent-init"
    ])
    fixture.config = read_json(fixture.root / "aspire.config.json")
    fixture.config["features"] = {
        **fixture.config.get("features", {}),
        "appHostServerV2": native,
        "defaultWatchEnabled": False,
    }
    fixture.config.pop("channel", None)
    fixture.config["sdk"] = {"version": fixture.hive.version}
    if native:
        fixture.config["packages"] = {
            name: value for name, value in fixture.config.get("packages", {}).items()
            if name in ("Aspire.Hosting", "Aspire.Hosting.CodeGeneration.TypeScript")
        }
    fixture.save_config()
    if not native:
        fixture.cli("add", "Aspire.Hosting.Dotnet", "--non-interactive")
        fixture.config = read_json(fixture.root / "aspire.config.json")
    external_tarballs = {}
    for lock in fixture.root.glob("package-lock.json"):
        payload = read_json(lock)
        for location, package in payload.get("packages", {}).items():
            resolved = package.get("resolved")
            if not resolved or resolved.startswith((fixture.npm_registry, "file:")):
                continue
            name = package.get("name") or location.rsplit("node_modules/", 1)[-1]
            specifier = f"{name}@{package['version']}"
            external_tarballs.setdefault(specifier, set()).add(resolved)

    def confirm_registry_tarball(entry):
        specifier, resolutions = entry
        # A proxy can return dist.tarball on a different mirror host. Confirm
        # each exact locked version through the configured registry's metadata,
        # not a hard-coded host allowlist or locks shipped inside node_modules.
        # https://github.com/npm/registry/blob/main/docs/REGISTRY-API.md
        result = fixture.command([
            "npm", "view", specifier, "dist.tarball", "--json", "--prefer-offline",
            "--registry", fixture.npm_registry,
        ], timeout=30)
        tarball = json.loads(result.stdout)
        require(isinstance(tarball, str) and resolutions == {tarball},
                f"The configured npm registry did not confirm the locked tarball for {specifier}.")

    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
        list(pool.map(confirm_registry_tarball, external_tarballs.items()))


def typescript_model(fixture, registrations, value="one"):
    write(fixture.apphost, f"""
import {{ createBuilder }} from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();
{registrations}
await builder.build().run();
""".replace("'MODEL_VALUE'", f"'{value}'"))


def typescript_registration(fixture, name, project, extra=""):
    return f"""
await builder.addDotnetProject('{name}', './{project}')
    .withHttpEndpoint({{ env: 'ASPNETCORE_HTTP_PORTS' }})
    .withHttpHealthCheck({{ path: '/health' }})
    .withEnvironment('{RUN_TAG}', '{fixture.tag}')
    .withEnvironment('WATCH_MODEL_VALUE', 'MODEL_VALUE'){extra};
"""


def initialize_managed_typescript(context):
    fixture = Fixture(context, "typescript-combined", language="typescript")
    install_startup_probe(fixture)
    # Deliberately inherit private state before discovery. Short-lived managed
    # servers must override it rather than accidentally joining that old session.
    fixture.environment.update({
        "ASPIRE_DOTNET_WATCH_OWNER": "cli",
        "ASPIRE_DOTNET_WATCH_HOST_CONTROL": str(fixture.root / "nonexistent-old-session.sock"),
        "ASPIRE_DOTNET_WATCH_HOST_SESSION": uuid.uuid4().hex,
        WATCH_ENABLED: "true",
    })
    initialize_typescript(fixture)
    probes = managed_probe_records(fixture)
    require(probes, "No actual managed bootstrap/discovery process was observed.")
    require(all(not record.get("owner") and not record.get("control") and not record.get("session")
                and record.get("suppression") == "discovery" for record in probes),
            f"Bootstrap/discovery inherited private watch state: {probes}")
    context.evidence.check("typescript-bootstrap-and-discovery-isolation", observations=probes)
    return fixture


def start_managed_typescript(context):
    fixture = initialize_managed_typescript(context)

    fixture.shared()
    fixture.service("Api", shared=True)
    fixture.service("Worker", shared=True)
    registrations = (
        typescript_registration(fixture, "api", "Api/Api.csproj")
        + typescript_registration(fixture, "worker", "Worker/Worker.csproj")
    )
    typescript_model(fixture, registrations)
    fixture.config["features"]["defaultWatchEnabled"] = True
    fixture.save_config()
    fixture.start(watch=None)
    initial = fixture.response()
    worker = fixture.response("worker")
    fixture.application_snapshot()
    fixture.application_snapshot("worker")
    servers = fixture.servers()
    require(len(servers) == 1, f"Shared physical references should have one service build owner: {servers}")
    managed = [record for record in managed_probe_records(fixture) if record.get("suppression") == ""]
    require(managed and all(record.get("owner") == "cli" and not record.get("control") and not record.get("session")
                            for record in managed),
            f"Watch was not projected only into the final managed server: {managed}")
    require(not any(WATCH_TOOL in " ".join(process.arguments) and "host" in process.arguments
                    for process in fixture.capture_generation()),
            "A TypeScript guest was wrapped in the .NET AppHost host watcher.")
    context.evidence.check("typescript-combined-packed-managed-first-start",
                          responses={"api": initial, "worker": worker})
    return fixture, initial, worker


def verify_typescript(context):
    fixture, initial, worker = start_managed_typescript(context)

    hot_reload(fixture, fixture.root / "Api" / "Program.cs", '=> "before"', '=> "after"', ["api"], expected="after")
    updated = hot_reload(
        fixture, fixture.root / "Shared" / "SharedValue.cs",
        '=> "shared-before"', '=> "shared-after"', ["api", "worker"], expected="shared-after", field="shared")
    verify_same_application(initial, updated["api"], "Combined default service/shared watch")
    verify_same_application(worker, updated["worker"], "Shared-library consumer watch")
    context.evidence.check("typescript-default-combined-service-and-shared-hot-reload", responses=updated)

    old = fixture.capture_generation()
    old_services = {
        process for process in old
        if process.pid in {updated["api"]["pid"], updated["worker"]["pid"]}
        or any(WATCH_HOST in argument for argument in process.arguments)
    }
    # Both registrations intentionally take the same model value; update the
    # guest-native source, not a C# AppHost or a generated SDK file.
    source = fixture.apphost.read_text()
    require(source.count("'WATCH_MODEL_VALUE', 'one'") == 2, "TypeScript edit anchors changed.")
    write(fixture.apphost, source.replace("'WATCH_MODEL_VALUE', 'one'", "'WATCH_MODEL_VALUE', 'two'"))
    native_updated = fixture.response("api", lambda value: value["configured"] == "two", timeout=240)
    fixture.response("worker", lambda value: value["configured"] == "two")
    verify_new_application(updated["api"], native_updated, "A real TypeScript guest-native model edit")
    fixture.wait_cleanup(old_services)
    fixture.application_snapshot()
    context.evidence.check("typescript-real-native-model-edit", response=native_updated)
    fixture.stop()

    fixture.start(watch=False)
    ordinary = fixture.response()
    ordinary_worker = fixture.response("worker")
    require(len(fixture.servers()) == 1, "--watch false disabled resource watching.")
    fixture.application_snapshot()
    replace_once(fixture.root / "Api" / "Program.cs", '=> "after"', '=> "ordinary-edit"')
    source = fixture.apphost.read_text()
    write(fixture.apphost, source.replace("'WATCH_MODEL_VALUE', 'two'", "'WATCH_MODEL_VALUE', 'disabled-edit'"))
    fixture.command_state("api", "rebuild", "Disabled")
    rebuilt = fixture.response("api", lambda value: value["value"] == "ordinary-edit")
    verify_same_application(ordinary, rebuilt, "Resource hot reload with AppHost watching explicitly disabled")
    stays_true(lambda: request_json(fixture.endpoint())["configured"] == ordinary["configured"],
               "checking --watch false disables AppHost edits only")
    verify_same_application(ordinary_worker, fixture.response("worker"), "Independent watched worker")
    context.evidence.check("typescript-apphost-false-preserves-default-resource-watch", response=rebuilt)
    fixture.stop()

    fixture.config["features"]["defaultWatchEnabled"] = False
    fixture.save_config()
    fixture.start(watch=None)
    fixture.response()
    require(len(fixture.servers()) == 1, "The AppHost-watch feature default disabled independent resource watching.")
    fixture.stop()
    fixture.start(watch=True)
    fixture.response()
    fixture.application_snapshot()
    fixture.stop()
    context.evidence.check("typescript-default-false-and-explicit-true")


def observe_instances(fixture, name, count, field=None, expected=None):
    seen = {}

    def collect():
        # A logical name shared by replicas is not a unique CLI resource
        # selector. Select their explicit display names from the full snapshot;
        # HTTP OTEL identities below still establish each actual instance.
        resources = [
            resource for resource in fixture.describe(retry=True)
            if resource["name"] == name or resource.get("displayName") == name
        ]
        addresses = {
            item["url"] for resource in resources for item in resource.get("urls", [])
            if item.get("name") == "http"
        }
        for address in addresses:
            for _ in range(max(8, count * 4)):
                value = request_json(address)
                fixture.track_application(value)
                if field is not None and value[field] != expected:
                    continue
                # Aspire injects "service.instance.id=<DCP suffix>" through its real
                # OTEL_RESOURCE_ATTRIBUTES environment. Match that explicit identity,
                # not an application PID inferred from a proxy or project event.
                attributes = value.get("otel") or ""
                match = re.search(r"(?:^|,)service\.instance\.id=([^,]+)(?:,|$)", attributes)
                require(match is not None, f"No explicit DCP instance identity in HTTP response: {value}")
                suffix = match.group(1)
                matches = [resource for resource in resources if resource["name"].endswith("-" + suffix)]
                require(len(matches) == 1, f"HTTP instance identity did not match its actual resource: {value}, {resources}")
                seen[matches[0]["name"]] = value
        return dict(seen) if len(seen) == count else None

    return wait_for(collect, f"observing {count} distinct HTTP instances of '{name}'")


def verify_group_snapshot(fixture, name):
    snapshot = fixture.application_snapshot(name, scope="project-group")
    require(snapshot.get("exitCode") is None, f"A project-only exit was attributed to replica {name}: {snapshot}")
    require("per-instance" in snapshot["properties"]["dotnet-watch.coverage"],
            f"Group attribution limitations were hidden: {snapshot}")
    return snapshot


def verify_replicas(context):
    fixture = Fixture(context, "replicas-and-duplicate-entrypoints")
    fixture.shared()
    fixture.service(shared=True)
    fixture.csharp_model(
        fixture.registration("api", "Api/Api.csproj", ".WithReplicas(2)")
        + fixture.registration("duplicate", "Api/Api.csproj")
    )
    fixture.start()
    fixture.host_info()
    replicas = observe_instances(fixture, "api", 2)
    duplicate = observe_instances(fixture, "duplicate", 1)
    instances = {**replicas, **duplicate}
    require(len({value["pid"] for value in instances.values()}) == 3
            and len({value["instance"] for value in instances.values()}) == 3,
            f"Replicas/duplicate registrations did not launch distinct real applications: {instances}")
    server_resource, server = fixture.server_for("Api/Api.csproj")
    require(server.arguments.count(str(fixture.root / "Api" / "Api.csproj")) == 1,
            f"The physical watch root was duplicated: {server.arguments}")
    for name in instances:
        verify_group_snapshot(fixture, name)
        fixture.rejected(name, "restart")
        fixture.rejected(name, "rebuild")
    context.evidence.check("replicas-and-duplicates-distinct-http-identities", responses=instances,
                          server=server.evidence())

    replace_once(fixture.root / "Api" / "Program.cs", '=> "before"', '=> "replicas-after"')
    reloaded = {
        **observe_instances(fixture, "api", 2, "value", "replicas-after"),
        **observe_instances(fixture, "duplicate", 1, "value", "replicas-after"),
    }
    for name in instances:
        verify_same_application(instances[name], reloaded[name], "Replica/duplicate metadata update")
    context.evidence.check("every-replica-and-duplicate-hot-reloads", responses=reloaded)

    stopped_name = sorted(replicas)[0]
    stopped = reloaded[stopped_name]
    stopped_process = fixture.track(stopped["pid"])
    fixture.command_state(stopped_name, "stop", "Enabled")
    fixture.cli("resource", stopped_name, "stop", "--non-interactive")
    fixture.wait_cleanup({stopped_process})
    wait_for(lambda: fixture.resource(stopped_name)["state"] == "Exited",
             "waiting for the manually stopped replica's transport to settle")
    fixture.rejected(stopped_name, "start")
    context.evidence.check("replica-manual-stop-and-rejected-start", stoppedResource=stopped_name,
                          stoppedInstance=stopped)

    project = fixture.root / "Api" / "Api.csproj"
    replace_once(project, "<Nullable>enable</Nullable>",
                 "<Nullable>enable</Nullable>\n    <WatchFixtureRefresh>project-save</WatchFixtureRefresh>")
    new_server = wait_for(
        lambda: (item if (item := fixture.server_for("Api/Api.csproj"))[1] != server else None),
        "waiting for project-save watcher replacement",
    )[1]
    fixture.wait_cleanup({server})
    active = {
        **observe_instances(fixture, "api", 1, "value", "replicas-after"),
        **observe_instances(fixture, "duplicate", 1, "value", "replicas-after"),
    }
    require(stopped_name not in active, f"An internal project refresh revived the stopped replica: {active}")
    require(fixture.resource(stopped_name)["state"] == "Exited", "Internal refresh lost the stopped instance state.")
    fixture.rejected(stopped_name, "start")
    context.evidence.check("stopped-replica-survives-project-refresh", responses=active,
                          replacementServer=new_server.evidence())

    replace_once(project, "</ItemGroup>", """
    <PackageReference Include="System.IO.Hashing" Version="10.0.0" />
  </ItemGroup>""")
    package_server = wait_for(
        lambda: (item if (item := fixture.server_for("Api/Api.csproj"))[1] != new_server else None),
        "waiting for package-save watcher replacement",
    )[1]
    fixture.wait_cleanup({new_server})
    active = {
        **observe_instances(fixture, "api", 1, "value", "replicas-after"),
        **observe_instances(fixture, "duplicate", 1, "value", "replicas-after"),
    }
    replace_once(fixture.root / "Api" / "Program.cs", 'Read() => "replicas-after"',
                 'Read() => typeof(System.IO.Hashing.XxHash3).Assembly.GetName().Name!')
    with_package = {
        **observe_instances(fixture, "api", 1, "value", "System.IO.Hashing"),
        **observe_instances(fixture, "duplicate", 1, "value", "System.IO.Hashing"),
    }
    require(stopped_name not in with_package, f"Package deployment revived the stopped instance: {with_package}")
    fixture.rejected(stopped_name, "start")
    stays_true(lambda: fixture.resource(stopped_name)["state"] == "Exited" and not stopped_process.alive(),
               "checking stopped intent remains fenced after package deployment")
    context.evidence.check("stopped-replica-survives-package-refresh", responses=with_package)

    crashed_name = next(name for name in with_package if name.startswith("api-"))
    crashed = fixture.track(with_package[crashed_name]["pid"])
    peer_name = next(name for name in with_package if name != crashed_name)
    peer = with_package[peer_name]
    crashed.signal(signal.SIGKILL, fixture.tag)
    fixture.wait_cleanup({crashed})
    wait_for(
        lambda: any("process_exited" in resource["properties"].get("dotnet-watch.last-event", "")
                    for resource in fixture.describe("duplicate", retry=True)),
        "waiting for a real path-only group crash observation",
    )
    verify_same_application(peer, fixture.response(peer_name), "An unrelated replica after a path-only crash")
    for name in instances:
        snapshot = fixture.resource(name)
        require(snapshot["properties"]["dotnet-watch.scope"] == "project-group"
                and snapshot.get("exitCode") is None,
                f"The group crash invented an instance-specific application exit: {snapshot}")
    require(fixture.resource(peer_name)["state"] not in ("Crashed", "FailedToStart"),
            "A project event marked a still-serving peer as crashed.")
    fixture.stop()
    context.evidence.check("honest-project-group-crash-and-instance-cleanup",
                          crashed=crashed.evidence(), servingPeer=peer, server=package_server.evidence())


def add_build_counter(fixture, project):
    # The watcher also runs CoreCompile in-process with SkipCompilerExecution=true
    # to collect design-time compiler arguments. Count physical build attempts,
    # not that evaluation, so the exact-one assertion measures build ownership.
    # https://github.com/dotnet/dotnet/blob/5a2162bea6845eabc1fc721624a43568c65db3b1/src/sdk/src/Dotnet.Watch/Watch/Build/EvaluationResult.cs
    replace_once(project, "</Project>", """
  <Target Name="WatchFixtureBuildAttempt" BeforeTargets="CoreCompile"
          Condition="'$(SkipCompilerExecution)' != 'true'">
    <WriteLinesToFile File="$(MSBuildProjectDirectory)/watch-build-attempts.log"
                      Lines="$([System.DateTime]::UtcNow.Ticks)" Overwrite="false" />
  </Target>
</Project>""")


def build_attempts(project):
    path = project.parent / "watch-build-attempts.log"
    return len(path.read_text().splitlines()) if path.is_file() else 0


def no_http_response(url):
    try:
        request_json(url)
        return False
    except (urllib.error.URLError, TimeoutError):
        return True


def add_ef_fixture(fixture):
    write(fixture.root / "Api" / "WatchDbContext.cs", """
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using System.Text.Json;

public sealed class WatchContext(DbContextOptions<WatchContext> options) : DbContext(options)
{
}

public sealed class WatchContextFactory : IDesignTimeDbContextFactory<WatchContext>
{
    public WatchContext CreateDbContext(string[] args)
    {
        var root = Environment.GetEnvironmentVariable("WATCH_FIXTURE_ROOT")!;
        File.WriteAllText(Path.Combine(root, "ef-built-assembly.json"),
            JsonSerializer.Serialize(new { value = ValueSource.Read(), pid = Environment.ProcessId }));
        return new WatchContext(new DbContextOptionsBuilder<WatchContext>()
            .UseSqlite("Data Source=" + Path.Combine(root, "watch.db")).Options);
    }
}

[DbContext(typeof(WatchContext))]
[Migration("202611060001_WatchInitial")]
public sealed class WatchInitial : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("watch_evidence",
            columns: table => new { value = table.Column<string>(type: "TEXT", nullable: false) },
            constraints: table => { });
        migrationBuilder.InsertData("watch_evidence", "value", columnType: "TEXT", value: ValueSource.Read());
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("watch_evidence");
    }
}
""")


def verify_builds(context):
    fixture = Fixture(context, "initial-build-commands-and-ef", extra_packages=("Aspire.Hosting.EntityFrameworkCore",))
    api_project = fixture.service(packages=(
        ("Microsoft.EntityFrameworkCore.Design", "10.0.0"),
        ("Microsoft.EntityFrameworkCore.Sqlite", "10.0.0"),
    ))
    add_build_counter(fixture, api_project)
    add_ef_fixture(fixture)
    fixture.service("Ordinary")
    worker_project = fixture.service("Independent/Worker")
    fixture.pin_sdk(fixture.root / "Independent")
    add_build_counter(fixture, worker_project)
    api = fixture.registration(
        "api", "Api/Api.csproj", f'.WithEnvironment("WATCH_FIXTURE_ROOT", {json.dumps(str(fixture.root))})'
    ).replace("builder.AddDotnetProject", "var api = builder.AddDotnetProject", 1)
    fixture.csharp_model(
        api + f"""
        var migrations = api.AddEFMigrations("migrations", "WatchContext",
            tool => tool.WithToolVersion("10.0.0")
                .WithToolSource({json.dumps(fixture.tool_source)})
                .WithToolIgnoreExistingFeeds())
            .RunDatabaseUpdateOnStart();
        api.WaitFor(migrations);
""" + fixture.registration("ordinary", "Ordinary/Ordinary.csproj", ".WithWatch(false)")
        + fixture.registration("worker", "Independent/Worker/Worker.csproj")
    )

    fixture.dotnet_command("build", api_project, "--nologo", "-v:quiet")
    require((api_project.parent / "bin" / "Debug" / "net10.0" / "Api.dll").is_file(),
            "No stale assembly was seeded for the initial-build regression.")
    (api_project.parent / "watch-build-attempts.log").unlink()
    program = fixture.root / "Api" / "Program.cs"
    replace_once(program, 'Read() => "before"', "Read() => ;")
    fixture.start(detached=False)
    wait_for(lambda: fixture.resource("api", retry=True).get("properties", {}).get("dotnet-watch.build") == "failed",
             "waiting for the watcher's failed initial build")
    require(build_attempts(api_project) == 1,
            f"Initial service build ownership was duplicated: {build_attempts(api_project)} compile attempts.")
    require(not (fixture.root / "ef-built-assembly.json").exists(),
            "EF consumed stale output before the first-success watcher build barrier.")
    snapshot = fixture.resource()
    require(snapshot.get("properties", {}).get("dotnet-watch.enabled") is True
            and not snapshot["properties"].get("dotnet-watch.transport.pid")
            and not snapshot["properties"].get("executable.pid"),
            "The failed initial watcher build launched a service transport or ordinary executable.")
    url = fixture.endpoint()
    # A build-gated resource can have no allocated URL yet. Neither an absent
    # URL nor an allocated but unserved URL may hide a launched stale process.
    require(url is None or no_http_response(url), "A stale ordinary service was launched after watch build handoff.")
    context.evidence.check("watcher-sole-initial-build-and-no-stale-ef-or-service",
                          attempts=build_attempts(api_project), snapshot=fixture.resource())

    replace_once(program, "Read() => ;", 'Read() => "build-recovered"')
    api_response = fixture.response("api", lambda value: value["value"] == "build-recovered", timeout=240)
    ordinary = fixture.response("ordinary")
    worker = fixture.response("worker")

    def migration_finished():
        state = fixture.resource("migrations", retry=True).get("state")
        require(state not in ("FailedToStart", "Exited", "Crashed"),
                f"The actual EF migration failed before completion: {state}")
        return state == "Finished"

    # WaitFor establishes resource readiness, not command completion. Observe the
    # real migration finishing before reading its assembly and database evidence.
    wait_for(migration_finished, "waiting for the actual EF migration to finish", timeout=90)
    require(read_json(fixture.root / "ef-built-assembly.json")["value"] == "build-recovered",
            "EF did not load the first successfully built watched assembly.")
    with sqlite3.connect(fixture.root / "watch.db") as database:
        require(database.execute("SELECT value FROM watch_evidence").fetchall() == [("build-recovered",)],
                "Actual EF migration did not wait for the watched build output.")
    fixture.application_snapshot()
    context.evidence.check("initial-build-source-fix-and-real-ef-barrier", response=api_response,
                          ef=read_json(fixture.root / "ef-built-assembly.json"))

    api_builds = build_attempts(api_project)
    worker_builds = build_attempts(worker_project)
    fixture.rejected("api", "rebuild")
    rebuilders = [
        resource for resource in fixture.describe(hidden=True)
        if resource.get("displayName", resource["name"]) == "api-rebuilder"
        or resource["name"].startswith("api-rebuilder-")
    ]
    require(len(rebuilders) == 1, f"The assembled model did not contain its guarded watched rebuilder: {rebuilders}")
    rebuilder = rebuilders[0]
    result = fixture.cli("resource", rebuilder["name"], "start", "--non-interactive", success=False, timeout=30)
    require(result.returncode != 0, f"The hidden watched rebuilder was physically started: {rebuilder}")
    require(build_attempts(api_project) == api_builds, "Rejected Rebuild still launched a physical watch build.")
    fixture.command_state("ordinary", "rebuild", "Enabled")
    replace_once(fixture.root / "Ordinary" / "Program.cs", '=> "before"', '=> "ordinary-after"')
    fixture.cli("resource", "ordinary", "rebuild", "--non-interactive")
    rebuilt = fixture.response("ordinary", lambda value: value["value"] == "ordinary-after")
    verify_new_application(ordinary, rebuilt, "Unwatched ordinary Rebuild")
    verify_same_application(api_response, fixture.response(), "Ordinary Rebuild isolation")
    verify_same_application(worker, fixture.response("worker"), "Independent watcher isolation")
    require(build_attempts(api_project) == api_builds and build_attempts(worker_project) == worker_builds,
            "Ordinary Rebuild disturbed independently owned watcher builds.")
    context.evidence.check("watched-rebuild-rejection-and-ordinary-rebuild-isolation", ordinary=rebuilt)

    proxy = fixture.application_snapshot()["properties"]["dotnet-watch.transport.pid"]
    for _ in range(2):
        fixture.command_state("api", "restart", "Enabled")
        previous = api_response
        fixture.cli("resource", "api", "restart", "--non-interactive", timeout=45)
        api_response = fixture.response("api", lambda value: value["instance"] != previous["instance"])
        verify_new_application(previous, api_response, "Restart-only control")
        require(fixture.application_snapshot()["properties"]["dotnet-watch.transport.pid"] == proxy,
                "Restart-only control replaced its transport rather than restarting the actual service.")
        verify_same_application(worker, fixture.response("worker"), "Restart-only independent group")
    require(build_attempts(api_project) == api_builds, "Restart-only control secretly built the project.")
    context.evidence.check("single-launch-restart-only-with-stable-transport", response=api_response)

    stopped = fixture.track(api_response["pid"])
    fixture.cli("resource", "api", "stop", "--non-interactive")
    fixture.wait_cleanup({stopped})
    wait_for(lambda: fixture.resource()["state"] == "Exited", "waiting for the stopped single launch")
    fixture.rejected("api", "start")
    require(fixture.resource().get("exitCode") is None, "Manual Stop published the proxy's zero application exit.")
    fixture.stop()
    fixture.start()
    replacement = fixture.response("api", lambda value: value["value"] == "build-recovered")
    verify_new_application(api_response, replacement, "A full watch-run restart after manual Stop")
    fixture.application_snapshot()
    worker = fixture.response("worker")
    rebuilt = fixture.response("ordinary", lambda value: value["value"] == "ordinary-after")
    api_response = replacement
    context.evidence.check("single-launch-stop-rejects-start-until-full-run-restart", response=replacement)

    _, owner = fixture.server_for("Api/Api.csproj")
    _, independent_owner = fixture.server_for("Independent/Worker/Worker.csproj")
    require(owner != independent_owner, "Failure-isolation fixture did not establish independent watch owners.")
    active_application = fixture.track(api_response["pid"])
    handle = (fixture.root / "pending-restart.log").open("w", encoding="utf-8")
    fixture.log_handles.append(handle)
    pending = subprocess.Popen(
        ["aspire", "resource", "api", "restart", "--apphost", str(fixture.apphost), "--non-interactive"],
        cwd=fixture.root, env=fixture.environment, stdout=handle, stderr=subprocess.STDOUT,
    )
    fixture.track(pending.pid)
    fixture.kill_watch_host(owner)
    pending.wait(timeout=45)
    fixture.wait_cleanup({owner, active_application})
    failed = wait_for(
        lambda: (value if (value := fixture.resource("api", retry=True))["state"] == "FailedToStart" else None),
        "waiting for real watch owner failure to fence its group",
    )
    require(failed["properties"]["dotnet-watch.enabled"] is True and failed.get("exitCode") is None,
            f"Post-handoff failure downgraded or invented an application exit: {failed}")
    require(independent_owner.alive(), "The failed group terminated an independent watcher.")
    verify_same_application(worker, fixture.response("worker"), "Real watcher failure isolation")
    verify_same_application(rebuilt, fixture.response("ordinary"), "Real watcher failure ordinary isolation")
    context.evidence.check("real-owner-termination-group-only-cleanup-and-settled-command",
                          failed=failed, pendingRestartExit=pending.returncode,
                          owner=owner.evidence(), independentOwner=independent_owner.evidence())
    fixture.stop()


def structured_log_has(fixture, name, text):
    result = fixture.cli("logs", name, "--include-hidden", "--tail", "200", "--format", "json", success=False, timeout=30)
    if result.returncode:
        raise ConnectionError(f"Logs unavailable during refresh: {result.stderr}")
    try:
        payload = json.loads(result.stdout)
    except json.JSONDecodeError as error:
        raise ConnectionError(f"No structured log snapshot: {result.stdout}") from error
    found = text.casefold() in json.dumps(payload).casefold()
    if found:
        write(fixture.context.evidence.path.parent / fixture.root.name / f"{name}-logs.json", result.stdout)
    return found


def verify_compatibility(context):
    fixture = Fixture(context, "file-app-and-release-launch-context")
    release_watch_properties = """    <Optimize>false</Optimize>
    <MetadataUpdaterSupport>true</MetadataUpdaterSupport>"""
    fixture.shared(properties=release_watch_properties)
    fixture.service("Api", shared=True, properties=release_watch_properties)
    fixture.service("Worker", shared=True, properties=release_watch_properties)
    working_directory = fixture.root / "run-directory"
    write(working_directory / "watch-working-token.txt", "actual-working-directory")
    arguments = [
        "--framework", "application-framework",
        "--project", "application-project",
        "--configuration", "application-configuration",
        "--launch-profile", "application-profile",
        "--watch", "application-watch",
        "--literal", "two words",
    ]
    profile_arguments = " ".join('"' + value + '"' if " " in value else value for value in arguments)
    write(fixture.root / "Api" / "Properties" / "launchSettings.json", json.dumps({
        "profiles": {
            "unused": {"commandName": "Project", "environmentVariables": {"WATCH_PROFILE_VALUE": "wrong-profile"}},
            "watch-profile": {
                "commandName": "Project",
                "commandLineArgs": profile_arguments,
                "environmentVariables": {"WATCH_PROFILE_VALUE": "selected-profile"},
            },
        }
    }, indent=2))
    program = fixture.root / "Api" / "Program.cs"
    replace_once(program, "WebApplication.CreateBuilder(args)", "WebApplication.CreateBuilder()")
    file_arguments = ["--framework", "file-framework", "--project", "file-project", "--watch", "file-watch"]
    # A file app owns its directory's content. Keep unrelated projects outside
    # it so their new bin/obj files do not become SDK file-app watch inputs.
    file_source = fixture.root / "FileApp" / "file.cs"
    write(file_source, """
#:sdk Microsoft.NET.Sdk.Web
#:property TargetFramework=net10.0
#:property PublishAot=false
#:property Optimize=false
#:property MetadataUpdaterSupport=true
""" + service_source().replace("WebApplication.CreateBuilder(args)", "WebApplication.CreateBuilder()"))
    extra_file_arguments = ".WithArgs(" + ", ".join(json.dumps(argument) for argument in file_arguments) + ")"
    fixture.csharp_model(
        fixture.registration("api", "Api/Api.csproj",
                             f".WithWorkingDirectory({json.dumps(str(working_directory))})",
                             options='options.LaunchProfileName = "watch-profile";')
        + fixture.registration("worker", "Worker/Worker.csproj")
        + fixture.registration("file", "FileApp/file.cs", extra_file_arguments)
    )
    source = fixture.apphost.parent / "AppHost.cs"
    replace_once(source, "var builder = DistributedApplication.CreateBuilder(args);", """
var builder = DistributedApplication.CreateBuilder(args);
builder.Configuration["AppHost:BuildConfiguration"] = "Release";""")
    host_arguments = ["--watch", "host-application-watch", "--configuration", "host-application-configuration"]
    fixture.start(application_args=host_arguments)
    api = fixture.response()
    worker = fixture.response("worker")
    file = fixture.response("file")
    for name in ("api", "worker", "file"):
        fixture.application_snapshot(name)
    require(api["configuration"] == worker["configuration"] == file["configuration"] == "Release",
            f"Non-Debug watch configuration was lost: {api}, {worker}, {file}")
    require(api["workingDirectory"] == str(working_directory)
            and api["workingToken"] == "actual-working-directory",
            f"The watch proxy did not preserve the custom working directory: {api}")
    require(api["profile"] == "selected-profile" and api["arguments"] == arguments,
            f"The selected launch profile or SDK-shaped application arguments were lost: {api}")
    require(file["arguments"] == file_arguments, f"File-app arguments were consumed as SDK switches: {file}")
    require(fixture.host_instance()["arguments"] == host_arguments,
            "Literal --watch/--configuration after -- were interpreted as CLI or watcher arguments.")
    application_temp = pathlib.Path(fixture.environment["TMPDIR"]).resolve()
    expected_temp = str(application_temp) + os.sep
    require(all(value["temporaryDirectory"] == expected_temp for value in (api, worker, file)),
            f"Watcher-only SDK-temp overrides leaked into applications: {api}, {worker}, {file}")
    servers = fixture.servers()
    require(len(servers) == 2, f"A managed file app should have a dedicated owner, plus the shared project group: {servers}")
    _, multi_root_server = fixture.server_for("Api/Api.csproj")
    server_environment = multi_root_server.environment()
    sdk_temp = pathlib.Path(server_environment.get("TMPDIR", str(application_temp))).resolve()
    sdk_pin = None
    if sdk_temp != application_temp:
        require(sdk_temp.is_relative_to(application_temp)
                and sdk_temp.name.startswith("tmp-") and sdk_temp.parent.name.startswith("aspw-")
                and sdk_temp.parent.stat().st_mode & 0o777 == 0o700,
                f"The multi-root server did not receive a private owned SDK-temp directory: {sdk_temp}")
        sdk_pin = read_json(sdk_temp / "global.json")
        require(sdk_pin["sdk"]["version"] == SDK_VERSION and sdk_pin["sdk"]["rollForward"] == "disable",
                f"Redirected multi-root SDK selection is not explicitly pinned: {sdk_pin}")
    selected = fixture.command([fixture.dotnet, "--version"], cwd=sdk_temp, environment=server_environment)
    require(selected.stdout.strip() == SDK_VERSION, f"The actual multi-root build temp selected {selected.stdout!r}")
    context.evidence.check("managed-file-app-release-profiles-working-directory-literal-args-and-sdk-temp",
                          api=api, worker=worker, file=file, sdkTemp=str(sdk_temp), sdkPin=sdk_pin)

    updated = hot_reload(fixture, program, '=> "before"', '=> "release-after"', ["api"], expected="release-after")
    require(updated["api"]["configuration"] == "Release", "Release adaptation was lost during hot reload.")
    replace_once(file_source, 'Read() => "before"', 'Read() => "file-after"')
    file = fixture.response("file", lambda value: value["value"] == "file-after", timeout=240)
    fixture.application_snapshot("file")
    file_owner_resource, file_owner = wait_for(
        lambda: fixture.server_for("FileApp/file.cs"), "capturing the current dedicated file-app watch owner")
    replace_once(file_source, 'Read() => "file-after"', "Read() => ;")
    # Compiler output belongs to the bundled watcher, not the file app's
    # stdout/stderr proxy stream. Require the same real diagnostic from its owner.
    wait_for(lambda: structured_log_has(fixture, file_owner_resource["name"], "CS1525"),
             "waiting for the managed file-app invalid-edit diagnostic")
    context.evidence.check("managed-file-app-invalid-edit-watcher-diagnostic",
                          owner=file_owner.evidence(), ownerResource=file_owner_resource["name"])
    replace_once(file_source, "Read() => ;", 'Read() => "file-recovered"')
    recovered = fixture.response("file", lambda value: value["value"] == "file-recovered", timeout=240)
    fixture.application_snapshot("file")
    require(recovered["configuration"] == "Release" and recovered["arguments"] == file_arguments,
            f"File-app invalid-edit recovery changed the launch context: {recovered}")
    verify_same_application(worker, fixture.response("worker"), "Dedicated file-app recovery isolation")
    context.evidence.check("managed-file-app-invalid-edit-hot-reload-recovery", response=recovered)

    _, before_rude_owner = wait_for(
        lambda: fixture.server_for("FileApp/file.cs"), "capturing the file-app owner before a rude edit")
    before_rude_application = fixture.track(recovered["pid"])
    # Changing a type's kind is an ENC rude edit, which exercises the pinned
    # SDK's virtual-project rebuild failure and the dedicated fresh-owner recovery.
    replace_once(file_source, "public class ValueSource", "public struct ValueSource")
    replace_once(file_source, 'Read() => "file-recovered"', 'Read() => "file-rude-recovered"')
    rude_recovered = fixture.response("file", lambda value: value["value"] == "file-rude-recovered", timeout=240)
    after_rude_owner = wait_for(
        lambda: (identity if (identity := fixture.server_for("FileApp/file.cs")[1]) != before_rude_owner else None),
        "waiting for the dedicated file-app's fresh watcher after a rude edit",
    )
    fixture.wait_cleanup({before_rude_owner, before_rude_application})
    verify_new_application(recovered, rude_recovered, "Dedicated file-app fresh-watcher recovery")
    fixture.application_snapshot("file")
    require(rude_recovered["configuration"] == "Release" and rude_recovered["arguments"] == file_arguments,
            f"Fresh file-app watcher recovery changed the launch context: {rude_recovered}")
    verify_same_application(worker, fixture.response("worker"), "Rude file-app edit independent group isolation")
    context.evidence.check("managed-file-app-invalid-edit-fresh-watcher-recovery", response=rude_recovered,
                          beforeOwner=before_rude_owner.evidence(), afterOwner=after_rude_owner.evidence())

    before_run_restart_host = fixture.host_instance()
    return_type_owner_resource, return_type_owner = wait_for(
        lambda: fixture.server_for("FileApp/file.cs"), "capturing the file-app owner before an SDK-accepted edit")
    replace_once(file_source, 'public static string Read() => "file-rude-recovered";',
                 'public static object Read() => "file-return-type-recovered";')

    def accepted_edit_replay_failure():
        try:
            request_json(fixture.endpoint("file"))
        except (urllib.error.URLError, TimeoutError, ConnectionError):
            # Requests drive the changed call site; the known broken SDK delta
            # can return an error or leave no responsive child during its replay.
            pass
        return structured_log_has(fixture, return_type_owner_resource["name"],
                                  "Attempted to invoke a deleted method implementation")

    wait_for(accepted_edit_replay_failure, "observing the SDK-accepted delete/reinsert replay limitation", timeout=45)
    context.evidence.check("sdk-accepted-return-type-replay-requires-full-run-restart",
                          owner=return_type_owner.evidence(), automaticRecovery=False)
    fixture.stop()
    require(not fixture.owned_processes(), "The old full watch run left fixture-owned processes behind.")
    fixture.start(application_args=host_arguments)
    restarted = fixture.response("file", lambda value: value["value"] == "file-return-type-recovered", timeout=240)
    _, restarted_owner = wait_for(
        lambda: fixture.server_for("FileApp/file.cs"), "capturing the fresh full-run file-app owner")
    restarted_host = fixture.host_instance()
    require(restarted_owner != return_type_owner, "A full watch-run restart retained the old file-app watcher.")
    verify_new_application(rude_recovered, restarted, "Full-run recovery from SDK-accepted delete/reinsert")
    require(restarted_host["pid"] != before_run_restart_host["pid"]
            and restarted_host["instance"] != before_run_restart_host["instance"]
            and restarted_host["backchannel"] != before_run_restart_host["backchannel"],
            "The explicit full-run recovery reused an old AppHost generation or backchannel.")
    require(restarted_host["arguments"] == host_arguments
            and restarted["configuration"] == "Release" and restarted["arguments"] == file_arguments,
            "Full-run recovery changed the AppHost/file-app launch context.")
    fixture.response("api", lambda value: value["value"] == "release-after")
    verify_new_application(worker, fixture.response("worker"), "Independent group after complete run replacement")
    fixture.application_snapshot("file")
    context.evidence.check("sdk-accepted-return-type-full-run-restart-and-cleanup", response=restarted,
                          beforeOwner=return_type_owner.evidence(), afterOwner=restarted_owner.evidence())
    fixture.stop()


def fenced_group(fixture, name, old_owner, old_application):
    failed = wait_for(
        lambda: (snapshot if (snapshot := fixture.resource(name, retry=True))["state"] == "FailedToStart" else None),
        "waiting for changed evaluated assumptions to fence the watcher group",
    )
    fixture.wait_cleanup({old_owner, old_application})
    require(failed.get("properties", {}).get("dotnet-watch.enabled") is True
            and failed.get("exitCode") is None,
            f"A post-handoff group invalidation silently downgraded or invented an exit: {failed}")
    require("restart" in failed["properties"]["dotnet-watch.coverage"].lower(),
            f"Invalidated coverage has no actionable full-run restart diagnostic: {failed}")
    fixture.rejected(name, "start")
    fixture.rejected(name, "restart")
    fixture.rejected(name, "rebuild")
    return failed


def verify_isolation(context):
    collision = Fixture(context, "evaluated-shared-output-collision")
    collision.shared()
    reference = "$(WatchSharedRoot)/Shared.csproj"
    collision.service(properties="    <WatchSharedRoot>../Shared</WatchSharedRoot>", references=(reference,))
    collision.service("Ordinary", properties="    <WatchSharedRoot>../Shared</WatchSharedRoot>", references=(reference,))
    collision.service("Independent/Worker")
    context_project = collision.service(
        "Context",
        properties="""    <WatchFixtureBuildFlavor>project-owned</WatchFixtureBuildFlavor>
    <OutputPath>bin/$(WatchFixtureBuildFlavor)/</OutputPath>""",
    )
    collision.pin_sdk(collision.root / "Independent")
    collision.csharp_model(
        collision.registration("api", "Api/Api.csproj")
        + collision.registration("ordinary", "Ordinary/Ordinary.csproj", ".WithWatch(false)")
        + collision.registration("worker", "Independent/Worker/Worker.csproj")
        + collision.registration("context", "Context/Context.csproj",
                                 '.WithBuildEnvironment("WatchFixtureBuildFlavor", "requested-flavor")')
    )
    collision.start()
    ordinary_api = collision.response()
    collision.response("ordinary")
    collision.response("worker")
    contextual = collision.response("context")
    require(collision.resource("context")["properties"].get("dotnet-watch.enabled") is not True,
            "An unrepresentable lower-precedence build environment was silently watched.")
    require((context_project.parent / "bin" / "requested-flavor" / "net10.0" / "Context.dll").is_file(),
            "Preflight fallback did not preserve the ordinary global-property build context.")
    require(structured_log_has(collision, "context", "ordinary and watcher build graphs differ"),
            "Unsupported watch context lost its build-context incompatibility diagnostic.")
    require(collision.resource()["properties"].get("dotnet-watch.enabled") is not True,
            "A nonliteral evaluated shared-output collision was incorrectly handed to the watcher.")
    require(structured_log_has(collision, "api", "share physical project")
            and structured_log_has(collision, "api", str(collision.root / "Shared" / "Shared.csproj")),
            "The preflight ordinary fallback lost its evaluated collision diagnostic.")
    collision.command_state("api", "rebuild", "Enabled")
    collision.application_snapshot("worker")
    require(len(collision.servers()) == 1, "Collision fallback created a competing build owner.")
    context.evidence.check("evaluated-nonliteral-shared-output-and-unrepresentable-context-preflight-fallback",
                          response=ordinary_api, contextual=contextual)
    collision.stop()
    source = collision.apphost.parent / "AppHost.cs"
    api_registration = collision.registration("api", "Api/Api.csproj")
    replace_once(source, api_registration, collision.registration("api", "Api/Api.csproj", ".WithWatch(true)"))
    collision.environment["ASPIRE_CLI_START_TIMEOUT"] = "15"
    failed_run = collision.start(detached=False)
    require(failed_run.wait(timeout=120) != 0, "Unsatisfied explicit WithWatch(true) silently ran ordinarily.")
    collision.foreground = None
    require("WithWatch(true)" in (collision.root / "watch-owner.log").read_text(),
            "Unsatisfied explicit watch did not name the requirement.")
    collision.wait_cleanup(collision.capture_generation())
    context.evidence.check("explicit-watch-rejects-unsatisfied-evaluated-collision")

    fixture = Fixture(context, "new-evaluated-references-and-imports")
    fixture.shared(name="References", value="reference-before")
    # Rename the one physical library project to make later wildcard additions
    # independent assemblies in the same evaluated project-reference directory.
    (fixture.root / "References" / "References.csproj").rename(fixture.root / "References" / "Existing.csproj")
    project = fixture.service(
        properties="""    <WatchReferenceRoot>../References</WatchReferenceRoot>
    <WatchInputsRoot>../Inputs</WatchInputsRoot>
    <WatchFixtureRefresh>initial</WatchFixtureRefresh>""",
        references=("$(WatchReferenceRoot)/*.csproj",),
        imports='  <Import Project="$(WatchInputsRoot)/active.props" />',
    )
    write(fixture.root / "Inputs" / "active.props", "<Project />")
    replace_once(fixture.root / "Api" / "Program.cs", 'Read() => "before"',
                 "Read() => WatchShared.SharedValue.Read()")
    fixture.service("Ordinary")
    fixture.service("Independent/Worker")
    fixture.pin_sdk(fixture.root / "Independent")
    fixture.csharp_model(
        fixture.registration("api", "Api/Api.csproj")
        + fixture.registration("ordinary", "Ordinary/Ordinary.csproj", ".WithWatch(false)")
        + fixture.registration("worker", "Independent/Worker/Worker.csproj")
    )
    fixture.start()
    initial = fixture.response("api", lambda value: value["value"] == "reference-before")
    initial_application = fixture.track(initial["pid"])
    ordinary = fixture.response("ordinary")
    worker = fixture.response("worker")
    _, owner = fixture.server_for("Api/Api.csproj")

    write(fixture.root / "References" / "Added.csproj", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup>
  <ItemGroup><Compile Include="Added.cs" /></ItemGroup>
</Project>
""")
    # Existing.csproj must not compile Added.cs as well; update its evaluated
    # Compile item inventory rather than relying on a literal reference scanner.
    existing = fixture.root / "References" / "Existing.csproj"
    replace_once(existing, "</Project>", """
  <ItemGroup><Compile Remove="Added.cs" /></ItemGroup>
</Project>""")
    write(fixture.root / "References" / "Added.cs", """
namespace WatchAdded;
public static class AddedValue
{
    public static string Read()
    {
#if LIBRARY_REFRESHED
        return "added-imported";
#else
        return "added-before";
#endif
    }
}
""")
    replace_once(project, "<WatchFixtureRefresh>initial</WatchFixtureRefresh>",
                 "<WatchFixtureRefresh>new-wildcard-reference</WatchFixtureRefresh>")
    replacement = wait_for(
        lambda: (item if (item := fixture.server_for("Api/Api.csproj"))[1] != owner else None),
        "waiting for evaluated wildcard-reference adoption",
    )[1]
    fixture.wait_cleanup({owner, initial_application})
    replace_once(fixture.root / "Api" / "Program.cs", "Read() => WatchShared.SharedValue.Read()",
                 "Read() => WatchAdded.AddedValue.Read()")
    added = fixture.response("api", lambda value: value["value"] == "added-before", timeout=240)
    fixture.application_snapshot()

    import_file = fixture.root / "References" / "Nested" / "newly-imported.props"
    write(import_file, "<Project />")
    replace_once(fixture.root / "References" / "Added.csproj", "</Project>", """
  <Import Project="Nested/newly-imported.props" />
</Project>""")
    imported_owner = wait_for(
        lambda: (item if (item := fixture.server_for("Api/Api.csproj"))[1] != replacement else None),
        "waiting for newly evaluated import inventory",
    )[1]
    fixture.wait_cleanup({replacement})
    fixture.response("api", lambda value: value["value"] == "added-before")
    write(import_file, """
<Project><PropertyGroup><DefineConstants>$(DefineConstants);LIBRARY_REFRESHED</DefineConstants></PropertyGroup></Project>
""")
    imported = fixture.response("api", lambda value: value["value"] == "added-imported", timeout=240)
    write(fixture.root / "Inputs" / "active.props", """
<Project><PropertyGroup><DefineConstants>$(DefineConstants);WATCH_IMPORTED</DefineConstants></PropertyGroup></Project>
""")
    complete = fixture.response("api", lambda value: value["imported"] == "imported"
                                and value["value"] == "added-imported", timeout=240)
    fixture.application_snapshot()
    verify_same_application(worker, fixture.response("worker"), "Observed-input refresh independent group")
    context.evidence.check("new-wildcard-reference-and-new-import-inventory", response=complete,
                          observedImport=str(import_file), owner=imported_owner.evidence())

    _, final_owner = fixture.server_for("Api/Api.csproj")
    final_application = fixture.track(complete["pid"])
    replace_once(project, "<Nullable>enable</Nullable>", """
<Nullable>enable</Nullable>
    <BaseOutputPath>../Ordinary/bin/</BaseOutputPath>""")
    invalidated = fenced_group(fixture, "api", final_owner, final_application)
    verify_same_application(ordinary, fixture.response("ordinary"), "Output-isolation invalidation ordinary resource")
    verify_same_application(worker, fixture.response("worker"), "Output-isolation invalidation independent group")
    context.evidence.check("changed-output-domain-fenced-without-late-fallback", snapshot=invalidated)
    fixture.stop()

    sdk_scope = Fixture(context, "changed-sdk-scope")
    sdk_scope.service()
    sdk_scope.csharp_model(sdk_scope.registration("api", "Api/Api.csproj"))
    sdk_scope.start()
    current = sdk_scope.response()
    _, sdk_owner = sdk_scope.server_for("Api/Api.csproj")
    application = sdk_scope.track(current["pid"])
    alternate = next((version for version in context.sdks if version.startswith("11.")), None)
    if alternate:
        sdk_scope.pin_sdk(sdk_scope.root / "Api", alternate)
        invalidated = fenced_group(sdk_scope, "api", sdk_owner, application)
        context.evidence.check("new-sdk-scope-fenced-and-waits-settled", selectedSdk=alternate,
                              snapshot=invalidated)
    else:
        # An unavailable exact pin cannot establish a changed SDK identity. Preserve
        # the last-good graph and service until another edit can be evaluated.
        write(sdk_scope.root / "Api" / "global.json", json.dumps({
            "sdk": {"version": "10.0.999", "rollForward": "disable",
                    "paths": [str(context.sdks[SDK_VERSION])]}
        }, indent=2))
        wait_for(lambda: structured_log_has(
            sdk_scope, "api", "Build-input evaluation failed: Watch SDK selection failed"),
            "waiting for unknown SDK evaluation fallback")
        preserved = sdk_scope.response()
        verify_same_application(current, preserved, "Unknown SDK scope evaluation fallback")
        require(sdk_owner.alive() and application.alive(),
                "Unknown SDK evaluation stopped the last-good watcher or application.")
        context.evidence.check("unknown-sdk-scope-preserves-last-good-state",
                              selectedSdk="10.0.999", response=preserved)
    sdk_scope.stop()


def create_certificates(fixture):
    project = fixture.root / "Certificates" / "Certificates.csproj"
    write(project, """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
</Project>
""")
    write(project.parent / "Program.cs", """
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

Directory.CreateDirectory(args[0]);
using var authorityKey = RSA.Create(2048);
var authorityRequest = new CertificateRequest("CN=Watch Fixture Authority", authorityKey,
    HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
authorityRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
authorityRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
    X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
authorityRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(authorityRequest.PublicKey, false));
using var authority = authorityRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
File.WriteAllText(Path.Combine(args[0], "authority.pem"), authority.ExportCertificatePem());

using var serverKey = RSA.Create(2048);
var serverRequest = new CertificateRequest("CN=localhost", serverKey,
    HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
serverRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
serverRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
    new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, true));
var names = new SubjectAlternativeNameBuilder();
names.AddDnsName("localhost");
names.AddIpAddress(IPAddress.Loopback);
names.AddIpAddress(IPAddress.IPv6Loopback);
serverRequest.CertificateExtensions.Add(names.Build());
using var publicServer = serverRequest.Create(authority, DateTimeOffset.UtcNow.AddHours(-1),
    DateTimeOffset.UtcNow.AddDays(1), RandomNumberGenerator.GetBytes(16));
using var server = publicServer.CopyWithPrivateKey(serverKey);
File.WriteAllBytes(Path.Combine(args[0], "server.pfx"), server.Export(X509ContentType.Pfx, "watch-fixture"));
""")
    fixture.dotnet_command("build", project, "--nologo", "-v:quiet")
    fixture.dotnet_command(
        "exec", project.parent / "bin" / "Debug" / "net10.0" / "Certificates.dll",
        fixture.root / "certificates",
    )


def verify_https(context):
    fixture = Fixture(context, "https-and-private-environment")
    create_certificates(fixture)
    fixture.service()
    fixture.service("Client")
    # Project defaults preserve explicit certificates through these unprefixed
    # Kestrel names; ASPNETCORE_-prefixed names do not suppress dev-cert injection.
    api = fixture.registration("api", "Api/Api.csproj", f"""
            .WithHttpsEndpoint(env: "ASPNETCORE_HTTPS_PORTS")
            .WithEnvironment("Kestrel__Certificates__Default__Path",
                {json.dumps(str(fixture.root / "certificates" / "server.pfx"))})
            .WithEnvironment("Kestrel__Certificates__Default__Password", "watch-fixture")"""
    ).replace("builder.AddDotnetProject", "var api = builder.AddDotnetProject", 1)
    client = fixture.registration("client", "Client/Client.csproj", """
            .WithEnvironment("WATCH_TRUST_URL", api.GetEndpoint("https"))
            .WithCertificateAuthorityCollection(authority)
            .WithCertificateTrustScope(CertificateTrustScope.Override)
            .WithDeveloperCertificateTrust(false)"""
    )
    fixture.csharp_model(f"""
        var authority = builder.AddCertificateAuthorityCollection("watch-authority")
            .WithCertificate(System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificateFromFile(
                {json.dumps(str(fixture.root / "certificates" / "authority.pem"))}));
""" + api + client)
    fixture.start()
    trust = ssl.create_default_context(cafile=str(fixture.root / "certificates" / "authority.pem"))
    initial = fixture.response("api", scheme="https", context=trust)
    client_response = fixture.response("client")
    startup_environment = fixture.track(client_response["pid"]).environment()
    trust_environment = client_response["trustEnvironment"]
    require(trust_environment and all(startup_environment.get(name) == value
                                      for name, value in trust_environment.items()),
            "The managed client's trust paths were not present in its actual native startup environment.")
    require(trust_environment != initial["trustEnvironment"],
            "Per-resource certificate trust was mixed into one shared watch-server environment.")
    fixture.application_snapshot("api")
    fixture.application_snapshot("client")
    trusted = wait_for(lambda: request_json(fixture.endpoint("client") + "/trust"),
                       "waiting for actual .NET client certificate-chain and hostname validation")
    verify_same_application(initial, trusted, "Custom trust forwarding through the watcher")
    require(client_response["privateEnvironment"] == initial["privateEnvironment"] == {},
            "Private AppHost ownership/session/control or watcher cleanup state reached a service.")
    environment = fixture.resource("client").get("environment", {})
    require(environment.get("SSL_CERT_DIR") or environment.get("SSL_CERT_FILE"),
            f"No actual late certificate trust environment reached the proxy: {environment}")
    context.evidence.check("real-https-and-forwarded-custom-trust-without-private-state",
                          https=initial, client=client_response, trustedResponse=trusted)
    replace_once(fixture.root / "Api" / "Program.cs", 'Read() => "before"', 'Read() => "https-after"')
    updated = fixture.response("api", lambda value: value["value"] == "https-after", scheme="https", context=trust)
    verify_same_application(initial, updated, "HTTPS service metadata update")
    trusted_update = wait_for(
        lambda: (value if (value := request_json(fixture.endpoint("client") + "/trust"))["value"] == "https-after" else None),
        "waiting for HTTPS hot reload through the custom-trusting managed client",
    )
    verify_same_application(updated, trusted_update, "Trusted HTTPS response after hot reload")
    fixture.stop()
    context.evidence.check("https-hot-reload-and-owned-process-cleanup", response=updated)


def query_payload(fixture, environment=None, success=True):
    result = fixture.dotnet_command(
        "msbuild", fixture.apphost, "-nologo", "-verbosity:quiet",
        "-target:ResolveAspireDotnetWatch", "-getTargetResult:ResolveAspireDotnetWatch",
        "-getProperty:NETCoreSdkVersion,MSBuildToolsPath,DotNetHostPath",
        environment=environment, success=success,
    )
    if not success:
        return result
    payload = json.loads(result.stdout)
    target = payload["TargetResults"]["ResolveAspireDotnetWatch"]
    require(target["Result"] == "Success" and len(target["Items"]) == 1,
            f"The actual packed resolve target did not return one descriptor: {payload}")
    item = target["Items"][0]
    require(item["SdkVersion"] == SDK_VERSION, f"Packed prelaunch silently selected a different SDK: {item}")
    require(pathlib.Path(item["ToolPath"]).is_file() and pathlib.Path(item["WatchHostPath"]).is_file(),
            f"Packed prelaunch did not resolve complete bundled entry points: {item}")
    msbuild = pathlib.Path(item["MSBuildPath"])
    # The package returns its executing MSBuildToolsPath directory. The CLI
    # also accepts a full MSBuild.dll descriptor; both identify the same selected SDK.
    sdk_directory = msbuild.parent if msbuild.name == "MSBuild.dll" else msbuild
    require((sdk_directory / "MSBuild.dll").is_file()
            and sdk_directory.name == SDK_VERSION
            and sdk_directory.parent.parent.resolve() == fixture.context.sdks[SDK_VERSION],
            f"The packed target did not return its actual selected SDK: {item}")
    require(pathlib.Path(item["DotnetPath"]).resolve() == pathlib.Path(fixture.dotnet).resolve(),
            f"The packed target substituted another muxer: {item}")
    return item


def watcher_install_cache_entries(fixture):
    entries = set()
    for directory in (
        pathlib.Path.home() / ".dotnet" / "tools" / ".store",
        pathlib.Path.home() / ".aspire" / "tools",
        pathlib.Path(fixture.environment["DOTNET_CLI_HOME"]) / ".dotnet" / "tools" / ".store",
    ):
        if directory.is_dir():
            entries.update(str(path) for path in directory.glob("*") if "watch" in path.name.lower())
    return entries


def compare_fresh_package_bytes(fixture, consumed, package_name, entry):
    package = fixture.hive.package(package_name)
    with zipfile.ZipFile(package) as archive:
        require(entry in archive.namelist(), f"The fresh {package_name} package has no '{entry}'.")
        require(consumed.is_file(), f"The consumed {package_name} asset is missing: {consumed}")
        require(
            consumed.read_bytes() == archive.read(entry),
            f"Stale same-version package cache or build output for {package_name}/{fixture.hive.version}: "
            f"'{consumed}' differs from fresh '{package}!{entry}'. Stop consumers and invalidate only the "
            "exact owned package ID/version cache before rerunning; this verifier never clears caches.",
        )
    return {"package": package_name, "version": fixture.hive.version,
            "entry": entry, "consumedPath": str(consumed), "freshPackage": str(package)}


def query_consumed_watch_assets(fixture):
    result = fixture.dotnet_command(
        "msbuild", fixture.apphost, "-nologo", "-verbosity:quiet", "-target:ResolveReferences",
        "-getItem:ReferencePath,ReferenceCopyLocalPaths",
        "-getProperty:ProjectAssetsFile,TargetFramework,TargetFrameworkMoniker,RuntimeIdentifier,TargetDir,"
        "AspireDotnetWatchToolsDir,AspireDotnetWatchHostDir",
    )
    query = json.loads(result.stdout)
    properties = query["Properties"]
    assets_path = pathlib.Path(properties["ProjectAssetsFile"])
    if not assets_path.is_absolute():
        assets_path = fixture.apphost.parent / assets_path
    assets = read_json(assets_path)
    suffix = "/" + properties["RuntimeIdentifier"] if properties.get("RuntimeIdentifier") else ""
    target_key = next((
        framework + suffix for framework in (properties["TargetFramework"], properties["TargetFrameworkMoniker"])
        if framework + suffix in assets["targets"]
    ), None)
    require(target_key is not None, f"No assets target matches the actual MSBuild framework/RID: {properties}")
    return query, assets, assets["targets"][target_key]


def consumed_watch_libraries(fixture, query, assets, target):
    packages = {}
    comparisons = []
    for package_name in ("Aspire.Hosting.Dotnet", "Aspire.Hosting"):
        keys = [
            key for key in target if key.rsplit("/", 1)[0].casefold() == package_name.casefold()
        ]
        require(len(keys) == 1, f"The active restored target has no unique {package_name}: {keys}")
        key = keys[0]
        require(key.rsplit("/", 1)[1].casefold() == fixture.hive.version.casefold(),
                f"The active restored {package_name} version differs from the fresh hive: {key}")
        library = assets["libraries"][key]
        require(library["type"] == "package", f"{package_name} was not consumed from its actual restored package.")
        expected_roots = {
            (pathlib.Path(folder) / library["path"]).resolve() for folder in assets["packageFolders"]
        }
        roots = set()
        outputs = []
        for item_name, asset_kind in (("ReferencePath", "compile"), ("ReferenceCopyLocalPaths", "runtime")):
            items = [
                item for item in query["Items"].get(item_name, [])
                if pathlib.Path(item["Identity"]).name == package_name + ".dll"
            ]
            require(len(items) == 1, f"MSBuild did not resolve one actual {package_name} {item_name}: {items}")
            item = items[0]
            require(not item.get("NuGetPackageId")
                    or item["NuGetPackageId"].casefold() == package_name.casefold(),
                    f"MSBuild resolved {package_name}.dll from another package: {item}")
            consumed = pathlib.Path(item.get("FullPath", item["Identity"])).resolve()
            # project.assets.json records e.g. compile "ref/net10.0/Aspire.Hosting.dll"
            # and runtime "lib/net10.0/Aspire.Hosting.dll". Select against the actual
            # resolved MSBuild path, including fallback package folders; do not infer
            # a package root from the bundled tool's directory layout.
            entries = [
                entry for entry in target[key].get(asset_kind, {})
                if tuple(consumed.parts[-len(pathlib.PurePosixPath(entry).parts):])
                == pathlib.PurePosixPath(entry).parts
            ]
            require(len(entries) == 1,
                    f"The actual MSBuild {package_name} {asset_kind} path has no unique assets entry: {consumed}")
            entry = entries[0]
            root = consumed.parents[len(pathlib.PurePosixPath(entry).parts) - 1]
            require(root in expected_roots,
                    f"MSBuild resolved {package_name} outside its actual assets package folders: {consumed}")
            roots.add(root)
            comparisons.append(compare_fresh_package_bytes(fixture, consumed, package_name, entry))
            if asset_kind == "runtime":
                # This is the SDK's ReferenceCopyLocalPaths destination metadata, not
                # a guessed bin/configuration/TFM path.
                output = (pathlib.Path(query["Properties"]["TargetDir"])
                          / item.get("DestinationSubDirectory", "") / consumed.name)
                outputs.append((output, entry))
        require(len(roots) == 1, f"Compile/runtime assets resolved different {package_name} package roots: {roots}")
        packages[package_name] = {
            "root": next(iter(roots)), "files": library["files"], "target": target[key], "outputs": outputs,
        }
    return packages, comparisons


def verify_payload(context):
    fixture = Fixture(context, "actual-packed-payload")
    fixture.service()
    fixture.csharp_model(fixture.registration("api", "Api/Api.csproj"))
    before = watcher_install_cache_entries(fixture)
    fixture.dotnet_command("restore", fixture.apphost, "--configfile", fixture.root / "NuGet.config",
                           "--nologo", "-v:quiet")
    descriptor = query_payload(fixture)
    consumption, assets, target = query_consumed_watch_assets(fixture)
    consumed_packages, comparisons = consumed_watch_libraries(fixture, consumption, assets, target)
    dotnet_package = consumed_packages["Aspire.Hosting.Dotnet"]
    properties = consumption["Properties"]
    tool = pathlib.Path(descriptor["ToolPath"]).resolve()
    host = pathlib.Path(descriptor["WatchHostPath"]).resolve()
    require(tool.parent == pathlib.Path(properties["AspireDotnetWatchToolsDir"]).resolve()
            and host.parent == pathlib.Path(properties["AspireDotnetWatchHostDir"]).resolve(),
            "The actual resolve descriptor and consumed MSBuild watch directories disagree.")
    package = fixture.hive.package("Aspire.Hosting.Dotnet")
    with zipfile.ZipFile(package) as archive:
        names = set(archive.namelist())
        required = (
            "buildTransitive/Aspire.Hosting.Dotnet.targets",
            f"tools/dotnet-watch/{WATCH_TOOL}",
            "tools/dotnet-watch/Microsoft.DotNet.HotReload.Watch.Aspire.deps.json",
            "tools/dotnet-watch/Microsoft.DotNet.HotReload.Watch.Aspire.runtimeconfig.json",
            f"tools/dotnet-watch-host/{WATCH_HOST}",
            "tools/dotnet-watch-host/Aspire.Hosting.Dotnet.WatchHost.deps.json",
            "tools/dotnet-watch-host/Aspire.Hosting.Dotnet.WatchHost.runtimeconfig.json",
        )
        require(all(name in names for name in required), f"The fresh packed watcher payload is incomplete: {names}")
    for consumed in (tool, host):
        require(consumed.is_relative_to(dotnet_package["root"]),
                f"The actual watch descriptor escaped its consumed Hosting.Dotnet package: {consumed}")
        entry = consumed.relative_to(dotnet_package["root"]).as_posix()
        require(entry in dotnet_package["files"], f"The resolved watch payload is not in the actual assets inventory: {entry}")
        comparisons.append(compare_fresh_package_bytes(fixture, consumed, "Aspire.Hosting.Dotnet", entry))
    for filename in ("Aspire.DotnetWatch.Evaluation.dll", "Aspire.DotnetWatch.Evaluation.proj"):
        entries = [entry for entry in dotnet_package["files"] if pathlib.PurePosixPath(entry).name == filename]
        require(len(entries) == 1, f"The actual restored assets inventory omitted one evaluator input: {filename}")
        entry = entries[0]
        # Hosting resolves both evaluator inputs from the MSBuild-selected watch-host
        # directory stamped on the AppHost, not from an in-repository build layout.
        consumed = pathlib.Path(properties["AspireDotnetWatchHostDir"]) / filename
        require(consumed.resolve() == (dotnet_package["root"] / entry).resolve(),
                f"The consumed evaluator input differs from its actual restored asset path: {consumed}")
        comparisons.append(compare_fresh_package_bytes(fixture, consumed, "Aspire.Hosting.Dotnet", entry))
    targets = [
        entry for kind in ("build", "buildTransitive")
        for entry in dotnet_package["target"].get(kind, {})
        if pathlib.PurePosixPath(entry).name == "Aspire.Hosting.Dotnet.targets"
    ]
    require(len(targets) == 1, f"The active assets target has no unique Hosting.Dotnet build target: {targets}")
    comparisons.append(compare_fresh_package_bytes(
        fixture, dotnet_package["root"] / targets[0], "Aspire.Hosting.Dotnet", targets[0]))
    runtime = read_json(pathlib.Path(descriptor["ToolPath"]).with_suffix(".runtimeconfig.json"))
    frameworks = runtime["runtimeOptions"].get("frameworks") or [runtime["runtimeOptions"]["framework"]]
    require(all(framework["version"].split(".")[0] == "10" for framework in frameworks),
            f"The actual bundled runtime contract is not .NET 10: {runtime}")

    relative = dict(fixture.environment)
    relative["ASPIRE_DOTNET_WATCH_TOOL_PATH"] = os.path.relpath(descriptor["ToolPath"], fixture.apphost.parent)
    require(query_payload(fixture, environment=relative)["ToolPath"] == descriptor["ToolPath"],
            "Relative explicit overrides were not resolved against the AppHost.")
    invalid = dict(fixture.environment)
    invalid["ASPIRE_DOTNET_WATCH_TOOL_PATH"] = "missing-explicit-watch-tool.dll"
    failed = query_payload(fixture, environment=invalid, success=False)
    require(failed.returncode != 0 and "missing-explicit-watch-tool.dll" in failed.stdout + failed.stderr,
            "An invalid explicit tool override silently fell back to the bundled default.")
    # Ordinary builds must not attach runtime work to Resolve/Restore/Build hooks.
    fixture.dotnet_command("build", fixture.apphost, "--no-restore", "--nologo", "-v:quiet", environment=invalid)
    for package_name, consumed_package in consumed_packages.items():
        for output, entry in consumed_package["outputs"]:
            comparisons.append(compare_fresh_package_bytes(fixture, output, package_name, entry))
    require(watcher_install_cache_entries(fixture) == before, "Ordinary Build acquired a watch runtime tool.")
    context.evidence.check("actual-packed-resolve-target-complete-payload-and-authoritative-overrides",
                          descriptor=descriptor, frameworks=frameworks, consumedAssets=comparisons)

    fixture.start()
    fixture.response()
    fixture.application_snapshot()
    _, server = fixture.server_for("Api/Api.csproj")
    require(descriptor["ToolPath"] in server.arguments and descriptor["WatchHostPath"] in server.arguments,
            f"Assembled services did not execute the resolved bundled payload: {server.arguments}")
    proxies = [
        process for process in fixture.capture_generation()
        if descriptor["WatchHostPath"] in process.arguments and "proxy" in process.arguments
    ]
    require(proxies, "No actual bundled WatchHost proxy was launched.")
    require(watcher_install_cache_entries(fixture) == before, "An assembled watch run acquired a runtime tool.")
    fixture.stop()
    context.evidence.check("actual-bundled-server-proxy-and-no-runtime-acquisition",
                          server=server.evidence(), proxies=[proxy.evidence() for proxy in proxies])


def verify_native(context):
    fixture = Fixture(context, "native-v2-reduced-coverage", language="typescript")
    initialize_typescript(fixture, native=True)
    fixture.service()
    fixture.dotnet_command("build", fixture.root / "Api" / "Api.csproj", "--nologo", "-v:quiet")
    typescript_model(fixture, f"""
await builder.addExecutable('api', {json.dumps(fixture.dotnet)}, {json.dumps(str(fixture.root / "Api"))})
    .withArgs('exec', './bin/Debug/net10.0/Api.dll')
    .withHttpEndpoint({{ env: 'ASPNETCORE_HTTP_PORTS' }})
    .withEnvironment('{RUN_TAG}', '{fixture.tag}')
    .withEnvironment('WATCH_MODEL_VALUE', 'MODEL_VALUE');
""")
    warning = "AppHost watching is unavailable for the selected native AppHost server."

    def native_server(expect_warning):
        require(fixture.config["features"]["appHostServerV2"] is True, "The fixture did not select native v2.")
        require(not fixture.servers(), "Native v2 was silently replaced by a managed service-watch server.")
        processes = fixture.capture_generation()
        servers = [
            process for process in processes
            if process.arguments and pathlib.Path(process.arguments[0]).name == "aspire-apphost-server"
        ]
        require(len(servers) == 1, f"The selected native v2 backend was not actually running: {servers}")
        require(not any("aspire-managed" in pathlib.Path(argument).name
                        for process in processes for argument in process.arguments),
                "Native v2 selected a managed hosting server instead of preserving the requested backend.")
        log = " ".join((fixture.root / "watch-run.log").read_text().split())
        require(log.count(warning) == (1 if expect_warning else 0),
                "Native watch coverage did not produce exactly one warning only for an effective watch request.")
        if expect_warning:
            require("stop and rerun Aspire to apply AppHost edits" in log
                    and "Managed C# service watching is also unavailable" in log,
                    "The native coverage warning did not explain normal-run behavior and manual restart.")
        return servers[0]

    fixture.start(watch=True, detached=False)
    first = fixture.response()
    first_server = native_server(expect_warning=True)
    replace_once(fixture.apphost, "'WATCH_MODEL_VALUE', 'one'", "'WATCH_MODEL_VALUE', 'two'")

    def unchanged_model():
        value = request_json(fixture.endpoint())
        verify_same_application(first, value, "Native v2 normal run after an AppHost edit")
        return value["configured"] == "one"

    stays_true(unchanged_model, "observing the documented native normal-run limitation")
    fixture.stop()
    context.evidence.check("native-v2-explicit-watch-warns-and-runs-without-apphost-watching",
                          response=first, nativeServer=first_server.evidence())

    fixture.config["features"]["defaultWatchEnabled"] = True
    fixture.save_config()
    fixture.start(watch=None, detached=False)
    updated = fixture.response("api", lambda value: value["configured"] == "two")
    verify_new_application(first, updated, "Explicitly restarting the native v2 run after an AppHost edit")
    replacement_server = native_server(expect_warning=True)
    require(first_server != replacement_server, "Explicit run restart reused the departed native server.")
    fixture.stop()
    context.evidence.check("native-v2-default-watch-warns-and-manual-restart-picks-up-model-edits",
                          response=updated, nativeServer=replacement_server.evidence())

    replace_once(fixture.apphost, "'WATCH_MODEL_VALUE', 'two'", "'WATCH_MODEL_VALUE', 'three'")
    fixture.start(watch=False, detached=False)
    ordinary = fixture.response("api", lambda value: value["configured"] == "three")
    verify_new_application(updated, ordinary, "Native v2 explicit false overrides default watch")
    ordinary_server = native_server(expect_warning=False)
    fixture.stop()
    context.evidence.check("native-v2-explicit-false-retains-backend-without-watch-unavailable-warning",
                          response=ordinary, nativeServer=ordinary_server.evidence())


def policy_model(fixture, default, choices, value):
    if fixture.language == "csharp":
        fixture.csharp_model(
            fixture.registration("api", "Api/Api.csproj", choices)
            + fixture.registration("plain", "Plain/Plain.csproj"), value=value)
        if default is not None:
            replace_once(fixture.apphost.parent / "AppHost.cs", "DistributedApplication.CreateBuilder(args)",
                         "DistributedApplication.CreateBuilder(new DistributedApplicationOptions "
                         f"{{ Args = args, DefaultResourceWatchEnabled = {str(default).lower()} }})")
    else:
        typescript_model(
            fixture,
            typescript_registration(fixture, "api", "Api/Api.csproj", choices)
            + typescript_registration(fixture, "plain", "Plain/Plain.csproj"), value=value)
        if default is not None:
            replace_once(fixture.apphost, "createBuilder()",
                         f"createBuilder({{ defaultResourceWatchEnabled: {str(default).lower()} }})")


def verify_policy(context):
    for language in ("csharp", "typescript"):
        fixture = Fixture(context, f"resource-policy-{language}", language=language)
        if language == "typescript":
            initialize_typescript(fixture)
        fixture.service("Api")
        fixture.service("Plain")
        policy_model(fixture, None, "", "default")
        fixture.start()
        first = fixture.response()
        fixture.response("plain")
        fixture.application_snapshot()
        host = fixture.host_info()
        changed = hot_reload(fixture, fixture.root / "Api" / "Program.cs",
                             '=> "before"', '=> "plain-default-edit"', ["api"], expected="plain-default-edit")
        require(fixture.host_info()["appHostPid"] == host["appHostPid"],
                "A service edit reconstructed an AppHost with AppHost watching disabled.")
        fixture.stop()
        context.evidence.check(f"{language}-plain-run-default-watch-without-apphost-reconstruction", response=changed)

        fixture.environment[WATCH_ENABLED] = "true"
        choices = ".WithWatch(false).WithWatch(true)" if language == "csharp" else ".withWatch({ enabled: false }).withWatch({ enabled: true })"
        policy_model(fixture, False, choices, "code-off")
        fixture.start(watch=False)
        fixture.response("api", lambda value: value["configured"] == "code-off")
        fixture.response("plain")
        fixture.application_snapshot()
        require(fixture.resource("plain")["properties"].get("resource.watch.state") == "excluded",
                "Configuration true overrode the explicit code default false.")
        fixture.command_state("plain", "rebuild", "Enabled")
        fixture.stop()
        context.evidence.check(f"{language}-code-first-disabled-default-and-required-resource")

        fixture.environment[WATCH_ENABLED] = "false"
        choices = ".WithWatch(true).WithWatch(false)" if language == "csharp" else ".withWatch({ enabled: true }).withWatch({ enabled: false })"
        policy_model(fixture, True, choices, "code-on")
        fixture.start(watch=False)
        fixture.response("api", lambda value: value["configured"] == "code-on")
        fixture.response("plain")
        require(fixture.resource("api")["properties"].get("resource.watch.state") == "excluded",
                "The last explicit resource opt-out was ignored.")
        fixture.application_snapshot("plain")
        fixture.stop()
        context.evidence.check(f"{language}-code-first-enabled-default-and-last-resource-opt-out")

        fixture.environment[WATCH_ENABLED] = "bogus"
        choices = ".WithWatch(true)" if language == "csharp" else ".withWatch({ enabled: true })"
        policy_model(fixture, None, choices, "invalid")
        fixture.start(watch=False, detached=False)
        fixture.response("api", lambda value: value["configured"] == "invalid")
        fixture.response("plain")
        fixture.application_snapshot()
        require(fixture.resource("plain")["properties"].get("resource.watch.state") == "excluded",
                "Invalid configuration did not disable the resolved default.")
        warning = "AppHost:Run:WatchEnabled must be true or false."
        wait_for(lambda: warning in " ".join((fixture.root / "watch-run.log").read_text().split()),
                 "waiting for the authored invalid-default warning")
        require(" ".join((fixture.root / "watch-run.log").read_text().split()).count(warning) == 1,
                "Invalid configuration did not produce exactly one terminal warning.")
        fixture.stop()
        context.evidence.check(f"{language}-invalid-default-warns-once-and-preserves-resource-opt-in")

        # The preceding C# build contains an explicit resource requirement. --no-build must not
        # silently downgrade it; TypeScript executes the same requirement directly from its source.
        failed = fixture.start(watch=None, detached=False, no_build=True, success=False)
        require(failed.wait(timeout=120) != 0, "Required resource watch silently survived --no-build.")
        fixture.foreground = None
        require("--no-build" in (fixture.root / "watch-owner.log").read_text(),
                "Required resource-watch conflict was not diagnosed.")
        fixture.wait_cleanup(fixture.tracked | fixture.capture_generation())

        fixture.environment.pop(WATCH_ENABLED)
        fixture.config["features"]["defaultWatchEnabled"] = True
        fixture.save_config()
        policy_model(fixture, None, "", "no-build")
        if language == "csharp":
            fixture.dotnet_command("build", fixture.apphost, "--nologo", "-v:quiet")
        fixture.start(watch=None, no_build=True)
        fixture.response("api", lambda value: value["configured"] == "no-build")
        fixture.response("plain")
        require(not fixture.servers(), "--no-build did not suppress implicit resource watching.")
        require(fixture.resource("api")["properties"].get("resource.watch.state") == "excluded",
                "--no-build coverage did not describe ordinary execution.")
        fixture.stop()
        conflict = fixture.cli("run", "--no-build", "--watch", success=False)
        require(conflict.returncode != 0 and "explicit AppHost watching" in conflict.stdout + conflict.stderr,
                "Explicit AppHost watching did not conflict clearly with --no-build.")
        context.evidence.check(f"{language}-no-build-suppression-and-explicit-conflicts")


def assert_terminal_boundary(fixture, canaries=()):
    transcript = fixture.terminal_text()
    # Watcher console records start with "dotnet watch ..."; compiler records include
    # "<path>(line,column): error CSxxxx" or "error MSBxxxx". Explicit canaries also
    # catch exception/collector/notice paths that might otherwise strip those prefixes.
    raw = [
        line for line in transcript.splitlines()
        if re.search(r"^\s*dotnet watch\b|(?:error|warning)\s+(?:CS|MSB)\d{4}\b", line, re.IGNORECASE)
        or any(canary in line for canary in canaries)
    ]
    require(len(raw) == 0, f"Expected zero raw watcher-origin terminal lines, observed {len(raw)}: {raw}")
    return {"rawWatcherLines": len(raw), "terminal": str(fixture.root / "watch-run.log")}


def verify_terminal(context):
    fixture = Fixture(context, "foreground-resource-terminal")
    fixture.service()
    fixture.csharp_model(fixture.registration("api", "Api/Api.csproj"))
    source = fixture.root / "Api" / "Program.cs"
    original = source.read_text()
    canaries = ("INITIAL_RESOURCE_BOUNDARY_CANARY", "EDIT_RESOURCE_BOUNDARY_CANARY")
    write(source, "#error INITIAL_RESOURCE_BOUNDARY_CANARY\n" + original)
    fixture.start(watch=False, detached=False, interactive=True)
    wait_for(lambda: fixture.resource("api", retry=True)["properties"].get("resource.watch.state") == "build-failed",
             "waiting for structured initial build-failed coverage")
    owner_snapshot, _ = fixture.server_for("Api/Api.csproj")
    log_name = owner_snapshot.get("displayName", owner_snapshot["name"])
    wait_for(lambda: structured_log_has(fixture, log_name, canaries[0]),
             "waiting for the preserved initial compiler diagnostic")
    wait_for(lambda: "Initial build failed" in fixture.terminal_text(),
             "waiting for positive authored build-failure UX")
    assert_terminal_boundary(fixture, canaries)

    write(source, original)
    first = fixture.response()
    fixture.application_snapshot()
    require("Resource watching (default on)" in " ".join(fixture.terminal_text().split()),
            "The real terminal did not report independent resource-watch policy/coverage.")
    hot_reload(fixture, source, '=> "before"', '=> "terminal-after"', ["api"], expected="terminal-after")
    working = fixture.response()
    write(source, "#error EDIT_RESOURCE_BOUNDARY_CANARY\n" + source.read_text())
    wait_for(lambda: structured_log_has(fixture, log_name, canaries[1]),
             "waiting for preserved session compiler diagnostics")
    verify_same_application(working, fixture.response(), "A compile error cannot launch stale or replacement output")
    assert_terminal_boundary(fixture, canaries)
    replace_once(source, "#error EDIT_RESOURCE_BOUNDARY_CANARY\n", "")
    recovered = hot_reload(fixture, source, '=> "terminal-after"', '=> "terminal-recovered"',
                           ["api"], expected="terminal-recovered")["api"]
    verify_same_application(first, recovered, "Source recovery after a session compile error")

    replace_once(source, "public class ValueSource\n", "public class ValueSource : ReplacementBase\n")
    restarted = fixture.response("api", lambda value: value["instance"] != recovered["instance"])
    verify_new_application(recovered, restarted, "A real rude edit restarts the affected application")
    _, owner = fixture.server_for("Api/Api.csproj")
    fixture.capture_generation()
    fixture.kill_watch_host(owner)
    wait_for(lambda: fixture.resource("api", retry=True)["properties"].get("resource.watch.state") == "failed",
             "waiting for typed infrastructure-failed coverage")
    wait_for(lambda: "Fix the cause and restart the complete Aspire run" in " ".join(fixture.terminal_text().split()),
             "waiting for authored watch-group failure/recovery guidance")
    assert_terminal_boundary(fixture, canaries)
    fixture.ctrl_c()
    context.evidence.check("real-foreground-terminal-source-failures-recovery-rude-edit-owner-failure-and-ctrl-c",
                          **assert_terminal_boundary(fixture, canaries),
                          orphanProcesses=len(fixture.owned_processes()),
                          leakedRunDirectories=sum(
                              directory.exists() and directory not in fixture.expected_preservations
                              for directory in fixture.run_directories),
                          intentionallyPreservedRunDirectories=fixture.assert_run_directories_clean())

    for kind in ("early-exit", "backchannel-timeout"):
        failed = Fixture(context, f"foreground-apphost-{kind}")
        failed.service()
        failed.csharp_model(failed.registration("api", "Api/Api.csproj"))
        host_source = failed.apphost.parent / "AppHost.cs"
        if kind == "early-exit":
            replace_once(host_source, "ShouldFail() => false", "ShouldFail() => true")
        else:
            replace_once(host_source, "var builder = DistributedApplication.CreateBuilder(args);",
                         'Environment.SetEnvironmentVariable("ASPIRE_BACKCHANNEL_PATH", null);\n'
                         "var builder = DistributedApplication.CreateBuilder(args);")
        failed.environment["ASPIRE_CLI_START_TIMEOUT"] = "15"
        run = failed.start(watch=True, detached=False, interactive=True, success=False)
        require(run.wait(timeout=150) != 0, f"{kind} returned a success-shaped exit.")
        failed.foreground = None
        failed.finish_terminal()
        failed.wait_cleanup(failed.tracked | failed.capture_generation())
        require("logs" in failed.terminal_text().lower(), f"{kind} omitted exact diagnostic-log guidance.")
        if kind == "early-exit":
            require("WATCH_TEST_MODEL_FAILURE" in (failed.root / "watch-owner.log").read_text(),
                    "Full early-exit exception diagnostics were not retained.")
        context.evidence.check(f"real-foreground-apphost-{kind}-safe-error-and-cleanup",
                              **assert_terminal_boundary(failed, ("WATCH_TEST_MODEL_FAILURE",)),
                              orphanProcesses=len(failed.owned_processes()))

def process_usage(identity):
    if not identity.alive():
        return None
    try:
        # /proc/<pid>/stat fields 14/15 are user/system CPU ticks; VmRSS is in KiB.
        stat = (pathlib.Path("/proc") / str(identity.pid) / "stat").read_text().rsplit(")", 1)[1].split()
        status = (pathlib.Path("/proc") / str(identity.pid) / "status").read_text()
        rss = re.search(r"^VmRSS:\s+(\d+)\s+kB$", status, re.MULTILINE)
        return {
            "cpuSeconds": (int(stat[11]) + int(stat[12])) / os.sysconf("SC_CLK_TCK"),
            "residentBytes": int(rss.group(1)) * 1024 if rss else 0,
        }
    except (FileNotFoundError, ProcessLookupError):
        return None


def verify_startup(context):
    observations = []
    for workload in ("shared-references", "independent-sdk-scopes", "all-excluded"):
        selections = [(False, False), (True, False)]
        if workload == "shared-references":
            selections.append((True, True))
        for enabled, apphost_watching in selections:
            selection = "apphost-on" if apphost_watching else "on" if enabled else "off"
            fixture = Fixture(context, f"startup-{workload}-{selection}")
            # Give each mode a fresh package cache so the first sample is genuinely cold for that mode.
            fixture.environment["NUGET_PACKAGES"] = str(fixture.root / "nuget-cache")
            fixture.environment[WATCH_ENABLED] = str(enabled).lower()
            if workload == "shared-references":
                fixture.shared()
                projects = [fixture.service("Api", shared=True), fixture.service("Worker", shared=True)]
                registrations = fixture.registration("api", "Api/Api.csproj") + fixture.registration("worker", "Worker/Worker.csproj")
                build_projects = [*projects, fixture.root / "Shared" / "Shared.csproj"]
            elif workload == "independent-sdk-scopes":
                projects = [fixture.service("Api"), fixture.service("Independent/Worker")]
                fixture.pin_sdk(fixture.root / "Independent")
                registrations = fixture.registration("api", "Api/Api.csproj") + fixture.registration("worker", "Independent/Worker/Worker.csproj")
                build_projects = projects
            else:
                projects = [fixture.service("Api"), fixture.service("Worker")]
                registrations = (
                    fixture.registration("api", "Api/Api.csproj", ".WithWatch(false)")
                    + fixture.registration("worker", "Worker/Worker.csproj", ".WithWatch(false)")
                )
                build_projects = projects
            for project in build_projects:
                add_build_counter(fixture, project)
            fixture.csharp_model(registrations)
            for iteration in range(3):
                before_builds = {str(project.relative_to(fixture.root)): build_attempts(project) for project in build_projects}
                started = time.monotonic()
                fixture.start(watch=apphost_watching, detached=False)
                fixture.response("api")
                fixture.response("worker")
                health_seconds = time.monotonic() - started
                processes = fixture.owned_processes()
                usage = [value for identity in processes if (value := process_usage(identity)) is not None]
                groups = len(fixture.servers())
                expected_groups = 0 if not enabled or workload == "all-excluded" else (
                    2 if workload == "independent-sdk-scopes" else 1)
                require(groups == expected_groups, f"Startup sample selected {groups} watcher groups, expected {expected_groups}.")
                observation = {
                    "workload": workload,
                    "resourceDefaultEnabled": enabled,
                    "appHostWatching": apphost_watching,
                    "iteration": iteration,
                    "cache": "cold packages and outputs" if iteration == 0 else "warm packages and outputs",
                    "startupToApplicationHealthSeconds": health_seconds,
                    "watcherGroups": groups,
                    "ownedProcessCount": len(processes),
                    "cpuSecondsAtHealth": sum(value["cpuSeconds"] for value in usage),
                    "residentBytesAtHealth": sum(value["residentBytes"] for value in usage),
                    "compileAttemptsByProject": {
                        str(project.relative_to(fixture.root)): build_attempts(project)
                        - before_builds[str(project.relative_to(fixture.root))]
                        for project in build_projects
                    },
                }
                fixture.stop()
                observations.append(observation)
                context.evidence.check("startup-application-health-and-resource-usage", **observation)

            # Capture a separate trace, not a wall-clock benchmark with collector overhead mixed in.
            archive = fixture.root / "startup-profile.zip"
            profile_run = fixture.start(watch=apphost_watching, detached=False, profile_path=archive)
            fixture.response("api")
            fixture.response("worker")
            fixture.capture_generation()
            # Self-profile stops the wrapped run after its capture delay and then exports.
            # An external `aspire stop` kills the owning CLI before that export can finish.
            require(profile_run.wait(timeout=180) == 0,
                    f"The profiling owner failed; see {fixture.root / 'watch-run.log'}.")
            fixture.stop()
            require(archive.is_file(), "The profiling command did not export its trace archive.")
            with zipfile.ZipFile(archive) as exported:
                profile = json.loads(exported.read("traces/profile.json"))
            spans = [
                span for resource in profile.get("resourceSpans", [])
                for scope in resource.get("scopeSpans", [])
                for span in scope.get("spans", [])
            ]
            evaluations = [span for span in spans if span.get("name") == "aspire/hosting/dotnet-watch/evaluate"]
            inventories = [span for span in spans if span.get("name") == "aspire/hosting/dotnet-watch/observe-build-inputs"]
            require(not enabled or workload == "all-excluded" or evaluations,
                    "The profiled watched startup omitted graph-evaluation spans.")
            # A successful initial restore rearms observation with newly evaluated NuGet imports.
            # These spans count initialization work, not concurrently active observers.
            require(bool(inventories) == bool(expected_groups),
                    "The profile omitted watched input initialization or recorded it for an unwatched workload.")
            for inventory in inventories:
                # Dashboard exports normalize attributes to strings, including JSON arrays:
                # {"stringValue":"[\"/path/project.csproj\",\"/path/import.targets\"]"}.
                # Counts are also exported as {"stringValue":"12"}, not OTLP intValue.
                attributes = {attribute["key"]: attribute["value"] for attribute in inventory["attributes"]}
                files = json.loads(attributes["aspire.dotnet_watch.input_files"]["stringValue"])
                require(isinstance(files, list) and all(isinstance(path, str) for path in files),
                        "The profiled input inventory was not an encoded string array.")
                require(int(attributes["aspire.dotnet_watch.input_count"]["stringValue"]) == len(files) > 0,
                        "The profiled input inventory count did not match its actual finite file set.")
                require(len(set(files)) == len(files) and all(pathlib.Path(path).is_absolute() for path in files),
                        "The profiled input inventory contained duplicate or non-physical paths.")
            context.evidence.check("startup-profile-and-exact-graph-evaluation-count",
                                  workload=workload, resourceDefaultEnabled=enabled,
                                  appHostWatching=apphost_watching,
                                  graphEvaluations=len(evaluations),
                                  inputObservationInitializations=len(inventories),
                                  inputObservationSpans=inventories,
                                  profile=str(pathlib.Path(fixture.root.name) / archive.name))
    context.evidence.values["startupSamples"] = observations
    context.evidence.save()


SCENARIOS = {
    "csharp": verify_csharp,
    "typescript": verify_typescript,
    "replicas": verify_replicas,
    "builds": verify_builds,
    "compatibility": verify_compatibility,
    "isolation": verify_isolation,
    "https": verify_https,
    "payload": verify_payload,
    "native": verify_native,
    "policy": verify_policy,
    "terminal": verify_terminal,
    "startup": verify_startup,
}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("scenario", choices=SCENARIOS)
    parser.add_argument("--workspace", type=pathlib.Path,
                        help="Fresh directory below cwd; omitted only when copied into a test workspace.")
    parser.add_argument("--packages", help="Fresh locally built Aspire package hive (otherwise ASPIRE_CLI_PACKAGES).")
    parser.add_argument("--nuget-config", type=pathlib.Path,
                        help="Approved feeds/mappings; otherwise the copied watch-feeds.config or WATCH_NUGET_CONFIG.")
    arguments = parser.parse_args()
    require(sys.platform == "linux", "These assembled scenarios require Linux and /proc process identities.")
    cwd = pathlib.Path.cwd().resolve()
    root = arguments.workspace.resolve() if arguments.workspace else cwd
    require(root.is_relative_to(cwd), "The fixture workspace must be below the current working directory.")
    require(root != cwd or (cwd / "verify-watch.py").is_file(), "Pass --workspace when invoking the source asset directly.")
    require(not (root / "Aspire.slnx").exists() and not (root / ".git").exists(),
            "Refusing to generate fixtures in a repository root.")
    root.mkdir(parents=True, exist_ok=True)
    feeds = arguments.nuget_config
    if feeds is None:
        copied = root / "watch-feeds.config"
        configured = os.environ.get("WATCH_NUGET_CONFIG")
        feeds = copied if copied.is_file() else pathlib.Path(configured) if configured else None
    if feeds is not None:
        require(feeds.is_file(), f"Approved NuGet configuration is absent: {feeds}")
    context = None
    succeeded = False
    try:
        context = Context(root, arguments.scenario, arguments.packages, feeds)
        SCENARIOS[arguments.scenario](context)
        for fixture in context.fixtures:
            require(not fixture.owned_processes(), f"Task-owned processes survived final cleanup in {fixture.root}.")
            preserved = fixture.assert_run_directories_clean()
            if preserved:
                context.evidence.check("unacknowledged-hard-kill-preserves-private-directory-with-zero-live-processes",
                                      directories=preserved, orphanProcesses=0)
        succeeded = True
    finally:
        if context is not None:
            for fixture in context.fixtures:
                if not succeeded:
                    fixture.diagnostics()
                    try:
                        fixture.cleanup_after_failure()
                    except (AssertionError, OSError, subprocess.TimeoutExpired) as error:
                        context.evidence.values.setdefault("cleanupFailures", []).append(str(error))
                        print(f"Owned fixture cleanup failed: {error}", file=sys.stderr)
                fixture.close()
                fixture.preserve_artifacts()
            context.evidence.save()
        permissions = subprocess.run(
            ["chmod", "-R", "a+rwX", str(root)],
            capture_output=True,
            text=True,
            timeout=30,
        )
        if permissions.returncode != 0:
            print(f"Could not make preserved diagnostics host-readable: {permissions.stderr}", file=sys.stderr)
    context.evidence.values["passed"] = True
    context.evidence.save()
    print(f"WATCH_E2E_OK:{arguments.scenario}", flush=True)


if __name__ == "__main__":
    main()
