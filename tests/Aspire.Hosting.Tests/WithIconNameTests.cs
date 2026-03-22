// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "3")]
public class WithIconNameTests
{
    [Fact]
    public void WithIconName_SetsIconNameAndDefaultVariant()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var container = builder.AddContainer("mycontainer", "myimage")
                              .WithIconName("Database");

        // Verify the annotation was added
        var iconAnnotation = container.Resource.Annotations.OfType<ResourceIconAnnotation>().Single();
        Assert.Equal("Database", iconAnnotation.IconName);
        Assert.Equal(IconVariant.Filled, iconAnnotation.IconVariant);
    }

    [Fact]
    public void WithIconName_SetsIconNameAndCustomVariant()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var container = builder.AddContainer("mycontainer", "myimage")
                              .WithIconName("CloudArrowUp", IconVariant.Regular);

        // Verify the annotation was added with correct variant
        var iconAnnotation = container.Resource.Annotations.OfType<ResourceIconAnnotation>().Single();
        Assert.Equal("CloudArrowUp", iconAnnotation.IconName);
        Assert.Equal(IconVariant.Regular, iconAnnotation.IconVariant);
    }

    [Fact]
    public void WithIconName_ThrowsOnNullIconName()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var container = builder.AddContainer("mycontainer", "myimage");

        Assert.Throws<ArgumentNullException>(() => container.WithIconName(null!));
    }

    [Fact]
    public void WithIconName_ThrowsOnEmptyIconName()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var container = builder.AddContainer("mycontainer", "myimage");

        Assert.Throws<ArgumentException>(() => container.WithIconName(""));
        Assert.Throws<ArgumentException>(() => container.WithIconName("   "));
    }

    [Fact]
    public void WithIconName_CanBeCalledOnAnyResourceType()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        // Test on different resource types
        var container = builder.AddContainer("container", "image")
                              .WithIconName("Box");

        var parameter = builder.AddParameter("param")
                              .WithIconName("Settings", IconVariant.Regular);

        var project = builder.AddProject<TestProject>("project")
                            .WithIconName("CodeCircle");

        // Verify all have the annotations
        Assert.Single(container.Resource.Annotations.OfType<ResourceIconAnnotation>());
        Assert.Single(parameter.Resource.Annotations.OfType<ResourceIconAnnotation>());
        Assert.Single(project.Resource.Annotations.OfType<ResourceIconAnnotation>());
    }

    [Fact]
    public void WithIconName_OverridesExistingIconAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var container = builder.AddContainer("mycontainer", "myimage")
                              .WithIconName("Database")
                              .WithIconName("CloudArrowUp", IconVariant.Regular);

        // Should have both annotations (WithIconName adds, doesn't replace)
        var iconAnnotations = container.Resource.Annotations.OfType<ResourceIconAnnotation>().ToList();
        Assert.Equal(2, iconAnnotations.Count);
        
        // Get the latest one - this is what ResourceNotificationService should use
        var latestAnnotation = iconAnnotations.Last();
        Assert.Equal("CloudArrowUp", latestAnnotation.IconName);
        Assert.Equal(IconVariant.Regular, latestAnnotation.IconVariant);
        
        // Verify that TryGetLastAnnotation returns the correct one
        Assert.True(container.Resource.TryGetLastAnnotation<ResourceIconAnnotation>(out var lastAnnotation));
        Assert.Equal("CloudArrowUp", lastAnnotation.IconName);
        Assert.Equal(IconVariant.Regular, lastAnnotation.IconVariant);
    }

    private sealed class TestProject : IProjectMetadata
    {
        public string ProjectPath => "TestProject.csproj";

        public LaunchSettings LaunchSettings { get; set; } = new();
    }
}