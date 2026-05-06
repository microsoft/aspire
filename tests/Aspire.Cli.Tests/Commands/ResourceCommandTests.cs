// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
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
    public async Task ResourceCommand_ForwardsOrderedArguments()
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
        var result = command.Parse("""resource web-browser-automation fill-browser "#name" Aspire""");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        AssertJsonStringArray(backchannel.ExecuteResourceCommandArguments, "#name", "Aspire");
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
    }

    [Fact]
    public async Task ResourceCommand_ForwardsEqualsArgumentsByOrder()
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
        var result = command.Parse("""resource web-browser-automation wait-for-browser "text=Submitted Aspire!" timeoutMilliseconds=500""");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        AssertJsonStringArray(backchannel.ExecuteResourceCommandArguments, "text=Submitted Aspire!", "timeoutMilliseconds=500");
    }

    [Fact]
    public async Task ResourceCommand_ForwardsJsonLookingArgumentByOrder()
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
        var result = command.Parse(["resource", "web-browser-automation", "click-browser", """{"selector":"#submit"}"""]);

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        AssertJsonStringArray(backchannel.ExecuteResourceCommandArguments, "{\"selector\":\"#submit\"}");
    }

    [Fact]
    public async Task ResourceCommand_ForwardsPositionalArgumentContainingEquals()
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
        AssertJsonStringArray(backchannel.ExecuteResourceCommandArguments, "https://example.com/?q=aspire");
    }

    [Fact]
    public async Task ResourceCommand_ForwardsExtraArguments()
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
        AssertJsonStringArray(backchannel.ExecuteResourceCommandArguments, "#submit", "extra");
    }

    [Fact]
    public async Task ResourceCommand_ForwardsOptionLookingArgumentsByOrder()
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
        AssertJsonStringArray(backchannel.ExecuteResourceCommandArguments, "--selector", "#submit", "--count", "2");
    }

    private static void AssertJsonStringArray(JsonElement? actual, params string[] expected)
    {
        Assert.NotNull(actual);
        Assert.Equal(JsonValueKind.Array, actual.Value.ValueKind);
        var actualElements = actual.Value.EnumerateArray().ToArray();
        Assert.Equal(expected.Length, actualElements.Length);

        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(JsonValueKind.String, actualElements[i].ValueKind);
            Assert.Equal(expected[i], actualElements[i].GetString());
        }
    }
}
