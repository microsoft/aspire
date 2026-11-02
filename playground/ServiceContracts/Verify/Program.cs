// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;

if (args.Contains("--help"))
{
    Console.WriteLine("Run from the Aspire root: ./dotnet.sh run --project playground/ServiceContracts/Verify -- --workspace <new-directory> [--mode all|source|container] [--prepare-only]");
    return;
}

string? workspaceArgument = null;
var mode = "all";
var prepareOnly = false;
for (var index = 0; index < args.Length; index++)
{
    switch (args[index])
    {
        case "--workspace" when index + 1 < args.Length:
            workspaceArgument = args[++index];
            break;
        case "--mode" when index + 1 < args.Length:
            mode = args[++index];
            break;
        case "--prepare-only":
            prepareOnly = true;
            break;
        default:
            throw new ArgumentException($"Unknown or incomplete argument '{args[index]}'. Use --help.");
    }
}
if (workspaceArgument is null || mode is not ("all" or "source" or "container"))
{
    throw new ArgumentException("Supply --workspace <new-directory> and --mode all|source|container.");
}
if (!OperatingSystem.IsLinux() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64)
{
    throw new PlatformNotSupportedException("This runtime experiment requires Linux x64.");
}

// The standalone runner's output lives under Verify/bin/<configuration>/net11.0.
var here = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
var aspire = Path.GetFullPath(Path.Combine(here, "../.."));
var workspace = Path.GetFullPath(workspaceArgument);
if (Path.Exists(workspace) || workspace == aspire ||
    workspace.StartsWith(aspire + Path.DirectorySeparatorChar, StringComparison.Ordinal))
{
    throw new ArgumentException("Workspace must be a new directory outside the Aspire checkout.");
}
var dotnet = Path.Combine(aspire, "dotnet.sh");
var producer = Path.Combine(workspace, "producer");
var consumer = Path.Combine(workspace, "consumer");
var artifact = Path.Combine(consumer, "contracts/catalog.json");
var producerContract = Path.Combine(producer, "aspire-service.json");
var offline = Path.Combine(workspace, "producer-offline");
var relocated = Path.Combine(producer, "src/Catalog.Renamed");
var originalProjectDirectory = Path.Combine(producer, "src/Catalog.Api");

Directory.CreateDirectory(workspace);
foreach (var name in new[] { "producer", "consumer" })
{
    var target = Path.Combine(workspace, name);
    CopyDirectory(Path.Combine(here, name), target);
    await Run("git", ["init", "--quiet"], target);
    if (name == "consumer")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        File.Copy(producerContract, artifact);
    }
    await Run("git", ["add", "."], target);
    await Run("git", ["-c", "user.name=Contract fixture", "-c", "user.email=fixture@example.invalid",
        "commit", "--quiet", "-m",
        "Initialize independently owned fixture\n\nCo-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"], target);
}
if (prepareOnly)
{
    Console.WriteLine($"Independent repositories ready in {workspace}");
    return;
}

Environment.SetEnvironmentVariable("AspireRepoRoot", aspire);
string? runtime = null;
foreach (var candidate in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
    .Select(directory => Path.Combine(directory, "dotnet")).Where(File.Exists).Distinct())
{
    var available = await Run(candidate, ["--list-runtimes"], aspire, capture: true);
    if (available.Output.Contains("Microsoft.NETCore.App 10.", StringComparison.Ordinal) &&
        available.Output.Contains("Microsoft.AspNetCore.App 10.", StringComparison.Ordinal))
    {
        runtime = File.ResolveLinkTarget(candidate, returnFinalTarget: true)?.FullName ?? Path.GetFullPath(candidate);
        break;
    }
}
if (runtime is null)
{
    throw new InvalidOperationException("Install the .NET 10 SDK (including ASP.NET Core runtime) and put dotnet on PATH.");
}
var runtimeRoot = Path.GetDirectoryName(runtime)!;
var support = Path.Combine(aspire, "src/Aspire.Hosting.AppHost/Aspire.Hosting.AppHost.csproj");
await Run(dotnet, ["restore", support, "--disable-parallel", "-p:NuGetAudit=false", "-v:q"], aspire);
var dashboardProject = Path.Combine(aspire, "src/Aspire.Dashboard/Aspire.Dashboard.csproj");
await Run(dotnet, ["restore", dashboardProject, "--disable-parallel", "-p:NuGetAudit=false", "-v:q"], aspire);
await Run(dotnet, ["build", dashboardProject, "--no-restore", "-v:q"], aspire);
var dashboard = Path.Combine(aspire, "artifacts/bin/Aspire.Dashboard/Debug/net11.0/Aspire.Dashboard.dll");
if (!File.Exists(dashboard))
{
    throw new FileNotFoundException("Dashboard build did not produce the required artifact.", dashboard);
}
var propertiesResult = await Run(dotnet, ["msbuild", support, "-nologo",
    "-getProperty:NuGetPackageRoot,MicrosoftDeveloperControlPlanedarwinamd64Version"], aspire, capture: true);
var properties = JsonNode.Parse(propertiesResult.Output)!["Properties"]!;
var version = properties["MicrosoftDeveloperControlPlanedarwinamd64Version"]!.GetValue<string>();
var dcp = Path.Combine(properties["NuGetPackageRoot"]!.GetValue<string>(),
    $"microsoft.developercontrolplane.linux-amd64/{version}/tools/dcp");
if (!File.Exists(dcp))
{
    throw new FileNotFoundException("The repository-pinned Linux DCP was not restored.", dcp);
}

var originalText = await File.ReadAllTextAsync(artifact);
var originalContract = JsonNode.Parse(originalText)!;
try
{
    await Build(Path.Combine(consumer, "AppHost/Consumer.AppHost.csproj"));
    await Build(Path.Combine(consumer, "Storefront/Storefront.csproj"));
    if (mode is "all" or "source")
    {
        var project = Path.Combine(producer, originalContract["project"]!.GetValue<string>());
        await Build(project);
        await Request("source", producer);
        await Request("source-missing-parameter", producer, missingKey: true);
        var settings = Path.Combine(originalProjectDirectory, "appsettings.json");
        await File.WriteAllTextAsync(settings, $$"""
            { "Kestrel": { "Endpoints": { "http": { "Url": "http://127.0.0.1:{{FreePort()}}" } } } }
            """);
        try
        {
            await Build(project);
            await Request("source-kestrel", producer);
        }
        finally
        {
            File.Delete(settings);
            // Web SDK builds do not prune removed content from an existing output.
            File.Delete(Path.Combine(originalProjectDirectory, "bin/Debug/net10.0/appsettings.json"));
        }
        Directory.Move(originalProjectDirectory, relocated);
        var evolved = originalContract.DeepClone();
        evolved["version"] = "1.1.0";
        evolved["project"] = "src/Catalog.Renamed/Catalog.Api.csproj";
        await File.WriteAllTextAsync(producerContract, evolved.ToJsonString());
        await Build(Path.Combine(producer, evolved["project"]!.GetValue<string>()));
        await Request("source-evolved", producer);
        evolved["version"] = "2.0.0";
        await File.WriteAllTextAsync(producerContract, evolved.ToJsonString());
        await Rejects("source-major-mismatch", "expected 'contoso.catalog' major 1", producer);
        Directory.Move(relocated, originalProjectDirectory);
        await File.WriteAllTextAsync(producerContract, originalText);
    }

    var changed = originalContract.DeepClone();
    changed["id"] = "contoso.other";
    await RejectArtifact("artifact-identity-mismatch", changed, "expected 'contoso.catalog' major 1");
    changed = originalContract.DeepClone();
    changed["schemaVersion"] = 2;
    await RejectArtifact("artifact-schema-mismatch", changed, "schemaVersion must be 1");
    changed = originalContract.DeepClone();
    changed["inputs"]!.AsArray().Add(new JsonObject { ["name"] = "tenant", ["environmentVariable"] = "CATALOG_TENANT" });
    await RejectArtifact("missing-binding", changed, "Missing: [tenant]");
    changed = originalContract.DeepClone();
    changed["inputs"]!.AsArray().RemoveAt(1);
    await RejectArtifact("unexpected-binding", changed, "Unexpected: [api-key]");
    await File.WriteAllTextAsync(artifact, "{");
    await Rejects("invalid-json", "Invalid service contract");
    File.Delete(artifact);
    await Rejects("missing-contract", "Obtain the producer's contract artifact");
    await File.WriteAllTextAsync(artifact, originalText);
    await Rejects("missing-checkout", "select a checkout containing aspire-service.json", Path.Combine(workspace, "absent"));

    if (mode is "all" or "container")
    {
        await Run("docker", ["info", "--format", "{{.ServerVersion}}"], workspace);
        await Run("docker", ["build", "--quiet", "-t", "aspire-contract-catalog:1.0.0", producer], workspace);
        Directory.Move(producer, offline);
        try
        {
            await Request("container-without-source");
            await Request("container-missing-parameter", missingKey: true);
        }
        finally
        {
            Directory.Move(offline, producer);
        }
    }
}
finally
{
    if (Directory.Exists(offline))
    {
        Directory.Move(offline, producer);
    }
    if (Directory.Exists(relocated))
    {
        Directory.Move(relocated, originalProjectDirectory);
    }
    await File.WriteAllTextAsync(producerContract, originalText);
    await File.WriteAllTextAsync(artifact, originalText);
}
foreach (var repository in new[] { producer, consumer })
{
    var status = await Run("git", ["status", "--porcelain"], repository, capture: true);
    if (!string.IsNullOrWhiteSpace(status.Output))
    {
        throw new InvalidOperationException($"Verification left fixture changes in {repository}: {status.Output}");
    }
}
Console.WriteLine($"PASS: evidence and independently owned repositories retained in {workspace}");

async Task Build(string project)
{
    await Run(dotnet, ["restore", project, "--disable-parallel", "--configfile", Path.Combine(aspire, "NuGet.config"),
        "-p:NuGetAudit=false", "-v:q"], aspire);
    await Run(dotnet, ["build", project, "--no-restore", "-v:q"], aspire);
}

List<string> HostArguments(string? source, bool missingKey)
{
    List<string> arguments = [Path.Combine(consumer, "AppHost/bin/Debug/net10.0/Consumer.AppHost.dll"),
        "--Parameters:catalog-region", "north"];
    if (!missingKey)
    {
        arguments.AddRange(["--Parameters:catalog-api-key", "fixture-key-not-a-secret"]);
    }
    if (source is not null)
    {
        arguments.AddRange(["--CatalogSource", source]);
    }
    return arguments;
}

async Task Request(string name, string? source = null, bool missingKey = false)
{
    var port = FreePort();
    var arguments = HostArguments(source, missingKey);
    arguments.AddRange(["--DcpPublisher:CliPath", dcp, "--DcpPublisher:DashboardPath", dashboard,
        "--StorefrontPort", port.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
    var logPath = Path.Combine(workspace, $"{name}.log");
    using var log = TextWriter.Synchronized(new StreamWriter(logPath) { AutoFlush = true });
    using var process = Start(runtime, arguments, Path.Combine(consumer, "AppHost"), redirect: true, runtimeRoot);
    var output = Pump(process.StandardOutput, log);
    var error = Pump(process.StandardError, log);
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(120))
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException($"{name}: AppHost exited {process.ExitCode}; see {logPath}");
            }
            var missingReported = false;
            if (missingKey)
            {
                var text = await File.ReadAllTextAsync(logPath);
                missingReported = text.Contains("catalog-api-key", StringComparison.Ordinal) &&
                    (text.Contains("could not be initialized", StringComparison.Ordinal) ||
                     text.Contains("Failed to initialize parameter resource", StringComparison.Ordinal));
            }
            string? payload = null;
            try
            {
                using var response = await http.GetAsync($"http://127.0.0.1:{port}/shop");
                if (response.IsSuccessStatusCode)
                {
                    payload = await response.Content.ReadAsStringAsync();
                }
            }
            catch (HttpRequestException)
            {
                // Connection refusal is expected while readiness gates startup.
            }
            catch (TaskCanceledException)
            {
                // Each HTTP attempt is bounded independently of the overall probe.
            }
            if (payload is not null)
            {
                var expected = JsonNode.Parse("""{"service":"catalog","contractVersion":1,"region":"north","products":["tea","coffee"]}""");
                if (missingKey || !JsonNode.DeepEquals(JsonNode.Parse(payload), expected))
                {
                    throw new InvalidOperationException($"{name}: unexpected successful response: {payload}");
                }
                Console.WriteLine($"PASS {name}: consumer /shop -> producer /products: {payload}");
                return;
            }
            if (missingReported)
            {
                Console.WriteLine($"PASS {name}: missing parameter reported; dependent service cannot start");
                return;
            }
            await Task.Delay(500);
        }
        throw new TimeoutException($"{name}: no expected result within 120 seconds; see {logPath}");
    }
    finally
    {
        await Stop(process);
        await Task.WhenAll(output, error);
    }
}

async Task RejectArtifact(string name, JsonNode contract, string expected)
{
    await File.WriteAllTextAsync(artifact, contract.ToJsonString());
    await Rejects(name, expected);
}

async Task Rejects(string name, string expected, string? source = null)
{
    var result = await Run(runtime, HostArguments(source, missingKey: false), Path.Combine(consumer, "AppHost"),
        capture: true, requireSuccess: false, timeout: TimeSpan.FromSeconds(30), runtimeRoot: runtimeRoot);
    await File.WriteAllTextAsync(Path.Combine(workspace, $"{name}.log"), result.Output);
    if (result.ExitCode == 0 || !result.Output.Contains(expected, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"{name}: expected nonzero exit and '{expected}'; got {result.ExitCode}: {result.Output}");
    }
    Console.WriteLine($"PASS {name}: {expected}");
}

static async Task<(int ExitCode, string Output)> Run(string command, IReadOnlyList<string> arguments, string cwd,
    bool capture = false, bool requireSuccess = true, TimeSpan? timeout = null, string? runtimeRoot = null)
{
    Console.WriteLine($"+ {command} {string.Join(' ', arguments)}");
    using var process = Start(command, arguments, cwd, capture, runtimeRoot);
    var output = capture ? process.StandardOutput.ReadToEndAsync() : Task.FromResult("");
    var error = capture ? process.StandardError.ReadToEndAsync() : Task.FromResult("");
    using var deadline = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(15));
    try
    {
        await process.WaitForExitAsync(deadline.Token);
    }
    catch (OperationCanceledException)
    {
        await Stop(process);
        throw new TimeoutException($"Command timed out: {command} {string.Join(' ', arguments)}");
    }
    var text = await output + await error;
    if (requireSuccess && process.ExitCode != 0)
    {
        throw new InvalidOperationException($"Command exited {process.ExitCode}: {command}\n{text}");
    }
    return (process.ExitCode, text);
}

static Process Start(string command, IReadOnlyList<string> arguments, string cwd, bool redirect, string? runtimeRoot = null)
{
    var info = new ProcessStartInfo(command)
    {
        WorkingDirectory = cwd,
        UseShellExecute = false,
        RedirectStandardOutput = redirect,
        RedirectStandardError = redirect
    };
    foreach (var argument in arguments)
    {
        info.ArgumentList.Add(argument);
    }
    if (runtimeRoot is not null)
    {
        // dotnet.sh prepends its SDK-only host; DCP's .NET 10 children need the
        // installed application runtime rather than the runner's .NET 11 host.
        info.Environment["DOTNET_ROOT"] = runtimeRoot;
        info.Environment["DOTNET_ROOT_X64"] = runtimeRoot;
        info.Environment["DOTNET_HOST_PATH"] = Path.Combine(runtimeRoot, "dotnet");
        info.Environment["PATH"] = runtimeRoot + Path.PathSeparator + info.Environment["PATH"];
    }
    return Process.Start(info) ?? throw new InvalidOperationException($"Unable to start {command}.");
}

static async Task Stop(Process process)
{
    if (process.HasExited)
    {
        return;
    }
    using var signal = Start("/bin/kill", ["-TERM", process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)],
        Directory.GetCurrentDirectory(), redirect: true);
    await signal.WaitForExitAsync();
    if (signal.ExitCode != 0 && !process.HasExited)
    {
        throw new InvalidOperationException($"Failed to terminate process {process.Id}: {await signal.StandardError.ReadToEndAsync()}");
    }
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    try
    {
        await process.WaitForExitAsync(deadline.Token);
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine($"Process {process.Id} did not stop gracefully; killing its process tree.");
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
    }
}

static async Task Pump(StreamReader reader, TextWriter writer)
{
    while (await reader.ReadLineAsync() is { } line)
    {
        await writer.WriteLineAsync(line);
    }
}

static int FreePort()
{
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    return ((IPEndPoint)listener.LocalEndpoint).Port;
}

static void CopyDirectory(string source, string target)
{
    Directory.CreateDirectory(target);
    foreach (var file in Directory.EnumerateFiles(source))
    {
        File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
    }
    foreach (var directory in Directory.EnumerateDirectories(source))
    {
        var name = Path.GetFileName(directory);
        if (name is not ("bin" or "obj" or ".git" or "publish" or ".aspire"))
        {
            CopyDirectory(directory, Path.Combine(target, name));
        }
    }
}
