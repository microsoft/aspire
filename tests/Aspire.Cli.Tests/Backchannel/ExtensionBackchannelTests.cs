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

        await Assert.ThrowsAsync<InvalidOperationException>(() => backchannel.ConnectAsync(CancellationToken.None)).DefaultTimeout();
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
