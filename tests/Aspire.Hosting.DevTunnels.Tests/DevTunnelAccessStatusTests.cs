// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.DevTunnels.Tests;

public class DevTunnelAccessStatusTests
{
    private static readonly DateTimeOffset s_now = new(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void LogAnonymousAccessPolicy_DeniesAnonymousForNoEntries()
    {
        var status = new DevTunnelAccessStatus();
        Assert.Equal("Denied", status.LogAnonymousAccessPolicy(NullLogger.Instance, s_now));
    }

    [Fact]
    public void LogAnonymousAccessPolicy_AllowsAnonymousForSingleInheritedAllow()
    {
        var status = new DevTunnelAccessStatus
        {
            AccessControlEntries = [new("Anonymous", IsDeny: false, IsInherited: true, [], ["connect"])]
        };
        Assert.Equal("Allowed", status.LogAnonymousAccessPolicy(NullLogger.Instance, s_now));
    }

    [Fact]
    public void LogAnonymousAccessPolicy_AllowsAnonymousForSingleExplicitAllow()
    {
        var status = new DevTunnelAccessStatus
        {
            AccessControlEntries = [new("Anonymous", IsDeny: false, IsInherited: false, [], ["connect"])]
        };
        Assert.Equal("Allowed", status.LogAnonymousAccessPolicy(NullLogger.Instance, s_now));
    }

    [Fact]
    public void LogAnonymousAccessPolicy_DeniesAnonymousWhenExplicitlyDeniedWithInheritedAllow()
    {
        var status = new DevTunnelAccessStatus
        {
            AccessControlEntries = [
                new("Anonymous", IsDeny: false, IsInherited: true, [], ["connect"]),
                new("Anonymous", IsDeny: true, IsInherited: false, [], ["connect"])
            ]
        };
        Assert.Equal("Denied", status.LogAnonymousAccessPolicy(NullLogger.Instance, s_now));
    }

    [Fact]
    public void LogAnonymousAccessPolicy_DeniesAnonymousForSingleExplicitDeny()
    {
        var status = new DevTunnelAccessStatus
        {
            AccessControlEntries = [
                new("Anonymous", IsDeny: true, IsInherited: false, [], ["connect"])
            ]
        };
        Assert.Equal("Denied", status.LogAnonymousAccessPolicy(NullLogger.Instance, s_now));
    }

    [Fact]
    public void LogAnonymousAccessPolicy_AllowsAnonymousWhenExplicitlyAllowedWithInheritedAllowed()
    {
        var status = new DevTunnelAccessStatus
        {
            AccessControlEntries = [
                new("Anonymous", IsDeny: false, IsInherited: true, [], ["connect"]),
                new("Anonymous", IsDeny: false, IsInherited: false, [], ["connect"])
            ]
        };
        Assert.Equal("Allowed", status.LogAnonymousAccessPolicy(NullLogger.Instance, s_now));
    }

    [Fact]
    public void LogAnonymousAccessPolicy_ReturnsDeniedForUnexpectedEntries()
    {
        var status = new DevTunnelAccessStatus
        {
            AccessControlEntries = [
                new("Something", IsDeny: false, IsInherited: false, [], ["random"]),
                new("Something", IsDeny: false, IsInherited: false, [], ["random"])
            ]
        };
        Assert.Equal("Denied", status.LogAnonymousAccessPolicy(NullLogger.Instance, s_now));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InverseAnonymousRulesDoNotApplyToAnonymousCallers(bool deny)
    {
        var status = new DevTunnelAccessStatus
        {
            AccessControlEntries = [
                new("Anonymous", deny, false, [], ["connect"]) { IsInverse = true },
                .. (deny ? new DevTunnelAccessStatus.AccessControlEntry[] { new("Anonymous", false, true, [], ["connect"]) } : [])
            ]
        };
        var expected = deny ? "Allowed" : "Denied";
        Assert.Equal(expected, status.GetAnonymousAccessPolicy(s_now));
        Assert.Equal(expected, status.LogAnonymousAccessPolicy(NullLogger.Instance, s_now));
    }

    [Fact]
    public void ExpiringDenyOnlyAffectsDisplayUntilExpiration()
    {
        var expiration = s_now.AddMinutes(1);
        var status = new DevTunnelAccessStatus
        {
            AccessControlEntries = [
                new("Anonymous", false, true, [], ["connect"]),
                new("Anonymous", true, false, [], ["connect"]) { Expiration = expiration }
            ]
        };
        Assert.Equal("Denied", status.GetAnonymousAccessPolicy(s_now));
        Assert.Equal("Allowed", status.GetAnonymousAccessPolicy(expiration));
        Assert.Equal("Allowed", status.GetAnonymousAccessPolicy(expiration.AddMinutes(1)));
    }

    [Fact]
    public void ExpiringAllowOnlyAffectsDisplayUntilExpiration()
    {
        var expiration = s_now.AddMinutes(1);
        var status = new DevTunnelAccessStatus
        {
            AccessControlEntries = [new("Anonymous", false, false, [], ["connect"]) { Expiration = expiration }]
        };
        Assert.Equal("Allowed", status.GetAnonymousAccessPolicy(s_now));
        Assert.Equal("Denied", status.GetAnonymousAccessPolicy(expiration));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PermanentPolicyMatchingRequiresCorrectSubjectAndNoExpiration(bool deny)
    {
        var rule = new DevTunnelAccessStatus.AccessControlEntry("Anonymous", deny, false, [], ["connect"]);
        Assert.True(rule.IsPermanentAnonymousConnectRule(deny));
        Assert.False((rule with { IsInverse = true }).IsPermanentAnonymousConnectRule(deny));
        Assert.False((rule with { Expiration = s_now.AddYears(1) }).IsPermanentAnonymousConnectRule(deny));
        Assert.False((rule with { Expiration = s_now.AddYears(-1) }).IsPermanentAnonymousConnectRule(deny));
        Assert.False(rule.IsPermanentAnonymousConnectRule(!deny));
    }
}
