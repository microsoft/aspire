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
    [InlineData("legacy", "/", "", "/legacy#")]
    [InlineData("legacy", "/", "?resource=api", "/legacy?resource=api#")]
    [InlineData("legacy", "/", "?view=Graph&hiddenTypes=Container", "/legacy?view=Graph&hiddenTypes=Container#")]
    [InlineData("legacy", "/home", "?resource=api", "/legacy#")]
    [InlineData("legacy", "/resources/api", "", "/legacy#")]
    [InlineData("legacy", "/traces", "", "/legacy/traces#")]
    [InlineData("legacy", "/traces/detail/abc123", "", "/legacy/traces/detail/abc123#")]
    [InlineData("legacy", "/consolelogs/resource/api", "", "/legacy/consolelogs/resource/api#")]
    [InlineData("legacy", "/structuredlogs/resource/my api", "?logLevel=error", "/legacy/structuredlogs/resource/my%20api?logLevel=error#")]
    [InlineData("modern", "/legacy", "", "/#")]
    [InlineData("modern", "/legacy/resources", "", "/#")]
    [InlineData("modern", "/legacy/traces", "", "/traces#")]
    [InlineData("modern", "/legacy/metrics/resource/api/meter/System.Runtime", "?duration=5", "/metrics/resource/api/meter/System.Runtime?duration=5#")]
    public async Task InvokeAsync_CrossExperienceHtmlNavigation_RedirectsToSamePageOrRoot(
        string experience,
        string path,
        string query,
        string expectedLocation)
    {
        var context = CreateContext(experience, path, "text/html");
        context.Request.QueryString = new QueryString(query);
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
        Assert.Equal("/legacy/traces#", context.Response.Headers.Location);
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
