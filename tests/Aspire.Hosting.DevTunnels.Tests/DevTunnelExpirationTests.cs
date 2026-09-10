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
    public void Expiration_DefaultsToNullAndCanBeCleared()
    {
        var options = new DevTunnelOptions();
        Assert.Null(options.Expiration);

        options.Expiration = TimeSpan.FromDays(1);
        options.Expiration = null;

        Assert.Null(options.Expiration);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(25)]
    [InlineData(720)]
    public void Expiration_AcceptsWholeHoursWithinRange(int hours)
    {
        var expiration = TimeSpan.FromHours(hours);
        var options = new DevTunnelOptions { Expiration = expiration };

        Assert.Equal(expiration, options.Expiration);
    }

    [Theory]
    [InlineData(long.MinValue)]
    [InlineData(-TimeSpan.TicksPerHour)]
    [InlineData(0)]
    [InlineData(TimeSpan.TicksPerHour - 1)]
    [InlineData(TimeSpan.TicksPerHour + 1)]
    [InlineData(TimeSpan.TicksPerHour + TimeSpan.TicksPerMinute)]
    [InlineData(TimeSpan.TicksPerHour + TimeSpan.TicksPerHour / 2)]
    [InlineData(30 * TimeSpan.TicksPerDay + 1)]
    [InlineData(long.MaxValue)]
    public void Expiration_RejectsInvalidValuesWithoutChangingOptions(long ticks)
    {
        var options = new DevTunnelOptions { Expiration = TimeSpan.FromDays(1) };
        var expiration = TimeSpan.FromTicks(ticks);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => options.Expiration = expiration);

        Assert.Equal("value", exception.ParamName);
        Assert.Equal(expiration, exception.ActualValue);
        Assert.Equal(TimeSpan.FromDays(1), options.Expiration);
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
        Assert.Equal(TimeSpan.FromHours(hours), tunnel.Resource.Options.Expiration);
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
        Assert.Null(tunnel.Resource.Options.Expiration);
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
            Expiration = hours is { } value ? TimeSpan.FromHours(value) : null
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
            var options = new DevTunnelOptions { Expiration = TimeSpan.FromHours(25) };

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
            Expiration = hours is { } value ? TimeSpan.FromHours(value) : null
        };

        var tunnel = await client.CreateTunnelAsync("mytunnel", options);

        Assert.Equal("mytunnel.eun1", tunnel.TunnelId);
        await Verify(cli.Calls).UseParameters(hours);
    }

    [Fact]
    public async Task ToLoggerString_IncludesExpiration()
    {
        var options = new DevTunnelOptions { Expiration = TimeSpan.FromDays(1) };

        await Verify(options.ToLoggerString());
    }
}
