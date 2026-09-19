// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using Aspire.Cli.Agents;
using Aspire.Cli.Commands;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using RootCommand = Aspire.Cli.Commands.RootCommand;

namespace Aspire.Cli.Tests.Commands;

public class AgentInitCommandTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData("", true)]
    [InlineData(" y", true)]
    [InlineData(" Y", true)]
    [InlineData("=y", true)]
    [InlineData("=Y", true)]
    [InlineData(" true", true)]
    [InlineData(" TrUe", true)]
    [InlineData("=true", true)]
    [InlineData("=TrUe", true)]
    [InlineData(" n", false)]
    [InlineData(" N", false)]
    [InlineData("=n", false)]
    [InlineData("=N", false)]
    [InlineData(" false", false)]
    [InlineData(" FaLsE", false)]
    [InlineData("=false", false)]
    [InlineData("=FaLsE", false)]
    public void AgentInitCommand_AssetOptions_AcceptBooleanValues(string suffix, bool expected)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper).BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        foreach (var option in AssetOptions)
        {
            var parseResult = command.Parse($"agent init {option.Name}{suffix}");

            Assert.Empty(parseResult.Errors);
            var (wasProvided, value) = GetAssetBinding(parseResult, option).Resolve();
            Assert.True(wasProvided);
            Assert.Equal(expected, value);
        }
    }

    [Fact]
    public void AgentInitCommand_OmittedAssetOptions_RemainUnspecified()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper).BuildServiceProvider();
        var parseResult = provider.GetRequiredService<RootCommand>().Parse("agent init");

        Assert.Empty(parseResult.Errors);
        Assert.All(AssetOptions, option => Assert.False(GetAssetBinding(parseResult, option).Resolve().WasProvided));
    }

    [Fact]
    public void AgentInitCommand_BareAssetFlags_DoNotConsumeFollowingGlobalOptions()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper).BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        foreach (var option in AssetOptions)
        {
            var parseResult = command.Parse($"agent init {option.Name} --non-interactive");

            Assert.Empty(parseResult.Errors);
            var (wasProvided, value) = GetAssetBinding(parseResult, option).Resolve();
            Assert.True(wasProvided);
            Assert.True(value);
            Assert.True(parseResult.GetValue(RootCommand.NonInteractiveOption));
        }
    }

    [Theory]
    [InlineData("", true)]
    [InlineData(" true", true)]
    [InlineData("=true", true)]
    [InlineData(" false", false)]
    [InlineData("=false", false)]
    public void AgentInitCommand_GlobalBooleanOptions_RetainBooleanParsing(string suffix, bool expected)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper).BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        foreach (var option in GlobalBooleanOptions)
        {
            var parseResult = command.Parse($"agent init --mcp y {option.Name}{suffix}");

            Assert.Empty(parseResult.Errors);
            Assert.Equal(expected, parseResult.GetValue(option));
            Assert.True(GetAssetBinding(parseResult, AgentInitCommand.s_mcpOption).Resolve().Value);
        }
    }

    [Theory]
    [InlineData(" y")]
    [InlineData("=Y")]
    [InlineData(" n")]
    [InlineData("=N")]
    public void AgentInitCommand_GlobalBooleanOptions_DoNotAcceptAssetOnlyAliases(string suffix)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper).BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        foreach (var option in GlobalBooleanOptions)
        {
            var parseResult = command.Parse($"agent init --mcp y {option.Name}{suffix}");

            Assert.NotEmpty(parseResult.Errors);
        }
    }

    [Fact]
    public void AgentInitCommand_AssetValues_DoNotChangeOmittedGlobalBooleanDefaults()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper).BuildServiceProvider();
        var parseResult = provider.GetRequiredService<RootCommand>().Parse("agent init --mcp y --aspire-skills n");

        Assert.Empty(parseResult.Errors);
        Assert.All(GlobalBooleanOptions, option => Assert.False(parseResult.GetValue(option)));
    }

    [Theory]
    [InlineData("--mcp maybe")]
    [InlineData("--playwright 1")]
    [InlineData("--dotnet-inspect yes")]
    [InlineData("--aspire-skills no")]
    [InlineData("--skills all")]
    [InlineData("--skill-locations all")]
    [InlineData("--unknown-asset")]
    public async Task AgentInitCommand_InvalidOptions_FailBeforeDiscoveryOrConfiguration(string arguments)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper).BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var detector = Assert.IsType<TestAgentEnvironmentDetector>(provider.GetRequiredService<IAgentEnvironmentDetector>());
        var service = Assert.IsType<TestAgentInitService>(provider.GetRequiredService<IAgentInitService>());
        var parseResult = command.Parse($"agent init {arguments}");

        Assert.NotEmpty(parseResult.Errors);
        var exitCode = await parseResult.InvokeAsync().DefaultTimeout();

        Assert.NotEqual(CliExitCodes.Success, exitCode);
        Assert.Empty(detector.Requests);
        Assert.Empty(service.Requests);
    }

    [Fact]
    public async Task AgentInitCommand_Help_DescribesIndependentAssetsAndClients()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper).BuildServiceProvider();
        using var output = new StringWriter();
        var parseResult = provider.GetRequiredService<RootCommand>().Parse("agent init --help");

        var exitCode = await parseResult.InvokeAsync(new InvocationConfiguration { Output = output }).DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        await Verify(output.ToString().TrimEnd(), "txt");
    }

    [Fact]
    public async Task AgentInitCommand_Interactive_PromptsForAssetsBeforeClients()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var operations = new List<string>();
        var interaction = new TestInteractionService
        {
            ConfirmCallback = (prompt, defaultValue) =>
            {
                operations.Add(prompt);
                return defaultValue;
            },
            ShowStatusCallback = _ => operations.Add("detect"),
            PromptForSelectionsCallback = (prompt, choices, formatter, _) =>
            {
                operations.Add(prompt);
                var clients = choices.Cast<AgentClientDescriptor>().ToArray();
                Assert.Equal(["copilot-cli", "copilot-app", "vscode", "claude-code", "opencode"], clients.Select(client => client.Id));
                Assert.Equal(
                    ["GitHub Copilot CLI", "GitHub Copilot App", "VS Code", "Claude Code", "OpenCode"],
                    clients.Select(client => formatter(client)));
                return [clients.Single(client => client.Kind is AgentClientKind.ClaudeCode)];
            }
        };
        var service = new TestAgentInitService
        {
            ConfigureAsyncCallback = (_, _) =>
            {
                operations.Add("configure");
                return Task.FromResult(new AgentInitResult([]));
            }
        };
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interaction;
            options.AgentEnvironmentDetectorFactory = _ => new TestAgentEnvironmentDetector();
            options.AgentInitServiceFactory = _ => service;
        });
        using var provider = services.BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>().Parse("agent init").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(
            [
                AgentCommandStrings.InitCommand_ConfigureMcpServerPrompt,
                AgentInitStrings.ConfigurePlaywrightPrompt,
                AgentInitStrings.ConfigureDotnetInspectPrompt,
                AgentInitStrings.ConfigureAspireSkillsPrompt,
                "detect",
                AgentInitStrings.SelectClients,
                "configure"
            ],
            operations);
        Assert.Equal([false, false, false, true], interaction.BooleanPromptCalls.Select(call => call.DefaultValue));
        var request = Assert.Single(service.Requests);
        Assert.Equal(new AgentAssetSelection(false, false, false, true), request.Assets);
        Assert.Equal([AgentClientKind.ClaudeCode], request.Clients);
        Assert.Empty(request.Detections);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AgentInitCommand_OmittedClients_SelectsDetectedClients(bool interactive)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        AgentClientDetection[] detections =
        [
            new(AgentClientKind.ClaudeCode, "2.1.0", IsInsiders: false),
            new(AgentClientKind.CopilotApp, Version: null, IsInsiders: false),
            new(AgentClientKind.ClaudeCode, "2.1.0", IsInsiders: false)
        ];
        var service = new TestAgentInitService();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AgentEnvironmentDetectorFactory = _ => new TestAgentEnvironmentDetector(detections);
            options.AgentInitServiceFactory = _ => service;
            if (interactive)
            {
                options.InteractionServiceFactory = _ => new TestInteractionService();
            }
        });
        using var provider = services.BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>().Parse("agent init").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        var request = Assert.Single(service.Requests);
        Assert.Equal([AgentClientKind.CopilotApp, AgentClientKind.ClaudeCode], request.Clients);
        Assert.Equal(detections, request.Detections);
        Assert.Equal(new AgentAssetSelection(false, false, false, true), request.Assets);
    }

    [Theory]
    [InlineData("COPILOT-CLI", "CopilotCli")]
    [InlineData("copilot-app", "CopilotApp")]
    [InlineData("VsCoDe", "VsCode")]
    [InlineData("CLAUDE-CODE", "ClaudeCode")]
    [InlineData("OpenCode", "OpenCode")]
    public async Task AgentInitCommand_ExplicitClient_CanSelectUndetectedClient(string clientId, string expectedKind)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var service = new TestAgentInitService();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AgentEnvironmentDetectorFactory = _ => new TestAgentEnvironmentDetector();
            options.AgentInitServiceFactory = _ => service;
        });
        using var provider = services.BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse($"agent init --clients {clientId}").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        var request = Assert.Single(service.Requests);
        Assert.Equal(Enum.Parse<AgentClientKind>(expectedKind), Assert.Single(request.Clients));
        Assert.Empty(request.Detections);
    }

    [Theory]
    [InlineData("ALL")]
    [InlineData("copilot-cli,COPILOT-APP,vscode,claude-code,opencode")]
    [InlineData("copilot-cli,copilot-app,vscode,claude-code,opencode,COPILOT-CLI")]
    public async Task AgentInitCommand_ExplicitClients_SelectsFullCatalog(string clients)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper).BuildServiceProvider();
        var service = Assert.IsType<TestAgentInitService>(provider.GetRequiredService<IAgentInitService>());

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse($"agent init --clients {clients}").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        var request = Assert.Single(service.Requests);
        Assert.Equal(
            [AgentClientKind.CopilotCli, AgentClientKind.CopilotApp, AgentClientKind.VsCode, AgentClientKind.ClaudeCode, AgentClientKind.OpenCode],
            request.Clients);
    }

    [Fact]
    public async Task AgentInitCommand_NonInteractive_NoDetectedClients_RequiresExplicitClients()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var error = new StringWriter();
        var service = new TestAgentInitService();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AgentEnvironmentDetectorFactory = _ => new TestAgentEnvironmentDetector();
            options.AgentInitServiceFactory = _ => service;
            options.ErrorTextWriter = error;
            options.DisableAnsi = true;
        });
        using var provider = services.BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("agent init --non-interactive").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.MissingRequiredArgument, exitCode);
        Assert.Empty(service.Requests);
        await Verify(error.ToString(), "txt");
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("copilot-cli,unknown")]
    public async Task AgentInitCommand_UnknownClient_FailsBeforeDiscoveryOrConfiguration(string clients)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var service = new TestAgentInitService();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AgentInitServiceFactory = _ => service;
        });
        using var provider = services.BuildServiceProvider();
        var detector = Assert.IsType<TestAgentEnvironmentDetector>(provider.GetRequiredService<IAgentEnvironmentDetector>());
        var parseResult = provider.GetRequiredService<RootCommand>().Parse($"agent init --mcp y --clients {clients}");

        Assert.NotEmpty(parseResult.Errors);
        var exitCode = await parseResult.InvokeAsync(new InvocationConfiguration { Output = TextWriter.Null, Error = TextWriter.Null }).DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.Empty(detector.Requests);
        Assert.Empty(service.Requests);
        await Verify(parseResult.Errors.Select(error => error.Message)).UseParameters(clients);
    }

    [Fact]
    public async Task AgentInitCommand_AllAssetsDisabled_SkipsDiscoveryAndLeavesExistingFilesUntouched()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var mcpPath = Path.Combine(workspace.WorkspaceRoot.FullName, ".mcp.json");
        const string existingMcp = """{"mcpServers":{"aspire":{"command":"aspire","args":["mcp","start"]}}}""";
        await File.WriteAllTextAsync(mcpPath, existingMcp);
        var skillDirectory = workspace.CreateDirectory(Path.Combine(".agents", "skills", "aspire"));
        var skillPath = Path.Combine(skillDirectory.FullName, "SKILL.md");
        const string existingSkill = "User-managed Aspire skill";
        await File.WriteAllTextAsync(skillPath, existingSkill);
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper).BuildServiceProvider();
        var detector = Assert.IsType<TestAgentEnvironmentDetector>(provider.GetRequiredService<IAgentEnvironmentDetector>());
        var service = Assert.IsType<TestAgentInitService>(provider.GetRequiredService<IAgentInitService>());

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("agent init --mcp n --playwright false --dotnet-inspect N --aspire-skills=false")
            .InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Empty(detector.Requests);
        Assert.Empty(service.Requests);
        Assert.Equal(existingMcp, await File.ReadAllTextAsync(mcpPath));
        Assert.Equal(existingSkill, await File.ReadAllTextAsync(skillPath));
    }

    [Fact]
    public async Task AgentInitCommand_ExplicitNoneClients_DoesNotConfigureAnyAssets()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var mcpPath = Path.Combine(workspace.WorkspaceRoot.FullName, ".mcp.json");
        const string existingMcp = """{"mcpServers":{"aspire":{"command":"aspire","args":["mcp","start"]}}}""";
        await File.WriteAllTextAsync(mcpPath, existingMcp);
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper).BuildServiceProvider();
        var service = Assert.IsType<TestAgentInitService>(provider.GetRequiredService<IAgentInitService>());

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("agent init --mcp --playwright --dotnet-inspect --aspire-skills --clients NONE")
            .InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Empty(service.Requests);
        Assert.Equal(existingMcp, await File.ReadAllTextAsync(mcpPath));
    }

    [Theory]
    [InlineData("working")]
    [InlineData("git")]
    [InlineData("explicit")]
    public async Task AgentInitCommand_UsesResolvedWorkspaceRoot(string rootSource)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var workingDirectory = workspace.CreateDirectory(Path.Combine("repository", "nested"));
        var explicitDirectory = workspace.CreateDirectory("other workspace");
        var expectedRoot = rootSource switch
        {
            "git" => workingDirectory.Parent!,
            "explicit" => explicitDirectory,
            _ => workingDirectory
        };
        var detector = new TestAgentEnvironmentDetector();
        var service = new TestAgentInitService();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.WorkingDirectory = workingDirectory;
            options.GitRepositoryFactory = _ => new TestGitRepository
            {
                GetRootAsyncCallback = _ => Task.FromResult(rootSource == "working" ? null : workingDirectory.Parent)
            };
            options.AgentEnvironmentDetectorFactory = _ => detector;
            options.AgentInitServiceFactory = _ => service;
        });
        using var provider = services.BuildServiceProvider();
        var arguments = rootSource == "explicit" ? $" --workspace-root \"{explicitDirectory.FullName}\"" : string.Empty;

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse($"agent init --clients copilot-cli{arguments}").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(expectedRoot.FullName, Assert.Single(service.Requests).WorkspaceRoot.FullName);
        var scan = Assert.Single(detector.Requests);
        Assert.Equal(expectedRoot.FullName, scan.RepositoryRoot.FullName);
        Assert.Equal(workingDirectory.FullName, scan.WorkingDirectory.FullName);
    }

    [Fact]
    public async Task AgentInitCommand_DotnetInspect_CanBeSelectedBeforeAnAppHostExists()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper).BuildServiceProvider();
        var service = Assert.IsType<TestAgentInitService>(provider.GetRequiredService<IAgentInitService>());

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("agent init --dotnet-inspect y --aspire-skills n --clients claude-code")
            .InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(new AgentAssetSelection(false, false, true, false), Assert.Single(service.Requests).Assets);
        Assert.Empty(workspace.WorkspaceRoot.GetFiles("*.cs", SearchOption.AllDirectories));
        Assert.Empty(workspace.WorkspaceRoot.GetFiles("*.csproj", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("Configured", CliExitCodes.Success)]
    [InlineData("Unchanged", CliExitCodes.Success)]
    [InlineData("Skipped", CliExitCodes.Success)]
    [InlineData("Blocked", CliExitCodes.InvalidCommand)]
    [InlineData("Failed", CliExitCodes.InvalidCommand)]
    public async Task AgentInitCommand_ReportsNativeRegistrationStatusWithoutClaimingAcquisition(string status, int expectedExitCode)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var subtleMessages = new List<string>();
        var interaction = new TestInteractionService { DisplaySubtleMessageCallback = subtleMessages.Add };
        var service = new TestAgentInitService
        {
            Result = new(
            [
                new(AgentAssetKind.AspireSkills, [AgentClientKind.CopilotCli, AgentClientKind.CopilotApp],
                    "project-settings.json", AgentConfigurationScope.Project,
                    Enum.Parse<AgentConfigurationStatus>(status), "Native client owns acquisition.")
            ])
        };
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interaction;
            options.AgentEnvironmentDetectorFactory = _ => new TestAgentEnvironmentDetector(
                new(AgentClientKind.CopilotCli, null, false), new(AgentClientKind.CopilotApp, null, false));
            options.AgentInitServiceFactory = _ => service;
        });
        using var provider = services.BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>().Parse("agent init").InvokeAsync().DefaultTimeout();

        Assert.Equal(expectedExitCode, exitCode);
        var logFilePath = provider.GetRequiredService<CliExecutionContext>().LogFilePath;
        await Verify(new
        {
            Messages = interaction.DisplayedMessages.Select(message => message.Message.Replace(logFilePath, "<log-file>", StringComparison.Ordinal)),
            Errors = interaction.DisplayedErrors,
            Success = interaction.DisplayedSuccess,
            SubtleMessages = subtleMessages
        }).UseParameters(status, expectedExitCode);
    }

    [Theory]
    [InlineData("Blocked")]
    [InlineData("Failed")]
    public async Task AgentInitCommand_HookFailure_IsAdvisoryWithQualifiedCompletion(string status)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var subtleMessages = new List<string>();
        var interaction = new TestInteractionService { DisplaySubtleMessageCallback = subtleMessages.Add };
        var service = new TestAgentInitService
        {
            Result = new(
            [
                new(AgentAssetKind.AspireSkills, [AgentClientKind.CopilotCli],
                    "project-settings.json", AgentConfigurationScope.Project, AgentConfigurationStatus.Configured, null),
                new(AgentAssetKind.TelemetryHooks, [AgentClientKind.CopilotCli],
                    "user-hooks.json", AgentConfigurationScope.User,
                    Enum.Parse<AgentConfigurationStatus>(status), "The hook could not be written.")
            ])
        };
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interaction;
            options.AgentInitServiceFactory = _ => service;
        });
        using var provider = services.BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>().Parse("agent init").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Empty(interaction.DisplayedErrors);
        Assert.Empty(interaction.DisplayedSuccess);
        Assert.Equal(KnownEmojis.Warning, interaction.DisplayedMessages[^1].Emoji);
        await Verify(new
        {
            Messages = interaction.DisplayedMessages.Select(message => message.Message),
            SubtleMessages = subtleMessages
        }).UseParameters(status);
    }

    [Fact]
    public async Task AgentInitCommand_ReportsManagedPayloadsAsInstalled()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        var service = new TestAgentInitService
        {
            Result = new(
            [
                new(AgentAssetKind.Playwright, [AgentClientKind.ClaudeCode],
                    "playwright-cli", AgentConfigurationScope.Project, AgentConfigurationStatus.Configured, null),
                new(AgentAssetKind.DotnetInspect, [AgentClientKind.ClaudeCode],
                    "dotnet-inspect", AgentConfigurationScope.User, AgentConfigurationStatus.Configured, null)
            ])
        };
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interaction;
            options.AgentEnvironmentDetectorFactory = _ => new TestAgentEnvironmentDetector(new AgentClientDetection(AgentClientKind.ClaudeCode, null, false));
            options.AgentInitServiceFactory = _ => service;
        });
        using var provider = services.BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("agent init --playwright --dotnet-inspect --aspire-skills n").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        await Verify(interaction.DisplayedMessages.Select(message => message.Message));
    }

    [Fact]
    public async Task AgentInitCommand_CancellationDuringDiscovery_PropagatesWithoutConfiguration()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var cancellation = new CancellationTokenSource();
        var interaction = new TestInteractionService { ShowStatusCallback = _ => cancellation.Cancel() };
        var service = new TestAgentInitService();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interaction;
            options.AgentInitServiceFactory = _ => service;
        });
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<AgentInitCommand>();
        var parseResult = command.Parse("init");

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => command.ExecuteCommandAsync(parseResult, cancellation.Token)).DefaultTimeout();

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Empty(service.Requests);
    }

    [Fact]
    public async Task AgentInitCommand_ForwardsCancellationToConfigurationService()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var cancellation = new CancellationTokenSource();
        var service = new TestAgentInitService
        {
            ConfigureAsyncCallback = (_, token) =>
            {
                Assert.Equal(cancellation.Token, token);
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                throw new InvalidOperationException("Cancellation must propagate.");
            }
        };
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options => options.AgentInitServiceFactory = _ => service);
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<AgentInitCommand>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => command.ExecuteCommandAsync(command.Parse("init --clients copilot-cli"), cancellation.Token)).DefaultTimeout();

        Assert.Single(service.Requests);
    }

    [Theory]
    [InlineData(CliExitCodes.FailedToCreateNewProject, true)]
    [InlineData(CliExitCodes.Success, false)]
    public async Task PromptAndChainAsync_PreviousFailureOrSuppression_SkipsAgentSetup(int previousExitCode, bool acceptSetup)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options => options.InteractionServiceFactory = _ => interaction);
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<AgentInitCommand>();
        var detector = Assert.IsType<TestAgentEnvironmentDetector>(provider.GetRequiredService<IAgentEnvironmentDetector>());
        var service = Assert.IsType<TestAgentInitService>(provider.GetRequiredService<IAgentInitService>());

        var result = await command.PromptAndChainAsync(
            interaction, previousExitCode, workspace.WorkspaceRoot, PromptBinding.CreateDefault(acceptSetup),
            AgentInitCommand.CreateBindings(command.Parse("init"), includeMcp: true), TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.Equal(previousExitCode, result.ExitCode);
        Assert.Empty(result.RegisteredClients);
        Assert.Empty(detector.Requests);
        Assert.Empty(service.Requests);
        Assert.Equal(previousExitCode == CliExitCodes.Success ? 1 : 0, interaction.BooleanPromptCalls.Count);
    }

    [Fact]
    public async Task PromptAndChainAsync_UsesOutputRootExcludesMcpAndReturnsOnlyRegisteredClients()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var outputRoot = workspace.CreateDirectory("output");
        var interaction = new TestInteractionService
        {
            PromptForSelectionsCallback = (_, choices, _, _) => choices.Cast<object>().ToArray()
        };
        var service = new TestAgentInitService
        {
            Result = new(
            [
                new(AgentAssetKind.AspireSkills, [AgentClientKind.CopilotCli],
                    "project-settings.json", AgentConfigurationScope.Project, AgentConfigurationStatus.Configured, null),
                new(AgentAssetKind.AspireSkills, [AgentClientKind.CopilotCli, AgentClientKind.ClaudeCode],
                    "user-settings.json", AgentConfigurationScope.User, AgentConfigurationStatus.Unchanged, null),
                new(AgentAssetKind.AspireSkills, [AgentClientKind.OpenCode],
                    "opencode.json", AgentConfigurationScope.Project, AgentConfigurationStatus.Blocked, "Catalog unavailable."),
                new(AgentAssetKind.Playwright, [AgentClientKind.VsCode],
                    "playwright-cli", AgentConfigurationScope.Project, AgentConfigurationStatus.Configured, null)
            ])
        };
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interaction;
            options.AgentInitServiceFactory = _ => service;
        });
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<AgentInitCommand>();
        var parseResult = command.Parse("init --mcp y");

        var result = await command.PromptAndChainAsync(
            interaction, CliExitCodes.Success, outputRoot, PromptBinding.CreateDefault(true),
            AgentInitCommand.CreateBindings(parseResult, includeMcp: true), TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, result.ExitCode);
        Assert.Equal([AgentClientKind.CopilotCli, AgentClientKind.ClaudeCode], result.RegisteredClients);
        var request = Assert.Single(service.Requests);
        Assert.Equal(outputRoot.FullName, request.WorkspaceRoot.FullName);
        Assert.False(request.Assets.Mcp);
        Assert.Equal(
            [SharedCommandStrings.PromptRunAgentInit, AgentInitStrings.ConfigurePlaywrightPrompt,
                AgentInitStrings.ConfigureDotnetInspectPrompt, AgentInitStrings.ConfigureAspireSkillsPrompt],
            interaction.BooleanPromptCalls.Select(call => call.PromptText));
    }

    private static PromptBinding<bool> GetAssetBinding(ParseResult parseResult, Option option)
    {
        var bindings = AgentInitCommand.CreateBindings(parseResult, includeMcp: true);

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
