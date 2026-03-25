// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Tests.Shared.DashboardModel;
using Xunit;

namespace Aspire.Dashboard.Tests.Model;

public sealed class ResourceUrlHelpersTests
{
    public static List<DisplayedUrl> GetUrls(ResourceViewModel resource, bool includeInternalUrls = false)
    {
        return ResourceUrlHelpers.GetUrls(resource, includeInternalUrls);
    }

    [Fact]
    public void GetUrls_Empty_NoResults()
    {
        var endpoints = GetUrls(ModelTestHelpers.CreateResource(urls: []));

        Assert.Empty(endpoints);
    }

    [Fact]
    public void GetUrls_HasServices_Results()
    {
        var endpoints = GetUrls(ModelTestHelpers.CreateResource(urls: [new("Test", new("http://localhost:8080"), isInternal: false, isInactive: false, displayProperties: UrlDisplayPropertiesViewModel.Empty)]));

        Assert.Collection(endpoints,
            e =>
            {
                Assert.Equal("http://localhost:8080", e.Text);
                Assert.Equal("Test", e.Name);
                Assert.Equal("http://localhost:8080", e.Url);
                Assert.Equal("localhost", e.Address);
                Assert.Equal(8080, e.Port);
            });
    }

    [Fact]
    public void GetUrls_HasEndpointAndService_Results()
    {
        var endpoints = GetUrls(ModelTestHelpers.CreateResource(urls: [
            new("Test", new("http://localhost:8080"), isInternal: false, isInactive: false, displayProperties: UrlDisplayPropertiesViewModel.Empty),
            new("Test2", new("http://localhost:8081"), isInternal: false, isInactive: false, displayProperties: UrlDisplayPropertiesViewModel.Empty)])
        );

        Assert.Collection(endpoints,
            e =>
            {
                Assert.Equal("http://localhost:8080", e.Text);
                Assert.Equal("Test", e.Name);
                Assert.Equal("http://localhost:8080", e.Url);
                Assert.Equal("localhost", e.Address);
                Assert.Equal(8080, e.Port);
            },
            e =>
            {
                Assert.Equal("http://localhost:8081", e.Text);
                Assert.Equal("Test2", e.Name);
                Assert.Equal("http://localhost:8081", e.Url);
                Assert.Equal("localhost", e.Address);
                Assert.Equal(8081, e.Port);
            });
    }

    [Fact]
    public void GetUrls_AllEndpointsSetTheUrl()
    {
        var endpoints = GetUrls(ModelTestHelpers.CreateResource(urls: [
            new("Test", new("http://localhost:8080"), isInternal: false, isInactive: false, displayProperties: UrlDisplayPropertiesViewModel.Empty),
            new("Test2", new("tcp://localhost:8081"), isInternal: false, isInactive: false, displayProperties: UrlDisplayPropertiesViewModel.Empty)])
        );

        Assert.Collection(endpoints,
            e =>
            {
                Assert.Equal("http://localhost:8080", e.Text);
                Assert.Equal("Test", e.Name);
                Assert.Equal("http://localhost:8080", e.Url);
                Assert.Equal("localhost", e.Address);
                Assert.Equal(8080, e.Port);
            },
            e =>
            {
                Assert.Equal("tcp://localhost:8081", e.Text);
                Assert.Equal("Test2", e.Name);
                Assert.Null(e.Url);
                Assert.Equal("localhost", e.Address);
                Assert.Equal(8081, e.Port);
            });
    }

    [Fact]
    public void GetUrls_NonHttpUrlSchemesAreDisplayed()
    {
        var endpoints = GetUrls(ModelTestHelpers.CreateResource(urls: [
            new("Email", new("mailto:test@example.com"), isInternal: false, isInactive: false, displayProperties: UrlDisplayPropertiesViewModel.Empty),
            new("FTP", new("ftp://files.example.com/path"), isInternal: false, isInactive: false, displayProperties: UrlDisplayPropertiesViewModel.Empty),
            new("Custom", new("myapp://resource/123"), isInternal: false, isInactive: false, displayProperties: UrlDisplayPropertiesViewModel.Empty)])
        );

        Assert.Collection(endpoints,
            e =>
            {
                Assert.Equal("myapp://resource/123", e.Text);
                Assert.Equal("Custom", e.Name);
                Assert.Equal("myapp://resource/123", e.Url);
                Assert.Equal("resource", e.Address);
                Assert.Equal(-1, e.Port);
            },
            e =>
            {
                Assert.Equal("mailto:test@example.com", e.Text);
                Assert.Equal("Email", e.Name);
                Assert.Equal("mailto:test@example.com", e.Url);
                Assert.Equal("example.com", e.Address);
                Assert.Equal(25, e.Port);
            },
            e =>
            {
                Assert.Equal("ftp://files.example.com/path", e.Text);
                Assert.Equal("FTP", e.Name);
                Assert.Equal("ftp://files.example.com/path", e.Url);
                Assert.Equal("files.example.com", e.Address);
                Assert.Equal(21, e.Port);
            });
    }

    [Fact]
    public void GetUrls_IncludeEndpointUrl_HasEndpointAndService_Results()
    {
        var endpoints = GetUrls(ModelTestHelpers.CreateResource(urls: [
            new("First", new("https://localhost:8080/test"), isInternal:false, isInactive: false, displayProperties: UrlDisplayPropertiesViewModel.Empty),
            new("Test", new("https://localhost:8081/test2"), isInternal:false, isInactive: false, displayProperties: UrlDisplayPropertiesViewModel.Empty)
        ]));

        Assert.Collection(endpoints,
            e =>
            {
                Assert.Equal("https://localhost:8080/test", e.Text);
                Assert.Equal("First", e.Name);
                Assert.Equal("https://localhost:8080/test", e.Url);
                Assert.Equal("localhost", e.Address);
                Assert.Equal(8080, e.Port);
            },
            e =>
            {
                Assert.Equal("https://localhost:8081/test2", e.Text);
                Assert.Equal("Test", e.Name);
                Assert.Equal("https://localhost:8081/test2", e.Url);
                Assert.Equal("localhost", e.Address);
                Assert.Equal(8081, e.Port);
            });
    }

    [Fact]
    public void GetUrls_ExcludesInternalUrls()
    {
        var endpoints = GetUrls(ModelTestHelpers.CreateResource(urls: [
            new("First", new("https://localhost:8080/test"), isInternal:true, isInactive : false, displayProperties: UrlDisplayPropertiesViewModel.Empty),
            new("Test", new("https://localhost:8081/test2"), isInternal:false, isInactive : false, displayProperties: UrlDisplayPropertiesViewModel.Empty)
        ]));

        Assert.Collection(endpoints,
            e =>
            {
                Assert.Equal("https://localhost:8081/test2", e.Text);
                Assert.Equal("Test", e.Name);
                Assert.Equal("https://localhost:8081/test2", e.Url);
                Assert.Equal("localhost", e.Address);
                Assert.Equal(8081, e.Port);
            });
    }

    [Fact]
    public void GetUrls_ExcludesInactiveUrls()
    {
        var endpoints = GetUrls(ModelTestHelpers.CreateResource(urls: [
            new("First", new("https://localhost:8080/test"), isInternal: false, isInactive : true, displayProperties: UrlDisplayPropertiesViewModel.Empty),
            new("Test", new("https://localhost:8081/test2"), isInternal: false, isInactive: false, displayProperties: UrlDisplayPropertiesViewModel.Empty)
        ]));

        Assert.Collection(endpoints,
            e =>
            {
                Assert.Equal("https://localhost:8081/test2", e.Text);
                Assert.Equal("Test", e.Name);
                Assert.Equal("https://localhost:8081/test2", e.Url);
                Assert.Equal("localhost", e.Address);
                Assert.Equal(8081, e.Port);
            });
    }

    [Fact]
    public void GetUrls_IncludesIncludeInternalUrls()
    {
        var endpoints = GetUrls(ModelTestHelpers.CreateResource(urls: [
            new("First", new("https://localhost:8080/test"), isInternal:true, isInactive: false, displayProperties: UrlDisplayPropertiesViewModel.Empty),
            new("Test", new("https://localhost:8081/test2"), isInternal:false, isInactive: false, displayProperties: UrlDisplayPropertiesViewModel.Empty)
        ]),
        includeInternalUrls: true);

        Assert.Collection(endpoints,
            e =>
            {
                Assert.Equal("https://localhost:8080/test", e.Text);
                Assert.Equal("First", e.Name);
                Assert.Equal("https://localhost:8080/test", e.Url);
                Assert.Equal("localhost", e.Address);
                Assert.Equal(8080, e.Port);
            },
            e =>
            {
                Assert.Equal("https://localhost:8081/test2", e.Text);
                Assert.Equal("Test", e.Name);
                Assert.Equal("https://localhost:8081/test2", e.Url);
                Assert.Equal("localhost", e.Address);
                Assert.Equal(8081, e.Port);
            });
    }

    [Theory]
    [InlineData("redis")]
    [InlineData("rediss")]
    public void GetUrls_RedisSchemes_AreNotDisplayedAsLinks(string scheme)
    {
        var endpoints = GetUrls(ModelTestHelpers.CreateResource(urls: [
            new("Cache", new($"{scheme}://localhost:6379"), isInternal: false, isInactive: false, displayProperties: UrlDisplayPropertiesViewModel.Empty)])
        );

        Assert.Collection(endpoints,
            e =>
            {
                Assert.Equal($"{scheme}://localhost:6379", e.Text);
                Assert.Equal("Cache", e.Name);
                Assert.Null(e.Url);
                Assert.Equal("localhost", e.Address);
                Assert.Equal(6379, e.Port);
            });
    }

    [Fact]
    public void GetUrls_OrderByName()
    {
        var endpoints = GetUrls(ModelTestHelpers.CreateResource(urls: [
            new("a", new("http://localhost:8080"), isInternal: false, isInactive: false, displayProperties: UrlDisplayPropertiesViewModel.Empty),
            new("C", new("http://localhost:8080"), isInternal: false, isInactive: false, displayProperties: UrlDisplayPropertiesViewModel.Empty),
            new("D", new("tcp://localhost:8080"), isInternal: false, isInactive: false, displayProperties: UrlDisplayPropertiesViewModel.Empty),
            new("B", new("tcp://localhost:8080"), isInternal: false, isInactive: false, displayProperties: UrlDisplayPropertiesViewModel.Empty),
            new("Z", new("https://localhost:8080"), isInternal: false, isInactive: false, displayProperties: UrlDisplayPropertiesViewModel.Empty)
        ]));

        Assert.Collection(endpoints,
            e => Assert.Equal("Z", e.Name),
            e => Assert.Equal("a", e.Name),
            e => Assert.Equal("C", e.Name),
            e => Assert.Equal("B", e.Name),
            e => Assert.Equal("D", e.Name));
    }

    [Fact]
    public void GetUrls_SortOrder_Combinations()
    {
        var endpoints = GetUrls(ModelTestHelpers.CreateResource(urls: [
            new("Zero-Https", new("https://localhost:8079"), isInternal: false, isInactive: false, displayProperties: new UrlDisplayPropertiesViewModel(string.Empty, 0)),
            new("Zero-Http", new("http://localhost:8080"), isInternal: false, isInactive: false, displayProperties: new UrlDisplayPropertiesViewModel(string.Empty, 0)),
            new("Positive", new("http://localhost:8082"), isInternal: false, isInactive: false, displayProperties: new UrlDisplayPropertiesViewModel(string.Empty, 1)),
            new("Negative", new("http://localhost:8083"), isInternal: false, isInactive: false, displayProperties: new UrlDisplayPropertiesViewModel(string.Empty, -1))
        ]));

        Assert.Collection(endpoints,
            e =>
            {
                Assert.Equal("Positive", e.Name);
                Assert.Equal("http://localhost:8082", e.Url);
            },
            e =>
            {
                Assert.Equal("Zero-Https", e.Name); // tie broken by protocol (https)
                Assert.Equal("https://localhost:8079", e.Url);
            },
            e =>
            {
                Assert.Equal("Zero-Http", e.Name);
                Assert.Equal("http://localhost:8080", e.Url);
            },
            e =>
            {
                Assert.Equal("Negative", e.Name);
                Assert.Equal("http://localhost:8083", e.Url);
            });
    }
}
