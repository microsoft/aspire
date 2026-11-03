// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.DotNet;
using Aspire.Cli.Tests.TestServices;
using Aspire.Shared;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.DotNet;

public class ProcessScopeTests
{
    [Fact]
    public async Task DisposeAsync_PreservesOperationAndCleanupFailures()
    {
        var operationFailure = new InvalidOperationException("Original installation failure.");
        var cleanupFailure = new InvalidOperationException("Could not observe guardian exit.");
        var execution = new TestProcessExecution(
            "guardian", [], null, new ProcessInvocationOptions(),
            (_, _, _) => Task.FromResult((0, (string?)null)), () => 1)
        {
            ProcessId = int.MaxValue,
            DisposeCallback = () => throw cleanupFailure
        };
        await execution.StartAsync(TestContext.Current.CancellationToken);
        var scope = new ProcessScope(execution, NullLogger.Instance, "test installation");

        var exception = await Assert.ThrowsAsync<AggregateException>(() => scope.DisposeAsync(operationFailure).AsTask());

        Assert.Collection(exception.InnerExceptions,
            failure => Assert.Same(operationFailure, failure),
            failure => Assert.Same(cleanupFailure, failure));
        Assert.Equal(1, execution.DisposeCount);
        Assert.Equal(0, execution.KillCount);
    }

    [Fact]
    public async Task WaitForExitAsync_ForwardsExecutionCancellationAndTerminationFailures()
    {
        var terminationFailure = new InvalidOperationException("Could not terminate guardian.");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancellationFailure = new OperationCanceledException(cancellation.Token);
        var executionFailure = new AggregateException(cancellationFailure, terminationFailure);
        var execution = new TestProcessExecution(
            "guardian", [], null, new ProcessInvocationOptions(),
            (_, _, _) => Task.FromResult((0, (string?)null)), () => 1)
        {
            ProcessId = int.MaxValue,
            WaitForExitAsyncCallback = (_, token) => token.IsCancellationRequested
                ? Task.FromException<int>(executionFailure)
                : Task.FromResult(0)
        };
        await execution.StartAsync(TestContext.Current.CancellationToken);
        var scope = new ProcessScope(execution, NullLogger.Instance, "cancelled installation");
        try
        {
            var exception = await Assert.ThrowsAsync<AggregateException>(() => scope.WaitForExitAsync(cancellation.Token));

            Assert.Same(executionFailure, exception);
            Assert.Collection(exception.InnerExceptions,
                failure => Assert.Same(cancellationFailure, failure),
                failure => Assert.Same(terminationFailure, failure));
            Assert.Equal(0, execution.KillCount);
        }
        finally
        {
            await scope.DisposeAsync();
        }
    }

    [Fact]
    public async Task Constructor_RejectsOwnershipOfTheCaller()
    {
        var execution = new TestProcessExecution(
            "guardian", [], null, new ProcessInvocationOptions(),
            (_, _, _) => Task.FromResult((0, (string?)null)), () => 1);
        await using var lifetime = execution;
        await execution.StartAsync(TestContext.Current.CancellationToken);

        var exception = Assert.Throws<InvalidOperationException>(() => new ProcessScope(execution, NullLogger.Instance, "invalid scope"));

        Assert.Equal("A supervised child process scope cannot own its caller.", exception.Message);
    }
}
