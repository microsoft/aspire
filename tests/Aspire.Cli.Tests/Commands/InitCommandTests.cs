// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using System.Xml.Linq;
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
    /// whose template package cache yields one Aspire.ProjectTemplates entry, and pins the
    /// running CLI's identity channel to <c>default</c> so the resolver matches that implicit
    /// channel by name. Init's project-mode path uses
    /// <see cref="CliExecutionContext.IdentityChannel"/> as the channel override; this helper keeps
    /// tests that don't care about channel selection on a single, predictable channel.
    /// </summary>
    private static void ConfigureImplicitTemplateChannel(CliServiceCollectionTestOptions options, string version = "13.3.0")
    {
        options.CliExecutionContextFactory = _ =>
            BuildExecutionContext(options.WorkingDirectory, channel: "default");

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
    public async Task InitCommand_SingleFileSkeleton_CreatesAppHostRunJsonWithDashboardEnvVars()
    {
        // Regression for https://github.com/microsoft/aspire/issues/15986: without
        // apphost.run.json, `dotnet run apphost.cs` after `aspire init` crashes because
        // the dashboard env vars (ASPNETCORE_URLS, ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL)
        // are not set. Init must emit apphost.run.json alongside aspire.config.json so
        // the file-based runner picks up a launch profile.
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var serviceProvider = services.BuildServiceProvider();
        var initCommand = serviceProvider.GetRequiredService<InitCommand>();

        var parseResult = initCommand.Parse("init");
        var exitCode = await parseResult.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        var runJsonPath = Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.run.json");
        Assert.True(File.Exists(runJsonPath), "apphost.run.json should be created so `dotnet run apphost.cs` works.");

        var runJson = JsonNode.Parse(File.ReadAllText(runJsonPath))!.AsObject();
        var profiles = runJson["profiles"]!.AsObject();

        var https = profiles["https"]!.AsObject();
        Assert.Equal("Project", https["commandName"]!.GetValue<string>());
        Assert.True(https["dotnetRunMessages"]!.GetValue<bool>());
        var httpsUrls = https["applicationUrl"]!.GetValue<string>();
        Assert.StartsWith("https://localhost:", httpsUrls);
        var httpsEnv = https["environmentVariables"]!.AsObject();
        Assert.Equal("Development", httpsEnv["ASPNETCORE_ENVIRONMENT"]!.GetValue<string>());
        Assert.Equal("Development", httpsEnv["DOTNET_ENVIRONMENT"]!.GetValue<string>());
        Assert.StartsWith("https://localhost:", httpsEnv["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"]!.GetValue<string>());
        Assert.StartsWith("https://localhost:", httpsEnv["ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL"]!.GetValue<string>());

        var http = profiles["http"]!.AsObject();
        Assert.Equal("Project", http["commandName"]!.GetValue<string>());
        var httpEnv = http["environmentVariables"]!.AsObject();
        Assert.Equal("Development", httpEnv["ASPNETCORE_ENVIRONMENT"]!.GetValue<string>());
        Assert.StartsWith("http://localhost:", httpEnv["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"]!.GetValue<string>());
        Assert.StartsWith("http://localhost:", httpEnv["ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL"]!.GetValue<string>());
        Assert.Equal("true", httpEnv["ASPIRE_ALLOW_UNSECURED_TRANSPORT"]!.GetValue<string>());

        // The two files must agree on ports — otherwise `aspire run` and
        // `dotnet run apphost.cs` would bind to different dashboard URLs.
        var aspireConfig = JsonNode.Parse(File.ReadAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "aspire.config.json")))!.AsObject();
        var aspireProfiles = aspireConfig["profiles"]!.AsObject();
        Assert.Equal(
            aspireProfiles["https"]!["applicationUrl"]!.GetValue<string>(),
            httpsUrls);
        Assert.Equal(
            aspireProfiles["http"]!["applicationUrl"]!.GetValue<string>(),
            http["applicationUrl"]!.GetValue<string>());
    }

    [Fact]
    public async Task InitCommand_SingleFileSkeleton_AppHostRunJsonAdoptsPortsFromExistingAspireConfig()
    {
        // If aspire.config.json already exists with a `profiles` section (e.g. user
        // re-ran `aspire init` after editing it, or copied a stale file in), the new
        // apphost.run.json must adopt those same ports — the two files should never
        // disagree on dashboard / OTLP / resource service endpoints.
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        const string existingAspireConfig = """
            {
              "appHost": {
                "path": "apphost.cs"
              },
              "profiles": {
                "https": {
                  "applicationUrl": "https://localhost:18000;http://localhost:18001",
                  "environmentVariables": {
                    "ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL": "https://localhost:18002",
                    "ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL": "https://localhost:18003"
                  }
                },
                "http": {
                  "applicationUrl": "http://localhost:18001",
                  "environmentVariables": {
                    "ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL": "http://localhost:18005",
                    "ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL": "http://localhost:18006",
                    "ASPIRE_ALLOW_UNSECURED_TRANSPORT": "true"
                  }
                }
              }
            }
            """;
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "aspire.config.json"), existingAspireConfig);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var serviceProvider = services.BuildServiceProvider();
        var initCommand = serviceProvider.GetRequiredService<InitCommand>();

        var parseResult = initCommand.Parse("init");
        var exitCode = await parseResult.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        var runJson = JsonNode.Parse(File.ReadAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.run.json")))!.AsObject();
        var profiles = runJson["profiles"]!.AsObject();
        var https = profiles["https"]!.AsObject();
        var http = profiles["http"]!.AsObject();

        Assert.Equal("https://localhost:18000;http://localhost:18001", https["applicationUrl"]!.GetValue<string>());
        Assert.Equal("https://localhost:18002", https["environmentVariables"]!["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"]!.GetValue<string>());
        Assert.Equal("https://localhost:18003", https["environmentVariables"]!["ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL"]!.GetValue<string>());
        Assert.Equal("http://localhost:18001", http["applicationUrl"]!.GetValue<string>());
        Assert.Equal("http://localhost:18005", http["environmentVariables"]!["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"]!.GetValue<string>());
        Assert.Equal("http://localhost:18006", http["environmentVariables"]!["ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL"]!.GetValue<string>());
    }

    [Fact]
    public async Task InitCommand_SingleFileSkeleton_PreservesUnparseableExistingProfiles()
    {
        // Regression guard for behavioral safety: if aspire.config.json already has a
        // `profiles` section that doesn't match the expected 6-port shape (e.g. user-customized,
        // missing one of the env vars, or an https-only setup), `aspire init` must NOT
        // overwrite those profiles. The user has clearly customized their config and we
        // shouldn't trash their data — even at the cost of apphost.run.json potentially
        // binding to different dashboard ports.
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        const string customAspireConfig = """
            {
              "appHost": {
                "path": "apphost.cs"
              },
              "profiles": {
                "https": {
                  "applicationUrl": "https://localhost:18000",
                  "environmentVariables": {
                    "MY_CUSTOM_VAR": "custom-value"
                  }
                }
              }
            }
            """;
        var aspireConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, "aspire.config.json");
        File.WriteAllText(aspireConfigPath, customAspireConfig);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var serviceProvider = services.BuildServiceProvider();
        var initCommand = serviceProvider.GetRequiredService<InitCommand>();

        var parseResult = initCommand.Parse("init");
        var exitCode = await parseResult.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        // The user's customizations must be preserved verbatim — only the appHost.path
        // is allowed to be touched (since that's the primary purpose of DropAspireConfig).
        var aspireConfig = JsonNode.Parse(File.ReadAllText(aspireConfigPath))!.AsObject();
        var preservedProfiles = aspireConfig["profiles"]!.AsObject();
        Assert.False(preservedProfiles.ContainsKey("http"), "http profile should NOT have been added.");
        var preservedHttps = preservedProfiles["https"]!.AsObject();
        Assert.Equal("https://localhost:18000", preservedHttps["applicationUrl"]!.GetValue<string>());
        var preservedEnv = preservedHttps["environmentVariables"]!.AsObject();
        Assert.Equal("custom-value", preservedEnv["MY_CUSTOM_VAR"]!.GetValue<string>());
        Assert.False(preservedEnv.ContainsKey("ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"), "OTLP env var should NOT have been added to user's custom profile.");

        // apphost.run.json still gets written so `dotnet run apphost.cs` works (with fresh
        // ports — accepted divergence in this edge case).
        Assert.True(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.run.json")));
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

        // The fix must also leave the solution-directory NuGet.config alone on the implicit
        // channel — TemplateNuGetConfigService.CreateOrUpdateNuGetConfigWithoutPromptAsync
        // short-circuits when the matched channel is not Explicit. A regression that dropped
        // the channel-type guard would silently create a workspace NuGet.config here.
        Assert.False(
            File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, "nuget.config")),
            "implicit channel must not write a workspace nuget.config in the solution directory.");
    }

    [Fact]
    public async Task InitCommand_WhenSolutionExistsAndPrHivesPresent_DoesNotWidenToAllChannels()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var solutionFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "Test.sln"));
        File.WriteAllText(solutionFile.FullName, "Fake solution file");

        // Simulate a stale PR hive on disk so executionContext.GetHiveCount() returns > 0.
        var hivesDir = new DirectoryInfo(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "hives"));
        Directory.CreateDirectory(Path.Combine(hivesDir.FullName, "pr-12345", "packages"));

        string? capturedTemplateVersion = null;

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ =>
                BuildExecutionContext(options.WorkingDirectory, channel: "default");

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
    public async Task InitCommand_WhenChannelTemplateSearchFails_DisplaysFriendlyError()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var solutionFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "Test.sln"));
        File.WriteAllText(solutionFile.FullName, "Fake solution file");

        var interactionService = new TestInteractionService();

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ =>
                BuildExecutionContext(options.WorkingDirectory, channel: "default");

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

    /// <summary>
    /// When the user does not pass <c>--channel</c>, the project-mode init path must resolve
    /// its template package against the channel baked into the running CLI binary (exposed
    /// as <see cref="CliExecutionContext.IdentityChannel"/>). One named explicit channel is registered
    /// per theory row and uniquely sourced; the assertion captures the package source seen by
    /// <c>dotnet new install</c> and verifies it came from the matching channel — proving the
    /// resolver picked the binary's identity channel rather than the implicit default or any
    /// other registered channel.
    /// </summary>
    [Theory]
    [InlineData("stable")]
    [InlineData("staging")]
    [InlineData("daily")]
    [InlineData("pr-12345")]
    public async Task InitCommand_ProjectMode_NoChannelOverride_ResolvesAgainstCliExecutionContextChannel(string contextChannel)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var solutionFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "Test.sln"));
        File.WriteAllText(solutionFile.FullName, "Fake solution file");

        string? capturedNuGetSource = null;

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => CreateExecutionContextForChannel(workspace.WorkspaceRoot, contextChannel);
            options.PackagingServiceFactory = _ => CreateNamedChannelPackagingService(contextChannel);

            options.DotNetCliRunnerFactory = _ =>
            {
                var runner = new TestDotNetCliRunner();
                runner.InstallTemplateAsyncCallback = (_, version, _, nugetSource, _, _, _) =>
                {
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
        Assert.Equal(SourceForChannel(contextChannel), capturedNuGetSource);
    }

    /// <summary>
    /// Exercises the full produce → bake → resolve pipeline for PR builds: a CLI binary built
    /// with identity channel <c>pr</c> and PR number 12345 must resolve its init-time template
    /// against the <c>pr-12345</c> hive. This is a joint-contract test — per-layer unit tests
    /// can pass while the producer emits <c>pr</c>, the consumer expects <c>pr-12345</c>, and
    /// only an end-to-end run catches the mismatch.
    /// </summary>
    [Fact]
    public async Task InitCommand_ProjectMode_PrBuildResolvesToPrNumberedHive()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var solutionFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "Test.sln"));
        File.WriteAllText(solutionFile.FullName, "Fake solution file");

        string? capturedNuGetSource = null;

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => BuildExecutionContext(workspace.WorkspaceRoot, channel: "pr-12345");
            options.PackagingServiceFactory = _ => CreateNamedChannelPackagingService("pr-12345");

            options.DotNetCliRunnerFactory = _ =>
            {
                var runner = new TestDotNetCliRunner();
                runner.InstallTemplateAsyncCallback = (_, version, _, nugetSource, _, _, _) =>
                {
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
        Assert.Equal(SourceForChannel("pr-12345"), capturedNuGetSource);
    }

    /// <summary>
    /// When the user does not pass <c>--channel</c>, the single-file init path must wire the
    /// workspace <c>nuget.config</c> to the channel baked into the running CLI binary
    /// (exposed as <see cref="CliExecutionContext.IdentityChannel"/>). One named explicit channel is
    /// registered per theory row with a uniquely-sourced feed; the assertion reads the
    /// workspace <c>nuget.config</c> emitted by <c>NuGetConfigMerger</c> and verifies it
    /// carries the matching feed URL — proving the resolver picked the binary's identity
    /// channel rather than skipping the merge or selecting a different registered channel.
    /// </summary>
    [Theory]
    [InlineData("stable")]
    [InlineData("staging")]
    [InlineData("daily")]
    [InlineData("pr-12345")]
    public async Task InitCommand_SingleFileMode_NoChannelOverride_WiresNuGetConfigToCliExecutionContextChannel(string contextChannel)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => CreateExecutionContextForChannel(workspace.WorkspaceRoot, contextChannel);
            options.PackagingServiceFactory = _ => CreateNamedChannelPackagingService(contextChannel);
        });

        var serviceProvider = services.BuildServiceProvider();
        var initCommand = serviceProvider.GetRequiredService<InitCommand>();

        var parseResult = initCommand.Parse("init");
        var exitCode = await parseResult.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.True(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.cs")));

        var nugetConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, "nuget.config");
        Assert.True(File.Exists(nugetConfigPath), $"nuget.config should be created in workspace for channel '{contextChannel}'.");

        AssertNuGetConfigHasChannelShape(nugetConfigPath, contextChannel);
    }

    /// <summary>
    /// Negative-shape tripwire: <c>aspire init</c> must never read the <c>channel</c> key from
    /// the global <see cref="IConfigurationService"/>. The injected configuration service throws
    /// on any <c>GetConfigurationAsync(key, ...)</c> or <c>GetConfigurationFromDirectoryAsync</c>
    /// call where the key is <c>channel</c>; if init invokes either, the test fails with the
    /// thrown message. Runs in project mode (with a solution file present) so the
    /// template-package resolver is exercised.
    /// </summary>
    [Fact]
    public async Task InitCommand_DoesNotConsultGlobalConfigurationServiceForChannelKey()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var solutionFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "Test.sln"));
        File.WriteAllText(solutionFile.FullName, "Fake solution file");

        var tripwireConfigService = new global::Aspire.Cli.Tests.TestServices.TestConfigurationService
        {
            OnGetConfiguration = key =>
            {
                if (string.Equals(key, "channel", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "aspire init must not consult IConfigurationService for the 'channel' key. " +
                        "Channel resolution sources from CliExecutionContext.IdentityChannel only.");
                }
                return null;
            },
            OnGetConfigurationFromDirectory = (key, _) =>
            {
                if (string.Equals(key, "channel", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "aspire init must not consult IConfigurationService.GetConfigurationFromDirectoryAsync " +
                        "for the 'channel' key. Channel resolution sources from CliExecutionContext.IdentityChannel only.");
                }
                return null;
            }
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => CreateExecutionContextForChannel(workspace.WorkspaceRoot, "stable");
            options.PackagingServiceFactory = _ => CreateNamedChannelPackagingService("stable");
            options.ConfigurationServiceFactory = _ => tripwireConfigService;

            options.DotNetCliRunnerFactory = _ =>
            {
                var runner = new TestDotNetCliRunner();
                runner.InstallTemplateAsyncCallback = (_, version, _, _, _, _, _) => (0, version);
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
    }

    /// <summary>
    /// Fresh-machine regression. On a developer machine that has never used Aspire,
    /// <c>~/.aspire/hives/</c> does not exist (so no per-hive channels are registered).
    /// A locally-built CLI bakes <c>local</c> as its identity channel via
    /// <c>[AssemblyMetadata("AspireCliChannel", "local")]</c>, and <see cref="CliExecutionContext.IdentityChannel"/>
    /// returns that value verbatim. <c>aspire init</c> currently passes
    /// <see cref="CliExecutionContext.IdentityChannel"/> as the channel-override into
    /// <c>TemplateNuGetConfigService.ResolveTemplatePackageAsync</c>, which name-matches against
    /// the channels produced by <see cref="PackagingService.GetChannelsAsync"/>: <c>default</c>
    /// (implicit), <c>stable</c>, <c>daily</c>, optional <c>staging</c>, and one entry per hive
    /// directory. With no <c>local</c> hive on disk, the lookup would otherwise throw
    /// <see cref="Aspire.Cli.Exceptions.ChannelNotFoundException"/> and clean-machine
    /// <c>aspire init</c> would fail.
    ///
    /// Pinned behavior: when the running CLI's identity channel is <c>local</c> AND no matching
    /// named channel is registered, init falls back to the implicit channel rather than throwing.
    /// This mirrors how the <c>local</c> channel gracefully degrades to public-feed package
    /// resolution when no local hive has been scaffolded yet.
    /// </summary>
    [Fact]
    public async Task InitCommand_OnLocalChannelCli_WithNoLocalHive_FallsBackToImplicitChannel()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var solutionFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "Test.sln"));
        File.WriteAllText(solutionFile.FullName, "Fake solution file");

        // Simulate a fresh machine: hives directory does not exist, no per-hive channels.
        var hivesDir = new DirectoryInfo(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "hives"));
        Assert.False(hivesDir.Exists, "Test precondition: hives directory must not exist on a fresh machine.");

        string? capturedTemplateVersion = null;

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            // Pin the running CLI's identity channel to "local" — the value baked into a CLI
            // built without an explicit /p:AspireCliChannel= override.
            options.CliExecutionContextFactory = _ =>
                BuildExecutionContext(options.WorkingDirectory, channel: "local");

            options.PackagingServiceFactory = _ =>
            {
                // Only the implicit channel is registered — no `local` named channel, no PR hives.
                // This matches what PackagingService.GetChannelsAsync returns on a clean machine
                // for a CLI whose channel is `local` (where stable/daily/staging are also present
                // but irrelevant — they don't match the `local` channel name).
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

    /// <summary>
    /// Project-mode counterpart to
    /// <see cref="InitCommand_SingleFileMode_NoChannelOverride_WiresNuGetConfigToCliExecutionContextChannel"/>.
    /// When the user does not pass <c>--channel</c>, the project-mode init path must wire a
    /// <c>NuGet.config</c> in the solution directory to the channel baked into the running CLI
    /// binary (exposed as <see cref="CliExecutionContext.IdentityChannel"/>) BEFORE invoking
    /// <c>dotnet new aspire-apphost</c>, so the aspire-apphost template's built-in
    /// <c>restore</c> post-action (template.json, conditioned on <c>!skipRestore</c>) can
    /// resolve <c>Aspire.AppHost.Sdk/&lt;version&gt;</c> from the channel-matched hive
    /// instead of probing only the user-level feeds. Without the right ordering the
    /// post-action restore silently fails (it runs with <c>continueOnError=true</c>),
    /// wasting work and emitting confusing errors. The capture inside
    /// <c>NewProjectAsyncCallback</c> guards both the ordering and the file content; the
    /// final structural assertion guards <c>&lt;clear/&gt;</c> + <c>Aspire*</c> mapping
    /// correctness.
    /// </summary>
    [Theory]
    [InlineData("stable")]
    [InlineData("staging")]
    [InlineData("daily")]
    [InlineData("pr-12345")]
    public async Task InitCommand_ProjectMode_NoChannelOverride_WiresNuGetConfigInSolutionDirToCliExecutionContextChannel(string contextChannel)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var solutionFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "Test.sln"));
        File.WriteAllText(solutionFile.FullName, "Fake solution file");

        var nugetConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, "nuget.config");
        string? nugetConfigContentAtNewProjectTime = null;

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => CreateExecutionContextForChannel(workspace.WorkspaceRoot, contextChannel);
            options.PackagingServiceFactory = _ => CreateNamedChannelPackagingService(contextChannel);

            options.DotNetCliRunnerFactory = _ =>
            {
                var runner = new TestDotNetCliRunner();
                runner.InstallTemplateAsyncCallback = (_, version, _, _, _, _, _) => (0, version);
                runner.NewProjectAsyncCallback = (_, _, outputPath, _, _) =>
                {
                    // The aspire-apphost template's restore post-action depends on the
                    // workspace nuget.config being on disk AND containing the channel
                    // source before `dotnet new` runs; capture the actual file content
                    // (not just existence) so a regression that writes a stale file first
                    // and updates it afterward would still be caught.
                    nugetConfigContentAtNewProjectTime = File.Exists(nugetConfigPath)
                        ? File.ReadAllText(nugetConfigPath)
                        : null;
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

        Assert.True(File.Exists(nugetConfigPath), $"nuget.config should be created in the solution directory for channel '{contextChannel}'.");
        Assert.NotNull(nugetConfigContentAtNewProjectTime);
        Assert.Contains(SourceForChannel(contextChannel), nugetConfigContentAtNewProjectTime!);

        AssertNuGetConfigHasChannelShape(nugetConfigPath, contextChannel);
    }

    /// <summary>
    /// Guards the recover-on-rerun contract: a user who ran a previously broken CLI (which
    /// produced an AppHost project but did NOT write a workspace NuGet.config) reruns
    /// <c>aspire init</c> against the fixed CLI and now has a working workspace. The fix
    /// in <c>DropCSharpProjectSkeletonAsync</c> writes the workspace NuGet.config BEFORE the
    /// AppHost-dir-already-exists early return — moving the write below the guard would
    /// silently regress this scenario because the early return reports Success without
    /// running channel-aware setup.
    /// </summary>
    [Fact]
    public async Task InitCommand_ProjectMode_RerunWithExistingAppHostDirAndMissingNuGetConfig_CreatesNuGetConfig()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var solutionFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "Test.sln"));
        File.WriteAllText(solutionFile.FullName, "Fake solution file");

        // Simulate the post-broken-init starting state: AppHost dir already exists, no workspace nuget.config.
        var appHostDir = new DirectoryInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "Test.AppHost"));
        appHostDir.Create();
        Assert.False(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, "nuget.config")));

        var contextChannel = "staging";

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => CreateExecutionContextForChannel(workspace.WorkspaceRoot, contextChannel);
            options.PackagingServiceFactory = _ => CreateNamedChannelPackagingService(contextChannel);
            // No DotNetCliRunner override — InstallTemplate / NewProject must NOT be invoked
            // because the AppHost dir already exists. The default TestDotNetCliRunner throws
            // on any unhandled callback, so this also guards the contract that nothing
            // template-related runs on the rerun-recovery path.
        });

        var serviceProvider = services.BuildServiceProvider();
        var initCommand = serviceProvider.GetRequiredService<InitCommand>();

        var parseResult = initCommand.Parse("init");
        var exitCode = await parseResult.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        var nugetConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, "nuget.config");
        Assert.True(File.Exists(nugetConfigPath), "rerun must create the workspace nuget.config even though the AppHost dir already exists.");
        AssertNuGetConfigHasChannelShape(nugetConfigPath, contextChannel);
    }

    /// <summary>
    /// Single-file counterpart of
    /// <see cref="InitCommand_ProjectMode_RerunWithExistingAppHostDirAndMissingNuGetConfig_CreatesNuGetConfig"/>.
    /// When <c>apphost.cs</c> already exists from a previous broken CLI but no workspace
    /// NuGet.config was written, the rerun must still drop the NuGet.config in the working
    /// directory so the existing apphost can resolve the channel-pinned SDK.
    /// </summary>
    [Fact]
    public async Task InitCommand_SingleFileMode_RerunWithExistingAppHostFileAndMissingNuGetConfig_CreatesNuGetConfig()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // Simulate the post-broken-init starting state: apphost.cs exists, no nuget.config.
        var appHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.cs");
        File.WriteAllText(appHostPath, "#:sdk Aspire.AppHost.Sdk@13.4.0-staging.x\nbuilder.Build().Run();");
        Assert.False(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, "nuget.config")));

        var contextChannel = "staging";

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => CreateExecutionContextForChannel(workspace.WorkspaceRoot, contextChannel);
            options.PackagingServiceFactory = _ => CreateNamedChannelPackagingService(contextChannel);
        });

        var serviceProvider = services.BuildServiceProvider();
        var initCommand = serviceProvider.GetRequiredService<InitCommand>();

        var parseResult = initCommand.Parse("init");
        var exitCode = await parseResult.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        var nugetConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, "nuget.config");
        Assert.True(File.Exists(nugetConfigPath), "rerun must create the workspace nuget.config even though apphost.cs already exists.");
        AssertNuGetConfigHasChannelShape(nugetConfigPath, contextChannel);
    }

    /// <summary>
    /// When the solution file lives in a subdirectory of the CLI's working directory,
    /// <c>SolutionLocator</c> still finds it (it searches with <c>SearchOption.AllDirectories</c>),
    /// and the workspace NuGet.config must be written next to the solution — not at the
    /// working directory root. A mutation that passed <c>workingDirectory.FullName</c> instead
    /// of <c>solutionDir.FullName</c> to <c>CreateOrUpdateNuGetConfigWithoutPromptAsync</c>
    /// would put the file in the wrong place and the AppHost wouldn't resolve.
    /// </summary>
    [Fact]
    public async Task InitCommand_ProjectMode_SolutionInSubdirectory_WritesNuGetConfigNextToSolutionNotInWorkingDirectory()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var solutionDir = workspace.WorkspaceRoot.CreateSubdirectory("nested");
        var solutionFile = new FileInfo(Path.Combine(solutionDir.FullName, "Test.sln"));
        File.WriteAllText(solutionFile.FullName, "Fake solution file");

        var contextChannel = "staging";

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => CreateExecutionContextForChannel(workspace.WorkspaceRoot, contextChannel);
            options.PackagingServiceFactory = _ => CreateNamedChannelPackagingService(contextChannel);

            options.DotNetCliRunnerFactory = _ =>
            {
                var runner = new TestDotNetCliRunner();
                runner.InstallTemplateAsyncCallback = (_, version, _, _, _, _, _) => (0, version);
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
        Assert.True(File.Exists(Path.Combine(solutionDir.FullName, "nuget.config")),
            "nuget.config must be written in the solution directory, not the working directory.");
        Assert.False(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, "nuget.config")),
            "nuget.config must NOT be written in the working directory when the solution lives in a subdirectory.");
        AssertNuGetConfigHasChannelShape(Path.Combine(solutionDir.FullName, "nuget.config"), contextChannel);
    }

    /// <summary>
    /// When a NuGet.config already exists in the solution directory with the user's own
    /// package source, <c>aspire init</c> must merge the channel feed in (via
    /// <c>NuGetConfigMerger.UpdateExistingNuGetConfigAsync</c>) instead of clobbering the
    /// file. The merger has its own unit tests; this test guards the InitCommand-level
    /// integration so a regression in how the helper is invoked from the command path
    /// (wrong arg, missing call, accidental overwrite) doesn't slip through.
    /// </summary>
    [Fact]
    public async Task InitCommand_ProjectMode_WithPreExistingNuGetConfig_PreservesUserSourcesAndAddsChannelSource()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var solutionFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "Test.sln"));
        File.WriteAllText(solutionFile.FullName, "Fake solution file");

        const string userFeedUrl = "https://contoso.example.invalid/v3/index.json";
        const string nugetConfigContent = $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <add key="contoso" value="{{userFeedUrl}}" />
              </packageSources>
            </configuration>
            """;
        var nugetConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, "nuget.config");
        File.WriteAllText(nugetConfigPath, nugetConfigContent);

        var contextChannel = "staging";

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => CreateExecutionContextForChannel(workspace.WorkspaceRoot, contextChannel);
            options.PackagingServiceFactory = _ => CreateNamedChannelPackagingService(contextChannel);

            options.DotNetCliRunnerFactory = _ =>
            {
                var runner = new TestDotNetCliRunner();
                runner.InstallTemplateAsyncCallback = (_, version, _, _, _, _, _) => (0, version);
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

        var merged = File.ReadAllText(nugetConfigPath);
        Assert.Contains(userFeedUrl, merged);
        Assert.Contains(SourceForChannel(contextChannel), merged);
    }

    /// <summary>
    /// When multiple explicit channels are registered (mirroring real
    /// <c>PackagingService</c> which exposes stable + optional staging + daily + PR hives
    /// simultaneously), the helper must select the channel whose name matches
    /// <c>CliExecutionContext.IdentityChannel</c> — not just "the first explicit channel".
    /// A mutation that took the first explicit channel would silently wire a daily-channel
    /// CLI to stable sources.
    /// </summary>
    [Fact]
    public async Task InitCommand_ProjectMode_MultipleExplicitChannels_PicksChannelMatchingIdentity()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var solutionFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "Test.sln"));
        File.WriteAllText(solutionFile.FullName, "Fake solution file");

        var contextChannel = "daily";

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => CreateExecutionContextForChannel(workspace.WorkspaceRoot, contextChannel);
            // Register stable + staging + daily simultaneously. The matching channel ("daily")
            // is not the first one in the list.
            options.PackagingServiceFactory = _ => CreateMultiChannelPackagingService("stable", "staging", "daily");

            options.DotNetCliRunnerFactory = _ =>
            {
                var runner = new TestDotNetCliRunner();
                runner.InstallTemplateAsyncCallback = (_, version, _, _, _, _, _) => (0, version);
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
        var nugetConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, "nuget.config");
        var content = File.ReadAllText(nugetConfigPath);

        Assert.Contains(SourceForChannel("daily"), content);
        Assert.DoesNotContain(SourceForChannel("stable"), content);
        Assert.DoesNotContain(SourceForChannel("staging"), content);
        AssertNuGetConfigHasChannelShape(nugetConfigPath, "daily");
    }

    /// <summary>
    /// Locally-built CLI (IdentityChannel == <c>local</c>) on a machine where no <c>local</c>
    /// channel is registered (e.g. <c>~/.aspire/hives/local</c> isn't materialized). The fix
    /// helper must short-circuit (return <see langword="false"/>) instead of throwing — a
    /// regression that threw on the no-matching-channel branch would crash every locally-built
    /// CLI run. <c>DropCSharpProjectSkeletonAsync</c> additionally falls back to the implicit
    /// channel for template install via its <c>ChannelNotFoundException</c> handler, so init
    /// must still complete successfully.
    /// </summary>
    [Fact]
    public async Task InitCommand_ProjectMode_LocalIdentityChannelWithNoLocalChannelRegistered_DoesNotWriteNuGetConfig()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var solutionFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "Test.sln"));
        File.WriteAllText(solutionFile.FullName, "Fake solution file");

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ =>
                BuildExecutionContext(workspace.WorkspaceRoot, channel: PackageChannelNames.Local);

            // Packaging service exposes only the implicit channel — no "local" channel.
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
                runner.InstallTemplateAsyncCallback = (_, version, _, _, _, _, _) => (0, version);
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
        Assert.False(
            File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, "nuget.config")),
            "no workspace nuget.config should be written when no matching channel exists for IdentityChannel='local'.");
    }

    /// <summary>
    /// <c>SolutionLocator</c> handles both <c>.sln</c> and <c>.slnx</c> formats
    /// (<c>SolutionLocator.GetSolutionFilesInDirectoryAndSubfoldersAsync</c>). The
    /// project-mode theory above covers <c>.sln</c>; this guards that the <c>.slnx</c>
    /// path produces an equivalently-wired workspace NuGet.config so a future divergence
    /// (e.g. a switch on extension that skipped the nuget.config write) would be caught.
    /// </summary>
    [Fact]
    public async Task InitCommand_ProjectMode_WithSlnxSolutionFile_WiresWorkspaceNuGetConfig()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var solutionFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "Test.slnx"));
        File.WriteAllText(solutionFile.FullName, """<Solution />""");

        var contextChannel = "staging";

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => CreateExecutionContextForChannel(workspace.WorkspaceRoot, contextChannel);
            options.PackagingServiceFactory = _ => CreateNamedChannelPackagingService(contextChannel);

            options.DotNetCliRunnerFactory = _ =>
            {
                var runner = new TestDotNetCliRunner();
                runner.InstallTemplateAsyncCallback = (_, version, _, _, _, _, _) => (0, version);
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
        var nugetConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, "nuget.config");
        Assert.True(File.Exists(nugetConfigPath));
        AssertNuGetConfigHasChannelShape(nugetConfigPath, contextChannel);
    }

    private static CliExecutionContext CreateExecutionContextForChannel(DirectoryInfo workingDirectory, string contextChannel)
    {
        return BuildExecutionContext(workingDirectory, channel: contextChannel);
    }

    private static CliExecutionContext BuildExecutionContext(DirectoryInfo workingDirectory, string channel)
    {
        var hivesDirectory = new DirectoryInfo(Path.Combine(workingDirectory.FullName, ".aspire", "hives"));
        var cacheDirectory = new DirectoryInfo(Path.Combine(workingDirectory.FullName, ".aspire", "cache"));
        var sdksDirectory = new DirectoryInfo(Path.Combine(workingDirectory.FullName, ".aspire", "sdks"));
        var logsDirectory = new DirectoryInfo(Path.Combine(workingDirectory.FullName, ".aspire", "logs"));
        var logFilePath = Path.Combine(logsDirectory.FullName, "test.log");

        return new CliExecutionContext(
            workingDirectory: workingDirectory,
            hivesDirectory: hivesDirectory,
            cacheDirectory: cacheDirectory,
            sdksDirectory: sdksDirectory,
            logsDirectory: logsDirectory,
            logFilePath: logFilePath,
            identityChannel: channel);
    }

    private static TestPackagingService CreateNamedChannelPackagingService(string channelName)
        => CreateMultiChannelPackagingService(channelName);

    /// <summary>
    /// Returns a <see cref="TestPackagingService"/> exposing one named explicit channel per
    /// entry in <paramref name="channelNames"/>, each with two mappings (<c>Aspire* → channelSource</c>
    /// and <c>* → fallbackSource</c>) so consumers can assert <c>&lt;packageSourceMapping&gt;</c>
    /// shape — not just that one URL appears in the file. The real <see cref="PackagingService"/>
    /// exposes multiple explicit channels simultaneously (stable, optional staging, daily,
    /// PR hives), so registering more than one here lets a test prove the resolver picks the
    /// channel matching <see cref="CliExecutionContext.IdentityChannel"/> rather than just
    /// taking the first explicit channel.
    /// </summary>
    private static TestPackagingService CreateMultiChannelPackagingService(params string[] channelNames)
    {
        var channels = channelNames.Select(channelName =>
        {
            var channelSource = SourceForChannel(channelName);
            var fallbackSource = FallbackSourceForChannel(channelName);
            var version = "13.3.0";

            var fakeCache = new FakeNuGetPackageCache
            {
                GetTemplatePackagesAsyncCallback = (_, _, _, _) =>
                    Task.FromResult<IEnumerable<NuGetPackageCli>>(
                        [new NuGetPackageCli { Id = "Aspire.ProjectTemplates", Source = channelSource, Version = version }])
            };

            return PackageChannel.CreateExplicitChannel(
                channelName,
                PackageChannelQuality.Both,
                [new PackageMapping("Aspire*", channelSource), new PackageMapping(PackageMapping.AllPackages, fallbackSource)],
                fakeCache);
        }).ToArray();

        return new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>(channels)
        };
    }

    private static string SourceForChannel(string channelName)
        => $"https://feeds.test.invalid/{channelName}/v3/index.json";

    private static string FallbackSourceForChannel(string channelName)
        => $"https://feeds.test.invalid/{channelName}-fallback/v3/index.json";

    /// <summary>
    /// Asserts that the workspace NuGet.config written by
    /// <c>TemplateNuGetConfigService.CreateOrUpdateNuGetConfigWithoutPromptAsync</c>
    /// carries the structural elements the fix depends on: the channel feed URL is registered
    /// as a package source, a <c>&lt;clear/&gt;</c> element neutralizes any inherited
    /// parent-directory NuGet config (without which a user-level config disabling our hive
    /// would shadow the mapping), and the <c>Aspire*</c> pattern routes to the channel
    /// feed via <c>&lt;packageSourceMapping&gt;</c>.
    /// </summary>
    private static void AssertNuGetConfigHasChannelShape(string nugetConfigPath, string channelName)
    {
        var channelSource = SourceForChannel(channelName);
        var root = XDocument.Load(nugetConfigPath).Root ?? throw new InvalidOperationException("Empty NuGet.config.");

        var packageSources = root.Element("packageSources");
        Assert.NotNull(packageSources);
        Assert.NotNull(packageSources!.Element("clear"));
        Assert.Contains(packageSources.Elements("add"), e => (string?)e.Attribute("value") == channelSource);

        var packageSourceMapping = root.Element("packageSourceMapping");
        Assert.NotNull(packageSourceMapping);
        var aspirePatternSource = packageSourceMapping!.Elements("packageSource")
            .FirstOrDefault(ps => ps.Elements("package").Any(p => (string?)p.Attribute("pattern") == "Aspire*"));
        Assert.NotNull(aspirePatternSource);
        Assert.Equal(channelSource, (string?)aspirePatternSource!.Attribute("key"));
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
