// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Certificates;
using Aspire.Cli.Commands;
using Aspire.Cli.Configuration;
using Aspire.Cli.DotNet;
using Aspire.Cli.Interaction;
using Aspire.Cli.NuGet;
using Aspire.Cli.Packaging;
using Aspire.Cli.Templating;
using Aspire.Cli.Tests.Utils;
using Aspire.Shared;
using Spectre.Console;

namespace Aspire.Cli.Tests.Templating;

public class DotNetTemplateFactoryTests
{
    private readonly ITestOutputHelper _outputHelper;

    public DotNetTemplateFactoryTests(ITestOutputHelper outputHelper)
    {
        _outputHelper = outputHelper;
    }

    private sealed class FakeNuGetPackageCache : INuGetPackageCache
    {
        public Task<IEnumerable<Aspire.Shared.NuGetPackageCli>> GetTemplatePackagesAsync(DirectoryInfo workingDirectory, bool prerelease, FileInfo? nugetConfigFile, CancellationToken cancellationToken)
        {
            _ = workingDirectory; _ = prerelease; _ = nugetConfigFile; _ = cancellationToken; return Task.FromResult<IEnumerable<Aspire.Shared.NuGetPackageCli>>([]);
        }
        public Task<IEnumerable<Aspire.Shared.NuGetPackageCli>> GetIntegrationPackagesAsync(DirectoryInfo workingDirectory, bool prerelease, FileInfo? nugetConfigFile, CancellationToken cancellationToken)
        {
            _ = workingDirectory; _ = prerelease; _ = nugetConfigFile; _ = cancellationToken; return Task.FromResult<IEnumerable<Aspire.Shared.NuGetPackageCli>>([]);
        }
        public Task<IEnumerable<Aspire.Shared.NuGetPackageCli>> GetCliPackagesAsync(DirectoryInfo workingDirectory, bool prerelease, FileInfo? nugetConfigFile, CancellationToken cancellationToken)
        {
            _ = workingDirectory; _ = prerelease; _ = nugetConfigFile; _ = cancellationToken; return Task.FromResult<IEnumerable<Aspire.Shared.NuGetPackageCli>>([]);
        }
        public Task<IEnumerable<Aspire.Shared.NuGetPackageCli>> GetPackagesAsync(DirectoryInfo workingDirectory, string packageId, Func<string, bool>? filter, bool prerelease, FileInfo? nugetConfigFile, bool useCache, CancellationToken cancellationToken)
        {
            _ = workingDirectory; _ = packageId; _ = filter; _ = prerelease; _ = nugetConfigFile; _ = useCache; _ = cancellationToken; return Task.FromResult<IEnumerable<Aspire.Shared.NuGetPackageCli>>([]);
        }
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
        await NuGetConfigMerger.CreateOrUpdateAsync(workingDir, channel);

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
        await NuGetConfigMerger.CreateOrUpdateAsync(workingDir, channel);

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
        await WriteNuGetConfigAsync(workingDir, parentConfigContent);

        var mappings = new[]
        {
            new PackageMapping("Aspire.*", "https://test.feed.example.com")
        };
        var channel = CreateExplicitChannel(mappings);

        // Act - Simulate subdirectory creation: output directory different from working directory
        await NuGetConfigMerger.CreateOrUpdateAsync(outputDir, channel);

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
        await NuGetConfigMerger.CreateOrUpdateAsync(outputDir, channel);

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
        await NuGetConfigMerger.CreateOrUpdateAsync(outputDir, channel);

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
        await NuGetConfigMerger.CreateOrUpdateAsync(outputDir, channel);

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
        await NuGetConfigMerger.CreateOrUpdateAsync(outputDir, channel);

        // Assert
        // No nuget.config should be created anywhere
        var workingConfigPath = Path.Combine(workingDir.FullName, "nuget.config");
        var outputConfigPath = Path.Combine(outputDir.FullName, "nuget.config");
        Assert.False(File.Exists(workingConfigPath), "No nuget.config should be created when no mappings exist");
        Assert.False(File.Exists(outputConfigPath), "No nuget.config should be created when no mappings exist");
    }

    [Fact]
    public void GetTemplates_WhenShowAllTemplatesIsEnabled_ReturnsAllTemplates()
    {
        // Arrange
        var features = new TestFeatures(showAllTemplates: true);
        var factory = CreateTemplateFactory(features);

        // Act
        var templates = factory.GetTemplates().ToList();

        // Assert
        var templateNames = templates.Select(t => t.Name).ToList();
        Assert.Contains("aspire-starter", templateNames);
        Assert.Contains("aspire", templateNames);
        Assert.Contains("aspire-apphost", templateNames);
        Assert.Contains("aspire-servicedefaults", templateNames);
        Assert.Contains("aspire-test", templateNames);
    }

    [Fact]
    public void GetTemplates_WhenShowAllTemplatesIsDisabled_ReturnsOnlyStarterTemplates()
    {
        // Arrange
        var features = new TestFeatures(showAllTemplates: false);
        var factory = CreateTemplateFactory(features);

        // Act
        var templates = factory.GetTemplates().ToList();

        // Assert
        var templateNames = templates.Select(t => t.Name).ToList();
        Assert.Contains("aspire-starter", templateNames);
        Assert.DoesNotContain("aspire", templateNames);
        Assert.DoesNotContain("aspire-apphost", templateNames);
        Assert.DoesNotContain("aspire-servicedefaults", templateNames);
        Assert.DoesNotContain("aspire-test", templateNames);
    }

    [Fact]
    public void GetTemplates_SingleFileAppHostIsAlwaysVisible()
    {
        // Arrange - single-file templates should always be visible now
        var features = new TestFeatures(showAllTemplates: false);
        var factory = CreateTemplateFactory(features);

        // Act
        var templates = factory.GetTemplates().ToList();

        // Assert
        var templateNames = templates.Select(t => t.Name).ToList();
        Assert.Contains("aspire-apphost-singlefile", templateNames);
        Assert.Contains("aspire-py-starter", templateNames);
    }

    private static DotNetTemplateFactory CreateTemplateFactory(TestFeatures features)
    {
        var interactionService = new TestInteractionService();
        var runner = new TestDotNetCliRunner();
        var certificateService = new TestCertificateService();
        var packagingService = new TestPackagingService();
        var prompter = new TestNewCommandPrompter();
        var workingDirectory = new DirectoryInfo("/tmp");
        var hivesDirectory = new DirectoryInfo("/tmp/hives");
        var cacheDirectory = new DirectoryInfo("/tmp/cache");
        var executionContext = new CliExecutionContext(workingDirectory, hivesDirectory, cacheDirectory, new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")));
        var configurationService = new FakeConfigurationService();

        return new DotNetTemplateFactory(
            interactionService,
            runner,
            certificateService,
            packagingService,
            prompter,
            executionContext,
            features,
            configurationService);
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

        public Task<string?> GetConfigurationAsync(string key, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<string?>(null);
        }

        public string GetSettingsFilePath(bool isGlobal)
        {
            return "/tmp/settings.json";
        }
    }

    private sealed class TestFeatures : IFeatures
    {
        private readonly bool _showAllTemplates;

        public TestFeatures(bool showAllTemplates = false)
        {
            _showAllTemplates = showAllTemplates;
        }

        public bool IsFeatureEnabled(string featureFlag, bool defaultValue)
        {
            return featureFlag switch
            {
                "showAllTemplates" => _showAllTemplates,
                _ => defaultValue
            };
        }
    }

    private sealed class TestInteractionService : IInteractionService
    {
        public Task<T> PromptForSelectionAsync<T>(string prompt, IEnumerable<T> choices, Func<T, string> displaySelector, CancellationToken cancellationToken) where T : notnull
            => throw new NotImplementedException();

        public Task<IReadOnlyList<T>> PromptForSelectionsAsync<T>(string promptText, IEnumerable<T> choices, Func<T, string> choiceFormatter, CancellationToken cancellationToken = default) where T : notnull
            => throw new NotImplementedException();

        public Task<string> PromptForStringAsync(string promptText, string? defaultValue = null, Func<string, ValidationResult>? validator = null, bool isSecret = false, bool required = false, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<bool> ConfirmAsync(string prompt, bool defaultAnswer, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<TResult> ShowStatusAsync<TResult>(string message, Func<Task<TResult>> work)
            => throw new NotImplementedException();

        public Task ShowStatusAsync(string message, Func<Task> work)
            => throw new NotImplementedException();

        public void ShowStatus(string message, Action work)
            => throw new NotImplementedException();

        public void DisplaySuccess(string message) { }
        public void DisplayError(string message) { }
        public void DisplayMessage(string emoji, string message) { }
        public void DisplayLines(IEnumerable<(string Stream, string Line)> lines) { }
        public void DisplayCancellationMessage() { }
        public int DisplayIncompatibleVersionError(AppHostIncompatibleException ex, string appHostHostingVersion) => 0;
        public void DisplayPlainText(string text) { }
        public void DisplayMarkdown(string markdown) { }
        public void DisplaySubtleMessage(string message, bool escapeMarkup = true) { }
        public void DisplayEmptyLine() { }
        public void DisplayVersionUpdateNotification(string message, string? updateCommand = null) { }
        public void WriteConsoleLog(string message, int? resourceHashCode, string? resourceName, bool isError) { }
    }

    private sealed class TestDotNetCliRunner : IDotNetCliRunner
    {
        public Task<(int ExitCode, string? TemplateVersion)> InstallTemplateAsync(string packageName, string version, FileInfo? nugetConfigFile, string? nugetSource, bool force, DotNetCliRunnerInvocationOptions options, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<int> NewProjectAsync(string templateName, string projectName, string outputPath, string[] extraArgs, DotNetCliRunnerInvocationOptions? options, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<int> BuildAsync(FileInfo projectFile, DotNetCliRunnerInvocationOptions options, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<int> AddPackageAsync(FileInfo projectFile, string packageName, string version, string? packageSourceUrl, DotNetCliRunnerInvocationOptions options, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<int> AddProjectToSolutionAsync(FileInfo solutionFile, FileInfo projectFile, DotNetCliRunnerInvocationOptions options, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<(int ExitCode, IReadOnlyList<FileInfo> Projects)> GetSolutionProjectsAsync(FileInfo solutionFile, DotNetCliRunnerInvocationOptions options, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<int> AddProjectReferenceAsync(FileInfo projectFile, FileInfo referencedProjectFile, DotNetCliRunnerInvocationOptions options, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<(int ExitCode, NuGetPackageCli[]? Packages)> SearchPackagesAsync(DirectoryInfo workingDirectory, string query, bool prerelease, int take, int skip, FileInfo? nugetConfigFile, bool useCache, DotNetCliRunnerInvocationOptions options, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<(int ExitCode, bool IsAspireHost, string? AspireHostingVersion)> GetAppHostInformationAsync(FileInfo projectFile, DotNetCliRunnerInvocationOptions options, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<(int ExitCode, JsonDocument? Output)> GetProjectItemsAndPropertiesAsync(FileInfo projectFile, string[] items, string[] properties, DotNetCliRunnerInvocationOptions options, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<int> RunAsync(FileInfo projectFile, bool watch, bool noBuild, string[] args, IDictionary<string, string>? env, TaskCompletionSource<IAppHostCliBackchannel>? backchannelCompletionSource, DotNetCliRunnerInvocationOptions options, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<int> CheckHttpCertificateAsync(DotNetCliRunnerInvocationOptions options, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<int> TrustHttpCertificateAsync(DotNetCliRunnerInvocationOptions options, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<(int ExitCode, string[] ConfigPaths)> GetNuGetConfigPathsAsync(DirectoryInfo workingDirectory, DotNetCliRunnerInvocationOptions options, CancellationToken cancellationToken)
            => throw new NotImplementedException();
    }

    private sealed class TestCertificateService : ICertificateService
    {
        public Task EnsureCertificatesTrustedAsync(IDotNetCliRunner runner, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class TestPackagingService : IPackagingService
    {
        public Task<IEnumerable<PackageChannel>> GetChannelsAsync(CancellationToken cancellationToken)
            => throw new NotImplementedException();
    }

    private sealed class TestNewCommandPrompter : INewCommandPrompter
    {
        public Task<string> PromptForProjectNameAsync(string defaultName, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<string> PromptForOutputPath(string defaultPath, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<(Aspire.Shared.NuGetPackageCli Package, PackageChannel Channel)> PromptForTemplatesVersionAsync(IEnumerable<(Aspire.Shared.NuGetPackageCli Package, PackageChannel Channel)> packages, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<ITemplate> PromptForTemplateAsync(ITemplate[] templates, CancellationToken cancellationToken)
            => throw new NotImplementedException();
    }
}