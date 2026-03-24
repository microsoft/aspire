// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Aspire.Hosting.Ats;
using Xunit;

namespace Aspire.Hosting.RemoteHost.Tests;

[Trait("Partition", "4")]
public class AtsExportsTests
{
    [Fact]
    public void GetConnectionString_ReturnsConfiguredValue()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:cache"] = "UseDevelopmentStorage=true"
            })
            .Build();

        var value = BuilderExports.GetConnectionString(configuration, "cache");

        Assert.Equal("UseDevelopmentStorage=true", value);
    }

    [Fact]
    public void IsDevelopment_ReturnsTrueForDevelopmentEnvironment()
    {
        var environment = new TestHostEnvironment
        {
            EnvironmentName = Environments.Development
        };

        var result = BuilderExports.IsDevelopment(environment);

        Assert.True(result);
    }

    [Fact]
    public void IsEnvironment_ReturnsTrueForMatchingEnvironmentName()
    {
        var environment = new TestHostEnvironment
        {
            EnvironmentName = "Custom"
        };

        var result = BuilderExports.IsEnvironment(environment, "Custom");

        Assert.True(result);
    }

    [Fact]
    public void ParseLogLevel_UnknownLevelDefaultsToInformation()
    {
        var result = LoggingExports.ParseLogLevel("verbose");

        Assert.Equal(LogLevel.Information, result);
    }

    [Fact]
    public void ParseLogLevel_StrictModeThrowsForUnknownLevel()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => LoggingExports.ParseLogLevel("verbose", throwOnUnknown: true));

        Assert.Equal("level", exception.ParamName);
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = nameof(TestHostEnvironment);
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
