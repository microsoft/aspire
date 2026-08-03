// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Acquisition;
using Aspire.Cli.Commands;
using Aspire.Cli.Tests.TestServices;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Commands;

public class InstallationInfoOutputTests
{
    [Fact]
    public async Task DiscoverAllSafelyAsync_TimesOutWhenDiscoveryDoesNotObserveCancellation()
    {
        using var releaseDiscovery = new ManualResetEventSlim();
        var discoveryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var discoveryExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var self = new InstallationInfo
        {
            Path = "/test/aspire",
            Status = InstallationInfoStatus.Ok,
        };
        var discovery = new FakeInstallationDiscovery(self)
        {
            DiscoverAllAsyncCallback = _ =>
            {
                discoveryStarted.SetResult();
                releaseDiscovery.Wait(CancellationToken.None);
                discoveryExited.SetResult();
                return Task.FromResult<IReadOnlyList<InstallationInfo>>([self]);
            },
        };
        var wingetProbe = new WingetFirstRunProbe(
            new TestWindowsRegistryReader(),
            NullLogger<WingetFirstRunProbe>.Instance);

        var discoveryTask = InstallationInfoOutput.DiscoverAllSafelyAsync(
            discovery,
            wingetProbe,
            NullLogger.Instance,
            TimeSpan.FromMilliseconds(100),
            TestContext.Current.CancellationToken);
        IReadOnlyList<InstallationInfo> results;
        try
        {
            await discoveryStarted.Task.DefaultTimeout();
            results = await discoveryTask.DefaultTimeout();
        }
        finally
        {
            releaseDiscovery.Set();
        }

        await discoveryExited.Task.DefaultTimeout();

        var result = Assert.Single(results);
        Assert.Equal(InstallationInfoStatus.Failed, result.Status);
        Assert.Contains("timed out after", result.StatusReason, StringComparison.OrdinalIgnoreCase);
    }
}