// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
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
    [InlineData("StarterXunitV2", "xUnit.net", "v2")]
    [InlineData("Starter.With.1", "xUnit.net", "v3mtp")]
    [InlineData("StarterNUnit", "NUnit", null)]
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

public sealed class SupportProjectTemplateBehaviorTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("aspire-xunit", "SupportTemplate.Xunit", null)]
    [InlineData("aspire-xunit", "SupportTemplate.XunitV3", "v3")]
    [InlineData("aspire-nunit", "SupportTemplate.NUnit", null)]
    [InlineData("aspire-mstest", "SupportTemplate.MSTest", null)]
    [CaptureWorkspaceOnFailure]
    public async Task SupportProjectTemplatesBuildAgainstGeneratedAppHost(string templateName, string projectName, string? xunitVersion)
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
        await DotNetTemplateBehaviorTestHelpers.CreateTemplateBootstrapAsync(auto, counter);

        string[] args = xunitVersion is null ? [] : ["--xunit-version", xunitVersion];
        var testProjectDirectory = await CreateSupportTemplateInBootstrapAsync(auto, counter, workspace, templateName, projectName, args);

        const string appHostProjectName = "TemplateBootstrap.AppHost";
        var testProjectPath = Path.Combine(testProjectDirectory, $"{projectName}.csproj");

        Assert.True(Directory.Exists(testProjectDirectory), $"Expected generated tests directory at {testProjectDirectory}.");
        Assert.True(File.Exists(testProjectPath), $"Expected generated tests project at {testProjectPath}.");

        PrepareSupportProject(testProjectPath, appHostProjectName);
        PrepareSupportTestSource(testProjectDirectory, templateName, appHostProjectName);

        await auto.TypeAsync($"cd {CliE2ETestHelpers.ToContainerPath(testProjectDirectory, workspace)}");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("dotnet build");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(4));

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }

    private static async Task<string> CreateSupportTemplateInBootstrapAsync(
        Hex1bTerminalAutomator auto,
        SequenceCounter counter,
        TemporaryWorkspace workspace,
        string templateName,
        string projectName,
        IReadOnlyList<string> extraArgs)
    {
        var commandParts = new List<string>
        {
            "dotnet",
            "new",
            templateName,
            "-n",
            Quote(projectName),
            "-o",
            Quote($"./{projectName}")
        };

        commandParts.AddRange(extraArgs);

        await auto.TypeAsync(string.Join(" ", commandParts));
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

        return Path.Combine(workspace.WorkspaceRoot.FullName, "TemplateBootstrap", projectName);
    }

    private static void PrepareSupportProject(string testProjectPath, string appHostProjectName)
    {
        var document = XDocument.Load(testProjectPath);
        var project = document.Root ?? throw new InvalidOperationException($"Project root not found in {testProjectPath}.");

        project.Add(new XElement("ItemGroup",
            new XElement("ProjectReference",
                new XAttribute("Include", Path.Combine("..", appHostProjectName, $"{appHostProjectName}.csproj")))));

        document.Save(testProjectPath);
    }

    private static void PrepareSupportTestSource(string testProjectDirectory, string templateName, string appHostProjectName)
    {
        var testSourcePath = Path.Combine(testProjectDirectory, "IntegrationTest1.cs");
        Assert.True(File.Exists(testSourcePath), $"Expected generated test source at {testSourcePath}.");

        var marker = templateName switch
        {
            "aspire-nunit" => "[Test]",
            "aspire-mstest" => "[TestMethod]",
            "aspire-xunit" => "[Fact]",
            _ => throw new ArgumentOutOfRangeException(nameof(templateName), templateName, "Unknown support template name.")
        };

        var uncomment = false;
        var lines = File.ReadAllLines(testSourcePath);
        for (var i = 0; i < lines.Length; i++)
        {
            if (!uncomment && lines[i].Contains(marker, StringComparison.Ordinal))
            {
                uncomment = true;
            }

            if (uncomment)
            {
                lines[i] = UncommentLine(lines[i]);
            }
        }

        var generatedAppHostClassName = Regex.Replace(appHostProjectName, "[^A-Za-z0-9_]", "_");
        var updatedSource = string.Join(Environment.NewLine, lines)
            .Replace("Projects.MyAspireApp_AppHost", $"Projects.{generatedAppHostClassName}", StringComparison.Ordinal);

        File.WriteAllText(testSourcePath, updatedSource + Environment.NewLine);
    }

    private static string UncommentLine(string line)
    {
        var trimmed = line.TrimStart();
        if (!trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            return line;
        }

        var indentation = line[..(line.Length - trimmed.Length)];
        var uncommented = trimmed.Length > 2 && trimmed[2] == ' ' ? trimmed[3..] : trimmed[2..];
        return indentation + uncommented;
    }

    private static string Quote(string value) => $"\"{value}\"";
}
