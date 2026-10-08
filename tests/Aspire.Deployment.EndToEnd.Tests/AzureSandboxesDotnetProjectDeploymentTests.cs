// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests;

public sealed class AzureSandboxesDotnetProjectDeploymentTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SandboxAppHostUsesScenarioIngressPolicy(bool useDotnetProject)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var projectDir = workspace.CreateDirectory("Sandbox");
        var appHostPath = Path.Combine(projectDir.FullName, "apphost.cs");
        File.WriteAllText(appHostPath, "#:sdk Aspire.AppHost.Sdk\n");

        AzureSandboxesDeploymentTests.WriteDotNetSandboxAppHost(
            workspace, "Sandbox", "Frontend", "Storage", useDotnetProject, "test-marker");

        await Verify(File.ReadAllText(appHostPath), "txt")
            .UseDirectory("Snapshots")
            .UseParameters(useDotnetProject);
    }

    [Fact]
    [ActiveIssue("https://github.com/microsoft/aspire/issues/20777")]
    public async Task DeployDotnetProjectResourcesWithEndpointsAndAzureStorageToAzureSandbox()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(90));
        await new AzureSandboxesDeploymentTests(output).DeployDotNetProjectsWithEndpointsAndAzureStorageToAzureSandboxCore(
            true, nameof(DeployDotnetProjectResourcesWithEndpointsAndAzureStorageToAzureSandbox), cts.Token);
    }
}
