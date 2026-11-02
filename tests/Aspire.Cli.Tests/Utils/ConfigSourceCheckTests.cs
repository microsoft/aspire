// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Utils.EnvironmentChecker;
using Aspire.Shared.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Utils;

public class ConfigSourceCheckTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckAsync_ReportsCandidateWithoutAcquisition(bool explicitSource)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var detector = new TestConfigSourceDetector
        {
            IsInternal = true,
            Outcome = explicitSource ? InternalMicrosoftDetectorOutcome.Failed : null
        };
        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            [CliConfigNames.InitSourceUrl] = explicitSource ? "https://git.example/configuration" : null,
            [CliConfigNames.InitSourcePath] = explicitSource ? "teams/platform.json" : "../ignored",
            [CliConfigNames.InitSourceRef] = explicitSource ? "config-preview" : ""
        });
        var git = new TestConfigSourceRepository();
        var interaction = new TestInteractionService();
        interaction.SetupCancellationResponse();
        using var sources = TestConfigSourceServices.Create(workspace.CreateExecutionContext(),
            environment: environment, detector: detector, git: git, interaction: interaction, interactive: true);
        var check = new ConfigSourceCheck(sources, new TestProjectLocator(), workspace.CreateExecutionContext(),
            NullLogger<ConfigSourceCheck>.Instance);

        var result = Assert.Single(await check.CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal(ConfigSourceCheck.MachineCheckName, result.Name);
        Assert.Equal(EnvironmentCheckCategories.Environment, result.Category);
        Assert.Equal(EnvironmentCheckStatus.Pass, result.Status);
        Assert.Equal(ConfigSourceStrings.DoctorAvailable, result.Message);
        Assert.True(result.Metadata!["available"]!.GetValue<bool>());
        Assert.Equal(explicitSource ? CliConfigNames.InitSourceUrl : "microsoftEmployee", result.Metadata["origin"]!.GetValue<string>());
        Assert.Equal(explicitSource ? "https://git.example/configuration" : ConfigSourceService.MicrosoftRepository,
            result.Metadata["repository"]!.GetValue<string>());
        Assert.Equal(explicitSource ? "teams/platform.json" : AspireConfigFile.FileName, result.Metadata["path"]!.GetValue<string>());
        Assert.Equal(explicitSource ? "config-preview" : null, result.Metadata["ref"]?.GetValue<string>());
        Assert.Equal(explicitSource ? 0 : 1, detector.Calls);
        Assert.Equal(0, git.Calls);
        Assert.Equal(0, git.CheckCalls);
        Assert.Empty(interaction.DisplayedMessages);
        Assert.False(File.Exists(Path.Combine(workspace.Path, AspireConfigFile.FileName)));
        Assert.False(Directory.Exists(Path.Combine(workspace.CreateExecutionContext().AspireHomeDirectory.FullName, "configsources")));
    }

    [Fact]
    public async Task CheckAsync_ReportsConfirmedAbsence()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var sources = TestConfigSourceServices.Create(workspace.CreateExecutionContext());
        var check = new ConfigSourceCheck(sources, new TestProjectLocator(), workspace.CreateExecutionContext(),
            NullLogger<ConfigSourceCheck>.Instance);

        var result = Assert.Single(await check.CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal(ConfigSourceCheck.MachineCheckName, result.Name);
        Assert.Equal(EnvironmentCheckStatus.Pass, result.Status);
        Assert.Equal(ConfigSourceStrings.DoctorUnavailable, result.Message);
        Assert.False(result.Metadata!["available"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData(InternalMicrosoftDetectorOutcome.Failed, "bound")]
    [InlineData(InternalMicrosoftDetectorOutcome.Failed, "optedOut")]
    [InlineData(InternalMicrosoftDetectorOutcome.Failed, "unconfigured")]
    [InlineData(InternalMicrosoftDetectorOutcome.TimedOut, "bound")]
    [InlineData(InternalMicrosoftDetectorOutcome.TimedOut, "optedOut")]
    [InlineData(InternalMicrosoftDetectorOutcome.TimedOut, "unconfigured")]
    public async Task CheckAsync_InconclusiveDetectionReportsWarningWithoutHidingSavedChoice(string outcome, string state)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHost = new FileInfo(Path.Combine(workspace.Path, "apphost.cs"));
        var path = Path.Combine(workspace.Path, AspireConfigFile.FileName);
        var configuration = state switch
        {
            "bound" => """{"configSource":{"repository":"https://saved.example/configuration","path":"saved.json"}}""",
            "optedOut" => """{"configSource":{"optOut":true}}""",
            _ => """{"custom":"keep"}"""
        };
        await File.WriteAllTextAsync(path, configuration);
        var git = new TestConfigSourceRepository();
        var interaction = new TestInteractionService();
        using var sources = TestConfigSourceServices.Create(workspace.CreateExecutionContext(), git: git, interaction: interaction,
            detector: new TestConfigSourceDetector { IsInternal = true, Outcome = outcome });
        var locator = new TestProjectLocator { GetAppHostFromSettingsAsyncCallback = _ => Task.FromResult<FileInfo?>(appHost) };
        var check = new ConfigSourceCheck(sources, locator, workspace.CreateExecutionContext(), NullLogger<ConfigSourceCheck>.Instance);

        var results = await check.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(state == "unconfigured" ? 1 : 2, results.Count);
        var machine = results[0];
        Assert.Equal(ConfigSourceCheck.MachineCheckName, machine.Name);
        Assert.Equal(EnvironmentCheckStatus.Warning, machine.Status);
        Assert.Equal(ConfigSourceStrings.DoctorCandidateFailed, machine.Message);
        Assert.Equal(outcome, machine.Metadata!["detectionOutcome"]!.GetValue<string>());
        Assert.Null(machine.Metadata["available"]);
        Assert.Null(machine.Fix);
        if (state != "unconfigured")
        {
            var project = results[1];
            Assert.Equal(ConfigSourceCheck.AppHostCheckName, project.Name);
            Assert.Equal(EnvironmentCheckStatus.Pass, project.Status);
            Assert.Equal(state == "bound" ? ConfigSourceStrings.DoctorBound : ConfigSourceStrings.DoctorOptOut, project.Message);
            Assert.Equal(state, project.Metadata!["state"]!.GetValue<string>());
            Assert.Null(project.Fix);
        }
        Assert.Equal(configuration, await File.ReadAllTextAsync(path));
        Assert.Equal(0, git.Calls);
        Assert.Equal(0, git.CheckCalls);
        Assert.Empty(interaction.DisplayedMessages);
    }

    [Theory]
    [InlineData("bound", false)]
    [InlineData("bound", true)]
    [InlineData("optedOut", false)]
    [InlineData("optedOut", true)]
    [InlineData("unconfigured", false)]
    [InlineData("unconfigured", true)]
    public async Task CheckAsync_UsesSelectedAppHostsNearestConfiguration(string state, bool available)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var parentPath = Path.Combine(workspace.Path, AspireConfigFile.FileName);
        const string parentConfiguration = """{"configSource":{"repository":"https://parent.example/configuration","path":"parent.json"}}""";
        await File.WriteAllTextAsync(parentPath, parentConfiguration);
        var project = workspace.CreateDirectory("AppHost");
        var appHost = new FileInfo(Path.Combine(project.FullName, "apphost.cs"));
        await File.WriteAllTextAsync(appHost.FullName, "#:sdk Aspire.AppHost.Sdk");
        var path = Path.Combine(project.FullName, AspireConfigFile.FileName);
        var configuration = state switch
        {
            "bound" => """{"configSource":{"repository":"https://saved.example/configuration","path":"saved.json","ref":"saved-branch","appliedVersion":"saved-version"}}""",
            "optedOut" => """{"ConfigSource":{"optOut":true}}""",
            _ => """{"custom":"keep"}"""
        };
        await File.WriteAllTextAsync(path, configuration);
        var locator = new TestProjectLocator
        {
            GetAppHostFromSettingsAsyncCallback = _ => Task.FromResult<FileInfo?>(appHost),
            UseOrFindAppHostProjectFileAsyncCallback = (_, _, _) => throw new InvalidOperationException("Doctor must not select or persist a project.")
        };
        var git = new TestConfigSourceRepository();
        using var sources = TestConfigSourceServices.Create(workspace.CreateExecutionContext(), git: git,
            detector: new TestConfigSourceDetector { IsInternal = available });
        var check = new ConfigSourceCheck(sources, locator, workspace.CreateExecutionContext(), NullLogger<ConfigSourceCheck>.Instance);

        var results = await check.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(state == "unconfigured" && !available ? 1 : 2, results.Count);
        if (results.Count == 2)
        {
            var result = results[1];
            Assert.Equal(ConfigSourceCheck.AppHostCheckName, result.Name);
            Assert.Equal(EnvironmentCheckCategories.AppHost, result.Category);
            Assert.Equal(state == "unconfigured" ? EnvironmentCheckStatus.Warning : EnvironmentCheckStatus.Pass, result.Status);
            Assert.Equal(state, result.Metadata!["state"]!.GetValue<string>());
            Assert.Equal(appHost.FullName, result.Metadata["appHostPath"]!.GetValue<string>());
            Assert.Equal(path, result.Metadata["settingsFile"]!.GetValue<string>());
            Assert.Equal(state == "unconfigured" ? ConfigSourceStrings.DoctorAdoptFix : null, result.Fix);
            if (state == "bound")
            {
                Assert.Equal("https://saved.example/configuration", result.Metadata["repository"]!.GetValue<string>());
                Assert.Equal("saved.json", result.Metadata["path"]!.GetValue<string>());
                Assert.Equal("saved-branch", result.Metadata["ref"]!.GetValue<string>());
                Assert.Equal("saved-version", result.Metadata["appliedVersion"]!.GetValue<string>());
            }
        }
        Assert.Equal(configuration, await File.ReadAllTextAsync(path));
        Assert.Equal(parentConfiguration, await File.ReadAllTextAsync(parentPath));
        Assert.Equal(0, git.Calls);
        Assert.Equal(0, git.CheckCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CheckAsync_OnlyInspectsAnUnambiguousCurrentDirectoryAppHost(int candidateCount)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var candidates = Enumerable.Range(0, candidateCount)
            .Select(index => new FileInfo(Path.Combine(workspace.Path, $"AppHost{index}.csproj"))).ToList();
        var locator = new TestProjectLocator
        {
            FindAppHostProjectFilesWithDepthAsyncCallback = (directory, scope, depth, _, _) =>
            {
                Assert.Equal(workspace.Path, directory.FullName);
                Assert.Equal(AppHostDiscoveryScope.ExplicitDirectory, scope);
                Assert.Equal(0, depth);
                return Task.FromResult(candidates);
            }
        };
        using var sources = TestConfigSourceServices.Create(workspace.CreateExecutionContext(),
            detector: new TestConfigSourceDetector { IsInternal = true });
        var check = new ConfigSourceCheck(sources, locator, workspace.CreateExecutionContext(), NullLogger<ConfigSourceCheck>.Instance);

        var results = await check.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(candidateCount == 1 ? 2 : 1, results.Count);
        Assert.Equal(candidateCount == 1 ? EnvironmentCheckStatus.Warning : EnvironmentCheckStatus.Pass, results[^1].Status);
        Assert.False(File.Exists(Path.Combine(workspace.Path, AspireConfigFile.FileName)));
    }

    [Fact]
    public async Task CheckAsync_InvalidCandidateDoesNotHideSavedOptOut()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHost = new FileInfo(Path.Combine(workspace.Path, "apphost.cs"));
        var path = Path.Combine(workspace.Path, AspireConfigFile.FileName);
        const string configuration = """{"configSource":{"optOut":true}}""";
        await File.WriteAllTextAsync(path, configuration);
        using var sources = TestConfigSourceServices.Create(workspace.CreateExecutionContext(),
            environment: new TestEnvironment(new Dictionary<string, string?> { [CliConfigNames.InitSourceUrl] = "" }));
        var locator = new TestProjectLocator { GetAppHostFromSettingsAsyncCallback = _ => Task.FromResult<FileInfo?>(appHost) };
        var check = new ConfigSourceCheck(sources, locator, workspace.CreateExecutionContext(), NullLogger<ConfigSourceCheck>.Instance);

        var results = await check.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Collection(results,
            machine =>
            {
                Assert.Equal(EnvironmentCheckStatus.Warning, machine.Status);
                Assert.Equal(ConfigSourceStrings.DoctorCandidateFailed, machine.Message);
                Assert.Equal(ConfigSourceStrings.InvalidBinding, machine.Details);
            },
            project =>
            {
                Assert.Equal(EnvironmentCheckStatus.Pass, project.Status);
                Assert.Equal(ConfigSourceStrings.DoctorOptOut, project.Message);
                Assert.Null(project.Fix);
            });
        Assert.Equal(configuration, await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("""{"repository":"https://git.example/configuration"}""")]
    public async Task CheckAsync_InvalidSavedBindingReportsWarningWithoutChangingIt(string binding)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHost = new FileInfo(Path.Combine(workspace.Path, "apphost.cs"));
        var path = Path.Combine(workspace.Path, AspireConfigFile.FileName);
        var configuration = $$"""{"configSource":{{binding}}}""";
        await File.WriteAllTextAsync(path, configuration);
        var git = new TestConfigSourceRepository();
        using var sources = TestConfigSourceServices.Create(workspace.CreateExecutionContext(), git: git);
        var locator = new TestProjectLocator { GetAppHostFromSettingsAsyncCallback = _ => Task.FromResult<FileInfo?>(appHost) };
        var check = new ConfigSourceCheck(sources, locator, workspace.CreateExecutionContext(), NullLogger<ConfigSourceCheck>.Instance);

        var results = await check.CheckAsync(TestContext.Current.CancellationToken);

        var project = results[1];
        Assert.Equal(EnvironmentCheckStatus.Warning, project.Status);
        Assert.Equal(ConfigSourceStrings.DoctorProjectFailed, project.Message);
        Assert.Equal(ConfigSourceStrings.InvalidBinding, project.Details);
        Assert.Equal(configuration, await File.ReadAllTextAsync(path));
        Assert.Equal(0, git.Calls);
        Assert.Equal(0, git.CheckCalls);
    }

    [Fact]
    public async Task CheckAsync_CancellationPropagates()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var sources = TestConfigSourceServices.Create(workspace.CreateExecutionContext());
        var locator = new TestProjectLocator
        {
            GetAppHostFromSettingsAsyncCallback = token =>
            {
                token.ThrowIfCancellationRequested();
                return Task.FromResult<FileInfo?>(null);
            }
        };
        var check = new ConfigSourceCheck(sources, locator, workspace.CreateExecutionContext(), NullLogger<ConfigSourceCheck>.Instance);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check.CheckAsync(cancellation.Token));
    }
}
