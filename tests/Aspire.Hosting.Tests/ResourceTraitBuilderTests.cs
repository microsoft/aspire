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
            .WithDisplayOrder(20).WithDisplayOrder(-10);

        Assert.Equal(
            [
                (KnownResourceTraits.Tag, "Backend"),
                (KnownResourceTraits.Tag, "API"),
                ("Owner", "Finance"),
                ("Owner", "Platform")
            ],
            resource.Resource.Annotations.OfType<TraitAnnotation>().Select(t => (t.Name, t.Value)));
    }

    [Theory]
    [InlineData("", "value")]
    [InlineData("Owner", " ")]
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
        Assert.Equal(["existing", "resource.trait.Aspire_Tag", "resource.trait.Owner"], properties.Select(p => p.Name));
        Assert.Equal(["Backend", "API"], Assert.IsType<string[]>(properties[1].Value));
        Assert.Equal(["Finance, Inc.", "Platform"], Assert.IsType<string[]>(properties[2].Value));
        Assert.Equal("Owner", properties[2].DisplayName);
    }

    [Theory]
    [InlineData("  Backend")]
    [InlineData("Back  end")]
    [InlineData(" ")]
    public void WithTagsRejectsInvalidTags(string tag)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddContainer("api", "image");

        Assert.ThrowsAny<ArgumentException>(() => resource.WithTags(tag));
        Assert.ThrowsAny<ArgumentException>(() => resource.WithTrait(KnownResourceTraits.Tag, tag));
    }

    [Fact]
    public void WithDisplayOrderReplacesOrderAndProducesProperty()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddContainer("api", "image").WithTags("Backend").WithDisplayOrder(10).WithDisplayOrder(-20);

        Assert.Equal(-20, Assert.Single(resource.Resource.Annotations.OfType<DisplayOrderAnnotation>()).Order);
        Assert.Equal([(KnownResourceTraits.Tag, "Backend")], resource.Resource.Annotations.OfType<TraitAnnotation>().Select(t => (t.Name, t.Value)));

        var properties = ResourceTraitSnapshotProperties.AddTraitProperties(resource.Resource, []);

        Assert.Equal(["resource.trait.Aspire_Tag", KnownProperties.Resource.DisplayOrder], properties.Select(p => p.Name));
        Assert.Equal("-20", properties[1].Value);
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
