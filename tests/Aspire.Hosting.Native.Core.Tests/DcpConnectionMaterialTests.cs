// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Shared;

namespace Aspire.Hosting.Native.Core.Tests;

public class DcpConnectionMaterialTests
{
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
        var directory = Directory.CreateTempSubdirectory("dcp-material-test-");
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => DcpConnectionMaterial.WaitAsync(
                Path.Combine(directory.FullName, "kubeconfig"), () => true, TimeSpan.FromSeconds(1),
                TimeSpan.FromMilliseconds(1), _ => { }, TestContext.Current.CancellationToken));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
