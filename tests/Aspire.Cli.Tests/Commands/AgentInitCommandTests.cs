// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using Aspire.Cli.Agents;
using Aspire.Cli.Commands;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.Agents;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Spectre.Console;
using RootCommand = Aspire.Cli.Commands.RootCommand;

namespace Aspire.Cli.Tests.Commands;

public class AgentInitCommandTests(ITestOutputHelper outputHelper) : IDisposable
{
    private readonly TemporaryWorkspace _workspace = TemporaryWorkspace.CreateForCli(outputHelper);
    private readonly TestTelemetryHookConfigurator _hooks = new();

    [Theory]
    [InlineData("", true)]
    [InlineData("--aspire-skills y", true)]
    [InlineData("--aspire-skills n", false)]
    public async Task AgentInitCommand_PluginAdvice_IsShownOnceBeforeItsConfirmation(string option, bool expectRisk)
    {
        var events = new List<string>();
        var interaction = new TestInteractionService
        {
            DisplayMessageCallback = (_, message, _) => events.Add(message),
            DisplaySubtleMessageCallback = events.Add,
            ConfirmCallback = (prompt, _) =>
            {
                events.Add(prompt);
                return false;
            }
        };
        using var provider = CreateServices(options =>
        {
            options.InteractionServiceFactory = _ => interaction;
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();
        }).BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse($"agent init --agent none {option}").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(expectRisk ? 1 : 0, events.Count(message => message == AgentCommandStrings.InitCommand_PluginRiskWarning));
        var alternative = string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_DirectSkillsAlternative,
            "https://github.com/microsoft/aspire-skills");
        Assert.Equal(1, events.Count(message => message == alternative));
        if (option.Length == 0)
        {
            Assert.True(events.IndexOf(AgentCommandStrings.InitCommand_PluginRiskWarning) <
                events.IndexOf(AgentCommandStrings.InitCommand_ConfigureAspireSkillsPrompt));
            Assert.True(events.IndexOf(alternative) < events.IndexOf(AgentCommandStrings.InitCommand_ConfigureAspireSkillsPrompt));
        }
        Assert.Empty(_hooks.Requests);
    }

    [Theory]
    [InlineData("project", true, true)]
    [InlineData("user", true, true)]
    [InlineData("project", false, false)]
    public async Task AgentInitCommand_WarnsAboutLocalSkillsBeforeRegistrationWithoutChangingThem(string scope, bool plugin, bool warns)
    {
        var path = Path.Combine(_workspace.WorkspaceRoot.FullName, ".github", "skills", "aspire", "SKILL.md");
        await AgentConfigurationTestContext.WriteAsync(path, "Customized local instructions.");
        var timestamp = File.GetLastWriteTimeUtc(path);
        var messages = new List<string>();
        var interaction = new TestInteractionService { DisplayMessageCallback = (_, message, _) => messages.Add(message) };
        _hooks.PlanCallback = _ =>
        {
            messages.Add("configure");
            return [];
        };
        using var provider = CreateServices(options =>
        {
            options.InteractionServiceFactory = _ => interaction;
            options.GitRepositoryFactory = _ => new TestGitRepository
            {
                GetRootAsyncCallback = _ => Task.FromResult<DirectoryInfo?>(_workspace.WorkspaceRoot)
            };
        }).BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse($"agent init --agent copilot --scope {scope} --mcp y --aspire-skills {(plugin ? "y" : "n")} --non-interactive")
            .InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        var warning = string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_LocalSkillsConflict, path);
        Assert.Equal(warns ? 1 : 0, messages.Count(message => message == warning));
        if (warns)
        {
            Assert.True(messages.IndexOf(warning) < messages.IndexOf("configure"));
        }
        Assert.Equal("Customized local instructions.", await File.ReadAllTextAsync(path));
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
    }

    [Theory]
    [InlineData("", "Project")]
    [InlineData("--scope project", "Project")]
    [InlineData("--scope PROJECT", "Project")]
    [InlineData("--scope user", "User")]
    [InlineData("--scope UsEr", "User")]
    public async Task AgentInitCommand_ResolvesExplicitOrDefaultScopeWithoutPrompts(string arguments, string expectedScope)
    {
        var interaction = new TestInteractionService
        {
            PromptForSelectionsCallback = (_, _, _, _) => throw new InvalidOperationException("Explicit agents must not prompt."),
            PromptForSelectionCallback = (_, _, _, _) => throw new InvalidOperationException("Non-interactive scope resolution must not prompt.")
        };
        using var provider = CreateServices(options => options.InteractionServiceFactory = _ => interaction).BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse($"agent init --agent copilot --non-interactive {arguments}").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(Enum.Parse<AgentConfigurationScope>(expectedScope), Assert.Single(_hooks.Requests).Scope);
    }

    [Fact]
    public async Task AgentInitCommand_EditorHint_SelectsCopilotWithoutInventingCliDetection()
    {
        using var provider = CreateServices(options =>
        {
            options.AgentEnvironments = TestAgentEnvironmentScanner.CreateEnvironments(new AgentClientDetection(AgentClientKind.VsCode, "1.120.0", false));
        }).BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>().Parse("agent init --non-interactive").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        var request = Assert.Single(_hooks.Requests);
        Assert.Equal("copilot", Assert.Single(request.Environments).Id);
        Assert.Equal(AgentClientKind.VsCode, Assert.Single(request.Detections).Client);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData(" y", true)]
    [InlineData(" TrUe", true)]
    [InlineData("=N", false)]
    [InlineData(" FaLsE", false)]
    [InlineData("=false", false)]
    public void AgentInitCommand_AssetOptions_AcceptBooleanValues(string suffix, bool expected)
    {
        using var provider = CreateServices().BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        foreach (var option in AssetOptions)
        {
            var parseResult = command.Parse($"agent init {option.Name}{suffix}");

            Assert.Empty(parseResult.Errors);
            var (wasProvided, value) = GetAssetBinding(provider, parseResult, option).Resolve();
            Assert.True(wasProvided);
            Assert.Equal(expected, value);
            Assert.All(GlobalBooleanOptions, global => Assert.False(parseResult.GetValue(global)));
        }
    }

    [Fact]
    public void AgentInitCommand_OmittedAssetOptions_RemainUnspecified()
    {
        using var provider = CreateServices().BuildServiceProvider();
        var parseResult = provider.GetRequiredService<RootCommand>().Parse("agent init");

        Assert.Empty(parseResult.Errors);
        Assert.All(AssetOptions, option => Assert.False(GetAssetBinding(provider, parseResult, option).Resolve().WasProvided));
    }

    [Fact]
    public void AgentInitCommand_BareAssetFlags_DoNotConsumeFollowingGlobalOptions()
    {
        using var provider = CreateServices().BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        foreach (var option in AssetOptions)
        {
            var parseResult = command.Parse($"agent init {option.Name} --non-interactive");

            Assert.Empty(parseResult.Errors);
            var (wasProvided, value) = GetAssetBinding(provider, parseResult, option).Resolve();
            Assert.True(wasProvided);
            Assert.True(value);
            Assert.True(parseResult.GetValue(RootCommand.NonInteractiveOption));
        }
    }

    [Theory]
    [InlineData("--mcp maybe")]
    [InlineData("--playwright 1")]
    [InlineData("--dotnet-inspect yes")]
    [InlineData("--aspire-skills no")]
    [InlineData("--skills all")]
    [InlineData("--skill-locations all")]
    [InlineData("--unknown-asset")]
    [InlineData("--clients copilot")]
    [InlineData("--environments copilot")]
    [InlineData("--scope global")]
    [InlineData("--scope 1")]
    [InlineData("--scope")]
    public async Task AgentInitCommand_InvalidOptions_FailBeforeDiscoveryOrConfiguration(string arguments)
    {
        using var provider = CreateServices().BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var detectors = provider.GetServices<IAgentEnvironmentScanner>().Cast<TestAgentEnvironmentScanner>();
        var parseResult = command.Parse($"agent init {arguments}");

        Assert.NotEmpty(parseResult.Errors);
        var exitCode = await parseResult.InvokeAsync().DefaultTimeout();

        Assert.NotEqual(CliExitCodes.Success, exitCode);
        Assert.All(detectors, scanner => Assert.Empty(scanner.Calls));
        Assert.Empty(_hooks.Requests);
    }

    [Fact]
    public async Task AgentInitCommand_Help_DescribesIndependentAssetsAndClients()
    {
        using var provider = CreateServices().BuildServiceProvider();
        using var output = new StringWriter();
        var parseResult = provider.GetRequiredService<RootCommand>().Parse("agent init --help");

        var exitCode = await parseResult.InvokeAsync(new InvocationConfiguration { Output = output }).DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        await Verify(output.ToString().TrimEnd(), "txt");
    }

    [Fact]
    public async Task AgentInitCommand_Interactive_PromptsForAssetsBeforeClients()
    {
        var operations = new List<string>();
        var interaction = new TestInteractionService
        {
            ConfirmCallback = (prompt, defaultValue) =>
            {
                operations.Add(prompt);
                return defaultValue;
            },
            ShowStatusCallback = _ => operations.Add("detect"),
            DisplaySubtleMessageCallback = message =>
            {
                if (message == AgentCommandStrings.InitCommand_EnvironmentSelectionNotice)
                {
                    operations.Add(message);
                }
            },
            PromptForSelectionsCallback = (prompt, choices, formatter, _) =>
            {
                operations.Add(prompt);
                var clients = choices.Cast<IAgentEnvironmentScanner>().ToArray();
                Assert.Equal(["copilot", "claude", "opencode"], clients.Select(client => client.Id));
                Assert.Equal(
                    clients.Select(client => client.DisplayName.EscapeMarkup() + Environment.NewLine + "  " + client.Description.EscapeMarkup()),
                    clients.Select(client => formatter(client)));
                return [clients.Single(client => client.Id == "claude")];
            },
            PromptForSelectionCallback = (prompt, choices, _, _) =>
            {
                operations.Add(prompt);
                Assert.Equal([AgentConfigurationScope.Project, AgentConfigurationScope.User], choices.Cast<AgentConfigurationScope>());
                return AgentConfigurationScope.Project;
            }
        };
        _hooks.PlanCallback = _ =>
        {
            operations.Add("configure");
            return [];
        };
        var services = CreateServices(options =>
        {
            options.InteractionServiceFactory = _ => interaction;
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();
            options.AgentEnvironments = TestAgentEnvironmentScanner.CreateEnvironments();
        });
        using var provider = services.BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>().Parse("agent init").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(
            [
                AgentCommandStrings.InitCommand_ConfigureMcpServerPrompt,
                McpCommandStrings.InitCommand_ConfigurePlaywrightPrompt,
                AgentCommandStrings.InitCommand_ConfigureDotnetInspectPrompt,
                AgentCommandStrings.InitCommand_ConfigureAspireSkillsPrompt,
                "detect",
                AgentCommandStrings.InitCommand_EnvironmentSelectionNotice,
                AgentCommandStrings.InitCommand_SelectAgentsPrompt,
                AgentCommandStrings.InitCommand_SelectScopePrompt,
                "configure"
            ],
            operations);
        Assert.Equal([false, false, false, true], interaction.BooleanPromptCalls.Select(call => call.DefaultValue));
        var request = Assert.Single(_hooks.Requests);
        Assert.Equal(new AgentAssetSelection(false, false, false, true), request.Assets);
        Assert.Equal(["claude"], request.Environments.Select(client => client.Id));
        Assert.Empty(request.Detections);
        Assert.Equal(AgentConfigurationScope.Project, request.Scope);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task AgentInitCommand_ScopeChoices_ShowSelectedAgentDestinationsWithoutWriting(bool nativeSkills, bool userScope)
    {
        using var context = new AgentConfigurationTestContext(outputHelper);
        context.SetVariable("COPILOT_HOME", Path.Combine(context.Home.FullName, "copilot[work]"));
        context.SetVariable("CLAUDE_CONFIG_DIR", Path.Combine(context.Home.FullName, "claude[work]"));
        var customOpenCode = Path.Combine(context.Workspace.Path, "custom-opencode.jsonc");
        context.SetVariable("OPENCODE_CONFIG", customOpenCode);
        var selectedScope = userScope ? AgentConfigurationScope.User : AgentConfigurationScope.Project;
        string[] existingEntries = [];
        var interaction = new TestInteractionService
        {
            PromptForSelectionsCallback = (_, choices, formatter, _) =>
            {
                var environments = choices.Cast<IAgentEnvironmentScanner>().ToArray();
                Assert.Equal(environments.Select(agent => agent.DisplayName.EscapeMarkup() + Environment.NewLine + "  " + agent.Description.EscapeMarkup()),
                    environments.Select(formatter));
                return environments.Cast<object>().ToArray();
            },
            PromptForSelectionCallback = (prompt, choices, formatter, _) =>
            {
                Assert.Equal(AgentCommandStrings.InitCommand_SelectScopePrompt, prompt);
                Assert.Equal(new[] { Describe(AgentConfigurationScope.Project), Describe(AgentConfigurationScope.User) },
                    choices.Cast<AgentConfigurationScope>().Select(scope => formatter(scope)));
                Assert.Equal(existingEntries, Directory.GetFileSystemEntries(context.Workspace.Path, "*", SearchOption.AllDirectories).Order());
                return selectedScope;
            }
        };
        var services = CliTestHelper.CreateServiceCollection(context.Workspace, outputHelper, options =>
        {
            options.WorkingDirectory = context.Project;
            options.InteractionServiceFactory = _ => interaction;
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();
            options.TelemetryHookConfiguratorFactory = _ => _hooks;
            options.GitRepositoryFactory = _ => new TestGitRepository
            {
                GetRootAsyncCallback = _ => Task.FromResult<DirectoryInfo?>(context.Project)
            };
        });
        services.AddSingleton(context.ExecutionContext);
        services.AddSingleton<IEnvironment>(context.Environment);
        services.RemoveAll<IAgentEnvironmentScanner>();
        foreach (var scanner in context.Environments)
        {
            services.AddSingleton(scanner);
        }
        using var provider = services.BuildServiceProvider();
        var flags = nativeSkills
            ? "--aspire-skills y --playwright n --dotnet-inspect n"
            : "--aspire-skills n --playwright y --dotnet-inspect y";

        var parseResult = provider.GetRequiredService<RootCommand>().Parse($"agent init --workspace-root \"{context.Project.FullName}\" --mcp n {flags}");
        existingEntries = Directory.GetFileSystemEntries(context.Workspace.Path, "*", SearchOption.AllDirectories).Order().ToArray();
        var untouched = userScope ? context.Project : context.Home;
        var untouchedEntries = Directory.GetFileSystemEntries(untouched.FullName, "*", SearchOption.AllDirectories).Order().ToArray();
        var result = await provider.GetRequiredService<AgentInitCommand>().ExecuteCommandAsync(parseResult, CancellationToken.None).DefaultTimeout();

        Assert.Empty(interaction.DisplayedErrors);
        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Equal(selectedScope, Assert.Single(_hooks.Requests).Scope);
        Assert.Equal(untouchedEntries, Directory.GetFileSystemEntries(untouched.FullName, "*", SearchOption.AllDirectories).Order());

        string Describe(AgentConfigurationScope scope)
        {
            var user = scope is AgentConfigurationScope.User;
            var lines = new List<string>
            {
                user ? AgentCommandStrings.InitCommand_UserScope : AgentCommandStrings.InitCommand_ProjectScope
            };
            foreach (var agent in context.Environments)
            {
                var path = nativeSkills
                    ? agent.Id switch
                    {
                        "copilot" => user ? Path.Combine("~", "copilot[work]", "settings.json") : Path.Combine(".github", "copilot", "settings.json"),
                        "claude" => user ? Path.Combine("~", "claude[work]", "settings.json") : Path.Combine(".claude", "settings.json"),
                        _ => user ? customOpenCode : "opencode.json"
                    }
                    : SkillPaths(agent.Id, user);
                lines.Add("  " + string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_EnvironmentLocationDescription,
                    agent.DisplayName, path).EscapeMarkup());
            }
            return string.Join(Environment.NewLine, lines);
        }

        static string SkillPaths(string agent, bool user)
        {
            var directory = agent == "claude" ? user ? "claude[work]" : ".claude" : ".agents";
            var root = user ? Path.Combine("~", directory, "skills") : Path.Combine(directory, "skills");
            return string.Join(", ", Path.Combine(root, "playwright-cli"), Path.Combine(root, "dotnet-inspect"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AgentInitCommand_OmittedClients_SelectsDetectedClients(bool interactive)
    {
        var targetRequests = new List<string>();
        AgentClientDetection[] detections =
        [
            new(AgentClientKind.ClaudeCode, "2.1.0", IsInsiders: false),
            new(AgentClientKind.ClaudeCode, "2.1.0", IsInsiders: false)
        ];
        var services = CreateServices(options =>
        {
            options.AgentEnvironments = TestAgentEnvironmentScanner.CreateEnvironments(detections);
            foreach (var scanner in options.AgentEnvironments)
            {
                scanner.GetTargetsCallback = _ =>
                {
                    targetRequests.Add(scanner.Id);
                    return [];
                };
            }
            if (interactive)
            {
                options.InteractionServiceFactory = _ => new TestInteractionService();
                options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();
            }
        });
        using var provider = services.BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>().Parse("agent init").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        var request = Assert.Single(_hooks.Requests);
        Assert.Equal(["claude"], request.Environments.Select(client => client.Id));
        Assert.Equal(
            detections.Distinct().OrderBy(detection => detection.Client).Select(detection => (detection.Client, detection.Version, detection.IsInsiders)),
            request.Detections.OrderBy(detection => detection.Client).Select(detection => (detection.Client, detection.Version, detection.IsInsiders)));
        Assert.Equal(new AgentAssetSelection(false, false, false, true), request.Assets);
        if (!interactive)
        {
            Assert.Equal(["claude"], targetRequests);
        }
    }

    [Theory]
    [InlineData("COPILOT", "copilot")]
    [InlineData("copilot", "copilot")]
    [InlineData("CLAUDE", "claude")]
    [InlineData("OpenCode", "opencode")]
    public async Task AgentInitCommand_ExplicitClient_CanSelectUndetectedClient(string clientId, string expectedId)
    {
        var targetRequests = new List<string>();
        var services = CreateServices(options =>
        {
            options.AgentEnvironments = TestAgentEnvironmentScanner.CreateEnvironments();
            foreach (var scanner in options.AgentEnvironments)
            {
                scanner.GetTargetsCallback = _ =>
                {
                    targetRequests.Add(scanner.Id);
                    return [];
                };
            }
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();
            options.InteractionServiceFactory = _ => new TestInteractionService
            {
                PromptForSelectionsCallback = (_, _, _, _) => throw new InvalidOperationException("Explicit selection must not open the environment picker.")
            };
        });
        using var provider = services.BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse($"agent init --agent {clientId} --scope project").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        var request = Assert.Single(_hooks.Requests);
        Assert.Equal(expectedId, Assert.Single(request.Environments).Id);
        Assert.Empty(request.Detections);
        Assert.Equal([expectedId], targetRequests);
    }

    [Fact]
    public async Task AgentInitCommand_ExplicitClients_PreservesUnselectedDetectionsForHooks()
    {
        AgentClientDetection[] detections =
        [
            new(AgentClientKind.CopilotCli, "1.0.0", false),
            new(AgentClientKind.ClaudeCode, "2.1.0", false)
        ];
        var services = CreateServices(options =>
        {
            options.AgentEnvironments = TestAgentEnvironmentScanner.CreateEnvironments(detections);
        });
        using var provider = services.BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("agent init --agent opencode --non-interactive").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        var request = Assert.Single(_hooks.Requests);
        Assert.Equal(["opencode"], request.Environments.Select(client => client.Id));
        Assert.Equal(
            detections.Select(detection => (detection.Client, detection.Version, detection.IsInsiders)),
            request.Detections.Select(detection => (detection.Client, detection.Version, detection.IsInsiders)));
    }

    [Theory]
    [InlineData("ALL")]
    [InlineData("copilot,claude,opencode")]
    [InlineData("copilot,claude,opencode,copilot")]
    public async Task AgentInitCommand_ExplicitClients_SelectsFullCatalog(string clients)
    {
        using var provider = CreateServices().BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse($"agent init --agent {clients}").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        var request = Assert.Single(_hooks.Requests);
        Assert.Equal(
            ["copilot", "claude", "opencode"],
            request.Environments.Select(client => client.Id));
        Assert.All(request.Environments, client =>
            Assert.Same(provider.GetServices<IAgentEnvironmentScanner>().Single(entry => entry.Id == client.Id), client));
    }

    [Fact]
    public async Task AgentInitCommand_ScansEnvironmentsOnceAndSnapshotsClientEvidence()
    {
        using var cancellation = new CancellationTokenSource();
        var workingDirectory = _workspace.CreateDirectory("nested");
        AgentEnvironmentScanContext? capturedContext = null;
        var copilot = new TestAgentEnvironmentScanner("copilot", AgentCommandStrings.Environment_Copilot)
        {
            ScanAsyncCallback = (context, _) =>
            {
                capturedContext = context;
                context.AddDetection(new(AgentClientKind.CopilotApp, null, false));
                context.AddDetection(new(AgentClientKind.CopilotCli, "1.0.0", false));
                return Task.CompletedTask;
            }
        };
        var openCode = new TestAgentEnvironmentScanner("opencode", "OpenCode")
        {
            ScanAsyncCallback = (context, _) =>
            {
                context.AddDetection(new(AgentClientKind.OpenCode, "2.0.0", false));
                return Task.CompletedTask;
            }
        };
        var services = CreateServices(options =>
        {
            options.WorkingDirectory = workingDirectory;
            options.GitRepositoryFactory = _ => new TestGitRepository
            {
                GetRootAsyncCallback = _ => Task.FromResult<DirectoryInfo?>(_workspace.WorkspaceRoot)
            };
        });
        services.RemoveAll<IAgentEnvironmentScanner>();
        services.AddSingleton<IAgentEnvironmentScanner>(copilot);
        services.AddSingleton<IAgentEnvironmentScanner>(openCode);
        using var provider = services.BuildServiceProvider();
        var parseResult = provider.GetRequiredService<RootCommand>().Parse("agent init --agent all");

        await provider.GetRequiredService<AgentInitCommand>().ExecuteCommandAsync(parseResult, cancellation.Token).DefaultTimeout();

        var request = Assert.Single(_hooks.Requests);
        Assert.Equal([copilot, openCode], request.Environments);
        Assert.Equal(
            [(AgentClientKind.CopilotApp, null, false), (AgentClientKind.CopilotCli, "1.0.0", false), (AgentClientKind.OpenCode, "2.0.0", false)],
            request.Detections.Select(detection => (detection.Client, detection.Version, detection.IsInsiders)));
        foreach (var environment in new[] { copilot, openCode })
        {
            var call = Assert.Single(environment.Calls);
            Assert.Same(workingDirectory, call.WorkingDirectory);
            Assert.Equal(_workspace.WorkspaceRoot.FullName, call.WorkspaceRoot.FullName);
            Assert.Equal(cancellation.Token, call.CancellationToken);
        }
        Assert.NotNull(capturedContext);
        capturedContext.AddDetection(new(AgentClientKind.ClaudeCode, null, false));
        Assert.Equal(3, request.Detections.Count);
        var snapshot = Assert.IsAssignableFrom<IList<AgentClientDetection>>(request.Detections);
        Assert.True(snapshot.IsReadOnly);
        Assert.Throws<NotSupportedException>(snapshot.Clear);
    }

    [Fact]
    public async Task AgentInitCommand_CancellationDuringScan_DoesNotInvokeRemainingEnvironmentOrConfigure()
    {
        using var cancellation = new CancellationTokenSource();
        var first = new TestAgentEnvironmentScanner("first", "First")
        {
            ScanAsyncCallback = (_, _) =>
            {
                cancellation.Cancel();
                return Task.CompletedTask;
            }
        };
        var second = new TestAgentEnvironmentScanner("second", "Second");
        var services = CreateServices();
        services.RemoveAll<IAgentEnvironmentScanner>();
        services.AddSingleton<IAgentEnvironmentScanner>(first);
        services.AddSingleton<IAgentEnvironmentScanner>(second);
        using var provider = services.BuildServiceProvider();
        var parseResult = provider.GetRequiredService<RootCommand>().Parse("agent init --agent all");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.GetRequiredService<AgentInitCommand>().ExecuteCommandAsync(parseResult, cancellation.Token)).DefaultTimeout();

        Assert.Single(first.Calls);
        Assert.Empty(second.Calls);
        Assert.Empty(_hooks.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AgentInitCommand_CancelledBeforeScan_DoesNotProbeOrConfigure(bool registerEnvironment)
    {
        var environment = new TestAgentEnvironmentScanner("test", "Test");
        var services = CreateServices();
        services.RemoveAll<IAgentEnvironmentScanner>();
        if (registerEnvironment)
        {
            services.AddSingleton<IAgentEnvironmentScanner>(environment);
        }
        using var provider = services.BuildServiceProvider();
        var parseResult = provider.GetRequiredService<RootCommand>().Parse("agent init --agent none");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.GetRequiredService<AgentInitCommand>().ExecuteCommandAsync(parseResult, new CancellationToken(canceled: true))).DefaultTimeout();

        Assert.Empty(environment.Calls);
        Assert.Empty(_hooks.Requests);
    }

    [Fact]
    public async Task AgentInitCommand_NonInteractive_NoDetectedClients_SkipsConfiguration()
    {
        using var error = new StringWriter();
        var services = CreateServices(options =>
        {
            options.AgentEnvironments = TestAgentEnvironmentScanner.CreateEnvironments();
            options.ErrorTextWriter = error;
            options.DisableAnsi = true;
        });
        using var provider = services.BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("agent init --non-interactive").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Empty(error.ToString());
        Assert.Empty(_hooks.Requests);
    }

    [Fact]
    public async Task AgentInitCommand_Interactive_NoDetectedClients_StartsWithNoSelection()
    {
        var services = CreateServices(options =>
        {
            options.AgentEnvironments = TestAgentEnvironmentScanner.CreateEnvironments();
            options.InteractionServiceFactory = _ => new TestInteractionService();
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();
        });
        using var provider = services.BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("agent init").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Empty(_hooks.Requests);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("copilot,unknown")]
    [InlineData("copilot-cli")]
    [InlineData("copilot-app")]
    [InlineData("claude-code")]
    [InlineData("vscode")]
    public async Task AgentInitCommand_UnknownClient_FailsBeforeDiscoveryOrConfiguration(string clients)
    {
        var services = CreateServices(options =>
        {
        });
        using var provider = services.BuildServiceProvider();
        var detectors = provider.GetServices<IAgentEnvironmentScanner>().Cast<TestAgentEnvironmentScanner>();
        var parseResult = provider.GetRequiredService<RootCommand>().Parse($"agent init --mcp y --agent {clients}");

        Assert.NotEmpty(parseResult.Errors);
        var exitCode = await parseResult.InvokeAsync(new InvocationConfiguration { Output = TextWriter.Null, Error = TextWriter.Null }).DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.All(detectors, scanner => Assert.Empty(scanner.Calls));
        Assert.Empty(_hooks.Requests);
        var expectedError = string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_InvalidEnvironments,
            clients, "copilot,claude,opencode", "all", "none");
        Assert.Equal(expectedError, Assert.Single(parseResult.Errors).Message);
    }

    [Fact]
    public async Task AgentInitCommand_AllAssetsDisabled_SkipsDiscoveryAndLeavesExistingFilesUntouched()
    {
        var mcpPath = Path.Combine(_workspace.WorkspaceRoot.FullName, ".mcp.json");
        const string existingMcp = """{"mcpServers":{"aspire":{"command":"aspire","args":["mcp","start"]}}}""";
        await File.WriteAllTextAsync(mcpPath, existingMcp);
        var skillDirectory = _workspace.CreateDirectory(Path.Combine(".agents", "skills", "aspire"));
        var skillPath = Path.Combine(skillDirectory.FullName, "SKILL.md");
        const string existingSkill = "User-managed Aspire skill";
        await File.WriteAllTextAsync(skillPath, existingSkill);
        using var provider = CreateServices().BuildServiceProvider();
        var detectors = provider.GetServices<IAgentEnvironmentScanner>().Cast<TestAgentEnvironmentScanner>();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("agent init --mcp n --playwright false --dotnet-inspect N --aspire-skills=false")
            .InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.All(detectors, scanner => Assert.Empty(scanner.Calls));
        Assert.Empty(_hooks.Requests);
        Assert.Equal(existingMcp, await File.ReadAllTextAsync(mcpPath));
        Assert.Equal(existingSkill, await File.ReadAllTextAsync(skillPath));
    }

    [Fact]
    public async Task AgentInitCommand_ExplicitNoneClients_DoesNotConfigureAnyAssets()
    {
        var mcpPath = Path.Combine(_workspace.WorkspaceRoot.FullName, ".mcp.json");
        const string existingMcp = """{"mcpServers":{"aspire":{"command":"aspire","args":["mcp","start"]}}}""";
        await File.WriteAllTextAsync(mcpPath, existingMcp);
        using var provider = CreateServices(options =>
        {
            options.AgentEnvironments = TestAgentEnvironmentScanner.CreateEnvironments();
        }).BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("agent init --mcp --playwright --dotnet-inspect --aspire-skills --agent NONE")
            .InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Empty(_hooks.Requests);
        Assert.Equal(existingMcp, await File.ReadAllTextAsync(mcpPath));
    }

    [Theory]
    [InlineData("working")]
    [InlineData("git")]
    [InlineData("explicit")]
    public async Task AgentInitCommand_UsesResolvedWorkspaceRoot(string rootSource)
    {
        var workingDirectory = _workspace.CreateDirectory(Path.Combine("repository", "nested"));
        var explicitDirectory = _workspace.CreateDirectory("other workspace");
        var expectedRoot = rootSource switch
        {
            "git" => workingDirectory.Parent!,
            "explicit" => explicitDirectory,
            _ => workingDirectory
        };
        var detectors = TestAgentEnvironmentScanner.CreateEnvironments();
        var services = CreateServices(options =>
        {
            options.WorkingDirectory = workingDirectory;
            options.GitRepositoryFactory = _ => new TestGitRepository
            {
                GetRootAsyncCallback = _ => Task.FromResult(rootSource == "working" ? null : workingDirectory.Parent)
            };
            options.AgentEnvironments = detectors;
        });
        using var provider = services.BuildServiceProvider();
        var arguments = rootSource == "explicit" ? $" --workspace-root \"{explicitDirectory.FullName}\"" : string.Empty;

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse($"agent init --agent copilot{arguments}").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(expectedRoot.FullName, Assert.Single(_hooks.Requests).WorkspaceRoot.FullName);
        var scans = detectors.SelectMany(scanner => scanner.Calls).ToArray();
        Assert.Equal(3, scans.Length);
        Assert.All(scans, scan =>
        {
            Assert.Equal(expectedRoot.FullName, scan.WorkspaceRoot.FullName);
            Assert.Equal(workingDirectory.FullName, scan.WorkingDirectory.FullName);
        });
    }

    [Fact]
    public async Task AgentInitCommand_DotnetInspect_CanBeSelectedBeforeAnAppHostExists()
    {
        using var provider = CreateServices().BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("agent init --dotnet-inspect y --aspire-skills n --agent claude")
            .InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(new AgentAssetSelection(false, false, true, false), Assert.Single(_hooks.Requests).Assets);
        Assert.Empty(_workspace.WorkspaceRoot.GetFiles("*.cs", SearchOption.AllDirectories));
        Assert.Empty(_workspace.WorkspaceRoot.GetFiles("*.csproj", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("Configured", CliExitCodes.Success)]
    [InlineData("Unchanged", CliExitCodes.Success)]
    [InlineData("Skipped", CliExitCodes.Success)]
    [InlineData("Blocked", CliExitCodes.InvalidCommand)]
    [InlineData("Failed", CliExitCodes.InvalidCommand)]
    public async Task AgentInitCommand_ReportsNativeRegistrationStatusWithoutClaimingAcquisition(string status, int expectedExitCode)
    {
        var subtleMessages = new List<string>();
        var interaction = new TestInteractionService { DisplaySubtleMessageCallback = subtleMessages.Add };
        var path = Path.Combine(_workspace.WorkspaceRoot.FullName, "project-settings.json");
        var services = CreateServices(options =>
        {
            options.InteractionServiceFactory = _ => interaction;
            options.AgentEnvironments = TestAgentEnvironmentScanner.CreateEnvironments(
                new(AgentClientKind.CopilotCli, null, false), new(AgentClientKind.CopilotCli, null, false));
            var copilot = options.AgentEnvironments.Single(scanner => scanner.Id == "copilot");
            copilot.GetTargetsCallback = _ =>
                [copilot.CreateTarget(path, AgentAssetKind.AspireSkills, Enum.Parse<AgentConfigurationStatus>(status), "Native client owns acquisition.")];
        });
        using var provider = services.BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>().Parse("agent init").InvokeAsync().DefaultTimeout();

        Assert.Equal(expectedExitCode, exitCode);
        var logFilePath = provider.GetRequiredService<CliExecutionContext>().LogFilePath;
        var targetFormat = status switch
        {
            "Configured" => AgentCommandStrings.InitCommand_RegisteredTarget,
            "Unchanged" => AgentCommandStrings.InitCommand_UnchangedTarget,
            "Skipped" => AgentCommandStrings.InitCommand_SkippedTarget,
            "Blocked" => AgentCommandStrings.InitCommand_BlockedTarget,
            "Failed" => AgentCommandStrings.InitCommand_FailedTarget,
            _ => throw new InvalidOperationException($"Unexpected status: {status}")
        };
        var targetMessage = string.Format(CultureInfo.CurrentCulture, targetFormat,
            AgentCommandStrings.InitCommand_AspireSkillsAsset, AgentCommandStrings.Environment_Copilot,
            AgentCommandStrings.InitCommand_ProjectScope, path);
        string[] expectedMessages = status switch
        {
            "Configured" => [targetMessage],
            "Unchanged" => [],
            "Skipped" => [targetMessage, AgentCommandStrings.ConfigurationCompletedWithWarnings],
            _ =>
            [
                AgentCommandStrings.ConfigurationCompletedWithErrors,
                string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.SeeLogsAt, "<log-file>")
            ]
        };
        string[] expectedErrors = status is "Blocked" or "Failed" ? [targetMessage] : [];
        string[] expectedSuccess = status is "Configured" or "Unchanged" ? [AgentCommandStrings.InitCommand_ConfigurationComplete] : [];
        string[] expectedSubtleMessages = status switch
        {
            "Configured" => ["Native client owns acquisition.", AgentCommandStrings.InitCommand_ClientAcquisitionNotice],
            "Unchanged" => [targetMessage, "Native client owns acquisition.", AgentCommandStrings.InitCommand_ClientAcquisitionNotice],
            _ => ["Native client owns acquisition."]
        };
        Assert.Equal(new[] { AgentCommandStrings.InitCommand_PluginRiskWarning }.Concat(expectedMessages),
            interaction.DisplayedMessages.Select(message => message.Message.Replace(logFilePath, "<log-file>", StringComparison.Ordinal)));
        Assert.Equal(expectedErrors, interaction.DisplayedErrors);
        Assert.Equal(expectedSuccess, interaction.DisplayedSuccess);
        Assert.Equal(new[]
        {
            string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_DirectSkillsAlternative, AspireSkillsPluginConfiguration.RepositoryUrl),
            AgentCommandStrings.InitCommand_EnvironmentSelectionNotice
        }.Concat(expectedSubtleMessages), subtleMessages);
    }

    [Theory]
    [InlineData("Blocked")]
    [InlineData("Failed")]
    public async Task AgentInitCommand_HookFailure_IsAdvisoryWithQualifiedCompletion(string status)
    {
        var subtleMessages = new List<string>();
        var interaction = new TestInteractionService { DisplaySubtleMessageCallback = subtleMessages.Add };
        var projectPath = Path.Combine(_workspace.WorkspaceRoot.FullName, "project-settings.json");
        var hookPath = Path.Combine(_workspace.WorkspaceRoot.FullName, "user-_hooks.json");
        var services = CreateServices(options =>
        {
            options.InteractionServiceFactory = _ => interaction;
            var copilot = options.AgentEnvironments.Single(scanner => scanner.Id == "copilot");
            copilot.GetTargetsCallback = _ =>
                [copilot.CreateTarget(projectPath, AgentAssetKind.AspireSkills, AgentConfigurationStatus.Configured, null)];
            options.TelemetryHookConfiguratorFactory = _ => new TestTelemetryHookConfigurator
            {
                PlanCallback = _ =>
                [
                    copilot.CreateTarget(hookPath, AgentAssetKind.TelemetryHooks, Enum.Parse<AgentConfigurationStatus>(status),
                        "The hook could not be written.") with { Scope = AgentConfigurationScope.User }
                ]
            };
        });
        using var provider = services.BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>().Parse("agent init").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Empty(interaction.DisplayedErrors);
        Assert.Empty(interaction.DisplayedSuccess);
        Assert.Equal(KnownEmojis.Warning, interaction.DisplayedMessages[^1].Emoji);
        Assert.Equal(
        [
            AgentCommandStrings.InitCommand_PluginRiskWarning,
            string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_RegisteredTarget,
                AgentCommandStrings.InitCommand_AspireSkillsAsset, AgentCommandStrings.Environment_Copilot, AgentCommandStrings.InitCommand_ProjectScope, projectPath),
            string.Format(CultureInfo.CurrentCulture, status == "Blocked" ? AgentCommandStrings.InitCommand_BlockedTarget : AgentCommandStrings.InitCommand_FailedTarget,
                AgentCommandStrings.InitCommand_TelemetryHooksAsset, AgentCommandStrings.Environment_Copilot, AgentCommandStrings.InitCommand_UserScope, hookPath),
            AgentCommandStrings.ConfigurationCompletedWithWarnings
        ],
        interaction.DisplayedMessages.Select(message => message.Message));
        Assert.Equal([string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_DirectSkillsAlternative, AspireSkillsPluginConfiguration.RepositoryUrl),
            AgentCommandStrings.InitCommand_EnvironmentSelectionNotice, "The hook could not be written.",
            AgentCommandStrings.InitCommand_ClientAcquisitionNotice], subtleMessages);
    }

    [Fact]
    public async Task AgentInitCommand_ReportsManagedPayloadsAsInstalled()
    {
        var interaction = new TestInteractionService();
        var services = CreateServices(options =>
        {
            options.InteractionServiceFactory = _ => interaction;
            options.AgentEnvironments = TestAgentEnvironmentScanner.CreateEnvironments(new AgentClientDetection(AgentClientKind.ClaudeCode, null, false));
            var claude = options.AgentEnvironments.Single(scanner => scanner.Id == "claude");
            options.AgentSkillInstallerFactory = _ => new TestAgentConfigurationSkillInstaller
            {
                Results =
                [
                    new(AgentAssetKind.Playwright, [claude], "playwright-cli", AgentConfigurationScope.Project, AgentConfigurationStatus.Configured, null),
                    new(AgentAssetKind.DotnetInspect, [claude], "dotnet-inspect", AgentConfigurationScope.Project, AgentConfigurationStatus.Configured, null)
                ]
            };
        });
        using var provider = services.BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("agent init --playwright --dotnet-inspect --aspire-skills n").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(
        [
            string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_InstalledTarget,
                AgentCommandStrings.InitCommand_PlaywrightAsset, "Claude Code", AgentCommandStrings.InitCommand_ProjectScope, "playwright-cli"),
            string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_InstalledTarget,
                AgentCommandStrings.InitCommand_DotnetInspectAsset, "Claude Code", AgentCommandStrings.InitCommand_ProjectScope, "dotnet-inspect")
        ],
        interaction.DisplayedMessages.Select(message => message.Message));
    }

    [Fact]
    public async Task AgentInitCommand_CancellationDuringDiscovery_PropagatesWithoutConfiguration()
    {
        using var cancellation = new CancellationTokenSource();
        var interaction = new TestInteractionService { ShowStatusCallback = _ => cancellation.Cancel() };
        var services = CreateServices(options =>
        {
            options.InteractionServiceFactory = _ => interaction;
        });
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<AgentInitCommand>();
        var parseResult = command.Parse("init");

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => command.ExecuteCommandAsync(parseResult, cancellation.Token)).DefaultTimeout();

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Empty(_hooks.Requests);
    }

    [Fact]
    public async Task AgentInitCommand_ForwardsCancellationToManagedInstallation()
    {
        using var cancellation = new CancellationTokenSource();
        var installer = new TestAgentConfigurationSkillInstaller
        {
            InstallAsyncCallback = (_, token) =>
            {
                Assert.Equal(cancellation.Token, token);
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                throw new InvalidOperationException("Cancellation must propagate.");
            }
        };
        var services = CreateServices(options => options.AgentSkillInstallerFactory = _ => installer);
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<AgentInitCommand>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => command.ExecuteCommandAsync(command.Parse("init --agent copilot --aspire-skills n --dotnet-inspect"), cancellation.Token)).DefaultTimeout();

        Assert.Single(installer.Requests);
    }

    [Theory]
    [InlineData(CliExitCodes.FailedToCreateNewProject, true)]
    [InlineData(CliExitCodes.Success, false)]
    public async Task PromptAndChainAsync_PreviousFailureOrSuppression_SkipsAgentSetup(int previousExitCode, bool acceptSetup)
    {
        var interaction = new TestInteractionService();
        var services = CreateServices(options => options.InteractionServiceFactory = _ => interaction);
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<AgentInitCommand>();
        var detectors = provider.GetServices<IAgentEnvironmentScanner>().Cast<TestAgentEnvironmentScanner>();

        var result = await command.PromptAndChainAsync(
            interaction, previousExitCode, _workspace.WorkspaceRoot, PromptBinding.CreateDefault(acceptSetup),
            command.CreateBindings(command.Parse("init"), includeMcp: true), TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.Equal(previousExitCode, result.ExitCode);
        Assert.Empty(result.RegisteredEnvironments);
        Assert.All(detectors, scanner => Assert.Empty(scanner.Calls));
        Assert.Empty(_hooks.Requests);
        Assert.Equal(previousExitCode == CliExitCodes.Success ? 1 : 0, interaction.BooleanPromptCalls.Count);
    }

    [Fact]
    public async Task PromptAndChainAsync_UsesOutputRootExcludesMcpAndReturnsOnlyRegisteredClients()
    {
        var outputRoot = _workspace.CreateDirectory("output");
        var interaction = new TestInteractionService
        {
            ConfirmCallback = (prompt, defaultValue) => prompt == McpCommandStrings.InitCommand_ConfigurePlaywrightPrompt || defaultValue,
            PromptForSelectionsCallback = (_, choices, _, _) => choices.Cast<object>().ToArray()
        };
        var services = CreateServices(options =>
        {
            options.InteractionServiceFactory = _ => interaction;
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();
            foreach (var scanner in options.AgentEnvironments)
            {
                var status = scanner.Id switch
                {
                    "copilot" => AgentConfigurationStatus.Configured,
                    "claude" => AgentConfigurationStatus.Unchanged,
                    _ => AgentConfigurationStatus.Blocked
                };
                scanner.GetTargetsCallback = request =>
                    [scanner.CreateTarget(Path.Combine(request.WorkspaceRoot.FullName, $"{scanner.Id}.json"),
                        AgentAssetKind.AspireSkills, status, "Native source result.")];
            }
            var openCode = options.AgentEnvironments.Single(scanner => scanner.Id == "opencode");
            options.AgentSkillInstallerFactory = _ => new TestAgentConfigurationSkillInstaller
            {
                Results = [new(AgentAssetKind.Playwright, [openCode], "playwright-cli", AgentConfigurationScope.Project, AgentConfigurationStatus.Configured, null)]
            };
        });
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<AgentInitCommand>();
        var parseResult = command.Parse("init --mcp y");

        var result = await command.PromptAndChainAsync(
            interaction, CliExitCodes.Success, outputRoot, PromptBinding.CreateDefault(true),
            command.CreateBindings(parseResult, includeMcp: true), TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, result.ExitCode);
        Assert.Equal(["copilot", "claude"], result.RegisteredEnvironments.Select(environment => environment.Id));
        var request = Assert.Single(_hooks.Requests);
        Assert.Equal(outputRoot.FullName, request.WorkspaceRoot.FullName);
        Assert.False(request.Assets.Mcp);
        Assert.Equal(
            [SharedCommandStrings.PromptRunAgentInit, McpCommandStrings.InitCommand_ConfigurePlaywrightPrompt,
                AgentCommandStrings.InitCommand_ConfigureDotnetInspectPrompt, AgentCommandStrings.InitCommand_ConfigureAspireSkillsPrompt],
            interaction.BooleanPromptCalls.Select(call => call.PromptText));
    }

    public void Dispose() => _workspace.Dispose();

    private IServiceCollection CreateServices(Action<CliServiceCollectionTestOptions>? configure = null)
        => CliTestHelper.CreateServiceCollection(_workspace, outputHelper, options =>
        {
            options.TelemetryHookConfiguratorFactory = _ => _hooks;
            configure?.Invoke(options);
        });

    private static PromptBinding<bool> GetAssetBinding(IServiceProvider provider, ParseResult parseResult, Option option)
    {
        var bindings = provider.GetRequiredService<AgentInitCommand>().CreateBindings(parseResult, includeMcp: true);

        return option.Name switch
        {
            "--mcp" => bindings.Mcp!,
            "--playwright" => bindings.Playwright,
            "--dotnet-inspect" => bindings.DotnetInspect,
            "--aspire-skills" => bindings.AspireSkills,
            _ => throw new ArgumentOutOfRangeException(nameof(option))
        };
    }

    private static IReadOnlyList<Option> AssetOptions =>
    [
        AgentInitCommand.s_mcpOption,
        AgentInitCommand.s_playwrightOption,
        AgentInitCommand.s_dotnetInspectOption,
        AgentInitCommand.s_aspireSkillsOption
    ];

    private static IReadOnlyList<Option<bool>> GlobalBooleanOptions =>
    [
        RootCommand.DebugOption,
        RootCommand.NonInteractiveOption,
        RootCommand.NoLogoOption,
        RootCommand.BannerOption,
        RootCommand.WaitForDebuggerOption,
        RootCommand.CliWaitForDebuggerOption,
        RootCommand.CaptureProfileOption
    ];
}
