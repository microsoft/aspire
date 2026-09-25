// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Utils;
using Aspire.Shared;
using Microsoft.DotNet.RemoteExecutor;

namespace Aspire.Hosting.Tests;

public class DockerUtilsTests
{
    [Fact]
    public void ResolveRuntimeExecutable_WhenDetectorThrows_ReturnsFailure()
    {
        var expectedException = new InvalidOperationException("probe failed");

        var resolution = DockerUtils.ResolveRuntimeExecutable(
            (_, _) => Task.FromException<ContainerRuntimeInfo?>(expectedException));

        Assert.Null(resolution.Executable);
        Assert.Equal("container runtime detection failed with InvalidOperationException: probe failed", resolution.FailureReason);
        Assert.Same(expectedException, resolution.DetectionException);
    }

    [Fact]
    public void AttemptDeleteDockerVolume_WhenRuntimeIsUnavailable_ReportsSkippedCleanup()
    {
        const string MissingRuntime = "aspire-missing-container-runtime";
        var options = new RemoteInvokeOptions();
        options.StartInfo.Environment[KnownConfigNames.ContainerRuntime] = MissingRuntime;

        RemoteExecutor.Invoke(static () =>
        {
            using var output = new StringWriter();
            Console.SetOut(output);

            DockerUtils.AttemptDeleteDockerVolume("test-volume");

            Assert.Equal(
                $"Failed to delete the volume named 'test-volume': the container runtime configured by {KnownConfigNames.ContainerRuntime} ('{MissingRuntime}') is not available.",
                output.ToString().Trim());
        }, options).Dispose();
    }
}
