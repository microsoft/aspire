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
    [InlineData(true, false, "catalogservice-v", "catalogservice-v")]
    [InlineData(true, false, "catalogservice-wxarteky", "catalogservice-…")]
    [InlineData(true, true, "catalogservice-v", "catalogservice-v")]
    [InlineData(true, true, "catalogservice-wxarteky", "catalogs…xarteky")]
    [InlineData(false, false, "catalogservice-wxarteky", "catalogservice-wxarteky")]
    [InlineData(false, false, "catalogservice-production-wxarteky", "catalogservice-producti…")]
    [InlineData(false, true, "catalogservice-wxarteky", "catalogservice-wxarteky")]
    [InlineData(false, true, "catalogservice-production-wxarteky", "catalogservi…on-wxarteky")]
    public void Render_LabelsUseAvailableWidthAndOnlyKeepReplicaSuffix(bool hasOutgoing, bool isReplica, string resourceName, string expectedLabel)
    {
        Services.AddLocalization();
        IReadOnlyList<RelationshipGraphNode> outgoing = hasOutgoing
            ? [new("catalogdb", "Reference", "/resources/catalogdb", IsReplica: false)]
            : [];
        var cut = Render<ResourceRelationshipGraph>(p => p
            .Add(p => p.Resource, new(resourceName, "Project", Url: null, IsReplica: isReplica))
            .Add(p => p.Incoming, [new("frontend", "Reference", "/resources/frontend", IsReplica: false)])
            .Add(p => p.Outgoing, outgoing));

        var center = cut.Find(".relationship-graph-node.center");
        Assert.Equal(expectedLabel, center.QuerySelector("text")!.TextContent);
        Assert.Equal($"{resourceName} (Project)", center.GetAttribute("aria-label"));
        Assert.Equal($"{resourceName} (Project)", center.GetAttribute("data-tooltip"));
    }
}
