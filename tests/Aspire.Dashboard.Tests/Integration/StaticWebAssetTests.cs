// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Reflection;
using Aspire.Dashboard.Configuration;
using Aspire.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Aspire.Dashboard.Tests.Integration;

public sealed class StaticWebAssetTests(ITestOutputHelper testOutputHelper)
{
    [Theory]
    [InlineData("/css/app.css", "text/css")]
    [InlineData("/js/app.js", "text/javascript")]
    [InlineData("/framework/blazor.web.js", "text/javascript")]
    [InlineData("/_content/Microsoft.FluentUI.AspNetCore.Components/Microsoft.FluentUI.AspNetCore.Components.lib.module.js", "text/javascript")]
    public async Task DashboardServesStaticWebAssetsWhenRunningFromBuildOutputInProduction(string path, string expectedContentType)
    {
        var dashboardAssemblyDirectory = GetDashboardAssemblyDirectory();

        await using var app = new DashboardWebApplication(
            options: new WebApplicationOptions
            {
                ApplicationName = "Aspire.Dashboard",
                ContentRootPath = dashboardAssemblyDirectory,
                EnvironmentName = Environments.Production,
                WebRootPath = Path.Combine(dashboardAssemblyDirectory, "wwwroot")
            },
            preConfigureBuilder: builder =>
            {
                var sources = ((IConfigurationBuilder)builder.Configuration).Sources;
                foreach (var item in sources.ToList())
                {
                    if (item is EnvironmentVariablesConfigurationSource)
                    {
                        sources.Remove(item);
                    }
                }

                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [DashboardConfigNames.DashboardFrontendUrlName.ConfigKey] = "http://127.0.0.1:0",
                    [DashboardConfigNames.DashboardOtlpGrpcUrlName.ConfigKey] = "http://127.0.0.1:0",
                    [DashboardConfigNames.DashboardOtlpAuthModeName.ConfigKey] = nameof(OtlpAuthMode.Unsecured),
                    [DashboardConfigNames.DashboardFrontendAuthModeName.ConfigKey] = nameof(FrontendAuthMode.Unsecured),
                    [DashboardConfigNames.DashboardApiAuthModeName.ConfigKey] = nameof(ApiAuthMode.Unsecured)
                });

                builder.Services.AddSingleton(IntegrationTestHelpers.CreateLoggerFactory(testOutputHelper));
            });

        await app.StartAsync().DefaultTimeout();

        using var client = new HttpClient { BaseAddress = new Uri($"http://{app.FrontendSingleEndPointAccessor().EndPoint}") };

        var response = await client.GetAsync(path).DefaultTimeout();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expectedContentType, response.Content.Headers.ContentType?.MediaType);
    }

    private static string GetDashboardAssemblyDirectory()
    {
        const string aspireDashboardAssemblyName = "Aspire.Dashboard";

        var currentAssemblyName = Assembly.GetExecutingAssembly().GetName().Name!;
        var currentAssemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        var aspireAssemblyDirectory = currentAssemblyDirectory.Replace(currentAssemblyName, aspireDashboardAssemblyName, StringComparison.Ordinal);

        Assert.True(File.Exists(Path.Combine(aspireAssemblyDirectory, $"{aspireDashboardAssemblyName}.dll")));

        return aspireAssemblyDirectory;
    }
}
