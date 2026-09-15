// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting.DevTunnels.Tests;

public class DevTunnelExpirationTests
{
    [Fact]
    public void ExpirationHours_DefaultsToNullAndCanBeCleared()
    {
        var options = new DevTunnelOptions();
        Assert.Null(options.ExpirationHours);

        options.ExpirationHours = 24;
        options.ExpirationHours = null;

        Assert.Null(options.ExpirationHours);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(25)]
    [InlineData(720)]
    public void ExpirationHours_AcceptsValuesWithinRange(int hours)
    {
        var options = new DevTunnelOptions { ExpirationHours = hours };

        Assert.Equal(hours, options.ExpirationHours);
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(0)]
    [InlineData(721)]
    [InlineData(int.MaxValue)]
    public void ExpirationHours_RejectsInvalidValuesWithoutChangingOptions(int hours)
    {
        var options = new DevTunnelOptions { ExpirationHours = 24 };

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => options.ExpirationHours = hours);

        Assert.Equal("value", exception.ParamName);
        Assert.Equal(hours, exception.ActualValue);
        Assert.Equal(24, options.ExpirationHours);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 24)]
    [InlineData(false, 720)]
    public void WithExpiration_ConfiguresOptions(bool polyglot, int hours)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var tunnel = polyglot
            ? builder.AddDevTunnelForPolyglot("tunnel")
            : builder.AddDevTunnel("tunnel");

        var result = tunnel.WithExpiration(hours);

        Assert.Same(tunnel, result);
        Assert.Equal(hours, tunnel.Resource.Options.ExpirationHours);
    }

    [Fact]
    public void WithExpiration_RejectsNullBuilder()
    {
        IResourceBuilder<DevTunnelResource> builder = null!;

        var exception = Assert.Throws<ArgumentNullException>(() => builder.WithExpiration(24));

        Assert.Equal("tunnelBuilder", exception.ParamName);
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(0)]
    [InlineData(721)]
    [InlineData(int.MaxValue)]
    public void WithExpiration_RejectsInvalidExpiration(int hours)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var tunnel = builder.AddDevTunnel("tunnel");

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => tunnel.WithExpiration(hours));

        Assert.Equal("expirationHours", exception.ParamName);
        Assert.Null(tunnel.Resource.Options.ExpirationHours);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(24)]
    [InlineData(25)]
    [InlineData(720)]
    public async Task CreateAndUpdate_PassExpirationInHours(int? hours)
    {
        var cli = new TestDevTunnelCli();
        cli.EnqueueCreateResult(0);
        cli.EnqueueUpdateResult(0);
        var options = new DevTunnelOptions
        {
            ExpirationHours = hours
        };

        await cli.CreateTunnelAsync("mytunnel", options);
        await cli.UpdateTunnelAsync("mytunnel", options);

        await Verify(cli.Calls.Select(call => call.Arguments)).UseParameters(hours);
    }

    [Fact]
    public async Task CreateAndUpdate_WithNullOptions_OmitExpiration()
    {
        var cli = new TestDevTunnelCli();
        cli.EnqueueCreateResult(0);
        cli.EnqueueUpdateResult(0);

        await cli.CreateTunnelAsync("mytunnel");
        await cli.UpdateTunnelAsync("mytunnel");

        await Verify(cli.Calls.Select(call => call.Arguments));
    }

    [Fact]
    public async Task CreateAndUpdate_FormatExpirationInvariantly()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            var cli = new TestDevTunnelCli();
            cli.EnqueueCreateResult(0);
            cli.EnqueueUpdateResult(0);
            var options = new DevTunnelOptions { ExpirationHours = 25 };

            await cli.CreateTunnelAsync("mytunnel", options);
            await cli.UpdateTunnelAsync("mytunnel", options);

            await Verify(cli.Calls.Select(call => call.Arguments));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData(24)]
    public async Task CreateTunnelAsync_WhenTunnelExists_PassesExpirationToUpdate(int? hours)
    {
        var cli = new TestDevTunnelCli();
        cli.EnqueueCreateResult(DevTunnelCli.ResourceConflictsWithExistingExitCode);
        cli.EnqueueUpdateResult(0, """{"tunnelId":"mytunnel.eun1"}""");
        cli.EnqueueResetAccessResult(0, """{"accessControlEntries":[]}""");
        var client = new DevTunnelCliClient(new ConfigurationBuilder().Build(), cli);
        var options = new DevTunnelOptions
        {
            Region = DevTunnelRegion.NorthEurope,
            ExpirationHours = hours
        };

        var tunnel = await client.CreateTunnelAsync("mytunnel", options);

        Assert.Equal("mytunnel.eun1", tunnel.TunnelId);
        await Verify(cli.Calls).UseParameters(hours);
    }

    [Fact]
    public async Task ToLoggerString_IncludesExpiration()
    {
        var options = new DevTunnelOptions { ExpirationHours = 24 };

        await Verify(options.ToLoggerString());
    }
}
