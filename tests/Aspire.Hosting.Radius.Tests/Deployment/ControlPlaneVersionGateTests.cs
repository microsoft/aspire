// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001 // Experimental: the pipeline step graph is under test.

using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Radius.Publishing;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Radius.Tests.Deployment;

/// <summary>
/// The generated Bicep targets the Radius v0.60 schemas, and an older control plane drops the
/// fields it does not recognize instead of rejecting them — so a v0.59 cluster reports a successful
/// deploy and produces an application whose backing resources have no recipe. The deploy step turns
/// that into a loud failure, which only works if the control plane version is read correctly.
/// </summary>
public class ControlPlaneVersionGateTests
{
    /// <summary>
    /// The shape `rad version -o json` emits, from the CLI's <c>CombinedVersionInfo</c>:
    /// https://github.com/radius-project/radius/blob/main/pkg/cli/cmd/version/version.go.
    /// </summary>
    [Theory]
    [InlineData("""{"cli":{"release":"0.60.0","version":"v0.60.0","bicep":"0.35.1","commit":"abc"},"controlPlane":{"version":"0.60.0","status":"Installed"}}""", "0.60.0")]
    [InlineData("""{"controlPlane":{"version":"0.59.0","status":"Installed"}}""", "0.59.0")]
    [InlineData("""{"controlPlane":{"version":"v0.61.2","status":"Installed"}}""", "0.61.2")]
    public void ControlPlaneVersion_IsReadFromRadVersionJson(string json, string expected)
    {
        Assert.True(RadiusDeploymentPipelineStep.TryParseControlPlaneVersion(json, out var version));
        Assert.Equal(Version.Parse(expected), version);
    }

    /// <summary>
    /// Every payload the CLI emits when it cannot report a version has to read as *unknown*, never
    /// as an old version: the gate exists to convert one silent failure into a loud one, and must
    /// not become a new way for an otherwise valid deploy to fail.
    /// </summary>
    [Theory]
    // Cluster unreachable, or Radius not installed on it — the CLI still exits 0.
    [InlineData("""{"controlPlane":{"version":"Not installed","status":"Not connected"}}""")]
    // An edge/dev build of the control plane.
    [InlineData("""{"controlPlane":{"version":"edge","status":"Installed"}}""")]
    // `rad version --cli`, or a CLI predating the combined payload.
    [InlineData("""{"cli":{"release":"0.60.0","version":"v0.60.0"}}""")]
    [InlineData("not json at all")]
    [InlineData("")]
    // `version` present but not a string — must read as unknown rather than throwing.
    [InlineData("""{"controlPlane":{"version":59,"status":"Installed"}}""")]
    [InlineData("""{"controlPlane":{"version":{"major":0},"status":"Installed"}}""")]
    [InlineData("""{"controlPlane":"Installed"}""")]
    public void UnreadableControlPlaneVersion_IsTreatedAsUnknown(string json)
    {
        Assert.False(RadiusDeploymentPipelineStep.TryParseControlPlaneVersion(json, out var version));
        Assert.Null(version);
    }

    [Fact]
    public void UnsupportedControlPlaneException_NamesTheVersionsAndTheRemediation()
    {
        var ex = RadiusDeploymentPipelineStep.CreateUnsupportedControlPlaneException(new Version(0, 59));

        Assert.Contains("0.59", ex.Message, StringComparison.Ordinal);
        Assert.Contains(RadiusDeploymentPipelineStep.MinimumControlPlaneVersion.ToString(), ex.Message, StringComparison.Ordinal);
        Assert.Contains("rad upgrade kubernetes", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ASPIRERADIUS091", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The gate is a separate step precisely so it runs before anything mutates the cluster or the
    /// machine: registering cloud credentials rewrites installation-global <c>rad</c> state and
    /// applying sealed secrets writes to the cluster. Folding it back into the deploy step, or
    /// dropping one of these edges, would silently restore the ordering bug — which no
    /// parsing-level test can observe.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ControlPlaneGate_RunsBeforeEveryStepThatMutatesTheClusterOrTheMachine(bool withCloudProvider)
    {
        var steps = await CreateEnvironmentStepsAsync("myenv", withCloudProvider);

        var gate = Assert.Single(steps, step => step.Name == "verify-radius-control-plane-myenv");

        Assert.Contains("deploy-radius-myenv", gate.RequiredBySteps);
        Assert.Contains("apply-sealed-secrets-myenv", gate.RequiredBySteps);
        Assert.Equal(withCloudProvider, gate.RequiredBySteps.Contains("register-radius-credentials-myenv"));
    }

    /// <summary>
    /// The gate contacts the cluster, which is deploy-only work: <c>aspire publish</c> must keep
    /// emitting artifacts on a machine with no cluster (or no <c>rad</c>) at all. Depending on
    /// <c>DeployPrereq</c> and being required only by deploy-side steps is what keeps it out of the
    /// publish graph.
    /// </summary>
    [Fact]
    public async Task ControlPlaneGate_IsNotPartOfThePublishGraph()
    {
        var steps = await CreateEnvironmentStepsAsync("myenv", withCloudProvider: false);

        var gate = Assert.Single(steps, step => step.Name == "verify-radius-control-plane-myenv");

        Assert.Equal([WellKnownPipelineSteps.DeployPrereq], gate.DependsOnSteps);
        Assert.DoesNotContain("publish-radius-myenv", gate.RequiredBySteps);
    }

    private static async Task<List<PipelineStep>> CreateEnvironmentStepsAsync(string environmentName, bool withCloudProvider)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var environment = builder.AddRadiusEnvironment(environmentName);
        if (withCloudProvider)
        {
            environment.WithAzureProvider(
                "00000000-0000-0000-0000-000000000000",
                "rg",
                azure => azure.WithServicePrincipal(
                    "00000000-0000-0000-0000-000000000001",
                    "00000000-0000-0000-0000-000000000002",
                    builder.AddParameter("clientsecret", "secret", secret: true)));
        }

        var annotation = Assert.Single(environment.Resource.Annotations.OfType<PipelineStepAnnotation>());
        var steps = await annotation.CreateStepsAsync(new PipelineStepFactoryContext
        {
            PipelineContext = null!,
            Resource = environment.Resource,
        });

        return steps.ToList();
    }
}
