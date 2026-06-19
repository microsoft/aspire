// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Backchannel;
using Aspire.Cli.Tests.Utils;
using Aspire.Hosting;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Backchannel;

public class ExtensionBackchannelTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task ConnectAsync_WhenConnectionSetupFails_PropagatesFailureAndAllowsRetry()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var backchannel = CreateBackchannel("not-a-valid-endpoint", workspace.CreateExecutionContext());

        await Assert.ThrowsAsync<ArgumentException>(() => backchannel.ConnectAsync(CancellationToken.None)).DefaultTimeout();
        await Assert.ThrowsAsync<ArgumentException>(() => backchannel.ConnectAsync(CancellationToken.None)).DefaultTimeout();
    }

    [Fact]
    public async Task ConnectAsync_WhenConnectionSetupFails_PropagatesFailureToConcurrentWaitersAndAllowsRetry()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var setupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSetup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var setupException = new InvalidOperationException("Simulated setup failure.");
        var backchannel = CreateBackchannel(
            "127.0.0.1:1",
            workspace.CreateExecutionContext(),
            async _ =>
            {
                setupEntered.TrySetResult();
                await releaseSetup.Task;
                throw setupException;
            });

        var firstConnectTask = backchannel.ConnectAsync(CancellationToken.None);
        await setupEntered.Task.DefaultTimeout();

        var waiterTasks = Enumerable.Range(0, 4)
            .Select(_ => backchannel.ConnectAsync(CancellationToken.None))
            .ToArray();
        await Task.Delay(100).DefaultTimeout();

        releaseSetup.SetResult();

        var exceptions = await Task.WhenAll(
            waiterTasks.Prepend(firstConnectTask).Select(async task => await Record.ExceptionAsync(() => task)))
            .DefaultTimeout();
        Assert.All(exceptions, exception => Assert.Same(setupException, exception));

        var retryException = await Assert.ThrowsAsync<InvalidOperationException>(() => backchannel.ConnectAsync(CancellationToken.None)).DefaultTimeout();
        Assert.Same(setupException, retryException);
    }

    [Fact]
    public async Task ConnectAsync_WhenExtensionIsIncompatible_PropagatesFailureToConcurrentWaitersWithoutRetrying()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var setupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSetup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var setupException = new ExtensionIncompatibleException("Simulated incompatible extension.", "test-capability");
        var connectAttempts = 0;
        var backchannel = CreateBackchannel(
            "127.0.0.1:1",
            workspace.CreateExecutionContext(),
            async _ =>
            {
                Interlocked.Increment(ref connectAttempts);
                setupEntered.TrySetResult();
                await releaseSetup.Task;
                throw setupException;
            });

        var firstConnectTask = backchannel.ConnectAsync(CancellationToken.None);
        await setupEntered.Task.DefaultTimeout();

        var waiterTasks = Enumerable.Range(0, 4)
            .Select(_ => backchannel.ConnectAsync(CancellationToken.None))
            .ToArray();
        await Task.Delay(100).DefaultTimeout();

        releaseSetup.SetResult();

        var exceptions = await Task.WhenAll(
            waiterTasks.Prepend(firstConnectTask).Select(async task => await Record.ExceptionAsync(() => task)))
            .DefaultTimeout();
        Assert.All(exceptions, exception => Assert.Same(setupException, exception));

        var retryException = await Assert.ThrowsAsync<ExtensionIncompatibleException>(() => backchannel.ConnectAsync(CancellationToken.None)).DefaultTimeout();
        Assert.Same(setupException, retryException);
        Assert.Equal(1, connectAttempts);
    }

    [Fact]
    public async Task ConnectAsync_WhenConnectorIsCanceled_ConcurrentWaiterTakesOverSetup()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var firstConnectorCts = new CancellationTokenSource();
        var firstSetupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var takeoverSetupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTakeoverSetup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var setupException = new ExtensionIncompatibleException("Simulated takeover setup failure.", "test-capability");
        var connectAttempts = 0;
        var backchannel = CreateBackchannel(
            "127.0.0.1:1",
            workspace.CreateExecutionContext(),
            async cancellationToken =>
            {
                var attempt = Interlocked.Increment(ref connectAttempts);
                if (attempt == 1)
                {
                    firstSetupEntered.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return;
                }

                takeoverSetupEntered.TrySetResult();
                await releaseTakeoverSetup.Task;
                throw setupException;
            });

        var firstConnectTask = backchannel.ConnectAsync(firstConnectorCts.Token);
        await firstSetupEntered.Task.DefaultTimeout();

        var waiterTasks = Enumerable.Range(0, 4)
            .Select(_ => backchannel.ConnectAsync(CancellationToken.None))
            .ToArray();
        await Task.Delay(100).DefaultTimeout();

        await firstConnectorCts.CancelAsync();
        await Assert.ThrowsAsync<TaskCanceledException>(() => firstConnectTask).DefaultTimeout();
        await takeoverSetupEntered.Task.DefaultTimeout();

        releaseTakeoverSetup.SetResult();

        var waiterExceptions = await Task.WhenAll(
            waiterTasks.Select(async task => await Record.ExceptionAsync(() => task)))
            .DefaultTimeout();
        Assert.All(waiterExceptions, exception => Assert.Same(setupException, exception));
        Assert.Equal(2, connectAttempts);
    }

    private static ExtensionBackchannel CreateBackchannel(
        string endpoint,
        CliExecutionContext executionContext,
        Func<CancellationToken, Task>? connectCoreAsyncOverride = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [KnownConfigNames.ExtensionEndpoint] = endpoint,
                [KnownConfigNames.ExtensionToken] = "test-token"
            })
            .Build();

        return new ExtensionBackchannel(NullLogger<ExtensionBackchannel>.Instance, new ExtensionRpcTarget(configuration, executionContext), configuration, connectCoreAsyncOverride);
    }

}
