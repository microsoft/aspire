// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Orchestrator;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Tests.Orchestrator;

[Trait("Partition", "3")]
public class RelationshipEvaluatorTests
{
    [Fact]
    public void HandlesNestedChildren()
    {
        var builder = DistributedApplication.CreateBuilder();

        var parentResource = builder.AddContainer("parent", "image");
        var childResource = builder.AddResource(new CustomChildResource("child", parentResource.Resource));
        var grandChildResource = builder.AddResource(new CustomChildResource("grandchild", childResource.Resource));
        var greatGrandChildResource = builder.AddResource(new CustomChildResource("greatgrandchild", grandChildResource.Resource));

        var childWithAnnotationsResource = builder.AddContainer("child-with-annotations", "image")
            .WithParentRelationship(parentResource);

        var grandChildWithAnnotationsResource = builder.AddContainer("grandchild-with-annotations", "image")
            .WithParentRelationship(childWithAnnotationsResource);

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var parentChildLookup = RelationshipEvaluator.GetParentChildLookup(appModel);
        Assert.Equal(4, parentChildLookup.Count);

        Assert.Collection(parentChildLookup[parentResource.Resource],
            x => Assert.Equal(childResource.Resource, x),
            x => Assert.Equal(childWithAnnotationsResource.Resource, x));

        Assert.Single(parentChildLookup[childResource.Resource], grandChildResource.Resource);
        Assert.Single(parentChildLookup[grandChildResource.Resource], greatGrandChildResource.Resource);

        Assert.Empty(parentChildLookup[greatGrandChildResource.Resource]);

        Assert.Single(parentChildLookup[childWithAnnotationsResource.Resource], grandChildWithAnnotationsResource.Resource);

        Assert.Empty(parentChildLookup[grandChildWithAnnotationsResource.Resource]);
    }

    [Fact]
    public void WithChildRelationshipUsingResourceBuilderCreatesCorrectParentChildLookup()
    {
        var builder = DistributedApplication.CreateBuilder();

        var parentResource = builder.AddContainer("parent", "image");
        var child1Resource = builder.AddContainer("child1", "image");
        var child2Resource = builder.AddContainer("child2", "image");

        parentResource.WithChildRelationship(child1Resource)
                     .WithChildRelationship(child2Resource);

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var parentChildLookup = RelationshipEvaluator.GetParentChildLookup(appModel);
        Assert.Equal(1, parentChildLookup.Count);

        Assert.Collection(parentChildLookup[parentResource.Resource],
            x => Assert.Equal(child1Resource.Resource, x),
            x => Assert.Equal(child2Resource.Resource, x));
    }

    [Fact]
    public void WithChildRelationshipUsingResourceCreatesCorrectParentChildLookup()
    {
        var builder = DistributedApplication.CreateBuilder();

        var parentResource = builder.AddContainer("parent", "image");
        var child1Resource = builder.AddContainer("child1", "image");
        var child2Resource = builder.AddContainer("child2", "image");

        parentResource.WithChildRelationship(child1Resource.Resource)
                     .WithChildRelationship(child2Resource.Resource);

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var parentChildLookup = RelationshipEvaluator.GetParentChildLookup(appModel);
        Assert.Equal(1, parentChildLookup.Count);

        Assert.Collection(parentChildLookup[parentResource.Resource],
            x => Assert.Equal(child1Resource.Resource, x),
            x => Assert.Equal(child2Resource.Resource, x));
    }

    [Fact]
    public void WithChildRelationshipAndWithParentRelationshipWorkTogether()
    {
        var builder = DistributedApplication.CreateBuilder();

        var parentResource = builder.AddContainer("parent", "image");
        var child1Resource = builder.AddContainer("child1", "image");
        var child2Resource = builder.AddContainer("child2", "image")
            .WithParentRelationship(parentResource);

        parentResource.WithChildRelationship(child1Resource);

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var parentChildLookup = RelationshipEvaluator.GetParentChildLookup(appModel);
        Assert.Equal(1, parentChildLookup.Count);

        Assert.Collection(parentChildLookup[parentResource.Resource],
            x => Assert.Equal(child1Resource.Resource, x),
            x => Assert.Equal(child2Resource.Resource, x));
    }

    [Fact]
    public void WithChildRelationshipHandlesNestedRelationships()
    {
        var builder = DistributedApplication.CreateBuilder();

        var grandParentResource = builder.AddContainer("grandparent", "image");
        var parentResource = builder.AddContainer("parent", "image");
        var childResource = builder.AddContainer("child", "image");

        grandParentResource.WithChildRelationship(parentResource);
        parentResource.WithChildRelationship(childResource);

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var parentChildLookup = RelationshipEvaluator.GetParentChildLookup(appModel);
        Assert.Equal(2, parentChildLookup.Count);

        Assert.Single(parentChildLookup[grandParentResource.Resource], parentResource.Resource);
        Assert.Single(parentChildLookup[parentResource.Resource], childResource.Resource);
    }

    private sealed class CustomChildResource(string name, IResource parent) : Resource(name), IResourceWithParent
    {
        public IResource Parent => parent;
    }
}
