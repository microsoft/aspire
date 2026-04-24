// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for Aspire CLI with a Java polyglot AppHost.
/// Tests creating a Java-based AppHost and adding a Vite application.
/// </summary>
public sealed class JavaPolyglotTests(ITestOutputHelper output)
{
    [Fact]
    public async Task CreateJavaAppHostWithViteApp()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect();
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, variant: CliE2ETestHelpers.DockerfileVariant.PolyglotJava, mountDockerSocket: true, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);
        await auto.EnableExperimentalJavaSupportAsync(counter);

        await auto.TypeAsync("aspire init");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Which language would you like to use?", timeout: TimeSpan.FromSeconds(30));
        await auto.DownAsync();
        await auto.DownAsync();
        await auto.WaitUntilTextAsync("> Java", timeout: TimeSpan.FromSeconds(5));
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Created AppHost.java", timeout: TimeSpan.FromMinutes(2));
        await auto.DeclineAgentInitPromptAsync(counter);

        await auto.TypeAsync("npm create -y vite@latest viteapp -- --template vanilla-ts --no-interactive");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

        await auto.TypeAsync("cd viteapp && npm install && cd ..");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

        await auto.TypeAsync("aspire add Aspire.Hosting.JavaScript");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("The package Aspire.Hosting.", timeout: TimeSpan.FromMinutes(2));
        await auto.WaitForSuccessPromptAsync(counter);

        var appHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.java");
        var newContent = """
            import aspire.*;

            void main(String[] args) throws Exception {
                var builder = DistributedApplication.CreateBuilder(args);

                ViteAppResource viteApp = builder.addViteApp("viteapp", "./viteapp");

                builder.build().run();
            }
            """;

        File.WriteAllText(appHostPath, newContent);

        await auto.TypeAsync("aspire run");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Press CTRL+C to stop the AppHost and exit.", timeout: TimeSpan.FromMinutes(3));

        await auto.Ctrl().KeyAsync(Hex1b.Input.Hex1bKey.C);
        await auto.WaitForSuccessPromptAsync(counter);
        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }
}
