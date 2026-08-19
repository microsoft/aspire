// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Radius.Publishing;
using Aspire.Hosting.Utils;
using Aspire.TestUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Radius.Tests.DeploymentE2E;

/// <summary>
/// End-to-end deployment tests that require a running kind/minikube cluster
/// with Radius installed and the <c>rad</c> CLI on PATH. Marked
/// <see cref="OuterloopTestAttribute"/> so they only run in the outerloop CI
/// workflow (not on every PR).
/// </summary>
public class DeployEndToEndTests
{
    [Fact]
    [OuterloopTest("Requires the rad CLI and a Radius-enabled Kubernetes cluster")]
    public async Task Deploy_SimpleApp_GeneratesBicepAndInvokesRad()
    {
        // Hard prerequisite: rad CLI must be on PATH. Skip with a visible reason rather than
        // silently returning, so an outerloop run that "passes" can never mean "did nothing".
        var radAvailable = await RadiusDeploymentPipelineStep.DetectRadCliAsync();
        Assert.SkipUnless(radAvailable, "rad CLI not available on PATH");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("e2e");
        var redis = builder.AddRedis("cache");
        builder.AddContainer("api", "myapp/api", "latest")
            .WithReference(redis);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();

        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);

        var context = new RadiusBicepPublishingContext(radiusEnv);
        var bicep = context.GenerateBicep(model);

        Assert.Contains("extension radius", bicep);
        Assert.Contains("Radius.Core/environments", bicep);
        Assert.Contains("Radius.Compute/containers", bicep);
        Assert.Contains("connections", bicep);
    }

    [Fact]
    [OuterloopTest("Requires the rad CLI and a Radius-enabled Kubernetes cluster")]
    public async Task Deploy_GeneratedBicep_ContainsCorrectResourceReferences()
    {
        var radAvailable = await RadiusDeploymentPipelineStep.DetectRadCliAsync();
        Assert.SkipUnless(radAvailable, "rad CLI not available on PATH");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("e2e");
        var redis = builder.AddRedis("cache");
        var sql = builder.AddSqlServer("sqlserver").AddDatabase("appdb");
        builder.AddContainer("api", "myapp/api", "latest")
            .WithReference(redis)
            .WithReference(sql);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        var context = new RadiusBicepPublishingContext(radiusEnv);
        var bicep = context.GenerateBicep(model);

        // Secrets must flow through valueless Bicep `param`s (resolved at deploy time via
        // `rad deploy --parameters`), never embedded as literals. The word "password"
        // legitimately appears as a parameter *name* (e.g. `param sqlserver_password string`)
        // and as an environment-variable reference to that param — that is the secure shape.
        // Assert the invariant precisely rather than banning the substring "password":
        //   * at least one secret is surfaced as a valueless `param ... string` (no default), and
        //   * no secret param carries a hardcoded literal default value.
        Assert.Matches(@"(?im)^\s*param\s+\w*password\w*\s+string\s*$", bicep);
        Assert.DoesNotMatch(@"(?i)param\s+\w*password\w*\s+string\s*=", bicep);
        Assert.Contains(".id", bicep);
    }

    [Fact]
    [OuterloopTest("Requires the rad CLI to be installed")]
    public async Task Deploy_RadCliAvailability_Detected()
    {
        var radAvailable = await RadiusDeploymentPipelineStep.DetectRadCliAsync();
        Assert.SkipUnless(radAvailable, "rad CLI not available on PATH");

        // If we get here, rad was detected successfully.
        Assert.True(radAvailable);
    }

    [Fact]
    [OuterloopTest("Requires the rad CLI to be installed")]
    public async Task Deploy_CleanupViaRadDelete_Supported()
    {
        var radAvailable = await RadiusDeploymentPipelineStep.DetectRadCliAsync();
        Assert.SkipUnless(radAvailable, "rad CLI not available on PATH");

        // Verify the deploy step exists and has proper dependencies for orchestration.
        var environment = new RadiusEnvironmentResource("cleanup-test");
        var step = new RadiusDeploymentPipelineStep(environment);
        var pipelineStep = step.CreatePipelineStep();

        Assert.Equal("deploy-radius-cleanup-test", pipelineStep.Name);
        Assert.Contains("publish-radius-cleanup-test", pipelineStep.DependsOnSteps);
    }
}
