// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;
using System.Text.Json;
using Xunit;

namespace Infrastructure.Tests.WinUI;

// Investigation-only coverage, dispatched explicitly rather than added to regular PR CI.
public sealed class WinUiBuildContentionTests(ITestOutputHelper output)
{
    [Fact]
    public async Task NormalBuildSucceedsWhileDesignTimeXamlInputIsLocked()
    {
        Assert.True(OperatingSystem.IsWindows(), "CAPABILITY_FAILURE: WinUI requires Windows.");
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
        {
            Assert.Equal(Architecture.X64, RuntimeInformation.OSArchitecture);
            Assert.Equal(Architecture.X64, RuntimeInformation.ProcessArchitecture);
        }

        var sourceRef = Environment.GetEnvironmentVariable("WINUI_PROBE_SOURCE_REF");
        Assert.NotNull(sourceRef);
        Assert.Matches("^[0-9a-f]{40}$", sourceRef);
        var resultsRoot = Environment.GetEnvironmentVariable("WINUI_PROBE_RESULTS_DIRECTORY");
        Assert.NotNull(resultsRoot);

        var repoRoot = RepoRoot.Path;
        Assert.Equal(sourceRef, GitCli.Run(repoRoot, "rev-parse", $"{sourceRef}^{{commit}}"));
        var workflowHead = GitCli.Run(repoRoot, "rev-parse", "HEAD");
        var generatorBlob = GitCli.Run(repoRoot, "rev-parse", $"{sourceRef}:extension/scripts/run-e2e.js");
        var evidence = Directory.CreateDirectory(Path.Combine(resultsRoot, $"execution-{Guid.NewGuid():N}")).FullName;
        using var workspace = TemporaryWorkspace.Create(output);
        var sourcePath = Path.Combine(evidence, "run-e2e-source.js");
        File.WriteAllText(sourcePath, GitCli.Run(repoRoot, "show", $"{sourceRef}:extension/scripts/run-e2e.js"));
        File.WriteAllText(Path.Combine(evidence, "metadata.json"), JsonSerializer.Serialize(new
        {
            sourceRef,
            workflowHead,
            generatorBlob,
            osArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            runId = Environment.GetEnvironmentVariable("GITHUB_RUN_ID"),
            runAttempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT"),
            runner = Environment.GetEnvironmentVariable("RUNNER_NAME")
        }, new JsonSerializerOptions { WriteIndented = true }));
        output.WriteLine($"WINUI_PROBE source={sourceRef} workflow={workflowHead} evidence={evidence}");

        var scripts = Path.Combine(repoRoot, "tests", "Infrastructure.Tests", "WinUI");
        using (var generator = new NodeCommand(output, "actual-generator")
            .WithWorkingDirectory(repoRoot)
            .WithTimeout(TimeSpan.FromMinutes(1)))
        {
            var generation = await generator.ExecuteScriptAsync(
                Path.Combine(scripts, "generate-winui.mts"), sourcePath, workspace.Path);
            File.WriteAllText(Path.Combine(evidence, "generator.log"), generation.Output);
            generation.EnsureSuccessful("CAPABILITY_FAILURE: actual generator extraction");
        }

        using var probe = new PowerShellCommand(Path.Combine(scripts, "invoke-contention-probe.ps1"), output, "winui-probe")
            .WithWorkingDirectory(repoRoot)
            .WithEnvironmentVariable("WINUI_PROBE_REPO_ROOT", repoRoot)
            .WithEnvironmentVariable("WINUI_PROBE_WORKSPACE", workspace.Path)
            .WithEnvironmentVariable("WINUI_PROBE_EVIDENCE", evidence)
            .WithEnvironmentVariable("DOTNET_ROOT", Path.Combine(repoRoot, ".dotnet"))
            .WithEnvironmentVariable("MSBUILDTERMINALLOGGER", "false")
            .WithEnvironmentVariable("MSBUILDDISABLENODEREUSE", "1")
            .WithEnvironmentVariable("DOTNET_CLI_UI_LANGUAGE", "en-US")
            .WithTimeout(TimeSpan.FromMinutes(8));
        var result = await probe.ExecuteAsync();
        File.WriteAllText(Path.Combine(evidence, "probe.log"), result.Output);
        result.EnsureSuccessful("CAPABILITY_OR_SETUP_FAILURE: not a contention reproduction");

        using var outcome = JsonDocument.Parse(File.ReadAllText(Path.Combine(evidence, "outcome.json")));
        var root = outcome.RootElement;
        var classification = root.GetProperty("classification").GetString();
        var buildExitCode = root.GetProperty("normalBuildExitCode").GetInt32();
        output.WriteLine($"WINUI_PROBE classification={classification} normalBuildExitCode={buildExitCode}");

        // The baseline must fail on the real build, not on an assertion about directory layout.
        // Only the separately recorded sharing-violation classification counts as reproduction.
        Assert.True(buildExitCode == 0,
            $"Normal WinUI build failed while design-time input.json was locked. " +
            $"Classification: {classification}; exit: {buildExitCode}.{Environment.NewLine}" +
            File.ReadAllText(Path.Combine(evidence, "locked-normal-build.log")));
        Assert.Equal("passed", classification);
        Assert.True(root.GetProperty("normalXamlRegenerated").GetBoolean());
    }
}
