// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Controls;
using Bunit;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Controls;

public class AspireLogoTests : Bunit.TestContext
{
    [Fact]
    public void Render_UsesCurrentBrandArtwork()
    {
        var cut = RenderComponent<AspireLogo>();

        var svg = cut.Find("svg");
        var paths = cut.FindAll("path");

        Assert.Equal("0 0 256 256", svg.GetAttribute("viewBox"));
        Assert.Equal("true", svg.GetAttribute("aria-hidden"));
        Assert.Collection(
            paths,
            path => Assert.Equal("#512BD4", path.GetAttribute("fill")),
            path => Assert.Equal("#7455DD", path.GetAttribute("fill")),
            path => Assert.Equal("#9780E5", path.GetAttribute("fill")),
            path => Assert.Equal("#B9AAEE", path.GetAttribute("fill")),
            path => Assert.Equal("#DCD5F6", path.GetAttribute("fill")),
            path => Assert.Equal("#9780E5", path.GetAttribute("fill")));
    }
}
