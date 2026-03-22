// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Dcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Tests.Dcp;

[Trait("Partition", "4")]
public class DcpCliArgsTests
{
    [Fact]
    public void TestDcpCliPathArgumentPopulatesConfig()
    {
        var builder = DistributedApplication.CreateBuilder([
            "--dcp-cli-path", "/not/a/valid/path",
            ]);

        Assert.Equal("/not/a/valid/path", builder.Configuration["DcpPublisher:CliPath"]);
    }

    [Fact]
    public void TestDcpDependencyCheckTimeoutPopulatesConfig()
    {
        var builder = DistributedApplication.CreateBuilder([
            "--dcp-dependency-check-timeout", "42",
            ]);

        Assert.Equal("42", builder.Configuration["DcpPublisher:DependencyCheckTimeout"]);
    }

    [Fact]
    public void TestDcpContainerRuntimePopulatesConfig()
    {
        var builder = DistributedApplication.CreateBuilder([
            "--dcp-container-runtime", "not-a-valid-container-runtime",
            ]);

        Assert.Equal("not-a-valid-container-runtime", builder.Configuration["DcpPublisher:ContainerRuntime"]);
    }

    [Fact]
    public void TestDcpOptionsPopulated()
    {
        var builder = DistributedApplication.CreateBuilder(
            [
            "--dcp-cli-path", "/not/a/valid/path",
            "--dcp-container-runtime", "not-a-valid-container-runtime",
            "--dcp-dependency-check-timeout", "42",
            "--dcp-dashboard-path", "/not/a/valid/path"
            ]);

        using var app = builder.Build();
        var dcpOptions = app.Services.GetRequiredService<IOptions<DcpOptions>>().Value;

        Assert.Equal("not-a-valid-container-runtime", dcpOptions.ContainerRuntime);
        Assert.Equal(42, dcpOptions.DependencyCheckTimeout);
        Assert.Equal("/not/a/valid/path", dcpOptions.CliPath);
        Assert.Equal("/not/a/valid/path", dcpOptions.DashboardPath);
    }
}
