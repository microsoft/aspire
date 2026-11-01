// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json;
using Aspire.Cli.Agents;
using Aspire.Cli.Agents.AspireSkills;
using Aspire.Cli.Agents.OpenCode;
using Aspire.Cli.Commands;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.Agents;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aspire.Cli.Tests.Commands;

public class AgentInitClientSelectionTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData("copilot", "project", ".agents")]
    [InlineData("opencode", "project", ".agents")]
    [InlineData("claude", "project", ".claude")]
    [InlineData("copilot", "global", ".agents")]
    [InlineData("opencode", "global", ".agents")]
    [InlineData("claude", "global", ".claude")]
    public async Task AgentInit_ExplicitUndetectedClient_InstallsCopiedSkillsOnlyInSelectedScope(string client, string scope, string skillFolder)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var homeDirectory = workspace.CreateDirectory("fake-home");
        var detector = new TestAgentEnvironmentDetector();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => TestExecutionContextHelper.CreateExecutionContext(workspace.WorkspaceRoot, homeDirectory: homeDirectory);
            options.AgentEnvironmentDetectorFactory = _ => detector;
        });
        using var provider = services.BuildServiceProvider();

        var result = provider.GetRequiredService<RootCommand>().Parse(
            $"agent init --agents {client} --scope {scope} --skills aspire --aspire-source embedded --mcp=false");
        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());
        Assert.Equal(scope == "global" ? 1 : 0, detector.ScanCount);

        var root = scope == "project" ? workspace.WorkspaceRoot : homeDirectory;
        var expected = Path.Combine(root.FullName, skillFolder, "skills");
        var candidates = new[]
        {
            Path.Combine(workspace.WorkspaceRoot.FullName, ".agents", "skills"),
            Path.Combine(workspace.WorkspaceRoot.FullName, ".claude", "skills"),
            Path.Combine(homeDirectory.FullName, ".agents", "skills"),
            Path.Combine(homeDirectory.FullName, ".claude", "skills"),
            Path.Combine(workspace.WorkspaceRoot.FullName, ".github", "skills"),
            Path.Combine(workspace.WorkspaceRoot.FullName, ".opencode", "skills"),
            Path.Combine(workspace.WorkspaceRoot.FullName, ".opencode", "skill"),
            Path.Combine(homeDirectory.FullName, ".github", "skills"),
            Path.Combine(homeDirectory.FullName, ".opencode", "skills"),
            Path.Combine(homeDirectory.FullName, ".opencode", "skill")
        };
        Assert.Equal([expected], candidates.Where(Directory.Exists));
        Assert.Equal(
            ["SKILL.md", Path.Combine("references", "app-commands.md")],
            Directory.EnumerateFiles(Path.Combine(expected, "aspire"), "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(Path.Combine(expected, "aspire"), path)).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("project")]
    [InlineData("global")]
    public async Task AgentInit_UniversalClients_LeaveExistingAgentSpecificSkillsUntouched(string scope)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var homeDirectory = workspace.CreateDirectory("fake-home");
        const string original = "existing agent-specific skill";
        var existingPaths = new List<string>();
        foreach (var root in new[] { workspace.WorkspaceRoot, homeDirectory })
        {
            foreach (var folder in new[] { ".github", ".opencode" })
            {
                var directory = Path.Combine(root.FullName, folder, "skills", "aspire");
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "SKILL.md");
                await File.WriteAllTextAsync(path, original);
                existingPaths.Add(path);
            }
        }

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => TestExecutionContextHelper.CreateExecutionContext(workspace.WorkspaceRoot, homeDirectory: homeDirectory);
        });
        using var provider = services.BuildServiceProvider();

        var result = provider.GetRequiredService<RootCommand>().Parse(
            $"agent init --agents copilot,opencode --scope {scope} --skills aspire --aspire-source embedded --mcp=false");
        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());
        var selectedRoot = scope == "project" ? workspace.WorkspaceRoot : homeDirectory;
        Assert.True(File.Exists(Path.Combine(selectedRoot.FullName, ".agents", "skills", "aspire", "SKILL.md")));
        foreach (var path in existingPaths)
        {
            Assert.Equal(original, await File.ReadAllTextAsync(path));
            Assert.Equal([path], Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*", SearchOption.AllDirectories));
        }
    }

    [Fact]
    public async Task AgentInit_CopilotAppCliAndEditor_AreOneDetectedSelection()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AgentEnvironmentDetectorFactory = _ => new TestAgentEnvironmentDetector
            {
                DetectedEnvironments = AgentEnvironment.CopilotApp | AgentEnvironment.CopilotCli | AgentEnvironment.VsCode
            };
        });
        using var provider = services.BuildServiceProvider();

        var result = provider.GetRequiredService<RootCommand>().Parse("agent init --skills aspire --aspire-source embedded --mcp=false");
        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());
        Assert.True(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, ".agents", "skills", "aspire", "SKILL.md")));
        Assert.False(Directory.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, ".claude")));
        var copilot = provider.GetServices<IAgentClient>().Single(client => string.Equals(client.Id, "copilot", StringComparisons.AgentClientId));
        Assert.Equal(AgentEnvironment.CopilotApp | AgentEnvironment.CopilotCli | AgentEnvironment.VsCode, copilot.Environments);
    }

    [Theory]
    [InlineData("project", true, false, null, "copilot")]
    [InlineData("project", false, true, null, "copilot")]
    [InlineData("project", true, true, null, "copilot")]
    [InlineData("project", false, false, null, "copilot")]
    [InlineData("global", true, false, null, "copilot")]
    [InlineData("global", false, true, null, "copilot")]
    [InlineData("global", true, true, null, "copilot")]
    [InlineData("global", false, false, null, "copilot")]
    [InlineData("project", true, false, nameof(AgentEnvironment.ClaudeCode), "claude")]
    [InlineData("project", false, true, nameof(AgentEnvironment.ClaudeCode), "claude")]
    [InlineData("project", true, true, nameof(AgentEnvironment.ClaudeCode), "claude")]
    [InlineData("project", false, false, nameof(AgentEnvironment.ClaudeCode), "claude")]
    [InlineData("global", true, false, nameof(AgentEnvironment.ClaudeCode), "claude")]
    [InlineData("global", false, true, nameof(AgentEnvironment.ClaudeCode), "claude")]
    [InlineData("global", true, true, nameof(AgentEnvironment.ClaudeCode), "claude")]
    [InlineData("global", false, false, nameof(AgentEnvironment.ClaudeCode), "claude")]
    [InlineData("project", false, true, nameof(AgentEnvironment.OpenCode), "opencode")]
    [InlineData("global", false, true, nameof(AgentEnvironment.OpenCode), "opencode")]
    public async Task AgentInit_DefaultsUseDetectionOrCopilotFallback_IndependentOfAssetsAndScope(
        string scope, bool installSkills, bool configureMcp, string? detectedEnvironmentName, string expectedClients)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var homeDirectory = workspace.CreateDirectory("fake-home");
        var hooks = new TestTelemetryHookConfigurator();
        var interaction = new TestInteractionService();
        AgentEnvironment? detectedEnvironment = detectedEnvironmentName is { } name ? Enum.Parse<AgentEnvironment>(name) : null;
        var detector = new TestAgentEnvironmentDetector
        {
            DetectedEnvironments = detectedEnvironment ?? AgentEnvironment.None
        };
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => TestExecutionContextHelper.CreateExecutionContext(workspace.WorkspaceRoot, homeDirectory: homeDirectory);
            options.AgentEnvironmentDetectorFactory = _ => detector;
            options.TelemetryHookConfiguratorFactory = _ => hooks;
            options.InteractionServiceFactory = _ => interaction;
        });
        using var provider = services.BuildServiceProvider();

        var result = provider.GetRequiredService<RootCommand>().Parse(
            $"agent init --scope {scope} --skills {(installSkills ? "aspire" : "none")} --aspire-source embedded --mcp={configureMcp}");
        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());
        Assert.Equal(1, detector.ScanCount);
        var agentPrompt = Assert.Single(interaction.MultiSelectionPromptCalls,
            call => call.PromptText == AgentCommandStrings.InitCommand_SelectAgentsPrompt);
        Assert.Equal(expectedClients, agentPrompt.DefaultValue);
        Assert.Equal(expectedClients.Split(','), agentPrompt.PreSelected.Cast<IAgentClient>().Select(client => client.Id), StringComparers.AgentClientId);

        var clients = provider.GetServices<IAgentClient>().ToArray();
        var selectedScope = scope == "project" ? AgentConfigurationScope.Project : AgentConfigurationScope.Global;
        var defaults = clients.Where(client => expectedClients.Split(',').Contains(client.Id, StringComparers.AgentClientId)).ToArray();
        Assert.Equal(
            configureMcp
                ? defaults.Select(client => client.GetMcpConfigPath(selectedScope, workspace.WorkspaceRoot)).Distinct().Order(StringComparer.Ordinal)
                : [],
            clients.Select(client => client.GetMcpConfigPath(selectedScope, workspace.WorkspaceRoot))
                .Distinct().Where(File.Exists).Order(StringComparer.Ordinal));
        Assert.Equal(
            installSkills
                ? defaults.Select(client => client.GetSkillBaseDirectory(selectedScope, workspace.WorkspaceRoot)).Distinct().Order(StringComparer.Ordinal)
                : [],
            clients.Select(client => client.GetSkillBaseDirectory(selectedScope, workspace.WorkspaceRoot))
                .Distinct().Where(directory => File.Exists(Path.Combine(directory, "aspire", "SKILL.md"))).Order(StringComparer.Ordinal));
        var unselectedRoot = scope == "project" ? homeDirectory : workspace.WorkspaceRoot;
        Assert.False(Directory.Exists(Path.Combine(unselectedRoot.FullName, ".agents")));
        Assert.Equal(
            (installSkills || configureMcp) && selectedScope is AgentConfigurationScope.Global && detectedEnvironment is { } detected &&
                defaults.Any(client => (client.Environments & detected) != AgentEnvironment.None) ? detected : AgentEnvironment.None,
            hooks.ConfiguredEnvironments);
    }

    [Theory]
    [InlineData("project", false, 0)]
    [InlineData("global", false, 0)]
    [InlineData("project", true, 0)]
    [InlineData("global", true, 1)]
    public async Task AgentInit_ExplicitAgents_DetectsOnlyForConsentedGlobalAssets(string scope, bool configureMcp, int expectedScanCount)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var homeDirectory = workspace.CreateDirectory("fake-home");
        var detector = new TestAgentEnvironmentDetector { DetectedEnvironments = AgentEnvironment.ClaudeCode };
        var hooks = new TestTelemetryHookConfigurator();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => TestExecutionContextHelper.CreateExecutionContext(workspace.WorkspaceRoot, homeDirectory: homeDirectory);
            options.AgentEnvironmentDetectorFactory = _ => detector;
            options.TelemetryHookConfiguratorFactory = _ => hooks;
        });
        using var provider = services.BuildServiceProvider();

        var result = provider.GetRequiredService<RootCommand>().Parse(
            $"agent init --agents claude --scope {scope} --skills none --mcp={configureMcp}");
        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());
        Assert.Equal(expectedScanCount, detector.ScanCount);
        Assert.Equal(expectedScanCount == 1 ? AgentEnvironment.ClaudeCode : AgentEnvironment.None, hooks.ConfiguredEnvironments);
    }

    [Theory]
    [InlineData("none", "all", "--mcp")]
    [InlineData("all", "none", "--mcp=false")]
    public async Task AgentInit_NoneOrAllOff_LeavesSkillsMcpAndHooksUnchanged(string clients, string skills, string mcp)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var homeDirectory = workspace.CreateDirectory("fake-home");
        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, ".mcp.json");
        const string original = """{"mcpServers":{"aspire":{"command":"aspire","args":["mcp","start"]}}}""";
        await File.WriteAllTextAsync(configPath, original);
        var before = GetFilesWithoutCliLogs(workspace.WorkspaceRoot);
        var detector = new TestAgentEnvironmentDetector();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => TestExecutionContextHelper.CreateExecutionContext(workspace.WorkspaceRoot, homeDirectory: homeDirectory);
            options.AgentEnvironmentDetectorFactory = _ => detector;
        });
        using var provider = services.BuildServiceProvider();

        var result = provider.GetRequiredService<RootCommand>().Parse(
            $"agent init --agents {clients} --scope global --skills {skills} {mcp}");
        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());
        Assert.Equal(0, detector.ScanCount);
        Assert.Equal(original, await File.ReadAllTextAsync(configPath));
        Assert.Equal(before, GetFilesWithoutCliLogs(workspace.WorkspaceRoot));
        Assert.Empty(Directory.GetFileSystemEntries(homeDirectory.FullName));
    }

    [Theory]
    [InlineData("--agents unsupported")]
    [InlineData("--agents copilot,unsupported")]
    [InlineData("--agents copilot,none")]
    [InlineData("--agents \"GitHub Copilot\"")]
    [InlineData("--agents \"Claude Code\"")]
    [InlineData("--agents")]
    [InlineData("--agents \"\"")]
    [InlineData("--scope both")]
    [InlineData("--scope 0")]
    [InlineData("--scope 1")]
    [InlineData("--scope user")]
    [InlineData("--scope project,global")]
    public void AgentInit_InvalidSelection_IsRejectedBeforeExecution(string options)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper).BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var before = GetFilesWithoutCliLogs(workspace.WorkspaceRoot);
        var result = command.Parse($"agent init {options}");

        Assert.Single(result.Errors);
        Assert.Equal(before, GetFilesWithoutCliLogs(workspace.WorkspaceRoot));
    }

    [Theory]
    [InlineData("PrOjEcT", nameof(AgentConfigurationScope.Project))]
    [InlineData("GlObAl", nameof(AgentConfigurationScope.Global))]
    public void AgentInit_ScopeNames_AreCaseInsensitive(string value, string expected)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper).BuildServiceProvider();

        var result = provider.GetRequiredService<RootCommand>().Parse($"agent init --scope {value}");

        Assert.Empty(result.Errors);
        Assert.Equal(Enum.Parse<AgentConfigurationScope>(expected), result.GetValue<AgentConfigurationScope>("--scope"));
    }

    [Theory]
    [InlineData("agent init")]
    [InlineData("mcp init")]
    [InlineData("init")]
    [InlineData("new aspire-empty")]
    public void AgentSelection_SingularOption_IsRejectedBeforeExecution(string command)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper).BuildServiceProvider();
        var before = GetFilesWithoutCliLogs(workspace.WorkspaceRoot);
        var result = provider.GetRequiredService<RootCommand>().Parse($"{command} --agent copilot");

        Assert.NotEmpty(result.Errors);
        Assert.Equal(before, GetFilesWithoutCliLogs(workspace.WorkspaceRoot));
    }

    [Theory]
    [InlineData("agent init")]
    [InlineData("mcp init")]
    [InlineData("init")]
    [InlineData("new aspire-empty")]
    public void AgentSelection_OptionUsesRegisteredClients(string command)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        services.RemoveAll<IAgentClient>();
        services.AddSingleton<IAgentClient, OpenCodeAgentClient>();
        using var provider = services.BuildServiceProvider();
        var root = provider.GetRequiredService<RootCommand>();
        foreach (var selection in new[] { "opencode", "OpEnCoDe", "all", "none" })
        {
            var result = root.Parse($"{command} --agents {selection}");
            Assert.Empty(result.Errors);
            Assert.Equal(selection, result.GetValue<string?>("--agents"));
        }

        var rejected = root.Parse($"{command} --agents copilot");
        Assert.Equal(string.Format(CultureInfo.CurrentCulture,
            AgentCommandStrings.InitCommand_InvalidEnvironments, "copilot", "opencode", "all", "none"),
            Assert.Single(rejected.Errors).Message);
        Assert.Equal(["all", "none", "opencode"],
            root.Parse($"{command} --agents ").GetCompletions().Select(item => item.Label).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task AgentInit_AllUsesOnlyRegisteredClients()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        services.RemoveAll<IAgentClient>();
        services.AddSingleton<IAgentClient, OpenCodeAgentClient>();
        using var provider = services.BuildServiceProvider();
        var before = GetFilesWithoutCliLogs(workspace.WorkspaceRoot);

        var result = provider.GetRequiredService<RootCommand>().Parse(
            "agent init --agents all --scope project --skills none --mcp");

        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());
        Assert.Equal(before.Append(Path.Combine(workspace.WorkspaceRoot.FullName, "opencode.jsonc")).Order(StringComparer.Ordinal),
            GetFilesWithoutCliLogs(workspace.WorkspaceRoot));
        Assert.Equal("opencode", Assert.Single(provider.GetServices<IAgentClient>()).Id);
    }

    [Fact]
    public async Task AgentInit_InteractiveEmptySelection_DoesNotConfigureDefaultClients()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var homeDirectory = workspace.CreateDirectory("fake-home");
        var hooks = new TestTelemetryHookConfigurator();
        var interaction = new TestInteractionService
        {
            PromptForSelectionsCallback = (prompt, choices, _, _) =>
            {
                Assert.Equal(AgentCommandStrings.InitCommand_SelectAgentsPrompt, prompt);
                Assert.Equal(["copilot", "claude", "opencode"], choices.Cast<IAgentClient>().Select(client => client.Id), StringComparers.AgentClientId);
                return [];
            },
            ConfirmCallback = (_, _) => throw new InvalidOperationException("Assets must not be prompted for an empty agent selection."),
            PromptForSelectionCallback = (_, _, _, _) => throw new InvalidOperationException("Scope must not be prompted for an empty agent selection.")
        };
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => TestExecutionContextHelper.CreateExecutionContext(workspace.WorkspaceRoot, homeDirectory: homeDirectory);
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();
            options.InteractionServiceFactory = _ => interaction;
            options.TelemetryHookConfiguratorFactory = _ => hooks;
        });
        using var provider = services.BuildServiceProvider();
        var before = GetFilesWithoutCliLogs(workspace.WorkspaceRoot);

        var result = provider.GetRequiredService<RootCommand>().Parse("agent init --skills all --mcp");
        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());
        Assert.Equal(before, GetFilesWithoutCliLogs(workspace.WorkspaceRoot));
        Assert.Empty(homeDirectory.GetFileSystemInfos());
        Assert.Equal(AgentEnvironment.None, hooks.ConfiguredEnvironments);
    }

    [Theory]
    [InlineData("project", true, false)]
    [InlineData("project", false, true)]
    [InlineData("project", true, true)]
    [InlineData("global", true, false)]
    [InlineData("global", false, true)]
    [InlineData("global", true, true)]
    public async Task AgentInit_InstallationLocations_ShowSelectedAssetsAndSharedConsumers(string scope, bool installSkills, bool configureMcp)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var homeDirectory = workspace.CreateDirectory("fake-home");
        var labels = new List<string>();
        var locations = new List<string>();
        var interaction = new TestInteractionService
        {
            PromptForSelectionsCallback = (prompt, choices, formatter, _) =>
            {
                Assert.Equal(AgentCommandStrings.InitCommand_SelectAgentsPrompt, prompt);
                labels.AddRange(choices.Cast<object>().Select(formatter));
                return choices.Cast<IAgentClient>().Where(client => string.Equals(client.Id, "copilot", StringComparisons.AgentClientId)).Cast<object>().ToArray();
            },
            DisplaySubtleMessageCallback = message =>
            {
                if (message.StartsWith(AgentCommandStrings.InitCommand_InstallationLocations, StringComparison.Ordinal))
                {
                    locations.Add(message);
                }
            }
        };
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => TestExecutionContextHelper.CreateExecutionContext(workspace.WorkspaceRoot, homeDirectory: homeDirectory);
            options.InteractionServiceFactory = _ => interaction;
        });
        using var provider = services.BuildServiceProvider();
        var result = provider.GetRequiredService<RootCommand>().Parse(
            $"agent init --scope {scope} --skills {(installSkills ? "aspire" : "none")} --aspire-source embedded --mcp={configureMcp}");
        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());
        await Verify(new { Agents = labels, Locations = Assert.Single(locations) }).UseParameters(scope, installSkills, configureMcp);
    }

    [Fact]
    public async Task AgentInit_PromptsForAgentsBeforeAssetsAndScope()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var homeDirectory = workspace.CreateDirectory("fake-home");
        var prompts = new List<string>();
        var riskWarnings = new List<string>();
        var interaction = new TestInteractionService
        {
            DisplayMessageCallback = (emoji, message, _) =>
            {
                if (emoji.Equals(KnownEmojis.Warning))
                {
                    riskWarnings.Add(message);
                }
            },
            PromptForSelectionsCallback = (prompt, choices, _, _) =>
            {
                prompts.Add(prompt);
                if (prompt == AgentCommandStrings.InitCommand_SelectAgentsPrompt)
                {
                    return choices.Cast<IAgentClient>().Where(client => string.Equals(client.Id, "copilot", StringComparisons.AgentClientId)).Cast<object>().ToArray();
                }
                Assert.Equal(AgentCommandStrings.InitCommand_SelectSkills, prompt);
                Assert.Equal(["aspire", "playwright-cli"], choices.Cast<string>());
                return ["aspire"];
            },
            ConfirmCallback = (prompt, _) =>
            {
                prompts.Add(prompt);
                return prompt == AgentCommandStrings.InitCommand_ConfigureMcpServerPrompt;
            },
            PromptForSelectionCallback = (prompt, choices, _, _) =>
            {
                prompts.Add(prompt);
                if (prompt == AgentCommandStrings.InitCommand_ConfigureAspireSourcePrompt)
                {
                    Assert.Equal([string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_PluginRiskWarning,
                        AspireSkillsRemoteConfiguration.RepositoryUrl)], riskWarnings);
                    Assert.Equal(["remote", "embedded"], choices.Cast<string>());
                    return "embedded";
                }
                Assert.Equal([AgentConfigurationScope.Project, AgentConfigurationScope.Global], choices.Cast<AgentConfigurationScope>());
                Assert.Empty(homeDirectory.GetFileSystemInfos());
                return AgentConfigurationScope.Global;
            }
        };
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => TestExecutionContextHelper.CreateExecutionContext(workspace.WorkspaceRoot, homeDirectory: homeDirectory);
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();
            options.InteractionServiceFactory = _ => interaction;
        });
        using var provider = services.BuildServiceProvider();

        var result = provider.GetRequiredService<RootCommand>().Parse("agent init");
        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());
        Assert.Equal(
            [AgentCommandStrings.InitCommand_SelectAgentsPrompt, AgentCommandStrings.InitCommand_SelectSkills,
                AgentCommandStrings.InitCommand_ConfigureAspireSourcePrompt, AgentCommandStrings.InitCommand_ConfigureMcpServerPrompt,
                AgentCommandStrings.InitCommand_SelectScopePrompt],
            prompts);
        Assert.Single(riskWarnings);
        Assert.True(File.Exists(Path.Combine(homeDirectory.FullName, ".agents", "skills", "aspire", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(homeDirectory.FullName, ".copilot", "mcp-config.json")));
    }

    [Fact]
    public async Task AgentInit_NoSelectedAssets_SkipsScopeAndWrites()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var homeDirectory = workspace.CreateDirectory("fake-home");
        var interaction = new TestInteractionService
        {
            PromptForSelectionsCallback = (prompt, choices, _, _) =>
            {
                return prompt == AgentCommandStrings.InitCommand_SelectAgentsPrompt
                    ? choices.Cast<IAgentClient>().Where(client => string.Equals(client.Id, "copilot", StringComparisons.AgentClientId)).Cast<object>().ToArray() : [];
            },
            PromptForSelectionCallback = (_, _, _, _) => throw new InvalidOperationException("Scope must not be prompted without selected assets.")
        };
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => TestExecutionContextHelper.CreateExecutionContext(workspace.WorkspaceRoot, homeDirectory: homeDirectory);
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();
            options.InteractionServiceFactory = _ => interaction;
        });
        using var provider = services.BuildServiceProvider();
        var before = GetFilesWithoutCliLogs(workspace.WorkspaceRoot);

        var result = provider.GetRequiredService<RootCommand>().Parse("agent init");
        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());
        Assert.Equal(before, GetFilesWithoutCliLogs(workspace.WorkspaceRoot));
        Assert.Empty(homeDirectory.GetFileSystemInfos());
    }

    [Fact]
    public async Task AgentInit_DuplicateClientsAndSharedDestinations_AreReportedOnce()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService
        {
            PromptForSelectionsCallback = (_, choices, _, _) => choices.Cast<object>()
                .OfType<SkillDefinition>()
                .Where(skill => skill.HasName("aspire")).Cast<object>().ToList()
        };
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interaction;
        });
        using var provider = services.BuildServiceProvider();

        var result = provider.GetRequiredService<RootCommand>().Parse("agent init --agents COPILOT,copilot,OpenCode --skills aspire --aspire-source embedded");
        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());
        var summary = Assert.Single(interaction.DisplayedMessages, message => message.Message.StartsWith(AgentCommandStrings.InitCommand_InstalledSkillsSummary, StringComparison.Ordinal));
        await Verify(summary.Message);
    }

    [Theory]
    [InlineData("project")]
    [InlineData("global")]
    public async Task AgentInit_ExplicitMcp_UsesSelectedScopeAndSharedProjectFile(string scope)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var homeDirectory = workspace.CreateDirectory("fake-home");
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(workspace.WorkspaceRoot, homeDirectory: homeDirectory);
        var environment = new TestEnvironment();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => executionContext;
        });
        services.AddSingleton<IEnvironment>(environment);
        using var provider = services.BuildServiceProvider();
        var before = GetFilesWithoutCliLogs(workspace.WorkspaceRoot);

        var result = provider.GetRequiredService<RootCommand>().Parse($"agent init --agents all --scope {scope} --skills none --mcp");
        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());

        var selectedScope = scope == "project" ? AgentConfigurationScope.Project : AgentConfigurationScope.Global;
        var paths = provider.GetServices<IAgentClient>().Select(client => client.GetMcpConfigPath(selectedScope, workspace.WorkspaceRoot)).Distinct().ToArray();
        var root = selectedScope is AgentConfigurationScope.Project ? workspace.WorkspaceRoot : homeDirectory;
        var contents = new Dictionary<string, JsonElement>();
        foreach (var path in paths)
        {
            contents.Add(Path.GetRelativePath(root.FullName, path).Replace('\\', '/'), JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(path)));
        }

        Assert.Equal(before.Concat(paths).Order(StringComparer.Ordinal), GetFilesWithoutCliLogs(workspace.WorkspaceRoot));

        await Verify(JsonSerializer.Serialize(contents), "json").UseParameters(scope);
    }

    [Fact]
    public async Task AgentInit_ExistingPinnedMcp_IsPreserved()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var path = Path.Combine(workspace.WorkspaceRoot.FullName, ".mcp.json");
        const string pinned = """{ "mcpServers": { "aspire": { "command": "custom-aspire", "args": ["--pinned"] } }, "preference": true }""";
        await File.WriteAllTextAsync(path, pinned);
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper).BuildServiceProvider();

        var result = provider.GetRequiredService<RootCommand>().Parse("agent init --agents copilot,claude --skills none --mcp");
        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());
        Assert.Equal(pinned, await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData("copilot", "project", true)]
    [InlineData("claude", "project", false)]
    [InlineData("copilot", "global", false)]
    public async Task AgentInit_LegacyMcpRepairs_AreLimitedToSelectedClientAndScope(string client, string scope, bool shouldRepair)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var configDirectory = workspace.CreateDirectory(".vscode");
        var configPath = Path.Combine(configDirectory.FullName, "mcp.json");
        const string original = """{"servers":{"aspire":{"command":"aspire","args":["mcp","start"]}}}""";
        await File.WriteAllTextAsync(configPath, original);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var result = provider.GetRequiredService<RootCommand>().Parse($"agent init --agents {client} --scope {scope} --skills none --mcp");
        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());
        using var config = JsonDocument.Parse(await File.ReadAllTextAsync(configPath));
        Assert.Equal(shouldRepair ? ["agent", "mcp"] : ["mcp", "start"],
            config.RootElement.GetProperty("servers").GetProperty("aspire").GetProperty("args").EnumerateArray().Select(argument => argument.GetString()));
        if (!shouldRepair)
        {
            Assert.Equal(original, await File.ReadAllTextAsync(configPath));
        }
    }

    [Theory]
    [InlineData("""
        {
            // Keep the editor's existing server and formatting.
            "servers": {
                "other": { "command": "other", "args": ["https://example.test/a//b"] }
            }
        }
        """)]
    [InlineData("""{"servers":{"other":{"command":"other","args":["--custom",],},},}""")]
    public async Task AgentInit_CopilotProjectMcp_AcceptsJsoncWithoutDeprecatedEntries(string original)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var configDirectory = workspace.CreateDirectory(".vscode");
        var configPath = Path.Combine(configDirectory.FullName, "mcp.json");
        await File.WriteAllTextAsync(configPath, original);
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper).BuildServiceProvider();

        var result = provider.GetRequiredService<RootCommand>().Parse(
            "agent init --agents copilot --scope project --skills none --mcp");

        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());
        Assert.Equal(original, await File.ReadAllTextAsync(configPath));
    }

    [Theory]
    [InlineData("copilot", "GitHub Copilot", ".mcp.json")]
    [InlineData("claude", "Claude Code", ".mcp.json")]
    [InlineData("opencode", "OpenCode", "opencode.jsonc")]
    public async Task AgentInit_AgentIds_UseLocalizedConfigurationDescriptions(string client, string displayName, string configFileName)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interaction;
        });
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"agent init --agents {client} --skills none --mcp");

        Assert.Empty(result.Errors);
        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());
        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, configFileName);
        Assert.True(File.Exists(configPath));
        Assert.Equal(
            string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_McpConfigurationDescription, displayName, configPath),
            Assert.Single(interaction.DisplayedMessages).Message);
    }

    [Theory]
    [InlineData("claude", "CLAUDE_CONFIG_DIR", "custom-claude", "custom-claude/.claude.json")]
    [InlineData("opencode", "OPENCODE_CONFIG", "custom-opencode.json", "custom-opencode.json")]
    [InlineData("opencode", "OPENCODE_CONFIG_DIR", "custom-opencode", "custom-opencode/opencode.jsonc")]
    [InlineData("opencode", "XDG_CONFIG_HOME", "custom-xdg", "custom-xdg/opencode/opencode.jsonc")]
    public async Task AgentInit_GlobalMcpOverrides_WriteOnlyTheResolvedDestination(
        string client, string variable, string value, string expectedRelativePath)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var homeDirectory = workspace.CreateDirectory("fake-home");
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => TestExecutionContextHelper.CreateExecutionContext(workspace.WorkspaceRoot, homeDirectory: homeDirectory);
        });
        services.AddSingleton<IEnvironment>(new TestEnvironment(new Dictionary<string, string?> { [variable] = value }));
        using var provider = services.BuildServiceProvider();
        var before = GetFilesWithoutCliLogs(workspace.WorkspaceRoot);
        var result = provider.GetRequiredService<RootCommand>().Parse(
            $"agent init --agents {client} --scope global --skills none --mcp");

        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());
        var expectedPath = Path.Combine(workspace.WorkspaceRoot.FullName, expectedRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.Equal(before.Append(expectedPath).Order(StringComparer.Ordinal), GetFilesWithoutCliLogs(workspace.WorkspaceRoot));
        Assert.Empty(homeDirectory.GetFileSystemInfos());
    }

    [Theory]
    [InlineData("project", false, false, "opencode.jsonc")]
    [InlineData("project", true, false, "opencode.json")]
    [InlineData("project", true, true, "opencode.jsonc")]
    [InlineData("global", false, false, "opencode.jsonc")]
    [InlineData("global", true, false, "opencode.json")]
    [InlineData("global", true, true, "opencode.jsonc")]
    public async Task AgentInit_OpenCodeConfiguration_UsesExistingFormatAndPrecedence(
        string scopeName, bool jsonExists, bool jsoncExists, string expectedFileName)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var homeDirectory = workspace.CreateDirectory("fake-home");
        var environment = new TestEnvironment();
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(workspace.WorkspaceRoot, homeDirectory: homeDirectory);
        var scope = scopeName == "project" ? AgentConfigurationScope.Project : AgentConfigurationScope.Global;
        var configDirectory = new DirectoryInfo(Path.GetDirectoryName(
            scope is AgentConfigurationScope.Project ? OpenCodePaths.GetMcpConfigPath(workspace.WorkspaceRoot)
                : OpenCodePaths.GetGlobalMcpConfigPath(executionContext, environment))!);
        configDirectory.Create();
        if (jsonExists)
        {
            await File.WriteAllTextAsync(Path.Combine(configDirectory.FullName, "opencode.json"), """{"model":"keep-json","mcp":{"other":{"command":["other"]}}}""");
        }
        if (jsoncExists)
        {
            await File.WriteAllTextAsync(Path.Combine(configDirectory.FullName, "opencode.jsonc"), """{"model":"keep-jsonc","mcp":{"other":{"command":["other"]}}}""");
        }
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => executionContext;
        });
        services.AddSingleton<IEnvironment>(environment);
        using var provider = services.BuildServiceProvider();
        var before = GetFilesWithoutCliLogs(workspace.WorkspaceRoot);
        var expectedPath = Path.Combine(configDirectory.FullName, expectedFileName);

        var result = provider.GetRequiredService<RootCommand>().Parse(
            $"agent init --agents opencode --scope {scopeName} --skills none --mcp");

        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());
        var openCode = provider.GetServices<IAgentClient>().Single(client => string.Equals(client.Id, "opencode", StringComparisons.AgentClientId));
        Assert.Equal(expectedPath, openCode.GetMcpConfigPath(scope, workspace.WorkspaceRoot));
        Assert.Equal(before.Append(expectedPath).Distinct().Order(StringComparer.Ordinal), GetFilesWithoutCliLogs(workspace.WorkspaceRoot));
        var configurations = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(configDirectory.FullName, "opencode.json*"))
        {
            configurations.Add(Path.GetFileName(path), await File.ReadAllTextAsync(path));
        }
        await Verify(configurations).UseParameters(scopeName, jsonExists, jsoncExists);
    }

    [Theory]
    [InlineData("project", false, false)]
    [InlineData("project", true, true)]
    [InlineData("project", true, false)]
    [InlineData("global", false, false)]
    [InlineData("global", true, true)]
    [InlineData("global", true, false)]
    public async Task AgentInit_OpenCodeConfiguration_PreservesPinnedServers(string scopeName, bool jsoncExists, bool jsoncPinned)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var homeDirectory = workspace.CreateDirectory("fake-home");
        var environment = new TestEnvironment();
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(workspace.WorkspaceRoot, homeDirectory: homeDirectory);
        var scope = scopeName == "project" ? AgentConfigurationScope.Project : AgentConfigurationScope.Global;
        var configDirectory = Path.GetDirectoryName(scope is AgentConfigurationScope.Project
            ? OpenCodePaths.GetMcpConfigPath(workspace.WorkspaceRoot) : OpenCodePaths.GetGlobalMcpConfigPath(executionContext, environment))!;
        Directory.CreateDirectory(configDirectory);
        var originals = new Dictionary<string, string>();
        foreach (var fileName in jsoncExists ? new[] { "opencode.json", "opencode.jsonc" } : ["opencode.json"])
        {
            var path = Path.Combine(configDirectory, fileName);
            var original = fileName switch
            {
                "opencode.json" when jsoncPinned => """{"mcp":{"aspire":{"command":["aspire","mcp","start"]}}}""",
                "opencode.jsonc" when !jsoncPinned => """
                    {
                        // Partial overrides must not shadow the Aspire command pin in opencode.json.
                        "model": "keep-jsonc",
                        "mcp": { "aspire": { "enabled": true }, "other": { "command": ["other"], }, },
                    }
                    """,
                _ => $$"""
                    {
                        "mcp": { "aspire": { "command": ["pinned-{{fileName}}"], "enabled": false } }
                    }
                    """
            };
            originals.Add(path, original);
            await File.WriteAllTextAsync(path, original);
        }
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => executionContext;
        });
        services.AddSingleton<IEnvironment>(environment);
        using var provider = services.BuildServiceProvider();
        var before = GetFilesWithoutCliLogs(workspace.WorkspaceRoot);

        var result = provider.GetRequiredService<RootCommand>().Parse(
            $"agent init --agents opencode --scope {scopeName} --skills none --mcp");

        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());
        foreach (var (path, original) in originals)
        {
            Assert.Equal(original, await File.ReadAllTextAsync(path));
        }
        Assert.Equal(before, GetFilesWithoutCliLogs(workspace.WorkspaceRoot));
    }

    [Theory]
    [InlineData("project")]
    [InlineData("global")]
    public async Task AgentInit_OpenCodeRemoteJsonc_DoesNotRepairUnusedSiblingCommand(string scopeName)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var homeDirectory = workspace.CreateDirectory("fake-home");
        var environment = new TestEnvironment();
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(workspace.WorkspaceRoot, homeDirectory: homeDirectory);
        var scope = scopeName == "project" ? AgentConfigurationScope.Project : AgentConfigurationScope.Global;
        var jsoncPath = scope is AgentConfigurationScope.Project ? OpenCodePaths.GetMcpConfigPath(workspace.WorkspaceRoot)
            : OpenCodePaths.GetGlobalMcpConfigPath(executionContext, environment);
        var jsonPath = Path.ChangeExtension(jsoncPath, ".json");
        const string original = """{"mcp":{"aspire":{"type":"local","command":["aspire","mcp","start"],"enabled":true}}}""";
        const string jsonc = """{"mcp":{"aspire":{"type":"remote","url":"https://example.test/mcp","enabled":true}}}""";
        Directory.CreateDirectory(Path.GetDirectoryName(jsoncPath)!);
        await File.WriteAllTextAsync(jsonPath, original);
        await File.WriteAllTextAsync(jsoncPath, jsonc);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => executionContext;
        });
        services.AddSingleton<IEnvironment>(environment);
        using var provider = services.BuildServiceProvider();

        var result = provider.GetRequiredService<RootCommand>().Parse(
            $"agent init --agents opencode --scope {scopeName} --skills none --mcp");

        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());
        Assert.Equal(original, await File.ReadAllTextAsync(jsonPath));
        Assert.Equal(jsonc, await File.ReadAllTextAsync(jsoncPath));
    }

    [Fact]
    public async Task AgentInit_OpenCodeExplicitConfigFile_DoesNotUseSiblingJsonPin()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var jsonPath = Path.Combine(workspace.WorkspaceRoot.FullName, "opencode.json");
        const string pinned = """{"mcp":{"aspire":{"command":["pinned-aspire"]}}}""";
        await File.WriteAllTextAsync(jsonPath, pinned);
        var interaction = new TestInteractionService();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interaction;
        });
        services.AddSingleton<IEnvironment>(new TestEnvironment(
            new Dictionary<string, string?> { ["OPENCODE_CONFIG"] = "opencode.jsonc" }));
        using var provider = services.BuildServiceProvider();
        var before = GetFilesWithoutCliLogs(workspace.WorkspaceRoot);
        var jsoncPath = Path.Combine(workspace.WorkspaceRoot.FullName, "opencode.jsonc");

        var result = provider.GetRequiredService<RootCommand>().Parse(
            "agent init --agents opencode --scope global --skills none --mcp");

        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());
        Assert.Equal(pinned, await File.ReadAllTextAsync(jsonPath));
        Assert.Equal(before.Append(jsoncPath).Order(StringComparer.Ordinal), GetFilesWithoutCliLogs(workspace.WorkspaceRoot));
        Assert.Equal(
            string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_McpConfigurationDescription,
                AgentCommandStrings.Environment_OpenCode, jsoncPath),
            Assert.Single(interaction.DisplayedMessages).Message);
    }

    [Theory]
    [InlineData("project", false)]
    [InlineData("global", false)]
    [InlineData("project", true)]
    [InlineData("global", true)]
    public async Task AgentInit_OpenCodeSiblingJson_RepairsInheritedDeprecatedCommand(string scopeName, bool partialOverride)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var homeDirectory = workspace.CreateDirectory("fake-home");
        var environment = new TestEnvironment();
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(workspace.WorkspaceRoot, homeDirectory: homeDirectory);
        var scope = scopeName == "project" ? AgentConfigurationScope.Project : AgentConfigurationScope.Global;
        var jsoncPath = scope is AgentConfigurationScope.Project ? OpenCodePaths.GetMcpConfigPath(workspace.WorkspaceRoot)
            : OpenCodePaths.GetGlobalMcpConfigPath(executionContext, environment);
        var jsonPath = Path.ChangeExtension(jsoncPath, ".json");
        const string original = """{"preference":true,"mcp":{"aspire":{"type":"local","command":["aspire","mcp","start"],"enabled":false},"other":{"command":["other"]}}}""";
        var jsonc = partialOverride
            ? """
                {
                    // Override settings, but inherit the command from opencode.json.
                    "model": "keep-jsonc",
                    "mcp": { "aspire": { "enabled": true, "environment": { "CUSTOM": "keep" }, }, "other": { "command": ["override-other"], }, },
                }
                """
            : """
                {
                    // Higher-precedence settings must not acquire a shadowing Aspire entry.
                    "model": "keep-jsonc",
                    "mcp": { "other": { "command": ["override-other"], }, },
                }
                """;
        Directory.CreateDirectory(Path.GetDirectoryName(jsoncPath)!);
        await File.WriteAllTextAsync(jsonPath, original);
        await File.WriteAllTextAsync(jsoncPath, jsonc);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => executionContext;
        });
        services.AddSingleton<IEnvironment>(environment);
        using var provider = services.BuildServiceProvider();
        var before = GetFilesWithoutCliLogs(workspace.WorkspaceRoot);

        var result = provider.GetRequiredService<RootCommand>().Parse(
            $"agent init --agents opencode --scope {scopeName} --skills none --mcp");

        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());
        var updated = await File.ReadAllTextAsync(jsonPath);
        Assert.Equal(jsonc, await File.ReadAllTextAsync(jsoncPath));
        Assert.Equal(before, GetFilesWithoutCliLogs(workspace.WorkspaceRoot));
        await Verify(updated, "json").UseParameters(scopeName, partialOverride);
    }

    [Theory]
    [InlineData("project", "{ malformed")]
    [InlineData("global", "{ malformed")]
    [InlineData("project", """{"mcp":[]}""")]
    [InlineData("global", """{"mcp":[]}""")]
    public async Task AgentInit_OpenCodeInvalidSiblingJson_FailsWithoutWriting(string scopeName, string malformed)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var homeDirectory = workspace.CreateDirectory("fake-home");
        var environment = new TestEnvironment();
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(workspace.WorkspaceRoot, homeDirectory: homeDirectory);
        var scope = scopeName == "project" ? AgentConfigurationScope.Project : AgentConfigurationScope.Global;
        var jsoncPath = scope is AgentConfigurationScope.Project ? OpenCodePaths.GetMcpConfigPath(workspace.WorkspaceRoot)
            : OpenCodePaths.GetGlobalMcpConfigPath(executionContext, environment);
        var jsonPath = Path.ChangeExtension(jsoncPath, ".json");
        const string original = """{"model":"keep-jsonc"}""";
        Directory.CreateDirectory(Path.GetDirectoryName(jsoncPath)!);
        await File.WriteAllTextAsync(jsoncPath, original);
        await File.WriteAllTextAsync(jsonPath, malformed);
        var interaction = new TestInteractionService();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => executionContext;
            options.InteractionServiceFactory = _ => interaction;
        });
        services.AddSingleton<IEnvironment>(environment);
        using var provider = services.BuildServiceProvider();
        var before = GetFilesWithoutCliLogs(workspace.WorkspaceRoot);

        var result = provider.GetRequiredService<RootCommand>().Parse(
            $"agent init --agents opencode --scope {scopeName} --skills none --mcp");

        Assert.Equal(CliExitCodes.InvalidCommand, await result.InvokeAsync().DefaultTimeout());
        Assert.Contains(jsonPath, Assert.Single(interaction.DisplayedErrors));
        Assert.Equal(original, await File.ReadAllTextAsync(jsoncPath));
        Assert.Equal(malformed, await File.ReadAllTextAsync(jsonPath));
        Assert.Equal(before, GetFilesWithoutCliLogs(workspace.WorkspaceRoot));
    }

    [Theory]
    [InlineData("OPENCODE_CONFIG_DIR", "custom-opencode", "custom-opencode")]
    [InlineData("XDG_CONFIG_HOME", "custom-xdg", "custom-xdg/opencode")]
    public async Task AgentInit_OpenCodeGlobalDirectoryOverrides_UseExistingJsonConfiguration(
        string variable, string value, string relativeDirectory)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var configDirectory = workspace.CreateDirectory(relativeDirectory.Replace('/', Path.DirectorySeparatorChar));
        var configPath = Path.Combine(configDirectory.FullName, "opencode.json");
        const string pinned = """{"mcp":{"aspire":{"command":["pinned-aspire"]}}}""";
        await File.WriteAllTextAsync(configPath, pinned);
        var environment = new TestEnvironment(new Dictionary<string, string?> { [variable] = value });
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        services.AddSingleton<IEnvironment>(environment);
        using var provider = services.BuildServiceProvider();
        var before = GetFilesWithoutCliLogs(workspace.WorkspaceRoot);

        var result = provider.GetRequiredService<RootCommand>().Parse("agent init --agents opencode --scope global --skills none --mcp");

        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());
        Assert.Equal(pinned, await File.ReadAllTextAsync(configPath));
        Assert.Equal(before, GetFilesWithoutCliLogs(workspace.WorkspaceRoot));
    }

    [Fact]
    public async Task AgentInit_PinnedGlobalMcpOverrides_ArePreserved()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var copilot = workspace.CreateDirectory("custom-copilot");
        var claude = workspace.CreateDirectory("custom-claude");
        var openCode = workspace.CreateDirectory("custom-opencode");
        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            ["COPILOT_HOME"] = copilot.FullName,
            ["CLAUDE_CONFIG_DIR"] = claude.FullName,
            ["OPENCODE_CONFIG"] = Path.Combine(openCode.FullName, "custom.json")
        });
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        services.AddSingleton<IEnvironment>(environment);
        using var provider = services.BuildServiceProvider();
        var originals = new Dictionary<string, string>();
        foreach (var client in provider.GetServices<IAgentClient>())
        {
            var path = client.GetMcpConfigPath(AgentConfigurationScope.Global, workspace.WorkspaceRoot);
            var content = string.Equals(client.Id, "opencode", StringComparisons.AgentClientId)
                ? """{ "mcp": { "aspire": { "command": ["pinned-aspire"] } }, "preference": true }"""
                : """{ "mcpServers": { "aspire": { "command": "pinned-aspire" } }, "preference": true }""";
            originals.Add(path, content);
            await File.WriteAllTextAsync(path, content);
        }

        var before = GetFilesWithoutCliLogs(workspace.WorkspaceRoot);
        var result = provider.GetRequiredService<RootCommand>().Parse("agent init --agents all --scope global --skills none --mcp");
        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());

        foreach (var (path, content) in originals)
        {
            Assert.Equal(content, await File.ReadAllTextAsync(path));
        }
        Assert.Equal(before, GetFilesWithoutCliLogs(workspace.WorkspaceRoot));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AgentInit_AliasedMcpDestinations_ConfigureEachSelectedServerContainer(bool pinned)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var homeDirectory = workspace.CreateDirectory("fake-home");
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(workspace.WorkspaceRoot, homeDirectory: homeDirectory);
        var path = Path.Combine(homeDirectory.FullName, ".copilot", "mcp-config.json");
        var environment = new TestEnvironment(new Dictionary<string, string?> { ["OPENCODE_CONFIG"] = path });
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var original = pinned
            ? """{"preference":true,"mcpServers":{"aspire":{"command":"pinned-aspire","args":["--pinned"]}}}"""
            : """{"preference":true}""";
        await File.WriteAllTextAsync(path, original);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => executionContext;
        });
        services.AddSingleton<IEnvironment>(environment);
        using var provider = services.BuildServiceProvider();
        var before = GetFilesWithoutCliLogs(workspace.WorkspaceRoot);

        var result = provider.GetRequiredService<RootCommand>().Parse(
            "agent init --agents copilot,opencode --scope global --skills none --mcp");

        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());
        Assert.Equal(before, GetFilesWithoutCliLogs(workspace.WorkspaceRoot));
        await Verify(await File.ReadAllTextAsync(path), "json").UseParameters(pinned);
    }

    [Fact]
    public async Task AgentInit_AliasedMalformedMcpContainer_FailsWithoutWriting()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var homeDirectory = workspace.CreateDirectory("fake-home");
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(workspace.WorkspaceRoot, homeDirectory: homeDirectory);
        var path = Path.Combine(homeDirectory.FullName, ".copilot", "mcp-config.json");
        var environment = new TestEnvironment(new Dictionary<string, string?> { ["OPENCODE_CONFIG"] = path });
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        const string original = """{ "preference": true, "mcp": [] }""";
        await File.WriteAllTextAsync(path, original);
        var interaction = new TestInteractionService();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => executionContext;
            options.InteractionServiceFactory = _ => interaction;
        });
        services.AddSingleton<IEnvironment>(environment);
        using var provider = services.BuildServiceProvider();
        var before = GetFilesWithoutCliLogs(workspace.WorkspaceRoot);

        var result = provider.GetRequiredService<RootCommand>().Parse(
            "agent init --agents copilot,opencode --scope global --skills none --mcp");

        Assert.Equal(CliExitCodes.InvalidCommand, await result.InvokeAsync().DefaultTimeout());
        Assert.Contains(path, Assert.Single(interaction.DisplayedErrors));
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.Equal(before, GetFilesWithoutCliLogs(workspace.WorkspaceRoot));
    }

    [Theory]
    [MemberData(nameof(MalformedMcpConfigurations))]
    public async Task AgentInit_MalformedSelectedMcp_FailsWithoutOverwriting(string clientId, string scopeName, string original)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var homeDirectory = workspace.CreateDirectory("fake-home");
        var environment = new TestEnvironment();
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(workspace.WorkspaceRoot, homeDirectory: homeDirectory);
        var interaction = new TestInteractionService();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => executionContext;
            options.InteractionServiceFactory = _ => interaction;
        });
        services.AddSingleton<IEnvironment>(environment);
        using var provider = services.BuildServiceProvider();
        var client = provider.GetServices<IAgentClient>().Single(client => string.Equals(client.Id, clientId, StringComparisons.AgentClientId));
        var scope = scopeName == "project" ? AgentConfigurationScope.Project : AgentConfigurationScope.Global;
        var path = client.GetMcpConfigPath(scope, workspace.WorkspaceRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, original);
        var before = GetFilesWithoutCliLogs(workspace.WorkspaceRoot);

        var result = provider.GetRequiredService<RootCommand>().Parse(
            $"agent init --agents {clientId} --scope {scopeName} --skills none --mcp");
        Assert.Equal(CliExitCodes.InvalidCommand, await result.InvokeAsync().DefaultTimeout());
        Assert.Contains(path, Assert.Single(interaction.DisplayedErrors));
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.Equal(before, GetFilesWithoutCliLogs(workspace.WorkspaceRoot));
    }

    public static IEnumerable<object[]> MalformedMcpConfigurations()
    {
        foreach (var client in new[] { "copilot", "claude", "opencode" })
        {
            foreach (var scope in new[] { "project", "global" })
            {
                foreach (var content in new[] { "{ malformed", "", "[]", "null" })
                {
                    yield return [client, scope, content];
                }
            }
        }

        yield return ["copilot", "project", """{"mcpServers":[]}"""];
        yield return ["opencode", "global", """{"mcp":"custom"}"""];
    }

    [Theory]
    [InlineData("copilot", "project")]
    [InlineData("claude", "project")]
    [InlineData("opencode", "project")]
    [InlineData("copilot", "global")]
    [InlineData("claude", "global")]
    [InlineData("opencode", "global")]
    public async Task AgentInit_DeprecatedMcpCommand_RepairsSelectedConfigurationWithoutReplacingOtherSettings(string clientId, string scopeName)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var homeDirectory = workspace.CreateDirectory("fake-home");
        var environment = new TestEnvironment();
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(workspace.WorkspaceRoot, homeDirectory: homeDirectory);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => executionContext;
        });
        services.AddSingleton<IEnvironment>(environment);
        using var provider = services.BuildServiceProvider();
        var client = provider.GetServices<IAgentClient>().Single(client => string.Equals(client.Id, clientId, StringComparisons.AgentClientId));
        var scope = scopeName == "project" ? AgentConfigurationScope.Project : AgentConfigurationScope.Global;
        var path = client.GetMcpConfigPath(scope, workspace.WorkspaceRoot);
        var original = clientId == "opencode"
            ? """{"preference":true,"mcp":{"aspire":{"type":"local","command":["aspire","mcp","start"],"enabled":false},"other":{"command":["other"]}}}"""
            : """{"preference":true,"mcpServers":{"aspire":{"command":"aspire","args":["mcp","start"],"env":{"CUSTOM":"keep"}},"other":{"command":"other"}}}""";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, original);

        var result = provider.GetRequiredService<RootCommand>().Parse(
            $"agent init --agents {clientId} --scope {scopeName} --skills none --mcp");
        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());
        await Verify(await File.ReadAllTextAsync(path), "json").UseParameters(clientId, scopeName);
    }

    [Theory]
    [InlineData("copilot", "global", """{"command":"aspire","args":["mcp","start","--custom"],"env":{"CUSTOM":"keep"}}""")]
    [InlineData("opencode", "project", """{"type":"local","command":["aspire","mcp","start","--custom"],"enabled":false}""")]
    [InlineData("copilot", "project", """{"command":"aspire","args":["mcp",42],"env":{"CUSTOM":"keep"}}""")]
    [InlineData("opencode", "global", """{"type":"local","command":["aspire",{},"start"],"enabled":false}""")]
    [InlineData("copilot", "project", """{"command":"custom-aspire","args":["mcp","start"],"env":{"CUSTOM":"keep"}}""")]
    public async Task AgentInit_CustomizedDeprecatedMcpCommand_IsPreserved(string clientId, string scopeName, string serverConfiguration)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var homeDirectory = workspace.CreateDirectory("fake-home");
        var environment = new TestEnvironment();
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(workspace.WorkspaceRoot, homeDirectory: homeDirectory);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => executionContext;
        });
        services.AddSingleton<IEnvironment>(environment);
        using var provider = services.BuildServiceProvider();
        var client = provider.GetServices<IAgentClient>().Single(client => string.Equals(client.Id, clientId, StringComparisons.AgentClientId));
        var scope = scopeName == "project" ? AgentConfigurationScope.Project : AgentConfigurationScope.Global;
        var path = client.GetMcpConfigPath(scope, workspace.WorkspaceRoot);
        var serversPropertyName = clientId == "opencode" ? "mcp" : "mcpServers";
        var original = $$"""{ "preference": true, "{{serversPropertyName}}": { "aspire": {{serverConfiguration}} } }""";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, original);

        var result = provider.GetRequiredService<RootCommand>().Parse(
            $"agent init --agents {clientId} --scope {scopeName} --skills none --mcp");

        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());
        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData("project")]
    [InlineData("global")]
    public async Task AgentInit_OpenCodeJsonc_PreservesExistingSettingsWhenAddingServer(string scopeName)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var homeDirectory = workspace.CreateDirectory("fake-home");
        var environment = new TestEnvironment();
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(workspace.WorkspaceRoot, homeDirectory: homeDirectory);
        var scope = scopeName == "project" ? AgentConfigurationScope.Project : AgentConfigurationScope.Global;
        var path = scope is AgentConfigurationScope.Project ? OpenCodePaths.GetMcpConfigPath(workspace.WorkspaceRoot)
            : OpenCodePaths.GetGlobalMcpConfigPath(executionContext, environment);
        const string original =
            """
            {
                // An existing schema must not be replaced.
                "$schema": "https://example.test/custom-schema.json",
                "quoted": "a \" // text inside a string",
                /* Block comments are valid JSONC too. */
                "mcp": {
                    "other": { "url": "https://example.test/a//b", },
                },
            }
            """;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, original);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => executionContext;
        });
        services.AddSingleton<IEnvironment>(environment);
        using var provider = services.BuildServiceProvider();

        var result = provider.GetRequiredService<RootCommand>().Parse(
            $"agent init --agents opencode --scope {scopeName} --skills none --mcp");
        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());
        await Verify(await File.ReadAllTextAsync(path), "json").UseParameters(scopeName);
    }

    [Theory]
    [InlineData("project")]
    [InlineData("global")]
    public async Task AgentInit_PinnedOpenCodeJsonc_PreservesCommentsAndConfiguration(string scopeName)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var homeDirectory = workspace.CreateDirectory("fake-home");
        var environment = new TestEnvironment();
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(workspace.WorkspaceRoot, homeDirectory: homeDirectory);
        var scope = scopeName == "project" ? AgentConfigurationScope.Project : AgentConfigurationScope.Global;
        var path = scope is AgentConfigurationScope.Project ? OpenCodePaths.GetMcpConfigPath(workspace.WorkspaceRoot)
            : OpenCodePaths.GetGlobalMcpConfigPath(executionContext, environment);
        const string original =
            """
            {
                // Keep the user's pinned command and formatting.
                "mcp": { "aspire": { "command": ["custom-aspire", "--pinned", ], "enabled": false, }, },
            }
            """;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, original);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => executionContext;
        });
        services.AddSingleton<IEnvironment>(environment);
        using var provider = services.BuildServiceProvider();
        var before = GetFilesWithoutCliLogs(workspace.WorkspaceRoot);

        var result = provider.GetRequiredService<RootCommand>().Parse(
            $"agent init --agents opencode --scope {scopeName} --skills none --mcp");
        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.Equal(before, GetFilesWithoutCliLogs(workspace.WorkspaceRoot));
    }

    [Theory]
    [InlineData("project")]
    [InlineData("global")]
    public async Task AgentInit_Hooks_AreLimitedToSelectedDetectedEnvironmentsInGlobalScope(string scope)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var hooks = new TestTelemetryHookConfigurator();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AgentEnvironmentDetectorFactory = _ => new TestAgentEnvironmentDetector
            {
                DetectedEnvironments = AgentEnvironment.CopilotApp | AgentEnvironment.ClaudeCode | AgentEnvironment.OpenCode
            };
            options.TelemetryHookConfiguratorFactory = _ => hooks;
        });
        using var provider = services.BuildServiceProvider();

        var result = provider.GetRequiredService<RootCommand>().Parse($"agent init --agents claude --scope {scope} --skills aspire --aspire-source embedded");
        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());
        Assert.Equal(scope == "global" ? AgentEnvironment.ClaudeCode : AgentEnvironment.None, hooks.ConfiguredEnvironments);
    }

    [Fact]
    public async Task McpInit_LegacyDelegation_UsesClientScopeAndCopiedSkillOptions()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper).BuildServiceProvider();

        var result = provider.GetRequiredService<RootCommand>().Parse("mcp init --agents claude --scope project --skills aspire --aspire-source embedded --mcp");
        Assert.Empty(result.Errors);
        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());
        Assert.True(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, ".claude", "skills", "aspire", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, ".mcp.json")));
    }

    [Theory]
    [InlineData("remote", "copied")]
    [InlineData("remote", "shared")]
    [InlineData("remote", "unreadable")]
    [InlineData("embedded", "remote")]
    [InlineData("embedded", "malformed")]
    public async Task ExistingSources_WarnForCopiedSkillsButStopForInvalidOrEnabledRemoteSettings(string source, string existing)
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        var hasCopiedSkills = existing is "copied" or "shared";
        var path = hasCopiedSkills || existing == "unreadable"
            ? Path.Combine(context.Home.FullName, ".agents", "skills", "aspire", "SKILL.md")
            : Path.Combine(context.CopilotDirectory, "settings.json");
        var content = existing switch
        {
            "copied" or "shared" => "My customized Aspire instructions.",
            "remote" => """{"enabledPlugins":{"aspire@aspire-skills":true}}""",
            _ => "{broken"
        };
        if (existing == "unreadable")
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            await AgentConfigurationTestContext.WriteAsync(path, content);
        }
        var projectDirectory = existing == "shared" ? ".claude" : ".agents";
        var projectSkillPath = Path.Combine(context.Project.FullName, projectDirectory, "skills", "aspire-init", "SKILL.md");
        if (hasCopiedSkills)
        {
            await AgentConfigurationTestContext.WriteAsync(projectSkillPath, content);
        }
        var interaction = new TestInteractionService();
        var hooks = new TestTelemetryHookConfigurator();
        var services = CliTestHelper.CreateServiceCollection(context.Workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => context.ExecutionContext;
            options.InteractionServiceFactory = _ => interaction;
            options.AgentEnvironmentDetectorFactory = _ => new TestAgentEnvironmentDetector { DetectedEnvironments = AgentEnvironment.CopilotCli };
            options.NpmRunnerFactory = _ => new FakeNpmRunner { IsAvailable = false };
            options.TelemetryHookConfiguratorFactory = _ => hooks;
        });
        services.AddSingleton<IEnvironment>(context.Environment);
        using var provider = services.BuildServiceProvider();
        var before = GetFilesWithoutCliLogs(context.Workspace.WorkspaceRoot);

        var result = provider.GetRequiredService<RootCommand>().Parse(
            $"agent init --agents copilot --scope global --skills all --aspire-source {source} --mcp");

        Assert.Equal(hasCopiedSkills ? CliExitCodes.Success : CliExitCodes.InvalidCommand,
            await result.InvokeAsync().DefaultTimeout());
        if (existing == "unreadable")
        {
            Assert.True(Directory.Exists(path));
        }
        else
        {
            Assert.Equal(content, await File.ReadAllTextAsync(path));
        }
        if (hasCopiedSkills)
        {
            Assert.Empty(interaction.DisplayedErrors);
            Assert.Contains(interaction.DisplayedMessages, message => message.Emoji.Equals(KnownEmojis.Warning) &&
                message.Message == string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_LocalSkillsConflict,
                    string.Join(System.Environment.NewLine,
                        $"  {projectDirectory}/skills/aspire-init/SKILL.md",
                        "  ~/.agents/skills/aspire/SKILL.md")));
            Assert.Equal(content, await File.ReadAllTextAsync(projectSkillPath));
            Assert.True(File.Exists(context.Copilot.GetIntegrationConfigPath(AgentConfigurationScope.Global, context.Project)));
            Assert.True(File.Exists(context.Copilot.GetMcpConfigPath(AgentConfigurationScope.Global, context.Project)));
            Assert.False(File.Exists(context.ClaudeCode.GetIntegrationConfigPath(AgentConfigurationScope.Global, context.Project)));
            Assert.Equal(AgentEnvironment.CopilotCli, hooks.ConfiguredEnvironments);
        }
        else
        {
            Assert.Single(interaction.DisplayedErrors);
            Assert.Equal(before, GetFilesWithoutCliLogs(context.Workspace.WorkspaceRoot));
            Assert.Equal(AgentEnvironment.None, hooks.ConfiguredEnvironments);
        }
    }

    private static string[] GetFilesWithoutCliLogs(DirectoryInfo workspaceRoot)
    {
        // Agent no-op assertions still allow the CLI's normal diagnostic log writes.
        var logDirectory = Path.Combine(".aspire", "logs") + Path.DirectorySeparatorChar;
        return [.. Directory.EnumerateFiles(workspaceRoot.FullName, "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(workspaceRoot.FullName, path).StartsWith(logDirectory, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)];
    }
}
