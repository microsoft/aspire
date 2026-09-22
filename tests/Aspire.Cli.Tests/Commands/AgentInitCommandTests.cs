// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
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
            var (wasProvided, value) = GetAssetBinding(provider, parseResult, option).Resolve();
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
        Assert.All(AssetOptions, option => Assert.False(GetAssetBinding(provider, parseResult, option).Resolve().WasProvided));
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
            var (wasProvided, value) = GetAssetBinding(provider, parseResult, option).Resolve();
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
            Assert.True(GetAssetBinding(provider, parseResult, AgentInitCommand.s_mcpOption).Resolve().Value);
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
        var detector = Assert.IsType<TestAgentClientEnvironment>(provider.GetRequiredService<IAgentClientEnvironment>());
        var service = Assert.IsType<TestAgentInitService>(provider.GetRequiredService<IAgentInitService>());
        var parseResult = command.Parse($"agent init {arguments}");

        Assert.NotEmpty(parseResult.Errors);
        var exitCode = await parseResult.InvokeAsync().DefaultTimeout();

        Assert.NotEqual(CliExitCodes.Success, exitCode);
        Assert.Empty(detector.Calls);
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
                var clients = choices.Cast<AgentClient>().ToArray();
                Assert.Equal(["copilot-cli", "copilot-app", "vscode", "claude-code", "opencode"], clients.Select(client => client.Id));
                Assert.Equal(
                    ["GitHub Copilot CLI", "GitHub Copilot App", "VS Code", "Claude Code", "OpenCode"],
                    clients.Select(client => formatter(client)));
                return [clients.Single(client => client.Id == "claude-code")];
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
            options.AgentEnvironmentFactory = _ => new TestAgentClientEnvironment();
            options.AgentInitServiceFactory = _ => service;
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
                McpCommandStrings.InitCommand_AgentConfigurationSelectPrompt,
                "configure"
            ],
            operations);
        Assert.Equal([false, false, false, true], interaction.BooleanPromptCalls.Select(call => call.DefaultValue));
        var request = Assert.Single(service.Requests);
        Assert.Equal(new AgentAssetSelection(false, false, false, true), request.Assets);
        Assert.Equal(["claude-code"], request.Clients.Select(client => client.Id));
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
            new(TestAgentClients.Default.ClaudeCode, "2.1.0", IsInsiders: false),
            new(TestAgentClients.Default.CopilotApp, Version: null, IsInsiders: false),
            new(TestAgentClients.Default.ClaudeCode, "2.1.0", IsInsiders: false)
        ];
        var service = new TestAgentInitService();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AgentEnvironmentFactory = _ => new TestAgentClientEnvironment(detections);
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
        Assert.Equal(["copilot-app", "claude-code"], request.Clients.Select(client => client.Id));
        Assert.Equal(
            detections.Distinct().Select(detection => (detection.Client.Id, detection.Version, detection.IsInsiders)),
            request.Detections.Select(detection => (detection.Client.Id, detection.Version, detection.IsInsiders)));
        Assert.All(request.Detections, detection =>
            Assert.Same(provider.GetRequiredService<AgentClientCatalog>().Clients.Single(client => client.Id == detection.Client.Id), detection.Client));
        Assert.Equal(new AgentAssetSelection(false, false, false, true), request.Assets);
    }

    [Theory]
    [InlineData("COPILOT-CLI", "copilot-cli")]
    [InlineData("copilot-app", "copilot-app")]
    [InlineData("VsCoDe", "vscode")]
    [InlineData("CLAUDE-CODE", "claude-code")]
    [InlineData("OpenCode", "opencode")]
    public async Task AgentInitCommand_ExplicitClient_CanSelectUndetectedClient(string clientId, string expectedId)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var service = new TestAgentInitService();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AgentEnvironmentFactory = _ => new TestAgentClientEnvironment();
            options.AgentInitServiceFactory = _ => service;
        });
        using var provider = services.BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse($"agent init --clients {clientId}").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        var request = Assert.Single(service.Requests);
        Assert.Equal(expectedId, Assert.Single(request.Clients).Id);
        Assert.Empty(request.Detections);
    }

    [Fact]
    public async Task AgentInitCommand_ExplicitClients_PreservesUnselectedDetectionsForHooks()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        AgentClientDetection[] detections =
        [
            new(TestAgentClients.Default.CopilotCli, "1.0.0", false),
            new(TestAgentClients.Default.ClaudeCode, "2.1.0", false)
        ];
        var service = new TestAgentInitService();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AgentEnvironmentFactory = _ => new TestAgentClientEnvironment(detections);
            options.AgentInitServiceFactory = _ => service;
        });
        using var provider = services.BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("agent init --clients opencode --non-interactive").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        var request = Assert.Single(service.Requests);
        Assert.Equal(["opencode"], request.Clients.Select(client => client.Id));
        Assert.Equal(
            detections.Select(detection => (detection.Client.Id, detection.Version, detection.IsInsiders)),
            request.Detections.Select(detection => (detection.Client.Id, detection.Version, detection.IsInsiders)));
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
            ["copilot-cli", "copilot-app", "vscode", "claude-code", "opencode"],
            request.Clients.Select(client => client.Id));
        Assert.All(request.Clients, client =>
            Assert.Same(provider.GetRequiredService<AgentClientCatalog>().Clients.Single(entry => entry.Id == client.Id), client));
    }

    [Fact]
    public async Task AgentInitCommand_ScansSharedEnvironmentsOnceAndCopiesDistinctEvidence()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var cancellation = new CancellationTokenSource();
        var workingDirectory = workspace.CreateDirectory("nested");
        var originalEvidence = new List<AgentClientDetection>();
        var copilot = new TestAgentClientEnvironment
        {
            ScanAsyncCallback = (clients, _, _, _) =>
            {
                var cli = clients.Single(client => client.Id == "copilot-cli");
                var app = clients.Single(client => client.Id == "copilot-app");
                originalEvidence.AddRange([new(cli, "1.0.0", false), new(app, null, false), new(cli, "1.0.0", false)]);
                return Task.FromResult<IReadOnlyList<AgentClientDetection>>(originalEvidence);
            }
        };
        var vsCode = new TestAgentClientEnvironment
        {
            ScanAsyncCallback = (clients, _, _, _) => Task.FromResult<IReadOnlyList<AgentClientDetection>>(
                [new(clients[0], "1.100.0", false), new(clients[0], "1.101.0-insider", true)])
        };
        var catalog = new AgentClientCatalog(
        [
            new("copilot-cli", "GitHub Copilot CLI", copilot),
            new("copilot-app", "GitHub Copilot App", copilot),
            new("vscode", "VS Code", vsCode)
        ]);
        var service = new TestAgentInitService();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.WorkingDirectory = workingDirectory;
            options.AgentInitServiceFactory = _ => service;
            options.GitRepositoryFactory = _ => new TestGitRepository
            {
                GetRootAsyncCallback = _ => Task.FromResult<DirectoryInfo?>(workspace.WorkspaceRoot)
            };
        });
        services.AddSingleton(catalog);
        using var provider = services.BuildServiceProvider();
        var parseResult = provider.GetRequiredService<RootCommand>().Parse("agent init --clients all");

        await provider.GetRequiredService<AgentInitCommand>().ExecuteCommandAsync(parseResult, cancellation.Token).DefaultTimeout();

        var request = Assert.Single(service.Requests);
        Assert.Equal(catalog.Clients, request.Clients);
        Assert.Equal(
            [("copilot-cli", "1.0.0", false), ("copilot-app", null, false), ("vscode", "1.100.0", false), ("vscode", "1.101.0-insider", true)],
            request.Detections.Select(detection => (detection.Client.Id, detection.Version, detection.IsInsiders)));
        Assert.All(request.Detections, detection =>
            Assert.Same(catalog.Clients.Single(client => client.Id == detection.Client.Id), detection.Client));
        foreach (var environment in new[] { copilot, vsCode })
        {
            var call = Assert.Single(environment.Calls);
            Assert.Same(workingDirectory, call.WorkingDirectory);
            Assert.Equal(workspace.WorkspaceRoot.FullName, call.WorkspaceRoot.FullName);
            Assert.Equal(cancellation.Token, call.CancellationToken);
            Assert.All(call.Clients, client => Assert.Same(environment, client.Environment));
        }
        originalEvidence.Clear();
        Assert.Equal(4, request.Detections.Count);
        var snapshot = Assert.IsAssignableFrom<IList<AgentClientDetection>>(request.Detections);
        Assert.True(snapshot.IsReadOnly);
        Assert.Throws<NotSupportedException>(snapshot.Clear);
    }

    [Fact]
    public async Task AgentInitCommand_CancellationDuringScan_DoesNotInvokeRemainingEnvironmentOrConfigure()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var cancellation = new CancellationTokenSource();
        var first = new TestAgentClientEnvironment
        {
            ScanAsyncCallback = (_, _, _, _) =>
            {
                cancellation.Cancel();
                return Task.FromResult<IReadOnlyList<AgentClientDetection>>([]);
            }
        };
        var second = new TestAgentClientEnvironment();
        var service = new TestAgentInitService();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options => options.AgentInitServiceFactory = _ => service);
        services.AddSingleton(new AgentClientCatalog([new("first", "First", first), new("second", "Second", second)]));
        using var provider = services.BuildServiceProvider();
        var parseResult = provider.GetRequiredService<RootCommand>().Parse("agent init --clients all");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.GetRequiredService<AgentInitCommand>().ExecuteCommandAsync(parseResult, cancellation.Token)).DefaultTimeout();

        Assert.Single(first.Calls);
        Assert.Empty(second.Calls);
        Assert.Empty(service.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AgentInitCommand_CancelledBeforeScan_DoesNotProbeOrConfigure(bool registerEnvironment)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var environment = new TestAgentClientEnvironment();
        var service = new TestAgentInitService();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options => options.AgentInitServiceFactory = _ => service);
        services.AddSingleton(new AgentClientCatalog(registerEnvironment ? [new("test", "Test", environment)] : []));
        using var provider = services.BuildServiceProvider();
        var parseResult = provider.GetRequiredService<RootCommand>().Parse("agent init --clients none");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.GetRequiredService<AgentInitCommand>().ExecuteCommandAsync(parseResult, new CancellationToken(canceled: true))).DefaultTimeout();

        Assert.Empty(environment.Calls);
        Assert.Empty(service.Requests);
    }

    [Fact]
    public async Task AgentInitCommand_NonInteractive_NoDetectedClients_RequiresExplicitClients()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var error = new StringWriter();
        var service = new TestAgentInitService();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AgentEnvironmentFactory = _ => new TestAgentClientEnvironment();
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
        var detector = Assert.IsType<TestAgentClientEnvironment>(provider.GetRequiredService<IAgentClientEnvironment>());
        var parseResult = provider.GetRequiredService<RootCommand>().Parse($"agent init --mcp y --clients {clients}");

        Assert.NotEmpty(parseResult.Errors);
        var exitCode = await parseResult.InvokeAsync(new InvocationConfiguration { Output = TextWriter.Null, Error = TextWriter.Null }).DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.Empty(detector.Calls);
        Assert.Empty(service.Requests);
        var expectedError = string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_InvalidClients,
            clients, "copilot-cli,copilot-app,vscode,claude-code,opencode", "all", "none");
        Assert.Equal(expectedError, Assert.Single(parseResult.Errors).Message);
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
        var detector = Assert.IsType<TestAgentClientEnvironment>(provider.GetRequiredService<IAgentClientEnvironment>());
        var service = Assert.IsType<TestAgentInitService>(provider.GetRequiredService<IAgentInitService>());

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("agent init --mcp n --playwright false --dotnet-inspect N --aspire-skills=false")
            .InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Empty(detector.Calls);
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
        var detector = new TestAgentClientEnvironment();
        var service = new TestAgentInitService();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.WorkingDirectory = workingDirectory;
            options.GitRepositoryFactory = _ => new TestGitRepository
            {
                GetRootAsyncCallback = _ => Task.FromResult(rootSource == "working" ? null : workingDirectory.Parent)
            };
            options.AgentEnvironmentFactory = _ => detector;
            options.AgentInitServiceFactory = _ => service;
        });
        using var provider = services.BuildServiceProvider();
        var arguments = rootSource == "explicit" ? $" --workspace-root \"{explicitDirectory.FullName}\"" : string.Empty;

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse($"agent init --clients copilot-cli{arguments}").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(expectedRoot.FullName, Assert.Single(service.Requests).WorkspaceRoot.FullName);
        var scan = Assert.Single(detector.Calls);
        Assert.Equal(expectedRoot.FullName, scan.WorkspaceRoot.FullName);
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
                new(AgentAssetKind.AspireSkills, [TestAgentClients.Default.CopilotCli, TestAgentClients.Default.CopilotApp],
                    "project-settings.json", AgentConfigurationScope.Project,
                    Enum.Parse<AgentConfigurationStatus>(status), "Native client owns acquisition.")
            ])
        };
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interaction;
            options.AgentEnvironmentFactory = _ => new TestAgentClientEnvironment(
                new(TestAgentClients.Default.CopilotCli, null, false), new(TestAgentClients.Default.CopilotApp, null, false));
            options.AgentInitServiceFactory = _ => service;
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
            AgentCommandStrings.InitCommand_AspireSkillsAsset, "GitHub Copilot CLI, GitHub Copilot App",
            AgentCommandStrings.InitCommand_ProjectScope, "project-settings.json");
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
        string[] expectedSuccess = status is "Configured" or "Unchanged" ? [McpCommandStrings.InitCommand_ConfigurationComplete] : [];
        string[] expectedSubtleMessages = status switch
        {
            "Configured" => ["Native client owns acquisition.", AgentCommandStrings.InitCommand_ClientAcquisitionNotice],
            "Unchanged" => [targetMessage, "Native client owns acquisition.", AgentCommandStrings.InitCommand_ClientAcquisitionNotice],
            _ => ["Native client owns acquisition."]
        };
        Assert.Equal(expectedMessages, interaction.DisplayedMessages.Select(message => message.Message.Replace(logFilePath, "<log-file>", StringComparison.Ordinal)));
        Assert.Equal(expectedErrors, interaction.DisplayedErrors);
        Assert.Equal(expectedSuccess, interaction.DisplayedSuccess);
        Assert.Equal(expectedSubtleMessages, subtleMessages);
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
                new(AgentAssetKind.AspireSkills, [TestAgentClients.Default.CopilotCli],
                    "project-settings.json", AgentConfigurationScope.Project, AgentConfigurationStatus.Configured, null),
                new(AgentAssetKind.TelemetryHooks, [TestAgentClients.Default.CopilotCli],
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
        Assert.Equal(
        [
            string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_RegisteredTarget,
                AgentCommandStrings.InitCommand_AspireSkillsAsset, "GitHub Copilot CLI", AgentCommandStrings.InitCommand_ProjectScope, "project-settings.json"),
            string.Format(CultureInfo.CurrentCulture, status == "Blocked" ? AgentCommandStrings.InitCommand_BlockedTarget : AgentCommandStrings.InitCommand_FailedTarget,
                AgentCommandStrings.InitCommand_TelemetryHooksAsset, "GitHub Copilot CLI", AgentCommandStrings.InitCommand_UserScope, "user-hooks.json"),
            AgentCommandStrings.ConfigurationCompletedWithWarnings
        ],
        interaction.DisplayedMessages.Select(message => message.Message));
        Assert.Equal(["The hook could not be written.", AgentCommandStrings.InitCommand_ClientAcquisitionNotice], subtleMessages);
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
                new(AgentAssetKind.Playwright, [TestAgentClients.Default.ClaudeCode],
                    "playwright-cli", AgentConfigurationScope.Project, AgentConfigurationStatus.Configured, null),
                new(AgentAssetKind.DotnetInspect, [TestAgentClients.Default.ClaudeCode],
                    "dotnet-inspect", AgentConfigurationScope.User, AgentConfigurationStatus.Configured, null)
            ])
        };
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interaction;
            options.AgentEnvironmentFactory = _ => new TestAgentClientEnvironment(new AgentClientDetection(TestAgentClients.Default.ClaudeCode, null, false));
            options.AgentInitServiceFactory = _ => service;
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
                AgentCommandStrings.InitCommand_DotnetInspectAsset, "Claude Code", AgentCommandStrings.InitCommand_UserScope, "dotnet-inspect")
        ],
        interaction.DisplayedMessages.Select(message => message.Message));
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
        var detector = Assert.IsType<TestAgentClientEnvironment>(provider.GetRequiredService<IAgentClientEnvironment>());
        var service = Assert.IsType<TestAgentInitService>(provider.GetRequiredService<IAgentInitService>());

        var result = await command.PromptAndChainAsync(
            interaction, previousExitCode, workspace.WorkspaceRoot, PromptBinding.CreateDefault(acceptSetup),
            command.CreateBindings(command.Parse("init"), includeMcp: true), TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.Equal(previousExitCode, result.ExitCode);
        Assert.Empty(result.RegisteredClients);
        Assert.Empty(detector.Calls);
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
                new(AgentAssetKind.AspireSkills, [TestAgentClients.Default.CopilotCli],
                    "project-settings.json", AgentConfigurationScope.Project, AgentConfigurationStatus.Configured, null),
                new(AgentAssetKind.AspireSkills, [TestAgentClients.Default.CopilotCli, TestAgentClients.Default.ClaudeCode],
                    "user-settings.json", AgentConfigurationScope.User, AgentConfigurationStatus.Unchanged, null),
                new(AgentAssetKind.AspireSkills, [TestAgentClients.Default.OpenCode],
                    "opencode.json", AgentConfigurationScope.Project, AgentConfigurationStatus.Blocked, "Catalog unavailable."),
                new(AgentAssetKind.Playwright, [TestAgentClients.Default.VsCode],
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
            command.CreateBindings(parseResult, includeMcp: true), TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, result.ExitCode);
        Assert.Equal([TestAgentClients.Default.CopilotCli, TestAgentClients.Default.ClaudeCode], result.RegisteredClients);
        var request = Assert.Single(service.Requests);
        Assert.Equal(outputRoot.FullName, request.WorkspaceRoot.FullName);
        Assert.False(request.Assets.Mcp);
        Assert.Equal(
            [SharedCommandStrings.PromptRunAgentInit, McpCommandStrings.InitCommand_ConfigurePlaywrightPrompt,
                AgentCommandStrings.InitCommand_ConfigureDotnetInspectPrompt, AgentCommandStrings.InitCommand_ConfigureAspireSkillsPrompt],
            interaction.BooleanPromptCalls.Select(call => call.PromptText));
    }

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
