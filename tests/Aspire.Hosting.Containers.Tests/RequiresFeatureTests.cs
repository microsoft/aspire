// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Microsoft.DotNet.RemoteExecutor;

namespace Aspire.Hosting.Containers.Tests;

public class RequiresFeatureTests
{
    [Theory]
    [InlineData("GITHUB_JOB")]
    [InlineData("BUILD_BUILDID")]
    [InlineData("HELIX_WORKITEM_ROOT")]
    public void CiContainerFeatures_KeepDockerAndTestcontainersSeparate(string ciVariable)
    {
        RemoteExecutor.Invoke(static ciVariable =>
        {
            Environment.SetEnvironmentVariable("GITHUB_JOB", null);
            Environment.SetEnvironmentVariable("BUILD_BUILDID", null);
            Environment.SetEnvironmentVariable("HELIX_WORKITEM_ROOT", null);
            Environment.SetEnvironmentVariable(ciVariable, "test");
            Environment.SetEnvironmentVariable("ASPIRE_CONTAINER_RUNTIME", null);
            // CI capability is an expectation, not a probe that silently skips when setup fails.
            Environment.SetEnvironmentVariable("PATH", "");

            var expectedContainerRuntime = OperatingSystem.IsLinux()
                || (OperatingSystem.IsWindows() && ciVariable == "GITHUB_JOB");
            Assert.Equal(expectedContainerRuntime, RequiresFeatureAttribute.IsFeatureSupported(TestFeature.ContainerRuntime));
            Assert.Equal(OperatingSystem.IsLinux(), RequiresFeatureAttribute.IsFeatureSupported(TestFeature.Docker));
            Assert.Equal(OperatingSystem.IsLinux(), RequiresFeatureAttribute.IsFeatureSupported(TestFeature.Testcontainers));
            Assert.Equal(OperatingSystem.IsLinux(), TestcontainersPodmanConfiguration.IsConfigurationInitialized);
        }, ciVariable).Dispose();
    }
}
