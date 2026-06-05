// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.InternalTesting;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Aspire.Cli.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Telemetry;

[SupportedOSPlatform("windows")]
public class WindowsInformationProviderTests
{
    [Fact]
    public async Task GetOrCreateDeviceId_WorksCorrectly()
    {
        Assert.SkipUnless(RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
            "Only supported on Windows.");

        // Arrange
        var provider = new WindowsMachineInformationProvider(NullLogger<WindowsMachineInformationProvider>.Instance);

        // Act
        var deviceId = await provider.GetOrCreateDeviceId().DefaultTimeout();

        // Assert
        Assert.NotNull(deviceId);
        Assert.NotEmpty(deviceId);

        // Verify it's persisted by calling again
        var deviceId2 = await provider.GetOrCreateDeviceId().DefaultTimeout();
        Assert.Equal(deviceId, deviceId2);
    }
}
