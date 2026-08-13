// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Semver;

namespace Aspire.Hosting.Tests.VersionChecking;

[Trait("Partition", "4")]
public class PackageUpdateHelpersTests
{
    [Fact]
    public void GetLatestVersion_MultipleVersions_LatestVersion()
    {
        // Arrange
        var json = """
            {
              "version": 2,
              "problems": [],
              "searchResult": [
                {
                  "sourceName": "feed1",
                  "packages": [
                    {
                      "id": "Aspire.Hosting.AppHost",
                      "latestVersion": "0.4.1"
                    }
                  ]
                },
                {
                  "sourceName": "feed2",
                  "packages": [
                    {
                      "id": "Aspire.Hosting.AppHost",
                      "latestVersion": "19.0.0"
                    }
                  ]
                },
                {
                  "sourceName": "feed3",
                  "packages": [
                    {
                      "id": "Aspire.Hosting.AppHost",
                      "latestVersion": "9.3.1"
                    }
                  ]
                }
              ]
            }
            """;

        // Act
        var packages = PackageUpdateHelpers.ParsePackageSearchResults(json, "Aspire.Hosting.AppHost");
        var latestVersion = PackageUpdateHelpers.GetNewerVersion(NullLogger.Instance, new SemVersion(1), packages);

        // Assert
        Assert.Equal(new SemVersion(19, 0, 0), latestVersion);
    }

    [Fact]
    public void GetLatestVersion_HasPrerelease_IgnorePrerelease()
    {
        // Arrange
        var json = """
            {
              "version": 2,
              "problems": [],
              "searchResult": [
                {
                  "sourceName": "feed1",
                  "packages": [
                    {
                      "id": "Aspire.Hosting.AppHost",
                      "latestVersion": "0.4.1"
                    }
                  ]
                },
                {
                  "sourceName": "feed2",
                  "packages": [
                    {
                      "id": "Aspire.Hosting.AppHost",
                      "latestVersion": "19.0.0-pre1"
                    }
                  ]
                },
                {
                  "sourceName": "feed3",
                  "packages": [
                    {
                      "id": "Aspire.Hosting.AppHost",
                      "latestVersion": "9.3.1"
                    }
                  ]
                }
              ]
            }
            """;

        // Act
        var packages = PackageUpdateHelpers.ParsePackageSearchResults(json, "Aspire.Hosting.AppHost");
        var latestVersion = PackageUpdateHelpers.GetNewerVersion(NullLogger.Instance, new SemVersion(1), packages);

        // Assert
        Assert.Equal(new SemVersion(9, 3, 1), latestVersion);
    }

    [Fact]
    public void GetLatestVersion_HasPrerelease_UsePrerelease()
    {
        // Arrange
        var json = """
            {
              "version": 2,
              "problems": [],
              "searchResult": [
                {
                  "sourceName": "feed1",
                  "packages": [
                    {
                      "id": "Aspire.Hosting.AppHost",
                      "latestVersion": "0.4.1"
                    }
                  ]
                },
                {
                  "sourceName": "feed2",
                  "packages": [
                    {
                      "id": "Aspire.Hosting.AppHost",
                      "latestVersion": "19.0.0-pre1"
                    }
                  ]
                },
                {
                  "sourceName": "feed3",
                  "packages": [
                    {
                      "id": "Aspire.Hosting.AppHost",
                      "latestVersion": "9.3.1"
                    }
                  ]
                }
              ]
            }
            """;

        // Act
        var packages = PackageUpdateHelpers.ParsePackageSearchResults(json, "Aspire.Hosting.AppHost");
        var latestVersion = PackageUpdateHelpers.GetNewerVersion(NullLogger.Instance, new SemVersion(10, prerelease: ["dev"]), packages);

        // Assert
        Assert.Equal(new SemVersion(19, 0, 0, prerelease: ["pre1"]), latestVersion);
    }

    [Fact]
    public void GetLatestVersion_NoVersions_NoVersion()
    {
        // Arrange
        var json = "{}";

        // Act
        var packages = PackageUpdateHelpers.ParsePackageSearchResults(json, "Aspire.Hosting.AppHost");
        var latestVersion = PackageUpdateHelpers.GetNewerVersion(NullLogger.Instance, new SemVersion(1), packages);

        // Assert
        Assert.Null(latestVersion);
    }

    [Fact]
    public void GetLatestVersion_MixedPackageIds_OnlyConsidersAppHostPackages()
    {
        // Arrange
        var json = """
            {
              "version": 2,
              "problems": [],
              "searchResult": [
                {
                  "sourceName": "feed1",
                  "packages": [
                    {
                      "id": "Aspire.Hosting.AppHost",
                      "latestVersion": "8.0.1"
                    }
                  ]
                },
                {
                  "sourceName": "feed2",
                  "packages": [
                    {
                      "id": "SomeOther.Package",
                      "latestVersion": "99.0.0"
                    }
                  ]
                },
                {
                  "sourceName": "feed3",
                  "packages": [
                    {
                      "id": "Aspire.Hosting.AppHost",
                      "latestVersion": "9.0.0"
                    }
                  ]
                }
              ]
            }
            """;

        // Act
        var packages = PackageUpdateHelpers.ParsePackageSearchResults(json, "Aspire.Hosting.AppHost");
        var latestVersion = PackageUpdateHelpers.GetNewerVersion(NullLogger.Instance, new SemVersion(1), packages);

        // Assert
        Assert.Equal(new SemVersion(9, 0, 0), latestVersion);
    }

    [Fact]
    public void ParsePackageSearchResults_WithCredentialProviderPreamble_ParsesPackages()
    {
        // The NuGet Azure Artifacts Credential Provider writes "[CredentialProvider]..." progress lines to stdout
        // before the JSON payload while `dotnet package search --format json` still exits 0, which previously broke
        // parsing. See https://github.com/microsoft/aspire/issues/19339.
        var pollutedStdout =
            "    [CredentialProvider]VstsCredentialProvider - Acquired bearer token using 'MSAL Silent'\n" +
            "    [CredentialProvider]Requested 8/13/2026 2:36:13 AM but received 8/12/2026 11:37:51 PM\n" +
            """
            {
              "version": 2,
              "problems": [],
              "searchResult": [
                {
                  "sourceName": "azure-default",
                  "packages": [
                    { "id": "Aspire.Hosting.AppHost", "latestVersion": "9.3.1" }
                  ]
                }
              ]
            }
            """;

        var packages = PackageUpdateHelpers.ParsePackageSearchResults(pollutedStdout, "Aspire.Hosting.AppHost");

        var package = Assert.Single(packages);
        Assert.Equal("Aspire.Hosting.AppHost", package.Id);
        Assert.Equal("9.3.1", package.Version);
        Assert.Equal("azure-default", package.Source);
    }
}
