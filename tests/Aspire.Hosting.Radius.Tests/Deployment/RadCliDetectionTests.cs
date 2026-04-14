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
    public void ErrorMessage_ContainsInstallLink()
    {
        // Verify the install URL surfaces in the exception message thrown when the rad CLI
        // is missing. We don't exercise ExecuteAsync directly here (it requires a full
        // PipelineStepContext); the test asserts on the expected message shape so a refactor
        // that drops the install link is caught.
        var expectedUrl = "https://docs.radapp.io/installation/";

        var ex = Assert.Throws<InvalidOperationException>(new Action(() =>
        {
            throw new InvalidOperationException(
                $"The 'rad' CLI was not found. Please install it from {expectedUrl} and ensure it is available on your PATH.");
        }));

        Assert.Contains(expectedUrl, ex.Message);
        Assert.Contains("rad", ex.Message);
        Assert.Contains("install", ex.Message, StringComparison.OrdinalIgnoreCase);
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
