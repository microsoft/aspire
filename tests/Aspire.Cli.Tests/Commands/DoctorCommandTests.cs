// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.Interaction;
using Aspire.Cli.Projects;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
using Aspire.Cli.Utils.EnvironmentChecker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests.Commands;

public class DoctorCommandTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task DoctorCommand_Help_Works()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse("doctor --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        
        // Help should return success
        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public async Task DoctorCommand_Json_IncludesCliVersionStatus()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var interactionService = new TestInteractionService();
        var updateNotifier = new TestCliUpdateNotifier
        {
            GetVersionStatusAsyncCallback = (_, _) => Task.FromResult(new CliVersionStatus("13.0.0", "13.1.0", "aspire update"))
        };
        var services = CreateDoctorVersionServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.CliUpdateNotifierFactory = _ => updateNotifier;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse("doctor --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        var (json, console) = Assert.Single(interactionService.DisplayedRawText);
        Assert.Equal(ConsoleOutput.Standard, console);
        using var document = JsonDocument.Parse(json);
        var cliVersionCheck = document.RootElement.GetProperty("checks").EnumerateArray()
            .Single(check => check.GetProperty("name").GetString() == "cli-version");

        Assert.Equal("aspire", cliVersionCheck.GetProperty("category").GetString());
        Assert.Equal("warning", cliVersionCheck.GetProperty("status").GetString());
        Assert.Contains("13.0.0", cliVersionCheck.GetProperty("message").GetString()!);
        Assert.Contains("13.1.0", cliVersionCheck.GetProperty("message").GetString()!);
        var cliVersionMetadata = cliVersionCheck.GetProperty("metadata");
        Assert.Equal("13.0.0", cliVersionMetadata.GetProperty("currentVersion").GetString());
        Assert.Equal("13.1.0", cliVersionMetadata.GetProperty("latestVersion").GetString());
        Assert.Equal("aspire update", cliVersionMetadata.GetProperty("updateCommand").GetString());
    }

    [Fact]
    public async Task DoctorCommand_Json_IncludesAppHostVersionWhenAppHostExists()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(appHostFile.FullName, "<Project />");

        var interactionService = new TestInteractionService();
        var services = CreateDoctorVersionServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.CliUpdateNotifierFactory = _ => new TestCliUpdateNotifier();
            options.AppHostProjectFactory = _ => new TestAppHostProjectFactory
            {
                GetAspireHostingVersionAsyncCallback = (_, _) => Task.FromResult<string?>("13.0.0")
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse("doctor --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        var (json, _) = Assert.Single(interactionService.DisplayedRawText);
        using var document = JsonDocument.Parse(json);
        var appHostVersionCheck = document.RootElement.GetProperty("checks").EnumerateArray()
            .Single(check => check.GetProperty("name").GetString() == "apphost-version");

        Assert.Equal("apphost", appHostVersionCheck.GetProperty("category").GetString());
        Assert.Equal("pass", appHostVersionCheck.GetProperty("status").GetString());
        Assert.Contains("13.0.0", appHostVersionCheck.GetProperty("message").GetString()!);
        Assert.Contains("AppHost.csproj", appHostVersionCheck.GetProperty("message").GetString()!);
        var appHostVersionMetadata = appHostVersionCheck.GetProperty("metadata");
        Assert.Equal("13.0.0", appHostVersionMetadata.GetProperty("version").GetString());
        Assert.Equal("AppHost.csproj", appHostVersionMetadata.GetProperty("appHostPath").GetString());
    }

    [Fact]
    public async Task DoctorCommand_Json_IncludesTypeScriptAppHostVersionFromAspireConfig()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.ts"));
        await File.WriteAllTextAsync(appHostFile.FullName, "export {};");
        await File.WriteAllTextAsync(
            Path.Combine(workspace.WorkspaceRoot.FullName, "aspire.config.json"),
            """
            {
              "sdk": {
                "version": "13.1.0"
              }
            }
            """);

        var interactionService = new TestInteractionService();
        var runnerCalled = false;
        var services = CreateDoctorVersionServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.CliUpdateNotifierFactory = _ => new TestCliUpdateNotifier();
            options.AppHostProjectFactory = _ => new TestAppHostProjectFactory
            {
                CanHandleCallback = file => file.Extension.Equals(".ts", StringComparison.OrdinalIgnoreCase),
                DetectionPatterns = ["apphost.ts"],
                GetAspireHostingVersionAsyncCallback = (_, _) => Task.FromResult<string?>("13.1.0")
            };
            options.DotNetCliRunnerFactory = _ => new TestDotNetCliRunner
            {
                GetAppHostInformationAsyncCallback = (_, _, _) =>
                {
                    runnerCalled = true;
                    return (0, true, "unexpected");
                }
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse("doctor --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.False(runnerCalled);

        var (json, _) = Assert.Single(interactionService.DisplayedRawText);
        using var document = JsonDocument.Parse(json);
        var appHostVersionCheck = document.RootElement.GetProperty("checks").EnumerateArray()
            .Single(check => check.GetProperty("name").GetString() == "apphost-version");

        Assert.Equal("apphost", appHostVersionCheck.GetProperty("category").GetString());
        Assert.Equal("pass", appHostVersionCheck.GetProperty("status").GetString());
        Assert.Contains("13.1.0", appHostVersionCheck.GetProperty("message").GetString()!);
        Assert.Contains("apphost.ts", appHostVersionCheck.GetProperty("message").GetString()!);
        var appHostVersionMetadata = appHostVersionCheck.GetProperty("metadata");
        Assert.Equal("13.1.0", appHostVersionMetadata.GetProperty("version").GetString());
        Assert.Equal("apphost.ts", appHostVersionMetadata.GetProperty("appHostPath").GetString());
    }

    [Fact]
    public async Task DoctorCommand_Json_DoesNotDiscoverNestedAppHostWithoutConfig()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostFile = CreateDeepAppHostFile(workspace, depth: LanguageInfo.DetectionRecurseLimit + 1);
        await File.WriteAllTextAsync(appHostFile.FullName, "<Project />");

        var interactionService = new TestInteractionService();
        var versionLookupCalled = false;
        var services = CreateDoctorVersionServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.CliUpdateNotifierFactory = _ => new TestCliUpdateNotifier();
            options.AppHostProjectFactory = _ => new TestAppHostProjectFactory
            {
                GetAspireHostingVersionAsyncCallback = (_, _) =>
                {
                    versionLookupCalled = true;
                    return Task.FromResult<string?>("unexpected");
                }
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse("doctor --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.False(versionLookupCalled);

        var (json, _) = Assert.Single(interactionService.DisplayedRawText);
        using var document = JsonDocument.Parse(json);
        Assert.DoesNotContain(document.RootElement.GetProperty("checks").EnumerateArray(),
            check => check.GetProperty("name").GetString() == "apphost-version");
    }

    [Fact]
    public async Task DoctorCommand_Json_DoesNotShowAppHostVersionForNonAppHostProject()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "Normal.csproj"));
        await File.WriteAllTextAsync(projectFile.FullName, "<Project />");

        var interactionService = new TestInteractionService();
        var versionLookupCalled = false;
        var services = CreateDoctorVersionServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.CliUpdateNotifierFactory = _ => new TestCliUpdateNotifier();
            options.AppHostProjectFactory = _ => new TestAppHostProjectFactory
            {
                ValidateAppHostCallback = _ => new AppHostValidationResult(IsValid: false),
                GetAspireHostingVersionAsyncCallback = (_, _) =>
                {
                    versionLookupCalled = true;
                    return Task.FromResult<string?>("unexpected");
                }
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse("doctor --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.False(versionLookupCalled);

        var (json, _) = Assert.Single(interactionService.DisplayedRawText);
        using var document = JsonDocument.Parse(json);
        Assert.DoesNotContain(document.RootElement.GetProperty("checks").EnumerateArray(),
            check => check.GetProperty("name").GetString() == "apphost-version");
    }

    [Fact]
    public async Task DoctorCommand_Json_DoesNotDiscoverNestedAppHostWhenAnotherProjectExists()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "Normal.csproj"));
        await File.WriteAllTextAsync(projectFile.FullName, "<Project />");
        var appHostDirectory = workspace.WorkspaceRoot.CreateSubdirectory("app");
        var appHostFile = new FileInfo(Path.Combine(appHostDirectory.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(appHostFile.FullName, "<Project />");

        var interactionService = new TestInteractionService();
        var services = CreateDoctorVersionServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.CliUpdateNotifierFactory = _ => new TestCliUpdateNotifier();
            options.AppHostProjectFactory = _ => new TestAppHostProjectFactory
            {
                ValidateAppHostCallback = file => new AppHostValidationResult(
                    IsValid: file.Name.Equals("AppHost.csproj", StringComparison.OrdinalIgnoreCase)),
                GetAspireHostingVersionAsyncCallback = (_, _) => Task.FromResult<string?>("13.2.0")
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse("doctor --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        var (json, _) = Assert.Single(interactionService.DisplayedRawText);
        using var document = JsonDocument.Parse(json);
        Assert.DoesNotContain(document.RootElement.GetProperty("checks").EnumerateArray(),
            check => check.GetProperty("name").GetString() == "apphost-version");
    }

    [Fact]
    public async Task DoctorCommand_Json_DoesNotChooseBetweenMultipleDirectAppHostsWithoutConfig()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        await File.WriteAllTextAsync(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"), "<Project />");
        await File.WriteAllTextAsync(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.fsproj"), "<Project />");

        var interactionService = new TestInteractionService();
        var versionLookupCalled = false;
        var services = CreateDoctorVersionServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.CliUpdateNotifierFactory = _ => new TestCliUpdateNotifier();
            options.AppHostProjectFactory = _ => new TestAppHostProjectFactory
            {
                GetAspireHostingVersionAsyncCallback = (_, _) =>
                {
                    versionLookupCalled = true;
                    return Task.FromResult<string?>("unexpected");
                }
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse("doctor --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.False(versionLookupCalled);

        var (json, _) = Assert.Single(interactionService.DisplayedRawText);
        using var document = JsonDocument.Parse(json);
        Assert.DoesNotContain(document.RootElement.GetProperty("checks").EnumerateArray(),
            check => check.GetProperty("name").GetString() == "apphost-version");
    }

    [Fact]
    public async Task DoctorCommand_Json_PreservesCliVersionWhenAppHostVersionResolutionFails()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.ts"));
        await File.WriteAllTextAsync(appHostFile.FullName, "export {};");

        var interactionService = new TestInteractionService();
        var services = CreateDoctorVersionServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.CliUpdateNotifierFactory = _ => new TestCliUpdateNotifier();
            options.AppHostProjectFactory = _ => new TestAppHostProjectFactory
            {
                CanHandleCallback = file => file.Extension.Equals(".ts", StringComparison.OrdinalIgnoreCase),
                DetectionPatterns = ["apphost.ts"],
                GetAspireHostingVersionAsyncCallback = (_, _) =>
                    throw new InvalidOperationException("invalid aspire.config.json")
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse("doctor --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        var (json, _) = Assert.Single(interactionService.DisplayedRawText);
        using var document = JsonDocument.Parse(json);
        var cliVersionCheck = document.RootElement.GetProperty("checks").EnumerateArray()
            .Single(check => check.GetProperty("name").GetString() == "cli-version");
        var appHostVersionCheck = document.RootElement.GetProperty("checks").EnumerateArray()
            .Single(check => check.GetProperty("name").GetString() == "apphost-version");

        Assert.Equal("pass", cliVersionCheck.GetProperty("status").GetString());
        Assert.Equal("warning", appHostVersionCheck.GetProperty("status").GetString());
        Assert.Equal("invalid aspire.config.json", appHostVersionCheck.GetProperty("details").GetString());
        Assert.Equal(
            "apphost.ts",
            appHostVersionCheck.GetProperty("metadata").GetProperty("appHostPath").GetString());
    }

    [Fact]
    public async Task DoctorCommand_Json_PreservesCliVersionWhenAppHostDiscoveryFails()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var interactionService = new TestInteractionService();
        var services = CreateDoctorVersionServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.CliUpdateNotifierFactory = _ => new TestCliUpdateNotifier();
            options.ProjectLocatorFactory = _ => new TestProjectLocator
            {
                GetAppHostFromSettingsAsyncCallback = _ => throw new IOException("settings lookup failed")
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse("doctor --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        var (json, _) = Assert.Single(interactionService.DisplayedRawText);
        using var document = JsonDocument.Parse(json);
        var cliVersionCheck = document.RootElement.GetProperty("checks").EnumerateArray()
            .Single(check => check.GetProperty("name").GetString() == "cli-version");
        var appHostVersionCheck = document.RootElement.GetProperty("checks").EnumerateArray()
            .Single(check => check.GetProperty("name").GetString() == "apphost-version");

        Assert.Equal("pass", cliVersionCheck.GetProperty("status").GetString());
        Assert.Equal("warning", appHostVersionCheck.GetProperty("status").GetString());
        Assert.Equal("settings lookup failed", appHostVersionCheck.GetProperty("details").GetString());
    }

    [Fact]
    public async Task DoctorCommand_Json_UsesConfiguredAppHostBeyondLanguageDetectionLimit()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostFile = CreateDeepAppHostFile(workspace, depth: LanguageInfo.DetectionRecurseLimit + 1);
        await File.WriteAllTextAsync(appHostFile.FullName, "<Project />");
        await File.WriteAllTextAsync(
            Path.Combine(workspace.WorkspaceRoot.FullName, "aspire.config.json"),
            $$"""
            {
              "appHost": {
                "path": "{{Path.GetRelativePath(workspace.WorkspaceRoot.FullName, appHostFile.FullName).Replace('\\', '/')}}"
              }
            }
            """);

        var interactionService = new TestInteractionService();
        var services = CreateDoctorVersionServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.CliUpdateNotifierFactory = _ => new TestCliUpdateNotifier();
            options.AppHostProjectFactory = _ => new TestAppHostProjectFactory
            {
                GetAspireHostingVersionAsyncCallback = (_, _) => Task.FromResult<string?>("13.2.0")
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse("doctor --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        var (json, _) = Assert.Single(interactionService.DisplayedRawText);
        using var document = JsonDocument.Parse(json);
        var appHostVersionCheck = document.RootElement.GetProperty("checks").EnumerateArray()
            .Single(check => check.GetProperty("name").GetString() == "apphost-version");

        Assert.Equal("apphost", appHostVersionCheck.GetProperty("category").GetString());
        Assert.Equal("pass", appHostVersionCheck.GetProperty("status").GetString());
        Assert.Contains("13.2.0", appHostVersionCheck.GetProperty("message").GetString()!);
        Assert.Contains("AppHost.csproj", appHostVersionCheck.GetProperty("message").GetString()!);
        var appHostVersionMetadata = appHostVersionCheck.GetProperty("metadata");
        Assert.Equal("13.2.0", appHostVersionMetadata.GetProperty("version").GetString());
        Assert.Equal(
            Path.Combine("level0", "level1", "level2", "level3", "level4", "level5", "AppHost.csproj"),
            appHostVersionMetadata.GetProperty("appHostPath").GetString());
    }

    private static IServiceCollection CreateDoctorVersionServiceCollection(
        TemporaryWorkspace workspace,
        ITestOutputHelper outputHelper,
        Action<CliServiceCollectionTestOptions>? configure)
    {
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, configure);
        services.RemoveAll<IEnvironmentCheck>();
        services.AddSingleton<IEnvironmentCheck, AspireVersionCheck>();
        return services;
    }

    private static FileInfo CreateDeepAppHostFile(TemporaryWorkspace workspace, int depth)
    {
        var directory = workspace.WorkspaceRoot;
        for (var i = 0; i < depth; i++)
        {
            directory = directory.CreateSubdirectory($"level{i}");
        }

        return new FileInfo(Path.Combine(directory.FullName, "AppHost.csproj"));
    }
}
