// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.DotNet;
using Aspire.Cli.Interaction;
using Aspire.Cli.Packaging;
using Aspire.Cli.Projects;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Shared;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests.Projects;

public class ProjectUpdaterTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task UpdateProjectFileAsync_DoesAttemptToUpdateIfNoUpdatesRequired()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var srcFolder = workspace.CreateDirectory("src");

        var serviceDefaultsFolder = workspace.CreateDirectory("UpdateTester.ServiceDefaults");
        var serviceDefaultsProjectFile = new FileInfo(Path.Combine(serviceDefaultsFolder.FullName, "UpdateTester.ServiceDefaults.csproj"));

        var webAppFolder = workspace.CreateDirectory("UpdateTester.WebApp");
        var webAppProjectFile = new FileInfo(Path.Combine(webAppFolder.FullName, "UpdateTester.WebApp.csproj"));

        var appHostFolder = workspace.CreateDirectory("UpdateTester.AppHost");
        var appHostProjectFile = new FileInfo(Path.Combine(appHostFolder.FullName, "UpdateTester.AppHost.csproj"));

        await File.WriteAllTextAsync(
            appHostProjectFile.FullName,
            $$"""
            <Project Sdk="Microsoft.NET.Sdk">
                <Sdk Name="Aspire.AppHost.Sdk" Version="9.5.1-preview.1" />
            </Project>
            """);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, config =>
        {
            config.DotNetCliRunnerFactory = (sp) =>
            {
                return new TestDotNetCliRunner()
                {
                    SearchPackagesAsyncCallback = (_, query, _, _, _, _, _, _, _) =>
                    {
                        var packages = new List<NuGetPackageCli>();

                        packages.Add(query switch
                        {
                            "Aspire.AppHost.Sdk" => new NuGetPackageCli { Id = "Aspire.AppHost.Sdk", Version = "9.4.1", Source = "nuget.org" },
                            "Aspire.Hosting.AppHost" => new NuGetPackageCli { Id = "Aspire.Hosting.AppHost", Version = "9.4.1", Source = "nuget.org" },
                            "Aspire.Hosting.Redis" => new NuGetPackageCli { Id = "Aspire.Hosting.Redis", Version = "9.4.1", Source = "nuget.org" },
                            "Aspire.StackExchange.Redis.OutputCaching" => new NuGetPackageCli { Id = "Aspire.StackExchange.Redis.OutputCaching", Version = "9.4.1", Source = "nuget.org" },
                            "Microsoft.Extensions.ServiceDiscovery" => new NuGetPackageCli { Id = "Microsoft.Extensions.ServiceDiscovery", Version = "9.4.1", Source = "nuget.org" },
                            _ => throw new InvalidOperationException("Unexpected package query."),
                        });

                        return (0, packages.ToArray());
                    },

                    GetProjectItemsAndPropertiesAsyncCallback = (projectFile, _, _, _, _) =>
                    {
                        var itemsAndProperties = new JsonObject();

                        if (projectFile.FullName == appHostProjectFile.FullName)
                        {
                            itemsAndProperties.WithSdkVersion("9.4.1");
                            itemsAndProperties.WithPackageReference("Aspire.Hosting.AppHost", "9.4.1");
                            itemsAndProperties.WithPackageReference("Aspire.Hosting.Redis", "9.4.1");
                            itemsAndProperties.WithProjectReference(webAppProjectFile.FullName);
                        }
                        else if (projectFile.FullName == webAppProjectFile.FullName)
                        {
                            itemsAndProperties.WithPackageReference("Aspire.StackExchange.Redis.OutputCaching", "9.4.1");
                            itemsAndProperties.WithProjectReference(serviceDefaultsProjectFile.FullName);
                        }
                        else if (projectFile.FullName == serviceDefaultsProjectFile.FullName)
                        {
                            itemsAndProperties.WithPackageReference("Microsoft.ServiceDiscovery.Extensions", "9.4.1");
                        }
                        else
                        {
                            throw new InvalidOperationException("Unexpected project file.");
                        }

                        var json = itemsAndProperties.ToJsonString();
                        var document = JsonDocument.Parse(json);
                        return (0, document);
                    }
                };
            };

            config.InteractionServiceFactory = (sp) =>
            {
                var interactionService = new TestInteractionService();
                interactionService.ConfirmCallback = (promptText, defaultValue) =>
                {
                    throw new InvalidOperationException("Should not prompt when no work required.");
                };

                return interactionService;
            };
        });
        var provider = services.BuildServiceProvider();

        // Services we need for project updater.
        var logger = provider.GetRequiredService<ILogger<ProjectUpdater>>();
        var runner = provider.GetRequiredService<IDotNetCliRunner>();
        var interactionService = provider.GetRequiredService<IInteractionService>();
        var cache = provider.GetRequiredService<IMemoryCache>();
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var packagingService = provider.GetRequiredService<IPackagingService>();

        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();
        var selectedChannel = channels.Single(c => c.Name == "default");

        // If this throws then it means that the updater prompted
        // for confirmation to do an update when no update was required!
        var projectUpdater = provider.GetRequiredService<IProjectUpdater>();
        var updateResult = await projectUpdater.UpdateProjectAsync(appHostProjectFile, selectedChannel).DefaultTimeout();
        Assert.False(updateResult.UpdatedApplied);
    }

    [Fact]
    public async Task UpdateProjectFileAsync_CanUpdateFromStableToDaily()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var srcFolder = workspace.CreateDirectory("src");

        var serviceDefaultsFolder = workspace.CreateDirectory("UpdateTester.ServiceDefaults");
        var serviceDefaultsProjectFile = new FileInfo(Path.Combine(serviceDefaultsFolder.FullName, "UpdateTester.ServiceDefaults.csproj"));

        var webAppFolder = workspace.CreateDirectory("UpdateTester.WebApp");
        var webAppProjectFile = new FileInfo(Path.Combine(webAppFolder.FullName, "UpdateTester.WebApp.csproj"));

        var appHostFolder = workspace.CreateDirectory("UpdateTester.AppHost");
        var appHostProjectFile = new FileInfo(Path.Combine(appHostFolder.FullName, "UpdateTester.AppHost.csproj"));

        await File.WriteAllTextAsync(
            appHostProjectFile.FullName,
            $$"""
            <Project Sdk="Microsoft.NET.Sdk">
                <Sdk Name="Aspire.AppHost.Sdk" Version="9.4.1" />
            </Project>
            """);

        var packagesAddsExecuted = new List<(FileInfo ProjectFile, string PackageId, string PackageVersion, string? PackageSource, bool NoRestore)>();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, config =>
        {
            config.DotNetCliRunnerFactory = (sp) =>
            {
                return new TestDotNetCliRunner()
                {
                    SearchPackagesAsyncCallback = (_, query, _, _, _, _, _, _, _) =>
                    {
                        var packages = new List<NuGetPackageCli>();

                        packages.Add(query switch
                        {
                            "Aspire.AppHost.Sdk" => new NuGetPackageCli { Id = "Aspire.AppHost.Sdk", Version = "9.5.0-preview.1", Source = "daily" },
                            "Aspire.Hosting.AppHost" => new NuGetPackageCli { Id = "Aspire.Hosting.AppHost", Version = "9.5.0-preview.1", Source = "daily" },
                            "Aspire.Hosting.Redis" => new NuGetPackageCli { Id = "Aspire.Hosting.Redis", Version = "9.5.0-preview.1", Source = "daily" },
                            "Aspire.StackExchange.Redis.OutputCaching" => new NuGetPackageCli { Id = "Aspire.StackExchange.Redis.OutputCaching", Version = "9.5.0-preview.1", Source = "daily" },
                            "Microsoft.Extensions.ServiceDiscovery" => new NuGetPackageCli { Id = "Microsoft.Extensions.ServiceDiscovery", Version = "9.5.0-preview.1", Source = "daily" },
                            _ => throw new InvalidOperationException("Unexpected package query."),
                        });

                        return (0, packages.ToArray());
                    },

                    GetProjectItemsAndPropertiesAsyncCallback = (projectFile, _, _, _, _) =>
                    {
                        var itemsAndProperties = new JsonObject();

                        if (projectFile.FullName == appHostProjectFile.FullName)
                        {
                            itemsAndProperties.WithSdkVersion("9.4.1");
                            itemsAndProperties.WithPackageReference("Aspire.Hosting.AppHost", "9.4.1");
                            itemsAndProperties.WithPackageReference("Aspire.Hosting.Redis", "9.4.1");
                            itemsAndProperties.WithProjectReference(webAppProjectFile.FullName);
                        }
                        else if (projectFile.FullName == webAppProjectFile.FullName)
                        {
                            itemsAndProperties.WithPackageReference("Aspire.StackExchange.Redis.OutputCaching", "9.4.1");
                            itemsAndProperties.WithProjectReference(serviceDefaultsProjectFile.FullName);
                        }
                        else if (projectFile.FullName == serviceDefaultsProjectFile.FullName)
                        {
                            itemsAndProperties.WithPackageReference("Microsoft.Extensions.ServiceDiscovery", "9.4.1");
                        }
                        else
                        {
                            throw new InvalidOperationException("Unexpected project file.");
                        }

                        var json = itemsAndProperties.ToJsonString();
                        var document = JsonDocument.Parse(json);
                        return (0, document);
                    },
                    // FileInfo, string, string, string?, DotNetCliRunnerInvocationOptions, CancellationToken, int
                    AddPackageAsyncCallback = (projectFile, packageId, packageVersion, source, noRestore, _, _) =>
                    {
                        packagesAddsExecuted.Add((projectFile, packageId, packageVersion, source!, noRestore));
                        return 0;
                    }
                };
            };

            config.InteractionServiceFactory = (s) =>
            {
                var interactionService = new TestInteractionService();
                return interactionService;
            };
        });
        var provider = services.BuildServiceProvider();

        // Services we need for project updater.
        var logger = provider.GetRequiredService<ILogger<ProjectUpdater>>();
        var runner = provider.GetRequiredService<IDotNetCliRunner>();
        var interactionService = provider.GetRequiredService<IInteractionService>();
        var cache = provider.GetRequiredService<IMemoryCache>();
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var packagingService = provider.GetRequiredService<IPackagingService>();

        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();
        var selectedChannel = channels.Single(c => c.Name == "daily");

        // If this throws then it means that the updater prompted
        // for confirmation to do an update when no update was required!
        var projectUpdater = provider.GetRequiredService<IProjectUpdater>();
        var updateResult = await projectUpdater.UpdateProjectAsync(appHostProjectFile, selectedChannel).DefaultTimeout();

        Assert.True(updateResult.UpdatedApplied);
        // Note: Aspire.Hosting.AppHost is not updated because it's removed during SDK migration
        Assert.Collection(
            packagesAddsExecuted,
            item =>
            {
                Assert.Equal("Aspire.Hosting.Redis", item.PackageId);
                Assert.Equal("9.5.0-preview.1", item.PackageVersion);
                Assert.Null(item.PackageSource); // Should be null because of --no-restore behavior.
                Assert.Equal(appHostProjectFile.FullName, item.ProjectFile.FullName);
            },
            item =>
            {
                Assert.Equal("Aspire.StackExchange.Redis.OutputCaching", item.PackageId);
                Assert.Equal("9.5.0-preview.1", item.PackageVersion);
                Assert.Null(item.PackageSource); // Should be null because of --no-restore behavior.
                Assert.Equal(webAppProjectFile.FullName, item.ProjectFile.FullName);
            }
        );
    }

    [Fact]
    public async Task UpdateProjectFileAsync_CanUpdateFromDailyToStableWhereOnePackageIsUnstableOnly()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var srcFolder = workspace.CreateDirectory("src");

        var serviceDefaultsFolder = workspace.CreateDirectory("UpdateTester.ServiceDefaults");
        var serviceDefaultsProjectFile = new FileInfo(Path.Combine(serviceDefaultsFolder.FullName, "UpdateTester.ServiceDefaults.csproj"));

        var webAppFolder = workspace.CreateDirectory("UpdateTester.WebApp");
        var webAppProjectFile = new FileInfo(Path.Combine(webAppFolder.FullName, "UpdateTester.WebApp.csproj"));

        var appHostFolder = workspace.CreateDirectory("UpdateTester.AppHost");
        var appHostProjectFile = new FileInfo(Path.Combine(appHostFolder.FullName, "UpdateTester.AppHost.csproj"));

        await File.WriteAllTextAsync(
            appHostProjectFile.FullName,
            $$"""
            <Project Sdk="Microsoft.NET.Sdk">
                <Sdk Name="Aspire.AppHost.Sdk" Version="9.4.1" />
            </Project>
            """);

        var packagesAddsExecuted = new List<(FileInfo ProjectFile, string PackageId, string PackageVersion, string? PackageSource, bool NoRestore)>();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, config =>
        {
            config.DotNetCliRunnerFactory = (sp) =>
            {
                return new TestDotNetCliRunner()
                {
                    SearchPackagesAsyncCallback = (_, query, prerelease, _, _, _, _, _, _) =>
                    {
                        var packages = new List<NuGetPackageCli>();

                        var matchedPackage = (query, prerelease) switch
                        {
                            { query: "Aspire.AppHost.Sdk", prerelease: false } => new NuGetPackageCli { Id = "Aspire.AppHost.Sdk", Version = "9.4.1", Source = "nuget" },
                            { query: "Aspire.Hosting.AppHost", prerelease: false } => new NuGetPackageCli { Id = "Aspire.Hosting.AppHost", Version = "9.4.1", Source = "nuget" },
                            { query: "Aspire.Hosting.Redis", prerelease: false } => new NuGetPackageCli { Id = "Aspire.Hosting.Redis", Version = "9.4.1", Source = "nuget" },
                            { query: "Aspire.Hosting.Docker", prerelease: true } => new NuGetPackageCli { Id = "Aspire.Hosting.Docker", Version = "9.4.1-preview.1", Source = "nuget" },
                            { query: "Aspire.Hosting.Docker", prerelease: false } => null, // Not in feed.
                            { query: "Aspire.StackExchange.Redis.OutputCaching", prerelease: false } => new NuGetPackageCli { Id = "Aspire.StackExchange.Redis.OutputCaching", Version = "9.4.1", Source = "nuget" },
                            { query: "Microsoft.Extensions.ServiceDiscovery", prerelease: false } => new NuGetPackageCli { Id = "Microsoft.Extensions.ServiceDiscovery", Version = "9.4.1", Source = "nuget" },
                            _ => throw new InvalidOperationException("Unexpected package query."),
                        };

                        if (matchedPackage != null)
                        {
                            packages.Add(matchedPackage);
                        }

                        return (0, packages.ToArray());
                    },

                    GetProjectItemsAndPropertiesAsyncCallback = (projectFile, _, _, _, _) =>
                    {
                        var itemsAndProperties = new JsonObject();

                        if (projectFile.FullName == appHostProjectFile.FullName)
                        {
                            itemsAndProperties.WithSdkVersion("9.5.1-preview.1");
                            itemsAndProperties.WithPackageReference("Aspire.Hosting.AppHost", "9.5.1-preview.1");
                            itemsAndProperties.WithPackageReference("Aspire.Hosting.Redis", "9.5.1-preview.1");
                            itemsAndProperties.WithPackageReference("Aspire.Hosting.Docker", "9.5.1-preview.1");
                            itemsAndProperties.WithProjectReference(webAppProjectFile.FullName);
                        }
                        else if (projectFile.FullName == webAppProjectFile.FullName)
                        {
                            itemsAndProperties.WithPackageReference("Aspire.StackExchange.Redis.OutputCaching", "9.5.1-preview.1");
                            itemsAndProperties.WithProjectReference(serviceDefaultsProjectFile.FullName);
                        }
                        else if (projectFile.FullName == serviceDefaultsProjectFile.FullName)
                        {
                            itemsAndProperties.WithPackageReference("Microsoft.Extensions.ServiceDiscovery", "9.5.1-preview.1");
                        }
                        else
                        {
                            throw new InvalidOperationException("Unexpected project file.");
                        }

                        var json = itemsAndProperties.ToJsonString();
                        var document = JsonDocument.Parse(json);
                        return (0, document);
                    },
                    // FileInfo, string, string, string?, DotNetCliRunnerInvocationOptions, CancellationToken, int
                    AddPackageAsyncCallback = (projectFile, packageId, packageVersion, source, noRestore, _, _) =>
                    {
                        packagesAddsExecuted.Add((projectFile, packageId, packageVersion, source!, noRestore));
                        return 0;
                    }
                };
            };

            config.InteractionServiceFactory = (s) =>
            {
                var interactionService = new TestInteractionService();
                return interactionService;
            };
        });
        var provider = services.BuildServiceProvider();

        // Services we need for project updater.
        var logger = provider.GetRequiredService<ILogger<ProjectUpdater>>();
        var runner = provider.GetRequiredService<IDotNetCliRunner>();
        var interactionService = provider.GetRequiredService<IInteractionService>();
        var cache = provider.GetRequiredService<IMemoryCache>();
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var packagingService = provider.GetRequiredService<IPackagingService>();

        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();
        var selectedChannel = channels.Single(c => c.Name == "stable");

        // If this throws then it means that the updater prompted
        // for confirmation to do an update when no update was required!
        var projectUpdater = provider.GetRequiredService<IProjectUpdater>();
        var updateResult = await projectUpdater.UpdateProjectAsync(appHostProjectFile, selectedChannel).DefaultTimeout();

        Assert.True(updateResult.UpdatedApplied);
        // Note: Aspire.Hosting.AppHost is not updated because it's removed during SDK migration
        Assert.Collection(
            packagesAddsExecuted,
            item =>
            {
                Assert.Equal("Aspire.Hosting.Redis", item.PackageId);
                Assert.Equal("9.4.1", item.PackageVersion);
                Assert.Null(item.PackageSource); // Should be null because of --no-restore behavior.
                Assert.Equal(appHostProjectFile.FullName, item.ProjectFile.FullName);
            },
            item =>
            {
                Assert.Equal("Aspire.Hosting.Docker", item.PackageId);
                Assert.Equal("9.4.1-preview.1", item.PackageVersion);
                Assert.Null(item.PackageSource); // Should be null because of --no-restore behavior.
                Assert.Equal(appHostProjectFile.FullName, item.ProjectFile.FullName);
            },
            item =>
            {
                Assert.Equal("Aspire.StackExchange.Redis.OutputCaching", item.PackageId);
                Assert.Equal("9.4.1", item.PackageVersion);
                Assert.Null(item.PackageSource); // Should be null because of --no-restore behavior.
                Assert.Equal(webAppProjectFile.FullName, item.ProjectFile.FullName);
            }
        );
    }

    [Fact]
    public async Task UpdateProjectFileAsync_DiamondDependency_DoesNotDuplicateUpdates()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Create diamond dependency scenario:
        // AppHost -> ProjectA, ProjectB
        // ProjectA -> SharedProject
        // ProjectB -> SharedProject
        // SharedProject has updatable package that should only appear once in update steps

        var sharedProjectFolder = workspace.CreateDirectory("SharedProject");
        var sharedProjectFile = new FileInfo(Path.Combine(sharedProjectFolder.FullName, "SharedProject.csproj"));

        var projectAFolder = workspace.CreateDirectory("ProjectA");
        var projectAFile = new FileInfo(Path.Combine(projectAFolder.FullName, "ProjectA.csproj"));

        var projectBFolder = workspace.CreateDirectory("ProjectB");
        var projectBFile = new FileInfo(Path.Combine(projectBFolder.FullName, "ProjectB.csproj"));

        var appHostFolder = workspace.CreateDirectory("DiamondTest.AppHost");
        var appHostProjectFile = new FileInfo(Path.Combine(appHostFolder.FullName, "DiamondTest.AppHost.csproj"));

        await File.WriteAllTextAsync(
            appHostProjectFile.FullName,
            """
            <Project Sdk="Microsoft.NET.Sdk">
                <Sdk Name="Aspire.AppHost.Sdk" Version="9.4.1" />
            </Project>
            """);

        var packagesAddsExecuted = new List<(FileInfo ProjectFile, string PackageId, string PackageVersion, string? PackageSource, bool NoRestore)>();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, config =>
        {
            config.DotNetCliRunnerFactory = (sp) =>
            {
                return new TestDotNetCliRunner()
                {
                    SearchPackagesAsyncCallback = (_, query, _, _, _, _, _, _, _) =>
                    {
                        var packages = new List<NuGetPackageCli>();

                        packages.Add(query switch
                        {
                            "Aspire.AppHost.Sdk" => new NuGetPackageCli { Id = "Aspire.AppHost.Sdk", Version = "9.5.0", Source = "nuget.org" },
                            "Aspire.Hosting.Redis" => new NuGetPackageCli { Id = "Aspire.Hosting.Redis", Version = "9.5.0", Source = "nuget.org" },
                            _ => throw new InvalidOperationException($"Unexpected package query: {query}"),
                        });

                        return (0, packages.ToArray());
                    },

                    GetProjectItemsAndPropertiesAsyncCallback = (projectFile, _, _, _, _) =>
                    {
                        var itemsAndProperties = new JsonObject();

                        if (projectFile.FullName == appHostProjectFile.FullName)
                        {
                            // AppHost references both ProjectA and ProjectB
                            itemsAndProperties.WithSdkVersion("9.4.1");
                            itemsAndProperties.WithPackageReference("Aspire.Hosting.Redis", "9.4.1");
                            itemsAndProperties.WithProjectReference(projectAFile.FullName);
                            itemsAndProperties.WithProjectReference(projectBFile.FullName);
                        }
                        else if (projectFile.FullName == projectAFile.FullName)
                        {
                            // ProjectA references SharedProject - needs empty package reference array
                            itemsAndProperties.WithPackageReference("DummyPackage", "1.0.0"); // Add dummy first to create structure
                            itemsAndProperties["Items"]!["PackageReference"]!.AsArray().Clear(); // Then clear it
                            itemsAndProperties.WithProjectReference(sharedProjectFile.FullName);
                        }
                        else if (projectFile.FullName == projectBFile.FullName)
                        {
                            // ProjectB also references SharedProject (creating the diamond) - needs empty package reference array
                            itemsAndProperties.WithPackageReference("DummyPackage", "1.0.0"); // Add dummy first to create structure
                            itemsAndProperties["Items"]!["PackageReference"]!.AsArray().Clear(); // Then clear it
                            itemsAndProperties.WithProjectReference(sharedProjectFile.FullName);
                        }
                        else if (projectFile.FullName == sharedProjectFile.FullName)
                        {
                            // SharedProject has an updatable package
                            itemsAndProperties.WithPackageReference("Aspire.Hosting.Redis", "9.4.1");
                        }
                        else
                        {
                            throw new InvalidOperationException($"Unexpected project file: {projectFile.FullName}");
                        }

                        var json = itemsAndProperties.ToJsonString();
                        var document = JsonDocument.Parse(json);
                        return (0, document);
                    },

                    AddPackageAsyncCallback = (projectFile, packageId, packageVersion, source, noRestore, _, _) =>
                    {
                        packagesAddsExecuted.Add((projectFile, packageId, packageVersion, source!, noRestore));
                        return 0;
                    }
                };
            };

            config.InteractionServiceFactory = (s) =>
            {
                var interactionService = new TestInteractionService();
                return interactionService;
            };
        });
        var provider = services.BuildServiceProvider();

        var logger = provider.GetRequiredService<ILogger<ProjectUpdater>>();
        var runner = provider.GetRequiredService<IDotNetCliRunner>();
        var interactionService = provider.GetRequiredService<IInteractionService>();
        var cache = provider.GetRequiredService<IMemoryCache>();
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var packagingService = provider.GetRequiredService<IPackagingService>();

        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();
        var selectedChannel = channels.Single(c => c.Name == "default");

        var projectUpdater = provider.GetRequiredService<IProjectUpdater>();
        var updateResult = await projectUpdater.UpdateProjectAsync(appHostProjectFile, selectedChannel).DefaultTimeout();

        Assert.True(updateResult.UpdatedApplied);

        // Verify that the SharedProject's package update only appears once, not twice
        var sharedProjectUpdates = packagesAddsExecuted.Where(p => p.ProjectFile.FullName == sharedProjectFile.FullName).ToList();
        Assert.Single(sharedProjectUpdates);

        var sharedProjectUpdate = sharedProjectUpdates.Single();
        Assert.Equal("Aspire.Hosting.Redis", sharedProjectUpdate.PackageId);
        Assert.Equal("9.5.0", sharedProjectUpdate.PackageVersion);

        // Should also have the AppHost package update
        var appHostUpdates = packagesAddsExecuted.Where(p => p.ProjectFile.FullName == appHostProjectFile.FullName).ToList();
        Assert.Single(appHostUpdates);

        Assert.Equal("Aspire.Hosting.Redis", appHostUpdates.Single().PackageId);
    }

    [Fact]
    public async Task UpdateProjectFileAsync_CentralPackageManagement_UpdatesDirectoryPackagesProps()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var serviceDefaultsFolder = workspace.CreateDirectory("UpdateTester.ServiceDefaults");
        var serviceDefaultsProjectFile = new FileInfo(Path.Combine(serviceDefaultsFolder.FullName, "UpdateTester.ServiceDefaults.csproj"));

        var appHostFolder = workspace.CreateDirectory("UpdateTester.AppHost");
        var appHostProjectFile = new FileInfo(Path.Combine(appHostFolder.FullName, "UpdateTester.AppHost.csproj"));

        var directoryPackagesPropsFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "Directory.Packages.props"));

        // Create AppHost project file
        await File.WriteAllTextAsync(
            appHostProjectFile.FullName,
            $$"""
            <Project Sdk="Microsoft.NET.Sdk">
                <Sdk Name="Aspire.AppHost.Sdk" Version="9.5.1" />
            </Project>
            """);

        // Create Service Defaults project file without Version attributes (CPM)
        await File.WriteAllTextAsync(
            serviceDefaultsProjectFile.FullName,
            $$"""
            <Project Sdk="Microsoft.NET.Sdk">
                <PropertyGroup>
                    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
                </PropertyGroup>
                <ItemGroup>
                    <PackageReference Include="Aspire.StackExchange.Redis.OutputCaching" />
                </ItemGroup>
            </Project>
            """);

        // Create Directory.Packages.props with outdated versions
        await File.WriteAllTextAsync(
            directoryPackagesPropsFile.FullName,
            $$"""
            <Project>
                <PropertyGroup>
                    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
                </PropertyGroup>
                <ItemGroup>
                    <PackageVersion Include="Aspire.StackExchange.Redis.OutputCaching" Version="9.4.1" />
                    <PackageVersion Include="Microsoft.Extensions.ServiceDiscovery" Version="9.4.1" />
                </ItemGroup>
            </Project>
            """);

        var updatedFiles = new List<string>();

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, config =>
        {
            config.DotNetCliRunnerFactory = (sp) =>
            {
                return new TestDotNetCliRunner()
                {
                    SearchPackagesAsyncCallback = (_, query, _, _, _, _, _, _, _) =>
                    {
                        var packages = new List<NuGetPackageCli>();

                        packages.Add(query switch
                        {
                            "Aspire.AppHost.Sdk" => new NuGetPackageCli { Id = "Aspire.AppHost.Sdk", Version = "9.5.0", Source = "nuget.org" },
                            "Aspire.Hosting.AppHost" => new NuGetPackageCli { Id = "Aspire.Hosting.AppHost", Version = "9.5.0", Source = "nuget.org" },
                            "Aspire.StackExchange.Redis.OutputCaching" => new NuGetPackageCli { Id = "Aspire.StackExchange.Redis.OutputCaching", Version = "9.5.0", Source = "nuget.org" },
                            "Microsoft.Extensions.ServiceDiscovery" => new NuGetPackageCli { Id = "Microsoft.Extensions.ServiceDiscovery", Version = "9.5.0", Source = "nuget.org" },
                            _ => throw new InvalidOperationException($"Unexpected package query: {query}"),
                        });

                        return (0, packages.ToArray());
                    },
                    GetProjectItemsAndPropertiesAsyncCallback = (projectFile, items, properties, options, cancellationToken) =>
                    {
                        var itemsAndProperties = new JsonObject();

                        if (projectFile.FullName == appHostProjectFile.FullName)
                        {
                            itemsAndProperties.WithSdkVersion("9.4.1");
                            itemsAndProperties.WithProjectReference(serviceDefaultsProjectFile.FullName);
                        }
                        else if (projectFile.FullName == serviceDefaultsProjectFile.FullName)
                        {
                            // For CPM projects, PackageReference elements don't have Version attributes
                            itemsAndProperties.WithPackageReferenceWithoutVersion("Aspire.StackExchange.Redis.OutputCaching");
                        }
                        else
                        {
                            throw new InvalidOperationException("Unexpected project file.");
                        }

                        var json = itemsAndProperties.ToJsonString();
                        var document = JsonDocument.Parse(json);
                        return (0, document);
                    }
                };
            };

            config.InteractionServiceFactory = (sp) =>
            {
                var interactionService = new TestInteractionService();
                interactionService.ConfirmCallback = (promptText, defaultValue) =>
                {
                    return true;
                };

                return interactionService;
            };
        });
        var provider = services.BuildServiceProvider();

        var logger = provider.GetRequiredService<ILogger<ProjectUpdater>>();
        var runner = provider.GetRequiredService<IDotNetCliRunner>();
        var interactionService = provider.GetRequiredService<IInteractionService>();
        var cache = provider.GetRequiredService<IMemoryCache>();
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var packagingService = provider.GetRequiredService<IPackagingService>();

        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();
        var selectedChannel = channels.Single(c => c.Name == "default");

        var projectUpdater = provider.GetRequiredService<IProjectUpdater>();
        var updateResult = await projectUpdater.UpdateProjectAsync(appHostProjectFile, selectedChannel).DefaultTimeout();

        Assert.True(updateResult.UpdatedApplied);

        // Verify Directory.Packages.props was updated
        var updatedContent = await File.ReadAllTextAsync(directoryPackagesPropsFile.FullName);
        Assert.Contains("Aspire.StackExchange.Redis.OutputCaching\" Version=\"9.5.0\"", updatedContent); // Redis package should be updated
        Assert.DoesNotContain("Aspire.StackExchange.Redis.OutputCaching\" Version=\"9.4.1\"", updatedContent); // Redis package should not contain old version
        // Microsoft.Extensions.ServiceDiscovery should remain unchanged since it's not referenced in any project file
        Assert.Contains("Microsoft.Extensions.ServiceDiscovery\" Version=\"9.4.1\"", updatedContent);
    }

    [Fact]
    public async Task UpdateProjectFileAsync_CentralPackageManagement_DetectedByDirectoryPackagesProps()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var appHostFolder = workspace.CreateDirectory("UpdateTester.AppHost");
        var appHostProjectFile = new FileInfo(Path.Combine(appHostFolder.FullName, "UpdateTester.AppHost.csproj"));

        var directoryPackagesPropsFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "Directory.Packages.props"));

        // Create AppHost project file without ManagePackageVersionsCentrally property
        await File.WriteAllTextAsync(
            appHostProjectFile.FullName,
            $$"""
            <Project Sdk="Microsoft.NET.Sdk">
                <Sdk Name="Aspire.AppHost.Sdk" Version="9.5.1" />
                <ItemGroup>
                    <PackageReference Include="Aspire.Hosting.Redis" />
                </ItemGroup>
            </Project>
            """);

        // Create Directory.Packages.props (presence should be detected as CPM)
        await File.WriteAllTextAsync(
            directoryPackagesPropsFile.FullName,
            $$"""
            <Project>
                <PropertyGroup>
                    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
                </PropertyGroup>
                <ItemGroup>
                    <PackageVersion Include="Aspire.Hosting.Redis" Version="9.4.1" />
                </ItemGroup>
            </Project>
            """);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, config =>
        {
            config.DotNetCliRunnerFactory = (sp) =>
            {
                return new TestDotNetCliRunner()
                {
                    SearchPackagesAsyncCallback = (_, query, _, _, _, _, _, _, _) =>
                    {
                        var packages = new List<NuGetPackageCli>();

                        packages.Add(query switch
                        {
                            "Aspire.AppHost.Sdk" => new NuGetPackageCli { Id = "Aspire.AppHost.Sdk", Version = "9.5.0", Source = "nuget.org" },
                            "Aspire.Hosting.AppHost" => new NuGetPackageCli { Id = "Aspire.Hosting.AppHost", Version = "9.5.0", Source = "nuget.org" },
                            "Aspire.Hosting.Redis" => new NuGetPackageCli { Id = "Aspire.Hosting.Redis", Version = "9.5.0", Source = "nuget.org" },
                            _ => throw new InvalidOperationException($"Unexpected package query: {query}"),
                        });

                        return (0, packages.ToArray());
                    },
                    GetProjectItemsAndPropertiesAsyncCallback = (projectFile, items, properties, options, cancellationToken) =>
                    {
                        var itemsAndProperties = new JsonObject();
                        itemsAndProperties.WithSdkVersion("9.4.1");
                        itemsAndProperties.WithPackageReferenceWithoutVersion("Aspire.Hosting.Redis");

                        var json = itemsAndProperties.ToJsonString();
                        var document = JsonDocument.Parse(json);
                        return (0, document);
                    }
                };
            };

            config.InteractionServiceFactory = (sp) =>
            {
                var interactionService = new TestInteractionService();
                interactionService.ConfirmCallback = (promptText, defaultValue) =>
                {
                    return true;
                };

                return interactionService;
            };
        });
        var provider = services.BuildServiceProvider();

        var logger = provider.GetRequiredService<ILogger<ProjectUpdater>>();
        var runner = provider.GetRequiredService<IDotNetCliRunner>();
        var interactionService = provider.GetRequiredService<IInteractionService>();
        var cache = provider.GetRequiredService<IMemoryCache>();
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var packagingService = provider.GetRequiredService<IPackagingService>();

        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();
        var selectedChannel = channels.Single(c => c.Name == "default");

        var projectUpdater = provider.GetRequiredService<IProjectUpdater>();
        var updateResult = await projectUpdater.UpdateProjectAsync(appHostProjectFile, selectedChannel).DefaultTimeout();

        Assert.True(updateResult.UpdatedApplied);

        // Verify Directory.Packages.props was updated
        var updatedContent = await File.ReadAllTextAsync(directoryPackagesPropsFile.FullName);
        Assert.Contains("Version=\"9.5.0\"", updatedContent);
    }

    [Fact]
    public async Task UpdateProjectFileAsync_CentralPackageManagement_PackageNotInDirectoryPackagesProps()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var appHostFolder = workspace.CreateDirectory("UpdateTester.AppHost");
        var appHostProjectFile = new FileInfo(Path.Combine(appHostFolder.FullName, "UpdateTester.AppHost.csproj"));

        var directoryPackagesPropsFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "Directory.Packages.props"));

        // Create AppHost project file
        await File.WriteAllTextAsync(
            appHostProjectFile.FullName,
            $$"""
            <Project Sdk="Microsoft.NET.Sdk">
                <Sdk Name="Aspire.AppHost.Sdk" Version="9.5.1" />
                <ItemGroup>
                    <PackageReference Include="Aspire.Hosting.Redis" />
                </ItemGroup>
            </Project>
            """);

        // Create Directory.Packages.props without the package (should be skipped)
        await File.WriteAllTextAsync(
            directoryPackagesPropsFile.FullName,
            $$"""
            <Project>
                <PropertyGroup>
                    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
                </PropertyGroup>
                <ItemGroup>
                    <!-- Package not included here -->
                </ItemGroup>
            </Project>
            """);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, config =>
        {
            config.DotNetCliRunnerFactory = (sp) =>
            {
                return new TestDotNetCliRunner()
                {
                    SearchPackagesAsyncCallback = (_, query, _, _, _, _, _, _, _) =>
                    {
                        var packages = new List<NuGetPackageCli>();

                        packages.Add(query switch
                        {
                            "Aspire.AppHost.Sdk" => new NuGetPackageCli { Id = "Aspire.AppHost.Sdk", Version = "9.5.0", Source = "nuget.org" },
                            "Aspire.Hosting.AppHost" => new NuGetPackageCli { Id = "Aspire.Hosting.AppHost", Version = "9.5.0", Source = "nuget.org" },
                            "Aspire.Hosting.Redis" => new NuGetPackageCli { Id = "Aspire.Hosting.Redis", Version = "9.5.0", Source = "nuget.org" },
                            _ => throw new InvalidOperationException($"Unexpected package query: {query}"),
                        });

                        return (0, packages.ToArray());
                    },
                    GetProjectItemsAndPropertiesAsyncCallback = (projectFile, items, properties, options, cancellationToken) =>
                    {
                        var itemsAndProperties = new JsonObject();
                        itemsAndProperties.WithSdkVersion("9.5.0"); // Already up to date
                        itemsAndProperties.WithPackageReferenceWithoutVersion("Aspire.Hosting.Redis");

                        var json = itemsAndProperties.ToJsonString();
                        var document = JsonDocument.Parse(json);
                        return (0, document);
                    }
                };
            };

            config.InteractionServiceFactory = (sp) =>
            {
                var interactionService = new TestInteractionService();
                interactionService.ConfirmCallback = (promptText, defaultValue) =>
                {
                    // Should not be called since no updates are needed
                    throw new InvalidOperationException("Should not prompt when no work required.");
                };

                return interactionService;
            };
        });
        var provider = services.BuildServiceProvider();

        var logger = provider.GetRequiredService<ILogger<ProjectUpdater>>();
        var runner = provider.GetRequiredService<IDotNetCliRunner>();
        var interactionService = provider.GetRequiredService<IInteractionService>();
        var cache = provider.GetRequiredService<IMemoryCache>();
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var packagingService = provider.GetRequiredService<IPackagingService>();

        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();
        var selectedChannel = channels.Single(c => c.Name == "default");

        var projectUpdater = provider.GetRequiredService<IProjectUpdater>();
        var updateResult = await projectUpdater.UpdateProjectAsync(appHostProjectFile, selectedChannel).DefaultTimeout();

        Assert.False(updateResult.UpdatedApplied);

        // Verify Directory.Packages.props was not modified
        var content = await File.ReadAllTextAsync(directoryPackagesPropsFile.FullName);
        Assert.DoesNotContain("Aspire.Hosting.Redis", content);
    }

    private static Aspire.Cli.CliExecutionContext CreateExecutionContext(DirectoryInfo workingDirectory)
    {
        // NOTE: This would normally be in the users home directory, but for tests we create
        //       it in the temporary workspace directory.
        var settingsDirectory = workingDirectory.CreateSubdirectory(".aspire");
        var hivesDirectory = settingsDirectory.CreateSubdirectory("hives");
        var cacheDirectory = new DirectoryInfo(Path.Combine(workingDirectory.FullName, ".aspire", "cache"));
        return new CliExecutionContext(workingDirectory, hivesDirectory, cacheDirectory, new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log");
    }

    [Fact]
    public void PackageUpdateStep_GetFormattedDisplayText_ReturnsFormattedString()
    {
        // Arrange
        var projectFile = new FileInfo("/path/to/MyProject.csproj");
        var packageStep = new PackageUpdateStep(
            "Update package Aspire.Hosting.Redis from 9.0.0 to 9.1.0",
            () => Task.CompletedTask,
            "Aspire.Hosting.Redis",
            "9.0.0",
            "9.1.0",
            projectFile);

        // Act
        var formattedText = packageStep.GetFormattedDisplayText();

        // Assert
        Assert.Equal("[bold yellow]Aspire.Hosting.Redis[/] [bold green]9.0.0[/] to [bold green]9.1.0[/]", formattedText);
    }

    [Fact]
    public async Task UpdateProjectFileAsync_CentralPackageManagement_ResolvesAspireVersionProperty()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var appHostFolder = workspace.CreateDirectory("UpdateTester.AppHost");
        var appHostProjectFile = new FileInfo(Path.Combine(appHostFolder.FullName, "UpdateTester.AppHost.csproj"));

        var directoryPackagesPropsFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "Directory.Packages.props"));

        // Create AppHost project file
        await File.WriteAllTextAsync(
            appHostProjectFile.FullName,
            $$"""
            <Project Sdk="Microsoft.NET.Sdk">
                <Sdk Name="Aspire.AppHost.Sdk" Version="9.5.1" />
                <ItemGroup>
                    <PackageReference Include="Aspire.Hosting.Redis" />
                </ItemGroup>
            </Project>
            """);

        // Create Directory.Packages.props with MSBuild property expression
        await File.WriteAllTextAsync(
            directoryPackagesPropsFile.FullName,
            $$"""
            <Project>
                <PropertyGroup>
                    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
                    <AspireVersion>9.4.1</AspireVersion>
                </PropertyGroup>
                <ItemGroup>
                    <PackageVersion Include="Aspire.Hosting.Redis" Version="$(AspireVersion)" />
                </ItemGroup>
            </Project>
            """);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, config =>
        {
            config.DotNetCliRunnerFactory = (sp) =>
            {
                return new TestDotNetCliRunner()
                {
                    SearchPackagesAsyncCallback = (_, query, _, _, _, _, _, _, _) =>
                    {
                        var packages = new List<NuGetPackageCli>();

                        packages.Add(query switch
                        {
                            "Aspire.AppHost.Sdk" => new NuGetPackageCli { Id = "Aspire.AppHost.Sdk", Version = "9.5.0", Source = "nuget.org" },
                            "Aspire.Hosting.Redis" => new NuGetPackageCli { Id = "Aspire.Hosting.Redis", Version = "9.5.0", Source = "nuget.org" },
                            _ => throw new InvalidOperationException($"Unexpected package query: {query}"),
                        });

                        return (0, packages.ToArray());
                    },
                    GetProjectItemsAndPropertiesAsyncCallback = (projectFile, items, properties, options, cancellationToken) =>
                    {
                        var itemsAndProperties = new JsonObject();

                        // For SDK version queries
                        if (properties.Contains("AspireHostingSDKVersion"))
                        {
                            itemsAndProperties.WithSdkVersion("9.4.1");
                            itemsAndProperties.WithPackageReferenceWithoutVersion("Aspire.Hosting.Redis");
                        }

                        // For property resolution queries
                        if (properties.Contains("AspireVersion"))
                        {
                            itemsAndProperties.WithProperty("AspireVersion", "9.4.1");
                        }

                        var json = itemsAndProperties.ToJsonString();
                        var document = JsonDocument.Parse(json);
                        return (0, document);
                    }
                };
            };

            config.InteractionServiceFactory = (sp) =>
            {
                var interactionService = new TestInteractionService();
                interactionService.ConfirmCallback = (promptText, defaultValue) =>
                {
                    return true;
                };

                return interactionService;
            };
        });
        var provider = services.BuildServiceProvider();

        var logger = provider.GetRequiredService<ILogger<ProjectUpdater>>();
        var runner = provider.GetRequiredService<IDotNetCliRunner>();
        var interactionService = provider.GetRequiredService<IInteractionService>();
        var cache = provider.GetRequiredService<IMemoryCache>();
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var packagingService = provider.GetRequiredService<IPackagingService>();

        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();
        var selectedChannel = channels.Single(c => c.Name == "default");

        var projectUpdater = provider.GetRequiredService<IProjectUpdater>();
        var updateResult = await projectUpdater.UpdateProjectAsync(appHostProjectFile, selectedChannel).DefaultTimeout();

        Assert.True(updateResult.UpdatedApplied);

        // Verify Directory.Packages.props was updated with new version
        var content = await File.ReadAllTextAsync(directoryPackagesPropsFile.FullName);
        Assert.Contains("<PackageVersion Include=\"Aspire.Hosting.Redis\" Version=\"9.5.0\" />", content);
    }

    [Fact]
    public async Task UpdateProjectFileAsync_CentralPackageManagement_ResolvesMultipleProperties()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var appHostFolder = workspace.CreateDirectory("UpdateTester.AppHost");
        var appHostProjectFile = new FileInfo(Path.Combine(appHostFolder.FullName, "UpdateTester.AppHost.csproj"));

        var directoryPackagesPropsFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "Directory.Packages.props"));

        // Create AppHost project file
        await File.WriteAllTextAsync(
            appHostProjectFile.FullName,
            $$"""
            <Project Sdk="Microsoft.NET.Sdk">
                <Sdk Name="Aspire.AppHost.Sdk" Version="9.5.1" />
                <ItemGroup>
                    <PackageReference Include="Aspire.Hosting.Redis" />
                    <PackageReference Include="Aspire.StackExchange.Redis" />
                </ItemGroup>
            </Project>
            """);

        // Create Directory.Packages.props with multiple MSBuild property expressions
        await File.WriteAllTextAsync(
            directoryPackagesPropsFile.FullName,
            $$"""
            <Project>
                <PropertyGroup>
                    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
                    <AspireVersion>9.4.1</AspireVersion>
                    <AspireUnstableVersion>9.4.1-preview.1</AspireUnstableVersion>
                </PropertyGroup>
                <ItemGroup>
                    <PackageVersion Include="Aspire.Hosting.Redis" Version="$(AspireVersion)" />
                    <PackageVersion Include="Aspire.StackExchange.Redis" Version="$(AspireUnstableVersion)" />
                </ItemGroup>
            </Project>
            """);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, config =>
        {
            config.DotNetCliRunnerFactory = (sp) =>
            {
                return new TestDotNetCliRunner()
                {
                    SearchPackagesAsyncCallback = (_, query, _, _, _, _, _, _, _) =>
                    {
                        var packages = new List<NuGetPackageCli>();

                        packages.Add(query switch
                        {
                            "Aspire.AppHost.Sdk" => new NuGetPackageCli { Id = "Aspire.AppHost.Sdk", Version = "9.5.0", Source = "nuget.org" },
                            "Aspire.Hosting.Redis" => new NuGetPackageCli { Id = "Aspire.Hosting.Redis", Version = "9.5.0", Source = "nuget.org" },
                            "Aspire.StackExchange.Redis" => new NuGetPackageCli { Id = "Aspire.StackExchange.Redis", Version = "9.5.0-preview.1", Source = "nuget.org" },
                            _ => throw new InvalidOperationException($"Unexpected package query: {query}"),
                        });

                        return (0, packages.ToArray());
                    },
                    GetProjectItemsAndPropertiesAsyncCallback = (projectFile, items, properties, options, cancellationToken) =>
                    {
                        var itemsAndProperties = new JsonObject();

                        // For SDK version queries
                        if (properties.Contains("AspireHostingSDKVersion"))
                        {
                            itemsAndProperties.WithSdkVersion("9.4.1");
                            itemsAndProperties.WithPackageReferenceWithoutVersion("Aspire.Hosting.Redis");
                            itemsAndProperties.WithPackageReferenceWithoutVersion("Aspire.StackExchange.Redis");
                        }

                        // For property resolution queries
                        if (properties.Contains("AspireVersion"))
                        {
                            itemsAndProperties.WithProperty("AspireVersion", "9.4.1");
                        }

                        if (properties.Contains("AspireUnstableVersion"))
                        {
                            itemsAndProperties.WithProperty("AspireUnstableVersion", "9.4.1-preview.1");
                        }

                        var json = itemsAndProperties.ToJsonString();
                        var document = JsonDocument.Parse(json);
                        return (0, document);
                    }
                };
            };

            config.InteractionServiceFactory = (sp) =>
            {
                var interactionService = new TestInteractionService();
                interactionService.ConfirmCallback = (promptText, defaultValue) =>
                {
                    return true;
                };

                return interactionService;
            };
        });
        var provider = services.BuildServiceProvider();

        var logger = provider.GetRequiredService<ILogger<ProjectUpdater>>();
        var runner = provider.GetRequiredService<IDotNetCliRunner>();
        var interactionService = provider.GetRequiredService<IInteractionService>();
        var cache = provider.GetRequiredService<IMemoryCache>();
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var packagingService = provider.GetRequiredService<IPackagingService>();

        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();
        var selectedChannel = channels.Single(c => c.Name == "default");

        var projectUpdater = provider.GetRequiredService<IProjectUpdater>();
        var updateResult = await projectUpdater.UpdateProjectAsync(appHostProjectFile, selectedChannel).DefaultTimeout();

        Assert.True(updateResult.UpdatedApplied);

        // Verify Directory.Packages.props was updated with new versions
        var content = await File.ReadAllTextAsync(directoryPackagesPropsFile.FullName);
        Assert.Contains("<PackageVersion Include=\"Aspire.Hosting.Redis\" Version=\"9.5.0\" />", content);
        Assert.Contains("<PackageVersion Include=\"Aspire.StackExchange.Redis\" Version=\"9.5.0-preview.1\" />", content);
    }

    [Fact]
    public async Task UpdateProjectFileAsync_CentralPackageManagement_PropertyResolutionFailsWithInvalidSemanticVersion()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var appHostFolder = workspace.CreateDirectory("UpdateTester.AppHost");
        var appHostProjectFile = new FileInfo(Path.Combine(appHostFolder.FullName, "UpdateTester.AppHost.csproj"));

        var directoryPackagesPropsFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "Directory.Packages.props"));

        // Create AppHost project file
        await File.WriteAllTextAsync(
            appHostProjectFile.FullName,
            $$"""
            <Project Sdk="Microsoft.NET.Sdk">
                <Sdk Name="Aspire.AppHost.Sdk" Version="9.5.1" />
                <ItemGroup>
                    <PackageReference Include="Aspire.Hosting.Redis" />
                </ItemGroup>
            </Project>
            """);

        // Create Directory.Packages.props with MSBuild property expression that resolves to an invalid version
        await File.WriteAllTextAsync(
            directoryPackagesPropsFile.FullName,
            $$"""
            <Project>
                <PropertyGroup>
                    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
                    <InvalidVersionProperty>not-a-version</InvalidVersionProperty>
                </PropertyGroup>
                <ItemGroup>
                    <PackageVersion Include="Aspire.Hosting.Redis" Version="$(InvalidVersionProperty)" />
                </ItemGroup>
            </Project>
            """);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, config =>
        {
            config.DotNetCliRunnerFactory = (sp) =>
            {
                return new TestDotNetCliRunner()
                {
                    SearchPackagesAsyncCallback = (_, query, _, _, _, _, _, _, _) =>
                    {
                        var packages = new List<NuGetPackageCli>();

                        packages.Add(query switch
                        {
                            "Aspire.AppHost.Sdk" => new NuGetPackageCli { Id = "Aspire.AppHost.Sdk", Version = "9.5.0", Source = "nuget.org" },
                            "Aspire.Hosting.Redis" => new NuGetPackageCli { Id = "Aspire.Hosting.Redis", Version = "9.5.0", Source = "nuget.org" },
                            _ => throw new InvalidOperationException($"Unexpected package query: {query}"),
                        });

                        return (0, packages.ToArray());
                    },
                    GetProjectItemsAndPropertiesAsyncCallback = (projectFile, items, properties, options, cancellationToken) =>
                    {
                        var itemsAndProperties = new JsonObject();

                        // For SDK version queries
                        if (properties.Contains("AspireHostingSDKVersion"))
                        {
                            itemsAndProperties.WithSdkVersion("9.4.1");
                            itemsAndProperties.WithPackageReferenceWithoutVersion("Aspire.Hosting.Redis");
                        }

                        // For property resolution queries - return invalid semantic version
                        if (properties.Contains("InvalidVersionProperty"))
                        {
                            itemsAndProperties.WithProperty("InvalidVersionProperty", "not-a-version");
                        }

                        var json = itemsAndProperties.ToJsonString();
                        var document = JsonDocument.Parse(json);
                        return (0, document);
                    }
                };
            };

            config.InteractionServiceFactory = (sp) =>
            {
                var interactionService = new TestInteractionService();
                interactionService.ConfirmCallback = (promptText, defaultValue) =>
                {
                    return true;
                };

                return interactionService;
            };
        });
        var provider = services.BuildServiceProvider();

        var logger = provider.GetRequiredService<ILogger<ProjectUpdater>>();
        var runner = provider.GetRequiredService<IDotNetCliRunner>();
        var interactionService = provider.GetRequiredService<IInteractionService>();
        var cache = provider.GetRequiredService<IMemoryCache>();
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var packagingService = provider.GetRequiredService<IPackagingService>();

        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();
        var selectedChannel = channels.Single(c => c.Name == "default");

        var projectUpdater = provider.GetRequiredService<IProjectUpdater>();

        // This should throw a ProjectUpdaterException
        var exception = await Assert.ThrowsAsync<ProjectUpdaterException>(async () =>
        {
            await projectUpdater.UpdateProjectAsync(appHostProjectFile, selectedChannel).DefaultTimeout();
        });

        Assert.Contains("Unable to resolve MSBuild property 'InvalidVersionProperty' to a valid semantic version", exception.Message);
    }

    [Fact]
    public async Task UpdateProjectFileAsync_CentralPackageManagement_PropertyResolutionFailsWithUnresolvableProperty()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var appHostFolder = workspace.CreateDirectory("UpdateTester.AppHost");
        var appHostProjectFile = new FileInfo(Path.Combine(appHostFolder.FullName, "UpdateTester.AppHost.csproj"));

        var directoryPackagesPropsFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "Directory.Packages.props"));

        // Create AppHost project file
        await File.WriteAllTextAsync(
            appHostProjectFile.FullName,
            $$"""
            <Project Sdk="Microsoft.NET.Sdk">
                <Sdk Name="Aspire.AppHost.Sdk" Version="9.5.1" />
                <ItemGroup>
                    <PackageReference Include="Aspire.Hosting.Redis" />
                </ItemGroup>
            </Project>
            """);

        // Create Directory.Packages.props with MSBuild property expression that cannot be resolved
        await File.WriteAllTextAsync(
            directoryPackagesPropsFile.FullName,
            $$"""
            <Project>
                <PropertyGroup>
                    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
                </PropertyGroup>
                <ItemGroup>
                    <PackageVersion Include="Aspire.Hosting.Redis" Version="$(NonExistentProperty)" />
                </ItemGroup>
            </Project>
            """);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, config =>
        {
            config.DotNetCliRunnerFactory = (sp) =>
            {
                return new TestDotNetCliRunner()
                {
                    SearchPackagesAsyncCallback = (_, query, _, _, _, _, _, _, _) =>
                    {
                        var packages = new List<NuGetPackageCli>();

                        packages.Add(query switch
                        {
                            "Aspire.AppHost.Sdk" => new NuGetPackageCli { Id = "Aspire.AppHost.Sdk", Version = "9.5.0", Source = "nuget.org" },
                            "Aspire.Hosting.Redis" => new NuGetPackageCli { Id = "Aspire.Hosting.Redis", Version = "9.5.0", Source = "nuget.org" },
                            _ => throw new InvalidOperationException($"Unexpected package query: {query}"),
                        });

                        return (0, packages.ToArray());
                    },
                    GetProjectItemsAndPropertiesAsyncCallback = (projectFile, items, properties, options, cancellationToken) =>
                    {
                        var itemsAndProperties = new JsonObject();

                        // For SDK version queries
                        if (properties.Contains("AspireHostingSDKVersion"))
                        {
                            itemsAndProperties.WithSdkVersion("9.4.1");
                            itemsAndProperties.WithPackageReferenceWithoutVersion("Aspire.Hosting.Redis");
                        }

                        // For property resolution queries - don't include the property, simulating it doesn't exist
                        // This will result in the property not being in the response, which should be handled gracefully

                        var json = itemsAndProperties.ToJsonString();
                        var document = JsonDocument.Parse(json);
                        return (0, document);
                    }
                };
            };

            config.InteractionServiceFactory = (sp) =>
            {
                var interactionService = new TestInteractionService();
                interactionService.ConfirmCallback = (promptText, defaultValue) =>
                {
                    return true;
                };

                return interactionService;
            };
        });
        var provider = services.BuildServiceProvider();

        var logger = provider.GetRequiredService<ILogger<ProjectUpdater>>();
        var runner = provider.GetRequiredService<IDotNetCliRunner>();
        var interactionService = provider.GetRequiredService<IInteractionService>();
        var cache = provider.GetRequiredService<IMemoryCache>();
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var packagingService = provider.GetRequiredService<IPackagingService>();

        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();
        var selectedChannel = channels.Single(c => c.Name == "default");

        var projectUpdater = provider.GetRequiredService<IProjectUpdater>();

        // This should throw a ProjectUpdaterException
        var exception = await Assert.ThrowsAsync<ProjectUpdaterException>(async () =>
        {
            await projectUpdater.UpdateProjectAsync(appHostProjectFile, selectedChannel).DefaultTimeout();
        });

        Assert.Contains("Unable to resolve MSBuild property 'NonExistentProperty' to a valid semantic version", exception.Message);
        Assert.Contains("Resolved value: 'null'", exception.Message);
    }

    [Fact]
    public async Task UpdateProject_FallbackMode_WhenSdkUnresolvable()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var appHostFolder = workspace.CreateDirectory("UpdateTester.AppHost");
        var appHostProjectFile = new FileInfo(Path.Combine(appHostFolder.FullName, "UpdateTester.AppHost.csproj"));

        // Create AppHost project file with an unresolvable SDK version
        await File.WriteAllTextAsync(
            appHostProjectFile.FullName,
            """
            <Project Sdk="Microsoft.NET.Sdk">
                <Sdk Name="Aspire.AppHost.Sdk" Version="99.0.0-unresolvable" />
                <ItemGroup>
                    <PackageReference Include="Aspire.Hosting.AppHost" Version="99.0.0-unresolvable" />
                    <PackageReference Include="Aspire.Hosting.Redis" Version="99.0.0-unresolvable" />
                </ItemGroup>
            </Project>
            """);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, config =>
        {
            config.DotNetCliRunnerFactory = (sp) =>
            {
                return new TestDotNetCliRunner()
                {
                    SearchPackagesAsyncCallback = (_, query, _, _, _, _, _, _, _) =>
                    {
                        var packages = new List<NuGetPackageCli>();

                        packages.Add(query switch
                        {
                            "Aspire.AppHost.Sdk" => new NuGetPackageCli { Id = "Aspire.AppHost.Sdk", Version = "9.5.0", Source = "nuget.org" },
                            "Aspire.Hosting.AppHost" => new NuGetPackageCli { Id = "Aspire.Hosting.AppHost", Version = "9.5.0", Source = "nuget.org" },
                            "Aspire.Hosting.Redis" => new NuGetPackageCli { Id = "Aspire.Hosting.Redis", Version = "9.5.0", Source = "nuget.org" },
                            _ => throw new InvalidOperationException($"Unexpected package query: {query}"),
                        });

                        return (0, packages.ToArray());
                    },

                    GetProjectItemsAndPropertiesAsyncCallback = (projectFile, _, _, _, _) =>
                    {
                        if (projectFile.FullName == appHostProjectFile.FullName)
                        {
                            // Simulate MSBuild failure due to unresolvable SDK
                            return (1, null);
                        }
                        else
                        {
                            throw new InvalidOperationException("Unexpected project file.");
                        }
                    },

                    AddPackageAsyncCallback = (projectFile, packageName, version, source, noRestore, options, cancellationToken) =>
                    {
                        // Simulate successful package addition
                        return 0;
                    }
                };
            };

            config.InteractionServiceFactory = (sp) =>
            {
                var interactionService = new TestInteractionService();
                interactionService.ConfirmCallback = (promptText, defaultValue) =>
                {
                    return true;
                };

                return interactionService;
            };
        });
        var provider = services.BuildServiceProvider();

        var logger = provider.GetRequiredService<ILogger<ProjectUpdater>>();
        var runner = provider.GetRequiredService<IDotNetCliRunner>();
        var interactionService = provider.GetRequiredService<IInteractionService>();
        var cache = provider.GetRequiredService<IMemoryCache>();
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var fallbackParser = provider.GetRequiredService<FallbackProjectParser>();
        var packagingService = provider.GetRequiredService<IPackagingService>();

        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();
        var selectedChannel = channels.Single(c => c.Name == "default");

        var projectUpdater = new ProjectUpdater(logger, runner, interactionService, cache, executionContext, fallbackParser);
        var updateResult = await projectUpdater.UpdateProjectAsync(appHostProjectFile, selectedChannel).DefaultTimeout();

        // Should not throw ProjectUpdaterException; should produce update steps including AppHost SDK
        Assert.True(updateResult.UpdatedApplied);
    }

    [Fact]
    public async Task FallbackMode_PackageReferenceWithoutVersion_CPM()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var appHostFolder = workspace.CreateDirectory("UpdateTester.AppHost");
        var appHostProjectFile = new FileInfo(Path.Combine(appHostFolder.FullName, "UpdateTester.AppHost.csproj"));

        var directoryPackagesPropsFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "Directory.Packages.props"));

        // Create AppHost project file with package reference without version (CPM)
        await File.WriteAllTextAsync(
            appHostProjectFile.FullName,
            """
            <Project Sdk="Microsoft.NET.Sdk">
                <Sdk Name="Aspire.AppHost.Sdk" Version="99.0.0-unresolvable" />
                <ItemGroup>
                    <PackageReference Include="Aspire.Hosting.Redis" />
                </ItemGroup>
            </Project>
            """);

        // Create Directory.Packages.props
        await File.WriteAllTextAsync(
            directoryPackagesPropsFile.FullName,
            """
            <Project>
                <PropertyGroup>
                    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
                </PropertyGroup>
                <ItemGroup>
                    <PackageVersion Include="Aspire.Hosting.Redis" Version="9.4.1" />
                </ItemGroup>
            </Project>
            """);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, config =>
        {
            config.DotNetCliRunnerFactory = (sp) =>
            {
                return new TestDotNetCliRunner()
                {
                    SearchPackagesAsyncCallback = (_, query, _, _, _, _, _, _, _) =>
                    {
                        var packages = new List<NuGetPackageCli>();

                        packages.Add(query switch
                        {
                            "Aspire.AppHost.Sdk" => new NuGetPackageCli { Id = "Aspire.AppHost.Sdk", Version = "9.5.0", Source = "nuget.org" },
                            "Aspire.Hosting.Redis" => new NuGetPackageCli { Id = "Aspire.Hosting.Redis", Version = "9.5.0", Source = "nuget.org" },
                            _ => throw new InvalidOperationException($"Unexpected package query: {query}"),
                        });

                        return (0, packages.ToArray());
                    },

                    GetProjectItemsAndPropertiesAsyncCallback = (projectFile, _, _, _, _) =>
                    {
                        if (projectFile.FullName == appHostProjectFile.FullName)
                        {
                            // Simulate MSBuild failure due to unresolvable SDK
                            return (1, null);
                        }
                        else
                        {
                            throw new InvalidOperationException("Unexpected project file.");
                        }
                    }
                };
            };

            config.InteractionServiceFactory = (sp) =>
            {
                var interactionService = new TestInteractionService();
                interactionService.ConfirmCallback = (promptText, defaultValue) =>
                {
                    return true;
                };

                return interactionService;
            };
        });
        var provider = services.BuildServiceProvider();

        var logger = provider.GetRequiredService<ILogger<ProjectUpdater>>();
        var runner = provider.GetRequiredService<IDotNetCliRunner>();
        var interactionService = provider.GetRequiredService<IInteractionService>();
        var cache = provider.GetRequiredService<IMemoryCache>();
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var fallbackParser = provider.GetRequiredService<FallbackProjectParser>();
        var packagingService = provider.GetRequiredService<IPackagingService>();

        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();
        var selectedChannel = channels.Single(c => c.Name == "default");

        var projectUpdater = new ProjectUpdater(logger, runner, interactionService, cache, executionContext, fallbackParser);
        var updateResult = await projectUpdater.UpdateProjectAsync(appHostProjectFile, selectedChannel).DefaultTimeout();

        // Should discover package reference (version may be absent) and not crash
        Assert.True(updateResult.UpdatedApplied);
    }

    [Fact]
    public async Task FallbackMode_InvalidXml_StillFails()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var appHostFolder = workspace.CreateDirectory("UpdateTester.AppHost");
        var appHostProjectFile = new FileInfo(Path.Combine(appHostFolder.FullName, "UpdateTester.AppHost.csproj"));

        // Create malformed AppHost project file
        await File.WriteAllTextAsync(
            appHostProjectFile.FullName,
            """
            <Project Sdk="Microsoft.NET.Sdk">
                <Sdk Name="Aspire.AppHost.Sdk" Version="99.0.0-unresolvable" />
                <!-- Missing closing tag to make invalid XML -->
                <ItemGroup>
                    <PackageReference Include="Aspire.Hosting.AppHost" Version="99.0.0-unresolvable" />
            """);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, config =>
        {
            config.DotNetCliRunnerFactory = (sp) =>
            {
                return new TestDotNetCliRunner()
                {
                    SearchPackagesAsyncCallback = (_, query, _, _, _, _, _, _, _) =>
                    {
                        var packages = new List<NuGetPackageCli>();

                        packages.Add(query switch
                        {
                            "Aspire.AppHost.Sdk" => new NuGetPackageCli { Id = "Aspire.AppHost.Sdk", Version = "9.5.0", Source = "nuget.org" },
                            _ => throw new InvalidOperationException($"Unexpected package query: {query}"),
                        });

                        return (0, packages.ToArray());
                    },

                    GetProjectItemsAndPropertiesAsyncCallback = (projectFile, _, _, _, _) =>
                    {
                        if (projectFile.FullName == appHostProjectFile.FullName)
                        {
                            // Simulate MSBuild failure due to unresolvable SDK
                            return (1, null);
                        }
                        else
                        {
                            throw new InvalidOperationException("Unexpected project file.");
                        }
                    }
                };
            };

            config.InteractionServiceFactory = (sp) =>
            {
                var interactionService = new TestInteractionService();
                return interactionService;
            };
        });
        var provider = services.BuildServiceProvider();

        var logger = provider.GetRequiredService<ILogger<ProjectUpdater>>();
        var runner = provider.GetRequiredService<IDotNetCliRunner>();
        var interactionService = provider.GetRequiredService<IInteractionService>();
        var cache = provider.GetRequiredService<IMemoryCache>();
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var fallbackParser = provider.GetRequiredService<FallbackProjectParser>();
        var packagingService = provider.GetRequiredService<IPackagingService>();

        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();
        var selectedChannel = channels.Single(c => c.Name == "default");

        var projectUpdater = new ProjectUpdater(logger, runner, interactionService, cache, executionContext, fallbackParser);

        // Should throw ProjectUpdaterException due to invalid XML
        await Assert.ThrowsAsync<ProjectUpdaterException>(() =>
            projectUpdater.UpdateProjectAsync(appHostProjectFile, selectedChannel).DefaultTimeout());
    }

    [Fact]
    public async Task NormalMode_NoFallback()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var appHostFolder = workspace.CreateDirectory("UpdateTester.AppHost");
        var appHostProjectFile = new FileInfo(Path.Combine(appHostFolder.FullName, "UpdateTester.AppHost.csproj"));

        await File.WriteAllTextAsync(
            appHostProjectFile.FullName,
            $$"""
            <Project Sdk="Microsoft.NET.Sdk">
                <Sdk Name="Aspire.AppHost.Sdk" Version="9.4.1" />
            </Project>
            """);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, config =>
        {
            config.DotNetCliRunnerFactory = (sp) =>
            {
                return new TestDotNetCliRunner()
                {
                    SearchPackagesAsyncCallback = (_, query, _, _, _, _, _, _, _) =>
                    {
                        var packages = new List<NuGetPackageCli>();

                        packages.Add(query switch
                        {
                            "Aspire.AppHost.Sdk" => new NuGetPackageCli { Id = "Aspire.AppHost.Sdk", Version = "9.4.1", Source = "nuget.org" },
                            _ => throw new InvalidOperationException("Unexpected package query."),
                        });

                        return (0, packages.ToArray());
                    },

                    GetProjectItemsAndPropertiesAsyncCallback = (projectFile, _, _, _, _) =>
                    {
                        // Normal successful MSBuild evaluation
                        var itemsAndProperties = new JsonObject();

                        if (projectFile.FullName == appHostProjectFile.FullName)
                        {
                            itemsAndProperties.WithSdkVersion("9.4.1");
                        }
                        else
                        {
                            throw new InvalidOperationException("Unexpected project file.");
                        }

                        var json = itemsAndProperties.ToJsonString();
                        var document = JsonDocument.Parse(json);
                        return (0, document);
                    }
                };
            };

            config.InteractionServiceFactory = (sp) =>
            {
                var interactionService = new TestInteractionService();
                return interactionService;
            };
        });
        var provider = services.BuildServiceProvider();

        var logger = provider.GetRequiredService<ILogger<ProjectUpdater>>();
        var runner = provider.GetRequiredService<IDotNetCliRunner>();
        var interactionService = provider.GetRequiredService<IInteractionService>();
        var cache = provider.GetRequiredService<IMemoryCache>();
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var fallbackParser = provider.GetRequiredService<FallbackProjectParser>();
        var packagingService = provider.GetRequiredService<IPackagingService>();

        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();
        var selectedChannel = channels.Single(c => c.Name == "default");

        var projectUpdater = new ProjectUpdater(logger, runner, interactionService, cache, executionContext, fallbackParser);
        var updateResult = await projectUpdater.UpdateProjectAsync(appHostProjectFile, selectedChannel).DefaultTimeout();

        // Normal path unaffected - no updates needed since version is already current
        Assert.False(updateResult.UpdatedApplied);
    }

    [Fact]
    public async Task UpdateProjectFileAsync_SingleFileAppHost_UpdatesSdkDirectiveWithVersion()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var appHostFolder = workspace.CreateDirectory("UpdateTester.AppHost");
        var appHostFile = new FileInfo(Path.Combine(appHostFolder.FullName, "apphost.cs"));

        // Create a single-file app host with an explicit version using @ separator
        await File.WriteAllTextAsync(
            appHostFile.FullName,
            """
            #:sdk Aspire.AppHost.Sdk@9.4.1
            using Aspire.Hosting;
            var builder = DistributedApplication.CreateBuilder(args);
            builder.Build().Run();
            """);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, config =>
        {
            config.DotNetCliRunnerFactory = (sp) =>
            {
                return new TestDotNetCliRunner()
                {
                    SearchPackagesAsyncCallback = (_, query, _, _, _, _, _, _, _) =>
                    {
                        var packages = new List<NuGetPackageCli>();

                        packages.Add(query switch
                        {
                            "Aspire.AppHost.Sdk" => new NuGetPackageCli { Id = "Aspire.AppHost.Sdk", Version = "9.5.0", Source = "nuget.org" },
                            _ => throw new InvalidOperationException($"Unexpected package query: {query}"),
                        });

                        return (0, packages.ToArray());
                    },
                    GetProjectItemsAndPropertiesAsyncCallback = (projectFile, items, properties, options, cancellationToken) =>
                    {
                        var itemsAndProperties = new JsonObject();
                        itemsAndProperties.WithSdkVersion("9.4.1");

                        var json = itemsAndProperties.ToJsonString();
                        var document = JsonDocument.Parse(json);
                        return (0, document);
                    }
                };
            };

            config.InteractionServiceFactory = (sp) =>
            {
                var interactionService = new TestInteractionService();
                interactionService.ConfirmCallback = (promptText, defaultValue) =>
                {
                    return true;
                };

                return interactionService;
            };
        });
        var provider = services.BuildServiceProvider();

        var logger = provider.GetRequiredService<ILogger<ProjectUpdater>>();
        var runner = provider.GetRequiredService<IDotNetCliRunner>();
        var interactionService = provider.GetRequiredService<IInteractionService>();
        var cache = provider.GetRequiredService<IMemoryCache>();
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var fallbackParser = provider.GetRequiredService<FallbackProjectParser>();
        var packagingService = provider.GetRequiredService<IPackagingService>();

        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();
        var selectedChannel = channels.Single(c => c.Name == "default");

        var projectUpdater = new ProjectUpdater(logger, runner, interactionService, cache, executionContext, fallbackParser);
        var updateResult = await projectUpdater.UpdateProjectAsync(appHostFile, selectedChannel).DefaultTimeout();

        Assert.True(updateResult.UpdatedApplied);

        // Verify the SDK directive was updated from old to new version
        var updatedContent = await File.ReadAllTextAsync(appHostFile.FullName);
        Assert.Contains("#:sdk Aspire.AppHost.Sdk@9.5.0", updatedContent);
        Assert.DoesNotContain("9.4.1", updatedContent);
    }

    [Fact]
    public async Task UpdateProjectFileAsync_SingleFileAppHost_UpdatesSdkDirectiveWithWildcard()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var appHostFolder = workspace.CreateDirectory("UpdateTester.AppHost");
        var appHostFile = new FileInfo(Path.Combine(appHostFolder.FullName, "apphost.cs"));

        // Create a single-file app host with wildcard version
        await File.WriteAllTextAsync(
            appHostFile.FullName,
            """
            #:sdk Aspire.AppHost.Sdk@*
            using Aspire.Hosting;
            var builder = DistributedApplication.CreateBuilder(args);
            builder.Build().Run();
            """);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, config =>
        {
            config.DotNetCliRunnerFactory = (sp) =>
            {
                return new TestDotNetCliRunner()
                {
                    SearchPackagesAsyncCallback = (_, query, _, _, _, _, _, _, _) =>
                    {
                        var packages = new List<NuGetPackageCli>();

                        packages.Add(query switch
                        {
                            "Aspire.AppHost.Sdk" => new NuGetPackageCli { Id = "Aspire.AppHost.Sdk", Version = "9.5.0", Source = "nuget.org" },
                            _ => throw new InvalidOperationException($"Unexpected package query: {query}"),
                        });

                        return (0, packages.ToArray());
                    },
                    GetProjectItemsAndPropertiesAsyncCallback = (projectFile, items, properties, options, cancellationToken) =>
                    {
                        var itemsAndProperties = new JsonObject();
                        itemsAndProperties.WithSdkVersion("*");

                        var json = itemsAndProperties.ToJsonString();
                        var document = JsonDocument.Parse(json);
                        return (0, document);
                    }
                };
            };

            config.InteractionServiceFactory = (sp) =>
            {
                var interactionService = new TestInteractionService();
                interactionService.ConfirmCallback = (promptText, defaultValue) =>
                {
                    return true;
                };

                return interactionService;
            };
        });
        var provider = services.BuildServiceProvider();

        var logger = provider.GetRequiredService<ILogger<ProjectUpdater>>();
        var runner = provider.GetRequiredService<IDotNetCliRunner>();
        var interactionService = provider.GetRequiredService<IInteractionService>();
        var cache = provider.GetRequiredService<IMemoryCache>();
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var fallbackParser = provider.GetRequiredService<FallbackProjectParser>();
        var packagingService = provider.GetRequiredService<IPackagingService>();

        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();
        var selectedChannel = channels.Single(c => c.Name == "default");

        var projectUpdater = new ProjectUpdater(logger, runner, interactionService, cache, executionContext, fallbackParser);
        var updateResult = await projectUpdater.UpdateProjectAsync(appHostFile, selectedChannel).DefaultTimeout();

        Assert.True(updateResult.UpdatedApplied);

        // Verify the SDK directive was updated from wildcard to specific version
        var updatedContent = await File.ReadAllTextAsync(appHostFile.FullName);
        Assert.Contains("#:sdk Aspire.AppHost.Sdk@9.5.0", updatedContent);
    }

    [Fact]
    public async Task UpdateProjectFileAsync_PackageReferenceWithWildcard_DoesNotFail()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var appHostFolder = workspace.CreateDirectory("UpdateTester.AppHost");
        var appHostProjectFile = new FileInfo(Path.Combine(appHostFolder.FullName, "UpdateTester.AppHost.csproj"));

        await File.WriteAllTextAsync(
            appHostProjectFile.FullName,
            """
            <Project Sdk="Microsoft.NET.Sdk">
                <Sdk Name="Aspire.AppHost.Sdk" Version="9.5.1" />
            </Project>
            """);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, config =>
        {
            config.DotNetCliRunnerFactory = (sp) =>
            {
                return new TestDotNetCliRunner()
                {
                    SearchPackagesAsyncCallback = (_, query, _, _, _, _, _, _, _) =>
                    {
                        var packages = new List<NuGetPackageCli>();

                        packages.Add(query switch
                        {
                            "Aspire.AppHost.Sdk" => new NuGetPackageCli { Id = "Aspire.AppHost.Sdk", Version = "9.5.0", Source = "nuget.org" },
                            "Aspire.Hosting.AppHost" => new NuGetPackageCli { Id = "Aspire.Hosting.AppHost", Version = "9.5.0", Source = "nuget.org" },
                            _ => throw new InvalidOperationException($"Unexpected package query: {query}"),
                        });

                        return (0, packages.ToArray());
                    },
                    GetProjectItemsAndPropertiesAsyncCallback = (projectFile, items, properties, options, cancellationToken) =>
                    {
                        var itemsAndProperties = new JsonObject();
                        itemsAndProperties.WithSdkVersion("9.4.1");
                        // Add a package reference with wildcard version
                        itemsAndProperties.WithPackageReference("Aspire.Hosting.AppHost", "*");

                        var json = itemsAndProperties.ToJsonString();
                        var document = JsonDocument.Parse(json);
                        return (0, document);
                    },
                    AddPackageAsyncCallback = (projectFile, packageName, version, source, noRestore, options, cancellationToken) =>
                    {
                        // Simulate successful package addition
                        return 0;
                    }
                };
            };

            config.InteractionServiceFactory = (sp) =>
            {
                var interactionService = new TestInteractionService();
                interactionService.ConfirmCallback = (promptText, defaultValue) =>
                {
                    return true;
                };

                return interactionService;
            };
        });
        var provider = services.BuildServiceProvider();

        var logger = provider.GetRequiredService<ILogger<ProjectUpdater>>();
        var runner = provider.GetRequiredService<IDotNetCliRunner>();
        var interactionService = provider.GetRequiredService<IInteractionService>();
        var cache = provider.GetRequiredService<IMemoryCache>();
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var fallbackParser = provider.GetRequiredService<FallbackProjectParser>();
        var packagingService = provider.GetRequiredService<IPackagingService>();

        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();
        var selectedChannel = channels.Single(c => c.Name == "default");

        var projectUpdater = new ProjectUpdater(logger, runner, interactionService, cache, executionContext, fallbackParser);

        // This should not throw and should handle the * version gracefully
        var updateResult = await projectUpdater.UpdateProjectAsync(appHostProjectFile, selectedChannel).DefaultTimeout();

        Assert.True(updateResult.UpdatedApplied);
    }

    [Fact]
    public async Task UpdateProjectFileAsync_AppHost_UpdatesSdkAttributeFormat()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var appHostFolder = workspace.CreateDirectory("UpdateTester.AppHost");
        var appHostProjectFile = new FileInfo(Path.Combine(appHostFolder.FullName, "UpdateTester.AppHost.csproj"));

        // Create AppHost project with SDK specified via Sdk attribute on Project element (new 13.0 format)
        await File.WriteAllTextAsync(
            appHostProjectFile.FullName,
            """
            <Project Sdk="Aspire.AppHost.Sdk/9.4.1">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, config =>
        {
            config.DotNetCliRunnerFactory = (sp) =>
            {
                return new TestDotNetCliRunner()
                {
                    SearchPackagesAsyncCallback = (_, query, _, _, _, _, _, _, _) =>
                    {
                        var packages = new List<NuGetPackageCli>();

                        packages.Add(query switch
                        {
                            "Aspire.AppHost.Sdk" => new NuGetPackageCli { Id = "Aspire.AppHost.Sdk", Version = "9.5.0", Source = "nuget.org" },
                            _ => throw new InvalidOperationException($"Unexpected package query: {query}"),
                        });

                        return (0, packages.ToArray());
                    },
                    GetProjectItemsAndPropertiesAsyncCallback = (projectFile, items, properties, options, cancellationToken) =>
                    {
                        var itemsAndProperties = new JsonObject();
                        itemsAndProperties.WithSdkVersion("9.4.1");

                        var json = itemsAndProperties.ToJsonString();
                        var document = JsonDocument.Parse(json);
                        return (0, document);
                    }
                };
            };

            config.InteractionServiceFactory = (sp) =>
            {
                var interactionService = new TestInteractionService();
                interactionService.ConfirmCallback = (promptText, defaultValue) =>
                {
                    return true;
                };

                return interactionService;
            };
        });
        var provider = services.BuildServiceProvider();

        var packagingService = provider.GetRequiredService<IPackagingService>();
        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();
        var selectedChannel = channels.Single(c => c.Name == "default");

        var projectUpdater = provider.GetRequiredService<IProjectUpdater>();
        var updateResult = await projectUpdater.UpdateProjectAsync(appHostProjectFile, selectedChannel).DefaultTimeout();

        Assert.True(updateResult.UpdatedApplied);

        // Verify the SDK attribute was updated
        var updatedContent = await File.ReadAllTextAsync(appHostProjectFile.FullName);
        Assert.Contains("Aspire.AppHost.Sdk/9.5.0", updatedContent);
        Assert.DoesNotContain("9.4.1", updatedContent);
    }

    [Fact]
    public async Task UpdateProjectFileAsync_AppHost_UpdatesSdkElementFormat()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var appHostFolder = workspace.CreateDirectory("UpdateTester.AppHost");
        var appHostProjectFile = new FileInfo(Path.Combine(appHostFolder.FullName, "UpdateTester.AppHost.csproj"));

        // Create AppHost project with SDK specified via Sdk element (old 9.5 format)
        await File.WriteAllTextAsync(
            appHostProjectFile.FullName,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <Sdk Name="Aspire.AppHost.Sdk" Version="9.4.1" />
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, config =>
        {
            config.DotNetCliRunnerFactory = (sp) =>
            {
                return new TestDotNetCliRunner()
                {
                    SearchPackagesAsyncCallback = (_, query, _, _, _, _, _, _, _) =>
                    {
                        var packages = new List<NuGetPackageCli>();

                        packages.Add(query switch
                        {
                            "Aspire.AppHost.Sdk" => new NuGetPackageCli { Id = "Aspire.AppHost.Sdk", Version = "9.5.0", Source = "nuget.org" },
                            _ => throw new InvalidOperationException($"Unexpected package query: {query}"),
                        });

                        return (0, packages.ToArray());
                    },
                    GetProjectItemsAndPropertiesAsyncCallback = (projectFile, items, properties, options, cancellationToken) =>
                    {
                        var itemsAndProperties = new JsonObject();
                        itemsAndProperties.WithSdkVersion("9.4.1");

                        var json = itemsAndProperties.ToJsonString();
                        var document = JsonDocument.Parse(json);
                        return (0, document);
                    }
                };
            };

            config.InteractionServiceFactory = (sp) =>
            {
                var interactionService = new TestInteractionService();
                interactionService.ConfirmCallback = (promptText, defaultValue) =>
                {
                    return true;
                };

                return interactionService;
            };
        });
        var provider = services.BuildServiceProvider();

        var packagingService = provider.GetRequiredService<IPackagingService>();
        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();
        var selectedChannel = channels.Single(c => c.Name == "default");

        var projectUpdater = provider.GetRequiredService<IProjectUpdater>();
        var updateResult = await projectUpdater.UpdateProjectAsync(appHostProjectFile, selectedChannel).DefaultTimeout();

        Assert.True(updateResult.UpdatedApplied);

        // Verify the project was migrated to the new SDK format
        var updatedContent = await File.ReadAllTextAsync(appHostProjectFile.FullName);
        await Verify(updatedContent, extension: "xml");
    }

    [Fact]
    public async Task UpdateProjectFileAsync_TreatsVersionRangeExpressionsLikeWildcard()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var appHostFolder = workspace.CreateDirectory("UpdateTester.AppHost");
        var appHostProjectFile = new FileInfo(Path.Combine(appHostFolder.FullName, "UpdateTester.AppHost.csproj"));

        await File.WriteAllTextAsync(
            appHostProjectFile.FullName,
            """
            <Project Sdk="Aspire.AppHost.Sdk/9.4.1">
              <ItemGroup>
                <PackageReference Include="Aspire.Hosting.Azure.Functions" Version="(9.4-*,9.5]" />
                <PackageReference Include="Aspire.Hosting.Redis" Version="9.4.1" />
              </ItemGroup>
            </Project>
            """);

        var packagesUpdated = new List<string>();

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, config =>
        {
            config.DotNetCliRunnerFactory = (sp) =>
            {
                return new TestDotNetCliRunner()
                {
                    SearchPackagesAsyncCallback = (_, query, _, _, _, _, _, _, _) =>
                    {
                        var packages = new List<NuGetPackageCli>();

                        packages.Add(query switch
                        {
                            "Aspire.AppHost.Sdk" => new NuGetPackageCli { Id = "Aspire.AppHost.Sdk", Version = "9.5.0", Source = "nuget.org" },
                            "Aspire.Hosting.Azure.Functions" => new NuGetPackageCli { Id = "Aspire.Hosting.Azure.Functions", Version = "9.5.0", Source = "nuget.org" },
                            "Aspire.Hosting.Redis" => new NuGetPackageCli { Id = "Aspire.Hosting.Redis", Version = "9.5.0", Source = "nuget.org" },
                            _ => throw new InvalidOperationException($"Unexpected package query: {query}"),
                        });

                        return (0, packages.ToArray());
                    },
                    GetProjectItemsAndPropertiesAsyncCallback = (projectFile, items, properties, options, cancellationToken) =>
                    {
                        var itemsAndProperties = new JsonObject();
                        itemsAndProperties.WithSdkVersion("9.4.1");
                        // Package with version range expression - should be treated like wildcard and updated
                        itemsAndProperties.WithPackageReference("Aspire.Hosting.Azure.Functions", "(9.4-*,9.5]");
                        // Normal package - should be updated
                        itemsAndProperties.WithPackageReference("Aspire.Hosting.Redis", "9.4.1");

                        var json = itemsAndProperties.ToJsonString();
                        var document = JsonDocument.Parse(json);
                        return (0, document);
                    },
                    AddPackageAsyncCallback = (projectFilePath, packageName, packageVersion, nugetSource, noRestore, options, cancellationToken) =>
                    {
                        // Track which packages are updated
                        packagesUpdated.Add(packageName);
                        Assert.Equal("9.5.0", packageVersion);
                        return 0;
                    }
                };
            };

            config.InteractionServiceFactory = (sp) =>
            {
                var interactionService = new TestInteractionService();
                interactionService.ConfirmCallback = (promptText, defaultValue) =>
                {
                    return true;
                };

                return interactionService;
            };
        });
        var provider = services.BuildServiceProvider();

        var packagingService = provider.GetRequiredService<IPackagingService>();
        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();
        var selectedChannel = channels.Single(c => c.Name == "default");

        var projectUpdater = provider.GetRequiredService<IProjectUpdater>();
        var updateResult = await projectUpdater.UpdateProjectAsync(appHostProjectFile, selectedChannel).DefaultTimeout();

        // Both packages should be updated - range expression is treated like wildcard
        Assert.True(updateResult.UpdatedApplied);
        Assert.Contains("Aspire.Hosting.Azure.Functions", packagesUpdated);
        Assert.Contains("Aspire.Hosting.Redis", packagesUpdated);
    }

    [Theory]
    [InlineData("#:sdk Aspire.AppHost.Sdk@9.5.2", true)]
    [InlineData("#:sdk Aspire.AppHost.Sdk@*", true)]
    [InlineData("#:sdk Aspire.AppHost.Sdk@10.0.0-preview.1", true)]
    [InlineData("#:sdk Aspire.AppHost.Sdk", false)]
    [InlineData("#:sdk Aspire.AppHost.Sdk@", false)]
    [InlineData("#:sdk Aspire.AppHost.Sdk 9.5.2", false)]
    [InlineData("#:package Aspire.Hosting.Redis@9.5.2", false)]
    [InlineData("#:package Aspire.Hosting.Redis", false)]
    public void SdkDirectiveRegex_MatchesValidDirectivesOnly(string directive, bool shouldMatch)
    {
        var regex = ProjectUpdater.SdkDirectiveRegex();
        var match = regex.IsMatch(directive);

        Assert.Equal(shouldMatch, match);
    }

    [Fact]
    public async Task UpdateProjectFileAsync_ManagePackageVersionsCentrallyFalse_UpdatesLocalProjectFile()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var appHostFolder = workspace.CreateDirectory("UpdateTester.AppHost");
        var appHostProjectFile = new FileInfo(Path.Combine(appHostFolder.FullName, "UpdateTester.AppHost.csproj"));

        var directoryPackagesPropsFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "Directory.Packages.props"));

        // Create AppHost project file with ManagePackageVersionsCentrally set to false
        await File.WriteAllTextAsync(
            appHostProjectFile.FullName,
            """
            <Project Sdk="Microsoft.NET.Sdk">
                <Sdk Name="Aspire.AppHost.Sdk" Version="9.5.2" />
                <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net9.0</TargetFramework>
                    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
                </PropertyGroup>
                <ItemGroup>
                    <PackageReference Include="Aspire.Hosting.Redis" Version="9.5.2" />
                </ItemGroup>
            </Project>
            """);

        // Create Directory.Packages.props (which should be ignored due to ManagePackageVersionsCentrally=false)
        await File.WriteAllTextAsync(
            directoryPackagesPropsFile.FullName,
            """
            <Project>
                <PropertyGroup>
                    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
                </PropertyGroup>
                <ItemGroup>
                </ItemGroup>
            </Project>
            """);

        var packagesAddsExecuted = new List<(FileInfo ProjectFile, string PackageId, string PackageVersion, string? PackageSource, bool NoRestore)>();

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, config =>
        {
            config.DotNetCliRunnerFactory = (sp) =>
            {
                return new TestDotNetCliRunner()
                {
                    SearchPackagesAsyncCallback = (_, query, _, _, _, _, _, _, _) =>
                    {
                        var packages = new List<NuGetPackageCli>();

                        packages.Add(query switch
                        {
                            "Aspire.AppHost.Sdk" => new NuGetPackageCli { Id = "Aspire.AppHost.Sdk", Version = "9.6.0", Source = "nuget.org" },
                            "Aspire.Hosting.Redis" => new NuGetPackageCli { Id = "Aspire.Hosting.Redis", Version = "9.6.0", Source = "nuget.org" },
                            _ => throw new InvalidOperationException($"Unexpected package query: {query}"),
                        });

                        return (0, packages.ToArray());
                    },
                    GetProjectItemsAndPropertiesAsyncCallback = (projectFile, items, properties, options, cancellationToken) =>
                    {
                        var itemsAndProperties = new JsonObject();
                        itemsAndProperties.WithSdkVersion("9.5.2");

                        // Set ManagePackageVersionsCentrally to false
                        itemsAndProperties.WithProperty("ManagePackageVersionsCentrally", "false");

                        // Package has version in the project file (not in Directory.Packages.props)
                        itemsAndProperties.WithPackageReference("Aspire.Hosting.Redis", "9.5.2");

                        var json = itemsAndProperties.ToJsonString();
                        var document = JsonDocument.Parse(json);
                        return (0, document);
                    },
                    AddPackageAsyncCallback = (projectFile, packageId, packageVersion, source, noRestore, _, _) =>
                    {
                        packagesAddsExecuted.Add((projectFile, packageId, packageVersion, source!, noRestore));
                        return 0;
                    }
                };
            };

            config.InteractionServiceFactory = (sp) =>
            {
                var interactionService = new TestInteractionService();
                interactionService.ConfirmCallback = (promptText, defaultValue) =>
                {
                    return true;
                };

                return interactionService;
            };
        });
        var provider = services.BuildServiceProvider();

        var logger = provider.GetRequiredService<ILogger<ProjectUpdater>>();
        var runner = provider.GetRequiredService<IDotNetCliRunner>();
        var interactionService = provider.GetRequiredService<IInteractionService>();
        var cache = provider.GetRequiredService<IMemoryCache>();
        var executionContext = CreateExecutionContext(workspace.WorkspaceRoot);
        var packagingService = provider.GetRequiredService<IPackagingService>();

        var channels = await packagingService.GetChannelsAsync().DefaultTimeout();
        var selectedChannel = channels.Single(c => c.Name == "default");

        var projectUpdater = provider.GetRequiredService<IProjectUpdater>();
        var updateResult = await projectUpdater.UpdateProjectAsync(appHostProjectFile, selectedChannel).DefaultTimeout();

        Assert.True(updateResult.UpdatedApplied);

        // Verify that the package was updated via dotnet add package (traditional management),
        // not by updating Directory.Packages.props
        Assert.Single(packagesAddsExecuted);
        var packageUpdate = packagesAddsExecuted.Single();
        Assert.Equal("Aspire.Hosting.Redis", packageUpdate.PackageId);
        Assert.Equal("9.6.0", packageUpdate.PackageVersion);
        Assert.Equal(appHostProjectFile.FullName, packageUpdate.ProjectFile.FullName);

        // Verify Directory.Packages.props was NOT modified (should still be empty)
        var directoryPackagesContent = await File.ReadAllTextAsync(directoryPackagesPropsFile.FullName);
        Assert.DoesNotContain("Aspire.Hosting.Redis", directoryPackagesContent);
    }

    [Fact]
    public async Task UpdateSdkVersionInCsprojAppHostAsync_MigratesFromOldFormatToNewFormat()
    {
        // Arrange - tests migration from old <Sdk Name="..."> to new <Project Sdk="..."> format
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectFile = Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj");
        var originalContent = """
            <Project Sdk="Microsoft.NET.Sdk">
                <Sdk Name="Aspire.AppHost.Sdk" Version="9.5.0" />
                <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net9.0</TargetFramework>
                </PropertyGroup>
            </Project>
            """;

        await File.WriteAllTextAsync(projectFile, originalContent);

        var package = new NuGetPackageCli { Id = "Aspire.AppHost.Sdk", Version = "13.0.2", Source = "nuget.org" };

        // Act
        await ProjectUpdater.UpdateSdkVersionInCsprojAppHostAsync(new FileInfo(projectFile), package).DefaultTimeout();

        // Assert
        var updatedContent = await File.ReadAllTextAsync(projectFile);
        await Verify(updatedContent, extension: "xml");
    }

    [Fact]
    public async Task UpdateSdkVersionInCsprojAppHostAsync_UpdatesExistingNewFormat()
    {
        // Arrange - tests updating a project already using the new format
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectFile = Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj");
        var originalContent = """
            <Project Sdk="Aspire.AppHost.Sdk/13.0.1">
                <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                </PropertyGroup>
            </Project>
            """;

        await File.WriteAllTextAsync(projectFile, originalContent);

        var package = new NuGetPackageCli { Id = "Aspire.AppHost.Sdk", Version = "13.0.2", Source = "nuget.org" };

        // Act
        await ProjectUpdater.UpdateSdkVersionInCsprojAppHostAsync(new FileInfo(projectFile), package).DefaultTimeout();

        // Assert
        var updatedContent = await File.ReadAllTextAsync(projectFile);
        await Verify(updatedContent, extension: "xml");
    }

    [Fact]
    public async Task UpdateSdkVersionInCsprojAppHostAsync_RemovesAspireHostingAppHostPackageReference()
    {
        // Arrange - tests removal of obsolete Aspire.Hosting.AppHost package reference
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectFile = Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj");
        var originalContent = """
            <Project Sdk="Microsoft.NET.Sdk">
                <Sdk Name="Aspire.AppHost.Sdk" Version="9.5.0" />
                <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net9.0</TargetFramework>
                </PropertyGroup>
                <ItemGroup>
                    <PackageReference Include="Aspire.Hosting.AppHost" Version="9.5.0" />
                    <PackageReference Include="Aspire.Hosting.Redis" Version="9.5.0" />
                </ItemGroup>
            </Project>
            """;

        await File.WriteAllTextAsync(projectFile, originalContent);

        var package = new NuGetPackageCli { Id = "Aspire.AppHost.Sdk", Version = "13.0.2", Source = "nuget.org" };

        // Act
        await ProjectUpdater.UpdateSdkVersionInCsprojAppHostAsync(new FileInfo(projectFile), package).DefaultTimeout();

        // Assert
        var updatedContent = await File.ReadAllTextAsync(projectFile);
        await Verify(updatedContent, extension: "xml");
    }

    [Fact]
    public async Task UpdateSdkVersionInCsprojAppHostAsync_RemovesEmptyItemGroupAfterPackageRemoval()
    {
        // Arrange - tests that empty ItemGroup is removed after Aspire.Hosting.AppHost removal
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectFile = Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj");
        var originalContent = """
            <Project Sdk="Microsoft.NET.Sdk">
                <Sdk Name="Aspire.AppHost.Sdk" Version="9.5.0" />
                <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net9.0</TargetFramework>
                </PropertyGroup>
                <ItemGroup>
                    <PackageReference Include="Aspire.Hosting.AppHost" Version="9.5.0" />
                </ItemGroup>
            </Project>
            """;

        await File.WriteAllTextAsync(projectFile, originalContent);

        var package = new NuGetPackageCli { Id = "Aspire.AppHost.Sdk", Version = "13.0.2", Source = "nuget.org" };

        // Act
        await ProjectUpdater.UpdateSdkVersionInCsprojAppHostAsync(new FileInfo(projectFile), package).DefaultTimeout();

        // Assert
        var updatedContent = await File.ReadAllTextAsync(projectFile);
        await Verify(updatedContent, extension: "xml");
    }

    [Fact]
    public async Task UpdateSdkVersionInCsprojAppHostAsync_PreservesOtherSdksInAttribute()
    {
        // Arrange - tests that other SDKs in the attribute are preserved
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectFile = Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj");
        var originalContent = """
            <Project Sdk="Aspire.AppHost.Sdk/13.0.1;Microsoft.NET.Sdk.Web">
                <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                </PropertyGroup>
            </Project>
            """;

        await File.WriteAllTextAsync(projectFile, originalContent);

        var package = new NuGetPackageCli { Id = "Aspire.AppHost.Sdk", Version = "13.0.2", Source = "nuget.org" };

        // Act
        await ProjectUpdater.UpdateSdkVersionInCsprojAppHostAsync(new FileInfo(projectFile), package).DefaultTimeout();

        // Assert
        var updatedContent = await File.ReadAllTextAsync(projectFile);
        await Verify(updatedContent, extension: "xml");
    }

    [Fact]
    public async Task UpdateSdkVersionInCsprojAppHostAsync_DoesNotMatchSimilarSdkName()
    {
        // Arrange - tests that Aspire.AppHost.SdkFoo doesn't match as the Aspire SDK
        // In this case, the old <Sdk Name="..."> element is used, so it's a migration scenario
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectFile = Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj");
        var originalContent = """
            <Project Sdk="Aspire.AppHost.SdkFoo/1.0.0">
                <Sdk Name="Aspire.AppHost.Sdk" Version="9.5.0" />
                <PropertyGroup>
                    <OutputType>Exe</OutputType>
                </PropertyGroup>
            </Project>
            """;

        await File.WriteAllTextAsync(projectFile, originalContent);

        var package = new NuGetPackageCli { Id = "Aspire.AppHost.Sdk", Version = "13.0.2", Source = "nuget.org" };

        // Act
        await ProjectUpdater.UpdateSdkVersionInCsprojAppHostAsync(new FileInfo(projectFile), package).DefaultTimeout();

        // Assert
        var updatedContent = await File.ReadAllTextAsync(projectFile);
        await Verify(updatedContent, extension: "xml");
    }
    [Fact]
    public async Task UpdateSdkVersionInCsprojAppHostAsync_RemovesPackageVersionFromDirectoryPackagesPropsForCpm()
    {
        // Arrange - simulates a CPM project with an old-format AppHost that has
        // Aspire.Hosting.AppHost in both csproj (as PackageReference) and
        // Directory.Packages.props (as PackageVersion). After SDK migration, the
        // PackageReference is removed from csproj but the orphaned PackageVersion
        // must also be removed to avoid NU1009.
        // See: https://github.com/microsoft/aspire/issues/14550
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectFile = Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj");
        var originalContent = """
            <Project Sdk="Microsoft.NET.Sdk">
                <Sdk Name="Aspire.AppHost.Sdk" Version="9.5.0" />
                <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net9.0</TargetFramework>
                </PropertyGroup>
                <ItemGroup>
                    <PackageReference Include="Aspire.Hosting.AppHost" />
                    <PackageReference Include="Aspire.Hosting.Redis" />
                </ItemGroup>
            </Project>
            """;

        await File.WriteAllTextAsync(projectFile, originalContent);

        // Create Directory.Packages.props with CPM enabled and PackageVersion for Aspire.Hosting.AppHost
        var directoryPackagesPropsFile = Path.Combine(workspace.WorkspaceRoot.FullName, "Directory.Packages.props");
        await File.WriteAllTextAsync(directoryPackagesPropsFile, """
            <Project>
                <PropertyGroup>
                    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
                </PropertyGroup>
                <ItemGroup>
                    <PackageVersion Include="Aspire.Hosting.AppHost" Version="9.5.0" />
                    <PackageVersion Include="Aspire.Hosting.Redis" Version="9.5.0" />
                </ItemGroup>
            </Project>
            """);

        var package = new NuGetPackageCli { Id = "Aspire.AppHost.Sdk", Version = "13.0.2", Source = "nuget.org" };

        // Act
        await ProjectUpdater.UpdateSdkVersionInCsprojAppHostAsync(new FileInfo(projectFile), package).DefaultTimeout();

        // Assert - PackageVersion for Aspire.Hosting.AppHost should be removed from Directory.Packages.props
        var updatedPropsContent = await File.ReadAllTextAsync(directoryPackagesPropsFile);
        Assert.DoesNotContain("Aspire.Hosting.AppHost", updatedPropsContent);
        // Other PackageVersion entries should be preserved
        Assert.Contains("Aspire.Hosting.Redis", updatedPropsContent);

        // Also verify the csproj was updated correctly
        var updatedCsprojContent = await File.ReadAllTextAsync(projectFile);
        Assert.DoesNotContain("Aspire.Hosting.AppHost", updatedCsprojContent);
        Assert.Contains("Aspire.Hosting.Redis", updatedCsprojContent);
        Assert.Contains("Aspire.AppHost.Sdk/13.0.2", updatedCsprojContent);
    }
}

internal static class MSBuildJsonDocumentExtensions
{
    public static JsonObject WithSdkVersion(this JsonObject root, string sdkVersion)
    {
        root.WithMSBuildOutput(); // Ensure Items structure exists

        JsonObject properties = new JsonObject();
        if (!root.TryAdd("Properties", properties))
        {
            properties = root["Properties"]!.AsObject();
        }

        properties.Add("AspireHostingSDKVersion", JsonValue.Create<string>(sdkVersion));
        return root;
    }

    public static JsonObject WithMSBuildOutput(this JsonObject root)
    {
        JsonObject items = new JsonObject();
        items.Add("ProjectReference", new JsonArray());
        items.Add("PackageReference", new JsonArray());

        if (!root.TryAdd("Items", items))
        {
            items = root["Items"]!.AsObject();

            // Ensure both arrays exist
            if (!items.ContainsKey("ProjectReference"))
            {
                items.Add("ProjectReference", new JsonArray());
            }
            if (!items.ContainsKey("PackageReference"))
            {
                items.Add("PackageReference", new JsonArray());
            }
        }

        return root;
    }

    public static JsonObject WithPackageReference(this JsonObject root, string packageId, string packageVersion)
    {
        root.WithMSBuildOutput();
        var items = root["Items"]!.AsObject();
        var packageReferences = items["PackageReference"]!.AsArray();

        JsonObject newPackageReference = new JsonObject
        {
            { "Identity", JsonValue.Create<string>(packageId) },
            { "Version", JsonValue.Create<string>(packageVersion) }
        };
        packageReferences.Add(newPackageReference);

        return root;
    }

    public static JsonObject WithPackageReferenceWithoutVersion(this JsonObject root, string packageId)
    {
        root.WithMSBuildOutput();
        var items = root["Items"]!.AsObject();
        var packageReferences = items["PackageReference"]!.AsArray();

        JsonObject newPackageReference = new JsonObject
        {
            { "Identity", JsonValue.Create<string>(packageId) }
            // No Version property for CPM
        };
        packageReferences.Add(newPackageReference);

        return root;
    }

    public static JsonObject WithProjectReference(this JsonObject root, string fullPath)
    {
        root.WithMSBuildOutput();
        var items = root["Items"]!.AsObject();
        var projectReferences = items["ProjectReference"]!.AsArray();

        JsonObject newProjectReference = new JsonObject
        {
            { "FullPath", JsonValue.Create<string>(fullPath) }
        };
        projectReferences.Add(newProjectReference);

        return root;
    }

    public static JsonObject WithProperty(this JsonObject root, string propertyName, string propertyValue)
    {
        JsonObject properties = new JsonObject();
        if (!root.TryAdd("Properties", properties))
        {
            properties = root["Properties"]!.AsObject();
        }

        properties.Add(propertyName, JsonValue.Create<string>(propertyValue));
        return root;
    }
}
