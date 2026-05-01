// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Cli.Agents;
using Aspire.Cli.Commands;
using Aspire.Cli.Configuration;
using Aspire.Cli.NuGet;
using Aspire.Cli.Packaging;
using Aspire.Cli.Projects;
using Aspire.Cli.Scaffolding;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests.Commands;

public class InitCommandTests(ITestOutputHelper outputHelper)
{
    /// <summary>
    /// Configures the test packaging service factory to return a single implicit channel
    /// whose template package cache yields one Aspire.ProjectTemplates entry. Init's project-mode path needs this
    /// to resolve a template version before invoking dotnet new install.
    /// </summary>
    private static void ConfigureImplicitTemplateChannel(CliServiceCollectionTestOptions options, string version = "13.3.0")
    {
        options.PackagingServiceFactory = _ =>
        {
            var fakeCache = new FakeNuGetPackageCache
            {
                GetTemplatePackagesAsyncCallback = (_, _, _, _) =>
                    Task.FromResult<IEnumerable<NuGetPackageCli>>(
                        [new NuGetPackageCli { Id = "Aspire.ProjectTemplates", Source = "nuget.org", Version = version }])
            };

            var implicitChannel = PackageChannel.CreateImplicitChannel(fakeCache);

            var packagingService = new TestPackagingService
            {
                GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([implicitChannel])
            };

            return packagingService;
        };
    }

    [Theory]
    [InlineData("Test.csproj")]
    [InlineData("Test.fsproj")]
    [InlineData("Test.vbproj")]
    public async Task InitCommand_WhenSolutionAndProjectInSameDirectory_CreatesProjectModeAppHost(string projectFileName)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var solutionFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "Test.sln"));
        File.WriteAllText(solutionFile.FullName, "Fake solution file");

        var projectFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, projectFileName));
        File.WriteAllText(projectFile.FullName, "<Project />");

        string? capturedTemplateName = null;
        string? capturedName = null;
        string? capturedOutputPath = null;

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            ConfigureImplicitTemplateChannel(options);
            options.DotNetCliRunnerFactory = _ =>
            {
                var runner = new TestDotNetCliRunner();
                runner.GetSolutionProjectsAsyncCallback = (_, _, _) =>
                {
                    throw new InvalidOperationException("GetSolutionProjectsAsync should not be called by init.");
                };
                runner.NewProjectAsyncCallback = (templateName, name, outputPath, _, _) =>
                {
                    capturedTemplateName = templateName;
                    capturedName = name;
                    capturedOutputPath = outputPath;
                    // Simulate template creating the directory
                    Directory.CreateDirectory(outputPath);
                    return 0;
                };
                return runner;
            };
        });

        var serviceProvider = services.BuildServiceProvider();
        var initCommand = serviceProvider.GetRequiredService<InitCommand>();

        var parseResult = initCommand.Parse("init");
        var exitCode = await parseResult.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.Equal("aspire-apphost", capturedTemplateName);
        Assert.Equal("Test.AppHost", capturedName);
        Assert.Contains("Test.AppHost", capturedOutputPath);
        Assert.False(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, "aspire.config.json")));
    }

    [Fact]
    public async Task InitCommand_WhenSolutionDirectoryHasNoProjectFiles_CreatesProjectModeAppHost()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var solutionFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "Test.sln"));
        File.WriteAllText(solutionFile.FullName, "Fake solution file");

        string? capturedTemplateName = null;

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            ConfigureImplicitTemplateChannel(options);
            options.DotNetCliRunnerFactory = _ =>
            {
                var runner = new TestDotNetCliRunner();
                runner.GetSolutionProjectsAsyncCallback = (_, _, _) =>
                {
                    throw new InvalidOperationException("GetSolutionProjectsAsync should not be called by init.");
                };
                runner.NewProjectAsyncCallback = (templateName, name, outputPath, _, _) =>
                {
                    capturedTemplateName = templateName;
                    Directory.CreateDirectory(outputPath);
                    return 0;
                };
                return runner;
            };
        });

        var serviceProvider = services.BuildServiceProvider();
        var initCommand = serviceProvider.GetRequiredService<InitCommand>();

        var parseResult = initCommand.Parse("init");
        var exitCode = await parseResult.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.Equal("aspire-apphost", capturedTemplateName);
        Assert.False(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, "aspire.config.json")));
    }

    [Fact]
    public async Task InitCommand_WhenNoSolutionExists_CreatesSingleFileAppHostAndAspireConfig()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var serviceProvider = services.BuildServiceProvider();
        var initCommand = serviceProvider.GetRequiredService<InitCommand>();

        var parseResult = initCommand.Parse("init");
        var exitCode = await parseResult.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.True(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.cs")));

        var config = JsonNode.Parse(File.ReadAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "aspire.config.json")))!.AsObject();
        var appHost = config["appHost"]!.AsObject();
        Assert.Equal("apphost.cs", appHost["path"]!.GetValue<string>());
        Assert.Null(appHost["language"]);
    }

    [Fact]
    public async Task InitCommand_WhenDeprecatedCompatibilityOptionsProvided_SucceedsAndWarns()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var interactionService = new TestInteractionService();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
        });
        var serviceProvider = services.BuildServiceProvider();
        var initCommand = serviceProvider.GetRequiredService<InitCommand>();

        Assert.True(initCommand.Options.Single(o => o.Name == "--source").Hidden);
        Assert.True(initCommand.Options.Single(o => o.Name == "--version").Hidden);
        Assert.True(initCommand.Options.Single(o => o.Name == "--channel").Hidden);

        var parseResult = initCommand.Parse("init --source https://example.test/v3/index.json --version 13.0.0 --channel daily --suppress-agent-init");
        var exitCode = await parseResult.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.True(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.cs")));
        Assert.Contains(interactionService.DisplayedMessages, m => m.Message.Contains("`aspire init --source` is deprecated", StringComparison.Ordinal));
        Assert.Contains(interactionService.DisplayedMessages, m => m.Message.Contains("`aspire init --version` is deprecated", StringComparison.Ordinal));
        Assert.Contains(interactionService.DisplayedMessages, m => m.Message.Contains("`aspire init --channel` is deprecated", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InitCommand_WhenTypeScriptSelected_CreatesAppHostAndAspireConfig()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.LanguageServiceFactory = (sp) =>
            {
                var projectFactory = sp.GetRequiredService<IAppHostProjectFactory>();
                var tsProject = projectFactory.GetProject(new LanguageInfo(
                    LanguageId: new LanguageId(KnownLanguageId.TypeScript),
                    DisplayName: "TypeScript (Node.js)",
                    PackageName: "@aspire/app-host",
                    DetectionPatterns: ["apphost.ts"],
                    CodeGenerator: "typescript",
                    AppHostFileName: "apphost.ts"));
                return new TestLanguageService { DefaultProject = tsProject };
            };
            options.ScaffoldingServiceFactory = _ => new TestScaffoldingService();
        });

        var serviceProvider = services.BuildServiceProvider();
        var initCommand = serviceProvider.GetRequiredService<InitCommand>();

        var parseResult = initCommand.Parse("init");
        var exitCode = await parseResult.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.True(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.ts")));

        var config = JsonNode.Parse(File.ReadAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "aspire.config.json")))!.AsObject();
        var appHost = config["appHost"]!.AsObject();
        Assert.Equal("apphost.ts", appHost["path"]!.GetValue<string>());
        Assert.Equal("typescript/nodejs", appHost["language"]!.GetValue<string>());
    }

    [Fact]
    public async Task InitCommand_WhenAspireifySkillSelected_PrintsToolSpecificFollowUpCommands()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var interactionService = new TestInteractionService
        {
            ConfirmCallback = (_, _) => true
        };

        var subtleMessages = new List<string>();
        interactionService.DisplaySubtleMessageCallback = subtleMessages.Add;
        interactionService.PromptForSelectionsCallback = (_, choices, _, _) =>
        {
            var items = choices.Cast<object>().ToList();

            if (items.FirstOrDefault() is SkillLocation)
            {
                return [SkillLocation.Standard, SkillLocation.ClaudeCode, SkillLocation.OpenCode];
            }

            return [SkillDefinition.Aspireify];
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.CliHostEnvironmentFactory = _ => global::Aspire.Cli.Tests.TestHelpers.CreateInteractiveHostEnvironment();
            options.ScaffoldingServiceFactory = _ => new TestScaffoldingService();
        });

        var serviceProvider = services.BuildServiceProvider();
        var initCommand = serviceProvider.GetRequiredService<InitCommand>();

        var parseResult = initCommand.Parse("init --language typescript");
        var exitCode = await parseResult.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.Contains(interactionService.DisplayedMessages, m => m.Message == "Aspire AppHost created! To complete setup, run one of:");
        Assert.DoesNotContain(subtleMessages, m => m.Contains("copilot", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("  claude \"run the aspireify skill\"", subtleMessages);
        Assert.Contains("  opencode --prompt \"run the aspireify skill\"", subtleMessages);
    }

    [Fact]
    public async Task InitCommand_WhenAspireifySkillNotSelected_DoesNotPrintFollowUpCommands()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var interactionService = new TestInteractionService
        {
            ConfirmCallback = (_, _) => true
        };

        var subtleMessages = new List<string>();
        interactionService.DisplaySubtleMessageCallback = subtleMessages.Add;
        interactionService.PromptForSelectionsCallback = (_, choices, _, _) =>
        {
            var items = choices.Cast<object>().ToList();

            if (items.FirstOrDefault() is SkillLocation)
            {
                return [SkillLocation.Standard];
            }

            return [SkillDefinition.Aspire];
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.CliHostEnvironmentFactory = _ => global::Aspire.Cli.Tests.TestHelpers.CreateInteractiveHostEnvironment();
            options.ScaffoldingServiceFactory = _ => new TestScaffoldingService();
        });

        var serviceProvider = services.BuildServiceProvider();
        var initCommand = serviceProvider.GetRequiredService<InitCommand>();

        var parseResult = initCommand.Parse("init --language typescript");
        var exitCode = await parseResult.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.DoesNotContain(interactionService.DisplayedMessages, m => m.Message.Contains("To complete setup", StringComparison.Ordinal));
        Assert.DoesNotContain(subtleMessages, m => m.Contains("run the aspireify skill", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InitCommand_WhenNoSolutionExists_SingleFileSkeletonPinsSdkVersion()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var serviceProvider = services.BuildServiceProvider();
        var initCommand = serviceProvider.GetRequiredService<InitCommand>();

        var parseResult = initCommand.Parse("init");
        var exitCode = await parseResult.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        var appHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.cs");
        var appHostContent = await File.ReadAllTextAsync(appHostPath);

        // The single-file skeleton must pin the SDK version with @<version> so downstream
        // CLI operations (ProjectUpdater / FallbackProjectParser) can locate and update
        // the directive.
        var firstLine = appHostContent.Split('\n')[0].TrimEnd('\r');
        Assert.StartsWith("#:sdk Aspire.AppHost.Sdk@", firstLine, StringComparison.Ordinal);
        Assert.NotEqual("#:sdk Aspire.AppHost.Sdk@", firstLine);
    }

    [Fact]
    public async Task InitCommand_WhenAspireConfigAlreadyExists_MergesAppHostSection()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Pre-write an aspire.config.json with custom properties a user might have edited in.
        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        var existingConfig = new JsonObject
        {
            ["channel"] = "stable",
            ["features"] = new JsonObject { ["polyglotSupportEnabled"] = true }
        };
        await File.WriteAllTextAsync(configPath, existingConfig.ToJsonString());

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var serviceProvider = services.BuildServiceProvider();
        var initCommand = serviceProvider.GetRequiredService<InitCommand>();

        var parseResult = initCommand.Parse("init");
        var exitCode = await parseResult.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        var merged = JsonNode.Parse(await File.ReadAllTextAsync(configPath))!.AsObject();
        Assert.Equal("apphost.cs", merged["appHost"]!.AsObject()["path"]!.GetValue<string>());
        // Pre-existing properties must be preserved.
        Assert.Equal("stable", merged["channel"]!.GetValue<string>());
        Assert.True(merged["features"]!.AsObject()["polyglotSupportEnabled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task InitCommand_WhenAspireConfigIsMalformed_FailsCleanly()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Write a malformed aspire.config.json. The CLI configuration layer
        // (ConfigurationHelper.AddSettingsFile) should surface a friendly
        // InvalidOperationException identifying the offending file rather than a raw
        // JsonReaderException stack trace.
        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, AspireConfigFile.FileName);
        await File.WriteAllTextAsync(configPath, "{ this is not json");

        var ex = Assert.ThrowsAny<InvalidOperationException>(() =>
        {
            var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
            using var serviceProvider = services.BuildServiceProvider();
            return serviceProvider.GetRequiredService<InitCommand>();
        });

        Assert.Contains(AspireConfigFile.FileName, ex.Message, StringComparison.Ordinal);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task InitCommand_WhenAppHostAlreadyExists_DoesNotOverwriteIt()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var appHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.cs");
        const string preExistingContent = "// user-authored apphost\n";
        await File.WriteAllTextAsync(appHostPath, preExistingContent);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var serviceProvider = services.BuildServiceProvider();
        var initCommand = serviceProvider.GetRequiredService<InitCommand>();

        var parseResult = initCommand.Parse("init");
        var exitCode = await parseResult.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.Equal(preExistingContent, await File.ReadAllTextAsync(appHostPath));
    }

    [Fact]
    public async Task InitCommand_WhenSolutionExistsAndChannelIsExplicit_PassesTemporaryNuGetConfigToTemplateInstall()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var solutionFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "Test.sln"));
        File.WriteAllText(solutionFile.FullName, "Fake solution file");

        FileInfo? capturedNuGetConfigFile = null;
        string? capturedNuGetSource = null;
        string? capturedTemplateNuGetConfigContents = null;

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            // Match the bug repro: user has a global `channel` setting pointing at an explicit channel.
            options.ConfigurationServiceFactory = _ => new FakeConfigurationServiceWithChannel("staging");

            options.PackagingServiceFactory = _ =>
            {
                var fakeCache = new FakeNuGetPackageCache
                {
                    GetTemplatePackagesAsyncCallback = (_, _, _, _) =>
                        Task.FromResult<IEnumerable<NuGetPackageCli>>(
                            [new NuGetPackageCli { Id = "Aspire.ProjectTemplates", Source = "https://example.test/staging/v3/index.json", Version = "13.3.0" }])
                };

                var explicitChannel = PackageChannel.CreateExplicitChannel(
                    "staging",
                    PackageChannelQuality.Both,
                    [new PackageMapping("Aspire*", "https://example.test/staging/v3/index.json")],
                    fakeCache);

                return new TestPackagingService
                {
                    GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([explicitChannel])
                };
            };

            options.DotNetCliRunnerFactory = _ =>
            {
                var runner = new TestDotNetCliRunner();
                runner.InstallTemplateAsyncCallback = (_, version, nugetConfigFile, nugetSource, _, _, _) =>
                {
                    capturedNuGetConfigFile = nugetConfigFile;
                    capturedNuGetSource = nugetSource;
                    if (nugetConfigFile is not null && File.Exists(nugetConfigFile.FullName))
                    {
                        capturedTemplateNuGetConfigContents = File.ReadAllText(nugetConfigFile.FullName);
                    }
                    return (0, version);
                };
                runner.NewProjectAsyncCallback = (_, _, outputPath, _, _) =>
                {
                    Directory.CreateDirectory(outputPath);
                    return 0;
                };
                return runner;
            };
        });

        var serviceProvider = services.BuildServiceProvider();
        var initCommand = serviceProvider.GetRequiredService<InitCommand>();

        var parseResult = initCommand.Parse("init");
        var exitCode = await parseResult.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.NotNull(capturedNuGetConfigFile);
        Assert.Equal("https://example.test/staging/v3/index.json", capturedNuGetSource);
        Assert.NotNull(capturedTemplateNuGetConfigContents);
        Assert.Contains("https://example.test/staging/v3/index.json", capturedTemplateNuGetConfigContents);
    }

    [Fact]
    public async Task InitCommand_WhenSolutionExistsAndChannelIsImplicit_LeavesNuGetConfigNull()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var solutionFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "Test.sln"));
        File.WriteAllText(solutionFile.FullName, "Fake solution file");

        FileInfo? capturedNuGetConfigFile = new FileInfo("sentinel");
        string? capturedNuGetSource = "sentinel";

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            ConfigureImplicitTemplateChannel(options);
            options.DotNetCliRunnerFactory = _ =>
            {
                var runner = new TestDotNetCliRunner();
                runner.InstallTemplateAsyncCallback = (_, version, nugetConfigFile, nugetSource, _, _, _) =>
                {
                    capturedNuGetConfigFile = nugetConfigFile;
                    capturedNuGetSource = nugetSource;
                    return (0, version);
                };
                runner.NewProjectAsyncCallback = (_, _, outputPath, _, _) =>
                {
                    Directory.CreateDirectory(outputPath);
                    return 0;
                };
                return runner;
            };
        });

        var serviceProvider = services.BuildServiceProvider();
        var initCommand = serviceProvider.GetRequiredService<InitCommand>();

        var parseResult = initCommand.Parse("init");
        var exitCode = await parseResult.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.Null(capturedNuGetConfigFile);
        // The implicit channel surfaces the package's Source field as the nugetSource even when no
        // temporary config is generated, so nugetSource may be non-null. The contract this test guards
        // is that nugetConfigFile stays null on the implicit channel.
    }

    [Fact]
    public async Task InitCommand_WhenSolutionExistsAndPrHivesPresent_DoesNotWidenToAllChannels()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var solutionFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "Test.sln"));
        File.WriteAllText(solutionFile.FullName, "Fake solution file");

        // Simulate a stale PR hive on disk so executionContext.GetPrHiveCount() returns > 0.
        var hivesDir = new DirectoryInfo(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "hives"));
        Directory.CreateDirectory(Path.Combine(hivesDir.FullName, "pr-12345", "packages"));

        string? capturedTemplateVersion = null;

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.PackagingServiceFactory = _ =>
            {
                // Implicit channel offers the expected stable version; PR hive channel offers a much
                // newer version that should NOT be selected because init opts out of PR-hive widening.
                var implicitCache = new FakeNuGetPackageCache
                {
                    GetTemplatePackagesAsyncCallback = (_, _, _, _) =>
                        Task.FromResult<IEnumerable<NuGetPackageCli>>(
                            [new NuGetPackageCli { Id = "Aspire.ProjectTemplates", Source = "nuget.org", Version = "13.3.0" }])
                };
                var prHiveCache = new FakeNuGetPackageCache
                {
                    GetTemplatePackagesAsyncCallback = (_, _, _, _) =>
                        Task.FromResult<IEnumerable<NuGetPackageCli>>(
                            [new NuGetPackageCli { Id = "Aspire.ProjectTemplates", Source = "pr-hive", Version = "99.0.0-pr.12345" }])
                };

                var implicitChannel = PackageChannel.CreateImplicitChannel(implicitCache);
                var prHiveChannel = PackageChannel.CreateExplicitChannel(
                    "pr-12345",
                    PackageChannelQuality.Both,
                    [new PackageMapping("Aspire*", hivesDir.FullName + "/pr-12345/packages")],
                    prHiveCache);

                return new TestPackagingService
                {
                    GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([implicitChannel, prHiveChannel])
                };
            };

            options.DotNetCliRunnerFactory = _ =>
            {
                var runner = new TestDotNetCliRunner();
                runner.InstallTemplateAsyncCallback = (_, version, _, _, _, _, _) =>
                {
                    capturedTemplateVersion = version;
                    return (0, version);
                };
                runner.NewProjectAsyncCallback = (_, _, outputPath, _, _) =>
                {
                    Directory.CreateDirectory(outputPath);
                    return 0;
                };
                return runner;
            };
        });

        var serviceProvider = services.BuildServiceProvider();
        var initCommand = serviceProvider.GetRequiredService<InitCommand>();

        var parseResult = initCommand.Parse("init");
        var exitCode = await parseResult.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.Equal("13.3.0", capturedTemplateVersion);
    }

    [Fact]
    public async Task InitCommand_WhenChannelResolutionThrowsChannelNotFound_DisplaysFriendlyError()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var solutionFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "Test.sln"));
        File.WriteAllText(solutionFile.FullName, "Fake solution file");

        var interactionService = new TestInteractionService();

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;

            // Configure global channel = "missing-channel" so the resolver looks for a channel that doesn't exist.
            options.ConfigurationServiceFactory = _ => new FakeConfigurationServiceWithChannel("missing-channel");

            // Return only an implicit/default channel so the lookup fails to match "missing-channel".
            options.PackagingServiceFactory = _ =>
            {
                var fakeCache = new FakeNuGetPackageCache
                {
                    GetTemplatePackagesAsyncCallback = (_, _, _, _) =>
                        Task.FromResult<IEnumerable<NuGetPackageCli>>(
                            [new NuGetPackageCli { Id = "Aspire.ProjectTemplates", Source = "nuget.org", Version = "13.3.0" }])
                };
                var implicitChannel = PackageChannel.CreateImplicitChannel(fakeCache);
                return new TestPackagingService
                {
                    GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([implicitChannel])
                };
            };

            options.DotNetCliRunnerFactory = _ =>
            {
                var runner = new TestDotNetCliRunner();
                runner.InstallTemplateAsyncCallback = (_, _, _, _, _, _, _) =>
                {
                    throw new InvalidOperationException("InstallTemplateAsync should not run when channel resolution fails.");
                };
                return runner;
            };
        });

        var serviceProvider = services.BuildServiceProvider();
        var initCommand = serviceProvider.GetRequiredService<InitCommand>();

        var parseResult = initCommand.Parse("init");
        var exitCode = await parseResult.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.FailedToInstallTemplates, exitCode);
        Assert.Contains(interactionService.DisplayedErrors, e => e.Contains("missing-channel", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InitCommand_WhenChannelTemplateSearchFails_DisplaysFriendlyError()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var solutionFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "Test.sln"));
        File.WriteAllText(solutionFile.FullName, "Fake solution file");

        var interactionService = new TestInteractionService();

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;

            // Fake cache throws NuGetPackageCacheException to simulate offline / inaccessible feed.
            options.PackagingServiceFactory = _ =>
            {
                var fakeCache = new FakeNuGetPackageCache
                {
                    GetTemplatePackagesAsyncCallback = (_, _, _, _) =>
                        throw new NuGetPackageCacheException("Package search failed: simulated network failure")
                };
                var implicitChannel = PackageChannel.CreateImplicitChannel(fakeCache);
                return new TestPackagingService
                {
                    GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([implicitChannel])
                };
            };

            options.DotNetCliRunnerFactory = _ =>
            {
                var runner = new TestDotNetCliRunner();
                runner.InstallTemplateAsyncCallback = (_, _, _, _, _, _, _) =>
                {
                    throw new InvalidOperationException("InstallTemplateAsync should not run when channel search fails.");
                };
                return runner;
            };
        });

        var serviceProvider = services.BuildServiceProvider();
        var initCommand = serviceProvider.GetRequiredService<InitCommand>();

        var parseResult = initCommand.Parse("init");
        var exitCode = await parseResult.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.FailedToInstallTemplates, exitCode);
        Assert.Contains(interactionService.DisplayedErrors, e => e.Contains("simulated network failure", StringComparison.Ordinal));
    }

    private sealed class FakeConfigurationServiceWithChannel(string channelValue) : IConfigurationService
    {
        public Task<string?> GetConfigurationAsync(string key, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(string.Equals(key, "channel", StringComparison.Ordinal) ? channelValue : null);

        public Task SetConfigurationAsync(string key, string value, bool isGlobal = false, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<bool> DeleteConfigurationAsync(string key, bool isGlobal = false, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<Dictionary<string, string>> GetAllConfigurationAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new Dictionary<string, string>());

        public Task<Dictionary<string, string>> GetLocalConfigurationAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new Dictionary<string, string>());

        public Task<Dictionary<string, string>> GetGlobalConfigurationAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new Dictionary<string, string>());

        public string GetSettingsFilePath(bool isGlobal) => string.Empty;
    }

    private sealed class TestScaffoldingService : IScaffoldingService
    {
        public Task<bool> ScaffoldAsync(ScaffoldContext context, CancellationToken cancellationToken)
        {
            var appHostFileName = context.Language.AppHostFileName ?? "apphost";
            File.WriteAllText(Path.Combine(context.TargetDirectory.FullName, appHostFileName), string.Empty);

            var config = new JsonObject
            {
                ["appHost"] = new JsonObject
                {
                    ["path"] = appHostFileName,
                    ["language"] = context.Language.LanguageId.Value
                }
            };
            File.WriteAllText(Path.Combine(context.TargetDirectory.FullName, AspireConfigFile.FileName), config.ToJsonString());

            return Task.FromResult(true);
        }
    }
}
