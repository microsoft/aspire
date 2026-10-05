// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Radius.Publishing;
using Aspire.TestUtilities;

namespace Aspire.Hosting.Radius.Tests.Deployment;

public class RadCliDetectionTests
{
    [Fact]
    [OuterloopTest("Spawns the real `rad` CLI process to verify detection")]
    public async Task DetectRadCliAsync_ReturnsBoolean()
    {
        // DetectRadCliAsync should not throw regardless of whether rad is installed.
        var result = await RadiusDeploymentPipelineStep.DetectRadCliAsync();
        Assert.IsType<bool>(result);
    }

    [Fact]
    [OuterloopTest("Spawns the real `rad` CLI process to verify detection")]
    public async Task DetectRadCliAsync_ReturnsTrueOrFalse_BasedOnEnvironment()
    {
        // Calling detection twice yields the same result — there is no caching, the result
        // is determined by the presence of `rad` on PATH for the current process.
        var result1 = await RadiusDeploymentPipelineStep.DetectRadCliAsync();
        var result2 = await RadiusDeploymentPipelineStep.DetectRadCliAsync();

        Assert.Equal(result1, result2);
    }

    [Fact]
    public async Task DetectRadCliAsync_SupportsCancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Pre-cancelled tokens should propagate as OperationCanceledException — they must not
        // be swallowed by the catch-all in DetectRadCliAsync. Honouring cancellation is what
        // lets CTRL-C abort a pending `rad version` probe.
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => RadiusDeploymentPipelineStep.DetectRadCliAsync(cts.Token));
    }

    [Fact]
    public void RadCliNotFoundException_ContainsInstallLinkAndRemediation()
    {
        // Exercises the real production factory both throw sites use (deploy step and
        // credential-register step). Asserting on the actual message means a refactor that
        // drops the install link or PATH remediation fails this test — unlike asserting on a
        // hand-written literal, which cannot observe production changes.
        var ex = RadiusDeploymentPipelineStep.CreateRadCliNotFoundException();

        Assert.Contains(RadiusDeploymentPipelineStep.RadInstallUrl, ex.Message, StringComparison.Ordinal);
        Assert.Contains("rad", ex.Message, StringComparison.Ordinal);
        Assert.Contains("install", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PATH", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeployStep_HasCorrectName()
    {
        var environment = new RadiusEnvironmentResource("myenv");
        var step = new RadiusDeploymentPipelineStep(environment);

        var pipelineStep = step.CreatePipelineStep();

        Assert.Equal("deploy-radius-myenv", pipelineStep.Name);
    }

    [Fact]
    public void DeployStep_DependsOnPublishStep()
    {
        var environment = new RadiusEnvironmentResource("myenv");
        var step = new RadiusDeploymentPipelineStep(environment);

        var pipelineStep = step.CreatePipelineStep();

        // Step should depend on publish, not push — Radius supports kind clusters without
        // a container registry, so the deploy step intentionally skips the push prerequisite.
        Assert.Contains("publish-radius-myenv", pipelineStep.DependsOnSteps);
        Assert.DoesNotContain("push", pipelineStep.DependsOnSteps, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeployStep_RequiredByDeployWellKnownStep()
    {
        var environment = new RadiusEnvironmentResource("testenv");
        var step = new RadiusDeploymentPipelineStep(environment);

        var pipelineStep = step.CreatePipelineStep();

        Assert.Contains("deploy", pipelineStep.RequiredBySteps);
    }
}
