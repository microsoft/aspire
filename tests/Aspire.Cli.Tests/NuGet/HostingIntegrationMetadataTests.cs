// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.NuGet;

namespace Aspire.Cli.Tests.NuGet;

public class HostingIntegrationMetadataTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void IsBuiltInHostingPackageId_ReturnsFalseForNullOrWhitespace(string? packageId)
    {
        Assert.False(HostingIntegrationMetadata.IsBuiltInHostingPackageId(packageId));
    }

    [Theory]
    [InlineData("Aspire.Hosting.Redis", true)]
    [InlineData("aspire.hosting.redis", true)]
    [InlineData("CommunityToolkit.Aspire.Hosting.AzureOpenAI", true)]
    [InlineData("communitytoolkit.aspire.hosting.azureopenai", true)]
    [InlineData("Aspire.Hosting.AppHost", false)]
    [InlineData("aspire.hosting.apphost", false)]
    [InlineData("Aspire.Hosting.Sdk", false)]
    [InlineData("Contoso.Hosting.MongoDb", false)]
    public void IsBuiltInHostingPackageId_MatchesKnownPackagePatterns(string packageId, bool expected)
    {
        Assert.Equal(expected, HostingIntegrationMetadata.IsBuiltInHostingPackageId(packageId));
    }

    [Theory]
    [InlineData("Aspire.StackExchange.Redis", true)]
    [InlineData("Aspire.Azure.Storage.Blobs", true)]
    [InlineData("CommunityToolkit.Aspire.Azure.DataApiBuilder", true)]
    [InlineData("Microsoft.NET.Sdk.Aspire.Manifest-8.0.100", true)]
    [InlineData("Aspire.Hosting.Redis", false)]
    [InlineData("CommunityToolkit.Aspire.Hosting.AzureOpenAI", false)]
    [InlineData("Scalar.Aspire", false)]
    [InlineData("DevProxy.Hosting", false)]
    public void IsKnownNonHostingAspirePackageId_MatchesKnownPackagePatterns(string packageId, bool expected)
    {
        Assert.Equal(expected, HostingIntegrationMetadata.IsKnownNonHostingAspirePackageId(packageId));
    }
}
