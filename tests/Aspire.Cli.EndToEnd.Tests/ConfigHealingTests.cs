// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Hex1b.Input;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests verifying that the CLI heals a corrupted aspire.config.json
/// (invalid apphost path, JSON comments) by auto-detecting the correct apphost
/// and updating the config file.
/// </summary>
public sealed class ConfigHealingTests(ITestOutputHelper output)
{
    /// <summary>
    /// Verifies that when aspire.config.json has an invalid apphost path and JSON comments,
    /// the CLI auto-detects the correct apphost, runs it successfully, and updates ("heals")
    /// the config file with the correct path.
    /// </summary>
    [Fact]
    public async Task InvalidAppHostPathWithComments_IsHealedOnRun()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);

        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(
            repoRoot, strategy, output,
            mountDockerSocket: true,
            workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        // 1. Create a starter project
        await auto.AspireNewAsync("HealTest", counter, useRedisCache: false);

        // 2. Overwrite aspire.config.json with an invalid apphost path and a JSON comment
        var configFilePath = Path.Combine(
            workspace.WorkspaceRoot.FullName,
            "HealTest",
            "aspire.config.json");

        var malformedConfig = """
            {
              // This comment should be handled gracefully
              "appHost": {
                "path": "nonexistent/path/to/AppHost.csproj" // this path doesn't exist
              },
              "channel": "stable"
            }
            """;
        File.WriteAllText(configFilePath, malformedConfig);

        // 3. Change into the project directory and run aspire run
        //    The CLI should detect the invalid path, find the real apphost,
        //    update the config, and start the app successfully
        await auto.TypeAsync("cd HealTest");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);
        await auto.TypeAsync("aspire run");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Press CTRL+C to stop the", timeout: TimeSpan.FromMinutes(3));
        await auto.Ctrl().KeyAsync(Hex1bKey.C);
        await auto.WaitForSuccessPromptAsync(counter);

        // 4. Verify the config file was healed (host-side file check)
        if (!File.Exists(configFilePath))
        {
            throw new InvalidOperationException(
                $"Config file does not exist after healing: {configFilePath}");
        }

        var content = File.ReadAllText(configFilePath);

        // The healed config should contain a valid apphost path
        // (pointing to the actual AppHost project)
        if (!content.Contains("HealTest.AppHost"))
        {
            throw new InvalidOperationException(
                $"Config file was not healed with correct AppHost path. Content:\n{content}");
        }

        // The invalid path should no longer be present
        if (content.Contains("nonexistent/path"))
        {
            throw new InvalidOperationException(
                $"Config file still contains invalid path after healing. Content:\n{content}");
        }

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }
}
