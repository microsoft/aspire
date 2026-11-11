// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Tests.Shared;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Controls;

[UseCulture("en-US")]
public class ResourceRelationshipGraphTests : DashboardTestContext
{
    [Theory]
    [InlineData(true, "catalogservice-v", "catalogservice-v")]
    [InlineData(true, "catalogservice-wxarteky", "catalogs…xarteky")]
    [InlineData(false, "catalogservice-wxarteky", "catalogservice-wxarteky")]
    [InlineData(false, "catalogservice-production-wxarteky", "catalogservi…on-wxarteky")]
    public void Render_LabelsUseAvailableWidthAndKeepReplicaSuffix(bool hasOutgoing, string resourceName, string expectedLabel)
    {
        Services.AddLocalization();
        IReadOnlyList<RelationshipGraphNode> outgoing = hasOutgoing
            ? [new("catalogdb", "Reference", "/resources/catalogdb")]
            : [];
        var cut = Render<ResourceRelationshipGraph>(p => p
            .Add(p => p.Resource, new(resourceName, "Project", Url: null))
            .Add(p => p.Incoming, [new("frontend", "Reference", "/resources/frontend")])
            .Add(p => p.Outgoing, outgoing));

        var center = cut.Find(".relationship-graph-node.center");
        Assert.Equal(expectedLabel, center.QuerySelector("text")!.TextContent);
        Assert.Equal($"{resourceName} (Project)", center.GetAttribute("aria-label"));
    }
}
