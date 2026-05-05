// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using Microsoft.AspNetCore.InternalTesting;
using System.Text.Json;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Certificates;
using Aspire.Cli.Commands;
using Aspire.Cli.Configuration;
using Aspire.Cli.DotNet;
using Aspire.Cli.Interaction;
using Aspire.Cli.Packaging;
using Aspire.Cli.Templating;
using Aspire.Cli.Tests.Telemetry;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
using Aspire.Shared;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Aspire.Cli.Tests.Templating;

public class DotNetTemplateFactoryTests
{
    private readonly ITestOutputHelper _outputHelper;

    public DotNetTemplateFactoryTests(ITestOutputHelper outputHelper)
    {
        _outputHelper = outputHelper;
    }

    private static PackageChannel CreateExplicitChannel(PackageMapping[] mappings) =>
        PackageChannel.CreateExplicitChannel("test", PackageChannelQuality.Both, mappings, new FakeNuGetPackageCache());

    private static async Task WriteNuGetConfigAsync(DirectoryInfo dir, string content)
    {
        var path = Path.Combine(dir.FullName, "nuget.config");
        await File.WriteAllTextAsync(path, content);
    }

    /// <summary>
    /// Test that simulates the path comparison logic by testing NuGetConfigMerger behavior
    /// directly, which is what PromptToCreateOrUpdateNuGetConfigAsync will ultimately call.
    /// </summary>
    [Fact]
    public async Task NuGetConfigMerger_InPlaceCreation_WithoutExistingConfig_CreatesInWorkingDirectory()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(_outputHelper);
        var workingDir = workspace.WorkspaceRoot;

        var mappings = new[]
        {
            new PackageMapping("Aspire.*", "https://test.feed.example.com")
        };
        var channel = CreateExplicitChannel(mappings);

        // Act - Simulate in-place creation: output directory same as working directory
        await NuGetConfigMerger.CreateOrUpdateAsync(workingDir, channel).DefaultTimeout();

        // Assert
        var nugetConfigPath = Path.Combine(workingDir.FullName, "nuget.config");
        Assert.True(File.Exists(nugetConfigPath), "nuget.config should be created in working directory for in-place creation");
    }

    [Fact]
    public async Task NuGetConfigMerger_InPlaceCreation_WithExistingConfig_UpdatesWorkingDirectoryConfig()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(_outputHelper);
        var workingDir = workspace.WorkspaceRoot;

        // Create existing NuGet.config in working directory without the required source
        await WriteNuGetConfigAsync(workingDir,
            """
            <?xml version="1.0"?>
            <configuration>
                <packageSources>
                    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
                </packageSources>
            </configuration>
            """);

        var mappings = new[]
        {
            new PackageMapping("Aspire.*", "https://test.feed.example.com")
        };
        var channel = CreateExplicitChannel(mappings);

        // Act - Simulate in-place creation: output directory same as working directory
        await NuGetConfigMerger.CreateOrUpdateAsync(workingDir, channel).DefaultTimeout();

        // Assert
        var nugetConfigPath = Path.Combine(workingDir.FullName, "nuget.config");
        Assert.True(File.Exists(nugetConfigPath), "nuget.config should exist in working directory");

        var content = await File.ReadAllTextAsync(nugetConfigPath);
        Assert.Contains("https://test.feed.example.com", content);
    }

    [Fact]
    public async Task NuGetConfigMerger_SubdirectoryCreation_WithParentConfig_IgnoresParentAndCreatesInOutputDirectory()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(_outputHelper);
        var workingDir = workspace.WorkspaceRoot;
        var outputDir = Directory.CreateDirectory(Path.Combine(workingDir.FullName, "MyProject"));

        // Create existing NuGet.config in working directory (parent)
        var parentConfigContent =
            """
            <?xml version="1.0"?>
            <configuration>
                <packageSources>
                    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
                </packageSources>
            </configuration>
            """;
        await WriteNuGetConfigAsync(workingDir, parentConfigContent).DefaultTimeout();

        var mappings = new[]
        {
            new PackageMapping("Aspire.*", "https://test.feed.example.com")
        };
        var channel = CreateExplicitChannel(mappings);

        // Act - Simulate subdirectory creation: output directory different from working directory
        await NuGetConfigMerger.CreateOrUpdateAsync(outputDir, channel).DefaultTimeout();

        // Assert
        // Parent nuget.config should remain unchanged
        var parentConfigPath = Path.Combine(workingDir.FullName, "nuget.config");
        var parentContent = await File.ReadAllTextAsync(parentConfigPath);
        Assert.Equal(parentConfigContent.ReplaceLineEndings(), parentContent.ReplaceLineEndings());
        Assert.DoesNotContain("https://test.feed.example.com", parentContent);

        // New nuget.config should be created in output directory
        var outputConfigPath = Path.Combine(outputDir.FullName, "nuget.config");
        Assert.True(File.Exists(outputConfigPath), "nuget.config should be created in output directory");

        var outputContent = await File.ReadAllTextAsync(outputConfigPath);
        Assert.Contains("https://test.feed.example.com", outputContent);
    }

    [Fact]
    public async Task NuGetConfigMerger_SubdirectoryCreation_WithExistingConfigInOutputDirectory_MergesInOutputDirectory()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(_outputHelper);
        var workingDir = workspace.WorkspaceRoot;
        var outputDir = Directory.CreateDirectory(Path.Combine(workingDir.FullName, "MyProject"));

        // Create existing NuGet.config in output directory
        await WriteNuGetConfigAsync(outputDir,
            """
            <?xml version="1.0"?>
            <configuration>
                <packageSources>
                    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
                </packageSources>
            </configuration>
            """);

        var mappings = new[]
        {
            new PackageMapping("Aspire.*", "https://test.feed.example.com")
        };
        var channel = CreateExplicitChannel(mappings);

        // Act - Simulate subdirectory creation: merge into existing config in output directory
        await NuGetConfigMerger.CreateOrUpdateAsync(outputDir, channel).DefaultTimeout();

        // Assert
        var outputConfigPath = Path.Combine(outputDir.FullName, "nuget.config");
        Assert.True(File.Exists(outputConfigPath), "nuget.config should exist in output directory");

        var content = await File.ReadAllTextAsync(outputConfigPath);
        Assert.Contains("https://test.feed.example.com", content);
        Assert.Contains("https://api.nuget.org/v3/index.json", content);
    }

    [Fact]
    public async Task NuGetConfigMerger_SubdirectoryCreation_WithoutAnyConfig_CreatesInOutputDirectory()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(_outputHelper);
        var workingDir = workspace.WorkspaceRoot;
        var outputDir = Directory.CreateDirectory(Path.Combine(workingDir.FullName, "MyProject"));

        var mappings = new[]
        {
            new PackageMapping("Aspire.*", "https://test.feed.example.com")
        };
        var channel = CreateExplicitChannel(mappings);

        // Act - Simulate subdirectory creation: create new config in output directory
        await NuGetConfigMerger.CreateOrUpdateAsync(outputDir, channel).DefaultTimeout();

        // Assert
        // No nuget.config should exist in working directory
        var workingConfigPath = Path.Combine(workingDir.FullName, "nuget.config");
        Assert.False(File.Exists(workingConfigPath), "No nuget.config should be created in working directory");

        // New nuget.config should be created in output directory
        var outputConfigPath = Path.Combine(outputDir.FullName, "nuget.config");
        Assert.True(File.Exists(outputConfigPath), "nuget.config should be created in output directory");

        var content = await File.ReadAllTextAsync(outputConfigPath);
        Assert.Contains("https://test.feed.example.com", content);
    }

    [Fact]
    public async Task NuGetConfigMerger_ImplicitChannel_DoesNothing()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(_outputHelper);
        var workingDir = workspace.WorkspaceRoot;
        var outputDir = Directory.CreateDirectory(Path.Combine(workingDir.FullName, "MyProject"));

        var channel = PackageChannel.CreateImplicitChannel(new FakeNuGetPackageCache());

        // Act
        await NuGetConfigMerger.CreateOrUpdateAsync(outputDir, channel).DefaultTimeout();

        // Assert
        // No nuget.config should be created anywhere
        var workingConfigPath = Path.Combine(workingDir.FullName, "nuget.config");
        var outputConfigPath = Path.Combine(outputDir.FullName, "nuget.config");
        Assert.False(File.Exists(workingConfigPath), "No nuget.config should be created for implicit channel");
        Assert.False(File.Exists(outputConfigPath), "No nuget.config should be created for implicit channel");
    }

    [Fact]
    public async Task NuGetConfigMerger_ExplicitChannelWithoutMappings_DoesNothing()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(_outputHelper);
        var workingDir = workspace.WorkspaceRoot;
        var outputDir = Directory.CreateDirectory(Path.Combine(workingDir.FullName, "MyProject"));

        var channel = CreateExplicitChannel([]); // No mappings

        // Act
        await NuGetConfigMerger.CreateOrUpdateAsync(outputDir, channel).DefaultTimeout();

        // Assert
        // No nuget.config should be created anywhere
        var workingConfigPath = Path.Combine(workingDir.FullName, "nuget.config");
        var outputConfigPath = Path.Combine(outputDir.FullName, "nuget.config");
        Assert.False(File.Exists(workingConfigPath), "No nuget.config should be created when no mappings exist");
        Assert.False(File.Exists(outputConfigPath), "No nuget.config should be created when no mappings exist");
    }

    [Fact]
    public async Task GetTemplates_WhenShowAllTemplatesIsEnabled_ReturnsAllTemplates()
    {
        // Arrange
        var features = new TestFeatures().SetFeature(KnownFeatures.ShowAllTemplates, true);
        var factory = CreateTemplateFactory(features);

        // Act
        var templates = (await factory.GetTemplatesAsync()).ToList();

        // Assert
        var templateNames = templates.Select(t => t.Name).ToList();
        Assert.Contains("aspire-starter", templateNames);
        Assert.Contains("aspire", templateNames);
        Assert.Contains("aspire-apphost", templateNames);
        Assert.Contains("aspire-servicedefaults", templateNames);
        Assert.Contains("aspire-test", templateNames);
    }

    [Fact]
    public async Task GetTemplates_WhenShowAllTemplatesIsDisabled_ReturnsOnlyStarterTemplates()
    {
        // Arrange
        var features = new TestFeatures();
        var factory = CreateTemplateFactory(features);

        // Act
        var templates = (await factory.GetTemplatesAsync()).ToList();

        // Assert
        var templateNames = templates.Select(t => t.Name).ToList();
        Assert.Contains("aspire-starter", templateNames);
        Assert.DoesNotContain(KnownTemplateId.DotNetEmptyAppHost, templateNames);
        Assert.DoesNotContain("aspire-apphost", templateNames);
        Assert.DoesNotContain("aspire-servicedefaults", templateNames);
        Assert.DoesNotContain("aspire-test", templateNames);
    }

    [Fact]
    public async Task GetTemplates_SingleFileAppHostIsNotReturned()
    {
        // Arrange
        var features = new TestFeatures();
        var factory = CreateTemplateFactory(features);

        // Act
        var templates = (await factory.GetTemplatesAsync()).ToList();

        // Assert
        var templateNames = templates.Select(t => t.Name).ToList();
        Assert.DoesNotContain("aspire-apphost-singlefile", templateNames);
        Assert.DoesNotContain("aspire-py-starter", templateNames);
    }

    [Fact]
    public async Task GetInitTemplates_IncludesSingleFileAppHostTemplate()
    {
        // Arrange
        var features = new TestFeatures();
        var factory = CreateTemplateFactory(features);

        // Act
        var templates = (await factory.GetInitTemplatesAsync()).ToList();

        // Assert
        var templateNames = templates.Select(t => t.Name).ToList();
        Assert.Contains("aspire-apphost-singlefile", templateNames);
    }

    [Fact]
    public async Task GetTemplates_WhenDotNetSdkIsUnavailable_ReturnsNoTemplates()
    {
        // Arrange
        var features = new TestFeatures().SetFeature(KnownFeatures.ShowAllTemplates, true);
        var sdkInstaller = new TestDotNetSdkInstaller
        {
            CheckAsyncCallback = _ => (false, null, "10.0.100")
        };
        var factory = CreateTemplateFactory(features, sdkInstaller: sdkInstaller);

        // Act
        var templates = await factory.GetTemplatesAsync();

        // Assert
        Assert.Empty(templates);
    }

    private static DotNetTemplateFactory CreateTemplateFactory(TestFeatures features, bool nonInteractive = false, TestDotNetSdkInstaller? sdkInstaller = null)
    {
        var interactionService = new TestInteractionService();
        var runner = new TestDotNetCliRunner();
        var certificateService = new TestCertificateService();
        var packagingService = new TestPackagingService();
        var prompter = new TestNewCommandPrompter();
        var workingDirectory = new DirectoryInfo("/tmp");
        var hivesDirectory = new DirectoryInfo("/tmp/hives");
        var cacheDirectory = new DirectoryInfo("/tmp/cache");
        var executionContext = new CliExecutionContext(workingDirectory, hivesDirectory, cacheDirectory, new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log");
        sdkInstaller ??= new TestDotNetSdkInstaller();
        var configurationService = new FakeConfigurationService();
        var telemetry = TestTelemetryHelper.CreateInitializedTelemetry();
        var hostEnvironment = new FakeCliHostEnvironment(nonInteractive);
        var templateNuGetConfigService = new TemplateNuGetConfigService(interactionService, executionContext, packagingService, configurationService, prompter, hostEnvironment);

        return new DotNetTemplateFactory(
            interactionService,
            runner,
            certificateService,
            prompter,
            executionContext,
            sdkInstaller,
            features,
            telemetry,
            hostEnvironment,
            templateNuGetConfigService);
    }

    private sealed class FakeConfigurationService : IConfigurationService
    {
        public Task SetConfigurationAsync(string key, string value, bool isGlobal = false, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<bool> DeleteConfigurationAsync(string key, bool isGlobal = false, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(false);
        }

        public Task<Dictionary<string, string>> GetAllConfigurationAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new Dictionary<string, string>());
        }

        public Task<Dictionary<string, string>> GetLocalConfigurationAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new Dictionary<string, string>());
        }

        public Task<Dictionary<string, string>> GetGlobalConfigurationAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new Dictionary<string, string>());
        }

        public Task<string?> GetConfigurationAsync(string key, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<string?>(null);
        }

        public string GetSettingsFilePath(bool isGlobal)
        {
            return "/tmp/settings.json";
        }
    }

    private sealed class TestInteractionService : IInteractionService
    {
        public ConsoleOutput Console { get; set; }

        public Task<T> PromptForSelectionAsync<T>(string prompt, IEnumerable<T> choices, Func<T, string> displaySelector, PromptBinding<string?>? binding = null, CancellationToken cancellationToken = default) where T : notnull
            => throw new NotImplementedException();

        public Task<IReadOnlyList<T>> PromptForSelectionsAsync<T>(string promptText, IEnumerable<T> choices, Func<T, string> choiceFormatter, IEnumerable<T>? preSelected = null, bool optional = false, PromptBinding<string?>? binding = null, CancellationToken cancellationToken = default) where T : notnull
            => throw new NotImplementedException();

        public Task<string> PromptForStringAsync(string promptText, Func<string, ValidationResult>? validator = null, bool isSecret = false, bool required = false, PromptBinding<string?>? binding = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<string> PromptForFilePathAsync(string promptText, Func<string, ValidationResult>? validator = null, bool directory = false, bool required = false, PromptBinding<string?>? binding = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<bool> PromptConfirmAsync(string prompt, PromptBinding<bool>? binding = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<TResult> ShowStatusAsync<TResult>(string message, Func<Task<TResult>> work, KnownEmoji? emoji = null, bool allowMarkup = false)
            => throw new NotImplementedException();

        public Task ShowStatusAsync(string message, Func<Task> work)
            => throw new NotImplementedException();

        public void ShowStatus(string message, Action work, KnownEmoji? emoji = null, bool allowMarkup = false)
            => throw new NotImplementedException();

        public void DisplaySuccess(string message, bool allowMarkup = false) { }
        public void DisplayError(string message) { }
        public void DisplayMessage(KnownEmoji emoji, string message, bool allowMarkup = false) { }
        public void DisplayLines(IEnumerable<(OutputLineStream Stream, string Line)> lines) { }
        public void DisplayCancellationMessage() { }
        public int DisplayIncompatibleVersionError(AppHostIncompatibleException ex, string appHostHostingVersion) => 0;
        public void DisplayPlainText(string text) { }
        public void DisplayRawText(string text, ConsoleOutput? consoleOverride = null) { }
        public void DisplayMarkdown(string markdown, ConsoleOutput? consoleOverride = null, int? maxWidth = null) { }
        public void DisplayMarkupLine(string markup) { }
        public void DisplaySubtleMessage(string message, bool allowMarkup = false) { }
        public void DisplayEmptyLine() { }
        public void DisplayVersionUpdateNotification(string message, string? updateCommand = null) { }
        public void WriteConsoleLog(string message, int? resourceHashCode, string? resourceName, bool isError) { }
        public void DisplayRenderable(IRenderable renderable) { }
        public Task DisplayLiveAsync(IRenderable initialRenderable, Func<Action<IRenderable>, Task> callback) => callback(_ => { });
    }

    private sealed class TestDotNetCliRunner : IDotNetCliRunner
    {
        public Task<(int ExitCode, string? TemplateVersion)> InstallTemplateAsync(string packageName, string version, FileInfo? nugetConfigFile, string? nugetSource, bool force, ProcessInvocationOptions options, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<int> NewProjectAsync(string templateName, string projectName, string outputPath, string[] extraArgs, ProcessInvocationOptions? options, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<int> RestoreAsync(FileInfo projectFile, ProcessInvocationOptions options, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<int> BuildAsync(FileInfo projectFile, bool noRestore, ProcessInvocationOptions options, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<int> AddPackageAsync(FileInfo projectFile, string packageName, string version, string? packageSourceUrl, bool noRestore, ProcessInvocationOptions options, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<int> AddProjectToSolutionAsync(FileInfo solutionFile, FileInfo projectFile, ProcessInvocationOptions options, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<(int ExitCode, IReadOnlyList<FileInfo> Projects)> GetSolutionProjectsAsync(FileInfo solutionFile, ProcessInvocationOptions options, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<int> AddProjectReferenceAsync(FileInfo projectFile, FileInfo referencedProjectFile, ProcessInvocationOptions options, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<(int ExitCode, NuGetPackageCli[]? Packages)> SearchPackagesAsync(DirectoryInfo workingDirectory, string query, bool exactMatch, bool prerelease, int take, int skip, FileInfo? nugetConfigFile, bool useCache, ProcessInvocationOptions options, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<(int ExitCode, bool IsAspireHost, string? AspireHostingVersion)> GetAppHostInformationAsync(FileInfo projectFile, ProcessInvocationOptions options, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<(int ExitCode, JsonDocument? Output)> GetProjectItemsAndPropertiesAsync(FileInfo projectFile, string[] items, string[] properties, ProcessInvocationOptions options, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<int> RunAsync(FileInfo projectFile, bool watch, bool noBuild, bool noRestore, string[] args, IDictionary<string, string>? env, TaskCompletionSource<IAppHostCliBackchannel>? backchannelCompletionSource, ProcessInvocationOptions options, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<(int ExitCode, string[] ConfigPaths)> GetNuGetConfigPathsAsync(DirectoryInfo workingDirectory, ProcessInvocationOptions options, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<int> InitUserSecretsAsync(FileInfo projectFile, ProcessInvocationOptions options, CancellationToken cancellationToken)
            => Task.FromResult(0);
    }

    private sealed class TestCertificateService : ICertificateService
    {
        public Task<EnsureCertificatesTrustedResult> EnsureCertificatesTrustedAsync(CancellationToken cancellationToken)
            => Task.FromResult(new EnsureCertificatesTrustedResult
            {
                EnvironmentVariables = new Dictionary<string, string>(),
                Success = true
            });
    }

    private sealed class TestNewCommandPrompter : INewCommandPrompter, ITemplateVersionPrompter
    {
        public Task<string> PromptForProjectNameAsync(string defaultName, ParseResult parseResult, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<string> PromptForOutputPath(string defaultPath, ParseResult parseResult, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<(Aspire.Shared.NuGetPackageCli Package, PackageChannel Channel)> PromptForTemplatesVersionAsync(IEnumerable<(Aspire.Shared.NuGetPackageCli Package, PackageChannel Channel)> packages, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<ITemplate> PromptForTemplateAsync(ITemplate[] templates, CancellationToken cancellationToken)
            => throw new NotImplementedException();
    }

    private sealed class FakeCliHostEnvironment(bool nonInteractive) : ICliHostEnvironment
    {
        public bool SupportsInteractiveInput => !nonInteractive;
        public bool SupportsInteractiveOutput => !nonInteractive;
        public bool SupportsAnsi => false;
    }
}
