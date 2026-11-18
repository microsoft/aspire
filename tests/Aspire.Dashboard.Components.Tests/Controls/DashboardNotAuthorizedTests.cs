// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Controls;

[UseCulture("en-US")]
public class DashboardNotAuthorizedTests : DashboardTestContext
{
    public DashboardNotAuthorizedTests()
    {
        Services.AddLocalization();
    }

    [Theory]
    [InlineData("http://localhost/", "owned-missing?value=1", "/login", "/owned-missing?value=1")]
    [InlineData("http://localhost/aspire/nested/", "owned-missing?value=1", "/aspire/nested/login", "/aspire/nested/owned-missing?value=1")]
    [InlineData("http://localhost/aspire/nested/", "owned%20missing?value=a%2Fb&other=x%26y#details", "/aspire/nested/login", "/aspire/nested/owned%20missing?value=a%2Fb&other=x%26y#details")]
    public void Render_BrowserToken_RedirectsToLogin(string baseUri, string missingPath, string expectedLoginPath, string expectedReturnUrl)
    {
        Services.AddSingleton<IOptions<DashboardOptions>>(Options.Create(CreateDashboardOptions(FrontendAuthMode.BrowserToken)));
        var navigationManager = new TestNavigationManager(baseUri);
        Services.AddSingleton<NavigationManager>(navigationManager);
        navigationManager.NavigateTo(missingPath);

        var cut = Render<DashboardNotAuthorized>();

        Assert.Equal(string.Empty, cut.Markup);
        var redirectedUri = navigationManager.ToAbsoluteUri(navigationManager.Uri);
        Assert.Equal(new Uri(baseUri).Authority, redirectedUri.Authority);
        Assert.Equal(expectedLoginPath, redirectedUri.AbsolutePath);
        var returnUrl = QueryHelpers.ParseQuery(redirectedUri.Query)["returnUrl"].ToString();
        Assert.Equal(expectedReturnUrl, returnUrl);
        Assert.True(UrlValidationHelper.IsSafeRedirectUrl(returnUrl));
        Assert.True(navigationManager.LastNavigationOptions is { ReplaceHistoryEntry: true });
    }

    [Theory]
    [InlineData(FrontendAuthMode.OpenIdConnect)]
    [InlineData(FrontendAuthMode.Unsecured)]
    [InlineData(null)]
    public void Render_OtherAuthMode_ShowsNotAuthorizedWithoutRedirect(FrontendAuthMode? authMode)
    {
        Services.AddSingleton<IOptions<DashboardOptions>>(Options.Create(CreateDashboardOptions(authMode)));
        var navigationManager = Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo("/owned-missing");

        var cut = Render<DashboardNotAuthorized>();

        Assert.Equal("http://localhost/owned-missing", navigationManager.Uri);
        Assert.Equal("Not authorized", cut.Markup);
    }

    private static DashboardOptions CreateDashboardOptions(FrontendAuthMode? authMode)
    {
        var options = new DashboardOptions();
        options.Frontend.AuthMode = authMode;

        return options;
    }
}
