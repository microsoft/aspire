// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Microsoft.DotNet.RemoteExecutor;

namespace Aspire.Hosting.Containers.Tests;

public class RequiresFeatureTests
{
    [Theory]
    [InlineData("")]
    [InlineData("docker")]
    [InlineData("podman")]
    [InlineData("wslc")]
    [InlineData("WSLC")]
    public void CiContainerFeatures_KeepDockerAndTestcontainersSeparate(string runtime)
    {
        RemoteExecutor.Invoke(static runtime =>
        {
            Environment.SetEnvironmentVariable("GITHUB_JOB", "test");
            Environment.SetEnvironmentVariable("ASPIRE_CONTAINER_RUNTIME", runtime);
            // CI capability is an expectation, not a probe that silently skips when setup fails.
            Environment.SetEnvironmentVariable("PATH", "");

            var expectedContainerRuntime = OperatingSystem.IsLinux()
                || (OperatingSystem.IsWindows() && runtime.Equals("wslc", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(expectedContainerRuntime, RequiresFeatureAttribute.IsFeatureSupported(TestFeature.ContainerRuntime));
            Assert.Equal(OperatingSystem.IsLinux(), RequiresFeatureAttribute.IsFeatureSupported(TestFeature.Docker));
            Assert.Equal(OperatingSystem.IsLinux(), RequiresFeatureAttribute.IsFeatureSupported(TestFeature.Testcontainers));
            Assert.False(TestcontainersPodmanConfiguration.IsConfigurationInitialized);
        }, runtime).Dispose();
    }
}
