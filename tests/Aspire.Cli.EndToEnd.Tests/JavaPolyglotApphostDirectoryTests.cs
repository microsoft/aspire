// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// Regression coverage for https://github.com/microsoft/aspire/issues/16513.
/// Polyglot (non-.NET) AppHosts run under a generic host process named "aspire-managed",
/// so without the env-var-based AppHostPath wiring in <c>AtsJavaCodeGenerator</c> the AppHost
/// reports its path as "&lt;dir&gt;/aspire-managed" and <c>aspire &lt;cmd&gt; --apphost
/// &lt;directory&gt;</c> cannot match the running guest AppHost via its backchannel socket.
/// This test duplicates the JavaPolyglotTests setup but starts the AppHost in the background
/// and then issues <c>aspire stop --non-interactive --apphost &lt;directory&gt;</c> from an
/// unrelated cwd so the stop command is forced to resolve the AppHost via the path the host
/// reported over the backchannel.
/// Each test class runs as a separate CI job for parallelization.
/// </summary>
public sealed class JavaPolyglotApphostDirectoryTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task StopJavaPolyglotAppHostUsingApphostDirectory()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, variant: CliE2ETestHelpers.DockerfileVariant.PolyglotJava, mountDockerSocket: true, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);
        await auto.EnableExperimentalJavaSupportAsync(counter);

        // Put the AppHost in a subdirectory so we can `cd ..` away from it and then resolve it
        // back via `--apphost javaapp`. That forces AppHost discovery through the path the
        // running host reported over the backchannel (the bug scenario from issue 16513),
        // rather than through the current working directory.
        await auto.TypeAsync("mkdir javaapp && cd javaapp");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

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

        var appHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "javaapp", "AppHost.java");
        var newContent = """
            import aspire.*;

            void main(String[] args) throws Exception {
                var builder = DistributedApplication.CreateBuilder(args);

                ViteAppResource viteApp = builder.addViteApp("viteapp", "./viteapp");

                builder.build().run();
            }
            """;

        File.WriteAllText(appHostPath, newContent);

        // Start the AppHost in the background (returns to the prompt once the AppHost reports it
        // has started over the backchannel).
        await auto.TypeAsync("aspire start");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync(RunCommandStrings.AppHostStartedSuccessfully, timeout: TimeSpan.FromMinutes(3));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.ClearScreenAsync(counter);

        // Leave the AppHost directory so the only way `aspire stop` can find the running guest
        // AppHost is via the AppHostPath it reported over the backchannel. Without the env-var
        // fix, the reported path ends in "aspire-managed" and this stop call would fail with
        // "AppHost not running at path".
        await auto.TypeAsync("cd ..");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire stop --non-interactive --apphost javaapp");
        await auto.EnterAsync();
        await auto.WaitUntilAppHostStoppedSuccessfullyAsync(timeout: TimeSpan.FromMinutes(1));
        await auto.WaitForSuccessPromptAsync(counter);
    }
}
