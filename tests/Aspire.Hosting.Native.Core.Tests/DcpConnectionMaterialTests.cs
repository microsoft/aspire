// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Shared;
using Aspire.Hosting.Native.Core.Tests.TestServices;
using Microsoft.Extensions.Time.Testing;

namespace Aspire.Hosting.Native.Core.Tests;

public class DcpConnectionMaterialTests
{
    [Fact]
    public async Task SuccessfulLegacyReadinessPreservesClientTimeoutBeforeAndAfterRequests()
    {
        using var capture = new DiagnosticCapture(true, DcpConnectionMaterial.ActivitySourceName);
        await using var host = await DcpReadyTestHost.StartAsync(TestContext.Current.CancellationToken);
        var timeout = TimeSpan.FromSeconds(10);
        using var material = await DcpConnectionMaterial.WaitAsync(host.KubeconfigPath, () => false,
            timeout, TimeSpan.FromMilliseconds(10), _ => { }, TestContext.Current.CancellationToken);
        Assert.Equal(timeout, material.Client.Timeout);
        var activity = Assert.Single(capture.Activities);
        Assert.Equal("dcp.connection.ready", activity.OperationName);
        Assert.Equal(ActivityStatusCode.Ok, activity.Status);
        Assert.Collection(activity.TagObjects.OrderBy(tag => tag.Key, StringComparer.Ordinal),
            tag => Assert.Equal(new KeyValuePair<string, object?>("dcp.authentication", "bearer"), tag),
            tag => Assert.Equal(new KeyValuePair<string, object?>("dcp.readiness.attempts", 1), tag));
        using var response = await material.Client.GetAsync("/readyz", TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal(timeout, material.Client.Timeout);
    }

    [Fact]
    public async Task SuccessfulProviderReadinessLeavesHttpTimeoutDisabledForNativeRequestBudgets()
    {
        await using var host = await DcpReadyTestHost.StartAsync(TestContext.Current.CancellationToken);
        using var material = await DcpConnectionMaterial.WaitAsync(host.KubeconfigPath, () => false,
            TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(10), _ => { }, new FakeTimeProvider(),
            TestContext.Current.CancellationToken);
        Assert.Equal(Timeout.InfiniteTimeSpan, material.Client.Timeout);
        using var response = await material.Client.GetAsync("/readyz", TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode);
    }

    [Theory]
    [InlineData("not-base64")]
    [InlineData("YQ==")]
    public async Task NonemptyTruncatedCertificateIsRetriedUntilTheStartupBudgetExpires(string certificate)
    {
        var directory = Directory.CreateTempSubdirectory("dcp-material-test-");
        try
        {
            var path = Path.Combine(directory.FullName, "kubeconfig");
            await File.WriteAllTextAsync(path, $"""
                clusters:
                - cluster:
                    server: https://127.0.0.1:1
                    certificate-authority-data: {certificate}
                users:
                - user:
                    token: token
                """);
            var attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellation = new CancellationTokenSource();
            var wait = DcpConnectionMaterial.WaitAsync(path, () => false, TimeSpan.FromSeconds(1),
                TimeSpan.FromMilliseconds(1), _ => attempted.TrySetResult(), cancellation.Token);
            await attempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(wait.IsCompleted);
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ControllerExitEndsRetriesWithoutAcceptingPartialMaterial()
    {
        using var capture = new DiagnosticCapture(true, DcpConnectionMaterial.ActivitySourceName);
        var directory = Directory.CreateTempSubdirectory("dcp-material-test-");
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => DcpConnectionMaterial.WaitAsync(
                Path.Combine(directory.FullName, "kubeconfig"), () => true, TimeSpan.FromSeconds(1),
                TimeSpan.FromMilliseconds(1), _ => { }, TestContext.Current.CancellationToken));
            var activity = Assert.Single(capture.Activities);
            Assert.Equal(ActivityStatusCode.Error, activity.Status);
            Assert.Collection(activity.TagObjects.OrderBy(tag => tag.Key, StringComparer.Ordinal),
                tag => Assert.Equal(new KeyValuePair<string, object?>("dcp.readiness.attempts", 1), tag),
                tag => Assert.Equal(new KeyValuePair<string, object?>("error.type", typeof(InvalidOperationException).FullName), tag));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
