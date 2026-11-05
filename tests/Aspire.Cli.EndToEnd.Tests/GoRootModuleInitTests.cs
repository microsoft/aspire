// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.Cli.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

public sealed class GoRootModuleInitTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [CaptureWorkspaceOnFailure]
    public async Task InitPreservesRootModuleAndWorkspace(bool hasWorkspace)
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        using var workspace = TemporaryWorkspace.Create(output);

        var root = workspace.WorkspaceRoot.FullName;
        var goMod = Encoding.UTF8.GetBytes("module example.com/existing\n\ngo 1.26\n");
        var mainGo = Encoding.UTF8.GetBytes("package main\n\nfunc main() {}\n");
        var goWork = Encoding.UTF8.GetBytes("go 1.26\n\nuse .\n");
        File.WriteAllBytes(Path.Combine(root, "go.mod"), goMod);
        File.WriteAllBytes(Path.Combine(root, "main.go"), mainGo);
        if (hasWorkspace)
        {
            File.WriteAllBytes(Path.Combine(root, "go.work"), goWork);
        }

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(
            repoRoot,
            strategy,
            output,
            variant: CliE2ETestHelpers.DockerfileVariant.PolyglotGo,
            mountDockerSocket: true,
            workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);
        await auto.RunCommandAsync("aspire init --language go --non-interactive", counter, TimeSpan.FromMinutes(3));

        Assert.Equal(goMod, File.ReadAllBytes(Path.Combine(root, "go.mod")));
        Assert.Equal(mainGo, File.ReadAllBytes(Path.Combine(root, "main.go")));
        if (hasWorkspace)
        {
            Assert.Equal(goWork, File.ReadAllBytes(Path.Combine(root, "go.work")));
        }
        else
        {
            Assert.False(File.Exists(Path.Combine(root, "go.work")));
        }

        var appHostRoot = Path.Combine(root, "aspire-apphost");
        Assert.True(File.Exists(Path.Combine(appHostRoot, "apphost.go")));
        Assert.True(File.Exists(Path.Combine(appHostRoot, "go.mod")));
        Assert.True(File.Exists(Path.Combine(appHostRoot, "go.work")));

        await auto.RunCommandAsync("go build ./...", counter, TimeSpan.FromMinutes(3));

        // Build and vet without overriding GOWORK: the nested workspace must keep an
        // ancestor go.work that only includes the original module from excluding the AppHost.
        await auto.RunCommandAsync(
            "cd aspire-apphost && aspire restore --non-interactive && go build -buildvcs=false ./... && go vet ./...",
            counter,
            TimeSpan.FromMinutes(4));
        await auto.AspireStartAsync(counter, startTimeout: TimeSpan.FromMinutes(3));
        await auto.AspireStopAsync(counter);
    }
}
