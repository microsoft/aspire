// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "2")]
public class ResourceTraitBuilderTests
{
    [Fact]
    public void RepeatedTraitsAndTagsPreserveAllValues()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddContainer("api", "image")
            .WithTags("Backend", "API")
            .WithTrait("Owner", "Finance").WithTrait("Owner", "Platform")
            .WithDashboardPosition(20).WithDashboardPosition(-10);

        Assert.Equal(
            [
                (KnownResourceTraits.DashboardTag, "Backend"),
                (KnownResourceTraits.DashboardTag, "API"),
                ("Owner", "Finance"),
                ("Owner", "Platform"),
                (KnownResourceTraits.DashboardPosition, "-10")
            ],
            resource.Resource.Annotations.OfType<TraitAnnotation>().Select(t => (t.Name, t.Value)));
    }

    [Theory]
    [InlineData("", "value")]
    [InlineData("Owner", " ")]
    [InlineData("Aspire_Dashboard_Tag", "  Backend")]
    [InlineData("Aspire_Dashboard_Tag", "Back  end")]
    [InlineData("Aspire_Dashboard_Position", "invalid")]
    [InlineData("Aspire_Dashboard_Position", "2147483648")]
    public void InvalidTraitsAreRejected(string name, string value)
    {
        Assert.ThrowsAny<ArgumentException>(() => new TraitAnnotation(name, value));
    }

    [Fact]
    public void TraitsProduceListValuedSnapshotProperties()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddContainer("api", "image")
            .WithTags("Backend", "API")
            .WithTrait("Owner", "Finance, Inc.").WithTrait("Owner", "Platform")
            .WithTrait("Owner", "Finance, Inc.");
        var source = new ResourcePropertySnapshot("existing", "value");
        var properties = ResourceTraitSnapshotProperties.AddTraitProperties(resource.Resource, [source]);

        Assert.Same(source, properties[0]);
        Assert.Equal(["existing", "resource.trait.Aspire_Dashboard_Tag", "resource.trait.Owner"], properties.Select(p => p.Name));
        Assert.Equal(["Backend", "API"], Assert.IsType<string[]>(properties[1].Value));
        Assert.Equal(["Finance, Inc.", "Platform"], Assert.IsType<string[]>(properties[2].Value));
        Assert.Equal("Owner", properties[2].DisplayName);
    }

    [Fact]
    public void PositionTraitReplacesOnlyPositionAndValidatesBeforeReplacing()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddContainer("api", "image").WithTags("Backend").WithDashboardPosition(10);
        Assert.Throws<ArgumentException>(() => resource.WithTrait(KnownResourceTraits.DashboardPosition, "invalid"));
        resource.WithTrait(KnownResourceTraits.DashboardPosition, "20");

        Assert.Equal([(KnownResourceTraits.DashboardTag, "Backend"), (KnownResourceTraits.DashboardPosition, "20")],
            resource.Resource.Annotations.OfType<TraitAnnotation>().Select(t => (t.Name, t.Value)));
    }

    [Fact]
    public void WithTagsAcceptsSingleMultipleAndSpanArguments()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddContainer("api", "image");
        ReadOnlySpan<string> tags = ["Finance", "Platform"];

        Assert.Same(resource, resource.WithTags("Backend"));
        Assert.Same(resource, resource.WithTags("API", "Service"));
        Assert.Same(resource, resource.WithTags(tags));
        Assert.Same(resource, resource.WithTags());
        Assert.Equal(["Backend", "API", "Service", "Finance", "Platform"],
            resource.Resource.Annotations.OfType<TraitAnnotation>().Select(t => t.Value));
    }
}
