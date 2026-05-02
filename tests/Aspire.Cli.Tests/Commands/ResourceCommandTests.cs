// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Backchannel;
using Aspire.Cli.Commands;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;

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

        // Missing required argument should fail
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

        // Missing required command argument should fail
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
    public async Task ResourceCommand_ForwardsPositionalArgumentsFromCommandInputs()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var backchannel = CreateBackchannelWithCommandInputs(
            "web-browser-logs",
            "fill-browser",
            new ResourceSnapshotCommandArgument { Name = "selector", InputType = "Text", Required = true },
            new ResourceSnapshotCommandArgument { Name = "value", InputType = "Text", Required = true });
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("hash", "/tmp/test.sock", backchannel);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("""resource web-browser-logs fill-browser "#name" Aspire""");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.NotNull(backchannel.ExecuteResourceCommandArguments);
        Assert.Equal("#name", backchannel.ExecuteResourceCommandArguments.Value.GetProperty("selector").GetString());
        Assert.Equal("Aspire", backchannel.ExecuteResourceCommandArguments.Value.GetProperty("value").GetString());
    }

    [Fact]
    public async Task ResourceCommand_ForwardsNamedArgumentsFromCommandInputs()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var backchannel = CreateBackchannelWithCommandInputs(
            "web-browser-logs",
            "wait-for-browser",
            new ResourceSnapshotCommandArgument { Name = "selector", InputType = "Text" },
            new ResourceSnapshotCommandArgument { Name = "text", InputType = "Text" },
            new ResourceSnapshotCommandArgument { Name = "timeoutMilliseconds", InputType = "Number" });
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("hash", "/tmp/test.sock", backchannel);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("""resource web-browser-logs wait-for-browser "text=Submitted Aspire!" timeoutMilliseconds=500""");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.NotNull(backchannel.ExecuteResourceCommandArguments);
        Assert.Equal("Submitted Aspire!", backchannel.ExecuteResourceCommandArguments.Value.GetProperty("text").GetString());
        Assert.Equal(500d, backchannel.ExecuteResourceCommandArguments.Value.GetProperty("timeoutMilliseconds").GetDouble());
        Assert.False(backchannel.ExecuteResourceCommandArguments.Value.TryGetProperty("selector", out _));
    }

    [Fact]
    public async Task ResourceCommand_ForwardsJsonObjectArgument()
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
        var result = command.Parse(["resource", "web-browser-logs", "click-browser", """{"selector":"#submit"}"""]);

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.NotNull(backchannel.ExecuteResourceCommandArguments);
        Assert.Equal("#submit", backchannel.ExecuteResourceCommandArguments.Value.GetProperty("selector").GetString());
    }

    [Fact]
    public async Task ResourceCommand_ForwardsPositionalArgumentContainingEquals()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var backchannel = CreateBackchannelWithCommandInputs(
            "web-browser-logs",
            "navigate-browser",
            new ResourceSnapshotCommandArgument { Name = "url", InputType = "Text", Required = true });
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("hash", "/tmp/test.sock", backchannel);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("""resource web-browser-logs navigate-browser "https://example.com/?q=aspire" """);

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.NotNull(backchannel.ExecuteResourceCommandArguments);
        Assert.Equal("https://example.com/?q=aspire", backchannel.ExecuteResourceCommandArguments.Value.GetProperty("url").GetString());
    }

    [Fact]
    public async Task ResourceCommand_RejectsInvalidJsonObjectArgument()
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
        var result = command.Parse(["resource", "web-browser-logs", "click-browser", "{not-json}"]);

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.InvalidCommand, exitCode);
        Assert.Null(backchannel.ExecuteResourceCommandArguments);
    }

    [Fact]
    public async Task ResourceCommand_RejectsTooManyPositionalArguments()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var backchannel = CreateBackchannelWithCommandInputs(
            "web-browser-logs",
            "click-browser",
            new ResourceSnapshotCommandArgument { Name = "selector", InputType = "Text", Required = true });
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("hash", "/tmp/test.sock", backchannel);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("""resource web-browser-logs click-browser "#submit" extra""");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.InvalidCommand, exitCode);
        Assert.Null(backchannel.ExecuteResourceCommandArguments);
    }

    [Fact]
    public async Task ResourceCommand_RejectsInvalidNumberArgument()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var backchannel = CreateBackchannelWithCommandInputs(
            "web-browser-logs",
            "wait-for-browser",
            new ResourceSnapshotCommandArgument { Name = "timeoutMilliseconds", InputType = "Number" });
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("hash", "/tmp/test.sock", backchannel);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("""resource web-browser-logs wait-for-browser timeoutMilliseconds=soon""");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.InvalidCommand, exitCode);
        Assert.Null(backchannel.ExecuteResourceCommandArguments);
    }

    private static TestAppHostAuxiliaryBackchannel CreateBackchannelWithCommandInputs(
        string resourceName,
        string commandName,
        params ResourceSnapshotCommandArgument[] argumentInputs)
    {
        return new TestAppHostAuxiliaryBackchannel
        {
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse { Success = true },
            ResourceSnapshots =
            [
                new ResourceSnapshot
                {
                    Name = resourceName,
                    Commands =
                    [
                        new ResourceSnapshotCommand
                        {
                            Name = commandName,
                            State = "Enabled",
                            ArgumentInputs = argumentInputs
                        }
                    ]
                }
            ]
        };
    }
}
