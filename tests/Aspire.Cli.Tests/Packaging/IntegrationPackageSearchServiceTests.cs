// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Packaging;
using Aspire.Cli.Tests.TestServices;
using NuGetPackage = Aspire.Shared.NuGetPackageCli;

namespace Aspire.Cli.Tests.Packaging;

public class IntegrationPackageSearchServiceTests
{
    [Theory]
    [InlineData("CommunityToolkit.Aspire.Hosting.Redis")]
    [InlineData("Acme.Aspire.Hosting.Redis")]
    public void GetIntegrationSearchMatchesPrioritizesOfficialPackageIdsOverRelevance(string thirdPartyPackageId)
    {
        var matches = GetMatches("redis",
            thirdPartyPackageId,
            "aspire.hosting.Rdis",
            "Aspire.Hosting.CommunityToolkit.Redis",
            "Aspire.Hosting.Redis");

        Assert.Equal(
            ["Aspire.Hosting.Redis", "Aspire.Hosting.CommunityToolkit.Redis", "aspire.hosting.Rdis", thirdPartyPackageId],
            matches.Select(p => p.Package.Id));
        Assert.Equal(0.6, matches[2].SearchScore, precision: 2);
        Assert.Equal(0.85, matches[3].SearchScore, precision: 2);
    }

    [Fact]
    public void GetIntegrationSearchMatchesOrdersEqualScoresAlphabeticallyWithinEachGroup()
    {
        var matches = GetMatches("sql",
            "CommunityToolkit.Aspire.Hosting.PostgreSQL",
            "Aspire.Hosting.PostgreSQL",
            "Acme.Aspire.Hosting.MySql",
            "Aspire.Hosting.Azure.MySql");

        Assert.Equal(
            ["Aspire.Hosting.Azure.MySql", "Aspire.Hosting.PostgreSQL", "Acme.Aspire.Hosting.MySql", "CommunityToolkit.Aspire.Hosting.PostgreSQL"],
            matches.Select(p => p.Package.Id));
        Assert.All(matches, p => Assert.Equal(0.85, p.SearchScore, precision: 2));
    }

    private static (string FriendlyName, NuGetPackage Package, PackageChannel Channel, double SearchScore)[] GetMatches(string searchTerm, params string[] packageIds)
    {
        var channel = new TestPackagingService().GetImplicitChannel();
        var packages = packageIds
            .Select(id => (Package: new NuGetPackage { Id = id, Version = "1.0.0", Source = "test" }, Channel: channel))
            .Select(IntegrationPackageSearchService.GenerateFriendlyName);

        return IntegrationPackageSearchService.GetIntegrationSearchMatches(packages, searchTerm).ToArray();
    }
}
