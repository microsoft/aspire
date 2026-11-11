// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json.Nodes;
using Aspire.Cli.Agents;
using Aspire.Cli.Agents.AspireSkills;
using Aspire.Cli.Commands;
using Aspire.Cli.Configuration;
using Aspire.Cli.DotNet;
using Aspire.Cli.Git;
using Aspire.Cli.Interaction;
using Aspire.Cli.Npm;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.Agents;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

namespace Aspire.Cli.Tests.Commands;

public class AgentInitCommandTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData("agent init")]
    [InlineData("mcp init")]
    [InlineData("new aspire-empty")]
    [InlineData("init")]
    public void AgentInitOptions_SelectSkillsWithoutLegacySourceOption(string command)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper).BuildServiceProvider();
        var root = provider.GetRequiredService<RootCommand>();
        Assert.Empty(root.Parse(command).Errors);

        foreach (var skills in new[] { "aspire", "playwright-cli", "PLAYWRIGHT-CLI", "dotnet-inspect", "ASPIRE,aspire,Playwright-CLI", "all", "none" })
        {
            Assert.Empty(root.Parse($"{command} --skills {skills}").Errors);
        }

        foreach (var options in new[]
        {
            "--aspire-source", "--aspire-source remote", "--aspire-source embedded",
            "--skills", "--skills unsupported", "--skills aspire,none", "--skills mcp", "--skills playwright", "--skills \"\"",
            "--aspire-skills remote", "--playwright", "--dotnet-inspect"
        })
        {
            Assert.NotEmpty(root.Parse($"{command} {options}").Errors);
        }

        Assert.Equal(["all", "aspire", "dotnet-inspect", "none", "playwright-cli"],
            root.Parse($"{command} --skills ").GetCompletions().Select(item => item.Label).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AgentInit_UsesConfiguredOrSelectedAppHostSettings(bool configured)
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        var first = context.Project.CreateSubdirectory("first");
        var selected = context.Project.CreateSubdirectory("selected");
        var firstProject = new FileInfo(Path.Combine(first.FullName, "First.csproj"));
        var selectedProject = new FileInfo(Path.Combine(selected.FullName, "Selected.csproj"));
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(selected.FullName, AspireConfigFile.FileName), """
            {"skillSources":{"default":{"enabled":false},"team":{"repository":"example/selected","plugins":["deployment"]}}}
            """);
        var interaction = new TestInteractionService
        {
            PromptForSelectionCallback = (prompt, choices, _, _) =>
            {
                Assert.False(configured);
                Assert.Equal(InteractionServiceStrings.SelectAppHostToUse, prompt);
                Assert.Equal([firstProject, selectedProject], choices.Cast<FileInfo>());
                return selectedProject;
            }
        };
        var locator = new TestProjectLocator
        {
            GetAppHostFromSettingsAsyncCallback = _ => Task.FromResult<FileInfo?>(configured ? selectedProject : null),
            FindAppHostProjectFilesAsyncCallback = (directory, scope, _, skipValidation) =>
            {
                Assert.False(configured);
                Assert.True(skipValidation);
                Assert.Equal(context.Project.FullName, directory.FullName);
                Assert.Equal(AppHostDiscoveryScope.DefaultFiltered, scope);
                return Task.FromResult<List<FileInfo>>([firstProject, selectedProject]);
            }
        };
        using var provider = context.CreateServiceCollection(outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interaction;
            options.ProjectLocatorFactory = _ => locator;
        }).BuildServiceProvider();
        var result = provider.GetRequiredService<RootCommand>().Parse(
            "agent init --agents copilot --scope project --skills aspire --non-interactive");

        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());
        await Verify(await File.ReadAllTextAsync(
            context.Copilot.GetIntegrationConfigPath(AgentConfigurationScope.Project, context.Project)), "json")
            .UseParameters(configured);
    }

    [Fact]
    public async Task AgentInit_WithoutAspireSkills_DoesNotDiscoverAppHost()
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        using var provider = context.CreateServiceCollection(outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => new TestProjectLocator
            {
                GetAppHostFromSettingsAsyncCallback = _ => throw new InvalidOperationException("AppHost lookup is unnecessary without Aspire skills."),
                FindAppHostProjectFilesAsyncCallback = (_, _, _, _) => throw new InvalidOperationException("AppHost discovery is unnecessary without Aspire skills.")
            };
        }).BuildServiceProvider();
        var result = provider.GetRequiredService<RootCommand>().Parse(
            "agent init --agents copilot --skills none --mcp=false --non-interactive");

        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("9.0.302")]
    public async Task AgentInit_UsesAppHostSettingsWithoutUsableSdk(string? highestDetectedVersion)
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        var appHostDirectory = context.Project.CreateSubdirectory("app");
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(appHostDirectory.FullName, "AppHost.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <IsAspireHost>true</IsAspireHost>
              </PropertyGroup>
            </Project>
            """);
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(context.Project.FullName, "web", "Web.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk.Web" />
            """);
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(appHostDirectory.FullName, AspireConfigFile.FileName), """
            {"skillSources":{"default":{"enabled":false},"team":{"repository":"example/selected","plugins":["deployment"]}}}
            """);
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(context.Project.FullName, AspireConfigFile.FileName), """
            {"appHost":{"path":"app/AppHost.csproj"}}
            """);
        var sdkChecks = 0;
        using var provider = context.CreateServiceCollection(outputHelper, options =>
        {
            options.ProjectLocatorFactory = options.CreateDefaultProjectLocatorFactory;
            options.DotNetSdkInstallerFactory = _ => new TestDotNetSdkInstaller
            {
                CheckAsyncCallback = _ =>
                {
                    sdkChecks++;
                    return (false, highestDetectedVersion, DotNetSdkInstaller.MinimumSdkVersion);
                }
            };
        }).BuildServiceProvider();
        var locator = Assert.IsType<ProjectLocator>(provider.GetRequiredService<IProjectLocator>());
        Assert.IsType<DotNetAppHostProject>(provider.GetRequiredService<IAppHostProjectFactory>().GetProject(
            new FileInfo(Path.Combine(appHostDirectory.FullName, "AppHost.csproj"))));
        Assert.Empty(await locator.FindAppHostProjectFilesAsync(
            context.Project, AppHostDiscoveryScope.DefaultFiltered, CancellationToken.None).DefaultTimeout());
        Assert.Equal(1, sdkChecks);
        Assert.Empty(await locator.FindAppHostProjectFilesAsync(
            context.Project, AppHostDiscoveryScope.ExplicitDirectory, maxDepth: 0,
            CancellationToken.None, skipValidation: true).DefaultTimeout());
        var candidates = await locator.FindAppHostProjectFilesAsync(
            context.Project, AppHostDiscoveryScope.ExplicitDirectory, maxDepth: 1,
            CancellationToken.None, skipValidation: true).DefaultTimeout();
        Assert.Equal([Path.Combine(appHostDirectory.FullName, "AppHost.csproj")],
            candidates.Select(candidate => candidate.FullName));
        Assert.Equal(1, sdkChecks);

        var result = provider.GetRequiredService<RootCommand>().Parse(
            "agent init --agents copilot --scope project --skills aspire --non-interactive");

        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());
        Assert.Equal(1, sdkChecks);
        await Verify(await File.ReadAllTextAsync(
            context.Copilot.GetIntegrationConfigPath(AgentConfigurationScope.Project, context.Project)), "json")
            .UseParameters(highestDetectedVersion);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData(" true", true)]
    [InlineData("=TRUE", true)]
    [InlineData(" false", false)]
    [InlineData("=false", false)]
    public void McpOption_RemainsAnIndependentConfirmation(string suffix, bool expected)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper).BuildServiceProvider();
        var root = provider.GetRequiredService<RootCommand>();
        var result = root.Parse($"agent init --mcp{suffix}");

        Assert.Empty(result.Errors);
        Assert.Equal(expected, result.GetValue<bool?>("--mcp"));
        Assert.NotEmpty(root.Parse("agent init --mcp invalid").Errors);
        Assert.NotEmpty(root.Parse($"new aspire-empty --mcp{suffix}").Errors);
        Assert.NotEmpty(root.Parse($"init --mcp{suffix}").Errors);
    }

    [Theory]
    [InlineData("copilot")]
    [InlineData("claude")]
    [InlineData("opencode")]
    [InlineData("copilot,claude,opencode")]
    public async Task AgentInit_WarnsAboutSelectedClientSourcesBeforeRegistration(string agents)
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(context.Project.FullName, AspireConfigFile.FileName), """
            {
              "skillSources": {
                "default": { "openCodeV1Catalog": "https://third-party.example/skills/" },
                "team": {
                  "repository": "example/team",
                  "marketplace": "TEAM",
                  "openCodeV1Catalog": "https://team.example/skills/",
                  "openCodeV2Catalog": "https://team.example/skills/"
                },
                "pluginsOnly": { "repository": "example/plugins" },
                "teamAlias": {
                  "repository": "example/team",
                  "marketplace": "team-marketplace",
                  "ref": "source-demo",
                  "openCodeV1Catalog": "https://team.example/skills/"
                },
                "disabled": {
                  "enabled": false,
                  "repository": "example/disabled",
                  "openCodeV1Catalog": "https://disabled.example/skills/"
                }
              }
            }
            """);
        var messagesBeforeRegistration = new List<string>();
        var interaction = new TestInteractionService
        {
            DisplayMessageCallback = (emoji, message, _) =>
            {
                if (emoji.Equals(KnownEmojis.Information) || emoji.Equals(KnownEmojis.PoliceCarLight))
                {
                    Assert.All(context.Clients, client => Assert.False(File.Exists(
                        client.GetIntegrationConfigPath(AgentConfigurationScope.Project, context.Project))));
                    messagesBeforeRegistration.Add(message);
                }
            },
            PromptForSelectionCallback = (_, _, _, _) => throw new InvalidOperationException("No source or scope prompt is expected.")
        };
        using var provider = context.CreateServiceCollection(outputHelper, options =>
        {
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();
            options.InteractionServiceFactory = _ => interaction;
        }).BuildServiceProvider();
        var result = provider.GetRequiredService<RootCommand>().Parse(
            $"agent init --agents {agents} --scope project --skills aspire");

        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());
        Assert.Equal([AgentCommandStrings.InitCommand_SkillSourcesExplanation, $"[yellow]{SharedCommandStrings.SourceTrustWarning}[/]"],
            messagesBeforeRegistration);
        Assert.NotEmpty(interaction.DisplayedRenderables);
        var warning = Assert.Single(interaction.DisplayedMessages, message => message.Emoji.Equals(KnownEmojis.PoliceCarLight));
        Assert.Equal($"[yellow]{SharedCommandStrings.SourceTrustWarning}[/]", warning.Message);
        Assert.Equal(ConsoleOutput.Error, warning.ConsoleOverride);
        Assert.Equal(messagesBeforeRegistration, interaction.DisplayedMessages
            .Where(message => message.Emoji.Equals(KnownEmojis.Information) || message.Emoji.Equals(KnownEmojis.PoliceCarLight))
            .Select(message => message.Message));
        using var renderedSources = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(renderedSources),
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false }
        });
        console.Profile.Width = 160;
        foreach (var source in interaction.DisplayedRenderables)
        {
            console.Write(source);
            console.WriteLine();
        }
        await Verify(new { Sources = renderedSources.ToString(), Warnings = messagesBeforeRegistration })
            .UseParameters(agents).ScrubLinesWithReplace(line => line.TrimEnd());
    }

    [Fact]
    public async Task AgentInit_KeepsExistingCliSkillLabels()
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        await File.WriteAllTextAsync(Path.Combine(context.Project.FullName, "apphost.cs"), string.Empty);
        var labels = new List<string>();
        var interaction = new TestInteractionService
        {
            PromptForSelectionsCallback = (prompt, choices, formatter, _) =>
            {
                Assert.Equal(AgentCommandStrings.InitCommand_SelectSkills, prompt);
                labels.AddRange(choices.Cast<object>().Select(formatter));
                return [];
            },
            PromptForSelectionCallback = (_, _, _, _) => throw new InvalidOperationException("No source is needed without Aspire.")
        };
        var services = context.CreateServiceCollection(outputHelper, options =>
        {
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();
            options.InteractionServiceFactory = _ => interaction;
        });
        using var provider = services.BuildServiceProvider();
        var result = provider.GetRequiredService<RootCommand>().Parse("agent init --agents copilot --mcp=false");

        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());
        Assert.Equal(
            [$"{CommonAgentApplicators.AspireSkillName} — {AgentCommandStrings.InitCommand_AspireSkillChoice}",
                $"{SkillDefinition.PlaywrightCli.Name} — {AgentCommandStrings.SkillDescription_PlaywrightCli}",
                $"{SkillDefinition.DotnetInspect.Name} — {AgentCommandStrings.SkillDescription_DotnetInspect}"],
            labels);
        Assert.Empty(context.Home.EnumerateFileSystemInfos());
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("   ", "   ")]
    [InlineData("Short description", "Short description")]
    [InlineData("Short description.", "Short description.")]
    [InlineData("First sentence. Second sentence.", "First sentence.")]
    [InlineData("**WORKFLOW SKILL** - Top-level router for Aspire 13.4 distributed apps. Detects the AppHost.", "Top-level router for Aspire 13.4 distributed apps.")]
    [InlineData("**ANALYSIS SKILL** — Observe Aspire apps. USE FOR: aspire logs.", "Observe Aspire apps.")]
    [InlineData("**SETUP SKILL**: One-time setup of resources. INVOKES: aspire add.", "One-time setup of resources.")]
    [InlineData("Visit github.com for docs. Then run the tool.", "Visit github.com for docs.")]
    [InlineData("**TYPE** -", "")]
    // Fix 1 regression: a leading separator that does NOT follow a "**TYPE**" prefix must be preserved.
    // The earlier implementation unconditionally trimmed leading separators after the bold-prefix
    // branch, which silently mutated skill descriptions that happened to start with '-' or ':'.
    [InlineData("-Quickly do X.", "-Quickly do X.")]
    [InlineData(":memo notes", ":memo notes")]
    public void SimplifyDescription_ProducesExpectedSummary(string input, string expected)
    {
        Assert.Equal(expected, AgentInitCommand.SimplifyDescription(input));
    }

    [Fact]
    public async Task AgentInitCommand_DoesNotCheckWorkspaceSource()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await TestConfigSourceServices.SaveBindingAsync(
            new ConfigSource { Repository = "https://git.example/configuration", Path = "aspire.config.json", AppliedVersion = "version-1" },
            workspace.WorkspaceRoot, CancellationToken.None);
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper).BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>().Parse(
            $"agent init --workspace-root \"{workspace.Path}\" --agents copilot --skills aspire --scope project --mcp false --non-interactive");

        Assert.Equal(CliExitCodes.Success, await command.InvokeAsync().DefaultTimeout());

        var git = Assert.IsType<TestConfigSourceRepository>(provider.GetRequiredService<IGitClient>());
        Assert.Equal(0, git.Calls);
        Assert.Equal(0, git.CheckCalls);
    }

    [Fact]
    public async Task AgentInitCommand_NonInteractive_WithNoClients_SucceedsWithNoSkillsInstalled()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --agents none --skills all");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);

        // No clients selected, so no skill directories should be created.
        var agentsDir = Path.Combine(workspace.WorkspaceRoot.FullName, ".agents");
        Assert.False(Directory.Exists(agentsDir), $"Expected no .agents directory but found {agentsDir}");
    }

    [Fact]
    public async Task AgentInitCommand_NonInteractive_WithoutClientSelection_UsesDefaultClients()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --skills none");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
    }

    [Theory]
    [InlineData("project")]
    [InlineData("global")]
    public async Task AgentInitCommand_NonInteractive_WithoutSkills_RegistersAspireRemotely(string scope)
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        var configurationScope = Enum.Parse<AgentConfigurationScope>(scope, ignoreCase: true);

        var services = context.CreateServiceCollection(outputHelper, options =>
        {
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateNonInteractiveHostEnvironment();
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"agent init --agents all --scope {scope}");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);

        foreach (var client in provider.GetServices<IAgentClient>())
        {
            Assert.True(File.Exists(client.GetIntegrationConfigPath(configurationScope, context.Project)));
            Assert.True(client.HasIntegrationRegistration(
                context.Project, configurationScope, context.Sources));
        }
        var skillsRoot = configurationScope is AgentConfigurationScope.Project ? context.Project : context.Home;
        Assert.False(Directory.Exists(Path.Combine(skillsRoot.FullName, ".agents", "skills")));
        Assert.False(Directory.Exists(Path.Combine(skillsRoot.FullName, ".claude", "skills")));
    }

    [Fact]
    public async Task AgentInitCommand_NonInteractive_WithInvalidClient_FailsValidation()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --agents invalid --skills all");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.NotEqual(CliExitCodes.Success, exitCode);
        Assert.Single(result.Errors);
    }

    [Fact]
    public async Task AgentInitCommand_NonInteractive_WithoutWorkspaceRoot_UsesWorkingDirectory()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("agent init --agents copilot");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        var client = provider.GetServices<IAgentClient>().Single(client =>
            string.Equals(client.Id, "copilot", StringComparisons.AgentClientId));
        Assert.True(client.HasIntegrationRegistration(
            workspace.WorkspaceRoot, AgentConfigurationScope.Project, SkillSource.Resolve(provider.GetRequiredService<IConfiguration>())));
    }

    [Fact]
    public async Task PromptAndChainAsync_RegistersSourcesFromSelectedAppConfiguration()
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        var app = context.Project.CreateSubdirectory("app");
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(app.FullName, "aspire.config.json"), """
            {"skillSources":{"default":{"enabled":false},"team":{"repository":"example/team","plugins":["team"]}}}
            """);
        var interaction = new TestInteractionService();
        var services = context.CreateServiceCollection(outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interaction;
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateNonInteractiveHostEnvironment();
        });
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<AgentInitCommand>();

        var result = await command.PromptAndChainAsync(
            interaction, CliExitCodes.Success, context.Project, app,
            PromptBinding.CreateDefault(true),
            PromptBinding.CreateDefault<string?>("copilot"),
            PromptBinding.CreateDefault<string?>(CommonAgentApplicators.AspireSkillName),
            PromptBinding.CreateDefault(AgentConfigurationScope.Project),
            PromptBinding.CreateDefault(true),
            CancellationToken.None).DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        var config = await File.ReadAllTextAsync(context.Copilot.GetIntegrationConfigPath(AgentConfigurationScope.Project, context.Project));
        await Verify(config, "json");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PromptAndChainAsync_UsesFreshSelectedAppAndGlobalSources(bool sameDirectory)
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(context.Project.FullName, "aspire.config.json"), """
            {"skillSources":{"default":{"enabled":false},"old":{"repository":"example/old"}}}
            """);
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(context.Project.FullName, ".aspire", "settings.global.json"), """
            {"skillSources":{"shared":{"repository":"example/shared"},"team":{"repository":"example/global","plugins":["team"],"ref":"main"}}}
            """);
        var interaction = new TestInteractionService();
        using var provider = context.CreateServiceCollection(outputHelper, options =>
        {
            options.WorkingDirectory = context.Project;
            options.InteractionServiceFactory = _ => interaction;
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateNonInteractiveHostEnvironment();
        }).BuildServiceProvider();
        Assert.Equal("example/old", provider.GetRequiredService<IConfiguration>()["skillSources:old:repository"]);
        var app = sameDirectory ? context.Project : context.Project.CreateSubdirectory("app");
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(app.FullName, "aspire.config.json"), """
            {"skillSources":{"default":{},"team":{"repository":"example/team"}}}
            """);

        var result = await provider.GetRequiredService<AgentInitCommand>().PromptAndChainAsync(
            interaction, CliExitCodes.Success, context.Project, app,
            PromptBinding.CreateDefault(true),
            PromptBinding.CreateDefault<string?>("copilot"),
            PromptBinding.CreateDefault<string?>(CommonAgentApplicators.AspireSkillName),
            PromptBinding.CreateDefault(AgentConfigurationScope.Project),
            PromptBinding.CreateDefault(true),
            CancellationToken.None).DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        await Verify(await File.ReadAllTextAsync(
            context.Copilot.GetIntegrationConfigPath(AgentConfigurationScope.Project, context.Project)), "json")
            .UseParameters(sameDirectory);
    }

    [Fact]
    public async Task AgentInitCommand_NonInteractive_WithNoneSkills_SucceedsWithNoSkillsInstalled()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --agents all --skills none");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);

        // No skills selected, so no skill files should be created
        var aspireSkillPath = Path.Combine(workspace.WorkspaceRoot.FullName, ".agents", "skills", CommonAgentApplicators.AspireSkillName);
        Assert.False(Directory.Exists(aspireSkillPath), $"Expected no aspire skill directory but found {aspireSkillPath}");
    }

    [Fact]
    public async Task AgentInitCommand_NonInteractive_DoesNotConfigureMcpByDefault()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --agents all");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.False(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, ".mcp.json")));
        var openCodeConfig = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(workspace.WorkspaceRoot.FullName, "opencode.jsonc")));
        Assert.NotNull(openCodeConfig);
        Assert.Null(openCodeConfig["mcp"]);
    }

    [Fact]
    public async Task AgentInitCommand_NonInteractive_WithMcpTrue_ConfiguresMcp()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --agents all --mcp");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.True(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, ".mcp.json")));
        Assert.True(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, "opencode.jsonc")));
    }

    [Fact]
    public async Task AgentInitCommand_NonInteractive_WithMcpFalse_DoesNotConfigureMcp()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --agents all --mcp=false");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.False(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, ".mcp.json")));
        var openCodeConfig = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(workspace.WorkspaceRoot.FullName, "opencode.jsonc")));
        Assert.NotNull(openCodeConfig);
        Assert.Null(openCodeConfig["mcp"]);
    }

    [Fact]
    public async Task AgentInitCommand_NonInteractive_WithAgentNone_DoesNotInstallAnySkills()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --agents none --skills all");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);

        // With --agents none, no skill files should be installed even when --skills all is passed.
        var agentsDir = Path.Combine(workspace.WorkspaceRoot.FullName, ".agents", "skills");
        Assert.False(Directory.Exists(agentsDir), $"Expected no agents/skills directory but found {agentsDir}");
    }

    [Theory]
    [InlineData("apphost.cs", true)]
    [InlineData("apphost.mts", false)]
    [InlineData(null, false)]
    public async Task AgentInitCommand_DotnetInspect_RespectsWorkspaceLanguage(string? appHostFileName, bool expected)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        if (appHostFileName is not null)
        {
            await File.WriteAllTextAsync(Path.Combine(workspace.Path, appHostFileName), string.Empty);
        }
        string[] offeredSkills = [];
        var interactionService = new TestInteractionService
        {
            PromptForSelectionsCallback = (prompt, choices, _, _) =>
            {
                Assert.Equal(AgentCommandStrings.InitCommand_SelectSkills, prompt);
                offeredSkills = choices.Cast<string>().ToArray();
                return [];
            }
        };
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();
            options.InteractionServiceFactory = _ => interactionService;
        });
        services.AddSingleton<INpmRunner>(new FakeNpmRunner { IsAvailable = false });
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        Assert.Equal(CliExitCodes.Success, await command.Parse(
            "agent init --agents copilot --scope project --mcp=false").InvokeAsync().DefaultTimeout());
        Assert.Equal(expected ? ["aspire", "playwright-cli", "dotnet-inspect"] : ["aspire", "playwright-cli"], offeredSkills);

        Assert.Equal(CliExitCodes.Success, await command.Parse(
            "agent init --non-interactive --agents copilot --scope project --skills all").InvokeAsync().DefaultTimeout());
        Assert.Equal(expected, File.Exists(Path.Combine(workspace.Path, ".agents", "skills", SkillDefinition.DotnetInspect.Name, "SKILL.md")));
    }

    [Theory]
    [InlineData(nameof(AgentEnvironment.CopilotCli), "GitHub Copilot CLI")]
    [InlineData(nameof(AgentEnvironment.CopilotApp), "GitHub Copilot App")]
    public async Task AgentInitCommand_GlobalScope_InstallsTelemetryHook_ForSelectedDetectedEnvironment(string environmentName, string displayName)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var homeDirectory = workspace.CreateDirectory("fake-home");
        var interactionService = new TestInteractionService();
        var environment = Enum.Parse<AgentEnvironment>(environmentName);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => CreateExecutionContext(workspace.WorkspaceRoot, homeDirectory);
            options.AgentEnvironmentDetectorFactory = _ => new TestAgentEnvironmentDetector { DetectedEnvironments = environment };
            options.InteractionServiceFactory = _ => interactionService;
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --agents copilot --scope global --skills aspire");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        var hookFile = Path.Combine(homeDirectory.FullName, ".copilot", "hooks", "aspire-telemetry.json");
        Assert.True(File.Exists(hookFile), $"Expected telemetry hook at {hookFile}");
        Assert.Contains(
            interactionService.DisplayedMessages,
            message => message.Message.Contains(displayName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AgentInitCommand_DoesNotFail_WhenTelemetryHookConfigurationThrows()
    {
        // Hook installation is best-effort transparency tooling. A non-IO failure such as a missing
        // embedded hook script (InvalidOperationException from the installer) must not abort `agent init`.
        const string failureMessage = "simulated hook configuration failure";
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var homeDirectory = workspace.CreateDirectory("fake-home");
        var interactionService = new TestInteractionService();
        var subtleMessages = new List<string>();
        interactionService.DisplaySubtleMessageCallback = subtleMessages.Add;

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => CreateExecutionContext(workspace.WorkspaceRoot, homeDirectory);
            options.AgentEnvironmentDetectorFactory = _ => new TestAgentEnvironmentDetector { DetectedEnvironments = AgentEnvironment.CopilotCli };
            options.InteractionServiceFactory = _ => interactionService;
            options.TelemetryHookConfiguratorFactory = _ => new TestTelemetryHookConfigurator { Exception = new InvalidOperationException(failureMessage) };
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"agent init --workspace-root {workspace.WorkspaceRoot.FullName} --agents copilot --scope global --skills aspire");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Contains(failureMessage, subtleMessages);
    }

    private static CliExecutionContext CreateExecutionContext(DirectoryInfo workingDirectory, DirectoryInfo homeDirectory)
    {
        return TestExecutionContextHelper.CreateExecutionContext(
            workingDirectory,
            homeDirectory: homeDirectory);
    }

    [Theory]
    [InlineData("copilot", "project", ".github/copilot/settings.json")]
    [InlineData("copilot", "global", ".copilot/settings.json")]
    [InlineData("claude", "project", ".claude/settings.json")]
    [InlineData("claude", "global", ".claude/settings.json")]
    [InlineData("opencode", "project", "opencode.jsonc")]
    [InlineData("opencode", "global", ".config/opencode/opencode.jsonc")]
    public async Task Remote_RegistersSelectedUndetectedClientsWithoutMcp(string client, string scope, string relativePath)
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        var messages = new List<string>();
        var interaction = new TestInteractionService
        {
            DisplaySubtleMessageCallback = messages.Add,
            PromptForSelectionCallback = (_, _, _, _) => throw new InvalidOperationException("Explicit skills and scope must not prompt.")
        };
        var services = context.CreateServiceCollection(outputHelper, options =>
        {
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();
            options.InteractionServiceFactory = _ => interaction;
        });
        using var provider = services.BuildServiceProvider();

        var result = provider.GetRequiredService<RootCommand>().Parse(
            $"agent init --agents {client} --scope {scope} --skills aspire");

        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());
        var root = scope == "project" ? context.Project : context.Home;
        var otherRoot = scope == "project" ? context.Home : context.Project;
        var expectedPath = Path.Combine(root.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.Equal([expectedPath], Directory.EnumerateFiles(root.FullName, "*", SearchOption.AllDirectories));
        Assert.Empty(otherRoot.EnumerateFileSystemInfos());
        var config = JsonNode.Parse(await File.ReadAllTextAsync(expectedPath))!.AsObject();
        Assert.Equal(client == "opencode" ? ["skills"] : new[] { "enabledPlugins", "extraKnownMarketplaces" }, config.Select(property => property.Key).Order());
        Assert.Empty(messages);
        Assert.Empty(interaction.DisplayedRenderables);
        Assert.Collection(interaction.DisplayedMessages,
            message =>
            {
                Assert.Equal(KnownEmojis.Robot, message.Emoji);
                Assert.Equal(string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_ConfiguredTargets,
                    string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_LocationWithAgents,
                        scope == "project" ? relativePath : $"~/{relativePath}",
                        context.Clients.Single(value => value.Id == client).DisplayName)), message.Message);
            });
        Assert.Empty(interaction.DisplayedErrors);
    }

    [Theory]
    [InlineData(nameof(AgentConfigurationScope.Project))]
    [InlineData(nameof(AgentConfigurationScope.Global))]
    public async Task Remote_CombinesNewAndExistingRegistrationsWithOneRowPerAgent(string scopeName)
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        var scope = Enum.Parse<AgentConfigurationScope>(scopeName);
        Assert.All(await context.ConfigureIntegrationAsync(scope, [context.Copilot]).DefaultTimeout(),
            result => Assert.Equal(AgentConfigurationStatus.Configured, result.Status));
        var interaction = new TestInteractionService();
        var services = context.CreateServiceCollection(outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interaction;
            options.AgentEnvironmentDetectorFactory = _ => new TestAgentEnvironmentDetector();
            options.TelemetryHookConfiguratorFactory = _ => new TestTelemetryHookConfigurator();
        });
        using var provider = services.BuildServiceProvider();
        var scopeRoot = scope is AgentConfigurationScope.Project ? context.Project : context.Home;
        var prefix = scope is AgentConfigurationScope.Global ? "~/" : string.Empty;
        var targetEntries = context.Clients.Select(client =>
            $"{prefix}{Path.GetRelativePath(scopeRoot.FullName, client.GetIntegrationConfigPath(scope, context.Project)).Replace(Path.DirectorySeparatorChar, '/')} ({client.DisplayName})").ToArray();

        for (var invocation = 0; invocation < 2; invocation++)
        {
            var previousMessageCount = interaction.DisplayedMessages.Count;
            var result = provider.GetRequiredService<RootCommand>().Parse(
                $"agent init --agents copilot,claude,opencode --scope {scope} --skills aspire --mcp=false");

            Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());
            var messages = interaction.DisplayedMessages.Skip(previousMessageCount).ToArray();
            Assert.Collection(messages,
                message =>
                {
                    Assert.Equal(KnownEmojis.Robot, message.Emoji);
                    Assert.Equal(string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_ConfiguredTargets,
                        string.Join(", ", targetEntries)), message.Message);
                });
            Assert.Empty(interaction.DisplayedErrors);
        }
    }

    [Fact]
    public async Task Remote_SummaryListsAgentConfigurationFiles()
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(context.Project.FullName, "aspire.config.json"), """
            {"skillSources":{"team":{"repository":"example/team","plugins":["deployment"]}}}
            """);
        var interaction = new TestInteractionService();
        using var provider = context.CreateServiceCollection(outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interaction;
        }).BuildServiceProvider();
        var result = provider.GetRequiredService<RootCommand>().Parse(
            "agent init --agents copilot,claude --scope project --skills aspire --mcp=false");

        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());
        var summary = Assert.Single(interaction.DisplayedMessages, message => message.Emoji.Equals(KnownEmojis.Robot));
        await Verify(summary.Message);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Remote_SummaryListsAgentConfigurationFilesWithExistingOpenCodeCatalogs(bool array, bool pinned)
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        var catalogs = new JsonArray("https://example.test/unrelated-catalog/");
        if (pinned)
        {
            catalogs.Add("https://raw.githubusercontent.com/microsoft/aspire-skills/release/opencode/v2/");
        }
        var config = new JsonObject
        {
            ["skills"] = array ? catalogs : new JsonObject { ["urls"] = catalogs }
        };
        await AgentConfigurationTestContext.WriteAsync(
            context.OpenCode.GetIntegrationConfigPath(AgentConfigurationScope.Project, context.Project), config.ToJsonString());
        var interaction = new TestInteractionService();
        using var provider = context.CreateServiceCollection(outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interaction;
        }).BuildServiceProvider();
        var result = provider.GetRequiredService<RootCommand>().Parse(
            "agent init --agents copilot,claude,opencode --scope project --skills aspire --mcp=false");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        outputHelper.WriteLine(string.Join(System.Environment.NewLine, interaction.DisplayedErrors));
        Assert.Empty(interaction.DisplayedErrors);
        Assert.Equal(CliExitCodes.Success, exitCode);
        var summary = Assert.Single(interaction.DisplayedMessages, message => message.Emoji.Equals(KnownEmojis.Robot));
        await Verify(summary.Message).UseParameters(array, pinned);
    }

    [Fact]
    public async Task Remote_FailedRegistrationsKeepEachTargetWithItsReason()
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        const string original = """{"enabledPlugins":{"aspire@aspire-skills":false}}""";
        IAgentClient[] clients = [context.Copilot, context.ClaudeCode];
        foreach (var client in clients)
        {
            await AgentConfigurationTestContext.WriteAsync(
                client.GetIntegrationConfigPath(AgentConfigurationScope.Project, context.Project), original);
        }
        var interaction = new TestInteractionService();
        using var provider = context.CreateServiceCollection(outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interaction;
        }).BuildServiceProvider();

        var result = provider.GetRequiredService<RootCommand>().Parse(
            "agent init --agents copilot,claude --scope project --skills aspire --mcp=false");

        Assert.Equal(CliExitCodes.InvalidCommand, await result.InvokeAsync().DefaultTimeout());
        var reason = string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_ExistingConfigurationConflict,
            "enabledPlugins.aspire@aspire-skills");
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_FailedTarget,
            $"GitHub Copilot: .github/copilot/settings.json{System.Environment.NewLine}  {reason}{System.Environment.NewLine}" +
            $"  Claude Code: .claude/settings.json{System.Environment.NewLine}  {reason}"),
            Assert.Single(interaction.DisplayedErrors));
        foreach (var client in clients)
        {
            Assert.Equal(original, await File.ReadAllTextAsync(
                client.GetIntegrationConfigPath(AgentConfigurationScope.Project, context.Project)));
        }
        Assert.Empty(context.Home.EnumerateFileSystemInfos());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Remote_OpenCodeSkillsAndMcpUseIndependentRepresentations(bool array)
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        var path = Path.Combine(context.Project.FullName, "opencode.json");
        var original = JsonNode.Parse(array
            ? """{"skills":["./team-skills"],"mcp":{"servers":{"aspire":{"type":"local","command":["pinned-aspire"],"disabled":true}}}}"""
            : """{"skills":{"paths":["./team-skills"]},"mcp":{"servers":{"aspire":{"type":"local","command":["pinned-aspire"],"disabled":true}}}}""")!;
        await File.WriteAllTextAsync(path, original.ToJsonString());
        using var provider = context.CreateServiceCollection(outputHelper).BuildServiceProvider();

        var result = provider.GetRequiredService<RootCommand>().Parse(
            "agent init --agents opencode --scope project --skills aspire --mcp");

        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());
        var content = await File.ReadAllTextAsync(path);
        var configured = JsonNode.Parse(content)!;
        Assert.True(JsonNode.DeepEquals(original["mcp"], configured["mcp"]));
        if (!array)
        {
            Assert.Equal(["./team-skills"], configured["skills"]!["paths"]!.AsArray().Select(value => (string)value!));
        }
        var sources = array ? configured["skills"]!.AsArray() : configured["skills"]!["urls"]!.AsArray();
        Assert.Equal(array
            ? ["./team-skills", "https://raw.githubusercontent.com/microsoft/aspire-skills/main/opencode/v2/"]
            : new[] { "https://raw.githubusercontent.com/microsoft/aspire-skills/main/opencode/v1/" },
            sources.Select(value => (string)value!));
        var repeated = provider.GetRequiredService<RootCommand>().Parse(
            "agent init --agents opencode --scope project --skills aspire --mcp");
        Assert.Equal(CliExitCodes.Success, await repeated.InvokeAsync().DefaultTimeout());
        Assert.Equal(content, await File.ReadAllTextAsync(path));
        Assert.Equal([path], context.Project.EnumerateFiles().Select(file => file.FullName));
        Assert.Empty(context.Home.EnumerateFileSystemInfos());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Remote_RemovesLegacyOpenCodeCopyAndLeavesConfiguredSkillRootsAlone(bool array)
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        var configPath = Path.Combine(context.Project.FullName, ".opencode", "opencode.jsonc");
        var skillDirectory = Path.Combine(context.Project.FullName, ".opencode", "skill", "aspire");
        var configuredSkillPath = Path.Combine(context.Project.FullName, "team-skills", "aspire", "SKILL.md");
        const string configuredSkill = "Team Aspire instructions.";
        await AgentConfigurationTestContext.WriteAsync(configPath, array
            ? """{"skills":["./team-skills"]}"""
            : """{"skills":{"paths":["./team-skills"]}}""");
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(skillDirectory, "SKILL.md"), "My customized Aspire instructions.");
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(skillDirectory, "references", "deploy.md"), "Supporting file.");
        await AgentConfigurationTestContext.WriteAsync(configuredSkillPath, configuredSkill);
        var interaction = new TestInteractionService();
        using var provider = context.CreateServiceCollection(outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interaction;
        }).BuildServiceProvider();

        var result = provider.GetRequiredService<RootCommand>().Parse(
            "agent init --agents opencode --scope project --skills aspire --mcp=false");

        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());
        Assert.Empty(interaction.DisplayedErrors);
        Assert.Contains(interaction.DisplayedMessages, message => message.Emoji.Equals(KnownEmojis.PoliceCarLight) &&
            message.Message == string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_LocalSkillsConflict,
                "  .opencode/skill/aspire"));
        Assert.Equal(
            [new BooleanPromptCall(AgentCommandStrings.InitCommand_RemoveSkillCopiesPrompt, true)],
            interaction.BooleanPromptCalls);
        Assert.Contains(interaction.DisplayedMessages, message => message.Emoji.Equals(KnownEmojis.CheckMarkButton) &&
            message.Message == string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_RemovedSkillCopy, ".opencode/skill/aspire"));
        Assert.False(Directory.Exists(skillDirectory));
        Assert.True(Directory.Exists(Path.Combine(context.Project.FullName, ".opencode", "skill")));
        // Configured skill roots belong to the user; the CLI never copied skills there.
        Assert.Equal(configuredSkill, await File.ReadAllTextAsync(configuredSkillPath));
        var config = JsonNode.Parse(await File.ReadAllTextAsync(configPath))!;
        var sources = array ? config["skills"]!.AsArray() : config["skills"]!["urls"]!.AsArray();
        Assert.Equal(array
            ? ["./team-skills", "https://raw.githubusercontent.com/microsoft/aspire-skills/main/opencode/v2/"]
            : new[] { "https://raw.githubusercontent.com/microsoft/aspire-skills/main/opencode/v1/" },
            sources.Select(value => (string)value!));
        Assert.Null(config["mcp"]);
        Assert.Empty(context.Home.EnumerateFileSystemInfos());
    }

    [Theory]
    [InlineData("ignored")]
    [InlineData("plugin-not-selected")]
    [InlineData("registration-failed")]
    public async Task Remote_KeepsSkillCopiesUnlessThePluginIsRegisteredAndRemovalIsAccepted(string reason)
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        var skillPath = Path.Combine(context.Project.FullName, ".agents", "skills", "aspire", "SKILL.md");
        const string skill = "My customized Aspire instructions.";
        await AgentConfigurationTestContext.WriteAsync(skillPath, skill);
        if (reason == "registration-failed")
        {
            await AgentConfigurationTestContext.WriteAsync(
                context.Copilot.GetIntegrationConfigPath(AgentConfigurationScope.Project, context.Project),
                """{"enabledPlugins":{"aspire@aspire-skills":false}}""");
        }
        var interaction = new TestInteractionService();
        using var provider = context.CreateServiceCollection(outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interaction;
        }).BuildServiceProvider();
        var arguments = reason switch
        {
            "ignored" => "--skills aspire --mcp=false --ignore-skill-copies",
            "plugin-not-selected" => "--skills none --mcp",
            _ => "--skills aspire --mcp=false"
        };

        var result = provider.GetRequiredService<RootCommand>().Parse($"agent init --agents copilot --scope project {arguments}");

        Assert.Equal(reason == "registration-failed" ? CliExitCodes.InvalidCommand : CliExitCodes.Success,
            await result.InvokeAsync().DefaultTimeout());
        Assert.Equal(skill, await File.ReadAllTextAsync(skillPath));
        // Copies are only reported when they would be removed: --ignore-skill-copies skips them entirely,
        // and they are irrelevant when the plugin is not selected.
        var conflictWarning = string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_LocalSkillsConflict,
            "  .agents/skills/aspire");
        Assert.Equal(reason == "registration-failed" ? 1 : 0,
            interaction.DisplayedMessages.Count(message => message.Message == conflictWarning));
        // The prompt is only recorded when no option value answers it, so a registration failure
        // shows the copies were selected for removal and kept only because registration failed.
        Assert.Equal(
            reason == "registration-failed" ? [new BooleanPromptCall(AgentCommandStrings.InitCommand_RemoveSkillCopiesPrompt, true)] : [],
            interaction.BooleanPromptCalls);
        Assert.Equal(reason == "registration-failed" ? 1 : 0, interaction.DisplayedErrors.Count);
    }

    [Fact]
    public async Task Remote_ReportsSkillCopiesReachedThroughLinkedFoldersWithoutRemovingThem()
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        var shared = context.Workspace.CreateDirectory("shared-skills");
        var sharedSkillPath = Path.Combine(shared.FullName, "aspire", "SKILL.md");
        const string skill = "Shared Aspire instructions.";
        await AgentConfigurationTestContext.WriteAsync(sharedSkillPath, skill);
        var linkPath = Path.Combine(context.Project.FullName, ".agents", "skills");
        Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
        try
        {
            Directory.CreateSymbolicLink(linkPath, shared.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"Symbolic links are not available: {ex.Message}");
        }
        var interaction = new TestInteractionService();
        using var provider = context.CreateServiceCollection(outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interaction;
        }).BuildServiceProvider();

        var result = provider.GetRequiredService<RootCommand>().Parse(
            "agent init --agents copilot --scope project --skills aspire --mcp=false");

        Assert.Equal(CliExitCodes.InvalidCommand, await result.InvokeAsync().DefaultTimeout());
        Assert.Equal(
            string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_RemoveSkillCopyFailed,
                ".agents/skills/aspire", AgentCommandStrings.InitCommand_SkillCopyBehindLink),
            Assert.Single(interaction.DisplayedErrors));
        Assert.Equal(skill, await File.ReadAllTextAsync(sharedSkillPath));
        Assert.True(context.Copilot.HasIntegrationRegistration(
            context.Project, AgentConfigurationScope.Project, [SkillSource.BuiltIn]));
    }

    [Theory]
    [InlineData(nameof(AgentConfigurationScope.Project))]
    [InlineData(nameof(AgentConfigurationScope.Global))]
    public async Task Mcp_InvalidOpenCodeNativeContainer_DoesNotStopIndependentClients(string scopeName)
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        var scope = Enum.Parse<AgentConfigurationScope>(scopeName);
        var path = context.OpenCode.GetMcpConfigPath(scope, context.Project);
        const string original = """{"mcp":{"servers":null}}""";
        await AgentConfigurationTestContext.WriteAsync(path, original);
        var interaction = new TestInteractionService();
        var hooks = new TestTelemetryHookConfigurator();
        var services = context.CreateServiceCollection(outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interaction;
            options.AgentEnvironmentDetectorFactory = _ => new TestAgentEnvironmentDetector { DetectedEnvironments = AgentEnvironment.CopilotCli };
            options.TelemetryHookConfiguratorFactory = _ => hooks;
        });
        using var provider = services.BuildServiceProvider();

        var result = provider.GetRequiredService<RootCommand>().Parse(
            $"agent init --agents opencode,copilot --scope {scope} --skills none --mcp");

        Assert.Equal(CliExitCodes.InvalidCommand, await result.InvokeAsync().DefaultTimeout());
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.True(File.Exists(context.Copilot.GetMcpConfigPath(scope, context.Project)));
        Assert.Equal(scope is AgentConfigurationScope.Global ? AgentEnvironment.CopilotCli : AgentEnvironment.None, hooks.ConfiguredEnvironments);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture,
            AgentCommandStrings.McpServerContainerMustBeJsonObject, "mcp.servers", path), Assert.Single(interaction.DisplayedErrors));
    }

    [Theory]
    [InlineData("copilot", "{broken")]
    [InlineData("claude", "{broken")]
    [InlineData("opencode", """{"skills":[true],"mcp":{"servers":{}}}""")]
    public async Task Remote_FailedRegistration_StillConfiguresIndependentlyRequestedMcp(string clientId, string malformed)
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        var client = context.Clients.Single(client => string.Equals(client.Id, clientId, StringComparisons.AgentClientId));
        var integrationPath = client.GetIntegrationConfigPath(AgentConfigurationScope.Project, context.Project);
        await AgentConfigurationTestContext.WriteAsync(integrationPath, malformed);
        var interaction = new TestInteractionService();
        var services = context.CreateServiceCollection(outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interaction;
        });
        using var provider = services.BuildServiceProvider();

        var clients = clientId == "opencode" ? "opencode,copilot" : clientId;
        var result = provider.GetRequiredService<RootCommand>().Parse(
            $"agent init --agents {clients} --scope project --skills aspire --mcp");

        Assert.Equal(CliExitCodes.InvalidCommand, await result.InvokeAsync().DefaultTimeout());
        Assert.Single(interaction.DisplayedErrors);
        var config = JsonNode.Parse(await File.ReadAllTextAsync(
            client.GetMcpConfigPath(AgentConfigurationScope.Project, context.Project)))!;
        if (clientId == "opencode")
        {
            Assert.True(File.Exists(context.Copilot.GetIntegrationConfigPath(AgentConfigurationScope.Project, context.Project)));
            Assert.True(File.Exists(context.Copilot.GetMcpConfigPath(AgentConfigurationScope.Project, context.Project)));
            Assert.Equal([true], config["skills"]!.AsArray().Select(value => (bool)value!));
            var server = config["mcp"]!["servers"]!["aspire"]!;
            Assert.Equal(["aspire", "agent", "mcp"], server["command"]!.AsArray().Select(value => (string)value!));
            Assert.False((bool)server["disabled"]!);
            Assert.Equal(["servers"], config["mcp"]!.AsObject().Select(property => property.Key));
        }
        else
        {
            Assert.Equal(malformed, await File.ReadAllTextAsync(integrationPath));
            var server = config["mcpServers"]!["aspire"]!;
            Assert.Equal("aspire", (string?)server["command"]);
            Assert.Equal(["agent", "mcp"], server["args"]!.AsArray().Select(value => (string?)value));
        }
        Assert.Empty(context.Home.EnumerateFileSystemInfos());
    }

}
