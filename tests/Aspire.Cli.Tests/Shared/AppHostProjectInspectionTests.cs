// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Shared;

namespace Aspire.Cli.Tests.Shared;

public sealed class AppHostProjectInspectionTests
{
    [Fact]
    public void FindPackageVersion_ReturnsNull_WhenItemsAreNull()
    {
        var version = AppHostProjectInspection.FindPackageVersion(null, "Aspire.Hosting");

        Assert.Null(version);
    }

    [Fact]
    public void FindPackageVersion_ReturnsNull_WhenNoListContainsAMatch()
    {
        var items = new AppHostProjectInspectionItems
        {
            PackageReference = [new() { Identity = "Some.Other.Package", Version = "1.0.0" }],
        };

        var version = AppHostProjectInspection.FindPackageVersion(items, "Aspire.Hosting", "Aspire.Hosting.AppHost");

        Assert.Null(version);
    }

    [Fact]
    public void FindPackageVersion_FindsVersionFromPackageReference()
    {
        var items = new AppHostProjectInspectionItems
        {
            PackageReference = [new() { Identity = "Aspire.Hosting.AppHost", Version = "9.0.0" }],
        };

        var version = AppHostProjectInspection.FindPackageVersion(items, "Aspire.Hosting", "Aspire.Hosting.AppHost");

        Assert.Equal("9.0.0", version);
    }

    [Fact]
    public void FindPackageVersion_PrefersPackageReference_OverAspireProjectOrPackageReferenceAndPackageVersion()
    {
        // List precedence is the outer loop: a direct PackageReference wins even when other lists
        // also declare the package, and even when an earlier-preferred package id only appears in a
        // lower-precedence list.
        var items = new AppHostProjectInspectionItems
        {
            PackageReference = [new() { Identity = "Aspire.Hosting.AppHost", Version = "9.0.0" }],
            AspireProjectOrPackageReference = [new() { Identity = "Aspire.Hosting", Version = "8.0.0" }],
            PackageVersion = [new() { Identity = "Aspire.Hosting", Version = "7.0.0" }],
        };

        var version = AppHostProjectInspection.FindPackageVersion(items, "Aspire.Hosting", "Aspire.Hosting.AppHost");

        Assert.Equal("9.0.0", version);
    }

    [Fact]
    public void FindPackageVersion_PrefersAspireProjectOrPackageReference_OverPackageVersion()
    {
        var items = new AppHostProjectInspectionItems
        {
            AspireProjectOrPackageReference = [new() { Identity = "Aspire.Hosting.AppHost", Version = "8.0.0" }],
            PackageVersion = [new() { Identity = "Aspire.Hosting.AppHost", Version = "7.0.0" }],
        };

        var version = AppHostProjectInspection.FindPackageVersion(items, "Aspire.Hosting", "Aspire.Hosting.AppHost");

        Assert.Equal("8.0.0", version);
    }

    [Fact]
    public void FindPackageVersion_PrefersEarlierPackageId_WithinTheSameList()
    {
        // Within a single list the package ids are tried in the order supplied, so the caller's
        // preference (Aspire.Hosting before Aspire.Hosting.AppHost) decides the winner.
        var items = new AppHostProjectInspectionItems
        {
            PackageReference =
            [
                new() { Identity = "Aspire.Hosting.AppHost", Version = "9.0.0" },
                new() { Identity = "Aspire.Hosting", Version = "8.0.0" },
            ],
        };

        var version = AppHostProjectInspection.FindPackageVersion(items, "Aspire.Hosting", "Aspire.Hosting.AppHost");

        Assert.Equal("8.0.0", version);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void FindPackageVersion_SkipsMatchingItemWithEmptyVersion_AndContinuesToNextList(string? emptyVersion)
    {
        // Central Package Management splits the declaration: the PackageReference carries no version
        // and the version lives in a PackageVersion entry. The matching-but-empty PackageReference
        // must be skipped so the search falls through to PackageVersion rather than returning empty.
        var items = new AppHostProjectInspectionItems
        {
            PackageReference = [new() { Identity = "Aspire.Hosting.AppHost", Version = emptyVersion }],
            PackageVersion = [new() { Identity = "Aspire.Hosting.AppHost", Version = "9.1.0" }],
        };

        var version = AppHostProjectInspection.FindPackageVersion(items, "Aspire.Hosting.AppHost");

        Assert.Equal("9.1.0", version);
    }

    [Fact]
    public void FindPackageVersion_SkipsMatchingItemWithEmptyVersion_AndContinuesToNextPackageId()
    {
        // The empty-version skip must also continue across package ids within the same list, not just
        // across lists.
        var items = new AppHostProjectInspectionItems
        {
            PackageReference =
            [
                new() { Identity = "Aspire.Hosting", Version = "" },
                new() { Identity = "Aspire.Hosting.AppHost", Version = "9.0.0" },
            ],
        };

        var version = AppHostProjectInspection.FindPackageVersion(items, "Aspire.Hosting", "Aspire.Hosting.AppHost");

        Assert.Equal("9.0.0", version);
    }
}
