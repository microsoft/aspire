// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Hex1b.Input;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for Aspire terminal commands.
/// </summary>
public sealed class TerminalCommandTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task TerminalReplicaInstanceId_RoutesAttachAndTapeInputToSelectedProcess()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);
        await auto.AspireNewAsync("TerminalReplicas", counter, template: AspireTemplate.EmptyAppHost);
        await auto.RunCommandAsync("cd TerminalReplicas", counter);
        await auto.RunCommandAsync("dotnet new console --output TerminalProbe --framework net10.0", counter);

        var projectDirectory = Path.Combine(workspace.WorkspaceRoot.FullName, "TerminalReplicas");
        var appHostPath = Path.Combine(projectDirectory, "apphost.cs");
        var appHostSource = File.ReadAllText(appHostPath);
        const string builderDeclaration = "var builder = DistributedApplication.CreateBuilder(args);";
        Assert.Contains(builderDeclaration, appHostSource);
        var header = appHostSource[..appHostSource.IndexOf(builderDeclaration, StringComparison.Ordinal)];
        File.WriteAllText(appHostPath, header + """
            #pragma warning disable ASPIRETERMINAL001

            var builder = DistributedApplication.CreateBuilder(args);
            builder.AddProject("replicated", Path.Combine(builder.AppHostDirectory, "TerminalProbe", "TerminalProbe.csproj"))
                .WithEnvironment("PROBE_DIRECTORY", Path.Combine(builder.AppHostDirectory, "received"))
                .WithReplicas(2)
                .WithTerminal();
            builder.Build().Run();
            """);
        File.WriteAllText(Path.Combine(projectDirectory, "TerminalProbe", "Program.cs"), """
            var directory = Environment.GetEnvironmentVariable("PROBE_DIRECTORY")!;
            Directory.CreateDirectory(directory);
            // DCP reports the "dotnet run" launcher's PID, not this console child's PID.
            // Linux /proc/self/status has a "PPid:\t123" field; use it to correlate
            // the actual input recipient with the independently discovered resource.
            var launcherPid = File.ReadLines("/proc/self/status")
                .Single(line => line.StartsWith("PPid:", StringComparison.Ordinal))["PPid:".Length..].Trim();
            var inputPath = Path.Combine(directory, $"{launcherPid}.input");
            File.WriteAllText(inputPath, "");
            Console.WriteLine($"READY:LAUNCHER:{launcherPid}|PID:{Environment.ProcessId}");
            while (Console.ReadLine() is { } line)
            {
                File.AppendAllText(inputPath, line + Environment.NewLine);
                Console.WriteLine($"ACK:{line}|LAUNCHER:{launcherPid}|PID:{Environment.ProcessId}");
            }
            """);
        File.WriteAllText(Path.Combine(projectDirectory, "probe.tape"), "");

        // Avoid concurrent first builds of the same project from the two "dotnet run" replicas.
        await auto.RunCommandAsync("dotnet build TerminalProbe/TerminalProbe.csproj", counter);
        await auto.AspireStartAsync(counter, isolated: true);
        const string replicasDiscovered = """
            [.resources[] | select(.displayName == "replicated" and .properties["terminal.replicaIndex"] != null and .properties["executable.pid"] != null)] | length == 2
            """;
        await auto.RunCommandAsync(
            $"for attempt in $(seq 1 120); do aspire describe --format json > resources.json && jq -e '{replicasDiscovered}' resources.json > /dev/null && break; sleep 1; done; jq -e '{replicasDiscovered}' resources.json > /dev/null",
            counter, TimeSpan.FromMinutes(3));

        using var discovery = JsonDocument.Parse(File.ReadAllText(Path.Combine(projectDirectory, "resources.json")));
        var replicas = discovery.RootElement.GetProperty("resources").EnumerateArray()
            .Where(resource => resource.GetProperty("displayName").GetString() == "replicated")
            .OrderBy(resource => resource.GetProperty("properties").GetProperty("terminal.replicaIndex").GetString())
            .ToArray();
        Assert.Equal(2, replicas.Length);
        Assert.Equal("0", replicas[0].GetProperty("properties").GetProperty("terminal.replicaIndex").GetString());
        Assert.Equal("1", replicas[1].GetProperty("properties").GetProperty("terminal.replicaIndex").GetString());
        var instanceIds = replicas.Select(resource => resource.GetProperty("name").GetString()!).ToArray();
        foreach (var instanceId in instanceIds)
        {
            await auto.RunCommandAsync($"aspire wait {instanceId} --status up --timeout 120", counter, TimeSpan.FromMinutes(3));
        }
        var otherLauncherPid = replicas[0].GetProperty("properties").GetProperty("executable.pid").GetInt32();
        var selectedLauncherPid = replicas[1].GetProperty("properties").GetProperty("executable.pid").GetInt32();
        Assert.NotEqual(otherLauncherPid, selectedLauncherPid);
        await auto.RunCommandAsync(
            $"timeout 120s bash -c 'until test -f received/{otherLauncherPid}.input && test -f received/{selectedLauncherPid}.input; do sleep 1; done'",
            counter, TimeSpan.FromMinutes(3));
        var availableIds = string.Join(", ", instanceIds.Order(StringComparer.OrdinalIgnoreCase));

        foreach (var options in new[] { "", " --non-interactive" })
        {
            // timeout makes an accidental replica picker fail instead of hanging the test.
            // Neither command receives input; only the expected exit code 1 passes.
            await auto.RunCommandAsync(
                $"timeout 15s aspire terminal attach replicated{options} > attach-ambiguity.log 2>&1; code=$?; cat attach-ambiguity.log; test \"$code\" -eq 1",
                counter, TimeSpan.FromSeconds(30));
            Assert.Contains($"Available instance IDs: {availableIds}.",
                File.ReadAllText(Path.Combine(projectDirectory, "attach-ambiguity.log")));
            await auto.RunCommandAsync(
                $"timeout 15s aspire terminal tape play replicated --tape-file probe.tape{options} > tape-ambiguity.log 2>&1; code=$?; cat tape-ambiguity.log; test \"$code\" -eq 1",
                counter, TimeSpan.FromSeconds(30));
            Assert.Contains($"Available instance IDs: {availableIds}.",
                File.ReadAllText(Path.Combine(projectDirectory, "tape-ambiguity.log")));
        }

        await auto.ClearScreenAsync(counter);
        await auto.TypeAsync($"aspire terminal attach {instanceIds[1]}");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync($"READY:LAUNCHER:{selectedLauncherPid}|", timeout: TimeSpan.FromSeconds(30));
        await auto.TypeAsync("attach-probe");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync($"ACK:attach-probe|LAUNCHER:{selectedLauncherPid}|", timeout: TimeSpan.FromSeconds(30));
        // Match the existing terminal E2E's interactive cadence so the viewer
        // processes the chord prefix before receiving the suffix.
        await auto.Ctrl().KeyAsync(Hex1bKey.B);
        await auto.WaitAsync(1000);
        await auto.KeyAsync(Hex1bKey.D);
        await auto.WaitForSuccessPromptAsync(counter, timeout: TimeSpan.FromSeconds(30));

        var selectedInput = Path.Combine(projectDirectory, "received", $"{selectedLauncherPid}.input");
        var otherInput = Path.Combine(projectDirectory, "received", $"{otherLauncherPid}.input");
        Assert.Equal(["attach-probe"], File.ReadAllLines(selectedInput));
        Assert.Empty(File.ReadAllLines(otherInput));

        File.WriteAllText(Path.Combine(projectDirectory, "probe.tape"), $$"""
            Set TypingSpeed 0
            Set WaitTimeout 20s
            Type "tape-probe"
            Enter
            Wait+Screen /ACK:tape-probe[|]LAUNCHER:{{selectedLauncherPid}}[|]/
            """);
        await auto.RunCommandAsync(
            $"aspire terminal tape play {instanceIds[1]} --tape-file probe.tape --timeout 30",
            counter, TimeSpan.FromSeconds(45));

        Assert.Equal(["attach-probe", "tape-probe"], File.ReadAllLines(selectedInput));
        Assert.Empty(File.ReadAllLines(otherInput));

        await auto.AspireStopAsync(counter);
    }

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task TerminalAttachFrontend_ShowsViteHelpAndDetaches()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        await auto.AspireNewAsync("TerminalSupportApp", counter, template: AspireTemplate.ExpressReact);

        var appHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "TerminalSupportApp", "apphost.mts");
        var appHostContent = File.ReadAllText(appHostPath);
        var oldFrontendLine = ".waitFor(app);";
        var newFrontendLine = """
            .waitFor(app)
                .withTerminal();
            """;

        if (!appHostContent.Contains(oldFrontendLine, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Could not find '{oldFrontendLine}' in {appHostPath}.");
        }

        File.WriteAllText(appHostPath, appHostContent.Replace(oldFrontendLine, newFrontendLine, StringComparison.Ordinal));

        await auto.TypeAsync("cd TerminalSupportApp");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.AspireStartAsync(counter);

        // Keep calling `aspire terminal ps` until the frontend row is reported alive.
        await auto.RunCommandAsync(
            "for i in $(seq 1 120); do aspire terminal ps | grep -Eq 'frontend.*alive' && break; sleep 1; done; aspire terminal ps | grep -Eq 'frontend.*alive'",
            counter,
            TimeSpan.FromMinutes(3));

        await auto.ClearScreenAsync(counter);

        await auto.TypeAsync("aspire terminal attach frontend");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Ctrl+B D", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitAsync(3000);

        await auto.TypeAsync("h");
        await auto.EnterAsync();
        await auto.WaitUntilAsync(
            s => s.ContainsText("Shortcuts") || s.ContainsText("press r + enter to restart the server"),
            timeout: TimeSpan.FromSeconds(30),
            description: "waiting for Vite help shortcuts");

        // Keep an explicit pause between chord keys to mirror interactive input cadence.
        await auto.Ctrl().KeyAsync(Hex1bKey.B);
        await auto.WaitAsync(1000);
        await auto.KeyAsync(Hex1bKey.D);
        await auto.WaitForSuccessPromptAsync(counter, timeout: TimeSpan.FromSeconds(30));

        await auto.AspireStopAsync(counter);
    }
}
