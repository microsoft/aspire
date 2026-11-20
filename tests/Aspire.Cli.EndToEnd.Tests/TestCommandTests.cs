// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Hex1b.Input;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

public sealed class TestCommandTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task CSharpAppHost_TestResourcesCollectResultsAndStop() => TestAppHostAsync(typeScript: false);

    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task TypeScriptAppHost_TestResourcesCollectResultsAndStop() => TestAppHostAsync(typeScript: true);

    private async Task TestAppHostAsync(bool typeScript)
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);
        var localChannel = CliE2ETestHelpers.PrepareLocalChannel(repoRoot, strategy,
            typeScript ? ["Aspire.Hosting.CodeGeneration.TypeScript."] : null);
        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromMinutes(5));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);
        if (typeScript)
        {
            await auto.AspireNewTypeScriptEmptyAppHostAsync("TestingApp", counter);
        }
        else
        {
            await auto.AspireNewAsync("TestingApp", counter, template: AspireTemplate.EmptyAppHost);
        }

        var projectDirectory = Path.Combine(workspace.WorkspaceRoot.FullName, "TestingApp");
        if (localChannel is not null)
        {
            CliE2ETestHelpers.WriteLocalChannelSettings(projectDirectory, localChannel.SdkVersion);
        }

        WriteFixtures(projectDirectory, typeScript);
        // The harness shares scaffolded source across host/container UIDs. Keep that
        // accessible for fixture writes, then harden this container's own Aspire home
        // and runtime-created files before allocating private test artifacts.
        await auto.RunCommandAsync("umask 077 && chmod 700 \"$HOME/.aspire\"", counter);
        await auto.RunCommandAsync("cd TestingApp && aspire config set features.testCommandEnabled true", counter);
        await auto.RunCommandAsync("aspire restore --non-interactive", counter, TimeSpan.FromMinutes(3));
        if (typeScript)
        {
            await auto.RunCommandAsync("npm run build", counter, TimeSpan.FromMinutes(2));
        }

        // Reuse the same AppHost to prove that invocations have fresh output directories
        // and that process failure and published test failure are independent.
        foreach (var scenario in new[] { "passed", "process-failed", "result-failed" })
        {
            await auto.RunCommandAsync(
                $"SCENARIO={scenario} aspire test --format json --timeout-seconds 180 > {scenario}.json 2> {scenario}.stderr; " +
                $"actual=$?; cat {scenario}.stderr; python3 verify.py {scenario} \"$actual\"",
                counter, TimeSpan.FromMinutes(4));
        }

        if (typeScript)
        {
            // Reuse the guest AppHost and installation rather than adding another
            // expensive E2E setup. The CLI remains the foreground job for real Ctrl+C.
            await auto.TypeAsync("python3 inspect.py & SCENARIO=passed aspire test --format json --keep-alive > keep-alive.json 2> keep-alive.stderr");
            await auto.EnterAsync();
            await auto.WaitUntilAsync(
                snapshot => snapshot.ContainsText("Verified keep-alive inspection"),
                timeout: TimeSpan.FromMinutes(3),
                description: "completed collection and a responsive dependency during keep-alive");
            await auto.Ctrl().KeyAsync(Hex1bKey.C);
            await auto.WaitUntilAsync(
                snapshot => snapshot.ContainsText($"[{counter.Value} ERR:130] $ "),
                timeout: TimeSpan.FromSeconds(30),
                description: "keep-alive CLI returning exit code 130 after Ctrl+C");
            counter.Increment();
            await auto.RunCommandAsync("python3 verify.py keep-alive 130", counter);
        }
    }

    private static void WriteFixtures(string directory, bool typeScript)
    {
        File.WriteAllText(Path.Combine(directory, "service.py"), """
            import http.server
            import os
            import pathlib

            pathlib.Path("service.pid").write_text(str(os.getpid()))

            class Handler(http.server.BaseHTTPRequestHandler):
                def do_GET(self):
                    self.send_response(200)
                    self.end_headers()
                    self.wfile.write(b"dependency alive")

            http.server.HTTPServer(("127.0.0.1", int(os.environ["PORT"])), Handler).serve_forever()
            """);
        File.WriteAllText(Path.Combine(directory, "probe.py"), """
            import os
            import pathlib
            import sys
            import time
            import urllib.request

            name = sys.argv[1]
            pathlib.Path(name + ".pid").write_text(str(os.getpid()))
            if name == "remaining":
                deadline = time.monotonic() + 30
                while not pathlib.Path("collector-finished").exists():
                    if time.monotonic() >= deadline:
                        raise TimeoutError("The other resource's runtime collector did not finish")
                    time.sleep(0.05)

            with urllib.request.urlopen(os.environ["SERVICE_URL"], timeout=10) as response:
                body = response.read().decode()
            assert body == "dependency alive", body
            pathlib.Path("service.url").write_text(os.environ["SERVICE_URL"])
            pathlib.Path(name + ".txt").write_text(body)
            sys.exit(7 if name == "primary" and os.environ["SCENARIO"] == "process-failed" else 0)
            """);
        File.WriteAllText(Path.Combine(directory, "inspect.py"), """
            import json
            import pathlib
            import time
            import urllib.request

            deadline = time.monotonic() + 180
            while True:
                # The CLI emits one JSON object, but the file can be observed mid-write.
                try:
                    summary = json.loads(pathlib.Path("keep-alive.json").read_text())
                    break
                except (FileNotFoundError, json.JSONDecodeError):
                    if time.monotonic() >= deadline:
                        raise TimeoutError("The keep-alive CLI did not finalize its results")
                    time.sleep(0.05)

            assert summary["status"] == "passed", summary
            assert summary["cliExitCode"] is None, summary
            collections = {c["resourceName"]: c for c in summary["collections"]}
            assert collections["primary"]["status"] == "completed", collections
            for name in ["apphost", "service"]:
                pid = pathlib.Path(name + ".pid").read_text()
                assert pathlib.Path("/proc", pid).exists(), (name, pid)
            with urllib.request.urlopen(pathlib.Path("service.url").read_text(), timeout=10) as response:
                assert response.read().decode() == "dependency alive"
            print("Verified keep-alive inspection", flush=True)
            """);
        File.WriteAllText(Path.Combine(directory, "verify.py"), """
            import json
            import pathlib
            import sys
            import time

            scenario, actual = sys.argv[1], int(sys.argv[2])
            keep_alive = scenario == "keep-alive"
            expected = 130 if keep_alive else 0 if scenario == "passed" else 23
            assert actual == expected, (scenario, actual, expected)
            # json.loads rejects banners, extra JSON objects, and trailing console output.
            summary = json.loads(pathlib.Path(scenario + ".json").read_text())
            persisted = json.loads(pathlib.Path(summary["summaryPath"]).read_text())
            if keep_alive:
                # Inspection emits completed results once; cancellation updates only
                # the persisted command status, not those results or stdout.
                assert summary["status"] == "passed", summary
                assert summary["cliExitCode"] is None, summary
                assert persisted["status"] == "cancelled", persisted
                assert persisted["cliExitCode"] == 130, persisted
                for key in ["resources", "collections", "testResults", "publishedResults"]:
                    assert persisted[key] == summary[key], (key, summary, persisted)
            else:
                assert summary == persisted, (summary, persisted)
                assert summary["cliExitCode"] == expected
                assert summary["status"] == ("passed" if expected == 0 else "failed")
            assert summary["schemaVersion"] == 1
            assert summary["error"] is None, summary
            results = summary["testResults"]
            outcome = "failed" if scenario == "result-failed" else "passed"
            assert [(r["resourceName"], r["name"], r["outcome"]) for r in results] == [
                ("primary", "runtime service probe", outcome)
            ], results
            assert summary["publishedResults"] == {
                "passed": int(outcome == "passed"), "failed": int(outcome == "failed"), "skipped": 0
            }
            resources = summary["resources"]
            assert sorted(r["resourceName"] for r in resources) == ["primary", "remaining"], resources
            assert all(r["state"] == "Finished" for r in resources), resources
            assert {r["resourceName"]: r["exitCode"] for r in resources} == {
                "primary": 7 if scenario == "process-failed" else 0, "remaining": 0
            }, resources
            collections = {c["resourceName"]: c for c in summary["collections"]}
            assert collections["primary"]["status"] == "completed", collections
            assert collections["primary"]["collectorCount"] == 1
            assert collections["remaining"]["collectorCount"] == 0
            artifact = pathlib.Path(summary["outputPath"]) / "primary" / "probe.txt"
            assert artifact.read_text() == "dependency alive"
            assert pathlib.Path("remaining.txt").read_text() == "dependency alive"
            assert pathlib.Path("callback-started").exists()
            previous = pathlib.Path("previous-output")
            if previous.exists():
                assert previous.read_text() != summary["outputPath"]
            previous.write_text(summary["outputPath"])

            # Every PID came from this invocation in this isolated container. Wait for
            # actual process removal rather than relying on a delay after CLI exit.
            pids = [int(p.read_text()) for p in pathlib.Path(".").glob("*.pid")]
            assert len(pids) == 4, pids
            deadline = time.monotonic() + 10
            while any(pathlib.Path("/proc", str(pid)).exists() for pid in pids):
                if time.monotonic() >= deadline:
                    raise AssertionError(("Owned processes survived aspire test", pids))
                time.sleep(0.05)
            print("Verified " + scenario)
            """);
        if (typeScript)
        {
            File.WriteAllText(Path.Combine(directory, "apphost.mts"), """
                import { existsSync, readFileSync, unlinkSync, writeFileSync } from 'node:fs';
                import { join } from 'node:path';
                import { createBuilder, TestOutcome } from './.aspire/modules/aspire.mjs';

                for (const name of ['apphost.pid', 'service.pid', 'primary.pid', 'remaining.pid',
                    'primary.txt', 'remaining.txt', 'callback-started', 'collector-finished']) {
                    if (existsSync(name)) unlinkSync(name);
                }
                writeFileSync('apphost.pid', String(process.pid));
                const builder = await createBuilder();
                const service = await builder.addExecutable('service', 'python3', '.', ['service.py'])
                    .withHttpEndpoint({ env: 'PORT' }).withHttpHealthCheck();
                const endpoint = service.getEndpoint('http');
                await builder.addExecutable('primary', 'python3', '.', ['probe.py', 'primary'])
                    .withEnvironment('SERVICE_URL', endpoint)
                    .withEnvironment('SCENARIO', process.env.SCENARIO!)
                    .waitFor(service)
                    .withTesting({ callback: async context => {
                        writeFileSync('callback-started', 'runtime');
                        const passed = await context.waitForCompletionAsync();
                        if (passed !== (process.env.SCENARIO !== 'process-failed')) {
                            throw new Error('Authoritative process outcome differs');
                        }
                        const body = readFileSync('primary.txt', 'utf8');
                        const root = await context.testingContext().outputPath();
                        const destination = await context.outputPath();
                        if (destination !== join(root, 'primary')) throw new Error('Unexpected artifact path');
                        writeFileSync(join(destination, 'probe.txt'), body);
                        await context.publishResult({
                            name: 'runtime service probe',
                            outcome: process.env.SCENARIO === 'result-failed' ? TestOutcome.Failed : TestOutcome.Passed
                        });
                        writeFileSync('collector-finished', 'published');
                    } });
                await builder.addExecutable('remaining', 'python3', '.', ['probe.py', 'remaining'])
                    .withEnvironment('SERVICE_URL', endpoint).waitFor(service).withTesting();
                if (existsSync('callback-started')) throw new Error('Collector ran during model construction');
                await builder.build().run();
                """);
        }
        else
        {
            var appHostPath = Path.Combine(directory, "apphost.cs");
            var directives = string.Join(Environment.NewLine, File.ReadLines(appHostPath)
                .Where(line => line.StartsWith("#:", StringComparison.Ordinal)));
            File.WriteAllText(appHostPath, $$"""
                {{directives}}

                #pragma warning disable ASPIRETESTING001

                using Aspire.Hosting.ApplicationModel;

                foreach (var name in new[] { "apphost.pid", "service.pid", "primary.pid", "remaining.pid",
                    "primary.txt", "remaining.txt", "callback-started", "collector-finished" })
                {
                    File.Delete(name);
                }
                File.WriteAllText("apphost.pid", Environment.ProcessId.ToString());
                var builder = DistributedApplication.CreateBuilder(args);
                var service = builder.AddExecutable("service", "python3", ".", "service.py")
                    .WithHttpEndpoint(env: "PORT").WithHttpHealthCheck();
                var endpoint = service.GetEndpoint("http");
                var scenario = Environment.GetEnvironmentVariable("SCENARIO")!;
                builder.AddExecutable("primary", "python3", ".", "probe.py", "primary")
                    .WithEnvironment("SERVICE_URL", endpoint).WithEnvironment("SCENARIO", scenario)
                    .WaitFor(service).WithTesting(async context =>
                    {
                        await File.WriteAllTextAsync("callback-started", "runtime", context.CancellationToken);
                        var passed = await context.WaitForCompletionAsync();
                        if (passed != (scenario != "process-failed"))
                        {
                            throw new InvalidOperationException("Authoritative process outcome differs.");
                        }
                        var body = await File.ReadAllTextAsync("primary.txt", context.CancellationToken);
                        if (context.OutputPath != Path.Combine(context.TestingContext.OutputPath, "primary"))
                        {
                            throw new InvalidOperationException("Unexpected artifact path.");
                        }
                        await File.WriteAllTextAsync(Path.Combine(context.OutputPath, "probe.txt"), body, context.CancellationToken);
                        context.PublishResult(new TestResult
                        {
                            Name = "runtime service probe",
                            Outcome = scenario == "result-failed" ? TestOutcome.Failed : TestOutcome.Passed
                        });
                        await File.WriteAllTextAsync("collector-finished", "published", context.CancellationToken);
                    });
                builder.AddExecutable("remaining", "python3", ".", "probe.py", "remaining")
                    .WithEnvironment("SERVICE_URL", endpoint).WaitFor(service).WithTesting();
                if (File.Exists("callback-started"))
                {
                    throw new InvalidOperationException("Collector ran during model construction.");
                }
                builder.Build().Run();
                """);
        }
    }
}
