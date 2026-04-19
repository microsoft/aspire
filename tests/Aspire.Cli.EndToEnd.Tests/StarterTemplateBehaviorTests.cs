// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end regression tests for starter-template behaviors that were previously covered by the template harness.
/// </summary>
public sealed class StarterTemplateBehaviorTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("Starter.With.1", "xUnit.net", "v3mtp")]
    [InlineData("StarterMSTest", "MSTest", null)]
    [CaptureWorkspaceOnFailure]
    public async Task StarterTemplateCanGenerateAndRunBuiltInTests(string projectName, string testFramework, string? xunitVersion)
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect();
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        var args = new List<string>
        {
            "--use-redis-cache", "false",
            "--test-framework", testFramework
        };

        if (xunitVersion is not null)
        {
            args.Add("--xunit-version");
            args.Add(xunitVersion);
        }

        await auto.AspireNewSubcommandAsync("aspire-starter", projectName, counter, [.. args]);

        var testsDirectory = Path.Combine(workspace.WorkspaceRoot.FullName, projectName, $"{projectName}.Tests");
        var testsProjectPath = Path.Combine(testsDirectory, $"{projectName}.Tests.csproj");

        Assert.True(Directory.Exists(testsDirectory), $"Expected generated tests directory at {testsDirectory}.");
        Assert.True(File.Exists(testsProjectPath), $"Expected generated tests project at {testsProjectPath}.");

        await auto.TypeAsync($"cd {CliE2ETestHelpers.ToContainerPath(testsDirectory, workspace)}");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync($"dotnet test {Quote(Path.GetFileName(testsProjectPath))}");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(4));

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task StarterTemplateWithRedisHasCacheResourceAndApiResponds()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect();
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        await auto.AspireNewSubcommandAsync("aspire-starter", "StarterRedisApp", counter, "--use-redis-cache", "true", "--test-framework", "None");

        await auto.TypeAsync("cd StarterRedisApp");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.AspireStartAsync(counter);

        await auto.AssertResourcesExistAsync(counter, "webfrontend", "apiservice", "cache");

        var apiJsonPath = await auto.CaptureJsonOutputAsync(
            "aspire describe apiservice --format json",
            workspace,
            counter,
            "apiservice.json");
        var apiUrl = GetFirstApiServiceUrl(apiJsonPath);

        var hostWeatherPath = CliE2ETestHelpers.GetWorkspaceFilePath(workspace, "weather.json");
        var containerWeatherPath = CliE2ETestHelpers.ToContainerPath(hostWeatherPath, workspace);
        CliE2ETestHelpers.RegisterCaptureFile("weather.json", hostWeatherPath);

        await auto.TypeAsync($"curl -ksSL {Quote($"{apiUrl.TrimEnd('/')}/weatherforecast")} > {Quote(containerWeatherPath)}");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

        using var document = JsonDocument.Parse(File.ReadAllText(hostWeatherPath));
        Assert.True(document.RootElement.ValueKind is JsonValueKind.Array, $"Expected JSON array in {hostWeatherPath}.");
        Assert.Equal(5, document.RootElement.GetArrayLength());

        await auto.AspireStopAsync(counter);

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }

    private static string GetFirstApiServiceUrl(string jsonPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(jsonPath));
        var resources = document.RootElement.GetProperty("resources");
        foreach (var resource in resources.EnumerateArray())
        {
            if (!resource.TryGetProperty("displayName", out var displayName) ||
                !string.Equals(displayName.GetString(), "apiservice", StringComparison.Ordinal))
            {
                continue;
            }

            if (!resource.TryGetProperty("urls", out var urls))
            {
                continue;
            }

            foreach (var url in urls.EnumerateArray())
            {
                if (!url.TryGetProperty("url", out var value))
                {
                    continue;
                }

                var urlString = value.GetString();
                if (!string.IsNullOrWhiteSpace(urlString))
                {
                    return urlString;
                }
            }
        }

        throw new InvalidOperationException($"Expected an api service URL in {jsonPath}.");
    }

    private static string Quote(string value) => $"\"{value}\"";
}
