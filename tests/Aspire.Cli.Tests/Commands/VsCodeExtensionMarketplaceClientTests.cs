// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Text;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils.EnvironmentChecker;
using Semver;

namespace Aspire.Cli.Tests.Commands;

public class VsCodeExtensionMarketplaceClientTests
{
    [Fact]
    public async Task GetLatestVersionsAsync_CreatesClientForEachRequest()
    {
        var requestCount = 0;
        using var handler = new MockHttpMessageHandler(_ =>
        {
            Interlocked.Increment(ref requestCount);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{ "results": [] }""", Encoding.UTF8, "application/json")
            };
        });
        var factory = new MockHttpClientFactory(handler);
        var client = new VsCodeExtensionMarketplaceClient(factory);

        await client.GetLatestVersionsAsync(TestContext.Current.CancellationToken);
        await client.GetLatestVersionsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, factory.CreatedClientNames.Count);
        Assert.All(factory.CreatedClientNames, name => Assert.Equal("VsCodeExtensionMarketplace", name));
        Assert.Equal(2, requestCount);
    }

    [Fact]
    public async Task GetLatestVersionsAsync_ReturnsLatestVersionForEachChannel()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {
                  "results": [{
                    "extensions": [{
                      "versions": [
                        { "version": "1.3.0" },
                        {
                          "version": "1.4.0",
                          "properties": [{
                            "key": "Microsoft.VisualStudio.Code.PreRelease",
                            "value": "true"
                          }]
                        }
                      ]
                    }]
                  }]
                }
                """,
                Encoding.UTF8,
                "application/json")
        };
        using var handler = new MockHttpMessageHandler(response);
        var client = new VsCodeExtensionMarketplaceClient(new MockHttpClientFactory(handler));

        var versions = await client.GetLatestVersionsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            new VsCodeExtensionMarketplaceVersions(
                SemVersion.Parse("1.3.0", SemVersionStyles.Strict),
                SemVersion.Parse("1.4.0", SemVersionStyles.Strict)),
            versions);
    }

    [Fact]
    public async Task GetLatestVersionsAsync_IgnoresMalformedUnrelatedVersionProperty()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {
                  "results": [{
                    "extensions": [{
                      "versions": [{
                        "version": "1.4.0",
                        "properties": [
                          { "key": "Unrelated.Property" },
                          {
                            "key": "Microsoft.VisualStudio.Code.PreRelease",
                            "value": "true"
                          }
                        ]
                      }]
                    }]
                  }]
                }
                """,
                Encoding.UTF8,
                "application/json")
        };
        using var handler = new MockHttpMessageHandler(response);
        var client = new VsCodeExtensionMarketplaceClient(new MockHttpClientFactory(handler));

        var versions = await client.GetLatestVersionsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            new VsCodeExtensionMarketplaceVersions(
                null,
                SemVersion.Parse("1.4.0", SemVersionStyles.Strict)),
            versions);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("""{ "value": "true" }""")]
    [InlineData("""{ "key": 42, "value": "true" }""")]
    public async Task GetLatestVersionsAsync_IgnoresUnmatchableMalformedVersionProperties(string unrelatedProperty)
    {
        var responseJson = $$"""
            {
              "results": [{
                "extensions": [{
                  "versions": [{
                    "version": "1.4.0",
                    "properties": [
                      {{unrelatedProperty}},
                      {
                        "key": "Microsoft.VisualStudio.Code.PreRelease",
                        "value": "true"
                      }
                    ]
                  }]
                }]
              }]
            }
            """;
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
        };
        using var handler = new MockHttpMessageHandler(response);
        var client = new VsCodeExtensionMarketplaceClient(new MockHttpClientFactory(handler));

        var versions = await client.GetLatestVersionsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            new VsCodeExtensionMarketplaceVersions(
                null,
                SemVersion.Parse("1.4.0", SemVersionStyles.Strict)),
            versions);
    }

    [Fact]
    public async Task GetLatestVersionsAsync_ClassifiesVersionWithOnlyUnrelatedPropertiesAsStable()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {
                  "results": [{
                    "extensions": [{
                      "versions": [{
                        "version": "1.3.0",
                        "properties": [{
                          "key": "Unrelated.Property",
                          "value": "value"
                        }]
                      }]
                    }]
                  }]
                }
                """,
                Encoding.UTF8,
                "application/json")
        };
        using var handler = new MockHttpMessageHandler(response);
        var client = new VsCodeExtensionMarketplaceClient(new MockHttpClientFactory(handler));

        var versions = await client.GetLatestVersionsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            new VsCodeExtensionMarketplaceVersions(
                SemVersion.Parse("1.3.0", SemVersionStyles.Strict),
                null),
            versions);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""[{ "key": "Microsoft.VisualStudio.Code.PreRelease" }]""")]
    [InlineData(
        """
        [
          { "key": "Microsoft.VisualStudio.Code.PreRelease", "value": "false" },
          { "key": "microsoft.visualstudio.code.prerelease", "value": "true" }
        ]
        """)]
    public async Task GetLatestVersionsAsync_IgnoresVersion_WhenPreReleasePropertiesAreMalformedOrAmbiguous(
        string properties)
    {
        var responseJson = $$"""
            {
              "results": [{
                "extensions": [{
                  "versions": [{
                    "version": "1.4.0",
                    "properties": {{properties}}
                  }]
                }]
              }]
            }
            """;
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
        };
        using var handler = new MockHttpMessageHandler(response);
        var client = new VsCodeExtensionMarketplaceClient(new MockHttpClientFactory(handler));

        var versions = await client.GetLatestVersionsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new VsCodeExtensionMarketplaceVersions(null, null), versions);
    }

    [Fact]
    public async Task GetLatestVersionsAsync_SendsTheAnonymousMarketplaceQuery()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{ "results": [] }""", Encoding.UTF8, "application/json")
        };
        using var handler = new MockHttpMessageHandler(
            response,
            request =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal(
                    "https://marketplace.visualstudio.com/_apis/public/gallery/extensionquery",
                    request.RequestUri?.AbsoluteUri);
                Assert.Equal("3.0-preview.1", request.Headers.Accept.Single().Parameters.Single().Value);
            });
        var client = new VsCodeExtensionMarketplaceClient(new MockHttpClientFactory(handler));

        await client.GetLatestVersionsAsync(TestContext.Current.CancellationToken);

        Assert.True(handler.RequestValidated);
    }
}
