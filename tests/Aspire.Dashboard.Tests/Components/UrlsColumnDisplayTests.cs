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
}
