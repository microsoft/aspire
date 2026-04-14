// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Radius.Tests;

public class RadiusExtensionsTests
{
    [Fact]
    public void AddRadiusEnvironment_CreatesResource_WithDefaults()
    {
        var builder = DistributedApplication.CreateBuilder();

        var resourceBuilder = builder.AddRadiusEnvironment();

        Assert.NotNull(resourceBuilder);
        Assert.Equal("radius", resourceBuilder.Resource.Name);
        Assert.Equal("default", resourceBuilder.Resource.Namespace);
    }

    [Fact]
    public void AddRadiusEnvironment_WithCustomName()
    {
        var builder = DistributedApplication.CreateBuilder();

        var resourceBuilder = builder.AddRadiusEnvironment("staging");

        Assert.Equal("staging", resourceBuilder.Resource.Name);
    }

    [Fact]
    public void AddRadiusEnvironment_AddsResourceToModel()
    {
        // The Run-mode short-circuit in AddRadiusEnvironment intentionally does not register
        // the environment with the application builder (matching the Kubernetes integration),
        // so this test runs the AppHost in Publish mode to verify the resource is registered
        // on the publish path.
        using var builder = Aspire.Hosting.Utils.TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment();

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        Assert.Single(model.Resources.OfType<RadiusEnvironmentResource>());
    }

    [Fact]
    public void WithNamespace_SetsNamespace()
    {
        var builder = DistributedApplication.CreateBuilder();

        var resourceBuilder = builder.AddRadiusEnvironment()
            .WithNamespace("staging-ns");

        Assert.Equal("staging-ns", resourceBuilder.Resource.Namespace);
    }

    [Fact]
    public void WithNamespace_ValidatesRFC1123_ThrowsOnInvalid()
    {
        var builder = DistributedApplication.CreateBuilder();
        var resourceBuilder = builder.AddRadiusEnvironment();

        Assert.Throws<ArgumentException>(() => resourceBuilder.WithNamespace("Invalid_Namespace"));
    }

    [Fact]
    public void WithNamespace_ThrowsOnLeadingHyphen()
    {
        var builder = DistributedApplication.CreateBuilder();
        var resourceBuilder = builder.AddRadiusEnvironment();

        Assert.Throws<ArgumentException>(() => resourceBuilder.WithNamespace("-invalid"));
    }

    [Fact]
    public void WithNamespace_ThrowsOnTrailingHyphen()
    {
        var builder = DistributedApplication.CreateBuilder();
        var resourceBuilder = builder.AddRadiusEnvironment();

        Assert.Throws<ArgumentException>(() => resourceBuilder.WithNamespace("invalid-"));
    }

    [Fact]
    public void WithNamespace_ThrowsOnUppercase()
    {
        var builder = DistributedApplication.CreateBuilder();
        var resourceBuilder = builder.AddRadiusEnvironment();

        Assert.Throws<ArgumentException>(() => resourceBuilder.WithNamespace("MyNamespace"));
    }

    [Fact]
    public void WithNamespace_ThrowsOnTooLong()
    {
        var builder = DistributedApplication.CreateBuilder();
        var resourceBuilder = builder.AddRadiusEnvironment();

        var longName = new string('a', 64);
        Assert.Throws<ArgumentException>(() => resourceBuilder.WithNamespace(longName));
    }

    [Fact]
    public void WithNamespace_AcceptsValidNames()
    {
        var builder = DistributedApplication.CreateBuilder();
        var resourceBuilder = builder.AddRadiusEnvironment();

        // Single char
        resourceBuilder.WithNamespace("a");
        Assert.Equal("a", resourceBuilder.Resource.Namespace);

        // Hyphens in middle
        resourceBuilder.WithNamespace("my-ns");
        Assert.Equal("my-ns", resourceBuilder.Resource.Namespace);

        // All numbers
        resourceBuilder.WithNamespace("123");
        Assert.Equal("123", resourceBuilder.Resource.Namespace);

        // Max length
        var maxName = new string('a', 63);
        resourceBuilder.WithNamespace(maxName);
        Assert.Equal(maxName, resourceBuilder.Resource.Namespace);
    }

    [Fact]
    public void AddRadiusEnvironment_ThrowsOnNullBuilder()
    {
        IDistributedApplicationBuilder builder = null!;

        Assert.Throws<ArgumentNullException>(() => builder.AddRadiusEnvironment());
    }

    [Fact]
    public void AddRadiusEnvironment_ThrowsOnEmptyName()
    {
        var builder = DistributedApplication.CreateBuilder();

        Assert.Throws<ArgumentException>(() => builder.AddRadiusEnvironment(""));
    }
}
