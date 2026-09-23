// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests;

public sealed class AcaDotnetProjectDeploymentTests(ITestOutputHelper output)
{
    [Fact]
    public async Task DeployDotnetProjectsToAzureContainerApps()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(45));
        await new AcaStarterDeploymentTests(output).DeployStarterTemplateToAzureContainerAppsCore(
            true, nameof(DeployDotnetProjectsToAzureContainerApps), cts.Token);
    }
}
