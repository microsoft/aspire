// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components;
using Aspire.Dashboard.Model;
using Xunit;

namespace Aspire.Dashboard.Tests.Components;

public sealed class UrlsColumnDisplayTests
{
    [Fact]
    public void GetTooltipText_UsesUrl()
    {
        var displayedUrl = new DisplayedUrl
        {
            Index = 0,
            Name = "https",
            Text = "Api endpoint",
            Url = "https://localhost:17174",
            OriginalUrlString = "https://localhost:17174"
        };

        Assert.Equal("https://localhost:17174", UrlsColumnDisplay.GetTooltipText(displayedUrl));
    }

    [Fact]
    public void GetTooltipText_FallsBackToOriginalUrlStringWhenUrlIsNull()
    {
        var displayedUrl = new DisplayedUrl
        {
            Index = 0,
            Name = "grpc",
            Text = "Grpc endpoint",
            Url = null,
            OriginalUrlString = "grpc://localhost:17175"
        };

        Assert.Equal("grpc://localhost:17175", UrlsColumnDisplay.GetTooltipText(displayedUrl));
    }

    [Fact]
    public void GetTooltipText_DropsCredentialsAndFragmentAndMasksQueryValues()
    {
        const string address = "redis://:synthetic-secret@localhost:6379/?token=synthetic-secret#access_token=synthetic-secret";
        var displayedUrl = new DisplayedUrl
        {
            Index = 0,
            Name = "redis",
            Text = address,
            Url = null,
            OriginalUrlString = address
        };

        Assert.Equal("redis://localhost:6379/?token=●●●●●●●●", UrlsColumnDisplay.GetTooltipText(displayedUrl));
        Assert.Equal("redis://localhost:6379/?token=●●●●●●●●", UrlsColumnDisplay.GetDisplayText(displayedUrl));
    }

    [Fact]
    public void GetDisplayText_KeepsDisplayName()
    {
        var displayedUrl = new DisplayedUrl
        {
            Index = 0,
            Name = "https",
            Text = "Login page",
            DisplayName = "Login page",
            Url = "https://localhost:17174/login?token=synthetic-secret",
            OriginalUrlString = "https://localhost:17174/login?token=synthetic-secret"
        };

        Assert.Equal("Login page", UrlsColumnDisplay.GetDisplayText(displayedUrl));
        Assert.Equal("https://localhost:17174/login?token=●●●●●●●●", UrlsColumnDisplay.GetTooltipText(displayedUrl));
    }
}
