// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests;

public sealed class AzureSandboxesDotnetProjectDeploymentTests(ITestOutputHelper output)
{
    [Fact]
    public async Task DeployDotnetProjectResourcesWithEndpointsAndAzureStorageToAzureSandbox()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(90));
        await new AzureSandboxesDeploymentTests(output).DeployDotNetProjectsWithEndpointsAndAzureStorageToAzureSandboxCore(
            true, nameof(DeployDotnetProjectResourcesWithEndpointsAndAzureStorageToAzureSandbox), cts.Token);
    }
}
