// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dotnet.Tests.Helpers;
using Aspire.Hosting.Tests;

namespace Aspire.Hosting.Dotnet.Tests;

public sealed class DotnetProjectWithReferenceTests(ITestOutputHelper output)
    : WithReferenceTests<DotnetProjectResource>, IDisposable
{
    private readonly DotnetProjectContractFactory _factory = new(output);

    protected override IResourceBuilder<DotnetProjectResource> AddProject<TProject>(
        IDistributedApplicationBuilder builder, string name)
    {
        return _factory.AddProject<TProject>(builder, name);
    }

    public void Dispose()
    {
        _factory.Dispose();
    }
}

public sealed class DotnetProjectWithEnvironmentTests(ITestOutputHelper output)
    : WithEnvironmentTests<DotnetProjectResource>, IDisposable
{
    private readonly DotnetProjectContractFactory _factory = new(output);

    protected override IResourceBuilder<DotnetProjectResource> AddProject<TProject>(
        IDistributedApplicationBuilder builder, string name)
    {
        return _factory.AddProject<TProject>(builder, name);
    }

    public void Dispose()
    {
        _factory.Dispose();
    }
}

public sealed class DotnetProjectWithEndpointTests(ITestOutputHelper output)
    : WithEndpointTests<DotnetProjectResource>, IDisposable
{
    private readonly DotnetProjectContractFactory _factory = new(output);

    protected override IResourceBuilder<DotnetProjectResource> AddProject<TProject>(
        IDistributedApplicationBuilder builder, string name)
    {
        return _factory.AddProject<TProject>(builder, name);
    }

    public void Dispose()
    {
        _factory.Dispose();
    }
}

public sealed class DotnetProjectWithUrlsTests(ITestOutputHelper output)
    : WithUrlsTests<DotnetProjectResource>(output), IDisposable
{
    private readonly DotnetProjectContractFactory _factory = new(output);

    protected override IResourceBuilder<DotnetProjectResource> AddProject<TProject>(
        IDistributedApplicationBuilder builder, string name)
    {
        return _factory.AddProject<TProject>(builder, name);
    }

    protected override IResourceBuilder<DotnetProjectResource> AddProject<TProject>(
        IDistributedApplicationBuilder builder, string name, string? launchProfileName)
    {
        return _factory.AddProject<TProject>(builder, name, launchProfileName);
    }

    protected override void SetReplicas(IResourceBuilder<DotnetProjectResource> project, int replicas)
    {
        project.WithReplicas(replicas);
    }

    public void Dispose()
    {
        _factory.Dispose();
    }
}

public sealed class DotnetProjectWithVolumeTests(ITestOutputHelper output)
    : WithVolumeTests<DotnetProjectResource>, IDisposable
{
    private readonly DotnetProjectContractFactory _factory = new(output);

    protected override IResourceBuilder<DotnetProjectResource> AddProject<TProject>(
        IDistributedApplicationBuilder builder, string name, string? launchProfileName)
    {
        return _factory.AddProject<TProject>(builder, name, launchProfileName);
    }

    public void Dispose()
    {
        _factory.Dispose();
    }
}

public sealed class DotnetProjectContainerRegistryTests(ITestOutputHelper output)
    : ContainerRegistryResourceTests<DotnetProjectResource>, IDisposable
{
    private readonly DotnetProjectContractFactory _factory = new(output);

    protected override IResourceBuilder<DotnetProjectResource> AddProject<TProject>(
        IDistributedApplicationBuilder builder, string name)
    {
        return _factory.AddProject<TProject>(builder, name);
    }

    public void Dispose()
    {
        _factory.Dispose();
    }
}

public sealed class DotnetProjectContainerTunnelTests(ITestOutputHelper output)
    : ContainerTunnelTests<DotnetProjectResource>(output), IDisposable
{
    private readonly DotnetProjectContractFactory _factory = new(output);

    protected override IResourceBuilder<DotnetProjectResource> AddProject<TProject>(
        IDistributedApplicationBuilder builder, string name)
    {
        return _factory.AddProject<TProject>(builder, name);
    }

    protected override IResourceBuilder<DotnetProjectResource> AddProject<TProject>(
        IDistributedApplicationBuilder builder, string name, string? launchProfileName)
    {
        return _factory.AddProject<TProject>(builder, name, launchProfileName);
    }

    public void Dispose()
    {
        _factory.Dispose();
    }
}
