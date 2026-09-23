// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Claims;
using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Configuration;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Controls;

[UseCulture("en-US")]
public class UserProfileTests : DashboardTestContext
{
    [Fact]
    public void AuthenticatedUser_RendersAnchoredProfilePopover()
    {
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddOptions();
        Services.AddFluentUIComponents();
        Services.Configure<DashboardOptions>(options =>
        {
            options.Frontend.AuthMode = FrontendAuthMode.OpenIdConnect;
            Assert.True(options.Frontend.OpenIdConnect.TryParseOptions(out _));
        });
        FluentUISetupHelpers.SetupFluentButton(this);

        var authorizationContext = this.AddTestAuthorization();
        authorizationContext.SetAuthorized("Ada Lovelace");
        authorizationContext.SetClaims(
            new Claim("name", "Ada Lovelace"),
            new Claim("preferred_username", "ada@example.com"));

        var cut = RenderComponent<UserProfile>();

        var button = cut.Find(".profile-menu-button");
        var popover = cut.Find("fluent-popover-b");
        Assert.Equal("Ada Lovelace", button.GetAttribute("aria-label"));
        Assert.Equal("false", button.GetAttribute("aria-expanded"));
        Assert.Equal(button.Id, popover.GetAttribute("anchor-id"));
        Assert.Equal("false", popover.GetAttribute("opened"));

        button.Click();

        Assert.Equal("true", button.GetAttribute("aria-expanded"));
        Assert.Equal("true", popover.GetAttribute("opened"));
        Assert.Equal("Logged in as:", cut.Find(".profile-popover-header > span").TextContent);
        Assert.Equal("Ada Lovelace", cut.Find(".full-name").TextContent);
        Assert.Equal("ada@example.com", cut.Find(".user-id").TextContent);
        Assert.Equal("authentication/logout", cut.Find(".sign-out-form").GetAttribute("action"));
        Assert.Equal("Sign out", cut.Find(".sign-out-form fluent-button").TextContent.Trim());
    }
}