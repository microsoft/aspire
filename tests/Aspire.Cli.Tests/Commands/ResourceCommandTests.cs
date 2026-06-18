// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Commands;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using InvocationConfiguration = System.CommandLine.InvocationConfiguration;

namespace Aspire.Cli.Tests.Commands;

public class ResourceCommandTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task ResourceCommand_Help_Works()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("resource --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
    }

    [Fact]
    public async Task ResourceCommand_HelpShowsAvailableResourceCommandsMatchesSnapshot()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var helpWriter = new StringWriter();

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ResourceSnapshots =
            [
                CreateResourceSnapshot(
                    "web",
                    CreateCommand("wait-for-browser", "Waits for text in the browser."),
                    CreateCommand("configure", "Configures the browser."),
                    CreateCommand("dashboard-only", "Dashboard-only command.", state: "Enabled", visibility: KnownCommandVisibility.UI),
                    CreateCommand("missing-visibility", "Missing visibility command.", state: "Enabled", visibility: null!)),
                CreateResourceSnapshot(
                    "api",
                    CreateCommand("wait-for-browser", "Waits for text in the browser."),
                    CreateCommand("disabled-command", "Disabled command.", state: "Disabled", visibility: KnownCommandVisibility.Api))
            ]
        };
        await using var provider = CreateServiceProvider(workspace, outputHelper, backchannel);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("resource web --non-interactive --help");

        var exitCode = await result.InvokeAsync(new InvocationConfiguration { Output = helpWriter }).DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        await Verify(helpWriter.ToString(), extension: "txt");
    }

    [Fact]
    public async Task ResourceCommand_HelpDoesNotPromptForOutOfScopeAppHostsMatchesSnapshot()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var helpWriter = new StringWriter();

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            IsInScope = false,
            ResourceSnapshots =
            [
                CreateResourceSnapshot(
                    "web",
                    CreateCommand("wait-for-browser", "Waits for text in the browser."))
            ]
        };
        var interactionService = new TestInteractionService
        {
            PromptForSelectionCallback = (_, _, _, _) => throw new InvalidOperationException("Help should not prompt for an AppHost.")
        };
        await using var provider = CreateServiceProvider(workspace, outputHelper, backchannel, interactionService);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("resource web --help");

        var exitCode = await result.InvokeAsync(new InvocationConfiguration { Output = helpWriter }).DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        await Verify(helpWriter.ToString(), extension: "txt");
    }

    [Fact]
    public async Task ResourceCommand_HelpFallsBackToDefaultHelpWhenAvailableCommandsScanFails()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var helpWriter = new StringWriter();

        var monitor = new TestAuxiliaryBackchannelMonitor
        {
            ScanAsyncCallback = _ => throw new InvalidOperationException("Scan failed.")
        };
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("resource web --help");

        var exitCode = await result.InvokeAsync(new InvocationConfiguration { Output = helpWriter }).DefaultTimeout();
        var helpOutput = helpWriter.ToString();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Contains("Execute a command on a resource", helpOutput);
        Assert.DoesNotContain("Available resource commands:", helpOutput);
    }

    [Fact]
    public async Task ResourceCommand_HelpFallsBackToDefaultHelpWhenAvailableCommandsSnapshotFails()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var helpWriter = new StringWriter();

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            GetResourceSnapshotsHandler = _ => throw new InvalidOperationException("Snapshot failed.")
        };
        await using var provider = CreateServiceProvider(workspace, outputHelper, backchannel);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("resource web --help");

        var exitCode = await result.InvokeAsync(new InvocationConfiguration { Output = helpWriter }).DefaultTimeout();
        var helpOutput = helpWriter.ToString();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Contains("Execute a command on a resource", helpOutput);
        Assert.DoesNotContain("Available resource commands:", helpOutput);
    }

    [Fact]
    public async Task ResourceCommand_HelpWithAppHostDirectoryDoesNotPromptWhenMultipleAppHostsFound()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var helpWriter = new StringWriter();
        var appHostDirectory = workspace.WorkspaceRoot.CreateSubdirectory("Apps");
        var prompted = false;
        var interactionService = new TestInteractionService
        {
            PromptForSelectionCallback = (_, _, _, _) =>
            {
                prompted = true;
                throw new InvalidOperationException("Help should not prompt for an AppHost.");
            }
        };
        var projectLocator = new TestProjectLocator
        {
            UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, behavior, _, _) =>
            {
                Assert.Equal(MultipleAppHostProjectsFoundBehavior.Throw, behavior);
                throw new ProjectLocatorException("multiple", ProjectLocatorFailureReason.MultipleProjectFilesFound);
            }
        };
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.ProjectLocatorFactory = _ => projectLocator;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"""resource web --apphost "{appHostDirectory.FullName}" --help""");

        var exitCode = await result.InvokeAsync(new InvocationConfiguration { Output = helpWriter }).DefaultTimeout();
        var helpOutput = helpWriter.ToString();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.False(prompted);
        Assert.Contains("Execute a command on a resource", helpOutput);
        Assert.DoesNotContain("Available resource commands:", helpOutput);
    }

    [Fact]
    public async Task ResourceCommand_HelpWithAppHostDoesNotPromptWhenMultipleRunningAppHostsMatch()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var helpWriter = new StringWriter();
        var appHostProjectFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        File.WriteAllText(appHostProjectFile.FullName, "<Project />");
        var prompted = false;
        var interactionService = new TestInteractionService
        {
            PromptForSelectionCallback = (_, _, _, _) =>
            {
                prompted = true;
                throw new InvalidOperationException("Help should not prompt for an AppHost.");
            }
        };
        var appHostInfo = new AppHostInformation
        {
            AppHostPath = appHostProjectFile.FullName,
            ProcessId = 1
        };
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection(
            "hash1",
            Path.Combine(workspace.WorkspaceRoot.FullName, "socket1"),
            new TestAppHostAuxiliaryBackchannel { AppHostInfo = appHostInfo });
        monitor.AddConnection(
            "hash2",
            Path.Combine(workspace.WorkspaceRoot.FullName, "socket2"),
            new TestAppHostAuxiliaryBackchannel { AppHostInfo = appHostInfo });
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
            options.InteractionServiceFactory = _ => interactionService;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"""resource web --apphost "{appHostProjectFile.FullName}" --help""");

        var exitCode = await result.InvokeAsync(new InvocationConfiguration { Output = helpWriter }).DefaultTimeout();
        var helpOutput = helpWriter.ToString();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.False(prompted);
        Assert.Contains("Execute a command on a resource", helpOutput);
        Assert.DoesNotContain("Available resource commands:", helpOutput);
    }

    [Fact]
    public async Task ResourceCommand_HelpIncludesHiddenResourceCommandsWhenRequestedMatchesSnapshot()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var helpWriter = new StringWriter();

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ResourceSnapshots =
            [
                CreateResourceSnapshot(
                    "hidden-worker",
                    "Hidden",
                    CreateCommand("inspect-hidden-worker", "Inspects the hidden worker."))
            ]
        };
        await using var provider = CreateServiceProvider(workspace, outputHelper, backchannel);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("resource hidden-worker --include-hidden --help");

        var exitCode = await result.InvokeAsync(new InvocationConfiguration { Output = helpWriter }).DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        await Verify(helpWriter.ToString(), extension: "txt");
    }

    [Fact]
    public async Task ResourceCommand_RequiresResourceArgument()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("resource");

        var error = Assert.Single(result.Errors);
        Assert.Equal("The 'resource' argument is required.", error.Message);

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.NotEqual(CliExitCodes.Success, exitCode);
    }

    [Theory]
    [InlineData("--message hi")]
    [InlineData("--message=hi")]
    public async Task ResourceCommand_RequiresResourceArgumentWhenCommandOptionsAreProvidedWithoutResource(string arguments)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"""resource {arguments}""");

        var error = Assert.Single(result.Errors);
        Assert.Equal("The 'resource' argument is required.", error.Message);

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.NotEqual(CliExitCodes.Success, exitCode);
    }

    [Fact]
    public async Task ResourceCommand_RequiresCommandArgument()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("resource myresource");

        var error = Assert.Single(result.Errors);
        Assert.Equal("The 'command' argument is required.", error.Message);

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.NotEqual(CliExitCodes.Success, exitCode);
    }

    [Theory]
    [InlineData("--message hi")]
    [InlineData("--message=hi")]
    [InlineData("-- --message hi")]
    public async Task ResourceCommand_RequiresCommandArgumentWhenCommandOptionsAreProvidedWithoutCommand(string arguments)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"""resource myresource {arguments}""");

        var error = Assert.Single(result.Errors);
        Assert.Equal("The 'command' argument is required.", error.Message);

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.NotEqual(CliExitCodes.Success, exitCode);
    }

    [Fact]
    public async Task ResourceCommand_AcceptsBothArguments()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("resource myresource my-command --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(CliExitCodes.Success, exitCode);
    }

    [Fact]
    public async Task ResourceCommand_AcceptsProjectOption()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("resource myresource my-command --apphost /path/to/project.csproj --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(CliExitCodes.Success, exitCode);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("restart")]
    [InlineData("rebuild")]
    [InlineData("set-parameter")]
    [InlineData("delete-parameter")]
    [InlineData("parameter-set")]
    [InlineData("parameter-delete")]
    public async Task ResourceCommand_AcceptsWellKnownCommandNames(string commandName)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"resource myresource {commandName} --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
    }

    [Theory]
    [InlineData("start", "Starting resource 'myresource'...", "Resource 'myresource' started successfully.")]
    [InlineData("stop", "Stopping resource 'myresource'...", "Resource 'myresource' stopped successfully.")]
    [InlineData("restart", "Restarting resource 'myresource'...", "Resource 'myresource' restarted successfully.")]
    [InlineData("rebuild", "Rebuilding resource 'myresource'...", "Resource 'myresource' rebuilt successfully.")]
    [InlineData("set-parameter", "Setting parameter for resource 'myresource'...", "Resource 'myresource' set successfully.")]
    [InlineData("delete-parameter", "Deleting parameter for resource 'myresource'...", "Resource 'myresource' deleted successfully.")]
    [InlineData("parameter-set", "Setting parameter for resource 'myresource'...", "Resource 'myresource' set successfully.")]
    [InlineData("parameter-delete", "Deleting parameter for resource 'myresource'...", "Resource 'myresource' deleted successfully.")]
    public async Task ResourceCommand_UsesWellKnownCommandDisplayMetadata(string commandName, string statusMessage, string successMessage)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var statuses = new List<string>();
        var interactionService = new TestInteractionService
        {
            ShowStatusCallback = statuses.Add
        };

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse { Success = true }
        };
        await using var provider = CreateServiceProvider(workspace, outputHelper, backchannel, interactionService);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"resource myresource {commandName}");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(1, backchannel.ExecuteResourceCommandCallCount);
        Assert.Collection(
            statuses,
            status => Assert.Equal("Scanning for running AppHosts...", status),
            status => Assert.Equal(statusMessage, status));
        Assert.Equal(successMessage, Assert.Single(interactionService.DisplayedSuccess));
    }

    [Fact]
    public async Task ResourceCommand_AcceptsProjectOptionWithStart()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("resource myresource start --apphost /path/to/project.csproj --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(CliExitCodes.Success, exitCode);
    }

    [Fact]
    public async Task ResourceCommand_DoesNotUseWellKnownCommandMatchingWithDifferentCase()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var statuses = new List<string>();
        var interactionService = new TestInteractionService
        {
            ShowStatusCallback = statuses.Add
        };

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse { Success = true }
        };
        await using var provider = CreateServiceProvider(workspace, outputHelper, backchannel, interactionService);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("resource myresource Start");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(1, backchannel.ExecuteResourceCommandCallCount);
        Assert.Contains("Validating and executing command 'Start' on resource 'myresource'...", statuses);
    }

    [Fact]
    public async Task ResourceCommand_DoesNotBindPositionalArgumentsByName()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse { Success = true },
            ResourceSnapshots =
            [
                CreateResourceSnapshot(
                    "web-browser-automation",
                    CreateCommand(
                        "fill-browser",
                        CreateArgument("selector"),
                        CreateArgument("value")))
            ]
        };
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("hash", "/tmp/test.sock", backchannel);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("""resource web-browser-automation fill-browser "#name" Aspire""");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.Equal(0, backchannel.ExecuteResourceCommandCallCount);
    }

    [Fact]
    public async Task ResourceCommand_DoesNotSendArgumentsWhenNoneProvided()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse { Success = true }
        };
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("hash", "/tmp/test.sock", backchannel);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("""resource web-browser-automation click-browser""");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Null(backchannel.ExecuteResourceCommandArguments);
        Assert.True(backchannel.ExecuteResourceCommandOptions?.NonInteractive == true);
    }

    [Fact]
    public async Task ResourceCommand_ForwardsOptionEqualsArgumentsByName()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse { Success = true },
            ResourceSnapshots =
            [
                CreateResourceSnapshot(
                    "web-browser-automation",
                    CreateCommand(
                        "wait-for-browser",
                        CreateArgument("text"),
                        CreateArgument("timeoutMilliseconds", inputType: "Number")))
            ]
        };
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("hash", "/tmp/test.sock", backchannel);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("""resource web-browser-automation wait-for-browser "--text=Submitted Aspire!" --timeoutMilliseconds=500""");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        AssertJsonObject(backchannel.ExecuteResourceCommandArguments, ("text", "Submitted Aspire!"), ("timeoutMilliseconds", "500"));
    }

    [Fact]
    public async Task ResourceCommand_LegacyParameterCommandName_UsesCurrentCommandMetadata()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse { Success = true },
            ResourceSnapshots =
            [
                CreateResourceSnapshot(
                    "greeting",
                    CreateCommand(
                        "set-parameter",
                        CreateArgument("Value")))
            ]
        };
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("hash", "/tmp/test.sock", backchannel);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("resource greeting parameter-set --value \"Hello world\"");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        AssertJsonObject(backchannel.ExecuteResourceCommandArguments, ("Value", "Hello world"));
    }

    [Fact]
    public async Task ResourceCommand_DoesNotBindJsonLookingPositionalArgumentByName()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse { Success = true },
            ResourceSnapshots =
            [
                CreateResourceSnapshot(
                    "web-browser-automation",
                    CreateCommand("click-browser", CreateArgument("selector")))
            ]
        };
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("hash", "/tmp/test.sock", backchannel);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse(["resource", "web-browser-automation", "click-browser", """{"selector":"#submit"}"""]);

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.Equal(0, backchannel.ExecuteResourceCommandCallCount);
    }

    [Fact]
    public async Task ResourceCommand_DoesNotForwardPositionalArgumentContainingEqualsAsArray()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse { Success = true }
        };
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("hash", "/tmp/test.sock", backchannel);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("""resource web-browser-automation navigate-browser "https://example.com/?q=aspire" """);

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        AssertJsonObject(backchannel.ExecuteResourceCommandArguments, ("https://example.com/?q=aspire", null));
    }

    [Fact]
    public async Task ResourceCommand_DoesNotForwardExtraArgumentsAsArray()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse { Success = true }
        };
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("hash", "/tmp/test.sock", backchannel);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("""resource web-browser-automation click-browser "#submit" extra""");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        AssertJsonObject(backchannel.ExecuteResourceCommandArguments, ("#submit", null), ("extra", null));
    }

    [Fact]
    public async Task ResourceCommand_DoesNotInferOptionLookingArgumentsWithoutMetadata()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse { Success = true }
        };
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("hash", "/tmp/test.sock", backchannel);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("""resource web-browser-automation click-browser --selector "#submit" --count 2""");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        AssertJsonObject(backchannel.ExecuteResourceCommandArguments, ("--selector #submit", null), ("--count 2", null));
    }

    [Fact]
    public async Task ResourceCommand_DoesNotInferBareOptionWithoutMetadata()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse { Success = true }
        };
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("hash", "/tmp/test.sock", backchannel);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("""resource web-browser-automation configure --verbose""");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        AssertJsonObject(backchannel.ExecuteResourceCommandArguments, ("--verbose", null));
    }

    [Fact]
    public async Task ResourceCommand_RemovesDelimiterWithoutMetadata()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse { Success = true }
        };
        await using var provider = CreateServiceProvider(workspace, outputHelper, backchannel);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("""resource web-browser-automation configure -- --selector "#submit" """);

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        AssertJsonObject(backchannel.ExecuteResourceCommandArguments, ("--selector #submit", null));
    }

    [Fact]
    public async Task ResourceCommand_ForwardsOptionalArgumentsByName()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse { Success = true },
            ResourceSnapshots =
            [
                CreateResourceSnapshot(
                    "web-browser-automation",
                    CreateCommand(
                        "wait-for-browser",
                        CreateArgument("selector"),
                        CreateArgument("timeoutMilliseconds", inputType: "Number")))
            ]
        };
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("hash", "/tmp/test.sock", backchannel);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("""resource web-browser-automation wait-for-browser --timeout-milliseconds 500""");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        AssertJsonObject(backchannel.ExecuteResourceCommandArguments, ("timeoutMilliseconds", "500"));
    }

    [Fact]
    public async Task ResourceCommand_DoesNotMatchCommandMetadataUsingDifferentCase()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse { Success = true },
            ResourceSnapshots =
            [
                CreateResourceSnapshot(
                    "web-browser-automation",
                    CreateCommand(
                        "configure",
                        CreateArgument("message")))
            ]
        };
        await using var provider = CreateServiceProvider(workspace, outputHelper, backchannel);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("""resource web-browser-automation Configure --message hello""");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        AssertJsonObject(backchannel.ExecuteResourceCommandArguments, ("--message hello", null));
    }

    [Fact]
    public async Task ResourceCommand_ForwardsKebabCaseEqualsAndBareBooleanArgumentsByName()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse { Success = true },
            ResourceSnapshots =
            [
                CreateResourceSnapshot(
                    "web-browser-automation",
                    CreateCommand(
                        "configure",
                        CreateArgument("timeoutMilliseconds", inputType: "Number"),
                        CreateArgument("proxy", inputType: "Boolean")))
            ]
        };
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("hash", "/tmp/test.sock", backchannel);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("""resource web-browser-automation configure --timeout-milliseconds=500 --proxy""");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        AssertJsonObject(backchannel.ExecuteResourceCommandArguments, ("timeoutMilliseconds", "500"), ("proxy", "true"));
    }

    [Theory]
    [InlineData("--proxy true", "true")]
    [InlineData("--proxy false", "false")]
    [InlineData("--proxy=false", "false")]
    public async Task ResourceCommand_ForwardsExplicitBooleanCommandOptionValues(string arguments, string expectedValue)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse { Success = true },
            ResourceSnapshots =
            [
                CreateResourceSnapshot(
                    "web-browser-automation",
                    CreateCommand(
                        "configure",
                        CreateArgument("proxy", inputType: "Boolean")))
            ]
        };
        await using var provider = CreateServiceProvider(workspace, outputHelper, backchannel);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"""resource web-browser-automation configure {arguments}""");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        AssertJsonObject(backchannel.ExecuteResourceCommandArguments, ("proxy", expectedValue));
    }

    [Fact]
    public async Task ResourceCommand_ForwardsValidChoiceCommandOption()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse { Success = true },
            ResourceSnapshots =
            [
                CreateResourceSnapshot(
                    "web-browser-automation",
                    CreateCommand(
                        "configure",
                        CreateArgument(
                            "flavor",
                            inputType: "Choice",
                            options: new Dictionary<string, string?>
                            {
                                ["vanilla"] = "Vanilla",
                                ["chocolate"] = "Chocolate"
                            })))
            ]
        };
        await using var provider = CreateServiceProvider(workspace, outputHelper, backchannel);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("""resource web-browser-automation configure --flavor chocolate""");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        AssertJsonObject(backchannel.ExecuteResourceCommandArguments, ("flavor", "chocolate"));
    }

    [Fact]
    public async Task ResourceCommand_ReturnsInvalidCommandForInvalidChoiceCommandOption()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var interactionService = new TestInteractionService();

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse { Success = true },
            ResourceSnapshots =
            [
                CreateResourceSnapshot(
                    "web-browser-automation",
                    CreateCommand(
                        "configure",
                        CreateArgument(
                            "flavor",
                            inputType: "Choice",
                            options: new Dictionary<string, string?>
                            {
                                ["vanilla"] = "Vanilla",
                                ["chocolate"] = "Chocolate"
                            })))
            ]
        };
        await using var provider = CreateServiceProvider(workspace, outputHelper, backchannel, interactionService);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("""resource web-browser-automation configure --flavor strawberry""");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.Equal(0, backchannel.ExecuteResourceCommandCallCount);
        var error = Assert.Single(interactionService.DisplayedErrors);
        Assert.Contains("--flavor", error);
        Assert.Contains("vanilla, chocolate", error);
    }

    [Fact]
    public async Task ResourceCommand_ReturnsInvalidCommandForUnknownCommandOption()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var interactionService = new TestInteractionService();

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse { Success = true },
            ResourceSnapshots =
            [
                CreateResourceSnapshot(
                    "web-browser-automation",
                    CreateCommand("configure", CreateArgument("message")))
            ]
        };
        await using var provider = CreateServiceProvider(workspace, outputHelper, backchannel, interactionService);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("""resource web-browser-automation configure --unknown value""");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.Equal(0, backchannel.ExecuteResourceCommandCallCount);
        var error = Assert.Single(interactionService.DisplayedErrors);
        Assert.Equal("Unrecognized command option '--unknown value'.", error);
    }

    [Fact]
    public async Task ResourceCommand_GroupsUnknownCommandOptionValueWhenCommandMetadataIsMissing()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse { Success = true },
            ResourceSnapshots =
            [
                CreateResourceSnapshot("web-browser-automation")
            ]
        };
        await using var provider = CreateServiceProvider(workspace, outputHelper, backchannel);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("""resource web-browser-automation configure --mm ss""");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        AssertJsonObject(backchannel.ExecuteResourceCommandArguments, ("--mm ss", null));
    }

    [Fact]
    public async Task ResourceCommand_ReturnsInvalidCommandForInvalidBooleanCommandOptionValue()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var interactionService = new TestInteractionService();

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse { Success = true },
            ResourceSnapshots =
            [
                CreateResourceSnapshot(
                    "web-browser-automation",
                    CreateCommand("configure", CreateArgument("proxy", inputType: "Boolean")))
            ]
        };
        await using var provider = CreateServiceProvider(workspace, outputHelper, backchannel, interactionService);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("""resource web-browser-automation configure --proxy maybe""");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.Equal(0, backchannel.ExecuteResourceCommandCallCount);
        var error = Assert.Single(interactionService.DisplayedErrors);
        Assert.Contains("maybe", error);
    }

    [Fact]
    public async Task ResourceCommand_ReturnsInvalidCommandForDisabledCommandOption()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var interactionService = new TestInteractionService();

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse { Success = true },
            ResourceSnapshots =
            [
                CreateResourceSnapshot(
                    "web-browser-automation",
                    CreateCommand("configure", CreateArgument("saveToUserSecrets", inputType: "Boolean", disabled: true)))
            ]
        };
        await using var provider = CreateServiceProvider(workspace, outputHelper, backchannel, interactionService);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("""resource web-browser-automation configure --save-to-user-secrets true""");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.Equal(0, backchannel.ExecuteResourceCommandCallCount);
        Assert.Equal("Option '--save-to-user-secrets' is disabled.", Assert.Single(interactionService.DisplayedErrors));
    }

    [Fact]
    public async Task ResourceCommand_DisplaysValidationErrorArgumentNamesAsCliOptions()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var interactionService = new TestInteractionService();

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse
            {
                Success = false,
                Message = "Command argument validation failed.",
                ValidationErrors =
                [
                    new ResourceCommandArgumentValidationError
                    {
                        ArgumentName = "timeoutSeconds",
                        ErrorMessage = "Value must be greater than 0."
                    }
                ]
            },
            ResourceSnapshots =
            [
                CreateResourceSnapshot(
                    "web-browser-automation",
                    CreateCommand("configure", CreateArgument("timeoutSeconds", inputType: "Number")))
            ]
        };
        await using var provider = CreateServiceProvider(workspace, outputHelper, backchannel, interactionService);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("""resource web-browser-automation configure --timeout-seconds 0""");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.FailedToExecuteResourceCommand, exitCode);
        var error = Assert.Single(interactionService.DisplayedErrors);
        Assert.Contains("Failed to validate command arguments for command 'configure' on resource 'web-browser-automation'", error);
        Assert.DoesNotContain("Command argument validation failed.", error);
        Assert.Contains("--timeout-seconds: Value must be greater than 0.", error);
        Assert.DoesNotContain("timeoutSeconds:", error);
    }

    [Fact]
    public async Task ResourceCommand_FailedExecution_DisplaysAppHostCliLogFilePath()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var interactionService = new TestInteractionService();

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            AppHostInfo = new AppHostInformation
            {
                AppHostPath = "/app/AppHost.csproj",
                ProcessId = 42,
                CliLogFilePath = "/tmp/aspire-logs/cli_apphost_20260516.log"
            },
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse
            {
                Success = false,
                Message = "Something went wrong."
            }
        };
        await using var provider = CreateServiceProvider(workspace, outputHelper, backchannel, interactionService);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("resource myresource my-command");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.FailedToExecuteResourceCommand, exitCode);

        var executionContext = provider.GetRequiredService<CliExecutionContext>();
        var expectedCliLogMessage = string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.SeeLogsAt, executionContext.LogFilePath);
        var expectedAppHostLogMessage = string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.SeeAppHostLogsAt, "/tmp/aspire-logs/cli_apphost_20260516.log");

        // Verify both the CLI log path and app host log path are displayed when the command fails
        Assert.Contains(interactionService.DisplayedMessages, m => m.Message == expectedCliLogMessage);
        Assert.Contains(interactionService.DisplayedMessages, m => m.Message == expectedAppHostLogMessage);
    }

    [Fact]
    public async Task ResourceCommand_FailsWhenCommandUsesInteractionService()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var interactionService = new TestInteractionService();

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ResourceSnapshots =
            [
                CreateResourceSnapshot(
                    "web",
                    CreateCommand("configure", "Configures the resource."))
            ]
        };
        await using var provider = CreateServiceProvider(workspace, outputHelper, backchannel, interactionService);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("""resource web configure""");

        await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(1, backchannel.ExecuteResourceCommandCallCount);
        Assert.True(backchannel.ExecuteResourceCommandOptions?.NonInteractive == true);
    }

    [Fact]
    public async Task ResourceCommand_ForwardsCustomChoiceCommandOptionWhenAllowed()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse { Success = true },
            ResourceSnapshots =
            [
                CreateResourceSnapshot(
                    "web-browser-automation",
                    CreateCommand(
                        "configure",
                        CreateArgument(
                            "flavor",
                            inputType: "Choice",
                            options: new Dictionary<string, string?>
                            {
                                ["vanilla"] = "Vanilla",
                                ["chocolate"] = "Chocolate"
                            },
                            allowCustomChoice: true)))
            ]
        };
        await using var provider = CreateServiceProvider(workspace, outputHelper, backchannel);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("""resource web-browser-automation configure --flavor strawberry""");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        AssertJsonObject(backchannel.ExecuteResourceCommandArguments, ("flavor", "strawberry"));
    }

    [Fact]
    public async Task ResourceCommand_DoesNotSerializeOmittedCommandOptionDefaults()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse { Success = true },
            ResourceSnapshots =
            [
                CreateResourceSnapshot(
                    "web-browser-automation",
                    CreateCommand(
                        "configure",
                        CreateArgument("message", value: "hello"),
                        CreateArgument("count", inputType: "Number", required: true, value: "5"),
                        CreateArgument("enabled", inputType: "Boolean", value: "true")))
            ]
        };
        await using var provider = CreateServiceProvider(workspace, outputHelper, backchannel);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("""resource web-browser-automation configure""");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        AssertJsonObject(backchannel.ExecuteResourceCommandArguments);
    }

    [Theory]
    [InlineData("--message", "--message")]
    [InlineData("--count", "--count")]
    [InlineData("-- --message", "--message")]
    public async Task ResourceCommand_ReturnsInvalidCommandForMissingCommandOptionValue(string arguments, string expectedOptionName)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var interactionService = new TestInteractionService();

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse { Success = true },
            ResourceSnapshots =
            [
                CreateResourceSnapshot(
                    "web-browser-automation",
                    CreateCommand(
                        "configure",
                        CreateArgument("message"),
                        CreateArgument("count", inputType: "Number")))
            ]
        };
        await using var provider = CreateServiceProvider(workspace, outputHelper, backchannel, interactionService);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"""resource web-browser-automation configure {arguments}""");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.Equal(0, backchannel.ExecuteResourceCommandCallCount);
        var error = Assert.Single(interactionService.DisplayedErrors);
        Assert.Contains(expectedOptionName, error);
    }

    [Fact]
    public async Task ResourceCommand_ReturnsInvalidCommandForDuplicateCommandOptionUsingExactAndKebabAliases()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var interactionService = new TestInteractionService();

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse { Success = true },
            ResourceSnapshots =
            [
                CreateResourceSnapshot(
                    "web-browser-automation",
                    CreateCommand("configure", CreateArgument("timeoutMilliseconds", inputType: "Number")))
            ]
        };
        await using var provider = CreateServiceProvider(workspace, outputHelper, backchannel, interactionService);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("""resource web-browser-automation configure --timeoutMilliseconds 1 --timeout-milliseconds 2""");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.Equal(0, backchannel.ExecuteResourceCommandCallCount);
        var error = Assert.Single(interactionService.DisplayedErrors);
        Assert.Contains("--timeoutMilliseconds", error);
        Assert.Contains("2 were provided", error);
    }

    [Fact]
    public async Task ResourceCommand_ReturnsInvalidCommandForDuplicateCommandOption()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse { Success = true },
            ResourceSnapshots =
            [
                CreateResourceSnapshot(
                    "web-browser-automation",
                    CreateCommand("configure", CreateArgument("message")))
            ]
        };
        await using var provider = CreateServiceProvider(workspace, outputHelper, backchannel);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("""resource web-browser-automation configure --message first --message second""");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.Equal(0, backchannel.ExecuteResourceCommandCallCount);
    }

    [Fact]
    public async Task ResourceCommand_ReturnsInvalidCommandForMissingRequiredCommandOption()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var interactionService = new TestInteractionService();

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse { Success = true },
            ResourceSnapshots =
            [
                CreateResourceSnapshot(
                    "web-browser-automation",
                    CreateCommand(
                        "wait-for-browser",
                        CreateArgument("selector", required: true),
                        CreateArgument("timeoutMilliseconds", inputType: "Number")))
            ]
        };
        await using var provider = CreateServiceProvider(workspace, outputHelper, backchannel, interactionService);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("""resource web-browser-automation wait-for-browser --timeout-milliseconds 500""");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.Equal(0, backchannel.ExecuteResourceCommandCallCount);
        var error = Assert.Single(interactionService.DisplayedErrors);
        Assert.Contains("--selector", error);
    }

    [Fact]
    public async Task ResourceCommand_ReturnsInvalidCommandForMultipleMissingRequiredCommandOptions()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var interactionService = new TestInteractionService();

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse { Success = true },
            ResourceSnapshots =
            [
                CreateResourceSnapshot(
                    "web-browser-automation",
                    CreateCommand(
                        "wait-for-browser",
                        CreateArgument("selector", required: true),
                        CreateArgument("text", required: true)))
            ]
        };
        await using var provider = CreateServiceProvider(workspace, outputHelper, backchannel, interactionService);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("""resource web-browser-automation wait-for-browser""");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.Equal(0, backchannel.ExecuteResourceCommandCallCount);
        var error = Assert.Single(interactionService.DisplayedErrors);
        Assert.Contains("--selector", error);
        Assert.Contains("--text", error);
    }

    [Fact]
    public async Task ResourceCommand_ReturnsInvalidCommandForInvalidNumberCommandOption()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse { Success = true },
            ResourceSnapshots =
            [
                CreateResourceSnapshot(
                    "web-browser-automation",
                    CreateCommand(
                        "wait-for-browser",
                        CreateArgument("timeoutMilliseconds", inputType: "Number")))
            ]
        };
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("hash", "/tmp/test.sock", backchannel);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("""resource web-browser-automation wait-for-browser --timeout-milliseconds not-a-number""");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.Equal(0, backchannel.ExecuteResourceCommandCallCount);
    }

    [Fact]
    public async Task ResourceCommand_DoesNotBindMixedNamedAndPositionalArgumentsByName()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse { Success = true },
            ResourceSnapshots =
            [
                CreateResourceSnapshot(
                    "web-browser-automation",
                    CreateCommand(
                        "fill-browser",
                        CreateArgument("selector"),
                        CreateArgument("value")))
            ]
        };
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("hash", "/tmp/test.sock", backchannel);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("""resource web-browser-automation fill-browser --selector "#name" Aspire""");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.Equal(0, backchannel.ExecuteResourceCommandCallCount);
    }

    [Fact]
    public async Task ResourceCommand_ForwardsCommandOptionAfterDelimiterWhenNameCollidesWithCliOption()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse { Success = true },
            ResourceSnapshots =
            [
                CreateResourceSnapshot(
                    "web-browser-automation",
                    CreateCommand(
                        "configure",
                        CreateArgument("logLevel")))
            ]
        };
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("hash", "/tmp/test.sock", backchannel);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("""resource web-browser-automation configure -- --log-level Debug""");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        AssertJsonObject(backchannel.ExecuteResourceCommandArguments, ("logLevel", "Debug"));
    }

    [Fact]
    public async Task ResourceCommand_ForwardsCommandOptionsAfterDelimiter()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse { Success = true },
            ResourceSnapshots =
            [
                CreateResourceSnapshot(
                    "web-browser-automation",
                    CreateCommand(
                        "configure",
                        CreateArgument("message"),
                        CreateArgument("timeoutMilliseconds", inputType: "Number")))
            ]
        };
        await using var provider = CreateServiceProvider(workspace, outputHelper, backchannel);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("""resource web-browser-automation configure -- --message "from delimiter" --timeout-milliseconds 10""");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        AssertJsonObject(backchannel.ExecuteResourceCommandArguments, ("message", "from delimiter"), ("timeoutMilliseconds", "10"));
    }

    [Fact]
    public async Task ResourceCommand_IncludeHiddenExecutesHiddenResourceCommandWithMetadata()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse { Success = true },
            ResourceSnapshots =
            [
                CreateResourceSnapshot(
                    "hidden-worker",
                    "Hidden",
                    CreateCommand(
                        "configure",
                        CreateArgument("message")))
            ]
        };
        await using var provider = CreateServiceProvider(workspace, outputHelper, backchannel);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("resource hidden-worker configure --include-hidden -- --message \"from hidden resource\"");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(1, backchannel.ExecuteResourceCommandCallCount);
        AssertJsonObject(backchannel.ExecuteResourceCommandArguments, ("message", "from hidden resource"));
    }

    [Fact]
    public async Task ResourceCommand_DoesNotForwardCommandOptionWithoutDelimiterWhenNameCollidesWithCliOption()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse { Success = true },
            ResourceSnapshots =
            [
                CreateResourceSnapshot(
                    "web-browser-automation",
                    CreateCommand(
                        "configure",
                        CreateArgument("logLevel")))
            ]
        };
        await using var provider = CreateServiceProvider(workspace, outputHelper, backchannel);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("""resource web-browser-automation configure --log-level Debug""");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        AssertJsonObject(backchannel.ExecuteResourceCommandArguments);
    }

    [Fact]
    public async Task ResourceCommand_ForwardsExactArgumentNameThatStartsWithNo()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse { Success = true },
            ResourceSnapshots =
            [
                CreateResourceSnapshot(
                    "web-browser-automation",
                    CreateCommand(
                        "configure",
                        CreateArgument("noProxy")))
            ]
        };
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("hash", "/tmp/test.sock", backchannel);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("""resource web-browser-automation configure --no-proxy localhost""");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        AssertJsonObject(backchannel.ExecuteResourceCommandArguments, ("noProxy", "localhost"));
    }

    [Fact]
    public async Task ResourceCommand_LoadArgumentsWritesLoadedArgumentInputsAsJson()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var output = new StringWriter();

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse
            {
                Success = false,
                Message = "Command argument validation failed.",
                ArgumentInputs =
                [
                    CreateArgument("browser", inputType: "Choice", value: "Chrome"),
                    CreateArgument(
                        "profile",
                        inputType: "Choice",
                        options: new Dictionary<string, string?>
                        {
                            ["Default"] = "Default"
                        })
                ]
            },
            ResourceSnapshots =
            [
                CreateResourceSnapshot(
                    "web-browser-automation",
                    CreateCommand(
                        "configure",
                        CreateArgument("browser", inputType: "Choice")))
            ]
        };
        await using var provider = CreateServiceProvider(workspace, outputHelper, backchannel);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("""resource web-browser-automation configure --load-arguments -- --browser Chrome""");

        var exitCode = await result.InvokeAsync(new InvocationConfiguration { Output = output }).DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.True(backchannel.ExecuteResourceCommandOptions?.ValidateOnly);
        Assert.True(backchannel.ExecuteResourceCommandOptions?.ReturnArgumentInputs);
        AssertJsonObject(backchannel.ExecuteResourceCommandArguments, ("browser", "Chrome"));

        var json = JsonNode.Parse(output.ToString());
        var argumentInputs = Assert.IsType<JsonArray>(json);
        Assert.Equal(2, argumentInputs.Count);
        Assert.Equal("browser", argumentInputs[0]!["name"]!.GetValue<string>());
        Assert.Equal("Chrome", argumentInputs[0]!["value"]!.GetValue<string>());
        Assert.Equal("profile", argumentInputs[1]!["name"]!.GetValue<string>());
        Assert.Equal("Default", argumentInputs[1]!["options"]!["Default"]!.GetValue<string>());
    }

    [Fact]
    public async Task ResourceCommand_LoadArgumentsDoesNotWriteJsonWhenArgumentInputsAreMissing()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var output = new StringWriter();

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse
            {
                Success = false,
                Message = "Loaded argument inputs were not returned."
            },
            ResourceSnapshots =
            [
                CreateResourceSnapshot(
                    "web-browser-automation",
                    CreateCommand(
                        "configure",
                        CreateArgument("browser", inputType: "Choice")))
            ]
        };
        var interactionService = new TestInteractionService();
        await using var provider = CreateServiceProvider(workspace, outputHelper, backchannel, interactionService);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("""resource web-browser-automation configure --load-arguments -- --browser Chrome""");

        var exitCode = await result.InvokeAsync(new InvocationConfiguration { Output = output }).DefaultTimeout();

        Assert.Equal(CliExitCodes.FailedToExecuteResourceCommand, exitCode);
        Assert.True(backchannel.ExecuteResourceCommandOptions?.ValidateOnly);
        Assert.True(backchannel.ExecuteResourceCommandOptions?.ReturnArgumentInputs);
        Assert.DoesNotContain("[]", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Loaded argument inputs were not returned.", interactionService.DisplayedErrors);
    }

    [Fact]
    public async Task ResourceCommand_LoadArgumentsReportsFallbackErrorWhenArgumentInputsAndMessageAreMissing()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var output = new StringWriter();

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse
            {
                Success = false
            },
            ResourceSnapshots =
            [
                CreateResourceSnapshot(
                    "web-browser-automation",
                    CreateCommand(
                        "configure",
                        CreateArgument("browser", inputType: "Choice")))
            ]
        };
        var interactionService = new TestInteractionService();
        await using var provider = CreateServiceProvider(workspace, outputHelper, backchannel, interactionService);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("""resource web-browser-automation configure --load-arguments -- --browser Chrome""");

        var exitCode = await result.InvokeAsync(new InvocationConfiguration { Output = output }).DefaultTimeout();

        Assert.Equal(CliExitCodes.FailedToExecuteResourceCommand, exitCode);
        Assert.Contains("AppHost returned no loaded argument inputs.", interactionService.DisplayedErrors);
    }

    [Fact]
    public async Task ResourceCommand_LoadArgumentsAllowsPartialDynamicArguments()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var output = new StringWriter();

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse
            {
                Success = false,
                Message = "Command argument validation failed.",
                ArgumentInputs =
                [
                    CreateArgument("category", inputType: "Choice", required: true, value: "fruit"),
                    CreateArgument("item", inputType: "Choice", required: true, options: new Dictionary<string, string?> { ["banana"] = "Banana" }),
                ]
            },
            ResourceSnapshots =
            [
                CreateResourceSnapshot(
                    "argument-commands",
                    CreateCommand(
                        "dependent-arguments",
                        CreateArgument("category", inputType: "Choice", required: true, options: new Dictionary<string, string?> { ["fruit"] = "Fruit" }),
                        CreateArgument("item", inputType: "Choice", required: true, disabled: true, dynamicLoading: new ResourceSnapshotCommandArgumentDynamicLoading { DependsOnInputs = ["category"] })))
            ]
        };
        await using var provider = CreateServiceProvider(workspace, outputHelper, backchannel);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("""resource argument-commands dependent-arguments --load-arguments -- --category fruit""");

        var exitCode = await result.InvokeAsync(new InvocationConfiguration { Output = output }).DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.True(backchannel.ExecuteResourceCommandOptions?.ValidateOnly);
        Assert.True(backchannel.ExecuteResourceCommandOptions?.ReturnArgumentInputs);
        AssertJsonObject(backchannel.ExecuteResourceCommandArguments, ("category", "fruit"));
        Assert.DoesNotContain("Required option '--item'", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResourceCommand_ExecuteAllowsDynamicallyEnabledArguments()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse { Success = true },
            ResourceSnapshots =
            [
                CreateResourceSnapshot(
                    "argument-commands",
                    CreateCommand(
                        "dependent-arguments",
                        CreateArgument("category", inputType: "Choice", required: true, options: new Dictionary<string, string?> { ["fruit"] = "Fruit" }),
                        CreateArgument("item", inputType: "Choice", required: true, disabled: true, dynamicLoading: new ResourceSnapshotCommandArgumentDynamicLoading { DependsOnInputs = ["category"] }),
                        CreateArgument("quantity", inputType: "Number", required: true),
                        CreateArgument("priority", inputType: "Choice", disabled: true, dynamicLoading: new ResourceSnapshotCommandArgumentDynamicLoading { DependsOnInputs = ["item"] })))
            ]
        };
        await using var provider = CreateServiceProvider(workspace, outputHelper, backchannel);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("""resource argument-commands dependent-arguments -- --category=fruit --item=banana --quantity=2 --priority=express""");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        AssertJsonObject(
            backchannel.ExecuteResourceCommandArguments,
            ("category", "fruit"),
            ("item", "banana"),
            ("quantity", "2"),
            ("priority", "express"));
    }

    [Fact]
    public async Task ResourceCommand_DoesNotSynthesizeNegatedBooleanArgument()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse { Success = true },
            ResourceSnapshots =
            [
                CreateResourceSnapshot(
                    "web-browser-automation",
                    CreateCommand(
                        "configure",
                        CreateArgument("proxy", inputType: "Boolean")))
            ]
        };
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("hash", "/tmp/test.sock", backchannel);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("""resource web-browser-automation configure --no-proxy""");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.Equal(0, backchannel.ExecuteResourceCommandCallCount);
    }

    [Fact]
    public async Task ResourceCommand_ResourceOnlyHelpUsesDefaultHelp()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var helpWriter = new StringWriter();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("""resource myresource --help""");

        var exitCode = await result.InvokeAsync(new InvocationConfiguration { Output = helpWriter }).DefaultTimeout();
        var helpOutput = helpWriter.ToString();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Contains("Execute a command on a resource", helpOutput);
        Assert.Contains("resource <resource> <command>", helpOutput);
    }

    [Fact]
    public async Task ResourceCommand_CommandSpecificHelpShowsArgumentInputs()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var helpWriter = new StringWriter();

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ResourceSnapshots =
            [
                CreateResourceSnapshot(
                    "web-browser-automation",
                    CreateCommand(
                        "wait-for-browser",
                        "Waits for text in the browser.",
                        CreateArgument("selector", description: "Selector to wait for.", required: true),
                        CreateArgument("timeoutMilliseconds", description: "Timeout in milliseconds.", inputType: "Number")))
            ]
        };
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("hash", "/tmp/test.sock", backchannel);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("""resource web-browser-automation wait-for-browser --help""");

        var exitCode = await result.InvokeAsync(new InvocationConfiguration { Output = helpWriter }).DefaultTimeout();
        var helpOutput = helpWriter.ToString();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Contains("Waits for text in the browser.", helpOutput);
        Assert.Contains("--selector <value>", helpOutput);
        Assert.Contains("Selector to wait for. Required.", helpOutput);
        Assert.Contains("--timeout-milliseconds <value>", helpOutput);
        Assert.Contains("Timeout in milliseconds.", helpOutput);
    }

    [Fact]
    public async Task ResourceCommand_CommandSpecificHelpBeforeDelimiterShowsHelp()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var helpWriter = new StringWriter();

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ResourceSnapshots =
            [
                CreateResourceSnapshot(
                    "web-browser-automation",
                    CreateCommand(
                        "wait-for-browser",
                        "Waits for text in the browser.",
                        CreateArgument("selector", description: "Selector to wait for.", required: true)))
            ]
        };
        await using var provider = CreateServiceProvider(workspace, outputHelper, backchannel);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("""resource web-browser-automation wait-for-browser --help -- --selector #main""");

        var exitCode = await result.InvokeAsync(new InvocationConfiguration { Output = helpWriter }).DefaultTimeout();
        var helpOutput = helpWriter.ToString();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(0, backchannel.ExecuteResourceCommandCallCount);
        Assert.Contains("Waits for text in the browser.", helpOutput);
        Assert.Contains("--selector <value>", helpOutput);
    }

    [Fact]
    public async Task ResourceCommand_HelpAfterDelimiterIsForwardedToResourceCommand()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var helpWriter = new StringWriter();

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse { Success = true },
            ResourceSnapshots =
            [
                CreateResourceSnapshot(
                    "web-browser-automation",
                    CreateCommand("custom-command"))
            ]
        };
        await using var provider = CreateServiceProvider(workspace, outputHelper, backchannel);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("""resource web-browser-automation custom-command -- --help""");

        var exitCode = await result.InvokeAsync(new InvocationConfiguration { Output = helpWriter }).DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(1, backchannel.ExecuteResourceCommandCallCount);
        Assert.DoesNotContain("Usage:", helpWriter.ToString());
        AssertJsonObject(backchannel.ExecuteResourceCommandArguments, ("--help", null));
    }

    [Fact]
    public async Task ResourceCommand_CommandSpecificHelpShowsDelimiterForArgumentNamesThatCollideWithCliOptions()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var helpWriter = new StringWriter();

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ResourceSnapshots =
            [
                CreateResourceSnapshot(
                    "web-browser-automation",
                    CreateCommand(
                        "configure",
                        "Configures the browser.",
                        CreateArgument("logLevel", description: "Log level for the resource command.")))
            ]
        };
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("hash", "/tmp/test.sock", backchannel);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("""resource web-browser-automation configure --help""");

        var exitCode = await result.InvokeAsync(new InvocationConfiguration { Output = helpWriter }).DefaultTimeout();
        var helpOutput = helpWriter.ToString();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Contains("aspire resource web-browser-automation configure [options] [[--] <command-options>...]", helpOutput);
        Assert.Contains("--log-level <value>", helpOutput);
        Assert.Contains("Log level for the resource command.", helpOutput);
        Assert.Contains("Use `-- --log-level <value>` to pass this command", helpOutput);
    }

    [Fact]
    public async Task ResourceCommand_CommandSpecificHelpShowsVisibleCliOptions()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var helpWriter = new StringWriter();

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ResourceSnapshots =
            [
                CreateResourceSnapshot(
                    "web-browser-automation",
                    CreateCommand("configure", "Configures the browser.", CreateArgument("selector")))
            ]
        };
        await using var provider = CreateServiceProvider(workspace, outputHelper, backchannel);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("""resource web-browser-automation configure --help""");

        var exitCode = await result.InvokeAsync(new InvocationConfiguration { Output = helpWriter }).DefaultTimeout();
        var helpOutput = helpWriter.ToString();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Contains("--apphost <apphost>", helpOutput);
        Assert.Contains("-?, -h, /?, /h, --help", helpOutput);
        Assert.Contains("-l, --log-level <log-level>", helpOutput);
        Assert.Contains("--non-interactive", helpOutput);
        Assert.Contains("--nologo", helpOutput);
        Assert.Contains("--banner", helpOutput);
        Assert.Contains("--wait-for-debugger", helpOutput);
        Assert.DoesNotContain("--debug", helpOutput);
    }

    [Fact]
    public async Task ResourceCommand_CommandSpecificHelpDoesNotMarkDefaultedRequiredArgumentsAsRequired()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var helpWriter = new StringWriter();

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ResourceSnapshots =
            [
                CreateResourceSnapshot(
                    "web-browser-automation",
                    CreateCommand(
                        "configure",
                        "Configures the browser.",
                        CreateArgument("count", description: "Count value.", inputType: "Number", required: true, value: "5")))
            ]
        };
        await using var provider = CreateServiceProvider(workspace, outputHelper, backchannel);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("""resource web-browser-automation configure --help""");

        var exitCode = await result.InvokeAsync(new InvocationConfiguration { Output = helpWriter }).DefaultTimeout();
        var helpOutput = helpWriter.ToString();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Contains("--count <value>", helpOutput);
        Assert.Contains("Count value. Default: 5.", helpOutput);
        Assert.DoesNotContain("Count value. Required.", helpOutput);
    }

    [Fact]
    public async Task ResourceCommand_CommandSpecificHelpFallsBackToDefaultHelpWhenAppHostIsNotRunning()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var helpWriter = new StringWriter();

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("""resource web-browser-automation configure --help""");

        var exitCode = await result.InvokeAsync(new InvocationConfiguration { Output = helpWriter }).DefaultTimeout();
        var helpOutput = helpWriter.ToString();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Contains("Usage:", helpOutput);
        Assert.DoesNotContain("Command options:", helpOutput);
    }

    [Fact]
    public async Task ResourceCommand_CommandSpecificHelpFallsBackToDefaultHelpWhenCommandMetadataIsMissing()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var helpWriter = new StringWriter();

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ResourceSnapshots =
            [
                CreateResourceSnapshot("web-browser-automation")
            ]
        };
        await using var provider = CreateServiceProvider(workspace, outputHelper, backchannel);

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("""resource web-browser-automation configure --help""");

        var exitCode = await result.InvokeAsync(new InvocationConfiguration { Output = helpWriter }).DefaultTimeout();
        var helpOutput = helpWriter.ToString();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Contains("Usage:", helpOutput);
        Assert.DoesNotContain("Command options:", helpOutput);
    }

    [Fact]
    public async Task ResourceCommand_CommandSpecificHelpForAllArgumentTypesMatchesSnapshot()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var helpWriter = new StringWriter();

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ResourceSnapshots =
            [
                CreateResourceSnapshot(
                    "argument-commands",
                    CreateCommand(
                        "all-argument-types",
                        "Exercises command help for every supported resource command argument shape.",
                        CreateArgument("message", description: "Text input shown as a value option.", required: true),
                        CreateArgument("secret", description: "Secret text input shown as a value option.", inputType: "SecretText"),
                        CreateArgument("count", description: "Number input with a default.", inputType: "Number", required: true, value: "5"),
                        CreateArgument("enabled", description: "Boolean input shown as a flag.", inputType: "Boolean", value: "true"),
                        CreateArgument(
                            "flavor",
                            description: "Choice input with fixed allowed values.",
                            inputType: "Choice",
                            options: new Dictionary<string, string?>
                            {
                                ["vanilla"] = "Vanilla",
                                ["chocolate"] = "Chocolate"
                            }),
                        CreateArgument("logLevel", description: "Command option colliding with a CLI option.")))
            ]
        };
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("hash", "/tmp/test.sock", backchannel);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("""resource argument-commands all-argument-types --help""");

        var exitCode = await result.InvokeAsync(new InvocationConfiguration { Output = helpWriter }).DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        await Verify(helpWriter.ToString(), extension: "txt");
    }

    private static void AssertJsonObject(JsonNode? actual, params (string Name, string? Value)[] expected)
    {
        Assert.NotNull(actual);
        var actualObject = Assert.IsType<JsonObject>(actual);
        Assert.Equal(expected.Length, actualObject.Count);

        foreach (var (name, value) in expected)
        {
            Assert.True(actualObject.TryGetPropertyValue(name, out var actualValue), $"Expected argument '{name}' to exist.");
            if (value is null)
            {
                Assert.Null(actualValue);
            }
            else
            {
                var actualElement = Assert.IsAssignableFrom<JsonValue>(actualValue);
                Assert.Equal(JsonValueKind.String, actualElement.GetValueKind());
                Assert.Equal(value, actualElement.GetValue<string>());
            }
        }
    }

    private static ResourceSnapshot CreateResourceSnapshot(string name, params ResourceSnapshotCommand[] commands)
    {
        return CreateResourceSnapshot(name, state: "Running", commands);
    }

    private static ResourceSnapshot CreateResourceSnapshot(string name, string state, params ResourceSnapshotCommand[] commands)
    {
        return new ResourceSnapshot
        {
            Name = name,
            DisplayName = name,
            State = state,
            Commands = commands
        };
    }

    private static ServiceProvider CreateServiceProvider(
        TemporaryWorkspace workspace,
        ITestOutputHelper outputHelper,
        TestAppHostAuxiliaryBackchannel backchannel,
        TestInteractionService? interactionService = null)
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("hash", "/tmp/test.sock", backchannel);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
            if (interactionService is not null)
            {
                options.InteractionServiceFactory = _ => interactionService;
            }
        });

        return services.BuildServiceProvider();
    }

    private static ResourceSnapshotCommand CreateCommand(string name, params ResourceSnapshotCommandArgument[] argumentInputs)
    {
        return CreateCommand(name, description: null, argumentInputs);
    }

    private static ResourceSnapshotCommand CreateCommand(string name, string? description, params ResourceSnapshotCommandArgument[] argumentInputs)
    {
        return CreateCommand(name, description, state: "Enabled", visibility: KnownCommandVisibility.Default, argumentInputs);
    }

    private static ResourceSnapshotCommand CreateCommand(string name, string? description, string state, string? visibility, params ResourceSnapshotCommandArgument[] argumentInputs)
    {
        return new ResourceSnapshotCommand
        {
            Name = name,
            Description = description,
            State = state,
            Visibility = visibility!,
            ArgumentInputs = argumentInputs
        };
    }

    private static ResourceSnapshotCommandArgument CreateArgument(
        string name,
        string? description = null,
        string inputType = "Text",
        bool required = false,
        string? value = null,
        Dictionary<string, string?>? options = null,
        bool allowCustomChoice = false,
        bool disabled = false,
        ResourceSnapshotCommandArgumentDynamicLoading? dynamicLoading = null)
    {
        return new ResourceSnapshotCommandArgument
        {
            Name = name,
            Description = description,
            InputType = inputType,
            Required = required,
            Value = value,
            Options = options,
            AllowCustomChoice = allowCustomChoice,
            Disabled = disabled,
            DynamicLoading = dynamicLoading
        };
    }
}
