// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Azure.Provisioning.AppContainers;

namespace Aspire.Hosting.Azure.Tests.PublicApiTests;

public class AppContainersPublicApiTests
{
    [Fact]
    public void PublishAsAzureContainerAppShouldThrowWhenContainerIsNull()
    {
        IResourceBuilder<ContainerResource> container = null!;
        Action<AzureResourceInfrastructure, ContainerApp> configure = (r, c) => { };

        var action = () => container.PublishAsAzureContainerApp(configure);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(container), exception.ParamName);
    }

    [Fact]
    public void PublishAsAzureContainerAppShouldThrowWhenConfigureIsNull()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var container = builder.AddContainer("api", "myimage");
        Action<AzureResourceInfrastructure, ContainerApp> configure = null!;

        var action = () => container.PublishAsAzureContainerApp(configure);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(configure), exception.ParamName);
    }

    [Fact]
    public void CtorAzureContainerAppCustomizationAnnotationShouldThrowWhenConfigureIsNull()
    {
        Action<AzureResourceInfrastructure, ContainerApp> configure = null!;

        var action = () => new AzureContainerAppCustomizationAnnotation(configure);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(configure), exception.ParamName);
    }

    [Fact]
    public void PublishAsAzureContainerAppShouldThrowWhenExecutableIsNull()
    {
        IResourceBuilder<ExecutableResource> executable = null!;
        Action<AzureResourceInfrastructure, ContainerApp> configure = (r, c) => { };

        var action = () => executable.PublishAsAzureContainerApp(configure);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(executable), exception.ParamName);
    }

    [Fact]
    public void PublishAsAzureContainerAppForExecutableShouldThrowWhenConfigureIsNull()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var executable = builder.AddExecutable("api", "node.exe", Environment.CurrentDirectory);
        Action<AzureResourceInfrastructure, ContainerApp> configure = null!;

        var action = () => executable.PublishAsAzureContainerApp(configure);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(configure), exception.ParamName);
    }

    [Fact]
    public void AddAzureContainerAppsInfrastructureShouldThrowWhenBuilderIsNull()
    {
        IDistributedApplicationBuilder builder = null!;

        var action = () =>
        {
#pragma warning disable CS0618 // Type or member is obsolete
            builder.AddAzureContainerAppsInfrastructure();
#pragma warning restore CS0618 // Type or member is obsolete
        };

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void PublishAsAzureContainerAppShouldThrowWhenProjectIsNull()
    {
        IResourceBuilder<ProjectResource> project = null!;
        Action<AzureResourceInfrastructure, ContainerApp> configure = (r, c) => { };

        var action = () => project.PublishAsAzureContainerApp(configure);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(project), exception.ParamName);
    }

    [Fact]
    public void PublishAsAzureContainerAppForProjectShouldThrowWhenConfigureIsNull()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<ProjectA>("serviceA", o => o.ExcludeLaunchProfile = true);
        Action<AzureResourceInfrastructure, ContainerApp> configure = null!;

        var action = () => project.PublishAsAzureContainerApp(configure);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(configure), exception.ParamName);
    }

    [Fact]
    public void ConfigureCustomDomainShouldThrowWhenAppIsNull()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        ContainerApp app = null!;
        var customDomain = builder.AddParameter("customDomain");
        var certificateName = builder.AddParameter("certificateName");

        var action = () => app.ConfigureCustomDomain(customDomain, certificateName);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(app), exception.ParamName);
    }

    [Fact]
    public void ConfigureCustomDomainShouldThrowWhenCustomDomainIsNull()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var app = new ContainerApp("app");
        IResourceBuilder<ParameterResource> customDomain = null!;
        var certificateName = builder.AddParameter("certificateName");

        var action = () => app.ConfigureCustomDomain(customDomain, certificateName);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(customDomain), exception.ParamName);
    }

    [Fact]
    public void ConfigureCustomDomainShouldThrowWhenCertificateNameIsNull()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var app = new ContainerApp("app");
        var customDomain = builder.AddParameter("customDomain");
        IResourceBuilder<ParameterResource> certificateName = null!;

        var action = () => app.ConfigureCustomDomain(customDomain, certificateName);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(certificateName), exception.ParamName);
    }

    private sealed class ProjectA : IProjectMetadata
    {
        public string ProjectPath => "projectA";
    }
}
