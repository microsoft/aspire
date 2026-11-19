// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Cli.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests that validate <c>aspire restore</c> for Go AppHosts by creating a
/// Go AppHost with multiple integrations and verifying the generated Go SDK files.
/// </summary>
public sealed class GoCodegenValidationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [CaptureWorkspaceOnFailure]
    public async Task RestoreGeneratesSdkFiles(bool nonInteractive, bool existingProject)
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        using var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, variant: CliE2ETestHelpers.DockerfileVariant.PolyglotGo, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        var existingFiles = new Dictionary<string, string>();
        if (existingProject)
        {
            var apiDirectory = workspace.CreateDirectory("api");
            existingFiles[Path.Combine(apiDirectory.FullName, "go.mod")] = """
                module example.com/existing-api

                go 1.26
                """;
            existingFiles[Path.Combine(apiDirectory.FullName, "main.go")] = """
                package main

                import "fmt"

                func main() {
                    fmt.Println("existing Go application")
                }
                """;
            foreach (var (path, content) in existingFiles)
            {
                await File.WriteAllTextAsync(path, content);
            }
        }

        if (nonInteractive)
        {
            await auto.RunCommandAsync(
                "aspire init --language go --non-interactive --suppress-agent-init < /dev/null",
                counter,
                TimeSpan.FromMinutes(3));
        }
        else
        {
            await auto.AspireInitGoAppHostAsync(counter);
        }

        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, "aspire.config.json");
        var config = JsonNode.Parse(await File.ReadAllTextAsync(configPath));
        Assert.NotNull(config);
        Assert.Equal("go", config["appHost"]?["language"]?.GetValue<string>());
        Assert.Equal("apphost.go", config["appHost"]?["path"]?.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(config["packages"]?["Aspire.Hosting.Go"]?.GetValue<string>()));

        foreach (var (path, content) in existingFiles)
        {
            Assert.Equal(content, await File.ReadAllTextAsync(path));
        }

        if (existingProject)
        {
            GoAppHostTestHelpers.InsertBeforeBuild(
                Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.go"),
                """builder.AddGoApp("existing-api", "./api")""");
        }

        await auto.AspireAddPackageAsync("Aspire.Hosting.Redis", counter);
        await auto.RunCommandAsync("aspire restore --non-interactive", counter, TimeSpan.FromMinutes(3));

        var modulesDir = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "modules");
        Assert.True(Directory.Exists(modulesDir), $".aspire/modules directory was not created at {modulesDir}");

        // aspire.go is the whole generated SDK in one file, so assert on substrings and keep it out of failure messages.
        var sdkPath = Path.Combine(modulesDir, "aspire.go");
        var codegenHashPath = Path.Combine(modulesDir, ".codegen-hash");
        Assert.True(File.Exists(sdkPath), $"Expected generated file not found: {sdkPath}");
        Assert.True(File.Exists(codegenHashPath), $"Expected generated file not found: {codegenHashPath}");
        Assert.False(
            File.ReadAllText(sdkPath).Contains("AddSqlServer", StringComparison.Ordinal),
            "Baseline SDK already exposes AddSqlServer; test cannot verify refresh behavior after adding Aspire.Hosting.SqlServer.");
        var initialCodegenHash = File.ReadAllText(codegenHashPath);

        // `aspire add` regenerates the SDK itself, so once it returns aspire.go already exposes AddSqlServer and
        // .codegen-hash has already changed, and the restore below would have nothing left to prove. Putting the
        // baseline copies back leaves a stale SDK next to an aspire.config.json that lists SqlServer, which is
        // the state a restore has to refresh. The copies run inside the container: the workspace is bind-mounted
        // and its files belong to the container's root user, so a write from the host can fail on Linux.
        await auto.RunCommandAsync("mkdir -p ~/sdk-baseline && cp .aspire/modules/aspire.go .aspire/modules/.codegen-hash ~/sdk-baseline/", counter);
        await auto.AspireAddPackageAsync("Aspire.Hosting.SqlServer", counter);
        await auto.RunCommandAsync("cp ~/sdk-baseline/aspire.go ~/sdk-baseline/.codegen-hash .aspire/modules/", counter);

        Assert.False(
            File.ReadAllText(sdkPath).Contains("AddSqlServer", StringComparison.Ordinal),
            "The baseline SDK was not put back after aspire add, so restore would have nothing to refresh.");
        Assert.Equal(initialCodegenHash, File.ReadAllText(codegenHashPath));

        await auto.RunCommandAsync("aspire restore --non-interactive", counter, TimeSpan.FromMinutes(3));

        var expectedFiles = new[] { "aspire.go", "base.go", "transport.go", "go.mod" };

        foreach (var file in expectedFiles)
        {
            var filePath = Path.Combine(modulesDir, file);
            Assert.True(File.Exists(filePath), $"Expected generated file not found: {filePath}");
            Assert.False(string.IsNullOrWhiteSpace(File.ReadAllText(filePath)), $"Generated file is empty: {filePath}");
        }

        var sdk = File.ReadAllText(sdkPath);
        Assert.True(sdk.Contains("AddRedis", StringComparison.Ordinal), "aspire.go does not contain AddRedis from Aspire.Hosting.Redis");
        Assert.True(
            sdk.Contains("AddSqlServer", StringComparison.Ordinal),
            "aspire.go does not contain AddSqlServer from Aspire.Hosting.SqlServer; restore did not refresh an already-populated .aspire/modules");

        Assert.NotEqual(initialCodegenHash, File.ReadAllText(codegenHashPath));

        // Build the consumer and vet the generated module explicitly: ./... skips the hidden .aspire directory.
        await auto.RunCommandAsync("go build -buildvcs=false ./... && go vet ./...", counter, TimeSpan.FromMinutes(3));
        await auto.RunCommandAsync("(cd .aspire/modules && go vet ./...)", counter, TimeSpan.FromMinutes(3));
        if (existingProject)
        {
            await auto.RunCommandAsync("(cd api && go build -buildvcs=false ./... && go vet ./...)", counter, TimeSpan.FromMinutes(3));
        }
    }
}
