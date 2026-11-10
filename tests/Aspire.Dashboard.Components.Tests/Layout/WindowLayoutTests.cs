// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Configuration;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Layout;

public class WindowLayoutTests : DashboardTestContext
{
    public WindowLayoutTests()
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);
    }

    [Theory]
    [InlineData(FrontendAuthMode.BrowserToken)]
    [InlineData(FrontendAuthMode.OpenIdConnect)]
    public void Initialize_SecuredModeMissingAuthenticationState_Throws(FrontendAuthMode authMode)
    {
        Services.GetRequiredService<IOptions<DashboardOptions>>().Value.Frontend.AuthMode = authMode;

        var exception = Assert.Throws<InvalidOperationException>(() => Render<WindowLayout>());

        Assert.Equal("Dashboard authentication is required, but the current user is not authenticated.", exception.Message);
    }

    [Theory]
    [InlineData(FrontendAuthMode.BrowserToken, true)]
    [InlineData(FrontendAuthMode.OpenIdConnect, true)]
    [InlineData(FrontendAuthMode.Unsecured, false)]
    [InlineData(null, false)]
    public void Initialize_AllowedAuthenticationState_RendersBodyAndTooltipProvider(FrontendAuthMode? authMode, bool authenticated)
    {
        var authorizationContext = this.AddAuthorization();
        if (authenticated)
        {
            authorizationContext.SetAuthorized("test-user");
        }
        else
        {
            authorizationContext.SetNotAuthorized();
        }
        Services.GetRequiredService<IOptions<DashboardOptions>>().Value.Frontend.AuthMode = authMode;

        var cut = Render<WindowLayout>(builder => builder
            .Add(p => p.Body, bodyBuilder => bodyBuilder.AddMarkupContent(0, "<div id=\"window-content\">Window content</div>")));

        Assert.Equal("Window content", cut.Find("#window-content").TextContent);
        Assert.Single(cut.FindComponents<FluentTooltipProvider>());
    }
}
