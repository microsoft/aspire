// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRERADIUS001 // WithContainerImage is marked experimental; opt in for tests.

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Radius.Tests;

public class RadiusExtensionsTests
{
    [Fact]
    public void AddRadiusEnvironment_CreatesResource_WithDefaults()
    {
        var builder = DistributedApplication.CreateBuilder();

        var resourceBuilder = builder.AddRadiusEnvironment("radius");

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
        builder.AddRadiusEnvironment("radius");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        Assert.Single(model.Resources.OfType<RadiusEnvironmentResource>());
    }

    [Fact]
    public void WithNamespace_SetsNamespace()
    {
        var builder = DistributedApplication.CreateBuilder();

        var resourceBuilder = builder.AddRadiusEnvironment("radius")
            .WithNamespace("staging-ns");

        Assert.Equal("staging-ns", resourceBuilder.Resource.Namespace);
    }

    [Fact]
    public void WithNamespace_ValidatesRFC1123_ThrowsOnInvalid()
    {
        var builder = DistributedApplication.CreateBuilder();
        var resourceBuilder = builder.AddRadiusEnvironment("radius");

        Assert.Throws<ArgumentException>(() => resourceBuilder.WithNamespace("Invalid_Namespace"));
    }

    [Fact]
    public void WithNamespace_ThrowsOnLeadingHyphen()
    {
        var builder = DistributedApplication.CreateBuilder();
        var resourceBuilder = builder.AddRadiusEnvironment("radius");

        Assert.Throws<ArgumentException>(() => resourceBuilder.WithNamespace("-invalid"));
    }

    [Fact]
    public void WithNamespace_ThrowsOnTrailingHyphen()
    {
        var builder = DistributedApplication.CreateBuilder();
        var resourceBuilder = builder.AddRadiusEnvironment("radius");

        Assert.Throws<ArgumentException>(() => resourceBuilder.WithNamespace("invalid-"));
    }

    [Fact]
    public void WithNamespace_ThrowsOnUppercase()
    {
        var builder = DistributedApplication.CreateBuilder();
        var resourceBuilder = builder.AddRadiusEnvironment("radius");

        Assert.Throws<ArgumentException>(() => resourceBuilder.WithNamespace("MyNamespace"));
    }

    [Fact]
    public void WithNamespace_ThrowsOnTooLong()
    {
        var builder = DistributedApplication.CreateBuilder();
        var resourceBuilder = builder.AddRadiusEnvironment("radius");

        var longName = new string('a', 64);
        Assert.Throws<ArgumentException>(() => resourceBuilder.WithNamespace(longName));
    }

    [Fact]
    public void WithNamespace_AcceptsValidNames()
    {
        var builder = DistributedApplication.CreateBuilder();
        var resourceBuilder = builder.AddRadiusEnvironment("radius");

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

        Assert.Throws<ArgumentNullException>(() => builder.AddRadiusEnvironment("radius"));
    }

    [Fact]
    public void AddRadiusEnvironment_ThrowsOnEmptyName()
    {
        var builder = DistributedApplication.CreateBuilder();

        Assert.Throws<ArgumentException>(() => builder.AddRadiusEnvironment(""));
    }

    // The cases below pin the documented behaviour of RadiusExtensions.ParseImageReference
    // (private), which is exercised through WithContainerImage. The heuristic for splitting
    // the registry segment from a Docker Hub user namespace (contains '.' or ':' or equals
    // "localhost") matches Docker/containerd, and we own the documented examples in the
    // RadiusExtensions.ParseImageReference summary comment — keep this table in sync if
    // those docs change.
    [Theory]
    [InlineData("redis", null, "redis", "latest")]
    [InlineData("redis:7", null, "redis", "7")]
    [InlineData("library/redis:7", null, "library/redis", "7")]
    [InlineData("localhost:5001/api:latest", "localhost:5001", "api", "latest")]
    [InlineData("ghcr.io/owner/repo:v1", "ghcr.io", "owner/repo", "v1")]
    public void WithContainerImage_ParsesImageReference(
        string image, string? expectedRegistry, string expectedImage, string expectedTag)
    {
        using var builder = Aspire.Hosting.Utils.TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var project = builder.AddProject<ParseImageReferenceProjectMetadata>("webapp");

        project.WithContainerImage(image);

        var annotation = project.Resource.Annotations.OfType<ContainerImageAnnotation>().Single();
        Assert.Equal(expectedRegistry, annotation.Registry);
        Assert.Equal(expectedImage, annotation.Image);
        Assert.Equal(expectedTag, annotation.Tag);
    }

    [Fact]
    public void WithContainerImage_ReplacesExistingAnnotation()
    {
        // The documented contract on WithContainerImage is that a second call overrides the
        // first (matches the LastOrDefault() lookup the publisher uses). Pin that here too.
        using var builder = Aspire.Hosting.Utils.TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var project = builder.AddProject<ParseImageReferenceProjectMetadata>("webapp");

        project.WithContainerImage("redis:6");
        project.WithContainerImage("ghcr.io/owner/repo:v2");

        var annotation = project.Resource.Annotations.OfType<ContainerImageAnnotation>().Single();
        Assert.Equal("ghcr.io", annotation.Registry);
        Assert.Equal("owner/repo", annotation.Image);
        Assert.Equal("v2", annotation.Tag);
    }

    private sealed class ParseImageReferenceProjectMetadata : IProjectMetadata
    {
        public string ProjectPath => "testproject";
        public LaunchSettings LaunchSettings { get; } = new();
    }
}
