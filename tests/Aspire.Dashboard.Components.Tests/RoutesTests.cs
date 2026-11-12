// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Components.Pages;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Configuration;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Aspire.Dashboard.Components.Tests;

[UseCulture("en-US")]
public class RoutesTests : DashboardTestContext
{
    public RoutesTests()
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        FluentUISetupHelpers.SetupFluentAnchor(this);
        FluentUISetupHelpers.SetupFluentTextField(this);
        JSInterop.SetupModule("/Components/Pages/Login.razor.js");
        JSInterop.Setup<ViewportSize>("window.getWindowDimensions").SetResult(new ViewportSize(1440, 1000));
        JSInterop.SetupVoid("window.listenToWindowResize", _ => true).SetVoidResult();
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/error/404")]
    [InlineData("/terminal-window/apphost/shell")]
    [InlineData("/owned-missing")]
    public void UnauthenticatedBrowserToken_RedirectsWithoutProtectedLayout(string route)
    {
        this.AddAuthorization().SetNotAuthorized();
        Services.GetRequiredService<IOptions<DashboardOptions>>().Value.Frontend.AuthMode = FrontendAuthMode.BrowserToken;
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(route);

        var cut = Render<Routes>();

        cut.WaitForAssertion(() =>
        {
            var uri = new Uri(navigation.Uri);
            Assert.Equal("/login", uri.AbsolutePath);
            Assert.Equal(route, QueryHelpers.ParseQuery(uri.Query)["returnUrl"]);
            Assert.Single(cut.FindComponents<Login>());
            Assert.Empty(cut.FindComponents<MainLayout>());
            Assert.Empty(cut.FindComponents<WindowLayout>());
            Assert.Empty(cut.FindComponents<DashboardDialogProvider>());
        });
    }

    [Theory]
    [InlineData(FrontendAuthMode.OpenIdConnect)]
    [InlineData(FrontendAuthMode.Unsecured)]
    [InlineData(null)]
    public void UnauthenticatedNonBrowserToken_UsesEmptyLayoutAndLocalizedMessage(FrontendAuthMode? authMode)
    {
        this.AddAuthorization().SetNotAuthorized();
        Services.GetRequiredService<IOptions<DashboardOptions>>().Value.Frontend.AuthMode = authMode;

        var cut = Render<Routes>();

        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindComponents<EmptyLayout>());
            Assert.Empty(cut.FindComponents<MainLayout>());
            Assert.Single(cut.FindComponents<DashboardNotAuthorized>());
            Assert.Equal("Not authorized", string.Concat(cut.FindComponent<EmptyLayout>().Nodes.Select(node => node.TextContent)).Trim());
        });
    }

    [Theory]
    [InlineData(FrontendAuthMode.OpenIdConnect)]
    [InlineData(FrontendAuthMode.Unsecured)]
    [InlineData(null)]
    public void UnauthenticatedNonBrowserToken_MissingRouteShowsLocalizedMessageWithoutProtectedLayout(FrontendAuthMode? authMode)
    {
        this.AddAuthorization().SetNotAuthorized();
        Services.GetRequiredService<IOptions<DashboardOptions>>().Value.Frontend.AuthMode = authMode;
        Services.GetRequiredService<NavigationManager>().NavigateTo("/owned-missing");

        var cut = Render<Routes>();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("Not authorized", cut.FindComponent<DashboardNotAuthorized>().Markup);
            Assert.Empty(cut.FindComponents<MainLayout>());
            Assert.Empty(cut.FindComponents<DashboardDialogProvider>());
        });
    }

    [Fact]
    public void AuthenticatedUserWithoutFrontendPolicy_DoesNotUseMainLayout()
    {
        this.AddAuthorization().SetAuthorized("test-user");
        Services.GetRequiredService<IOptions<DashboardOptions>>().Value.Frontend.AuthMode = FrontendAuthMode.OpenIdConnect;

        var cut = Render<Routes>();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(typeof(EmptyLayout), cut.FindComponent<AuthorizeRouteView>().Instance.DefaultLayout);
            Assert.Single(cut.FindComponents<EmptyLayout>());
            Assert.Empty(cut.FindComponents<MainLayout>());
            Assert.Empty(cut.FindComponents<DashboardDialogProvider>());
        });
    }

    [Theory]
    [InlineData(FrontendAuthMode.BrowserToken)]
    [InlineData(FrontendAuthMode.OpenIdConnect)]
    public void PendingAuthorization_DoesNotRenderPageOrProtectedLayout(FrontendAuthMode authMode)
    {
        this.AddAuthorization().SetAuthorizing();
        Services.GetRequiredService<IOptions<DashboardOptions>>().Value.Frontend.AuthMode = authMode;

        var cut = Render<Routes>();

        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindComponents<AuthorizeView>());
            Assert.Empty(cut.FindComponents<AuthorizeRouteView>());
            Assert.Empty(cut.FindComponents<MainLayout>());
            Assert.Empty(cut.FindComponents<WindowLayout>());
        });
    }

    [Fact]
    public void UnauthenticatedErrorPage_UsesEmptyLayout()
    {
        this.AddAuthorization().SetNotAuthorized();
        Services.GetRequiredService<IOptions<DashboardOptions>>().Value.Frontend.AuthMode = FrontendAuthMode.BrowserToken;
        Services.GetRequiredService<NavigationManager>().NavigateTo("/error");

        var cut = Render<Routes>();

        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindComponents<Error>());
            Assert.Single(cut.FindComponents<EmptyLayout>());
            Assert.Empty(cut.FindComponents<MainLayout>());
            Assert.Empty(cut.FindComponents<DashboardNotAuthorized>());
        });
    }

    [Fact]
    public void AuthenticatedUser_UsesMainLayoutByDefaultAndPreservesAnonymousPageLayout()
    {
        var authorization = this.AddAuthorization();
        authorization.SetAuthorized("test-user");
        authorization.SetPolicies(FrontendAuthorizationDefaults.PolicyName);
        Services.GetRequiredService<IOptions<DashboardOptions>>().Value.Frontend.AuthMode = FrontendAuthMode.BrowserToken;
        Services.GetRequiredService<NavigationManager>().NavigateTo("/error");

        var cut = Render<Routes>();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(typeof(MainLayout), cut.FindComponent<AuthorizeRouteView>().Instance.DefaultLayout);
            Assert.Single(cut.FindComponents<Error>());
            Assert.Single(cut.FindComponents<EmptyLayout>());
            Assert.Empty(cut.FindComponents<MainLayout>());
        });
    }
}
