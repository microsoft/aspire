// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Tests.Model;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Aspire.Dashboard.Tests.Middleware;

public sealed class DashboardExperienceRedirectMiddlewareTests
{
    [Theory]
    [InlineData("legacy", "/traces", "/legacy#")]
    [InlineData("modern", "/legacy/traces", "/#")]
    public async Task InvokeAsync_CrossExperienceHtmlNavigation_RedirectsToRoot(
        string experience,
        string path,
        string expectedLocation)
    {
        var context = CreateContext(experience, path, "text/html");
        var middleware = CreateMiddleware();

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status302Found, context.Response.StatusCode);
        Assert.Equal(expectedLocation, context.Response.Headers.Location);
    }

    [Theory]
    [InlineData("legacy", "/legacy/traces", "text/html")]
    [InlineData("modern", "/traces", "text/html")]
    [InlineData("legacy", "/api/resources", "text/html")]
    [InlineData("legacy", "/styles/app.css", "text/css")]
    public async Task InvokeAsync_SameExperienceOrExcludedRequest_ContinuesPipeline(
        string experience,
        string path,
        string accept)
    {
        var context = CreateContext(experience, path, accept);
        var middleware = CreateMiddleware();

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status204NoContent, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_ModernDashboardDisabled_IgnoresModernCookie()
    {
        var context = CreateContext("modern", "/traces", "text/html");
        var middleware = CreateMiddleware(disableModernDashboard: true);

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status302Found, context.Response.StatusCode);
        Assert.Equal("/legacy#", context.Response.Headers.Location);
    }

    [Fact]
    public async Task InvokeAsync_LegacyUnavailable_IgnoresLegacyConfigurationAndCookie()
    {
        var context = CreateContext("legacy", "/traces", "text/html");
        var middleware = CreateMiddleware(disableModernDashboard: true, legacyAvailable: false);

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status204NoContent, context.Response.StatusCode);
    }

    private static DashboardExperienceRedirectMiddleware CreateMiddleware(
        bool disableModernDashboard = false,
        bool legacyAvailable = true)
    {
        var options = new DashboardOptions();
        options.UI.DisableModernDashboard = disableModernDashboard;
        return new DashboardExperienceRedirectMiddleware(
            context =>
            {
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return Task.CompletedTask;
            },
            new TestOptionsMonitor<DashboardOptions>(options),
            new DashboardExperienceAvailability(legacyAvailable));
    }

    private static DefaultHttpContext CreateContext(string experience, string path, string accept)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = path;
        context.Request.Headers.Accept = accept;
        context.Request.Headers.Cookie = $"Aspire_Dashboard_Experience={experience}";
        return context;
    }
}
