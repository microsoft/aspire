// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Maui.Utilities;

namespace Aspire.Hosting.Maui.Tests;

public class MauiAndroidDeviceSelectionTests
{
    private const string DevicesJson = """
        {
          "Items": {
            "Devices": [
              { "Identity": "emulator-5554", "Type": "Emulator", "Status": "Online" },
              { "Identity": "emulator-5556", "Type": "Emulator", "Status": "Offline" },
              { "Identity": "0A041FDD400327", "Type": "Device", "Status": "Online" },
              { "Identity": "Pixel_7_API_35", "Type": "Emulator", "Status": "NotRunning" }
            ]
          }
        }
        """;

    [Theory]
    [InlineData("-s emulator-5554", "emulator-5554")]
    [InlineData("-s 192.168.1.100:5555", "192.168.1.100:5555")]
    [InlineData("-e", null)]
    [InlineData("-d", null)]
    [InlineData("-s", null)]
    public void GetSerial_ReturnsSerialOnlyForSpecificTarget(string adbTarget, string? expectedSerial)
    {
        Assert.Equal(expectedSerial, MauiAndroidDeviceSelection.GetSerial(adbTarget));
    }

    [Theory]
    [InlineData("-e", "Emulator")]
    [InlineData("-d", "Device")]
    [InlineData("-s emulator-5554", null)]
    public void GetDeviceType_MapsAdbSwitches(string adbTarget, string? expectedType)
    {
        Assert.Equal(expectedType, MauiAndroidDeviceSelection.GetDeviceType(adbTarget));
    }

    [Fact]
    public void GetOnlineDevices_ReturnsOnlyOnlineDevicesOfType()
    {
        Assert.Equal(["emulator-5554"], MauiAndroidDeviceSelection.GetOnlineDevices(DevicesJson, MauiAndroidDeviceSelection.EmulatorType));
        Assert.Equal(["0A041FDD400327"], MauiAndroidDeviceSelection.GetOnlineDevices(DevicesJson, MauiAndroidDeviceSelection.DeviceType));
    }

    [Fact]
    public void GetOnlineDevices_IgnoresOutputBeforeJson()
    {
        var output = "warning NU1903: vulnerable package" + Environment.NewLine + DevicesJson;

        Assert.Equal(["emulator-5554"], MauiAndroidDeviceSelection.GetOnlineDevices(output, MauiAndroidDeviceSelection.EmulatorType));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{ \"Items\": { } }")]
    [InlineData("{ \"Items\": { \"Devices\": [ ")]
    public void GetOnlineDevices_ReturnsEmptyForMissingOrInvalidOutput(string output)
    {
        Assert.Empty(MauiAndroidDeviceSelection.GetOnlineDevices(output, MauiAndroidDeviceSelection.EmulatorType));
    }
}
