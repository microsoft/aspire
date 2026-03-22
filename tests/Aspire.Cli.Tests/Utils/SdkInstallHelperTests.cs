// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Telemetry;
using Aspire.Cli.Tests.Telemetry;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Tests.Utils;

public class SdkInstallHelperTests
{
    [Fact]
    public async Task EnsureSdkInstalledAsync_WhenSdkAlreadyInstalled_RecordsAlreadyInstalledTelemetry()
    {
        using var fixture = new TelemetryFixture();

        var sdkInstaller = new TestDotNetSdkInstaller
        {
            CheckAsyncCallback = _ => (true, "9.0.302", "9.0.302")
        };

        var interactionService = new TestInteractionService();

        var result = await SdkInstallHelper.EnsureSdkInstalledAsync(
            sdkInstaller,
            interactionService,
            fixture.Telemetry);

        Assert.True(result);
        Assert.NotNull(fixture.CapturedActivity);

        var tags = fixture.CapturedActivity.Tags.ToDictionary(t => t.Key, t => t.Value);
        Assert.Equal("9.0.302", tags[TelemetryConstants.Tags.SdkDetectedVersion]);
        Assert.Equal("9.0.302", tags[TelemetryConstants.Tags.SdkMinimumRequiredVersion]);
        Assert.Equal("already_installed", tags[TelemetryConstants.Tags.SdkCheckResult]);
    }

    [Fact]
    public async Task EnsureSdkInstalledAsync_WhenSdkMissing_RecordsNotInstalledTelemetry()
    {
        using var fixture = new TelemetryFixture();

        var sdkInstaller = new TestDotNetSdkInstaller
        {
            CheckAsyncCallback = _ => (false, null, "9.0.302")
        };

        var interactionService = new TestInteractionService();

        var result = await SdkInstallHelper.EnsureSdkInstalledAsync(
            sdkInstaller,
            interactionService,
            fixture.Telemetry);

        Assert.False(result);
        Assert.NotNull(fixture.CapturedActivity);

        var tags = fixture.CapturedActivity.Tags.ToDictionary(t => t.Key, t => t.Value);
        Assert.Equal("(not found)", tags[TelemetryConstants.Tags.SdkDetectedVersion]);
        Assert.Equal("9.0.302", tags[TelemetryConstants.Tags.SdkMinimumRequiredVersion]);
        Assert.Equal("not_installed", tags[TelemetryConstants.Tags.SdkCheckResult]);
    }

    [Fact]
    public async Task EnsureSdkInstalledAsync_WhenSdkMissing_DisplaysError()
    {
        using var fixture = new TelemetryFixture();

        var sdkInstaller = new TestDotNetSdkInstaller
        {
            CheckAsyncCallback = _ => (false, "8.0.100", "9.0.302")
        };

        var interactionService = new TestInteractionService();

        var result = await SdkInstallHelper.EnsureSdkInstalledAsync(
            sdkInstaller,
            interactionService,
            fixture.Telemetry);

        Assert.False(result);
        Assert.NotNull(fixture.CapturedActivity);

        var tags = fixture.CapturedActivity.Tags.ToDictionary(t => t.Key, t => t.Value);
        Assert.Equal("8.0.100", tags[TelemetryConstants.Tags.SdkDetectedVersion]);
        Assert.Equal("9.0.302", tags[TelemetryConstants.Tags.SdkMinimumRequiredVersion]);
        Assert.Equal("not_installed", tags[TelemetryConstants.Tags.SdkCheckResult]);
    }
}
