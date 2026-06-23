// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.InternalTesting;
using System.Text.Json;
using Aspire.Cli.Configuration;
using Aspire.Cli.DotNet;
using Aspire.Cli.Git;
using Aspire.Cli.Interaction;
using Aspire.Cli.Projects;
using Aspire.Cli.Utils;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Tests.Telemetry;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;

namespace Aspire.Cli.Tests.Projects;

public class ProjectLocatorTests(ITestOutputHelper outputHelper)
{
    private static Aspire.Cli.CliExecutionContext CreateExecutionContext(DirectoryInfo workingDirectory, IReadOnlyDictionary<string, string?>? environmentVariables = null)
    {
        return TestExecutionContextHelper.CreateExecutionContext(
            workingDirectory,
            environment: new TestEnvironment(environmentVariables));
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
    public async Task UseOrFindAppHostProjectFileUsesValidSettingsWithoutScanningInThrowMode()
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
            multipleAppHostProjectsFoundBehavior: MultipleAppHostProjectsFoundBehavior.Throw,
            createSettingsFile: false,
            CancellationToken.None).DefaultTimeout();

        Assert.Equal(targetAppHostProjectFile.FullName, result.SelectedProjectFile?.FullName);
        var candidate = Assert.Single(result.AllProjectFileCandidates);
        Assert.Equal(targetAppHostProjectFile.FullName, candidate.FullName);
    }

    [Fact]
    public async Task UseOrFindAppHostProjectFileUsesValidGuestAppHostSettingsWithoutScanning()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var guestAppHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.ts"));
        await File.WriteAllTextAsync(guestAppHostFile.FullName, "// guest apphost");

        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new
        {
            appHost = new
            {
                path = "apphost.ts"
            }
        }));

        var projectFactory = new GuestAppHostFileProjectFactory("apphost.ts");

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocator(
            executionContext,
            projectFactory: projectFactory,
            languageDiscovery: new ThrowingLanguageDiscovery());

        var result = await projectLocator.UseOrFindAppHostProjectFileAsync(
            projectFile: null,
            multipleAppHostProjectsFoundBehavior: MultipleAppHostProjectsFoundBehavior.Prompt,
            createSettingsFile: false,
            CancellationToken.None).DefaultTimeout();

        Assert.Equal(guestAppHostFile.FullName, result.SelectedProjectFile?.FullName);
        var candidate = Assert.Single(result.AllProjectFileCandidates);
        Assert.Equal(guestAppHostFile.FullName, candidate.FullName);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task UseOrFindAppHostProjectFileFallsBackToDiscoveryWhenConfiguredAppHostIsInvalid(bool isUnsupported, bool isPossiblyUnbuildable)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var configuredAppHostDirectory = workspace.WorkspaceRoot.CreateSubdirectory("ConfiguredAppHost");
        var configuredAppHostProjectFile = new FileInfo(Path.Combine(configuredAppHostDirectory.FullName, "ConfiguredAppHost.csproj"));
        await File.WriteAllTextAsync(configuredAppHostProjectFile.FullName, "Not a real apphost");

        var realAppHostProjectFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "RealAppHost.csproj"));
        await File.WriteAllTextAsync(realAppHostProjectFile.FullName, "Not a real apphost");

        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new
        {
            appHost = new
            {
                path = Path.GetRelativePath(workspace.WorkspaceRoot.FullName, configuredAppHostProjectFile.FullName).Replace(Path.DirectorySeparatorChar, '/')
            }
        }));

        var projectFactory = new TestAppHostProjectFactory
        {
            ValidateAppHostCallback = projectFile =>
            {
                if (projectFile.FullName == realAppHostProjectFile.FullName)
                {
                    return new AppHostValidationResult(IsValid: true);
                }
                if (projectFile.FullName == configuredAppHostProjectFile.FullName)
                {
                    return new AppHostValidationResult(IsValid: false, IsUnsupported: isUnsupported, IsPossiblyUnbuildable: isPossiblyUnbuildable);
                }
                return new AppHostValidationResult(IsValid: false);
            }
        };

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocator(executionContext, projectFactory: projectFactory);

        var result = await projectLocator.UseOrFindAppHostProjectFileAsync(
            projectFile: null,
            multipleAppHostProjectsFoundBehavior: MultipleAppHostProjectsFoundBehavior.None,
            createSettingsFile: false,
            CancellationToken.None).DefaultTimeout();

        Assert.Equal(realAppHostProjectFile.FullName, result.SelectedProjectFile?.FullName);
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
        var displayedSubtleMessages = new List<string>();
        var interactionService = new TestInteractionService
        {
            DisplaySubtleMessageCallback = displayedSubtleMessages.Add
        };
        var projectLocator = CreateProjectLocator(executionContext, interactionService: interactionService);

        var result = await projectLocator.UseOrFindAppHostProjectFileAsync(
            projectFile: null,
            multipleAppHostProjectsFoundBehavior: MultipleAppHostProjectsFoundBehavior.None,
            createSettingsFile: false,
            CancellationToken.None).DefaultTimeout();

        Assert.Equal(settingsAppHostFile.FullName, result.SelectedProjectFile?.FullName);
        Assert.Contains(result.AllProjectFileCandidates, file => file.FullName == settingsAppHostFile.FullName);
        Assert.Contains(Path.Join("..", "SettingsAppHost.csproj"), displayedSubtleMessages);
    }

    [Fact]
    public async Task UseOrFindAppHostProjectFileTreatsSettingsAppHostWithoutProjectHandlerAsUnsupported()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var settingsAppHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.custom"));
        await File.WriteAllTextAsync(settingsAppHostFile.FullName, "Not a supported apphost type");

        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new
        {
            appHost = new
            {
                path = "apphost.custom"
            }
        }));

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var interactionService = new TestInteractionService();
        var projectLocator = CreateProjectLocator(executionContext, interactionService: interactionService);

        var exception = await Assert.ThrowsAsync<ProjectLocatorException>(() =>
            projectLocator.UseOrFindAppHostProjectFileAsync(
                projectFile: null,
                multipleAppHostProjectsFoundBehavior: MultipleAppHostProjectsFoundBehavior.None,
                createSettingsFile: false,
                CancellationToken.None)).DefaultTimeout();

        Assert.Equal(ProjectLocatorFailureReason.UnsupportedProjects, exception.FailureReason);
        var warning = Assert.Single(interactionService.DisplayedMessages);
        Assert.Equal(KnownEmojis.Warning, warning.Emoji);
        Assert.Contains("apphost.custom", warning.Message);
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
    public async Task UseOrFindAppHostProjectFileUpdatesStaleConfigForExplicitProjectFile()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var legacyAppHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.ts"));
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.mts"));
        await File.WriteAllTextAsync(appHostFile.FullName, "// TypeScript AppHost");

        await File.WriteAllTextAsync(Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName), JsonSerializer.Serialize(new
        {
            appHost = new
            {
                path = legacyAppHostFile.Name,
                language = KnownLanguageId.TypeScript
            }
        }));

        var globalSettingsFilePath = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "settings.global.json");
        var globalSettingsFile = new FileInfo(globalSettingsFilePath);
        var config = new ConfigurationBuilder().Build();
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var configurationService = new ConfigurationService(config, executionContext, globalSettingsFile, NullLogger<ConfigurationService>.Instance);
        var projectLocator = CreateProjectLocator(
            executionContext,
            configurationService: configurationService,
            projectFactory: new TestTypeScriptStarterProjectFactory((_, _, _) => Task.FromResult(true)));

        var returnedProjectFile = await projectLocator.UseOrFindAppHostProjectFileAsync(appHostFile, createSettingsFile: true).DefaultTimeout();

        Assert.Equal(appHostFile.FullName, returnedProjectFile!.FullName);

        var updatedConfig = AspireConfigFile.Load(workspace.WorkspaceRoot.FullName);
        Assert.Equal("apphost.mts", updatedConfig?.AppHost?.Path);
        Assert.Equal(KnownLanguageId.TypeScript, updatedConfig?.AppHost?.Language);
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
    public async Task UseOrFindAppHostProjectFile_UpdatesAppHostAdjacentConfigWhenDiscoveredFromParent()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var srcDirectory = workspace.WorkspaceRoot.CreateSubdirectory("src");
        var appHostFile = new FileInfo(Path.Combine(srcDirectory.FullName, "apphost.ts"));
        await File.WriteAllTextAsync(appHostFile.FullName, "// TypeScript apphost");

        var adjacentConfigPath = Path.Combine(srcDirectory.FullName, AspireConfigFile.FileName);
        await File.WriteAllTextAsync(
            adjacentConfigPath,
            """
            {
              "sdk": {
                "version": "10.0.0-preview.5.26311.1"
              },
              "channel": "staging",
              "profiles": {
                "http": {
                  "applicationUrl": "http://localhost:15071",
                  "environmentVariables": {
                    "ASPIRE_ALLOW_UNSECURED_TRANSPORT": "true"
                  }
                }
              },
              "packages": {
                "Aspire.Hosting.Redis": ""
              }
            }
            """);

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var globalSettingsFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "settings.global.json"));
        var configurationService = new ConfigurationService(new ConfigurationBuilder().Build(), executionContext, globalSettingsFile, NullLogger<ConfigurationService>.Instance);
        var projectLocator = CreateProjectLocator(
            executionContext,
            configurationService: configurationService,
            projectFactory: new GuestAppHostFileProjectFactory("apphost.ts"),
            languageDiscovery: new TestLanguageDiscovery(new LanguageInfo(
                LanguageId: new LanguageId(KnownLanguageId.TypeScript),
                DisplayName: "TypeScript (Node.js)",
                PackageName: "Aspire.Hosting.JavaScript",
                DetectionPatterns: ["apphost.ts"],
                CodeGenerator: "TypeScript",
                AppHostFileName: "apphost.ts")));

        var result = await projectLocator.UseOrFindAppHostProjectFileAsync(
            projectFile: null,
            multipleAppHostProjectsFoundBehavior: MultipleAppHostProjectsFoundBehavior.Throw,
            createSettingsFile: true,
            CancellationToken.None).DefaultTimeout();

        Assert.Equal(appHostFile.FullName, result.SelectedProjectFile?.FullName);
        Assert.False(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName)));

        var adjacentConfigJson = await File.ReadAllTextAsync(adjacentConfigPath);
        var adjacentConfig = JsonSerializer.Deserialize<AspireConfigFile>(adjacentConfigJson);

        Assert.Equal("apphost.ts", adjacentConfig?.AppHost?.Path);
        Assert.Equal(KnownLanguageId.TypeScript, adjacentConfig?.AppHost?.Language);
        Assert.Contains("\"Aspire.Hosting.Redis\"", adjacentConfigJson, StringComparison.Ordinal);
        Assert.Contains("\"channel\"", adjacentConfigJson, StringComparison.Ordinal);
        Assert.Contains("\"profiles\"", adjacentConfigJson, StringComparison.Ordinal);
        Assert.Contains("\"ASPIRE_ALLOW_UNSECURED_TRANSPORT\"", adjacentConfigJson, StringComparison.Ordinal);
        Assert.Contains("\"sdk\"", adjacentConfigJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UseOrFindAppHostProjectFile_UpdatesSelectedAppHostAdjacentConfigWhenMultipleAppHostsFound()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHost1Directory = workspace.WorkspaceRoot.CreateSubdirectory("AppHost1");
        var appHost1File = new FileInfo(Path.Combine(appHost1Directory.FullName, "apphost.ts"));
        await File.WriteAllTextAsync(appHost1File.FullName, "// TypeScript apphost");
        var appHost1ConfigPath = Path.Combine(appHost1Directory.FullName, AspireConfigFile.FileName);
        await File.WriteAllTextAsync(appHost1ConfigPath, """
            {
              "packages": {
                "Aspire.Hosting.Redis": ""
              }
            }
            """);

        var appHost2Directory = workspace.WorkspaceRoot.CreateSubdirectory("AppHost2");
        var appHost2File = new FileInfo(Path.Combine(appHost2Directory.FullName, "apphost.ts"));
        await File.WriteAllTextAsync(appHost2File.FullName, "// TypeScript apphost");
        var appHost2ConfigPath = Path.Combine(appHost2Directory.FullName, AspireConfigFile.FileName);
        await File.WriteAllTextAsync(appHost2ConfigPath, """
            {
              "packages": {
                "Aspire.Hosting.PostgreSQL": ""
              }
            }
            """);

        var interactionService = new TestInteractionService
        {
            PromptForSelectionCallback = (_, choices, _, _) =>
                choices.Cast<FileInfo>().Single(file => file.Directory?.Name == appHost2Directory.Name)
        };
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var globalSettingsFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "settings.global.json"));
        var configurationService = new ConfigurationService(new ConfigurationBuilder().Build(), executionContext, globalSettingsFile, NullLogger<ConfigurationService>.Instance);
        var projectLocator = CreateProjectLocator(
            executionContext,
            interactionService: interactionService,
            configurationService: configurationService,
            projectFactory: new GuestAppHostFileProjectFactory("apphost.ts"),
            languageDiscovery: new TestLanguageDiscovery(new LanguageInfo(
                LanguageId: new LanguageId(KnownLanguageId.TypeScript),
                DisplayName: "TypeScript (Node.js)",
                PackageName: "Aspire.Hosting.JavaScript",
                DetectionPatterns: ["apphost.ts"],
                CodeGenerator: "TypeScript",
                AppHostFileName: "apphost.ts")));

        var result = await projectLocator.UseOrFindAppHostProjectFileAsync(
            projectFile: null,
            multipleAppHostProjectsFoundBehavior: MultipleAppHostProjectsFoundBehavior.Prompt,
            createSettingsFile: true,
            CancellationToken.None).DefaultTimeout();

        Assert.Equal(appHost2File.FullName, result.SelectedProjectFile?.FullName);
        Assert.False(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName)));

        var appHost1ConfigJson = await File.ReadAllTextAsync(appHost1ConfigPath);
        var appHost1Config = JsonSerializer.Deserialize<AspireConfigFile>(appHost1ConfigJson);
        Assert.Null(appHost1Config?.AppHost?.Path);
        Assert.Contains("\"Aspire.Hosting.Redis\"", appHost1ConfigJson, StringComparison.Ordinal);

        var appHost2ConfigJson = await File.ReadAllTextAsync(appHost2ConfigPath);
        var appHost2Config = JsonSerializer.Deserialize<AspireConfigFile>(appHost2ConfigJson);
        Assert.Equal("apphost.ts", appHost2Config?.AppHost?.Path);
        Assert.Equal(KnownLanguageId.TypeScript, appHost2Config?.AppHost?.Language);
        Assert.Contains("\"Aspire.Hosting.PostgreSQL\"", appHost2ConfigJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UseOrFindAppHostProjectFile_HealsStaleAppHostPathInAdjacentConfig()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var srcDirectory = workspace.WorkspaceRoot.CreateSubdirectory("src");
        var appHostFile = new FileInfo(Path.Combine(srcDirectory.FullName, "apphost.ts"));
        await File.WriteAllTextAsync(appHostFile.FullName, "// TypeScript apphost");

        var staleConfig = new AspireConfigFile
        {
            AppHost = new AspireConfigAppHost
            {
                Path = "missing-apphost.ts",
                Language = KnownLanguageId.TypeScript
            }
        };
        staleConfig.Save(srcDirectory.FullName);

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var globalSettingsFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "settings.global.json"));
        var configurationService = new ConfigurationService(new ConfigurationBuilder().Build(), executionContext, globalSettingsFile, NullLogger<ConfigurationService>.Instance);
        var projectLocator = CreateProjectLocator(
            executionContext,
            configurationService: configurationService,
            projectFactory: new GuestAppHostFileProjectFactory("apphost.ts"),
            languageDiscovery: new TestLanguageDiscovery(new LanguageInfo(
                LanguageId: new LanguageId(KnownLanguageId.TypeScript),
                DisplayName: "TypeScript (Node.js)",
                PackageName: "Aspire.Hosting.JavaScript",
                DetectionPatterns: ["apphost.ts"],
                CodeGenerator: "TypeScript",
                AppHostFileName: "apphost.ts")));

        var result = await projectLocator.UseOrFindAppHostProjectFileAsync(
            projectFile: null,
            multipleAppHostProjectsFoundBehavior: MultipleAppHostProjectsFoundBehavior.Throw,
            createSettingsFile: true,
            CancellationToken.None).DefaultTimeout();

        Assert.Equal(appHostFile.FullName, result.SelectedProjectFile?.FullName);
        Assert.False(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName)));

        var healedConfig = AspireConfigFile.Load(srcDirectory.FullName);
        Assert.Equal("apphost.ts", healedConfig?.AppHost?.Path);
        Assert.Equal(KnownLanguageId.TypeScript, healedConfig?.AppHost?.Language);
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
    public async Task FindAppHostProjectFilesAsync_DoesNotDuplicateFilesThatMatchMultiplePatterns()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var projectFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(projectFile.FullName, "Not a real project file.");

        var overlappingLanguage = new LanguageInfo(
            LanguageId: new LanguageId("overlapping"),
            DisplayName: "Overlapping",
            PackageName: "",
            DetectionPatterns: ["AppHost.csproj"],
            CodeGenerator: "",
            AppHostFileName: "AppHost.csproj");

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocator(
            executionContext,
            languageDiscovery: new TestLanguageDiscovery(overlappingLanguage));

        var foundFiles = await projectLocator.FindAppHostProjectFilesAsync(workspace.WorkspaceRoot.FullName, CancellationToken.None).DefaultTimeout();

        var foundFile = Assert.Single(foundFiles);
        Assert.Equal(projectFile.FullName, foundFile.FullName);
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

        public Task<string?> GetConfigurationFromDirectoryAsync(string key, DirectoryInfo startDirectory, bool continueSearchWhenKeyMissing = false, CancellationToken cancellationToken = default)
        {
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

    private sealed class GuestAppHostFileProjectFactory : IAppHostProjectFactory
    {
        private readonly string _supportedFileName;
        private readonly GuestAppHostTestProject _project;

        public GuestAppHostFileProjectFactory(string supportedFileName)
        {
            _supportedFileName = supportedFileName;
            _project = new GuestAppHostTestProject(supportedFileName);
        }

        public IAppHostProject GetProject(LanguageInfo language) => _project;

        public IAppHostProject GetProject(FileInfo appHostFile)
            => TryGetProject(appHostFile) ?? throw new NotSupportedException($"No handler available for AppHost file '{appHostFile.Name}'.");

        public IAppHostProject? TryGetProject(FileInfo appHostFile)
            => appHostFile.Name.Equals(_supportedFileName, StringComparison.OrdinalIgnoreCase) ? _project : null;

        public IAppHostProject? GetProjectByLanguageId(string languageId)
            => string.Equals(languageId, _project.LanguageId, StringComparison.OrdinalIgnoreCase) ? _project : null;

        public IEnumerable<IAppHostProject> GetAllProjects() => [_project];

        private sealed class GuestAppHostTestProject(string supportedFileName) : IAppHostProject
        {
            public bool IsUnsupported { get; set; }
            public string LanguageId => "typescript";
            public string DisplayName => "TypeScript";
            public string? AppHostFileName => supportedFileName;

            public bool IsUsingProjectReferences(FileInfo appHostFile) => false;

            public Task<string[]> GetDetectionPatternsAsync(CancellationToken cancellationToken)
                => Task.FromResult<string[]>([supportedFileName]);

            public bool CanHandle(FileInfo appHostFile)
                => appHostFile.Name.Equals(supportedFileName, StringComparison.OrdinalIgnoreCase);

            public Task ScaffoldAsync(DirectoryInfo directory, string? projectName, CancellationToken cancellationToken)
                => throw new NotImplementedException();

            public Task<int> RunAsync(AppHostProjectContext context, CancellationToken cancellationToken)
                => throw new NotImplementedException();

            public Task<int> PublishAsync(PublishContext context, CancellationToken cancellationToken)
                => throw new NotImplementedException();

            public Task<IReadOnlyList<(string PackageId, string Version)>> GetPackageReferencesAsync(FileInfo appHostFile, CancellationToken cancellationToken)
                => throw new NotImplementedException();

            public Task<AppHostValidationResult> ValidateAppHostAsync(FileInfo appHostFile, CancellationToken cancellationToken)
                => Task.FromResult(new AppHostValidationResult(IsValid: appHostFile.Name.Equals(supportedFileName, StringComparison.OrdinalIgnoreCase)));

            public Task<string?> GetAspireHostingVersionAsync(FileInfo appHostFile, CancellationToken cancellationToken)
                => Task.FromResult<string?>(VersionHelper.GetDefaultTemplateVersion());

            public Task<bool> AddPackageAsync(AddPackageContext context, CancellationToken cancellationToken)
                => throw new NotImplementedException();

            public Task<UpdatePackagesResult> UpdatePackagesAsync(UpdatePackagesContext context, CancellationToken cancellationToken)
                => throw new NotImplementedException();

            public Task<RunningInstanceResult> FindAndStopRunningInstanceAsync(FileInfo appHostFile, DirectoryInfo homeDirectory, CancellationToken cancellationToken)
                => Task.FromResult(RunningInstanceResult.NoRunningInstance);

            public Task<string?> GetUserSecretsIdAsync(FileInfo appHostFile, bool autoInit, CancellationToken cancellationToken)
                => Task.FromResult<string?>(null);
        }
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
    public async Task FindAppHostProjectsAsync_WritesSdkWarningToErrorStreamWhenFindingCandidatesForLs()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var projectFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(projectFile.FullName, "Not a real project file.");

        var sdkInstaller = new TestDotNetSdkInstaller
        {
            CheckAsyncCallback = _ => (false, null, "10.0.100")
        };
        var interactionService = new TestInteractionService();

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocator(executionContext, interactionService: interactionService, sdkInstaller: sdkInstaller);

        var found = await projectLocator.FindAppHostProjectsAsync(workspace.WorkspaceRoot, AppHostDiscoveryScope.DefaultFiltered, CancellationToken.None).DefaultTimeout();

        Assert.Empty(found);
        Assert.Empty(interactionService.DisplayedErrors);
        var warning = Assert.Single(interactionService.DisplayedRawText);
        Assert.Equal(ErrorStrings.DotNetSdkUnavailableAppHostDiscoveryWarning, warning.Text);
        Assert.Equal(ConsoleOutput.Error, warning.ConsoleOverride);
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

    [Fact]
    public async Task FindAppHostProjectsAsync_DefaultFiltered_ExcludesNodeModulesByDefault_NonGitRepo()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Real AppHost project under a normal directory.
        var realAppHost = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "App", "AppHost.csproj"));
        Directory.CreateDirectory(realAppHost.DirectoryName!);
        await File.WriteAllTextAsync(realAppHost.FullName, "Not a real project file.");

        // Stale copy inside node_modules — must be skipped by default.
        var staleAppHost = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "node_modules", "foo", "AppHost.csproj"));
        Directory.CreateDirectory(staleAppHost.DirectoryName!);
        await File.WriteAllTextAsync(staleAppHost.FullName, "Not a real project file.");

        var projectFactory = new TestAppHostProjectFactory
        {
            ValidateAppHostCallback = _ => new AppHostValidationResult(IsValid: true)
        };

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocator(executionContext, projectFactory: projectFactory);

        var found = await projectLocator.FindAppHostProjectsAsync(workspace.WorkspaceRoot, AppHostDiscoveryScope.DefaultFiltered, CancellationToken.None).DefaultTimeout();

        Assert.Single(found);
        Assert.Equal(realAppHost.FullName, found[0].AppHostFile.FullName);
    }

    [Fact]
    public async Task FindAppHostProjectsAsync_AllFilesScope_IncludesEverything()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var realAppHost = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "App", "AppHost.csproj"));
        Directory.CreateDirectory(realAppHost.DirectoryName!);
        await File.WriteAllTextAsync(realAppHost.FullName, "Not a real project file.");

        var staleAppHost = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "node_modules", "foo", "AppHost.csproj"));
        Directory.CreateDirectory(staleAppHost.DirectoryName!);
        await File.WriteAllTextAsync(staleAppHost.FullName, "Not a real project file.");

        var projectFactory = new TestAppHostProjectFactory
        {
            ValidateAppHostCallback = _ => new AppHostValidationResult(IsValid: true)
        };

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocator(executionContext, projectFactory: projectFactory);

        var found = await projectLocator.FindAppHostProjectsAsync(workspace.WorkspaceRoot, AppHostDiscoveryScope.AllFiles, CancellationToken.None).DefaultTimeout();

        Assert.Equal(2, found.Count);
        var paths = found.Select(c => c.AppHostFile.FullName).ToHashSet();
        Assert.Contains(realAppHost.FullName, paths);
        Assert.Contains(staleAppHost.FullName, paths);
    }

    [Fact]
    public async Task FindAppHostProjectFilesAsync_LegacyStringOverload_UsesAllFilesScope()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var realAppHost = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "App", "AppHost.csproj"));
        Directory.CreateDirectory(realAppHost.DirectoryName!);
        await File.WriteAllTextAsync(realAppHost.FullName, "Not a real project file.");

        var skipListedAppHost = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "node_modules", "foo", "AppHost.csproj"));
        Directory.CreateDirectory(skipListedAppHost.DirectoryName!);
        await File.WriteAllTextAsync(skipListedAppHost.FullName, "Not a real project file.");

        var projectFactory = new TestAppHostProjectFactory
        {
            ValidateAppHostCallback = _ => new AppHostValidationResult(IsValid: true)
        };

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocator(executionContext, projectFactory: projectFactory);

        var found = await projectLocator.FindAppHostProjectFilesAsync(workspace.WorkspaceRoot.FullName, CancellationToken.None).DefaultTimeout();

        var paths = found.Select(file => file.FullName).ToHashSet();
        Assert.Equal(2, paths.Count);
        Assert.Contains(realAppHost.FullName, paths);
        Assert.Contains(skipListedAppHost.FullName, paths);
    }

    [Fact]
    public async Task FindAppHostProjectsAsync_IncludesCandidateStatus()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var buildableAppHost = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "Buildable", "AppHost.csproj"));
        Directory.CreateDirectory(buildableAppHost.DirectoryName!);
        await File.WriteAllTextAsync(buildableAppHost.FullName, "Not a real project file.");

        var unbuildableAppHost = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "Unbuildable", "AppHost.csproj"));
        Directory.CreateDirectory(unbuildableAppHost.DirectoryName!);
        await File.WriteAllTextAsync(unbuildableAppHost.FullName, "Not a real project file.");

        var projectFactory = new TestAppHostProjectFactory
        {
            ValidateAppHostCallback = projectFile => projectFile.FullName == buildableAppHost.FullName
                ? new AppHostValidationResult(IsValid: true)
                : new AppHostValidationResult(IsValid: false, IsPossiblyUnbuildable: true)
        };

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocator(executionContext, projectFactory: projectFactory);

        var found = await projectLocator.FindAppHostProjectsAsync(workspace.WorkspaceRoot, AppHostDiscoveryScope.AllFiles, CancellationToken.None).DefaultTimeout();

        Assert.Collection(found.OrderBy(candidate => candidate.AppHostFile.FullName),
            candidate =>
            {
                Assert.Equal(buildableAppHost.FullName, candidate.AppHostFile.FullName);
                Assert.Equal(AppHostProjectCandidateStatus.Buildable, candidate.Status);
            },
            candidate =>
            {
                Assert.Equal(unbuildableAppHost.FullName, candidate.AppHostFile.FullName);
                Assert.Equal(AppHostProjectCandidateStatus.PossiblyUnbuildable, candidate.Status);
            });
    }

    [Fact]
    public async Task FindAppHostProjectsAsync_ExplicitDirectoryScope_AppliesSkipListButNotGit()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // AppHost in a normal location.
        var realAppHost = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "App", "AppHost.csproj"));
        Directory.CreateDirectory(realAppHost.DirectoryName!);
        await File.WriteAllTextAsync(realAppHost.FullName, "Not a real project file.");

        // node_modules sibling — must still be skipped.
        var staleAppHost = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "node_modules", "foo", "AppHost.csproj"));
        Directory.CreateDirectory(staleAppHost.DirectoryName!);
        await File.WriteAllTextAsync(staleAppHost.FullName, "Not a real project file.");

        var projectFactory = new TestAppHostProjectFactory
        {
            ValidateAppHostCallback = _ => new AppHostValidationResult(IsValid: true)
        };

        // Use a TestGitRepository that returns a populated set including the stale path.
        // ExplicitDirectory scope must ignore the git list entirely and rely on the filesystem walk + skip list.
        var sentinelGit = new TestGitRepository
        {
            GetIncludedFilesAsyncCallback = (_, _) => Task.FromResult<IReadOnlySet<string>?>(new HashSet<string>
            {
                realAppHost.FullName,
                staleAppHost.FullName
            })
        };

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocator(executionContext, projectFactory: projectFactory, gitRepository: sentinelGit);

        var found = await projectLocator.FindAppHostProjectsAsync(workspace.WorkspaceRoot, AppHostDiscoveryScope.ExplicitDirectory, CancellationToken.None).DefaultTimeout();

        Assert.Single(found);
        Assert.Equal(realAppHost.FullName, found[0].AppHostFile.FullName);
    }

    [Fact]
    public async Task FindAppHostProjectsAsync_DefaultFiltered_GitMode_OnlyIncludesGitListedFiles()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Two AppHosts on disk; only one is in the git-included set.
        var trackedAppHost = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "App", "AppHost.csproj"));
        Directory.CreateDirectory(trackedAppHost.DirectoryName!);
        await File.WriteAllTextAsync(trackedAppHost.FullName, "Not a real project file.");

        var ignoredAppHost = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "legacy", "Stale.csproj"));
        Directory.CreateDirectory(ignoredAppHost.DirectoryName!);
        await File.WriteAllTextAsync(ignoredAppHost.FullName, "Not a real project file.");

        var projectFactory = new TestAppHostProjectFactory
        {
            ValidateAppHostCallback = _ => new AppHostValidationResult(IsValid: true)
        };

        var fakeGit = new TestGitRepository
        {
            GetIncludedFilesAsyncCallback = (_, _) => Task.FromResult<IReadOnlySet<string>?>(new HashSet<string>
            {
                trackedAppHost.FullName
            })
        };

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocator(executionContext, projectFactory: projectFactory, gitRepository: fakeGit);

        var found = await projectLocator.FindAppHostProjectsAsync(workspace.WorkspaceRoot, AppHostDiscoveryScope.DefaultFiltered, CancellationToken.None).DefaultTimeout();

        Assert.Single(found);
        Assert.Equal(trackedAppHost.FullName, found[0].AppHostFile.FullName);
    }

    [Fact]
    public async Task FindAppHostProjectsAsync_DefaultFiltered_GitMode_AppliesSkipListEvenWhenGitIncludesPath()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var realAppHost = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "App", "AppHost.csproj"));
        Directory.CreateDirectory(realAppHost.DirectoryName!);
        await File.WriteAllTextAsync(realAppHost.FullName, "Not a real project file.");

        // Pretend the user committed node_modules — the skip list still removes it.
        var nodeModulesAppHost = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "node_modules", "foo", "AppHost.csproj"));
        Directory.CreateDirectory(nodeModulesAppHost.DirectoryName!);
        await File.WriteAllTextAsync(nodeModulesAppHost.FullName, "Not a real project file.");

        var projectFactory = new TestAppHostProjectFactory
        {
            ValidateAppHostCallback = _ => new AppHostValidationResult(IsValid: true)
        };

        var fakeGit = new TestGitRepository
        {
            GetIncludedFilesAsyncCallback = (_, _) => Task.FromResult<IReadOnlySet<string>?>(new HashSet<string>
            {
                realAppHost.FullName,
                nodeModulesAppHost.FullName
            })
        };

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocator(executionContext, projectFactory: projectFactory, gitRepository: fakeGit);

        var found = await projectLocator.FindAppHostProjectsAsync(workspace.WorkspaceRoot, AppHostDiscoveryScope.DefaultFiltered, CancellationToken.None).DefaultTimeout();

        Assert.Single(found);
        Assert.Equal(realAppHost.FullName, found[0].AppHostFile.FullName);
    }

    [Fact]
    public async Task FindAppHostProjectsAsync_DefaultFiltered_GitMode_DropsDeletedTrackedFiles()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var realAppHost = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "App", "AppHost.csproj"));
        Directory.CreateDirectory(realAppHost.DirectoryName!);
        await File.WriteAllTextAsync(realAppHost.FullName, "Not a real project file.");

        // git tracked this path but it is no longer present on disk.
        var phantomAppHost = Path.Combine(workspace.WorkspaceRoot.FullName, "Removed", "AppHost.csproj");

        var projectFactory = new TestAppHostProjectFactory
        {
            ValidateAppHostCallback = _ => new AppHostValidationResult(IsValid: true)
        };

        var fakeGit = new TestGitRepository
        {
            GetIncludedFilesAsyncCallback = (_, _) => Task.FromResult<IReadOnlySet<string>?>(new HashSet<string>
            {
                realAppHost.FullName,
                phantomAppHost
            })
        };

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocator(executionContext, projectFactory: projectFactory, gitRepository: fakeGit);

        var found = await projectLocator.FindAppHostProjectsAsync(workspace.WorkspaceRoot, AppHostDiscoveryScope.DefaultFiltered, CancellationToken.None).DefaultTimeout();

        Assert.Single(found);
        Assert.Equal(realAppHost.FullName, found[0].AppHostFile.FullName);
    }

    // ---------------------------------------------------------------------
    // GetAppHostFromSettingsAsync regression tests (issue: `aspire update`
    // crash when the configured AppHost's SDK pin no longer resolves).
    //
    // `aspire update` is the one command whose job is to *repair* an AppHost
    // whose pinned SDK can't be restored. The strict discovery path
    // (UseOrFindAppHostProjectFileAsync) calls MSBuild against the configured
    // path via ValidateAppHostAsync; when MSBuild fails, the configured path
    // is dropped and discovery cannot find any "buildable" AppHost either,
    // so the user gets "No buildable AppHosts were found".
    //
    // GetAppHostFromSettingsAsync intentionally does NOT MSBuild-validate the
    // configured path — it only checks file existence and handler presence.
    // The four tests below lock down the contract:
    //   * settings lookup returns the configured path when validation would fail;
    //   * settings lookup still rejects a missing file (returns null);
    //   * settings lookup still rejects a project with no handler (returns null);
    //   * strict discovery still rejects an unbuildable configured path (so a
    //     future change cannot silently weaken `aspire run` / `aspire add`).
    // ---------------------------------------------------------------------

    [Fact]
    public async Task GetAppHostFromSettings_ReturnsConfiguredAppHostEvenWhenValidationWouldFail()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var appHostDirectory = workspace.WorkspaceRoot.CreateSubdirectory("AppHost");
        var appHostProjectFile = new FileInfo(Path.Combine(appHostDirectory.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(appHostProjectFile.FullName, "Not a real apphost");

        var workspaceSettingsDirectory = workspace.CreateDirectory(".aspire");
        var aspireSettingsFile = new FileInfo(Path.Combine(workspaceSettingsDirectory.FullName, "settings.json"));

        using (var writer = aspireSettingsFile.OpenWrite())
        {
            await JsonSerializer.SerializeAsync(writer, new
            {
                appHostPath = Path.GetRelativePath(aspireSettingsFile.Directory!.FullName, appHostProjectFile.FullName)
            });
        }

        // Even if MSBuild validation would fail (simulating an unresolvable SDK pin),
        // GetAppHostFromSettingsAsync must return the configured path: it doesn't
        // invoke ValidateAppHostAsync at all. `aspire update` relies on this so it
        // can rewrite the pin via ProjectUpdater's fallback parser.
        var projectFactory = new TestAppHostProjectFactory
        {
            ValidateAppHostAsyncCallback = (_, _) => Task.FromResult(new AppHostValidationResult(IsValid: false))
        };

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocator(executionContext, projectFactory: projectFactory);

        var foundAppHost = await projectLocator.GetAppHostFromSettingsAsync(CancellationToken.None).DefaultTimeout();

        Assert.NotNull(foundAppHost);
        Assert.Equal(appHostProjectFile.FullName, foundAppHost!.FullName);
    }

    [Fact]
    public async Task GetAppHostFromSettings_ReturnsNullWhenConfiguredFileDoesNotExist()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var workspaceSettingsDirectory = workspace.CreateDirectory(".aspire");
        var aspireSettingsFile = new FileInfo(Path.Combine(workspaceSettingsDirectory.FullName, "settings.json"));

        using (var writer = aspireSettingsFile.OpenWrite())
        {
            await JsonSerializer.SerializeAsync(writer, new
            {
                appHostPath = "DoesNotExist/AppHost.csproj"
            });
        }

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocator(executionContext);

        // Skipping MSBuild validation does not mean accepting paths that
        // aren't on disk — the existence check still runs.
        var foundAppHost = await projectLocator.GetAppHostFromSettingsAsync(CancellationToken.None).DefaultTimeout();

        Assert.Null(foundAppHost);
    }

    [Fact]
    public async Task GetAppHostFromSettings_ReturnsNullWhenNoHandlerCanProcessFile()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var appHostDirectory = workspace.WorkspaceRoot.CreateSubdirectory("AppHost");
        // Use an extension with no registered handler; TryGetProject returns null
        // for this file. Settings lookup must still reject it: skipping MSBuild
        // validation is not the same as accepting anything.
        var appHostProjectFile = new FileInfo(Path.Combine(appHostDirectory.FullName, "AppHost.unknown"));
        await File.WriteAllTextAsync(appHostProjectFile.FullName, "Not a real apphost");

        var workspaceSettingsDirectory = workspace.CreateDirectory(".aspire");
        var aspireSettingsFile = new FileInfo(Path.Combine(workspaceSettingsDirectory.FullName, "settings.json"));

        using (var writer = aspireSettingsFile.OpenWrite())
        {
            await JsonSerializer.SerializeAsync(writer, new
            {
                appHostPath = Path.GetRelativePath(aspireSettingsFile.Directory!.FullName, appHostProjectFile.FullName)
            });
        }

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocator(executionContext);

        var foundAppHost = await projectLocator.GetAppHostFromSettingsAsync(CancellationToken.None).DefaultTimeout();

        Assert.Null(foundAppHost);
    }

    [Fact]
    public async Task UseOrFindAppHostProjectFile_RejectsUnbuildableSettingsAppHost()
    {
        // Inverse of the GetAppHostFromSettings tests above: locks down today's
        // strict UseOrFindAppHostProjectFileAsync behavior so a future change
        // cannot silently make `aspire run` / `aspire add` trust an unbuildable
        // configured path. The discovery fallback must also fail (no other
        // AppHosts on disk), so we expect a ProjectLocatorException.
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var appHostDirectory = workspace.WorkspaceRoot.CreateSubdirectory("AppHost");
        var appHostProjectFile = new FileInfo(Path.Combine(appHostDirectory.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(appHostProjectFile.FullName, "Not a real apphost");

        var workspaceSettingsDirectory = workspace.CreateDirectory(".aspire");
        var aspireSettingsFile = new FileInfo(Path.Combine(workspaceSettingsDirectory.FullName, "settings.json"));

        using (var writer = aspireSettingsFile.OpenWrite())
        {
            await JsonSerializer.SerializeAsync(writer, new
            {
                appHostPath = Path.GetRelativePath(aspireSettingsFile.Directory!.FullName, appHostProjectFile.FullName)
            });
        }

        var projectFactory = new TestAppHostProjectFactory
        {
            ValidateAppHostAsyncCallback = (_, _) => Task.FromResult(new AppHostValidationResult(IsValid: false))
        };

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocator(executionContext, projectFactory: projectFactory);

        await Assert.ThrowsAsync<ProjectLocatorException>(async () =>
        {
            await projectLocator.UseOrFindAppHostProjectFileAsync(
                projectFile: null,
                createSettingsFile: false,
                CancellationToken.None).DefaultTimeout();
        });
    }

    [Fact]
    public async Task GetAppHostFromSettings_LegacyFile_WarningNamesSettingsJsonNotAspireConfigJson()
    {
        // Regression test for https://github.com/microsoft/aspire/issues/17620.
        // When the only user-authored config is the legacy .aspire/settings.json and its
        // appHostPath points at a file that no longer exists, the warning must reference
        // .aspire/settings.json (the file the user wrote) and must NOT reference
        // aspire.config.json (which the user never authored).
        //
        // The discovery walk (FindAppHostProjectsAsync, the path used by `aspire ls`) is
        // the user-facing surface that emits this warning via AddSettingsAppHostCandidateAsync.
        // The probe-style GetAppHostFromSettingsAsync API is intentionally silent so background
        // feature-detection callers (DotNetSdkCheck, AspireVersionCheck, etc.) don't leak
        // user-facing output.
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var workspaceSettingsDirectory = workspace.CreateDirectory(".aspire");
        var aspireSettingsFile = new FileInfo(Path.Combine(workspaceSettingsDirectory.FullName, "settings.json"));

        using (var writer = aspireSettingsFile.OpenWrite())
        {
            await JsonSerializer.SerializeAsync(writer, new
            {
                appHostPath = "DoesNotExist/AppHost.csproj"
            });
        }

        var interactionService = new TestInteractionService();
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocator(executionContext, interactionService: interactionService);

        await projectLocator.FindAppHostProjectsAsync(workspace.WorkspaceRoot, AppHostDiscoveryScope.DefaultFiltered, CancellationToken.None).DefaultTimeout();

        var warning = Assert.Single(interactionService.DisplayedMessages);
        Assert.Equal(KnownEmojis.Warning, warning.Emoji);
        Assert.Contains(aspireSettingsFile.FullName, warning.Message);
        Assert.DoesNotContain(AspireConfigFile.FileName, warning.Message);
    }

    [Fact]
    public async Task GetAppHostFromSettings_AspireConfigJson_WithNulByteInPath_ReportsValidationErrorAndDoesNotCrash()
    {
        // Regression test for https://github.com/microsoft/aspire/issues/17624.
        // A NUL byte in appHost.path survives JSON deserialization, then would crash
        // inside Path.Combine / new FileInfo with "Null character in path." surfaced as
        // a generic "unexpected error". Discovery must instead return null and display
        // a clear validation error naming the user-authored config file.
        //
        // Driven through FindAppHostProjectsAsync (the user-facing `aspire ls` path) so
        // the validation error surfaces via AddSettingsAppHostCandidateAsync.
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        // \u0000 is the escaped NUL byte; the JSON parser preserves it as a literal \0
        // inside the deserialized string. Writing a raw NUL would test JSON parsing,
        // not the post-deserialization validation we want to exercise.
        await File.WriteAllTextAsync(configPath, """
            { "appHost": { "path": "a\u0000b.csproj" } }
            """);

        var sink = new TestSink();
        var logger = new TestLogger<ProjectLocator>(new TestLoggerFactory(sink, enabled: true));
        var interactionService = new TestInteractionService();
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocator(executionContext, interactionService: interactionService, logger: logger);

        await projectLocator.FindAppHostProjectsAsync(workspace.WorkspaceRoot, AppHostDiscoveryScope.DefaultFiltered, CancellationToken.None).DefaultTimeout();

        var error = Assert.Single(interactionService.DisplayedErrors);
        Assert.Contains(configPath, error);
        Assert.Contains("appHost.path", error);
        Assert.DoesNotContain(sink.Writes, w => w.LogLevel == LogLevel.Warning && w.Message?.Contains("Ignoring configured AppHost path", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task GetAppHostFromSettings_LegacySettings_WithNulByteInPath_ReportsValidationErrorAndDoesNotCrash()
    {
        // Same scenario as the modern-config case above, but for the legacy
        // .aspire/settings.json reader. Both code paths consume a user-supplied path
        // string and must validate it before reaching System.IO APIs.
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var workspaceSettingsDirectory = workspace.CreateDirectory(".aspire");
        var aspireSettingsFile = new FileInfo(Path.Combine(workspaceSettingsDirectory.FullName, "settings.json"));
        await File.WriteAllTextAsync(aspireSettingsFile.FullName, """
            { "appHostPath": "a\u0000b.csproj" }
            """);

        var interactionService = new TestInteractionService();
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocator(executionContext, interactionService: interactionService);

        await projectLocator.FindAppHostProjectsAsync(workspace.WorkspaceRoot, AppHostDiscoveryScope.DefaultFiltered, CancellationToken.None).DefaultTimeout();

        var error = Assert.Single(interactionService.DisplayedErrors);
        Assert.Contains(aspireSettingsFile.FullName, error);
        Assert.Contains("appHostPath", error);
    }

    [Fact]
    public async Task GetAppHostFromSettings_AspireConfigJson_WithEmptyPath_ReportsValidationErrorAndDoesNotCrash()
    {
        // Adversarial regression: an empty string for appHost.path is technically valid
        // JSON but used to fall through the L4 validation with the misleading
        // "contains characters that are not allowed" message even though it has no
        // characters at all. Path.Combine(directory, "") collapses to the directory,
        // which would then surface a "directory is not a file" warning downstream.
        // The validator now rejects empty paths up front with the corrected message.
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        await File.WriteAllTextAsync(configPath, """
            { "appHost": { "path": "" } }
            """);

        var interactionService = new TestInteractionService();
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocator(executionContext, interactionService: interactionService);

        await projectLocator.FindAppHostProjectsAsync(workspace.WorkspaceRoot, AppHostDiscoveryScope.DefaultFiltered, CancellationToken.None).DefaultTimeout();

        var error = Assert.Single(interactionService.DisplayedErrors);
        Assert.Contains(configPath, error);
        Assert.Contains("appHost.path", error);
        Assert.Contains("is empty or contains", error);
    }

    [Fact]
    public async Task GetAppHostFromSettings_LegacySettings_WithEmptyPath_ReportsValidationErrorAndDoesNotCrash()
    {
        // Companion to the modern-config empty-path case above; the legacy reader has
        // the same gap and must surface the same corrected validation error.
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var workspaceSettingsDirectory = workspace.CreateDirectory(".aspire");
        var aspireSettingsFile = new FileInfo(Path.Combine(workspaceSettingsDirectory.FullName, "settings.json"));
        await File.WriteAllTextAsync(aspireSettingsFile.FullName, """
            { "appHostPath": "" }
            """);

        var interactionService = new TestInteractionService();
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocator(executionContext, interactionService: interactionService);

        await projectLocator.FindAppHostProjectsAsync(workspace.WorkspaceRoot, AppHostDiscoveryScope.DefaultFiltered, CancellationToken.None).DefaultTimeout();

        var error = Assert.Single(interactionService.DisplayedErrors);
        Assert.Contains(aspireSettingsFile.FullName, error);
        Assert.Contains("appHostPath", error);
        Assert.Contains("is empty or contains", error);
    }

    [Fact]
    public async Task GetAppHostFromSettings_MalformedConfig_SilentProbeLogsWarning()
    {
        // Probe-style callers intentionally don't write to the user-facing interaction
        // service, but malformed config still needs diagnostics so background checks are
        // debuggable.
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        await File.WriteAllTextAsync(configPath, """
            { "appHost": {
            """);

        var sink = new TestSink();
        var logger = new TestLogger<ProjectLocator>(new TestLoggerFactory(sink, enabled: true));
        var interactionService = new TestInteractionService();
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocator(executionContext, interactionService: interactionService, logger: logger);

        var foundAppHost = await projectLocator.GetAppHostFromSettingsAsync(CancellationToken.None).DefaultTimeout();

        Assert.Null(foundAppHost);
        Assert.Empty(interactionService.DisplayedErrors);

        var warning = Assert.Single(sink.Writes, w => w.LogLevel == LogLevel.Warning);
        Assert.Contains(configPath, warning.Message);
        Assert.IsAssignableFrom<JsonException>(warning.Exception);
    }

    [Fact]
    public async Task GetAppHostFromSettings_MalformedLegacySettings_SilentProbeLogsWarning()
    {
        // The legacy settings reader uses JsonDocument directly, so it needs the same
        // silent-mode diagnostics as aspire.config.json instead of letting JsonException
        // escape from background probes.
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var workspaceSettingsDirectory = workspace.CreateDirectory(".aspire");
        var settingsPath = Path.Combine(workspaceSettingsDirectory.FullName, "settings.json");
        await File.WriteAllTextAsync(settingsPath, """
            { "appHostPath":
            """);

        var sink = new TestSink();
        var logger = new TestLogger<ProjectLocator>(new TestLoggerFactory(sink, enabled: true));
        var interactionService = new TestInteractionService();
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocator(executionContext, interactionService: interactionService, logger: logger);

        var foundAppHost = await projectLocator.GetAppHostFromSettingsAsync(CancellationToken.None).DefaultTimeout();

        Assert.Null(foundAppHost);
        Assert.Empty(interactionService.DisplayedErrors);

        var warning = Assert.Single(sink.Writes, w => w.LogLevel == LogLevel.Warning);
        Assert.Contains(settingsPath, warning.Message);
        Assert.IsAssignableFrom<JsonException>(warning.Exception);
    }

    [Fact]
    public async Task GetAppHostFromSettings_LegacySettingsWithNonStringPath_ReportsValidationErrorAndDoesNotCrash()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var workspaceSettingsDirectory = workspace.CreateDirectory(".aspire");
        var settingsPath = Path.Combine(workspaceSettingsDirectory.FullName, "settings.json");
        await File.WriteAllTextAsync(settingsPath, """
            { "appHostPath": 123 }
            """);

        var sink = new TestSink();
        var logger = new TestLogger<ProjectLocator>(new TestLoggerFactory(sink, enabled: true));
        var interactionService = new TestInteractionService();
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocator(executionContext, interactionService: interactionService, logger: logger);

        var foundAppHost = await projectLocator.GetAppHostFromSettingsAsync(CancellationToken.None).DefaultTimeout();

        Assert.Null(foundAppHost);
        Assert.Empty(interactionService.DisplayedErrors);

        var warning = Assert.Single(sink.Writes, w => w.LogLevel == LogLevel.Warning);
        Assert.Contains(settingsPath, warning.Message);
        Assert.Contains("not a JSON string", warning.Message);
    }

    [Fact]
    public async Task GetAppHostFromSettings_LegacySettingsWithNonObjectRoot_ReportsValidationErrorAndDoesNotCrash()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var workspaceSettingsDirectory = workspace.CreateDirectory(".aspire");
        var settingsPath = Path.Combine(workspaceSettingsDirectory.FullName, "settings.json");
        await File.WriteAllTextAsync(settingsPath, """
            []
            """);

        var sink = new TestSink();
        var logger = new TestLogger<ProjectLocator>(new TestLoggerFactory(sink, enabled: true));
        var interactionService = new TestInteractionService();
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocator(executionContext, interactionService: interactionService, logger: logger);

        var foundAppHost = await projectLocator.GetAppHostFromSettingsAsync(CancellationToken.None).DefaultTimeout();

        Assert.Null(foundAppHost);
        Assert.Empty(interactionService.DisplayedErrors);

        var warning = Assert.Single(sink.Writes, w => w.LogLevel == LogLevel.Warning);
        Assert.Contains(settingsPath, warning.Message);
        Assert.Contains("not a JSON object", warning.Message);
    }

    [Fact]
    public async Task FindAppHostProjectsAsync_DeduplicatesSettingsCandidateAcrossSymlink()
    {
        // Regression test for https://github.com/microsoft/aspire/issues/17626.
        // When the user's settings file points at an apphost via a symlinked path
        // (for example /tmp/L5/x.cs on macOS while the canonical path is
        // /private/tmp/L5/x.cs), the directory walk surfaces the canonical path and the
        // settings reader surfaces the user-written symbolic path. Plain string
        // comparison between the two used to produce a duplicate entry; dedupe must now
        // canonicalize symlinks before comparing.
        //
        // To isolate the settings-vs-walk dedupe from the walk's own behaviour, the
        // symlink target lives in a directory the default discovery filter skips
        // (node_modules), so the walk only surfaces the real path.
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var realAppDirectory = workspace.WorkspaceRoot.CreateSubdirectory("real-app");
        var realAppHost = new FileInfo(Path.Combine(realAppDirectory.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(realAppHost.FullName, "Not a real project file.");

        // node_modules is excluded from DefaultFiltered discovery, so a symlink placed
        // there is invisible to the walk. The same symlink path used in settings still
        // resolves through the link to the real apphost file.
        var nodeModulesDir = workspace.WorkspaceRoot.CreateSubdirectory("node_modules");
        var linkDirectory = Path.Combine(nodeModulesDir.FullName, "link");
        try
        {
            Directory.CreateSymbolicLink(linkDirectory, realAppDirectory.FullName);
        }
        catch (UnauthorizedAccessException ex)
        {
            Assert.Skip($"Cannot create symbolic links in this environment: {ex.Message}");
        }
        catch (IOException ex)
        {
            Assert.Skip($"Symbolic link creation failed in this environment: {ex.Message}");
        }

        var pathThroughLink = Path.Combine(linkDirectory, "AppHost.csproj");

        // Sanity-check the test setup: the symlink path must resolve to the real file,
        // and the two strings must differ — otherwise the test would pass trivially.
        Assert.True(File.Exists(pathThroughLink), "Symlinked path should resolve to the real apphost file.");
        Assert.NotEqual(realAppHost.FullName, pathThroughLink);

        var workspaceSettingsDirectory = workspace.CreateDirectory(".aspire");
        var aspireSettingsFile = new FileInfo(Path.Combine(workspaceSettingsDirectory.FullName, "settings.json"));
        using (var writer = aspireSettingsFile.OpenWrite())
        {
            await JsonSerializer.SerializeAsync(writer, new
            {
                appHostPath = pathThroughLink
            });
        }

        var projectFactory = new TestAppHostProjectFactory
        {
            ValidateAppHostCallback = _ => new AppHostValidationResult(IsValid: true)
        };

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocator(executionContext, projectFactory: projectFactory);

        var found = await projectLocator.FindAppHostProjectsAsync(workspace.WorkspaceRoot, AppHostDiscoveryScope.DefaultFiltered, CancellationToken.None).DefaultTimeout();

        // Exactly one candidate: the settings-derived path through the symlink must be
        // recognized as the same file as the walk-discovered real path.
        Assert.Single(found);
    }

    [Fact]
    public async Task FindAppHostProjectsAsync_DeduplicatesAspireConfigCandidateWithDifferentCasing()
    {
        // Regression test for https://github.com/microsoft/aspire/issues/17635.
        // Case-insensitive file systems can resolve a user-authored config path like
        // APPHOST.csproj to the same file that the discovery walk reports as
        // AppHost.csproj. The two strings differ, but the paths should still dedupe.
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var appHostProjectFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(appHostProjectFile.FullName, "Not a real project file.");

        var differentlyCasedPath = Path.Combine(workspace.WorkspaceRoot.FullName, "APPHOST.csproj");
        if (!File.Exists(differentlyCasedPath))
        {
            Assert.Skip("The current file system is case-sensitive.");
        }

        Assert.NotEqual(appHostProjectFile.FullName, differentlyCasedPath);

        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        await File.WriteAllTextAsync(configPath, $$"""
            { "appHost": { "path": {{JsonSerializer.Serialize(differentlyCasedPath)}} } }
            """);

        var projectFactory = new TestAppHostProjectFactory
        {
            ValidateAppHostCallback = _ => new AppHostValidationResult(IsValid: true)
        };

        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var projectLocator = CreateProjectLocator(executionContext, projectFactory: projectFactory);

        var found = await projectLocator.FindAppHostProjectsAsync(workspace.WorkspaceRoot, AppHostDiscoveryScope.DefaultFiltered, CancellationToken.None).DefaultTimeout();

        Assert.Single(found);
    }

    private static ProjectLocator CreateProjectLocator(
        CliExecutionContext executionContext,
        IInteractionService? interactionService = null,
        IConfigurationService? configurationService = null,
        IAppHostProjectFactory? projectFactory = null,
        ILanguageDiscovery? languageDiscovery = null,
        IDotNetSdkInstaller? sdkInstaller = null,
        IGitRepository? gitRepository = null,
        AspireCliTelemetry? telemetry = null,
        ILogger<ProjectLocator>? logger = null)
    {
        var appHostCandidateFinder = new AppHostCandidateFinder(
            gitRepository ?? new TestGitRepository(),
            new ProfilingTelemetry(new ConfigurationBuilder().Build()),
            NullLogger<AppHostCandidateFinder>.Instance);

        return new ProjectLocator(
            logger ?? NullLogger<ProjectLocator>.Instance,
            executionContext,
            interactionService ?? new TestInteractionService(),
            configurationService ?? new TestConfigurationService(executionContext),
            projectFactory ?? new TestAppHostProjectFactory(),
            languageDiscovery ?? new TestLanguageDiscovery(),
            sdkInstaller ?? new TestDotNetSdkInstaller(),
            appHostCandidateFinder,
            telemetry ?? TestTelemetryHelper.CreateInitializedTelemetry());
    }
}
