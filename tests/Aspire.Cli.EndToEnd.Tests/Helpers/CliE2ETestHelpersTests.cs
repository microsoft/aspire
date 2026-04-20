// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Aspire.Cli.EndToEnd.Tests.Helpers;

[Collection(CliInstallEnvironmentCollection.Name)]
public sealed class CliE2ETestHelpersTests(ITestOutputHelper output)
{
    [Fact]
    public void ExecuteWithDockerBuildRetry_RetriesTransientMcrFailures()
    {
        var attempts = 0;

        var result = CliE2ETestHelpers.ExecuteWithDockerBuildRetry(
            () =>
            {
                attempts++;

                if (attempts < 3)
                {
                    throw new InvalidOperationException(
                        "docker build failed with exit code 1: failed to resolve source metadata for mcr.microsoft.com/dotnet/sdk:10.0: unexpected status from HEAD request to https://mcr.microsoft.com/v2/dotnet/sdk/manifests/10.0: 403 Forbidden");
                }

                return "success";
            },
            output,
            "create Docker test terminal",
            [TimeSpan.Zero, TimeSpan.Zero]);

        Assert.Equal("success", result);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public void ExecuteWithDockerBuildRetry_DoesNotRetryNonTransientFailures()
    {
        var attempts = 0;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            CliE2ETestHelpers.ExecuteWithDockerBuildRetry(
                () =>
                {
                    attempts++;
                    throw new InvalidOperationException("docker build failed because the Dockerfile is invalid");
                },
                output,
                "create Docker test terminal",
                [TimeSpan.Zero, TimeSpan.Zero]));

        Assert.Equal("docker build failed because the Dockerfile is invalid", exception.Message);
        Assert.Equal(1, attempts);
    }
}
