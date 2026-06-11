// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Xunit;

namespace Aspire.Hosting.Testing.Tests;

// Tests that DistributedApplicationTestingBuilder throws exceptions at the right times when the app crashes.
public class TestingFactoryCrashTests
{
    [Theory]
    [RequiresFeature(TestFeature.Docker)]
    [InlineData("before-build")]
    [InlineData("after-build")]
    [InlineData("after-start")]
    [InlineData("after-shutdown")]
    public async Task CrashTests(string crashArg)
    {
        var timeout = TimeSpan.FromMinutes(5);
        using var cts = new CancellationTokenSource(timeout);

        var factory = new DistributedApplicationFactory(typeof(Projects.TestingAppHost1_AppHost), [$"--crash-{crashArg}"]);

        if (crashArg is "before-build" or "after-build")
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => factory.StartAsync().WaitAsync(cts.Token));
            Assert.Contains(crashArg, exception.Message);
        }
        else
        {
            await factory.StartAsync().WaitAsync(cts.Token);
        }

        await factory.DisposeAsync().AsTask().WaitAsync(cts.Token);
    }
}
