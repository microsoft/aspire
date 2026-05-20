// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;

namespace Aspire.Hosting.Tests;

/// <summary>
/// Tests that verify the command-line arguments generated for each MAUI platform.
/// </summary>
public class MauiWithArgsTests
{
    [Theory]
    [InlineData("net10.0-windows10.0.19041.0")]
    [InlineData("net10.0-windows10.0.22621.0")]
    public async Task WindowsDevice_Args_ContainRunAndTfm(string windowsTfm)
    {
        var projectContent = MauiTestHelper.CreateProjectContent(windowsTfm);
        var tempFile = MauiTestHelper.CreateTempProjectFile(projectContent);

        try
        {
            var appBuilder = DistributedApplication.CreateBuilder();
            var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
            var windows = maui.AddWindowsDevice();

            var args = await ArgumentEvaluator.GetArgumentListAsync(windows.Resource);

            Assert.Contains("run", args);
            Assert.Contains("-f", args);
            Assert.Contains(windowsTfm, args);
        }
        finally
        {
            MauiTestHelper.CleanupTempFile(tempFile);
        }
    }

    [Fact]
    public async Task MacCatalystDevice_Args_ContainRunTfmAndOpenArguments()
    {
        var projectContent = MauiTestHelper.CreateProjectContent("net10.0-maccatalyst");
        var tempFile = MauiTestHelper.CreateTempProjectFile(projectContent);

        try
        {
            var appBuilder = DistributedApplication.CreateBuilder();
            var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
            var macCatalyst = maui.AddMacCatalystDevice();

            var args = await ArgumentEvaluator.GetArgumentListAsync(macCatalyst.Resource);

            Assert.Contains("run", args);
            Assert.Contains("-f", args);
            Assert.Contains("net10.0-maccatalyst", args);
            Assert.Contains("-p:OpenArguments=-W", args);
        }
        finally
        {
            MauiTestHelper.CleanupTempFile(tempFile);
        }
    }

    [Fact]
    public async Task AndroidDevice_DefaultArgs_ContainRunTfmAndAdbTargetDevice()
    {
        var projectContent = MauiTestHelper.CreateProjectContent("net10.0-android");
        var tempFile = MauiTestHelper.CreateTempProjectFile(projectContent);

        try
        {
            var appBuilder = DistributedApplication.CreateBuilder();
            var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
            var androidDevice = maui.AddAndroidDevice();

            var args = await ArgumentEvaluator.GetArgumentListAsync(androidDevice.Resource);

            Assert.Contains("run", args);
            Assert.Contains("-f", args);
            Assert.Contains("net10.0-android", args);
            // Default (no device ID) should use -d flag for "only attached device"
            Assert.Contains("-p:AdbTarget=-d", args);
        }
        finally
        {
            MauiTestHelper.CleanupTempFile(tempFile);
        }
    }

    [Fact]
    public async Task AndroidDevice_WithDeviceId_ContainAdbTargetWithSerial()
    {
        var projectContent = MauiTestHelper.CreateProjectContent("net10.0-android");
        var tempFile = MauiTestHelper.CreateTempProjectFile(projectContent);

        try
        {
            var appBuilder = DistributedApplication.CreateBuilder();
            var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
            var androidDevice = maui.AddAndroidDevice("my-device", "abc12345");

            var args = await ArgumentEvaluator.GetArgumentListAsync(androidDevice.Resource);

            Assert.Contains("-p:AdbTarget=-s abc12345", args);
            Assert.DoesNotContain("-p:AdbTarget=-d", args);
        }
        finally
        {
            MauiTestHelper.CleanupTempFile(tempFile);
        }
    }

    [Fact]
    public async Task AndroidEmulator_DefaultArgs_ContainAdbTargetEmulator()
    {
        var projectContent = MauiTestHelper.CreateProjectContent("net10.0-android");
        var tempFile = MauiTestHelper.CreateTempProjectFile(projectContent);

        try
        {
            var appBuilder = DistributedApplication.CreateBuilder();
            var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
            var emulator = maui.AddAndroidEmulator();

            var args = await ArgumentEvaluator.GetArgumentListAsync(emulator.Resource);

            Assert.Contains("run", args);
            Assert.Contains("-f", args);
            Assert.Contains("net10.0-android", args);
            // Default (no emulator ID) should use -e flag for "only running emulator"
            Assert.Contains("-p:AdbTarget=-e", args);
        }
        finally
        {
            MauiTestHelper.CleanupTempFile(tempFile);
        }
    }

    [Fact]
    public async Task AndroidEmulator_WithEmulatorId_ContainAdbTargetWithSerial()
    {
        var projectContent = MauiTestHelper.CreateProjectContent("net10.0-android");
        var tempFile = MauiTestHelper.CreateTempProjectFile(projectContent);

        try
        {
            var appBuilder = DistributedApplication.CreateBuilder();
            var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
            var emulator = maui.AddAndroidEmulator("my-emulator", "emulator-5554");

            var args = await ArgumentEvaluator.GetArgumentListAsync(emulator.Resource);

            Assert.Contains("-p:AdbTarget=-s emulator-5554", args);
            Assert.DoesNotContain("-p:AdbTarget=-e", args);
        }
        finally
        {
            MauiTestHelper.CleanupTempFile(tempFile);
        }
    }

    [Fact]
    public async Task iOSDevice_DefaultArgs_ContainRuntimeIdentifier()
    {
        var projectContent = MauiTestHelper.CreateProjectContent("net10.0-ios");
        var tempFile = MauiTestHelper.CreateTempProjectFile(projectContent);

        try
        {
            var appBuilder = DistributedApplication.CreateBuilder();
            var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
            var device = maui.AddiOSDevice();

            var args = await ArgumentEvaluator.GetArgumentListAsync(device.Resource);

            Assert.Contains("run", args);
            Assert.Contains("-f", args);
            Assert.Contains("net10.0-ios", args);
            Assert.Contains("-p:RuntimeIdentifier=ios-arm64", args);
        }
        finally
        {
            MauiTestHelper.CleanupTempFile(tempFile);
        }
    }

    [Fact]
    public async Task iOSDevice_WithDeviceId_ContainDeviceName()
    {
        var projectContent = MauiTestHelper.CreateProjectContent("net10.0-ios");
        var tempFile = MauiTestHelper.CreateTempProjectFile(projectContent);

        try
        {
            var appBuilder = DistributedApplication.CreateBuilder();
            var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
            var device = maui.AddiOSDevice("my-device", "00008030-001234567890123A");

            var args = await ArgumentEvaluator.GetArgumentListAsync(device.Resource);

            Assert.Contains("-p:RuntimeIdentifier=ios-arm64", args);
            Assert.Contains("-p:_DeviceName=00008030-001234567890123A", args);
        }
        finally
        {
            MauiTestHelper.CleanupTempFile(tempFile);
        }
    }

    [Fact]
    public async Task iOSSimulator_DefaultArgs_DoNotContainDeviceName()
    {
        var projectContent = MauiTestHelper.CreateProjectContent("net10.0-ios");
        var tempFile = MauiTestHelper.CreateTempProjectFile(projectContent);

        try
        {
            var appBuilder = DistributedApplication.CreateBuilder();
            var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
            var simulator = maui.AddiOSSimulator();

            var args = await ArgumentEvaluator.GetArgumentListAsync(simulator.Resource);

            Assert.Contains("run", args);
            Assert.Contains("-f", args);
            Assert.Contains("net10.0-ios", args);
            // No device name when no simulator ID specified
            Assert.DoesNotContain(args, a => a.Contains("_DeviceName"));
        }
        finally
        {
            MauiTestHelper.CleanupTempFile(tempFile);
        }
    }

    [Fact]
    public async Task iOSSimulator_WithSimulatorId_ContainDeviceNameWithUdidPrefix()
    {
        var projectContent = MauiTestHelper.CreateProjectContent("net10.0-ios");
        var tempFile = MauiTestHelper.CreateTempProjectFile(projectContent);

        try
        {
            var appBuilder = DistributedApplication.CreateBuilder();
            var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
            var simulator = maui.AddiOSSimulator("my-simulator", "E25BBE37-69BA-4720-B6FD-D54C97791E79");

            var args = await ArgumentEvaluator.GetArgumentListAsync(simulator.Resource);

            Assert.Contains("-p:_DeviceName=:v2:udid=E25BBE37-69BA-4720-B6FD-D54C97791E79", args);
            // Simulator should NOT have RuntimeIdentifier=ios-arm64 (that's for devices only)
            Assert.DoesNotContain(args, a => a.Contains("RuntimeIdentifier=ios-arm64"));
        }
        finally
        {
            MauiTestHelper.CleanupTempFile(tempFile);
        }
    }

    [Fact]
    public async Task AllPlatforms_ArgsStartWithRun()
    {
        // Create a project with all platform TFMs
        var projectContent = """
            <Project Sdk="Microsoft.NET.Sdk">
                <PropertyGroup>
                    <TargetFrameworks>net10.0-windows10.0.19041.0;net10.0-maccatalyst;net10.0-android;net10.0-ios</TargetFrameworks>
                </PropertyGroup>
            </Project>
            """;
        var tempFile = MauiTestHelper.CreateTempProjectFile(projectContent);

        try
        {
            var appBuilder = DistributedApplication.CreateBuilder();
            var maui = appBuilder.AddMauiProject("mauiapp", tempFile);

            var platforms = new IResourceBuilder<IResource>[]
            {
                maui.AddWindowsDevice("win"),
                maui.AddMacCatalystDevice("mac"),
                maui.AddAndroidDevice("android-dev"),
                maui.AddAndroidEmulator("android-emu"),
                maui.AddiOSDevice("ios-dev"),
                maui.AddiOSSimulator("ios-sim"),
            };

            foreach (var platform in platforms)
            {
                var args = await ArgumentEvaluator.GetArgumentListAsync(platform.Resource);
                Assert.True(args.Count > 0, $"Expected args for {platform.Resource.Name}");
                Assert.Equal("run", args[0]);
            }
        }
        finally
        {
            MauiTestHelper.CleanupTempFile(tempFile);
        }
    }
}
