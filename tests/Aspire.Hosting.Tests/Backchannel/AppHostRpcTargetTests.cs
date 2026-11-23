// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Resources;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Backchannel;

[Trait("Partition", "4")]
public class AppHostRpcTargetTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData("--operation", "publish", false)]
    [InlineData("--operation", "publish", true)]
    [InlineData("--publisher", "manifest", false)]
    [InlineData("--publisher", "manifest", true)]
    [InlineData("--AppHost:Operation", "publish", false)]
    [InlineData("--AppHost:Operation", "publish", true)]
    public async Task GetDashboardUrlsAsync_InPublishMode_ThrowsWithPublishGuidance(string option, string value, bool disableDashboard)
    {
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.DisableDashboard = disableDashboard,
            outputHelper,
            option, value);
        await using var app = builder.Build();
        var target = app.Services.GetRequiredService<AppHostRpcTarget>();

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => target.GetDashboardUrlsAsync(TestContext.Current.CancellationToken).DefaultTimeout());

        Assert.Equal(MessageStrings.PublishModeNotSupportedByRunCommand, exception.Message);
    }

    [Fact]
    public async Task GetDashboardUrlsAsync_InRunModeWithDashboardDisabled_ReturnsUnhealthy()
    {
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.DisableDashboard = true,
            outputHelper);
        await using var app = builder.Build();
        var target = app.Services.GetRequiredService<AppHostRpcTarget>();

        var result = await target.GetDashboardUrlsAsync(TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.False(result.DashboardHealthy);
        Assert.Null(result.BaseUrlWithLoginToken);
        Assert.Null(result.CodespacesUrlWithLoginToken);
    }
}
