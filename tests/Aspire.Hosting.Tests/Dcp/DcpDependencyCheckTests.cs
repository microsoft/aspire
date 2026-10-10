// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Dcp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace Aspire.Hosting.Tests.Dcp;

[Trait("Partition", "4")]
public sealed class DcpDependencyCheckTests
{
    [Fact]
    public void CheckDcpInfoAndLogErrors_UnhealthyRuntimeSurfacesErrorAtWarningAndInException()
    {
        const string error = "docker CLI version 23.0.6 is not supported; DCP requires Docker CLI version 25.0.0 or newer";
        var logger = new FakeLogger();
        var options = new DcpOptions { ContainerRuntime = "docker" };
        var dcpInfo = new DcpInfo
        {
            Containers = new DcpContainersInfo
            {
                Installed = true,
                Running = false,
                Error = error
            }
        };
        var (message, linkUrl) = DcpDependencyCheck.BuildContainerRuntimeUnhealthyMessage("docker");

        var exception = Assert.Throws<DistributedApplicationException>(
            () => DcpDependencyCheck.CheckDcpInfoAndLogErrors(logger, options, dcpInfo, throwIfUnhealthy: true));

        Assert.Equal(
            $"{message}{Environment.NewLine}The error from the container runtime check was: {error}",
            exception.Message);
        var warning = Assert.Single(logger.Collector.GetSnapshot());
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Equal(
            $"{message} For more information, visit: {linkUrl}{Environment.NewLine}The error from the container runtime check was: {error}",
            warning.Message);
    }

    [Fact]
    public void CheckDcpInfoAndLogErrors_MissingRuntimeSurfacesErrorAtWarningAndInException()
    {
        const string error = "exec: \"docker\": executable file not found in PATH";
        const string message = "Container runtime 'docker' could not be found. See https://aka.ms/aspire/containers for more details on supported container runtimes.";
        var logger = new FakeLogger();
        var options = new DcpOptions { ContainerRuntime = "docker" };
        var dcpInfo = new DcpInfo
        {
            Containers = new DcpContainersInfo
            {
                Installed = false,
                Running = false,
                Error = error
            }
        };

        var exception = Assert.Throws<DistributedApplicationException>(
            () => DcpDependencyCheck.CheckDcpInfoAndLogErrors(logger, options, dcpInfo, throwIfUnhealthy: true));

        Assert.Equal(
            $"{message}{Environment.NewLine}The error from the container runtime check was: {error}",
            exception.Message);
        var warning = Assert.Single(logger.Collector.GetSnapshot());
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Equal(
            $"{message}{Environment.NewLine}The error from the container runtime check was: {error}",
            warning.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CheckDcpInfoAndLogErrors_WithoutErrorPreservesGenericDiagnostic(string? error)
    {
        var logger = new FakeLogger();
        var options = new DcpOptions { ContainerRuntime = "docker" };
        var dcpInfo = new DcpInfo
        {
            Containers = new DcpContainersInfo
            {
                Installed = true,
                Running = false,
                Error = error
            }
        };
        var (message, linkUrl) = DcpDependencyCheck.BuildContainerRuntimeUnhealthyMessage("docker");

        var exception = Assert.Throws<DistributedApplicationException>(
            () => DcpDependencyCheck.CheckDcpInfoAndLogErrors(logger, options, dcpInfo, throwIfUnhealthy: true));

        Assert.Equal(message, exception.Message);
        var warning = Assert.Single(logger.Collector.GetSnapshot());
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Equal($"{message} For more information, visit: {linkUrl}", warning.Message);
    }
}
