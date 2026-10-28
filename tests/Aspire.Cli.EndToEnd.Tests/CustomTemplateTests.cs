// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Cli.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

public sealed class CustomTemplateTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task DirectoryTemplatePromptsWithoutDotNetSdk() => CreateFromTemplateAsync(repository: false, nonInteractive: false);

    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task RepositoryTemplatePromptsWithoutDotNetSdk() => CreateFromTemplateAsync(repository: true, nonInteractive: false);

    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task RepositoryTemplateUsesDefaultsNonInteractivelyWithoutDotNetSdk() => CreateFromTemplateAsync(repository: true, nonInteractive: true);

    private async Task CreateFromTemplateAsync(bool repository, bool nonInteractive)
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var testDirectory = Path.Combine(repoRoot, "tests", "Aspire.Cli.EndToEnd.Tests");
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);
        TestDirectoryHelpers.CopyDirectory(
            Path.Combine(testDirectory, "Fixtures", "CustomTemplate"),
            Path.Combine(workspace.WorkspaceRoot.FullName, "template"));

        if (nonInteractive)
        {
            // The interactive fixture requires an explicit greeting; unattended generation uses a default instead.
            var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, "template", ".template.config", "template.json");
            var config = JsonNode.Parse(await File.ReadAllTextAsync(configPath))!;
            var greeting = config["symbols"]!["Greeting"]!;
            greeting["isRequired"] = false;
            greeting["defaultValue"] = "Hello from template defaults";
            await File.WriteAllTextAsync(configPath, config.ToJsonString());
        }

        // The Git server runs in the test process; host networking makes its loopback address reachable.
        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(
            repoRoot, strategy, output, variant: CliE2ETestHelpers.DockerfileVariant.Polyglot,
            workspace: workspace, network: repository ? "host" : null);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(
            terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);
        await auto.RunCommandAsync("! command -v dotnet", counter);

        if (repository)
        {
            // The bind-mounted template belongs to the host UID. Trust only this fixture for Git setup.
            await auto.RunCommandAsync(
                "git -C template init && git -c safe.directory=\"$PWD/template\" -C template add . && " +
                "git -c safe.directory=\"$PWD/template\" -C template -c user.name='Aspire tests' " +
                "-c user.email=tests@example.com -c commit.gpgsign=false commit -m 'Add template' && " +
                "git -c safe.directory=\"$PWD/template\" clone --bare template template.git",
                counter);
        }

        await using var server = repository
            ? await GitHttpServer.StartAsync(workspace.Path, TestContext.Current.CancellationToken)
            : null;
        var source = server is not null ? $"--template-repository \"{server.RepositoryUrl}\"" : "--template-path template";
        var mode = nonInteractive ? " --non-interactive" : "";
        await auto.TypeAsync($"aspire new {source} --name PromptDemo --output out --suppress-agent-init --nologo{mode}");
        await auto.EnterAsync();

        if (!nonInteractive)
        {
            await auto.WaitUntilTextAsync("Greeting text", timeout: TimeSpan.FromSeconds(60));
            await auto.TypeAsync("Hello from the terminal");
            await auto.EnterAsync();

            await auto.WaitUntilTextAsync("Include readme", timeout: TimeSpan.FromSeconds(30));
            if (!repository)
            {
                // Confirmation accepts y/n immediately; a trailing Enter would answer the next prompt.
                await auto.TypeAsync("n");
            }
            else
            {
                await auto.EnterAsync();
            }

            await auto.WaitUntilTextAsync("> HTTP API", timeout: TimeSpan.FromSeconds(30));
            if (repository)
            {
                await auto.DownAsync();
            }
            await auto.EnterAsync();

            await auto.WaitUntilTextAsync("Included features", timeout: TimeSpan.FromSeconds(30));
            await auto.WaitUntilTextAsync("> [X] Web support", timeout: TimeSpan.FromSeconds(30));
            await auto.DownAsync();
            await auto.TypeAsync(" ");
            await auto.EnterAsync();
        }
        await auto.WaitForSuccessPromptAsync(counter, timeout: nonInteractive ? TimeSpan.FromSeconds(60) : null);
        await auto.RunCommandAsync("! command -v dotnet", counter);

        var outputDirectory = Path.Combine(workspace.WorkspaceRoot.FullName, "out");
        var files = Directory.GetFiles(outputDirectory, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .ToDictionary(path => Path.GetRelativePath(outputDirectory, path).Replace('\\', '/'), File.ReadAllText);
        await VerifyXunit.Verifier.Verify(files).UseDirectory(Path.Combine(testDirectory, "Snapshots"));
    }
}
