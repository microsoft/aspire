// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Backchannel;
using Aspire.Cli.Commands;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Cli.Tests.Commands;

public class WaitCommandTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task WaitCommand_Help_Works()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("wait --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public async Task WaitCommand_RequiresResourceArgument()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("wait");

        // Missing required argument should fail
        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.NotEqual(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public async Task WaitCommand_AcceptsResourceArgument()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("wait myresource --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public async Task WaitCommand_AcceptsProjectOption()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("wait myresource --apphost /path/to/project.csproj --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public async Task WaitCommand_AcceptsStatusOption()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("wait myresource --status up --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public async Task WaitCommand_AcceptsTimeoutOption()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("wait myresource --timeout 60 --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Theory]
    [InlineData("healthy")]
    [InlineData("up")]
    [InlineData("down")]
    public async Task WaitCommand_AcceptsAllStatusValues(string status)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"wait myresource --status {status} --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Theory]
    [InlineData("Healthy")]
    [InlineData("UP")]
    [InlineData("Down")]
    public async Task WaitCommand_StatusIsCaseInsensitive(string status)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"wait myresource --status {status} --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public async Task WaitCommand_ResourceNotFound_ReturnsFailure()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            WaitForResourceResult = new WaitForResourceResponse
            {
                Success = false,
                ResourceNotFound = true,
                ErrorMessage = "Resource 'nonexistent' was not found."
            }
        };
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("hash", "/tmp/test.sock", backchannel);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
        });
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("wait nonexistent --timeout 5");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.WaitResourceFailed, exitCode);
    }

    [Fact]
    public async Task WaitCommand_ResourceRunning_WaitForUp_ReturnsSuccess()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ResourceSnapshots = [CreateSnapshot("myapp")],
            WaitForResourceResult = new WaitForResourceResponse { Success = true, State = "Running" }
        };
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("hash", "/tmp/test.sock", backchannel);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
        });
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("wait myapp --status up --timeout 5");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public async Task WaitCommand_ResourceHealthy_WaitForHealthy_ReturnsSuccess()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ResourceSnapshots = [CreateSnapshot("mydb")],
            WaitForResourceResult = new WaitForResourceResponse { Success = true, State = "Running", HealthStatus = "Healthy" }
        };
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("hash", "/tmp/test.sock", backchannel);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
        });
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("wait mydb --status healthy --timeout 5");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public async Task WaitCommand_DisplayName_ResolvesToCanonicalResourceName()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ResourceSnapshots =
            [
                new ResourceSnapshot { Name = "apiservice-tzykkput", DisplayName = "apiservice", ResourceType = "Project", State = "Running" }
            ],
            WaitForResourceResult = new WaitForResourceResponse { Success = true, State = "Running", HealthStatus = "Healthy" }
        };
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("hash", "/tmp/test.sock", backchannel);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
        });
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("wait apiservice --status healthy --timeout 5");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.Equal("apiservice-tzykkput", backchannel.LastWaitResourceName);
        Assert.Equal("healthy", backchannel.LastWaitStatus);
        Assert.Equal(5, backchannel.LastWaitTimeoutSeconds);
    }

    [Fact]
    public async Task WaitCommand_CanonicalResourceName_PassesThroughUnchanged()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ResourceSnapshots =
            [
                new ResourceSnapshot { Name = "apiservice-tzykkput", DisplayName = "apiservice", ResourceType = "Project", State = "Running" }
            ],
            WaitForResourceResult = new WaitForResourceResponse { Success = true, State = "Running", HealthStatus = "Healthy" }
        };
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("hash", "/tmp/test.sock", backchannel);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
        });
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("wait apiservice-tzykkput --status healthy --timeout 5");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.Equal("apiservice-tzykkput", backchannel.LastWaitResourceName);
    }

    [Fact]
    public async Task WaitCommand_CanonicalResourceName_WithoutSnapshots_PassesThroughUnchanged()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            WaitForResourceResult = new WaitForResourceResponse { Success = true, State = "Running", HealthStatus = "Healthy" }
        };
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("hash", "/tmp/test.sock", backchannel);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
        });
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("wait apiservice-tzykkput --status healthy --timeout 5");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.Equal("apiservice-tzykkput", backchannel.LastWaitResourceName);
    }

    [Fact]
    public async Task WaitCommand_AmbiguousDisplayName_ReturnsFailureWithoutWaiting()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ResourceSnapshots =
            [
                new ResourceSnapshot { Name = "cache-abc12345", DisplayName = "cache", ResourceType = "Container", State = "Running" },
                new ResourceSnapshot { Name = "cache-def67890", DisplayName = "cache", ResourceType = "Container", State = "Running" }
            ]
        };
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("hash", "/tmp/test.sock", backchannel);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
        });
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("wait cache --status healthy --timeout 5");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.WaitResourceFailed, exitCode);
        Assert.Null(backchannel.LastWaitResourceName);
    }

    [Fact]
    public async Task WaitCommand_DisplayName_ResolvedResourceDisappearsBeforeWait_ReturnsFailure()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ResourceSnapshots =
            [
                new ResourceSnapshot { Name = "apiservice-tzykkput", DisplayName = "apiservice", ResourceType = "Project", State = "Running" }
            ],
            WaitForResourceResult = new WaitForResourceResponse { Success = false, ResourceNotFound = true, ErrorMessage = "Resource 'apiservice-tzykkput' was not found." }
        };
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("hash", "/tmp/test.sock", backchannel);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
        });
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("wait apiservice --status healthy --timeout 5");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.WaitResourceFailed, exitCode);
        Assert.Equal("apiservice-tzykkput", backchannel.LastWaitResourceName);
        Assert.Equal("healthy", backchannel.LastWaitStatus);
        Assert.Equal(5, backchannel.LastWaitTimeoutSeconds);
    }

    [Fact]
    public async Task WaitCommand_Timeout_ReturnsTimeoutExitCode()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ResourceSnapshots = [CreateSnapshot("mydb")],
            WaitForResourceResult = new WaitForResourceResponse
            {
                Success = false,
                TimedOut = true,
                ErrorMessage = "Timed out waiting for resource 'mydb'."
            }
        };
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("hash", "/tmp/test.sock", backchannel);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
        });
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("wait mydb --status healthy --timeout 2");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.WaitTimeout, exitCode);
    }

    [Fact]
    public async Task WaitCommand_ResourceExited_WaitForDown_ReturnsSuccess()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ResourceSnapshots = [CreateSnapshot("worker")],
            WaitForResourceResult = new WaitForResourceResponse { Success = true, State = "Exited" }
        };
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("hash", "/tmp/test.sock", backchannel);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
        });
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("wait worker --status down --timeout 5");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public async Task WaitCommand_ResourceFailedToStart_WaitForUp_ReturnsFailure()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ResourceSnapshots = [CreateSnapshot("myapp")],
            WaitForResourceResult = new WaitForResourceResponse
            {
                Success = false,
                State = "FailedToStart",
                ErrorMessage = "Resource 'myapp' failed to start."
            }
        };
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("hash", "/tmp/test.sock", backchannel);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
        });
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("wait myapp --status up --timeout 5");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.WaitResourceFailed, exitCode);
    }

    private static ResourceSnapshot CreateSnapshot(string name)
    {
        return new ResourceSnapshot
        {
            Name = name,
            DisplayName = name,
            ResourceType = "Project",
            State = "Running"
        };
    }
}
