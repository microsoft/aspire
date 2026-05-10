// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Commands;
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

        Assert.Equal(ExitCodeConstants.Success, exitCode);
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
        Assert.NotEqual(ExitCodeConstants.Success, exitCode);
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
        Assert.NotEqual(ExitCodeConstants.Success, exitCode);
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
        Assert.NotEqual(ExitCodeConstants.Success, exitCode);
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
        Assert.NotEqual(ExitCodeConstants.Success, exitCode);
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
        Assert.Equal(ExitCodeConstants.Success, exitCode);
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
        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public async Task ResourceCommand_AcceptsWellKnownCommandNames()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();

        // Test with start
        var startResult = command.Parse("resource myresource start --help");
        var startExitCode = await startResult.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.Success, startExitCode);

        // Test with stop
        var stopResult = command.Parse("resource myresource stop --help");
        var stopExitCode = await stopResult.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.Success, stopExitCode);

        // Test with restart
        var restartResult = command.Parse("resource myresource restart --help");
        var restartExitCode = await restartResult.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.Success, restartExitCode);
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
        Assert.Equal(ExitCodeConstants.Success, exitCode);
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

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.Equal(1, backchannel.ExecuteResourceCommandCallCount);
        Assert.Contains("Executing command 'Start' on resource 'myresource'...", statuses);
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

        Assert.Equal(ExitCodeConstants.InvalidCommand, exitCode);
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

        Assert.Equal(ExitCodeConstants.Success, exitCode);
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

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        AssertJsonObject(backchannel.ExecuteResourceCommandArguments, ("text", "Submitted Aspire!"), ("timeoutMilliseconds", "500"));
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

        Assert.Equal(ExitCodeConstants.InvalidCommand, exitCode);
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

        Assert.Equal(ExitCodeConstants.Success, exitCode);
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

        Assert.Equal(ExitCodeConstants.Success, exitCode);
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

        Assert.Equal(ExitCodeConstants.Success, exitCode);
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

        Assert.Equal(ExitCodeConstants.Success, exitCode);
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

        Assert.Equal(ExitCodeConstants.Success, exitCode);
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

        Assert.Equal(ExitCodeConstants.Success, exitCode);
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

        Assert.Equal(ExitCodeConstants.Success, exitCode);
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

        Assert.Equal(ExitCodeConstants.Success, exitCode);
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

        Assert.Equal(ExitCodeConstants.Success, exitCode);
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

        Assert.Equal(ExitCodeConstants.Success, exitCode);
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

        Assert.Equal(ExitCodeConstants.InvalidCommand, exitCode);
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

        Assert.Equal(ExitCodeConstants.InvalidCommand, exitCode);
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

        Assert.Equal(ExitCodeConstants.Success, exitCode);
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

        Assert.Equal(ExitCodeConstants.InvalidCommand, exitCode);
        Assert.Equal(0, backchannel.ExecuteResourceCommandCallCount);
        var error = Assert.Single(interactionService.DisplayedErrors);
        Assert.Contains("maybe", error);
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

        Assert.Equal(ExitCodeConstants.FailedToExecuteResourceCommand, exitCode);
        var error = Assert.Single(interactionService.DisplayedErrors);
        Assert.Contains("--timeout-seconds: Value must be greater than 0.", error);
        Assert.DoesNotContain("timeoutSeconds:", error);
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

        Assert.Equal(ExitCodeConstants.Success, exitCode);
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

        Assert.Equal(ExitCodeConstants.Success, exitCode);
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

        Assert.Equal(ExitCodeConstants.InvalidCommand, exitCode);
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

        Assert.Equal(ExitCodeConstants.InvalidCommand, exitCode);
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

        Assert.Equal(ExitCodeConstants.InvalidCommand, exitCode);
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

        Assert.Equal(ExitCodeConstants.InvalidCommand, exitCode);
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

        Assert.Equal(ExitCodeConstants.InvalidCommand, exitCode);
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

        Assert.Equal(ExitCodeConstants.InvalidCommand, exitCode);
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

        Assert.Equal(ExitCodeConstants.InvalidCommand, exitCode);
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

        Assert.Equal(ExitCodeConstants.Success, exitCode);
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

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        AssertJsonObject(backchannel.ExecuteResourceCommandArguments, ("message", "from delimiter"), ("timeoutMilliseconds", "10"));
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

        Assert.Equal(ExitCodeConstants.Success, exitCode);
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

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        AssertJsonObject(backchannel.ExecuteResourceCommandArguments, ("noProxy", "localhost"));
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

        Assert.Equal(ExitCodeConstants.InvalidCommand, exitCode);
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

        Assert.Equal(ExitCodeConstants.Success, exitCode);
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

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.Contains("Waits for text in the browser.", helpOutput);
        Assert.Contains("--selector <value>", helpOutput);
        Assert.Contains("Selector to wait for. Required.", helpOutput);
        Assert.Contains("--timeout-milliseconds <value>", helpOutput);
        Assert.Contains("Timeout in milliseconds.", helpOutput);
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

        Assert.Equal(ExitCodeConstants.Success, exitCode);
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

        Assert.Equal(ExitCodeConstants.Success, exitCode);
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

        Assert.Equal(ExitCodeConstants.Success, exitCode);
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

        Assert.Equal(ExitCodeConstants.Success, exitCode);
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

        Assert.Equal(ExitCodeConstants.Success, exitCode);
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

        Assert.Equal(ExitCodeConstants.Success, exitCode);
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
        return new ResourceSnapshot
        {
            Name = name,
            DisplayName = name,
            State = "Running",
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
        return new ResourceSnapshotCommand
        {
            Name = name,
            Description = description,
            State = "Enabled",
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
        bool allowCustomChoice = false)
    {
        return new ResourceSnapshotCommandArgument
        {
            Name = name,
            Description = description,
            InputType = inputType,
            Required = required,
            Value = value,
            Options = options,
            AllowCustomChoice = allowCustomChoice
        };
    }
}
