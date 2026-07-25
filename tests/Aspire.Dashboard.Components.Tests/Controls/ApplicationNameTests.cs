// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Tests.Shared;
using Bunit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using IConfiguration = Microsoft.Extensions.Configuration.IConfiguration;

namespace Aspire.Dashboard.Components.Tests.Controls;

public class ApplicationNameTests : DashboardTestContext
{
    [Fact]
    public void Render_DashboardClientDisabled_Success()
    {
        // Arrange
        AddDashboardClientServices();

        // Act
        var cut = RenderComponent<ApplicationName>();

        // Assert
        cut.MarkupMatches("Aspire");
    }

    [Fact]
    public void Render_With_Args()
    {
        // Arrange
        AddDashboardClientServices();

        // Act
        var cut = RenderComponent<ApplicationName>(builder =>
        {
            builder.Add(p => p.ResourceName, "{0} traces");
            builder.Add(p => p.Loc, new TestStringLocalizer<string>());
            builder.Add(p => p.AdditionalText, "Hello World");
        });

        // Assert
        cut.MarkupMatches("Localized:Aspire traces (Hello World)");
    }

    [Fact]
    public void Render_DashboardClientEnabled_HtmlInName_Success()
    {
        // Arrange
        Services.AddSingleton<IConfiguration>(new ConfigurationManager());
        Services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        Services.AddSingleton<IDashboardClient>(new TestDashboardClient(applicationName: "<marquee>An HTML title!</marquee>"));

        // Act
        var cut = RenderComponent<ApplicationName>();

        // Assert
        cut.MarkupMatches("&lt;marquee&gt;An HTML title!&lt;/marquee&gt;");
    }

    private void AddDashboardClientServices()
    {
        Services.AddLocalization();
        Services.AddSingleton<IConfiguration>(new ConfigurationManager());
        Services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        Services.AddSingleton<IDashboardClient, DashboardClient>();
        Services.AddSingleton<BrowserTimeProvider>();
        Services.AddSingleton<IKnownPropertyLookup>(new MockKnownPropertyLookup());
    }

    private sealed class TestStringLocalizer<T> : IStringLocalizer<T>
    {
        public LocalizedString this[string name] => new LocalizedString(name, $"Localized:{name}");
        public LocalizedString this[string name, params object[] arguments] => new LocalizedString(name, $"Localized:{name}:" + string.Join("+", arguments));

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }
}
