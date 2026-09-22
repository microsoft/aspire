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
            Assert.All(GlobalBooleanOptions, global => Assert.False(parseResult.GetValue(global)));
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
    [InlineData("--mcp maybe")]
    [InlineData("--playwright 1")]
    [InlineData("--dotnet-inspect yes")]
    [InlineData("--aspire-skills no")]
    [InlineData("--skills all")]
    [InlineData("--skill-locations all")]
    [InlineData("--unknown-asset")]
    [InlineData("--clients copilot")]
    public async Task AgentInitCommand_InvalidOptions_FailBeforeDiscoveryOrConfiguration(string arguments)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper).BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var detector = Assert.IsType<TestAgentClientEnvironment>(provider.GetRequiredService<IAgentEnvironmentScanner>());
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
                var clients = choices.Cast<AgentClient>().ToArray();
                Assert.Equal(["copilot", "vscode", "claude", "opencode"], clients.Select(client => client.Id));
                Assert.Equal(
                    [AgentCommandStrings.Environment_Copilot, AgentCommandStrings.Environment_VsCode, "Claude Code", "OpenCode"],
                    clients.Select(client => formatter(client)));
                return [clients.Single(client => client.Id == "claude")];
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
                AgentCommandStrings.InitCommand_EnvironmentSelectionNotice,
                McpCommandStrings.InitCommand_AgentConfigurationSelectPrompt,
                "configure"
            ],
            operations);
        Assert.Equal([false, false, false, true], interaction.BooleanPromptCalls.Select(call => call.DefaultValue));
        var request = Assert.Single(service.Requests);
        Assert.Equal(new AgentAssetSelection(false, false, false, true), request.Assets);
        Assert.Equal(["claude"], request.Clients.Select(client => client.Id));
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
            new(TestAgentClients.Default.Copilot, Version: null, IsInsiders: false),
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
        Assert.Equal(["copilot", "claude"], request.Clients.Select(client => client.Id));
        Assert.Equal(
            detections.Distinct().OrderBy(detection => detection.Client.Id).Select(detection => (detection.Client.Id, detection.Version, detection.IsInsiders)),
            request.Detections.OrderBy(detection => detection.Client.Id).Select(detection => (detection.Client.Id, detection.Version, detection.IsInsiders)));
        Assert.All(request.Detections, detection =>
            Assert.Same(provider.GetRequiredService<AgentClientCatalog>().Clients.Single(client => client.Id == detection.Client.Id), detection.Client));
        Assert.Equal(new AgentAssetSelection(false, false, false, true), request.Assets);
    }

    [Theory]
    [InlineData("COPILOT", "copilot")]
    [InlineData("copilot", "copilot")]
    [InlineData("VsCoDe", "vscode")]
    [InlineData("CLAUDE", "claude")]
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
            .Parse($"agent init --environments {clientId}").InvokeAsync().DefaultTimeout();

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
            new(TestAgentClients.Default.Copilot, "1.0.0", false),
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
            .Parse("agent init --environments opencode --non-interactive").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        var request = Assert.Single(service.Requests);
        Assert.Equal(["opencode"], request.Clients.Select(client => client.Id));
        Assert.Equal(
            detections.Select(detection => (detection.Client.Id, detection.Version, detection.IsInsiders)),
            request.Detections.Select(detection => (detection.Client.Id, detection.Version, detection.IsInsiders)));
    }

    [Theory]
    [InlineData("ALL")]
    [InlineData("copilot,vscode,claude,opencode")]
    [InlineData("copilot,vscode,claude,opencode,copilot")]
    public async Task AgentInitCommand_ExplicitClients_SelectsFullCatalog(string clients)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper).BuildServiceProvider();
        var service = Assert.IsType<TestAgentInitService>(provider.GetRequiredService<IAgentInitService>());

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse($"agent init --environments {clients}").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        var request = Assert.Single(service.Requests);
        Assert.Equal(
            ["copilot", "vscode", "claude", "opencode"],
            request.Clients.Select(client => client.Id));
        Assert.All(request.Clients, client =>
            Assert.Same(provider.GetRequiredService<AgentClientCatalog>().Clients.Single(entry => entry.Id == client.Id), client));
    }

    [Fact]
    public async Task AgentInitCommand_ScansEnvironmentsOnceAndBindsEvidenceToCatalogEntries()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var cancellation = new CancellationTokenSource();
        var workingDirectory = workspace.CreateDirectory("nested");
        AgentEnvironmentDetection? originalEvidence = new("1.0.0", false);
        var copilot = new TestAgentClientEnvironment
        {
            ScanAsyncCallback = (_, _, _) =>
            {
                return Task.FromResult(originalEvidence);
            }
        };
        var vsCode = new TestAgentClientEnvironment
        {
            ScanAsyncCallback = (_, _, _) => Task.FromResult<AgentEnvironmentDetection?>(new("1.101.0-insider", true))
        };
        var catalog = new AgentClientCatalog(
        [
            new("copilot", AgentCommandStrings.Environment_Copilot, copilot),
            new("vscode", AgentCommandStrings.Environment_VsCode, vsCode)
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
        var parseResult = provider.GetRequiredService<RootCommand>().Parse("agent init --environments all");

        await provider.GetRequiredService<AgentInitCommand>().ExecuteCommandAsync(parseResult, cancellation.Token).DefaultTimeout();

        var request = Assert.Single(service.Requests);
        Assert.Equal(catalog.Clients, request.Clients);
        Assert.Equal(
            [("copilot", "1.0.0", false), ("vscode", "1.101.0-insider", true)],
            request.Detections.Select(detection => (detection.Client.Id, detection.Version, detection.IsInsiders)));
        Assert.All(request.Detections, detection =>
            Assert.Same(catalog.Clients.Single(client => client.Id == detection.Client.Id), detection.Client));
        foreach (var environment in new[] { copilot, vsCode })
        {
            var call = Assert.Single(environment.Calls);
            Assert.Same(workingDirectory, call.WorkingDirectory);
            Assert.Equal(workspace.WorkspaceRoot.FullName, call.WorkspaceRoot.FullName);
            Assert.Equal(cancellation.Token, call.CancellationToken);
        }
        originalEvidence = null;
        Assert.Equal(2, request.Detections.Count);
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
            ScanAsyncCallback = (_, _, _) =>
            {
                cancellation.Cancel();
                return Task.FromResult<AgentEnvironmentDetection?>(null);
            }
        };
        var second = new TestAgentClientEnvironment();
        var service = new TestAgentInitService();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options => options.AgentInitServiceFactory = _ => service);
        services.AddSingleton(new AgentClientCatalog([new("first", "First", first), new("second", "Second", second)]));
        using var provider = services.BuildServiceProvider();
        var parseResult = provider.GetRequiredService<RootCommand>().Parse("agent init --environments all");

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
        var parseResult = provider.GetRequiredService<RootCommand>().Parse("agent init --environments none");

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
    [InlineData("copilot,unknown")]
    [InlineData("copilot-cli")]
    [InlineData("copilot-app")]
    [InlineData("claude-code")]
    public async Task AgentInitCommand_UnknownClient_FailsBeforeDiscoveryOrConfiguration(string clients)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var service = new TestAgentInitService();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AgentInitServiceFactory = _ => service;
        });
        using var provider = services.BuildServiceProvider();
        var detector = Assert.IsType<TestAgentClientEnvironment>(provider.GetRequiredService<IAgentEnvironmentScanner>());
        var parseResult = provider.GetRequiredService<RootCommand>().Parse($"agent init --mcp y --environments {clients}");

        Assert.NotEmpty(parseResult.Errors);
        var exitCode = await parseResult.InvokeAsync(new InvocationConfiguration { Output = TextWriter.Null, Error = TextWriter.Null }).DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.Empty(detector.Calls);
        Assert.Empty(service.Requests);
        var expectedError = string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_InvalidEnvironments,
            clients, "copilot,vscode,claude,opencode", "all", "none");
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
        var detector = Assert.IsType<TestAgentClientEnvironment>(provider.GetRequiredService<IAgentEnvironmentScanner>());
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
            .Parse("agent init --mcp --playwright --dotnet-inspect --aspire-skills --environments NONE")
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
            .Parse($"agent init --environments copilot{arguments}").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(expectedRoot.FullName, Assert.Single(service.Requests).WorkspaceRoot.FullName);
        Assert.Equal(4, detector.Calls.Count);
        Assert.All(detector.Calls, scan =>
        {
            Assert.Equal(expectedRoot.FullName, scan.WorkspaceRoot.FullName);
            Assert.Equal(workingDirectory.FullName, scan.WorkingDirectory.FullName);
        });
    }

    [Fact]
    public async Task AgentInitCommand_DotnetInspect_CanBeSelectedBeforeAnAppHostExists()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper).BuildServiceProvider();
        var service = Assert.IsType<TestAgentInitService>(provider.GetRequiredService<IAgentInitService>());

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("agent init --dotnet-inspect y --aspire-skills n --environments claude")
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
                new(AgentAssetKind.AspireSkills, [TestAgentClients.Default.Copilot],
                    "project-settings.json", AgentConfigurationScope.Project,
                    Enum.Parse<AgentConfigurationStatus>(status), "Native client owns acquisition.")
            ])
        };
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interaction;
            options.AgentEnvironmentFactory = _ => new TestAgentClientEnvironment(
                new(TestAgentClients.Default.Copilot, null, false), new(TestAgentClients.Default.Copilot, null, false));
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
            AgentCommandStrings.InitCommand_AspireSkillsAsset, AgentCommandStrings.Environment_Copilot,
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
        Assert.Equal(new[] { AgentCommandStrings.InitCommand_EnvironmentSelectionNotice }.Concat(expectedSubtleMessages), subtleMessages);
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
                new(AgentAssetKind.AspireSkills, [TestAgentClients.Default.Copilot],
                    "project-settings.json", AgentConfigurationScope.Project, AgentConfigurationStatus.Configured, null),
                new(AgentAssetKind.TelemetryHooks, [TestAgentClients.Default.Copilot],
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
                AgentCommandStrings.InitCommand_AspireSkillsAsset, AgentCommandStrings.Environment_Copilot, AgentCommandStrings.InitCommand_ProjectScope, "project-settings.json"),
            string.Format(CultureInfo.CurrentCulture, status == "Blocked" ? AgentCommandStrings.InitCommand_BlockedTarget : AgentCommandStrings.InitCommand_FailedTarget,
                AgentCommandStrings.InitCommand_TelemetryHooksAsset, AgentCommandStrings.Environment_Copilot, AgentCommandStrings.InitCommand_UserScope, "user-hooks.json"),
            AgentCommandStrings.ConfigurationCompletedWithWarnings
        ],
        interaction.DisplayedMessages.Select(message => message.Message));
        Assert.Equal([AgentCommandStrings.InitCommand_EnvironmentSelectionNotice, "The hook could not be written.", AgentCommandStrings.InitCommand_ClientAcquisitionNotice], subtleMessages);
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
            () => command.ExecuteCommandAsync(command.Parse("init --environments copilot"), cancellation.Token)).DefaultTimeout();

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
        var detector = Assert.IsType<TestAgentClientEnvironment>(provider.GetRequiredService<IAgentEnvironmentScanner>());
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
                new(AgentAssetKind.AspireSkills, [TestAgentClients.Default.Copilot],
                    "project-settings.json", AgentConfigurationScope.Project, AgentConfigurationStatus.Configured, null),
                new(AgentAssetKind.AspireSkills, [TestAgentClients.Default.Copilot, TestAgentClients.Default.ClaudeCode],
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
        Assert.Equal([TestAgentClients.Default.Copilot, TestAgentClients.Default.ClaudeCode], result.RegisteredClients);
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
