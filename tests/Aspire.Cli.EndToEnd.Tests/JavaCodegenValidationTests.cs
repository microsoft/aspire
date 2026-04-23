// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests that validate <c>aspire restore</c> for Java AppHosts by creating a
/// Java AppHost with multiple integrations and verifying the generated Java SDK files.
/// </summary>
public sealed class JavaCodegenValidationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task RestoreGeneratesSdkFiles()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect();
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, variant: CliE2ETestHelpers.DockerfileVariant.PolyglotJava, workspace: workspace);

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

        await auto.TypeAsync("aspire add Aspire.Hosting.Redis");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("The package Aspire.Hosting.", timeout: TimeSpan.FromMinutes(2));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire add Aspire.Hosting.SqlServer");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("The package Aspire.Hosting.", timeout: TimeSpan.FromMinutes(2));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire restore");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("SDK code restored successfully", timeout: TimeSpan.FromMinutes(3));
        await auto.WaitForSuccessPromptAsync(counter);

        var modulesDir = Path.Combine(workspace.WorkspaceRoot.FullName, ".modules");
        if (!Directory.Exists(modulesDir))
        {
            throw new InvalidOperationException($".modules directory was not created at {modulesDir}");
        }

        var expectedFiles = new[]
        {
            "Aspire.java",
            "AspireClient.java",
            "DistributedApplication.java",
            "IDistributedApplicationBuilder.java",
            "sources.txt"
        };

        foreach (var file in expectedFiles)
        {
            var filePath = Path.Combine(modulesDir, file);
            if (!File.Exists(filePath))
            {
                throw new InvalidOperationException($"Expected generated file not found: {filePath}");
            }

            var content = File.ReadAllText(filePath);
            if (string.IsNullOrWhiteSpace(content))
            {
                throw new InvalidOperationException($"Generated file is empty: {filePath}");
            }
        }

        var builderJava = File.ReadAllText(Path.Combine(modulesDir, "IDistributedApplicationBuilder.java"));
        if (!builderJava.Contains("addRedis"))
        {
            throw new InvalidOperationException("IDistributedApplicationBuilder.java does not contain addRedis from Aspire.Hosting.Redis");
        }
        if (!builderJava.Contains("addSqlServer"))
        {
            throw new InvalidOperationException("IDistributedApplicationBuilder.java does not contain addSqlServer from Aspire.Hosting.SqlServer");
        }

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }
}
