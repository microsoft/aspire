// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
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
    public void ParsePackageSearchResults_WithCredentialProviderOutputAroundPayload_ParsesExpectedPayload()
    {
        // Credential-provider diagnostics use an inherited stdout handle, so braced text or JSON can arrive before
        // the package-search payload and additional output can arrive after it. The diagnostic JSON deliberately
        // contains nested package-search property names to ensure only complete root payload objects are considered.
        // The accented characters ensure the scanner handles UTF-8 byte offsets correctly.
        var pollutedStdout =
            "    [CredentialProvider]R\u00e9ponse re\u00e7ue for request {request-42}\n" +
            """{"error":{"searchResult":[{"packages":[]}]}}""" + "\n" +
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
            """ +
            "\n    [CredentialProvider]VstsCredentialProvider - Acquired bearer token using 'MSAL Silent'";

        var packages = PackageUpdateHelpers.ParsePackageSearchResults(pollutedStdout, "Aspire.Hosting.AppHost");

        var package = Assert.Single(packages);
        Assert.Equal("Aspire.Hosting.AppHost", package.Id);
        Assert.Equal("9.3.1", package.Version);
        Assert.Equal("azure-default", package.Source);
    }

    [Fact]
    public void ParsePackageSearchResults_EmptyPayloadWithOutputAroundIt_ReturnsEmpty()
    {
        var pollutedStdout =
            "    [CredentialProvider]Acquiring token\n" +
            """{"version":2,"problems":[],"searchResult":[]}""" +
            "\n    [CredentialProvider]Token acquired";

        var packages = PackageUpdateHelpers.ParsePackageSearchResults(pollutedStdout);

        Assert.Empty(packages);
    }

    [Theory]
    [InlineData("""{"searchResult":[{"sourceName":"feed","packages":[]}]}""")]
    [InlineData("""{"version":2,"searchResult":[{"packages":[]}]}""")]
    [InlineData("""{"version":2,"searchResult":[{"sourceName":"feed","packages":{}}]}""")]
    public void ParsePackageSearchResults_RootNearMatchBeforePayload_ParsesExpectedPayload(string nearMatch)
    {
        var pollutedStdout =
            nearMatch + "\n" +
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

        var packages = PackageUpdateHelpers.ParsePackageSearchResults(pollutedStdout);

        var package = Assert.Single(packages);
        Assert.Equal("Aspire.Hosting.AppHost", package.Id);
        Assert.Equal("9.3.1", package.Version);
        Assert.Equal("azure-default", package.Source);
    }

    [Fact]
    public void ParsePackageSearchResults_WithoutExpectedPayload_PreservesParseFailure()
    {
        var pollutedStdout = "    [CredentialProvider]Acquiring token for request {request-42}";

        Assert.ThrowsAny<JsonException>(() => PackageUpdateHelpers.ParsePackageSearchResults(pollutedStdout));
    }
}
