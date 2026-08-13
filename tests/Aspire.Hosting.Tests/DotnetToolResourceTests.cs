// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREDOTNETTOOL // Type is for evaluation purposes only and is subject to change or removal in future updates.

using Aspire.Dashboard.Model;
using Aspire.Hosting.Resources;

namespace Aspire.Hosting.Tests;

public class DotnetToolResourceTests
{
    [Fact]
    public void CreateSnapshotPropertiesAddsDisplayMetadataForToolProperties()
    {
        var resource = new DotnetToolResource("tool", "dotnet-dump");
        resource.ToolConfiguration!.Version = "1.2.3";

        var properties = resource.CreateSnapshotProperties().ToDictionary(p => p.Name);

        AssertToolProperty(properties[KnownProperties.Tool.Package], "dotnet-dump", MessageStrings.ResourcePropertyToolPackageDisplayName, expectedSortOrder: 0);
        AssertToolProperty(properties[KnownProperties.Tool.Version], "1.2.3", MessageStrings.ResourcePropertyToolVersionDisplayName, expectedSortOrder: 1);

        var sourceProperty = properties[KnownProperties.Resource.Source];
        Assert.Equal("dotnet-dump", sourceProperty.Value);
        Assert.Null(sourceProperty.DisplayName);
        Assert.False(sourceProperty.IsHighlighted);
        Assert.Null(sourceProperty.SortOrder);
    }

    private static void AssertToolProperty(ResourcePropertySnapshot property, string expectedValue, string expectedDisplayName, int expectedSortOrder)
    {
        Assert.Equal(expectedValue, property.Value);
        Assert.Equal(expectedDisplayName, property.DisplayName);
        Assert.True(property.IsHighlighted);
        Assert.Equal(expectedSortOrder, property.SortOrder);
    }
}
