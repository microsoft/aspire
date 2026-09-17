// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
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
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, variant: CliE2ETestHelpers.DockerfileVariant.PolyglotJava, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

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

        await auto.TypeAsync("aspire restore");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("SDK code restored successfully", timeout: TimeSpan.FromMinutes(3));
        await auto.WaitForSuccessPromptAsync(counter);

        var modulesDir = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire/modules");
        if (!Directory.Exists(modulesDir))
        {
            throw new InvalidOperationException($".aspire/modules directory was not created at {modulesDir}");
        }

        // The second integration is added only after `.aspire/modules` is already populated. A
        // restore that treats a non-empty modules directory as up to date would leave the stale
        // SDK in place and still report success, so the SqlServer assertions below are what
        // actually prove the refresh. Adding both integrations before the first restore would
        // generate everything in one pass and never exercise that path.
        var codegenHashPath = Path.Combine(modulesDir, ".codegen-hash");
        var initialCodegenHash = File.Exists(codegenHashPath) ? File.ReadAllText(codegenHashPath) : null;

        var builderPath = Path.Combine(modulesDir, "aspire", "IDistributedApplicationBuilder.java");
        if (File.Exists(builderPath) && File.ReadAllText(builderPath).Contains("addSqlServer"))
        {
            throw new InvalidOperationException("Baseline SDK already exposes addSqlServer; test cannot verify refresh behavior after adding Aspire.Hosting.SqlServer.");
        }

        await auto.TypeAsync("aspire add Aspire.Hosting.SqlServer");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("The package Aspire.Hosting.", timeout: TimeSpan.FromMinutes(2));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire restore");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("SDK code restored successfully", timeout: TimeSpan.FromMinutes(3));
        await auto.WaitForSuccessPromptAsync(counter);

        // Generated sources are laid out by package: every file declares `package aspire;`, so javac
        // expects them under a matching `aspire/` directory. Only sources.txt sits at the modules root,
        // because the compiler is driven as `javac @.aspire/modules/sources.txt` from the AppHost directory.
        var expectedFiles = new[]
        {
            "aspire/Aspire.java",
            "aspire/AspireClient.java",
            "aspire/DistributedApplication.java",
            "aspire/IDistributedApplicationBuilder.java",
            "sources.txt"
        };

        foreach (var file in expectedFiles)
        {
            var filePath = Path.Combine(modulesDir, Path.Combine(file.Split('/')));
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

        var builderJava = File.ReadAllText(Path.Combine(modulesDir, "aspire", "IDistributedApplicationBuilder.java"));
        if (!builderJava.Contains("addRedis"))
        {
            throw new InvalidOperationException("IDistributedApplicationBuilder.java does not contain addRedis from Aspire.Hosting.Redis");
        }
        if (!builderJava.Contains("addSqlServer"))
        {
            throw new InvalidOperationException("IDistributedApplicationBuilder.java does not contain addSqlServer from Aspire.Hosting.SqlServer; restore did not refresh an already-populated .aspire/modules");
        }

        var restoredCodegenHash = File.Exists(codegenHashPath) ? File.ReadAllText(codegenHashPath) : null;
        if (initialCodegenHash == restoredCodegenHash)
        {
            throw new InvalidOperationException(".aspire/modules/.codegen-hash did not change after adding Aspire.Hosting.SqlServer and running aspire restore.");
        }
    }
}
