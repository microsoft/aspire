// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.RemoteHost.Language;
using Aspire.Tests.Utils;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Aspire.Hosting.RemoteHost.Tests;

public class IntegrationHostConfigurationTests(ITestOutputHelper output)
{
    [Fact]
    public void ConfigurationSnapshotsSettingsAndDescriptorsOnce()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var entryPoint = Path.Combine(workspace.Path, "host.mts");
        File.WriteAllText(entryPoint, "");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ASPIRE_INTEGRATION_HOSTS_ENABLED"] = "true",
            ["IntegrationHost:InvocationTimeout"] = "00:03:00",
            ["IntegrationHost:RegistrationTimeout"] = "00:04:00",
            ["IntegrationHost:DiscoveryTimeout"] = "00:05:00",
            ["IntegrationHost:ShutdownTimeout"] = "00:00:20",
            ["IntegrationHost:OutputDrainIdleTimeout"] = "00:00:10",
            ["IntegrationHost:StableHostPeriod"] = "00:02:00",
            ["IntegrationHost:RestartDelay"] = "00:00:02",
            ["IntegrationHost:MaxRestartDelay"] = "00:00:08",
            ["IntegrationHost:MaxRestartAttempts"] = "5",
            ["IntegrationHosts:0:Language"] = "typescript",
            ["IntegrationHosts:0:PackageName"] = "example",
            ["IntegrationHosts:0:HostEntryPoint"] = entryPoint
        }).Build();
        var settings = new IntegrationHostConfiguration(configuration);
        configuration["IntegrationHost:InvocationTimeout"] = "invalid";
        configuration["IntegrationHosts:0:PackageName"] = "changed";

        Assert.Equal(TimeSpan.FromMinutes(3), settings.InvocationTimeout);
        Assert.Equal(TimeSpan.FromMinutes(1), settings.CallbackTimeout);
        Assert.Equal(TimeSpan.FromMinutes(4), settings.RegistrationTimeout);
        Assert.Equal(TimeSpan.FromMinutes(5), settings.DiscoveryTimeout);
        Assert.Equal(TimeSpan.FromSeconds(20), settings.ShutdownTimeout);
        Assert.Equal(TimeSpan.FromSeconds(10), settings.OutputDrainIdleTimeout);
        Assert.Equal(TimeSpan.FromMinutes(2), settings.StableHostPeriod);
        Assert.Equal(5, settings.MaxRestartAttempts);
        Assert.Equal([2d, 4d, 8d, 8d], Enumerable.Range(1, 4).Select(attempt => settings.GetRestartDelay(attempt).TotalSeconds));
        var descriptors = settings.Descriptors;
        File.Delete(entryPoint);
        Assert.Same(descriptors, settings.Descriptors);
        Assert.Equal("example", Assert.Single(descriptors).PackageName);
    }

    [Theory]
    [InlineData("InvocationTimeout", "00:00:00")]
    [InlineData("RegistrationTimeout", "-00:00:01")]
    [InlineData("DiscoveryTimeout", "50.00:00:00")]
    [InlineData("CallbackTimeout", "invalid")]
    [InlineData("ShutdownTimeout", "00:00:00")]
    [InlineData("OutputDrainIdleTimeout", "00:00:00")]
    [InlineData("StableHostPeriod", "00:00:00")]
    [InlineData("RestartDelay", "00:00:00")]
    [InlineData("MaxRestartAttempts", "-1")]
    public void InvalidSettingIdentifiesConfigurationKey(string name, string value)
    {
        var key = $"IntegrationHost:{name}";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = value }).Build();

        var error = Assert.Throws<InvalidOperationException>(() => new IntegrationHostConfiguration(configuration));

        Assert.Contains(key, error.Message);
    }

    [Fact]
    public void RestartDelayCannotExceedMaximum()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["IntegrationHost:RestartDelay"] = "00:00:05"
        }).Build();

        var error = Assert.Throws<InvalidOperationException>(() => new IntegrationHostConfiguration(configuration));

        Assert.Equal("IntegrationHost:RestartDelay must not exceed IntegrationHost:MaxRestartDelay.", error.Message);
    }
}
