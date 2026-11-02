// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Cli.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Hex1b.Input;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

public sealed class CustomTemplateTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task DirectoryTemplatePromptsWithoutDotNetSdk() => CreateFromTemplateAsync(repository: false, nonInteractive: false, configured: false);

    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task RepositoryTemplatePromptsWithoutDotNetSdk() => CreateFromTemplateAsync(repository: true, nonInteractive: false, configured: false);

    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task RepositoryTemplateUsesDefaultsNonInteractivelyWithoutDotNetSdk() => CreateFromTemplateAsync(repository: true, nonInteractive: true, configured: false);

    [Fact]
    [CaptureWorkspaceOnFailure]
    public Task ConfiguredTemplateSourceCommandsAndSearchWithoutDotNetSdk() => CreateFromTemplateAsync(repository: false, nonInteractive: false, configured: true);

    private async Task CreateFromTemplateAsync(bool repository, bool nonInteractive, bool configured)
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

        if (configured)
        {
            await auto.RunCommandAsync("aspire template source add git-example --repository https://example.test/templates --ref main --path templates --yes --nologo", counter);
            await auto.RunCommandAsync("cp \"$HOME/.aspire/aspire.config.json\" before.json", counter);
            await auto.RunCommandAsync(
                "aspire template source add company --path \"$PWD/template\" --nologo < /dev/null; " +
                $"test $? -eq {CliExitCodes.InvalidCommand}", counter, timeout: TimeSpan.FromSeconds(30));
            await auto.RunCommandAsync("cmp before.json \"$HOME/.aspire/aspire.config.json\"", counter);

            var sourceLocation = CliE2ETestHelpers.ToContainerPath(Path.Combine(workspace.WorkspaceRoot.FullName, "template"), workspace);
            foreach (var response in new string?[] { null, "n", "y" })
            {
                // Clear answered prompts so each wait observes this invocation rather than stale terminal output.
                await auto.ClearScreenAsync(counter);
                var expectedExitCode = response == "y" ? CliExitCodes.Success : CliExitCodes.Cancelled;
                await auto.TypeAsync(
                    $"aspire template source add company --path \"$PWD/template\" --nologo; test $? -eq {expectedExitCode}");
                await auto.EnterAsync();
                await auto.WaitUntilTextAsync("Templates can run code. Do you trust template source 'company'", timeout: TimeSpan.FromSeconds(30));
                await auto.WaitUntilTextAsync(sourceLocation, timeout: TimeSpan.FromSeconds(30));
                await auto.WaitUntilTextAsync("[y/N]:", timeout: TimeSpan.FromSeconds(30));
                if (response is null)
                {
                    await auto.EnterAsync();
                }
                else
                {
                    // Confirmation consumes a single key; a trailing Enter would become an extra shell command.
                    await auto.TypeAsync(response);
                }
                await auto.WaitForSuccessPromptAsync(counter, timeout: TimeSpan.FromSeconds(30));
                if (response != "y")
                {
                    await auto.RunCommandAsync("cmp before.json \"$HOME/.aspire/aspire.config.json\"", counter);
                }
            }

            // Do not suppress the banner explicitly: JSON mode must keep redirected stdout parseable.
            await auto.RunCommandAsync("aspire template source list --format json > sources.json", counter);
            var listed = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(workspace.WorkspaceRoot.FullName, "sources.json")))!.AsObject();
            Assert.Equal(["company", "git-example"], listed.Select(source => source.Key));
            Assert.Equal(sourceLocation, listed["company"]!["path"]!.GetValue<string>());
            Assert.Equal("https://example.test/templates", listed["git-example"]!["repository"]!.GetValue<string>());
            Assert.Equal("main", listed["git-example"]!["ref"]!.GetValue<string>());
            Assert.Equal("templates", listed["git-example"]!["path"]!.GetValue<string>());
            await auto.RunCommandAsync("aspire template source list > sources.txt", counter);
            var table = await File.ReadAllTextAsync(Path.Combine(workspace.WorkspaceRoot.FullName, "sources.txt"));
            Assert.Contains("company", table);
            Assert.Contains("git-example", table);
            await auto.RunCommandAsync("cp \"$HOME/.aspire/aspire.config.json\" before.json", counter);
            await auto.RunCommandAsync($"aspire template source add COMPANY --repository https://example.test/replacement --yes --nologo; test $? -eq {CliExitCodes.InvalidCommand}", counter);
            await auto.RunCommandAsync("cmp before.json \"$HOME/.aspire/aspire.config.json\"", counter);
            await auto.RunCommandAsync($"aspire config set templateSources.company.path ../escape --global --nologo; test $? -eq {CliExitCodes.InvalidCommand}", counter);
            await auto.RunCommandAsync("cmp before.json \"$HOME/.aspire/aspire.config.json\"", counter);
            await auto.RunCommandAsync("aspire template source remove git-example --nologo", counter);
        }

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
        var source = configured ? "" : server is not null ? $"--template-repository \"{server.RepositoryUrl}\"" : "--template-path template";
        var mode = nonInteractive ? " --non-interactive" : "";
        await auto.TypeAsync($"aspire new {source} --name PromptDemo --output out --suppress-agent-init --nologo{mode}");
        await auto.EnterAsync();

        if (configured)
        {
            await auto.WaitUntilTextAsync("Search for more templates", timeout: TimeSpan.FromSeconds(60));
            await auto.KeyAsync(Hex1bKey.End);
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync("Search templates (leave empty to list all)", timeout: TimeSpan.FromSeconds(30));
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync("custom-template-fixture", timeout: TimeSpan.FromSeconds(30));
            await auto.WaitUntilTextAsync("Search again", timeout: TimeSpan.FromSeconds(30));
            await auto.DownAsync();
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync("Search templates (leave empty to list all)", timeout: TimeSpan.FromSeconds(30));
            await auto.TypeAsync("no-matching-template");
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync("Back to default templates", timeout: TimeSpan.FromSeconds(30));
            await auto.KeyAsync(Hex1bKey.End);
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync("Search for more templates", timeout: TimeSpan.FromSeconds(30));
            await auto.KeyAsync(Hex1bKey.End);
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync("Search templates (leave empty to list all)", timeout: TimeSpan.FromSeconds(30));
            await auto.TypeAsync("DESCRIPTION METADATA");
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync("custom-template-fixture", timeout: TimeSpan.FromSeconds(30));
            await auto.WaitUntilTextAsync("company", timeout: TimeSpan.FromSeconds(30));
            await auto.EnterAsync();
        }

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
        if (configured)
        {
            await auto.RunCommandAsync("aspire template source remove COMPANY --nologo", counter);
            await auto.RunCommandAsync("aspire template source list --format json > empty-sources.json", counter);
            Assert.Empty(JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(workspace.WorkspaceRoot.FullName, "empty-sources.json")))!.AsObject());
            await auto.RunCommandAsync($"aspire template source remove company --nologo; test $? -eq {CliExitCodes.ConfigNotFound}", counter);
        }

        var outputDirectory = Path.Combine(workspace.WorkspaceRoot.FullName, "out");
        var files = Directory.GetFiles(outputDirectory, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .ToDictionary(path => Path.GetRelativePath(outputDirectory, path).Replace('\\', '/'), File.ReadAllText);
        await VerifyXunit.Verifier.Verify(files).UseDirectory(Path.Combine(testDirectory, "Snapshots"));
    }
}
