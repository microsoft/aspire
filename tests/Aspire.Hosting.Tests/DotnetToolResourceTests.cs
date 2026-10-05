// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREDOTNETTOOL // Type is for evaluation purposes only and is subject to change or removal in future updates.
#pragma warning disable ASPIRECOMMAND001

using Aspire.Dashboard.Model;
using Aspire.Hosting.Resources;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

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

    [Theory]
    [InlineData("9.0.100", false)]
    [InlineData("10.0.100-preview.1", true)]
    [InlineData(null, true)]
    public async Task ValidateDotnetSdkVersionUsesSharedVersionProvider(string? version, bool expectedValid)
    {
        using var services = new ServiceCollection()
            .AddSingleton<IDotnetSdkVersionProvider>(new TestDotnetSdkVersionProvider(version))
            .BuildServiceProvider();
        var context = new RequiredCommandValidationContext(
            "dotnet",
            services,
            TestContext.Current.CancellationToken);

        var result = await DotnetToolResourceExtensions.ValidateDotnetSdkVersionAsync(
            context,
            Environment.CurrentDirectory);

        Assert.Equal(expectedValid, result.IsValid);
    }

    private static void AssertToolProperty(ResourcePropertySnapshot property, string expectedValue, string expectedDisplayName, int expectedSortOrder)
    {
        Assert.Equal(expectedValue, property.Value);
        Assert.Equal(expectedDisplayName, property.DisplayName);
        Assert.True(property.IsHighlighted);
        Assert.Equal(expectedSortOrder, property.SortOrder);
    }
}
