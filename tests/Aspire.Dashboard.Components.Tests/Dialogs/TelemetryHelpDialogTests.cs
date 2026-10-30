// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using Aspire.Dashboard.Components.Dialogs;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Hosting;
using Bunit;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Dialogs;

[UseCulture("en-US")]
public sealed class TelemetryHelpDialogTests : DashboardTestContext
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Render_OnlyEnabledEndpointsAreInMarkdownTable(bool grpc, bool http)
    {
        TelemetryHelpSetupHelpers.SetupDialogs(this, new OtlpEndpointInfo(
            () => grpc ? new ResolvedEndpointInfo(BindingAddress.Parse("https://0.0.0.0:0"), new IPEndPoint(IPAddress.Any, 4317), IsHttps: true) : null,
            () => http ? new ResolvedEndpointInfo(BindingAddress.Parse("http://[::]:0"), new IPEndPoint(IPAddress.IPv6Any, 4318), IsHttps: false) : null));

        var cut = Render<TelemetryHelpDialog>();
        var expected = new List<(string Protocol, string Endpoint)>();
        if (grpc)
        {
            expected.Add(("OTLP/gRPC", "https://localhost:4317"));
        }
        if (http)
        {
            expected.Add(("OTLP/HTTP", "http://localhost:4318"));
        }
        Assert.Equal(expected, cut.FindAll(".markdown-container tbody tr").Select(row =>
            (row.Children[0].TextContent, row.Children[1].TextContent)));
        Assert.Equal(expected.Select(endpoint => endpoint.Endpoint), cut.FindAll("td code").Select(code => code.TextContent));
        Assert.Equal(grpc || http ? ["Protocol", "Dashboard endpoint"] : [], cut.FindAll("th").Select(header => header.TextContent));
        Assert.Equal(grpc || http ? 1 : 0, cut.FindAll("table").Count);
        Assert.Equal(!(grpc || http), cut.Markup.Contains(Resources.Dialogs.TelemetryHelpNoEndpointsDescription, StringComparison.Ordinal));
        Assert.Equal(
            [
                "https://aka.ms/aspire/otlp-spec",
                "https://aka.ms/aspire/dashboard-send-telemetry",
                "https://aka.ms/aspire/dashboard-manage-data"
            ],
            cut.FindAll("a").Select(link => link.GetAttribute("href")));
        Assert.Equal("Connect your app to the dashboard", cut.Find("a[href='https://aka.ms/aspire/dashboard-send-telemetry']").TextContent);
        Assert.All(cut.FindAll("a"), link =>
        {
            Assert.Equal("noopener noreferrer nofollow", link.GetAttribute("rel"));
            Assert.Equal("_blank", link.GetAttribute("target"));
        });
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("false", false)]
    [InlineData("true", true)]
    public void Render_ContainerGuidanceRequiresContainerConfiguration(string? value, bool expectedGuidance)
    {
        TelemetryHelpSetupHelpers.SetupDialogs(this);
        Services.GetRequiredService<IConfiguration>()[KnownConfigNames.DotnetRunningInContainer] = value;

        var cut = Render<TelemetryHelpDialog>();

        Assert.Equal(expectedGuidance ? 1 : 0, cut.FindAll(".markdown-container p")
            .Count(paragraph => paragraph.TextContent == "The dashboard is running in a container. Access it from the host using port forwarding."));
    }

    [Theory]
    [InlineData(OtlpAuthMode.Unsecured, true, null)]
    [InlineData(OtlpAuthMode.ApiKey, true, nameof(Resources.Dialogs.TelemetryHelpApiKeyDescription))]
    [InlineData(OtlpAuthMode.ClientCertificate, true, nameof(Resources.Dialogs.TelemetryHelpCertificateDescription))]
    [InlineData(OtlpAuthMode.ApiKey, false, null)]
    public void Render_AuthenticationGuidanceNeverIncludesSecrets(OtlpAuthMode mode, bool hasEndpoint, string? resourceName)
    {
        const string primarySecret = "primary-secret-never-render";
        const string secondarySecret = "secondary-secret-never-render";
        TelemetryHelpSetupHelpers.SetupDialogs(this, new OtlpEndpointInfo(
            () => hasEndpoint ? new ResolvedEndpointInfo(BindingAddress.Parse("https://localhost:4317"), new IPEndPoint(IPAddress.Loopback, 4317), IsHttps: true) : null,
            () => null),
            options =>
            {
                options.Otlp.AuthMode = mode;
                options.Otlp.PrimaryApiKey = primarySecret;
                options.Otlp.SecondaryApiKey = secondarySecret;
            });

        var cut = Render<TelemetryHelpDialog>();

        Assert.Equal(
            resourceName is null ? [] : new[] { Resources.Dialogs.ResourceManager.GetString(resourceName) },
            cut.FindAll(".markdown-container p").Select(paragraph => paragraph.TextContent)
                .Where(text => text == Resources.Dialogs.TelemetryHelpApiKeyDescription || text == Resources.Dialogs.TelemetryHelpCertificateDescription));
        Assert.False(cut.Markup.Contains(primarySecret, StringComparison.Ordinal));
        Assert.False(cut.Markup.Contains(secondarySecret, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public async Task Render_ImportRespectsConfigurationAndReadOnlyState(bool disabled, bool readOnly, bool expectedButton)
    {
        TelemetryHelpSetupHelpers.SetupDialogs(this, configure: options => options.UI.DisableImport = disabled);
        await FluentUISetupHelpers.ConfigureTelemetryRepository(this, readOnly, _ => Task.CompletedTask);

        var cut = Render<TelemetryHelpDialog>();

        Assert.Equal(expectedButton ? 1 : 0, cut.FindAll(".telemetry-import-button").Count);
        Assert.Equal(expectedButton ? 1 : 0, cut.FindAll("a[href='https://aka.ms/aspire/dashboard-manage-data']").Count);
        Assert.Equal(
            expectedButton ? ["📡 Send telemetry from your app", "📥 Import telemetry from a file"] : ["📡 Send telemetry from your app"],
            cut.FindAll(".markdown-container h5").Select(heading => heading.TextContent));
    }

    [Fact]
    public async Task Import_OpensManageDataWithoutClosingHelp()
    {
        var service = TelemetryHelpSetupHelpers.SetupDialogs(this);
        var dashboardDialogs = Services.GetRequiredService<DashboardDialogService>();
        var helpReference = await dashboardDialogs.ShowDialogAsync<TelemetryHelpDialog>(new DialogParameters
        {
            Title = Resources.Dialogs.TelemetryHelpDialogTitle
        });
        var helpInstance = service.LastInstance!;
        var cut = Render<TelemetryHelpDialog>(parameters => parameters.AddCascadingValue(helpInstance));

        await cut.Find(".telemetry-import-button").ClickAsync(new());

        Assert.False(helpInstance.Result.IsCompleted);
        Assert.NotSame(helpInstance, service.LastInstance);
        Assert.Equal(Resources.Dialogs.ManageDataDialogTitle, service.LastInstance!.Options.Header.Title);
        await service.LastInstance.CloseAsync();
        Assert.False(helpInstance.Result.IsCompleted);
        await helpReference.CloseAsync();
    }
}
