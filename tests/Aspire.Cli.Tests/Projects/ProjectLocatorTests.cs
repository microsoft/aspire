// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.InternalTesting;
using System.Text.Json;
using Aspire.Cli.Configuration;
using Aspire.Cli.DotNet;
using Aspire.Cli.Interaction;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Tests.Telemetry;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Projects;

public class ProjectLocatorTests(ITestOutputHelper outputHelper)
{
    private static Aspire.Cli.CliExecutionContext CreateExecutionContext(DirectoryInfo workingDirectory, IReadOnlyDictionary<string, string?>? environmentVariables = null)
    {
        // NOTE: This would normally be in the users home directory, but for tests we create
        //       it in the temporary workspace directory.
        var settingsDirectory = workingDirectory.CreateSubdirectory(".aspire");
        var hivesDirectory = settingsDirectory.CreateSubdirectory("hives");
        var cacheDirectory = new DirectoryInfo(Path.Combine(workingDirectory.FullName, ".aspire", "cache"));
        return new CliExecutionContext(workingDirectory, hivesDirectory, cacheDirectory, new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log", environmentVariables: environmentVariables);
    }

    [Fact]
    public async Task UseOrFindAppHostProjectFileThrowsIfExplicitProjectFileDoesNotExist()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var projectFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocator(executionContext);

        var ex = await Assert.ThrowsAsync<ProjectLocatorException>(async () => {
            await projectLocator.UseOrFindAppHostProjectFileAsync(projectFile, createSettingsFile: true).DefaultTimeout();
        });

        Assert.Equal(ErrorStrings.ProjectFileDoesntExist, ex.Message);
    }

    [Fact]
    public async Task UseOrFindAppHostProjectFileUsesCachedSettingsWhenStillValidAmongMultipleAppHosts()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var targetAppHostDirectory = workspace.WorkspaceRoot.CreateSubdirectory("TargetAppHost");
        var targetAppHostProjectFile = new FileInfo(Path.Combine(targetAppHostDirectory.FullName, "TargetAppHost.csproj"));
        await File.WriteAllTextAsync(targetAppHostProjectFile.FullName, "Not a real apphost");

        var otherAppHostDirectory = workspace.WorkspaceRoot.CreateSubdirectory("OtherAppHost");
        var otherAppHostProjectFile = new FileInfo(Path.Combine(otherAppHostDirectory.FullName, "OtherAppHost.csproj"));
        await File.WriteAllTextAsync(otherAppHostProjectFile.FullName, "Not a real apphost");

        var workspaceSettingsDirectory = workspace.CreateDirectory(".aspire");
        var aspireSettingsFile = new FileInfo(Path.Combine(workspaceSettingsDirectory.FullName, "settings.json"));

        using var writer = aspireSettingsFile.OpenWrite();
        await JsonSerializer.SerializeAsync(writer, new
        {
            appHostPath = Path.GetRelativePath(aspireSettingsFile.Directory!.FullName, targetAppHostProjectFile.FullName)
        });
        writer.Close();

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocator(executionContext);

        // With multiple apphosts and a valid cached selection, should use the cached one
        // without prompting.
        var foundAppHost = await projectLocator.UseOrFindAppHostProjectFileAsync(null, createSettingsFile: true).DefaultTimeout();

        Assert.Equal(targetAppHostProjectFile.FullName, foundAppHost?.FullName);
    }

    [Fact]
    public async Task UseOrFindAppHostProjectFileUsesAppHostSpecifiedInSettings()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var targetAppHostDirectory = workspace.WorkspaceRoot.CreateSubdirectory("TargetAppHost");
        var targetAppHostProjectFile = new FileInfo(Path.Combine(targetAppHostDirectory.FullName, "TargetAppHost.csproj"));
        await File.WriteAllTextAsync(targetAppHostProjectFile.FullName, "Not a real apphost");

        var workspaceSettingsDirectory = workspace.CreateDirectory(".aspire");
        var aspireSettingsFile = new FileInfo(Path.Combine(workspaceSettingsDirectory.FullName, "settings.json"));

        using var writer = aspireSettingsFile.OpenWrite();
        await JsonSerializer.SerializeAsync(writer, new
        {
            appHostPath = Path.GetRelativePath(aspireSettingsFile.Directory!.FullName, targetAppHostProjectFile.FullName)
        });
        writer.Close();

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocator(executionContext);

        // With a single apphost, the settings file should be used
        var foundAppHost = await projectLocator.UseOrFindAppHostProjectFileAsync(null, createSettingsFile: true).DefaultTimeout();

        Assert.Equal(targetAppHostProjectFile.FullName, foundAppHost?.FullName);
    }

    [Fact]
    public async Task UseOrFindAppHostProjectFileUsesAppHostSpecifiedInSettingsWalksTree()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var dir1 = workspace.WorkspaceRoot.CreateSubdirectory("dir1");
        var dir2 = dir1.CreateSubdirectory("dir2");

        var targetAppHostDirectory = workspace.WorkspaceRoot.CreateSubdirectory("TargetAppHost");
        var targetAppHostProjectFile = new FileInfo(Path.Combine(targetAppHostDirectory.FullName, "TargetAppHost.csproj"));
        await File.WriteAllTextAsync(targetAppHostProjectFile.FullName, "Not a real apphost");

        var workspaceSettingsDirectory = workspace.CreateDirectory(".aspire");
        var aspireSettingsFile = new FileInfo(Path.Combine(workspaceSettingsDirectory.FullName, "settings.json"));

        using var writer = aspireSettingsFile.OpenWrite();
        await JsonSerializer.SerializeAsync(writer, new
        {
            appHostPath = Path.GetRelativePath(aspireSettingsFile.Directory!.FullName, targetAppHostProjectFile.FullName)
        });
        writer.Close();

        var executionContext = CreateExecutionContext(dir2);
        var projectLocator = CreateProjectLocator(executionContext);

        var foundAppHost = await projectLocator.UseOrFindAppHostProjectFileAsync(null, createSettingsFile: true).DefaultTimeout();

        Assert.Equal(targetAppHostProjectFile.FullName, foundAppHost?.FullName);
    }

    [Fact]
    public async Task UseOrFindAppHostProjectFileFallsBackWhenSettingsFileSpecifiesNonexistentAppHost()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Create a real apphost project file that can be discovered by scanning
        var realAppHostProjectFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "RealAppHost.csproj"));
        await File.WriteAllTextAsync(realAppHostProjectFile.FullName, "Not a real apphost project");

        // Create settings file that points to a non-existent apphost file
        var workspaceSettingsDirectory = workspace.CreateDirectory(".aspire");
        var aspireSettingsFile = new FileInfo(Path.Combine(workspaceSettingsDirectory.FullName, "settings.json"));

        using var writer = aspireSettingsFile.OpenWrite();
        await JsonSerializer.SerializeAsync(writer, new
        {
            appHostPath = "NonexistentAppHost/NonexistentAppHost.csproj"
        });
        writer.Close();

        var projectFactory = new TestAppHostProjectFactory
        {
            ValidateAppHostCallback = projectFile =>
            {
                if (projectFile.FullName == realAppHostProjectFile.FullName)
                {
                    return new AppHostValidationResult(IsValid: true);
                }
                return new AppHostValidationResult(IsValid: false);
            }
        };

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocator(executionContext, projectFactory: projectFactory);

        // This should fallback to scanning and find the real apphost project
        var foundAppHost = await projectLocator.UseOrFindAppHostProjectFileAsync(null, createSettingsFile: true).DefaultTimeout();

        Assert.Equal(realAppHostProjectFile.FullName, foundAppHost?.FullName);
    }

    [Fact]
    public async Task UseOrFindAppHostProjectFileFallsBackWhenSettingsFileSpecifiesExistingNonAppHost()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var staleProjectDirectory = workspace.WorkspaceRoot.CreateSubdirectory("StaleProject");
        var staleProjectFile = new FileInfo(Path.Combine(staleProjectDirectory.FullName, "StaleProject.csproj"));
        await File.WriteAllTextAsync(staleProjectFile.FullName, "Not a real apphost project");

        var realAppHostProjectFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "RealAppHost.csproj"));
        await File.WriteAllTextAsync(realAppHostProjectFile.FullName, "Not a real apphost project");

        var workspaceSettingsDirectory = workspace.CreateDirectory(".aspire");
        var aspireSettingsFile = new FileInfo(Path.Combine(workspaceSettingsDirectory.FullName, "settings.json"));

        using var writer = aspireSettingsFile.OpenWrite();
        await JsonSerializer.SerializeAsync(writer, new
        {
            appHostPath = Path.GetRelativePath(aspireSettingsFile.Directory!.FullName, staleProjectFile.FullName)
        });
        writer.Close();

        var projectFactory = new TestAppHostProjectFactory
        {
            ValidateAppHostCallback = projectFile =>
            {
                if (projectFile.FullName == realAppHostProjectFile.FullName)
                {
                    return new AppHostValidationResult(IsValid: true);
                }

                return new AppHostValidationResult(IsValid: false);
            }
        };

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocator(executionContext, projectFactory: projectFactory);

        var foundAppHost = await projectLocator.UseOrFindAppHostProjectFileAsync(null, createSettingsFile: false).DefaultTimeout();

        Assert.Equal(realAppHostProjectFile.FullName, foundAppHost?.FullName);
    }

    [Fact]
    public async Task UseOrFindAppHostProjectFileUsesValidSettingsWithoutScanning()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var targetAppHostDirectory = workspace.WorkspaceRoot.CreateSubdirectory("TargetAppHost");
        var targetAppHostProjectFile = new FileInfo(Path.Combine(targetAppHostDirectory.FullName, "TargetAppHost.csproj"));
        await File.WriteAllTextAsync(targetAppHostProjectFile.FullName, "Not a real apphost");

        var decoyAppHostDirectory = workspace.WorkspaceRoot.CreateSubdirectory("DecoyAppHost");
        var decoyAppHostProjectFile = new FileInfo(Path.Combine(decoyAppHostDirectory.FullName, "DecoyAppHost.csproj"));
        await File.WriteAllTextAsync(decoyAppHostProjectFile.FullName, "Not a real apphost");

        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new
        {
            appHost = new
            {
                path = Path.GetRelativePath(workspace.WorkspaceRoot.FullName, targetAppHostProjectFile.FullName).Replace(Path.DirectorySeparatorChar, '/')
            }
        }));

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocator(
            executionContext,
            languageDiscovery: new ThrowingLanguageDiscovery());

        var result = await projectLocator.UseOrFindAppHostProjectFileAsync(
            projectFile: null,
            multipleAppHostProjectsFoundBehavior: MultipleAppHostProjectsFoundBehavior.Prompt,
            createSettingsFile: false,
            CancellationToken.None).DefaultTimeout();

        Assert.Equal(targetAppHostProjectFile.FullName, result.SelectedProjectFile?.FullName);
        var candidate = Assert.Single(result.AllProjectFileCandidates);
        Assert.Equal(targetAppHostProjectFile.FullName, candidate.FullName);
    }

    [Fact]
    public async Task UseOrFindAppHostProjectFileScansWhenCandidateListingModeHasValidSettings()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var targetAppHostDirectory = workspace.WorkspaceRoot.CreateSubdirectory("TargetAppHost");
        var targetAppHostProjectFile = new FileInfo(Path.Combine(targetAppHostDirectory.FullName, "TargetAppHost.csproj"));
        await File.WriteAllTextAsync(targetAppHostProjectFile.FullName, "Not a real apphost");

        var otherAppHostDirectory = workspace.WorkspaceRoot.CreateSubdirectory("OtherAppHost");
        var otherAppHostProjectFile = new FileInfo(Path.Combine(otherAppHostDirectory.FullName, "OtherAppHost.csproj"));
        await File.WriteAllTextAsync(otherAppHostProjectFile.FullName, "Not a real apphost");

        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new
        {
            appHost = new
            {
                path = Path.GetRelativePath(workspace.WorkspaceRoot.FullName, targetAppHostProjectFile.FullName).Replace(Path.DirectorySeparatorChar, '/')
            }
        }));

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocator(executionContext);

        var result = await projectLocator.UseOrFindAppHostProjectFileAsync(
            projectFile: null,
            multipleAppHostProjectsFoundBehavior: MultipleAppHostProjectsFoundBehavior.None,
            createSettingsFile: false,
            CancellationToken.None).DefaultTimeout();

        Assert.Equal(targetAppHostProjectFile.FullName, result.SelectedProjectFile?.FullName);
        Assert.Equal(2, result.AllProjectFileCandidates.Count);
        Assert.Contains(result.AllProjectFileCandidates, file => file.FullName == targetAppHostProjectFile.FullName);
        Assert.Contains(result.AllProjectFileCandidates, file => file.FullName == otherAppHostProjectFile.FullName);
    }

    [Fact]
    public async Task UseOrFindAppHostProjectFileIncludesSettingsAppHostInCandidatesWhenOutsideDiscovery()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Settings AppHost lives in the workspace root.
        var settingsAppHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "SettingsAppHost.csproj"));
        await File.WriteAllTextAsync(settingsAppHostFile.FullName, "Not a real apphost");

        // CLI working directory is a subdirectory; discovery from there cannot reach files in the parent.
        var workingDirectory = workspace.WorkspaceRoot.CreateSubdirectory("WorkingDir");

        var configPath = Path.Combine(workingDirectory.FullName, AspireConfigFile.FileName);
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new
        {
            appHost = new
            {
                path = "../SettingsAppHost.csproj"
            }
        }));

        var executionContext = CreateExecutionContext(workingDirectory);
        var projectLocator = CreateProjectLocator(executionContext);

        var result = await projectLocator.UseOrFindAppHostProjectFileAsync(
            projectFile: null,
            multipleAppHostProjectsFoundBehavior: MultipleAppHostProjectsFoundBehavior.None,
            createSettingsFile: false,
            CancellationToken.None).DefaultTimeout();

        Assert.Equal(settingsAppHostFile.FullName, result.SelectedProjectFile?.FullName);
        Assert.Contains(result.AllProjectFileCandidates, file => file.FullName == settingsAppHostFile.FullName);
    }

    [Fact]
    public async Task UseOrFindAppHostProjectFileNormalizesForwardSlashesInSettings()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var targetAppHostDirectory = workspace.WorkspaceRoot.CreateSubdirectory("TargetAppHost");
        var targetAppHostProjectFile = new FileInfo(Path.Combine(targetAppHostDirectory.FullName, "TargetAppHost.csproj"));
        await File.WriteAllTextAsync(targetAppHostProjectFile.FullName, "Not a real apphost");

        var workspaceSettingsDirectory = workspace.CreateDirectory(".aspire");
        var aspireSettingsFile = new FileInfo(Path.Combine(workspaceSettingsDirectory.FullName, "settings.json"));

        // Get the relative path and ensure it uses forward slashes (as stored in settings.json)
        var relativePath = Path.GetRelativePath(aspireSettingsFile.Directory!.FullName, targetAppHostProjectFile.FullName);
        var forwardSlashPath = relativePath.Replace(Path.DirectorySeparatorChar, '/');

        using var writer = aspireSettingsFile.OpenWrite();
        await JsonSerializer.SerializeAsync(writer, new
        {
            appHostPath = forwardSlashPath
        });
        writer.Close();

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocator(executionContext);

        var foundAppHost = await projectLocator.UseOrFindAppHostProjectFileAsync(null, createSettingsFile: true).DefaultTimeout();

        // Should successfully find the file even though the path in settings uses forward slashes
        Assert.Equal(targetAppHostProjectFile.FullName, foundAppHost?.FullName);
    }

    [Fact]
    public async Task UseOrFindAppHostProjectFilePromptsWhenMultipleFilesFound()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectFile1 = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost1.csproj"));
        await File.WriteAllTextAsync(projectFile1.FullName, "Not a real project file.");

        var projectFile2 = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost2.csproj"));
        await File.WriteAllTextAsync(projectFile2.FullName, "Not a real project file.");

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocator(executionContext);

        var selectedProjectFile = await projectLocator.UseOrFindAppHostProjectFileAsync(null, createSettingsFile: true).DefaultTimeout();

        Assert.Equal(projectFile1.FullName, selectedProjectFile!.FullName);
    }

    [Fact]
    public async Task UseOrFindAppHostProjectFileOnlyConsidersValidAppHostProjects()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostProject = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(appHostProject.FullName, "Not a real apphost project.");

        var webProject = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "WebProject.csproj"));
        await File.WriteAllTextAsync(webProject.FullName, "Not a real web project.");

        var projectFactory = new TestAppHostProjectFactory
        {
            ValidateAppHostCallback = projectFile =>
            {
                if (projectFile.FullName == appHostProject.FullName)
                {
                    return new AppHostValidationResult(IsValid: true);
                }
                return new AppHostValidationResult(IsValid: false);
            }
        };

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocator(executionContext, projectFactory: projectFactory);
        var foundAppHost = await projectLocator.UseOrFindAppHostProjectFileAsync(null, createSettingsFile: true).DefaultTimeout();
        Assert.Equal(appHostProject.FullName, foundAppHost?.FullName);
    }

    [Fact]
    public async Task UseOrFindAppHostProjectFileThrowsIfNoProjectWasFound()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocator(executionContext);

        var ex = await Assert.ThrowsAsync<ProjectLocatorException>(async () =>{
            await projectLocator.UseOrFindAppHostProjectFileAsync(null, createSettingsFile: true).DefaultTimeout();
        });

        Assert.Equal(ErrorStrings.NoProjectFileFound, ex.Message);
    }

    [Theory]
    [InlineData(".csproj")]
    [InlineData(".fsproj")]
    [InlineData(".vbproj")]
    public async Task UseOrFindAppHostProjectFileReturnsExplicitProjectIfExistsAndProvided(string projectFileExtension)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, $"AppHost{projectFileExtension}"));
        await File.WriteAllTextAsync(projectFile.FullName, "Not a real project file.");

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocator(executionContext);

        var returnedProjectFile = await projectLocator.UseOrFindAppHostProjectFileAsync(projectFile, createSettingsFile: true).DefaultTimeout();

        Assert.Equal(projectFile, returnedProjectFile);
    }

    [Fact]
    public async Task UseOrFindAppHostProjectFileResultUsesOnDiskCasingForExplicitPath()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "On-disk path casing normalization is only required on Windows.");

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectDirectory = workspace.WorkspaceRoot.CreateSubdirectory("MyAppHost");
        var onDiskProjectFile = new FileInfo(Path.Combine(projectDirectory.FullName, "MyAppHost.csproj"));
        await File.WriteAllTextAsync(onDiskProjectFile.FullName, "Not a real project file.");

        static string ToggleCase(string path)
            => string.Concat(path.Select(c => char.IsLetter(c)
                ? (char.IsUpper(c) ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c))
                : c));

        var mismatchedCasePath = ToggleCase(onDiskProjectFile.FullName);
        Assert.False(string.Equals(onDiskProjectFile.FullName, mismatchedCasePath, StringComparison.Ordinal));
        Assert.True(File.Exists(mismatchedCasePath));

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocator(executionContext);

        var result = await projectLocator.UseOrFindAppHostProjectFileAsync(
            new FileInfo(mismatchedCasePath),
            MultipleAppHostProjectsFoundBehavior.Throw,
            createSettingsFile: false,
            CancellationToken.None).DefaultTimeout();

        Assert.NotNull(result.SelectedProjectFile);
        Assert.Equal(onDiskProjectFile.FullName, result.SelectedProjectFile!.FullName);
    }

    [Fact]
    public async Task UseOrFindAppHostProjectFileReturnsProjectFileInDirectoryIfNotExplicitlyProvided()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(projectFile.FullName, "Not a real project file.");

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocator(executionContext);

        var returnedProjectFile = await projectLocator.UseOrFindAppHostProjectFileAsync(null, createSettingsFile: true).DefaultTimeout();
        Assert.Equal(projectFile.FullName, returnedProjectFile!.FullName);
    }

    [Fact]
    public async Task CreateSettingsFileIfNotExistsAsync_UsesForwardSlashPathSeparator()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var srcDirectory = workspace.CreateDirectory("src");
        var appHostDirectory = srcDirectory.CreateSubdirectory("AppHost");
        var appHostProjectFile = new FileInfo(Path.Combine(appHostDirectory.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(appHostProjectFile.FullName, "Not a real project file.");

        var projectFactory = new TestAppHostProjectFactory
        {
            ValidateAppHostCallback = _ => new AppHostValidationResult(IsValid: true)
        };

        // Simulated global settings path for test isolation.
        var globalSettingsFilePath = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "settings.global.json");
        var globalSettingsFile = new FileInfo(globalSettingsFilePath);

        var config = new ConfigurationBuilder().Build();
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var configurationService = new ConfigurationService(config, executionContext, globalSettingsFile, NullLogger<ConfigurationService>.Instance);

        var locator = CreateProjectLocator(executionContext, configurationService: configurationService, projectFactory: projectFactory);

        await locator.UseOrFindAppHostProjectFileAsync(null, createSettingsFile: true, CancellationToken.None).DefaultTimeout();

        var settingsFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName));
        Assert.True(settingsFile.Exists, "Settings file should exist.");

        var settingsJson = await File.ReadAllTextAsync(settingsFile.FullName);
        var settings = JsonSerializer.Deserialize<AspireConfigFile>(settingsJson);

        Assert.NotNull(settings);
        Assert.NotNull(settings!.AppHost?.Path);
        Assert.DoesNotContain('\\', settings.AppHost.Path); // Ensure no backslashes
        Assert.Contains('/', settings.AppHost.Path); // Ensure forward slashes
    }

    [Fact]
    public async Task UseOrFindAppHostProjectFile_MigratesLegacySettingsToAspireConfigJson()
    {
        // Arrange: create a workspace with a legacy .aspire/settings.json
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var appHostDirectory = workspace.WorkspaceRoot.CreateSubdirectory("MyAppHost");
        var appHostProjectFile = new FileInfo(Path.Combine(appHostDirectory.FullName, "MyAppHost.csproj"));
        await File.WriteAllTextAsync(appHostProjectFile.FullName, "Not a real apphost");

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);

        // Write legacy .aspire/settings.json with a valid appHostPath
        // (CreateExecutionContext already created the .aspire directory)
        var aspireSettingsDir = new DirectoryInfo(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire"));
        var aspireSettingsFile = new FileInfo(Path.Combine(aspireSettingsDir.FullName, "settings.json"));
        var relativeAppHostPath = Path
            .GetRelativePath(aspireSettingsDir.FullName, appHostProjectFile.FullName)
            .Replace(Path.DirectorySeparatorChar, '/');
        await File.WriteAllTextAsync(aspireSettingsFile.FullName, JsonSerializer.Serialize(new { appHostPath = relativeAppHostPath }));

        // Use real ConfigurationService so migration actually writes to disk
        var globalSettingsFilePath = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "settings.global.json");
        var globalSettingsFile = new FileInfo(globalSettingsFilePath);
        var config = new ConfigurationBuilder().Build();
        var configurationService = new ConfigurationService(config, executionContext, globalSettingsFile, NullLogger<ConfigurationService>.Instance);

        var locator = CreateProjectLocator(executionContext, configurationService: configurationService);

        // Act
        var foundAppHost = await locator.UseOrFindAppHostProjectFileAsync(null, createSettingsFile: true, CancellationToken.None).DefaultTimeout();

        // Assert: correct AppHost was found via legacy settings migration
        Assert.NotNull(foundAppHost);
        Assert.Equal(appHostProjectFile.FullName, foundAppHost.FullName);

        // Assert: aspire.config.json was created by migration
        var aspireConfigFilePath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        Assert.True(File.Exists(aspireConfigFilePath), "aspire.config.json should have been created by migration from .aspire/settings.json");

        var configJson = await File.ReadAllTextAsync(aspireConfigFilePath);
        var migratedConfig = JsonSerializer.Deserialize<AspireConfigFile>(configJson);
        Assert.NotNull(migratedConfig?.AppHost?.Path);
    }

    [Fact]
    public async Task FindAppHostProjectFilesAsync_DiscoversSingleFileAppHostInRootDirectory()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Create a valid single-file apphost
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.cs"));
        await File.WriteAllTextAsync(
            appHostFile.FullName,
            """
            #:sdk Aspire.AppHost.Sdk
            using Aspire.Hosting;
            var builder = DistributedApplication.CreateBuilder(args);
            builder.Build().Run();
            """);

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocatorWithSingleFileEnabled(executionContext);

        var foundFiles = await projectLocator.FindAppHostProjectFilesAsync(workspace.WorkspaceRoot.FullName, CancellationToken.None).DefaultTimeout();

        Assert.Single(foundFiles);
        Assert.Equal(appHostFile.FullName, foundFiles[0].FullName);
    }

    [Fact]
    public async Task FindAppHostProjectFilesAsync_DiscoversSingleFileAppHostInSubdirectory()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var subDir = workspace.WorkspaceRoot.CreateSubdirectory("SubProject");
        var appHostFile = new FileInfo(Path.Combine(subDir.FullName, "apphost.cs"));
        await File.WriteAllTextAsync(
            appHostFile.FullName,
            """
            #:sdk Aspire.AppHost.Sdk
            using Aspire.Hosting;
            var builder = DistributedApplication.CreateBuilder(args);
            builder.Build().Run();
            """);

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocatorWithSingleFileEnabled(executionContext);

        var foundFiles = await projectLocator.FindAppHostProjectFilesAsync(workspace.WorkspaceRoot.FullName, CancellationToken.None).DefaultTimeout();

        Assert.Single(foundFiles);
        Assert.Equal(appHostFile.FullName, foundFiles[0].FullName);
    }

    [Fact]
    public async Task FindAppHostProjectFilesAsync_IgnoresSingleFileAppHostWhenSiblingCsprojExists()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Create a subdirectory with both apphost.cs and a .csproj file (single-file apphost should be ignored)
        var dirWithBoth = workspace.WorkspaceRoot.CreateSubdirectory("WithBoth");
        var appHostFile = new FileInfo(Path.Combine(dirWithBoth.FullName, "apphost.cs"));
        await File.WriteAllTextAsync(
            appHostFile.FullName,
            """
            #:sdk Aspire.AppHost.Sdk
            using Aspire.Hosting;
            var builder = DistributedApplication.CreateBuilder(args);
            builder.Build().Run();
            """);

        var csprojFile = new FileInfo(Path.Combine(dirWithBoth.FullName, "RegularProject.csproj"));
        await File.WriteAllTextAsync(csprojFile.FullName, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        // Create another subdirectory with only apphost.cs (single-file apphost should be found)
        var dirWithOnlyAppHost = workspace.WorkspaceRoot.CreateSubdirectory("OnlyAppHost");
        var validAppHostFile = new FileInfo(Path.Combine(dirWithOnlyAppHost.FullName, "apphost.cs"));
        await File.WriteAllTextAsync(
            validAppHostFile.FullName,
            """
            #:sdk Aspire.AppHost.Sdk
            using Aspire.Hosting;
            var builder = DistributedApplication.CreateBuilder(args);
            builder.Build().Run();
            """);

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocatorWithSingleFileEnabled(executionContext, projectFile =>
        {
            // The .csproj in WithBoth is not an AppHost, but has sibling apphost.cs so is "possibly unbuildable"
            if (projectFile.FullName == csprojFile.FullName)
            {
                return new AppHostValidationResult(IsValid: false, IsPossiblyUnbuildable: true);
            }
            return new AppHostValidationResult(IsValid: true);
        });

        var foundFiles = await projectLocator.FindAppHostProjectFilesAsync(workspace.WorkspaceRoot.FullName, CancellationToken.None).DefaultTimeout();

        // Should find the valid single-file apphost (from OnlyAppHost directory)
        // and the potentially unbuildable .csproj (from WithBoth directory due to sibling apphost.cs)
        // but NOT the single-file apphost from WithBoth directory (ignored due to sibling .csproj)
        Assert.Equal(2, foundFiles.Count);

        var foundPaths = foundFiles.Select(f => f.FullName).ToHashSet();
        Assert.Contains(validAppHostFile.FullName, foundPaths);
        Assert.Contains(csprojFile.FullName, foundPaths);
        Assert.DoesNotContain(appHostFile.FullName, foundPaths);
    }

    [Fact]
    public async Task FindAppHostProjectFilesAsync_IgnoresSingleFileAppHostWithoutDirective()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Create an apphost.cs file without the required directive
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.cs"));
        await File.WriteAllTextAsync(appHostFile.FullName, @"using Aspire.Hosting
var builder = DistributedApplication.CreateBuilder(args);
builder.Build().Run();");

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocatorWithSingleFileEnabled(executionContext);

        var foundFiles = await projectLocator.FindAppHostProjectFilesAsync(workspace.WorkspaceRoot.FullName, CancellationToken.None).DefaultTimeout();

        Assert.Empty(foundFiles);
    }

    [Fact]
    public async Task FindAppHostProjectFilesAsync_HandlesMixedAppHostAndSingleFile()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Create a valid .csproj AppHost in subdirectory
        var subDir1 = workspace.WorkspaceRoot.CreateSubdirectory("ProjectAppHost");
        var csprojFile = new FileInfo(Path.Combine(subDir1.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(csprojFile.FullName, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        // Create a valid single-file AppHost in another subdirectory
        var subDir2 = workspace.WorkspaceRoot.CreateSubdirectory("SingleFileAppHost");
        var appHostFile = new FileInfo(Path.Combine(subDir2.FullName, "apphost.cs"));
        await File.WriteAllTextAsync(
            appHostFile.FullName,
            """
            #:sdk Aspire.AppHost.Sdk
            using Aspire.Hosting;
            var builder = DistributedApplication.CreateBuilder(args);
            builder.Build().Run();
            """);

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocatorWithSingleFileEnabled(executionContext, projectFile =>
        {
            if (projectFile.FullName == csprojFile.FullName)
            {
                return new AppHostValidationResult(IsValid: true);
            }
            return new AppHostValidationResult(IsValid: true);
        });

        var foundFiles = await projectLocator.FindAppHostProjectFilesAsync(workspace.WorkspaceRoot.FullName, CancellationToken.None).DefaultTimeout();

        Assert.Equal(2, foundFiles.Count);
        // Verify deterministic ordering (sorted by FullName)
        Assert.True(foundFiles[0].FullName.CompareTo(foundFiles[1].FullName) < 0);

        var foundPaths = foundFiles.Select(f => f.FullName).ToHashSet();
        Assert.Contains(csprojFile.FullName, foundPaths);
        Assert.Contains(appHostFile.FullName, foundPaths);
    }

    [Fact]
    public async Task UseOrFindAppHostProjectFileAsync_AcceptsExplicitSingleFileAppHost()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.cs"));
        await File.WriteAllTextAsync(
            appHostFile.FullName,
            """
            #:sdk Aspire.AppHost.Sdk
            using Aspire.Hosting;
            var builder = DistributedApplication.CreateBuilder(args);
            builder.Build().Run();
            """);

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocatorWithSingleFileEnabled(executionContext);

        var result = await projectLocator.UseOrFindAppHostProjectFileAsync(appHostFile, createSettingsFile: true, CancellationToken.None).DefaultTimeout();

        Assert.Equal(appHostFile.FullName, result!.FullName);
    }

    [Fact]
    public async Task UseOrFindAppHostProjectFileAsync_RejectsInvalidSingleFileAppHost()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Create apphost.cs without directive
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.cs"));
        await File.WriteAllTextAsync(appHostFile.FullName, @"using Aspire.Hosting;
var builder = DistributedApplication.CreateBuilder(args);
builder.Build().Run();");

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocatorWithSingleFileEnabled(executionContext);

        var ex = await Assert.ThrowsAsync<ProjectLocatorException>(async () =>
        {
            await projectLocator.UseOrFindAppHostProjectFileAsync(appHostFile, createSettingsFile: true, CancellationToken.None).DefaultTimeout();
        });

        Assert.Equal(ErrorStrings.ProjectFileDoesntExist, ex.Message);
    }

    [Fact]
    public async Task UseOrFindAppHostProjectFileAsync_AllowsSingleFileAppHostWithSiblingCsproj()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.cs"));
        await File.WriteAllTextAsync(
            appHostFile.FullName,
            """
            #:sdk Aspire.AppHost.Sdk
            using Aspire.Hosting;
            var builder = DistributedApplication.CreateBuilder(args);
            builder.Build().Run();
            """);

        // Add sibling .csproj file
        var csprojFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "SomeProject.csproj"));
        await File.WriteAllTextAsync(csprojFile.FullName, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocatorWithSingleFileEnabled(executionContext);

        // Allow the single-file apphost to be used explicitly even with sibling .csproj
        await projectLocator.UseOrFindAppHostProjectFileAsync(appHostFile, createSettingsFile: true, CancellationToken.None).DefaultTimeout();
    }

    [Fact]
    public async Task UseOrFindAppHostProjectFileAsync_RejectsInvalidFileExtension()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var txtFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "readme.txt"));
        await File.WriteAllTextAsync(txtFile.FullName, "Some text file");

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocator(executionContext);

        var ex = await Assert.ThrowsAsync<ProjectLocatorException>(async () =>
        {
            await projectLocator.UseOrFindAppHostProjectFileAsync(txtFile, createSettingsFile: true, CancellationToken.None).DefaultTimeout();
        });

        Assert.Equal(ErrorStrings.ProjectFileDoesntExist, ex.Message);
    }

    [Fact]
    public async Task UseOrFindAppHostProjectFileAsync_ThrowsMultipleProjectsWhenBothCsprojAndSingleFileFound()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Create a valid .csproj AppHost
        var csprojFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(csprojFile.FullName, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        // Create a valid single-file AppHost in subdirectory (no sibling .csproj)
        var subDir = workspace.WorkspaceRoot.CreateSubdirectory("SingleFile");
        var appHostFile = new FileInfo(Path.Combine(subDir.FullName, "apphost.cs"));
        await File.WriteAllTextAsync(
            appHostFile.FullName,
            """
            #:sdk Aspire.AppHost.Sdk
            using Aspire.Hosting;
            var builder = DistributedApplication.CreateBuilder(args);
            builder.Build().Run();
            """);

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocatorWithSingleFileEnabled(executionContext, projectFile =>
        {
            if (projectFile.FullName == csprojFile.FullName)
            {
                return new AppHostValidationResult(IsValid: true);
            }
            return new AppHostValidationResult(IsValid: true);
        });

        // This should trigger the multiple projects selection, the test service will select the first one
        var result = await projectLocator.UseOrFindAppHostProjectFileAsync(null, createSettingsFile: true, CancellationToken.None).DefaultTimeout();

        // The test interaction service returns the first item
        Assert.NotNull(result);
        // Should be one of the two valid candidates
        Assert.True(result.FullName == csprojFile.FullName || result.FullName == appHostFile.FullName);
    }

    private sealed class TestConfigurationService(CliExecutionContext executionContext) : IConfigurationService
    {
        public Task SetConfigurationAsync(string key, string value, bool isGlobal = false, CancellationToken cancellationToken = default)
        {
            // For test purposes, just return a completed task
            return Task.CompletedTask;
        }

        public Task<bool> DeleteConfigurationAsync(string key, bool isGlobal = false, CancellationToken cancellationToken = default)
        {
            // For test purposes, just return false (not found)
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
            // For test purposes, just return null (not found)
            return Task.FromResult<string?>(null);
        }

        public string GetSettingsFilePath(bool isGlobal)
        {
            return isGlobal
                ? Path.Combine(executionContext.HomeDirectory.FullName, ".aspire", AspireConfigFile.FileName)
                : Path.Combine(executionContext.WorkingDirectory.FullName, AspireConfigFile.FileName);
        }
    }

    private sealed class ThrowingLanguageDiscovery : ILanguageDiscovery
    {
        public Task<IEnumerable<LanguageInfo>> GetAvailableLanguagesAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Recursive AppHost discovery should not run when a valid configured AppHost path exists.");

        public Task<string?> GetPackageForLanguageAsync(LanguageId languageId, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task<LanguageId?> DetectLanguageAsync(DirectoryInfo directory, CancellationToken cancellationToken = default)
            => Task.FromResult<LanguageId?>(null);

        public Task<LanguageId?> DetectLanguageRecursiveAsync(DirectoryInfo directory, CancellationToken cancellationToken = default)
            => Task.FromResult<LanguageId?>(null);

        public LanguageInfo? GetLanguageById(LanguageId languageId)
            => null;

        public LanguageInfo? GetLanguageByFile(FileInfo file)
            => null;
    }

    private static ProjectLocator CreateProjectLocatorWithSingleFileEnabled(CliExecutionContext executionContext, Func<FileInfo, AppHostValidationResult>? validateCallback = null)
    {
        var projectFactory = new TestAppHostProjectFactory();
        if (validateCallback is not null)
        {
            projectFactory.ValidateAppHostCallback = validateCallback;
        }
        return CreateProjectLocator(executionContext, projectFactory: projectFactory);
    }

    [Fact]
    public async Task UseOrFindAppHostProjectFileAcceptsDirectoryPathWithSingleProject()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Create a subdirectory with a single project file
        var projectDirectory = workspace.WorkspaceRoot.CreateSubdirectory("MyAppHost");
        var projectFile = new FileInfo(Path.Combine(projectDirectory.FullName, "MyAppHost.csproj"));
        await File.WriteAllTextAsync(projectFile.FullName, "Not a real project file.");

        var projectFactory = new TestAppHostProjectFactory
        {
            ValidateAppHostCallback = file =>
            {
                if (file.FullName == projectFile.FullName)
                {
                    return new AppHostValidationResult(IsValid: true);
                }
                return new AppHostValidationResult(IsValid: false);
            }
        };

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocator(executionContext, projectFactory: projectFactory);

        // Pass directory as FileInfo (this is how System.CommandLine would parse it)
        var directoryAsFileInfo = new FileInfo(projectDirectory.FullName);
        var returnedProjectFile = await projectLocator.UseOrFindAppHostProjectFileAsync(directoryAsFileInfo, createSettingsFile: true).DefaultTimeout();

        Assert.Equal(projectFile.FullName, returnedProjectFile!.FullName);
    }

    [Fact]
    public async Task UseOrFindAppHostProjectFileThrowsWhenDirectoryHasNoProjects()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Create an empty subdirectory
        var projectDirectory = workspace.WorkspaceRoot.CreateSubdirectory("EmptyDir");

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocator(executionContext);

        // Pass directory as FileInfo
        var directoryAsFileInfo = new FileInfo(projectDirectory.FullName);

        var ex = await Assert.ThrowsAsync<ProjectLocatorException>(async () =>
        {
            await projectLocator.UseOrFindAppHostProjectFileAsync(directoryAsFileInfo, createSettingsFile: true).DefaultTimeout();
        });

        Assert.Equal(ErrorStrings.ProjectFileDoesntExist, ex.Message);
    }

    [Fact]
    public async Task UseOrFindAppHostProjectFilePromptsWhenDirectoryHasMultipleProjects()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Create a subdirectory with multiple project files
        var projectDirectory = workspace.WorkspaceRoot.CreateSubdirectory("MultiProject");
        var projectFile1 = new FileInfo(Path.Combine(projectDirectory.FullName, "Project1.csproj"));
        await File.WriteAllTextAsync(projectFile1.FullName, "Not a real project file.");
        var projectFile2 = new FileInfo(Path.Combine(projectDirectory.FullName, "Project2.csproj"));
        await File.WriteAllTextAsync(projectFile2.FullName, "Not a real project file.");

        var projectFactory = new TestAppHostProjectFactory
        {
            ValidateAppHostCallback = file =>
            {
                // Both projects are AppHost projects
                if (file.FullName == projectFile1.FullName || file.FullName == projectFile2.FullName)
                {
                    return new AppHostValidationResult(IsValid: true);
                }
                return new AppHostValidationResult(IsValid: false);
            }
        };

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocator(executionContext, projectFactory: projectFactory);

        // Pass directory as FileInfo
        var directoryAsFileInfo = new FileInfo(projectDirectory.FullName);

        var returnedProjectFile = await projectLocator.UseOrFindAppHostProjectFileAsync(directoryAsFileInfo, createSettingsFile: true).DefaultTimeout();

        // Should return the first project file (TestInteractionService returns the first choice)
        Assert.Equal(projectFile1.FullName, returnedProjectFile!.FullName);
    }

    [Fact]
    public async Task UseOrFindAppHostProjectFileAcceptsDirectoryPathWithSingleFileAppHost()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Create a subdirectory with a single-file apphost (no .csproj)
        var projectDirectory = workspace.WorkspaceRoot.CreateSubdirectory("MyAppHost");
        var appHostFile = new FileInfo(Path.Combine(projectDirectory.FullName, "apphost.cs"));
        await File.WriteAllTextAsync(
            appHostFile.FullName,
            """
            #:sdk Aspire.AppHost.Sdk
            using Aspire.Hosting;
            var builder = DistributedApplication.CreateBuilder(args);
            builder.Build().Run();
            """);

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocator(executionContext);

        // Pass directory as FileInfo (this is how System.CommandLine would parse it)
        var directoryAsFileInfo = new FileInfo(projectDirectory.FullName);
        var returnedProjectFile = await projectLocator.UseOrFindAppHostProjectFileAsync(directoryAsFileInfo, createSettingsFile: true).DefaultTimeout();

        Assert.Equal(appHostFile.FullName, returnedProjectFile!.FullName);
    }

    [Fact]
    public async Task UseOrFindAppHostProjectFileAcceptsDirectoryPathWithRecursiveSearch()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Create a directory structure with a project in a subdirectory
        var topDirectory = workspace.WorkspaceRoot.CreateSubdirectory("playground");
        var subDirectory = topDirectory.CreateSubdirectory("mongo");
        var projectFile = new FileInfo(Path.Combine(subDirectory.FullName, "Mongo.AppHost.csproj"));
        await File.WriteAllTextAsync(projectFile.FullName, "Not a real project file.");

        var projectFactory = new TestAppHostProjectFactory
        {
            ValidateAppHostCallback = file =>
            {
                if (file.FullName == projectFile.FullName)
                {
                    return new AppHostValidationResult(IsValid: true);
                }
                return new AppHostValidationResult(IsValid: false);
            }
        };

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocator(executionContext, projectFactory: projectFactory);

        // Pass top directory as FileInfo - should find project in subdirectory
        var directoryAsFileInfo = new FileInfo(topDirectory.FullName);
        var returnedProjectFile = await projectLocator.UseOrFindAppHostProjectFileAsync(directoryAsFileInfo, createSettingsFile: true).DefaultTimeout();

        Assert.Equal(projectFile.FullName, returnedProjectFile!.FullName);
    }

    /// <summary>
    /// Regression test for https://github.com/microsoft/aspire/issues/13971
    /// Verifies that AppHost.cs (without SDK directive, with sibling .csproj) is NOT detected as a single-file apphost.
    /// This simulates the .NET starter template structure.
    /// </summary>
    [Fact]
    public async Task FindAppHostProjectFilesAsync_DoesNotDetectAppHostCsWithoutSdkDirective()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Create a directory with AppHost.csproj and AppHost.cs - typical .NET starter template structure
        var appHostDir = workspace.WorkspaceRoot.CreateSubdirectory("MyApp.AppHost");

        // Create the .csproj file (valid AppHost project)
        var csprojFile = new FileInfo(Path.Combine(appHostDir.FullName, "MyApp.AppHost.csproj"));
        await File.WriteAllTextAsync(csprojFile.FullName, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        // Create AppHost.cs WITHOUT the #:sdk directive - this is NOT a single-file apphost
        // This is typical of .NET starter templates where AppHost.cs is just the entry point
        var appHostCsFile = new FileInfo(Path.Combine(appHostDir.FullName, "AppHost.cs"));
        await File.WriteAllTextAsync(appHostCsFile.FullName, """
            var builder = DistributedApplication.CreateBuilder(args);
            builder.Build().Run();
            """);

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocatorWithSingleFileEnabled(executionContext, projectFile =>
        {
            // Only the .csproj should be validated as a valid AppHost
            if (projectFile.Extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                return new AppHostValidationResult(IsValid: true);
            }
            // AppHost.cs without SDK directive should fail validation (handled by TestAppHostProject)
            return new AppHostValidationResult(IsValid: false);
        });

        var foundFiles = await projectLocator.FindAppHostProjectFilesAsync(workspace.WorkspaceRoot.FullName, CancellationToken.None).DefaultTimeout();

        // Should find only the .csproj file, NOT the AppHost.cs file
        Assert.Single(foundFiles);
        Assert.Equal(csprojFile.FullName, foundFiles[0].FullName);
    }

    /// <summary>
    /// Regression test for https://github.com/microsoft/aspire/issues/13971
    /// Verifies that even if apphost.cs has the SDK directive, it is NOT detected if there's a sibling .csproj.
    /// </summary>
    [Fact]
    public async Task FindAppHostProjectFilesAsync_DoesNotDetectAppHostCsWithSiblingCsproj()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Create a directory with both apphost.cs (with SDK directive) AND a .csproj
        var appHostDir = workspace.WorkspaceRoot.CreateSubdirectory("MyApp.AppHost");

        // Create the .csproj file
        var csprojFile = new FileInfo(Path.Combine(appHostDir.FullName, "MyApp.AppHost.csproj"));
        await File.WriteAllTextAsync(csprojFile.FullName, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        // Create apphost.cs WITH the SDK directive but WITH sibling .csproj - should still be rejected
        var appHostCsFile = new FileInfo(Path.Combine(appHostDir.FullName, "apphost.cs"));
        await File.WriteAllTextAsync(appHostCsFile.FullName, """
            #:sdk Aspire.AppHost.Sdk
            var builder = DistributedApplication.CreateBuilder(args);
            builder.Build().Run();
            """);

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        // Don't use a custom callback - let the TestAppHostProject's default validation handle it
        var projectLocator = CreateProjectLocatorWithSingleFileEnabled(executionContext, projectFile =>
        {
            // Only the .csproj should be validated as valid
            if (projectFile.Extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                return new AppHostValidationResult(IsValid: true);
            }
            // .cs files with sibling .csproj should fail (this is what our fix ensures)
            return new AppHostValidationResult(IsValid: false);
        });

        var foundFiles = await projectLocator.FindAppHostProjectFilesAsync(workspace.WorkspaceRoot.FullName, CancellationToken.None).DefaultTimeout();

        // Should find only the .csproj file
        Assert.Single(foundFiles);
        Assert.Equal(csprojFile.FullName, foundFiles[0].FullName);
    }

    /// <summary>
    /// Verifies that a valid single-file apphost (with SDK directive and no sibling .csproj) IS detected.
    /// </summary>
    [Fact]
    public async Task FindAppHostProjectFilesAsync_DetectsValidSingleFileAppHost()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Create a directory with ONLY apphost.cs (no .csproj) - valid single-file apphost
        var appHostDir = workspace.WorkspaceRoot.CreateSubdirectory("SingleFileApp");

        var appHostCsFile = new FileInfo(Path.Combine(appHostDir.FullName, "apphost.cs"));
        await File.WriteAllTextAsync(appHostCsFile.FullName, """
            #:sdk Aspire.AppHost.Sdk
            var builder = DistributedApplication.CreateBuilder(args);
            builder.Build().Run();
            """);

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        // Use default validation - no callback
        var projectLocator = CreateProjectLocatorWithSingleFileEnabled(executionContext);

        var foundFiles = await projectLocator.FindAppHostProjectFilesAsync(workspace.WorkspaceRoot.FullName, CancellationToken.None).DefaultTimeout();

        // Should find the single-file apphost
        Assert.Single(foundFiles);
        Assert.Equal(appHostCsFile.FullName, foundFiles[0].FullName);
    }

    [Fact]
    public async Task FindAppHostProjectFilesAsync_ExcludesDotNetProjectsWhenSdkNotAvailable()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var projectFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(projectFile.FullName, "Not a real project file.");

        var sdkInstaller = new TestDotNetSdkInstaller
        {
            CheckAsyncCallback = _ => (false, null, "10.0.100")
        };

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocator(executionContext, sdkInstaller: sdkInstaller);

        var foundFiles = await projectLocator.FindAppHostProjectFilesAsync(workspace.WorkspaceRoot.FullName, CancellationToken.None).DefaultTimeout();

        Assert.Empty(foundFiles);
    }

    [Fact]
    public async Task FindAppHostProjectFilesAsync_IncludesDotNetProjectsWhenSdkAvailable()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var projectFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(projectFile.FullName, "Not a real project file.");

        var sdkInstaller = new TestDotNetSdkInstaller
        {
            CheckAsyncCallback = _ => (true, "10.0.100", "10.0.100")
        };

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocator(executionContext, sdkInstaller: sdkInstaller);

        var foundFiles = await projectLocator.FindAppHostProjectFilesAsync(workspace.WorkspaceRoot.FullName, CancellationToken.None).DefaultTimeout();

        Assert.Single(foundFiles);
        Assert.Equal(projectFile.FullName, foundFiles[0].FullName);
    }

    [Fact]
    public async Task FindAppHostProjectFilesAsync_DoesNotCheckSdkWhenNoDotNetProjects()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // No project files at all
        var sdkCheckCalled = false;
        var sdkInstaller = new TestDotNetSdkInstaller
        {
            CheckAsyncCallback = _ =>
            {
                sdkCheckCalled = true;
                return (false, null, "10.0.100");
            }
        };

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocator(executionContext, sdkInstaller: sdkInstaller);

        var foundFiles = await projectLocator.FindAppHostProjectFilesAsync(workspace.WorkspaceRoot.FullName, CancellationToken.None).DefaultTimeout();

        Assert.Empty(foundFiles);
        Assert.False(sdkCheckCalled);
    }

    [Fact]
    public async Task FindAppHostProjectFilesAsync_ExcludesProjectsInsideNuGetCache()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Create a valid AppHost project in a normal location
        var normalProject = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "MyApp", "AppHost.csproj"));
        Directory.CreateDirectory(normalProject.DirectoryName!);
        await File.WriteAllTextAsync(normalProject.FullName, "Not a real project file.");

        // Create a project inside a simulated NuGet cache path
        var nugetCacheDir = Path.Combine(workspace.WorkspaceRoot.FullName, ".nuget", "packages",
            "aspire.projecttemplates", "9.1.0", "content", "templates", "aspire-apphost");
        Directory.CreateDirectory(nugetCacheDir);
        var cachedProject = new FileInfo(Path.Combine(nugetCacheDir, "Aspire.AppHost1.csproj"));
        await File.WriteAllTextAsync(cachedProject.FullName, "Not a real project file.");

        var projectFactory = new TestAppHostProjectFactory
        {
            ValidateAppHostCallback = _ => new AppHostValidationResult(IsValid: true)
        };

        var envVars = new Dictionary<string, string?>
        {
            ["NUGET_PACKAGES"] = Path.Combine(workspace.WorkspaceRoot.FullName, ".nuget", "packages")
        };
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot, environmentVariables: envVars);
        var projectLocator = CreateProjectLocator(executionContext, projectFactory: projectFactory);

        var foundFiles = await projectLocator.FindAppHostProjectFilesAsync(
            workspace.WorkspaceRoot.FullName, CancellationToken.None).DefaultTimeout();

        // Should only find the normal project, not the one inside the NuGet cache
        Assert.Single(foundFiles);
        Assert.Equal(normalProject.FullName, foundFiles[0].FullName);
    }

    [Fact]
    public async Task FindAppHostProjectFilesAsync_FindsProjectsOutsideNuGetCache()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Create projects in various subdirectories (none are NuGet cache)
        var project1 = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "src", "App1", "AppHost.csproj"));
        Directory.CreateDirectory(project1.DirectoryName!);
        await File.WriteAllTextAsync(project1.FullName, "Not a real project file.");

        var project2 = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "samples", "App2", "AppHost.csproj"));
        Directory.CreateDirectory(project2.DirectoryName!);
        await File.WriteAllTextAsync(project2.FullName, "Not a real project file.");

        var projectFactory = new TestAppHostProjectFactory
        {
            ValidateAppHostCallback = _ => new AppHostValidationResult(IsValid: true)
        };

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocator(executionContext, projectFactory: projectFactory);

        var foundFiles = await projectLocator.FindAppHostProjectFilesAsync(
            workspace.WorkspaceRoot.FullName, CancellationToken.None).DefaultTimeout();

        Assert.Equal(2, foundFiles.Count);
        var foundPaths = foundFiles.Select(f => f.FullName).ToHashSet();
        Assert.Contains(project1.FullName, foundPaths);
        Assert.Contains(project2.FullName, foundPaths);
    }

    [Fact]
    public async Task FindAppHostProjectFilesAsync_RespectsCustomNuGetPackagesEnvVar()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Create a valid AppHost project in a normal location
        var normalProject = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "MyApp", "AppHost.csproj"));
        Directory.CreateDirectory(normalProject.DirectoryName!);
        await File.WriteAllTextAsync(normalProject.FullName, "Not a real project file.");

        // Create a project inside a custom NuGet cache location (not the default .nuget/packages)
        var customCacheDir = Path.Combine(workspace.WorkspaceRoot.FullName, "custom-nuget-cache");
        var cachedProjectDir = Path.Combine(customCacheDir, "aspire.projecttemplates", "9.1.0", "content");
        Directory.CreateDirectory(cachedProjectDir);
        var cachedProject = new FileInfo(Path.Combine(cachedProjectDir, "Aspire.AppHost1.csproj"));
        await File.WriteAllTextAsync(cachedProject.FullName, "Not a real project file.");

        var projectFactory = new TestAppHostProjectFactory
        {
            ValidateAppHostCallback = _ => new AppHostValidationResult(IsValid: true)
        };

        var envVars = new Dictionary<string, string?>
        {
            ["NUGET_PACKAGES"] = customCacheDir
        };
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot, environmentVariables: envVars);
        var projectLocator = CreateProjectLocator(executionContext, projectFactory: projectFactory);

        var foundFiles = await projectLocator.FindAppHostProjectFilesAsync(
            workspace.WorkspaceRoot.FullName, CancellationToken.None).DefaultTimeout();

        // Should only find the normal project, not the one inside the custom cache
        Assert.Single(foundFiles);
        Assert.Equal(normalProject.FullName, foundFiles[0].FullName);
    }

    [Fact]
    public async Task FindAppHostProjectFilesAsync_DoesNotExcludeSiblingDirectoriesOfNuGetCache()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Use a custom non-hidden cache path so the test isn't affected by hidden-directory skipping
        var customCacheDir = Path.Combine(workspace.WorkspaceRoot.FullName, "nuget-cache");

        // Create a project inside a sibling directory whose name shares a prefix with the cache
        var siblingDir = Path.Combine(workspace.WorkspaceRoot.FullName, "nuget-cache-extra", "MyApp");
        Directory.CreateDirectory(siblingDir);
        var siblingProject = new FileInfo(Path.Combine(siblingDir, "AppHost.csproj"));
        await File.WriteAllTextAsync(siblingProject.FullName, "Not a real project file.");

        // Create a project inside the actual cache that should be excluded
        var cachedProjectDir = Path.Combine(customCacheDir, "aspire.projecttemplates", "9.1.0", "content");
        Directory.CreateDirectory(cachedProjectDir);
        var cachedProject = new FileInfo(Path.Combine(cachedProjectDir, "Aspire.AppHost1.csproj"));
        await File.WriteAllTextAsync(cachedProject.FullName, "Not a real project file.");

        var projectFactory = new TestAppHostProjectFactory
        {
            ValidateAppHostCallback = _ => new AppHostValidationResult(IsValid: true)
        };

        var envVars = new Dictionary<string, string?>
        {
            ["NUGET_PACKAGES"] = customCacheDir
        };
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot, environmentVariables: envVars);
        var projectLocator = CreateProjectLocator(executionContext, projectFactory: projectFactory);

        var foundFiles = await projectLocator.FindAppHostProjectFilesAsync(
            workspace.WorkspaceRoot.FullName, CancellationToken.None).DefaultTimeout();

        // Should find the sibling project but not the one inside the cache
        Assert.Single(foundFiles);
        Assert.Equal(siblingProject.FullName, foundFiles[0].FullName);
    }

    [Fact]
    public async Task UseOrFindAppHostProjectFile_SingleAspireConfigJson_FindsAppHost()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Create one apphost project
        var appHostDir = workspace.WorkspaceRoot.CreateSubdirectory("MyAppHost");
        var appHostFile = new FileInfo(Path.Combine(appHostDir.FullName, "MyAppHost.csproj"));
        await File.WriteAllTextAsync(appHostFile.FullName, "Not a real apphost");

        // Create aspire.config.json pointing to it
        var config = new AspireConfigFile { AppHost = new AspireConfigAppHost { Path = "MyAppHost/MyAppHost.csproj" } };
        config.Save(workspace.WorkspaceRoot.FullName);

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocator(executionContext);

        var result = await projectLocator.UseOrFindAppHostProjectFileAsync(null, MultipleAppHostProjectsFoundBehavior.Throw, createSettingsFile: false, CancellationToken.None).DefaultTimeout();

        Assert.NotNull(result.SelectedProjectFile);
        Assert.Equal(appHostFile.FullName, result.SelectedProjectFile!.FullName);
    }

    [Fact]
    public async Task UseOrFindAppHostProjectFile_MultipleAspireConfigJsonFiles_FallsThroughToScan()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Create two apphost projects, each with its own aspire.config.json
        var appHost1Dir = workspace.WorkspaceRoot.CreateSubdirectory("AppHost1");
        var appHost1File = new FileInfo(Path.Combine(appHost1Dir.FullName, "AppHost1.csproj"));
        await File.WriteAllTextAsync(appHost1File.FullName, "Not a real apphost");
        var config1 = new AspireConfigFile { AppHost = new AspireConfigAppHost { Path = "AppHost1.csproj" } };
        config1.Save(appHost1Dir.FullName);

        var appHost2Dir = workspace.WorkspaceRoot.CreateSubdirectory("AppHost2");
        var appHost2File = new FileInfo(Path.Combine(appHost2Dir.FullName, "AppHost2.csproj"));
        await File.WriteAllTextAsync(appHost2File.FullName, "Not a real apphost");
        var config2 = new AspireConfigFile { AppHost = new AspireConfigAppHost { Path = "AppHost2.csproj" } };
        config2.Save(appHost2Dir.FullName);

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocator(executionContext);

        // Multiple config files → falls through to scan → finds 2 apphosts → Throw
        var ex = await Assert.ThrowsAsync<ProjectLocatorException>(async () =>
        {
            await projectLocator.UseOrFindAppHostProjectFileAsync(null, MultipleAppHostProjectsFoundBehavior.Throw, createSettingsFile: false, CancellationToken.None).DefaultTimeout();
        });

        Assert.Equal(ProjectLocatorFailureReason.MultipleProjectFilesFound, ex.FailureReason);
    }

    [Fact]
    public async Task UseOrFindAppHostProjectFile_AspireConfigJsonPointsToNonexistentFile_FallsThroughToScan()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Create one real apphost project
        var appHostDir = workspace.WorkspaceRoot.CreateSubdirectory("RealAppHost");
        var appHostFile = new FileInfo(Path.Combine(appHostDir.FullName, "RealAppHost.csproj"));
        await File.WriteAllTextAsync(appHostFile.FullName, "Not a real apphost");

        // Create aspire.config.json pointing to a nonexistent apphost
        var config = new AspireConfigFile { AppHost = new AspireConfigAppHost { Path = "NonexistentAppHost/NonexistentAppHost.csproj" } };
        config.Save(workspace.WorkspaceRoot.FullName);

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocator(executionContext);

        // Should fall through to scan and find the real apphost
        var result = await projectLocator.UseOrFindAppHostProjectFileAsync(null, MultipleAppHostProjectsFoundBehavior.Throw, createSettingsFile: false, CancellationToken.None).DefaultTimeout();

        Assert.NotNull(result.SelectedProjectFile);
        Assert.Equal(appHostFile.FullName, result.SelectedProjectFile!.FullName);
    }

    [Fact]
    public async Task UseOrFindAppHostProjectFile_MultipleAppHosts_NoConfig_ThrowBehavior_Throws()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Create two apphost projects with no config files at all
        var appHost1Dir = workspace.WorkspaceRoot.CreateSubdirectory("AppHost1");
        await File.WriteAllTextAsync(Path.Combine(appHost1Dir.FullName, "AppHost1.csproj"), "Not a real apphost");

        var appHost2Dir = workspace.WorkspaceRoot.CreateSubdirectory("AppHost2");
        await File.WriteAllTextAsync(Path.Combine(appHost2Dir.FullName, "AppHost2.csproj"), "Not a real apphost");

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocator(executionContext);

        var ex = await Assert.ThrowsAsync<ProjectLocatorException>(async () =>
        {
            await projectLocator.UseOrFindAppHostProjectFileAsync(null, MultipleAppHostProjectsFoundBehavior.Throw, createSettingsFile: false, CancellationToken.None).DefaultTimeout();
        });

        Assert.Equal(ProjectLocatorFailureReason.MultipleProjectFilesFound, ex.FailureReason);
    }

    [Fact]
    public async Task UseOrFindAppHostProjectFile_MultipleAppHosts_NoConfig_ThrowBehavior_CancelsRemainingValidationEarly()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var appHost1File = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost1.csproj"));
        await File.WriteAllTextAsync(appHost1File.FullName, "Not a real apphost");

        var appHost2File = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost2.csproj"));
        await File.WriteAllTextAsync(appHost2File.FullName, "Not a real apphost");

        var slowValidationFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "SlowValidation.csproj"));
        await File.WriteAllTextAsync(slowValidationFile.FullName, "Not a real apphost");

        var slowValidationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowValidationCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var projectFactory = new TestAppHostProjectFactory
        {
            ValidateAppHostAsyncCallback = async (projectFile, cancellationToken) =>
            {
                if (string.Equals(projectFile.FullName, slowValidationFile.FullName, StringComparison.Ordinal))
                {
                    slowValidationStarted.TrySetResult();

                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        slowValidationCanceled.TrySetResult();
                        throw;
                    }
                }

                if (string.Equals(projectFile.FullName, appHost2File.FullName, StringComparison.Ordinal))
                {
                    await slowValidationStarted.Task.DefaultTimeout();
                }

                return new AppHostValidationResult(IsValid: true);
            }
        };

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocator(executionContext, projectFactory: projectFactory);

        var ex = await Assert.ThrowsAsync<ProjectLocatorException>(async () =>
        {
            await projectLocator.UseOrFindAppHostProjectFileAsync(null, MultipleAppHostProjectsFoundBehavior.Throw, createSettingsFile: false, CancellationToken.None).DefaultTimeout();
        });

        Assert.Equal(ProjectLocatorFailureReason.MultipleProjectFilesFound, ex.FailureReason);
        await slowValidationCanceled.Task.DefaultTimeout();
    }

    [Fact]
    public async Task UseOrFindAppHostProjectFile_WithSettingsAndMultipleAppHosts_ThrowBehavior_UsesCachedSelection()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Create two apphost projects
        var appHost1Dir = workspace.WorkspaceRoot.CreateSubdirectory("AppHost1");
        var appHost1File = new FileInfo(Path.Combine(appHost1Dir.FullName, "AppHost1.csproj"));
        await File.WriteAllTextAsync(appHost1File.FullName, "Not a real apphost");

        var appHost2Dir = workspace.WorkspaceRoot.CreateSubdirectory("AppHost2");
        var appHost2File = new FileInfo(Path.Combine(appHost2Dir.FullName, "AppHost2.csproj"));
        await File.WriteAllTextAsync(appHost2File.FullName, "Not a real apphost");

        // Create settings file that caches AppHost1
        var aspireSettingsDir = new DirectoryInfo(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire"));
        aspireSettingsDir.Create();
        var aspireSettingsFile = new FileInfo(Path.Combine(aspireSettingsDir.FullName, "settings.json"));
        var relativeAppHostPath = Path
            .GetRelativePath(aspireSettingsDir.FullName, appHost1File.FullName)
            .Replace(Path.DirectorySeparatorChar, '/');
        await File.WriteAllTextAsync(aspireSettingsFile.FullName, JsonSerializer.Serialize(new { appHostPath = relativeAppHostPath }));

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocator(executionContext);

        // With Throw behavior, should use the cached selection rather than throwing
        var result = await projectLocator.UseOrFindAppHostProjectFileAsync(null, MultipleAppHostProjectsFoundBehavior.Throw, createSettingsFile: false, CancellationToken.None).DefaultTimeout();

        Assert.NotNull(result.SelectedProjectFile);
        Assert.Equal(appHost1File.FullName, result.SelectedProjectFile!.FullName);
    }

    [Fact]
    public async Task UseOrFindAppHostProjectFile_WithSettingsAndMultipleAppHosts_PromptBehavior_UsesCachedSelection()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Create two apphost projects
        var appHost1Dir = workspace.WorkspaceRoot.CreateSubdirectory("AppHost1");
        var appHost1File = new FileInfo(Path.Combine(appHost1Dir.FullName, "AppHost1.csproj"));
        await File.WriteAllTextAsync(appHost1File.FullName, "Not a real apphost");

        var appHost2Dir = workspace.WorkspaceRoot.CreateSubdirectory("AppHost2");
        var appHost2File = new FileInfo(Path.Combine(appHost2Dir.FullName, "AppHost2.csproj"));
        await File.WriteAllTextAsync(appHost2File.FullName, "Not a real apphost");

        // Create settings file that caches AppHost1
        var aspireSettingsDir = new DirectoryInfo(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire"));
        aspireSettingsDir.Create();
        var aspireSettingsFile = new FileInfo(Path.Combine(aspireSettingsDir.FullName, "settings.json"));
        var relativeAppHostPath = Path
            .GetRelativePath(aspireSettingsDir.FullName, appHost1File.FullName)
            .Replace(Path.DirectorySeparatorChar, '/');
        await File.WriteAllTextAsync(aspireSettingsFile.FullName, JsonSerializer.Serialize(new { appHostPath = relativeAppHostPath }));

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocator(executionContext);

        // With Prompt behavior and a valid cached selection, should use the cached one without scanning
        var result = await projectLocator.UseOrFindAppHostProjectFileAsync(null, MultipleAppHostProjectsFoundBehavior.Prompt, createSettingsFile: false, CancellationToken.None).DefaultTimeout();

        Assert.NotNull(result.SelectedProjectFile);
        Assert.Equal(appHost1File.FullName, result.SelectedProjectFile!.FullName);
        var candidate = Assert.Single(result.AllProjectFileCandidates);
        Assert.Equal(appHost1File.FullName, candidate.FullName);
    }

    private static ProjectLocator CreateProjectLocator(
        CliExecutionContext executionContext,
        IInteractionService? interactionService = null,
        IConfigurationService? configurationService = null,
        IAppHostProjectFactory? projectFactory = null,
        ILanguageDiscovery? languageDiscovery = null,
        IDotNetSdkInstaller? sdkInstaller = null,
        AspireCliTelemetry? telemetry = null)
    {
        var logger = NullLogger<ProjectLocator>.Instance;
        return new ProjectLocator(
            logger,
            executionContext,
            interactionService ?? new TestInteractionService(),
            configurationService ?? new TestConfigurationService(executionContext),
            projectFactory ?? new TestAppHostProjectFactory(),
            languageDiscovery ?? new TestLanguageDiscovery(),
            sdkInstaller ?? new TestDotNetSdkInstaller(),
            telemetry ?? TestTelemetryHelper.CreateInitializedTelemetry());
    }
}
