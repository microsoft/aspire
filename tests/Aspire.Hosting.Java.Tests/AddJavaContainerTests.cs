// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Java.Tests;

public class AddJavaContainerTests
{
    [Fact]
    public void AddJavaContainerShouldThrowWhenBuilderIsNull()
    {
        IDistributedApplicationBuilder builder = null!;

        var action = () => builder.AddJavaContainer("catalog", "mycompany/catalog");

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void AddJavaContainerShouldThrowWhenNameIsNullOrWhitespace(string? name)
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var action = () => builder.AddJavaContainer(name!, "mycompany/catalog");

        var exception = name is null
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal("name", exception.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void AddJavaContainerShouldThrowWhenImageIsNullOrWhitespace(string? image)
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var action = () => builder.AddJavaContainer("catalog", image!);

        var exception = image is null
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal("image", exception.ParamName);
    }

    [Fact]
    public void AddJavaContainer_UsesTheRequestedImageAndTag()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddJavaContainer("catalog", "mycompany/catalog", "1.4.0");

        var image = Assert.Single(app.Resource.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal("mycompany/catalog", image.Image);
        Assert.Equal("1.4.0", image.Tag);
    }

    [Fact]
    public void AddJavaContainer_WithoutTag_LeavesTheTagToTheContainerRuntime()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddJavaContainer("catalog", "mycompany/catalog");

        var image = Assert.Single(app.Resource.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal("mycompany/catalog", image.Image);
        Assert.Equal("latest", image.Tag);
    }

    [Fact]
    public void AddJavaContainer_IsDiscoverableAndUsesTheJavaIcon()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddJavaContainer("catalog", "mycompany/catalog");

        Assert.IsAssignableFrom<IResourceWithServiceDiscovery>(app.Resource);
        Assert.IsAssignableFrom<IJavaAppResource>(app.Resource);
        var icon = Assert.Single(app.Resource.Annotations.OfType<ResourceIconAnnotation>());
        Assert.Equal("DrinkCoffee", icon.IconName);
    }

    [Fact]
    public void AddJavaContainer_DeclaresNoEndpoint()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        // The port belongs to the image, so callers pick it with WithHttpEndpoint. Asserting the
        // absence here is what keeps a default 8080 endpoint from being reintroduced silently.
        var app = builder.AddJavaContainer("catalog", "mycompany/catalog");

        Assert.Empty(app.Resource.Annotations.OfType<EndpointAnnotation>());
    }

    [Fact]
    public async Task AddJavaContainer_ExportsTelemetryToAspire()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"] = "http://localhost:4317";

        var app = builder.AddJavaContainer("catalog", "mycompany/catalog");

        // Built rather than evaluated against TestServiceProvider because resolving a container's OTLP
        // endpoint needs the DCP options that decide how the container reaches the host, which is also
        // why the asserted host is not the configured localhost.
        using var built = builder.Build();
        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            app.Resource, serviceProvider: built.Services.GetRequiredService<IServiceProvider>());

        Assert.Equal("http://host.docker.internal:4317", envVars["OTEL_EXPORTER_OTLP_ENDPOINT"]);
    }

    [Fact]
    public async Task WithJvmArgs_AppliesToAContainerToo()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"] = "http://localhost:4317";

        var app = builder.AddJavaContainer("catalog", "mycompany/catalog")
            .WithJvmArgs(["-Xmx512m"])
            .WithJvmArgs(["-javaagent:/app/opentelemetry-javaagent.jar"]);

        using var built = builder.Build();
        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            app.Resource, serviceProvider: built.Services.GetRequiredService<IServiceProvider>());

        Assert.Equal("-Xmx512m -javaagent:/app/opentelemetry-javaagent.jar", envVars["JAVA_TOOL_OPTIONS"]);
    }

    [Fact]
    public void AddJavaContainer_KeepsTheJvmBuiltInCertificateAuthorities()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddJavaContainer("catalog", "mycompany/catalog");

        // The JVM's trust store setting replaces the default authorities rather than adding to them,
        // so the bundle Aspire generates has to carry the system roots as well.
        var scope = Assert.Single(app.Resource.Annotations.OfType<CertificateAuthorityCollectionAnnotation>());
        Assert.Equal(CertificateTrustScope.System, scope.Scope);
    }
}
