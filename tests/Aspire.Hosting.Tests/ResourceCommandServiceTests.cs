// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "2")]
public class ResourceCommandServiceTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task ExecuteCommandAsync_NoMatchingResource_Failure()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var custom = builder.AddResource(new CustomResource("myResource"));

        var app = builder.Build();
        await app.StartAsync();

        // Act
        var result = await app.ResourceCommands.ExecuteCommandAsync("NotFoundResourceId", "NotFound");

        // Assert
        Assert.False(result.Success);
        Assert.Equal("Resource 'NotFoundResourceId' not found.", result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteCommandAsync_ResourceNameMultipleMatches_Failure()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var custom = builder.AddResource(new CustomResource("myResource"));
        custom.WithAnnotation(new DcpInstancesAnnotation([
            new DcpInstance("myResource-abcdwxyz", "abcdwxyz", 0),
            new DcpInstance("myResource-efghwxyz", "efghwxyz", 1)
            ]));

        var app = builder.Build();
        await app.StartAsync();

        // Act
        var result = await app.ResourceCommands.ExecuteCommandAsync("myResource", "NotFound");

        // Assert
        Assert.False(result.Success);
        Assert.Equal("Resource 'myResource' not found.", result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteCommandAsync_NoMatchingCommand_Failure()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var custom = builder.AddResource(new CustomResource("myResource"));

        var app = builder.Build();
        await app.StartAsync();

        // Act
        var result = await app.ResourceCommands.ExecuteCommandAsync(custom.Resource, "NotFound");

        // Assert
        Assert.False(result.Success);
        Assert.Equal("Command 'NotFound' not available for resource 'myResource'.", result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteCommandAsync_ResourceNameMultipleMatches_Success()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var commandResourcesChannel = Channel.CreateUnbounded<string>();

        var custom = builder.AddResource(new CustomResource("myResource"));
        custom.WithAnnotation(new DcpInstancesAnnotation([
            new DcpInstance("myResource-abcdwxyz", "abcdwxyz", 0)
            ]));
        custom.WithCommand(name: "mycommand",
                displayName: "My command",
                executeCommand: async e =>
                {
                    await commandResourcesChannel.Writer.WriteAsync(e.ResourceName);
                    return new ExecuteCommandResult { Success = true };
                });

        var app = builder.Build();
        await app.StartAsync();

        // Act
        var result = await app.ResourceCommands.ExecuteCommandAsync("myResource", "mycommand");
        commandResourcesChannel.Writer.Complete();

        // Assert
        Assert.True(result.Success);

        var resolvedResourceNames = custom.Resource.GetResolvedResourceNames().ToList();
        await foreach (var resourceName in commandResourcesChannel.Reader.ReadAllAsync().DefaultTimeout())
        {
            Assert.True(resolvedResourceNames.Remove(resourceName));
        }
    }

    [Fact]
    public async Task ExecuteCommandAsync_HasReplicas_Success_CalledPerReplica()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var commandResourcesChannel = Channel.CreateUnbounded<string>();

        var custom = builder.AddResource(new CustomResource("myResource"));
        custom.WithAnnotation(new DcpInstancesAnnotation([
            new DcpInstance("myResource-abcdwxyz", "abcdwxyz", 0),
            new DcpInstance("myResource-efghwxyz", "efghwxyz", 1)
            ]));
        custom.WithCommand(name: "mycommand",
                displayName: "My command",
                executeCommand: async e =>
                {
                    await commandResourcesChannel.Writer.WriteAsync(e.ResourceName);
                    return new ExecuteCommandResult { Success = true };
                });

        // Act
        var app = builder.Build();
        await app.StartAsync();

        var result = await app.ResourceCommands.ExecuteCommandAsync(custom.Resource, "mycommand");
        commandResourcesChannel.Writer.Complete();

        // Assert
        Assert.True(result.Success);

        var resolvedResourceNames = custom.Resource.GetResolvedResourceNames().ToList();
        Assert.Equal(2, resolvedResourceNames.Count);
        Assert.Contains("myResource-abcdwxyz", resolvedResourceNames);
        Assert.Contains("myResource-efghwxyz", resolvedResourceNames);

        await foreach (var resourceName in commandResourcesChannel.Reader.ReadAllAsync().DefaultTimeout())
        {
            Assert.True(resolvedResourceNames.Remove(resourceName));
        }
    }

    [Fact]
    public async Task ExecuteCommandAsync_HasReplicas_Failure_CalledPerReplica()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var custom = builder.AddResource(new CustomResource("myResource"));
        custom.WithAnnotation(new DcpInstancesAnnotation([
            new DcpInstance("myResource-abcdwxyz", "abcdwxyz", 0),
            new DcpInstance("myResource-efghwxyz", "efghwxyz", 1)
            ]));
        custom.WithCommand(name: "mycommand",
                displayName: "My command",
                executeCommand: e =>
                {
                    return Task.FromResult(new ExecuteCommandResult { Success = false, ErrorMessage = "Failure!" });
                });

        // Act
        var app = builder.Build();
        await app.StartAsync();

        var result = await app.ResourceCommands.ExecuteCommandAsync(custom.Resource, "mycommand");

        // Assert
        Assert.False(result.Success);

        var resourceNames = custom.Resource.GetResolvedResourceNames();
        Assert.Equal(2, resourceNames.Length);
        Assert.Equal("myResource-abcdwxyz", resourceNames[0]);
        Assert.Equal("myResource-efghwxyz", resourceNames[1]);

        Assert.Equal($"""
            2 command executions failed.
            Resource '{resourceNames[0]}' failed with error message: Failure!
            Resource '{resourceNames[1]}' failed with error message: Failure!
            """, result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteCommandAsync_Canceled_Success()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var custom = builder.AddResource(new CustomResource("myResource"));
        custom.WithCommand(name: "mycommand",
                displayName: "My command",
                executeCommand: e =>
                {
                    return Task.FromResult(CommandResults.Canceled());
                });

        var app = builder.Build();
        await app.StartAsync();

        // Act
        var result = await app.ResourceCommands.ExecuteCommandAsync(custom.Resource, "mycommand");

        // Assert
        Assert.False(result.Success);
        Assert.True(result.Canceled);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteCommandAsync_HasReplicas_Canceled_CalledPerReplica()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var custom = builder.AddResource(new CustomResource("myResource"));
        custom.WithAnnotation(new DcpInstancesAnnotation([
            new DcpInstance("myResource-abcdwxyz", "abcdwxyz", 0),
            new DcpInstance("myResource-efghwxyz", "efghwxyz", 1)
            ]));
        custom.WithCommand(name: "mycommand",
                displayName: "My command",
                executeCommand: e =>
                {
                    return Task.FromResult(CommandResults.Canceled());
                });

        // Act
        var app = builder.Build();
        await app.StartAsync();

        var result = await app.ResourceCommands.ExecuteCommandAsync(custom.Resource, "mycommand");

        // Assert
        Assert.False(result.Success);
        Assert.True(result.Canceled);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteCommandAsync_HasReplicas_MixedFailureAndCanceled_OnlyFailuresInErrorMessage()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var callCount = 0;
        var custom = builder.AddResource(new CustomResource("myResource"));
        custom.WithAnnotation(new DcpInstancesAnnotation([
            new DcpInstance("myResource-abcdwxyz", "abcdwxyz", 0),
            new DcpInstance("myResource-efghwxyz", "efghwxyz", 1),
            new DcpInstance("myResource-ijklwxyz", "ijklwxyz", 2)
            ]));
        custom.WithCommand(name: "mycommand",
                displayName: "My command",
                executeCommand: e =>
                {
                    var count = Interlocked.Increment(ref callCount);
                    return Task.FromResult(count switch
                    {
                        1 => CommandResults.Failure("Failure!"),
                        2 => CommandResults.Canceled(),
                        _ => CommandResults.Success()
                    });
                });

        // Act
        var app = builder.Build();
        await app.StartAsync();

        var result = await app.ResourceCommands.ExecuteCommandAsync(custom.Resource, "mycommand");

        // Assert
        Assert.False(result.Success);
        Assert.False(result.Canceled); // Should not be canceled since there was at least one failure

        var resourceNames = custom.Resource.GetResolvedResourceNames();
        Assert.Equal($"""
            1 command executions failed.
            Resource '{resourceNames[0]}' failed with error message: Failure!
            """, result.ErrorMessage);
    }

    [Fact] 
    public void CommandResults_Canceled_ProducesCorrectResult()
    {
        // Act
        var result = CommandResults.Canceled();

        // Assert
        Assert.False(result.Success);
        Assert.True(result.Canceled);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteCommandAsync_OperationCanceledException_Canceled()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var custom = builder.AddResource(new CustomResource("myResource"));
        custom.WithCommand(name: "mycommand",
                displayName: "My command",
                executeCommand: e =>
                {
                    throw new OperationCanceledException("Command was canceled");
                });

        var app = builder.Build();
        await app.StartAsync();

        // Act
        var result = await app.ResourceCommands.ExecuteCommandAsync(custom.Resource, "mycommand");

        // Assert
        Assert.False(result.Success);
        Assert.True(result.Canceled);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteCommandAsync_LegacyCommandName_FallsBackToCurrentName()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var custom = builder.AddResource(new CustomResource("myResource"));
        custom.WithCommand(name: KnownResourceCommands.StartCommand,
                displayName: "Start",
                executeCommand: _ => Task.FromResult(new ExecuteCommandResult { Success = true }));

        var app = builder.Build();
        await app.StartAsync();

        // Act - use the legacy "resource-start" name
        var result = await app.ResourceCommands.ExecuteCommandAsync(custom.Resource, "resource-start");

        // Assert - should succeed via fallback
        Assert.True(result.Success);
    }

    [Fact]
    public async Task ExecuteCommandAsync_LegacyCommandName_ById_FallsBackToCurrentName()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var custom = builder.AddResource(new CustomResource("myResource"));
        custom.WithCommand(name: KnownResourceCommands.StopCommand,
                displayName: "Stop",
                executeCommand: _ => Task.FromResult(new ExecuteCommandResult { Success = true }));

        var app = builder.Build();
        await app.StartAsync();

        // Act - use the legacy "resource-stop" name via resource ID
        var result = await app.ResourceCommands.ExecuteCommandAsync("myResource", "resource-stop");

        // Assert - should succeed via fallback
        Assert.True(result.Success);
    }

    private sealed class CustomResource(string name) : Resource(name), IResourceWithEndpoints, IResourceWithWaitSupport
    {

    }
}
