// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;

namespace Aspire.Hosting.Tests;

/// <summary>
/// Tests that verify the command-line arguments generated for each MAUI platform.
/// </summary>
public class MauiWithArgsTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData("net10.0-windows10.0.19041.0")]
    [InlineData("net10.0-windows10.0.22621.0")]
    public async Task WindowsDevice_Args_ContainRunAndTfm(string windowsTfm)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempFile = Path.Combine(workspace.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent(windowsTfm));

        var appBuilder = DistributedApplication.CreateBuilder();
        var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
        var windows = maui.AddWindowsDevice();

        var args = await ArgumentEvaluator.GetArgumentListAsync(windows.Resource);

        Assert.Contains("run", args);
        Assert.Contains("-f", args);
        Assert.Contains(windowsTfm, args);
    }

    [Fact]
    public async Task MacCatalystDevice_Args_ContainRunTfmAndOpenArguments()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempFile = Path.Combine(workspace.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent("net10.0-maccatalyst"));

        var appBuilder = DistributedApplication.CreateBuilder();
        var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
        var macCatalyst = maui.AddMacCatalystDevice();

        var args = await ArgumentEvaluator.GetArgumentListAsync(macCatalyst.Resource);

        Assert.Contains("run", args);
        Assert.Contains("-f", args);
        Assert.Contains("net10.0-maccatalyst", args);
        Assert.Contains("-p:OpenArguments=-W", args);
    }

    [Fact]
    public async Task AndroidDevice_DefaultArgs_ContainRunTfmAndAdbTargetDevice()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempFile = Path.Combine(workspace.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent("net10.0-android"));

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

    [Fact]
    public async Task AndroidDevice_WithDeviceId_ContainAdbTargetWithSerial()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempFile = Path.Combine(workspace.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent("net10.0-android"));

        var appBuilder = DistributedApplication.CreateBuilder();
        var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
        var androidDevice = maui.AddAndroidDevice("my-device", "abc12345");

        var args = await ArgumentEvaluator.GetArgumentListAsync(androidDevice.Resource);

        Assert.Contains("-p:AdbTarget=-s abc12345", args);
        Assert.DoesNotContain("-p:AdbTarget=-d", args);
    }

    [Fact]
    public async Task AndroidEmulator_DefaultArgs_DoNotContainStaticAdbTarget()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempFile = Path.Combine(workspace.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent("net10.0-android"));

        var appBuilder = DistributedApplication.CreateBuilder();
        var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
        var emulator = maui.AddAndroidEmulator();

        var args = await ArgumentEvaluator.GetArgumentListAsync(emulator.Resource);

        Assert.Contains("run", args);
        Assert.Contains("-f", args);
        Assert.Contains("net10.0-android", args);
        Assert.DoesNotContain(args, a => a.Contains("AdbTarget", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AndroidEmulator_WithEmulatorId_ContainAdbTargetWithSerial()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempFile = Path.Combine(workspace.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent("net10.0-android"));

        var appBuilder = DistributedApplication.CreateBuilder();
        var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
        var emulator = maui.AddAndroidEmulator("my-emulator", "emulator-5554");

        var args = await ArgumentEvaluator.GetArgumentListAsync(emulator.Resource);

        Assert.Contains("-p:AdbTarget=-s emulator-5554", args);
        Assert.DoesNotContain("-p:AdbTarget=-e", args);
    }

    [Fact]
    public async Task iOSDevice_DefaultArgs_ContainRuntimeIdentifier()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempFile = Path.Combine(workspace.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent("net10.0-ios"));

        var appBuilder = DistributedApplication.CreateBuilder();
        var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
        var device = maui.AddiOSDevice();

        var args = await ArgumentEvaluator.GetArgumentListAsync(device.Resource);

        Assert.Contains("run", args);
        Assert.Contains("-f", args);
        Assert.Contains("net10.0-ios", args);
        Assert.Contains("-p:RuntimeIdentifier=ios-arm64", args);
    }

    [Fact]
    public async Task iOSDevice_WithDeviceId_ContainDeviceName()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempFile = Path.Combine(workspace.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent("net10.0-ios"));

        var appBuilder = DistributedApplication.CreateBuilder();
        var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
        var device = maui.AddiOSDevice("my-device", "00008030-001234567890123A");

        var args = await ArgumentEvaluator.GetArgumentListAsync(device.Resource);

        Assert.Contains("-p:RuntimeIdentifier=ios-arm64", args);
        Assert.Contains("-p:_DeviceName=00008030-001234567890123A", args);
    }

    [Fact]
    public async Task iOSSimulator_DefaultArgs_DoNotContainDeviceName()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempFile = Path.Combine(workspace.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent("net10.0-ios"));

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

    [Fact]
    public async Task iOSSimulator_WithSimulatorId_ContainDeviceNameWithUdidPrefix()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempFile = Path.Combine(workspace.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent("net10.0-ios"));

        var appBuilder = DistributedApplication.CreateBuilder();
        var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
        var simulator = maui.AddiOSSimulator("my-simulator", "E25BBE37-69BA-4720-B6FD-D54C97791E79");

        var args = await ArgumentEvaluator.GetArgumentListAsync(simulator.Resource);

        Assert.Contains("-p:_DeviceName=:v2:udid=E25BBE37-69BA-4720-B6FD-D54C97791E79", args);
        // Simulator should NOT have RuntimeIdentifier=ios-arm64 (that's for devices only)
        Assert.DoesNotContain(args, a => a.Contains("RuntimeIdentifier=ios-arm64"));
    }

    [Fact]
    public async Task AllPlatforms_ArgsStartWithRunAndHaveExpectedTfm()
    {
        // Create a project with all platform TFMs. Because the project is multi-TFM, this is the
        // only test that exercises GetPlatformTargetFramework picking the correct entry out of a
        // ';'-joined list. The per-platform tests above use single-TFM projects, so asserting the
        // exact '-f <tfm>' here catches a regression that returns the whole list or the wrong TFM.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempFile = Path.Combine(workspace.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, """
            <Project Sdk="Microsoft.NET.Sdk">
                <PropertyGroup>
                    <TargetFrameworks>net10.0-windows10.0.19041.0;net10.0-maccatalyst;net10.0-android;net10.0-ios</TargetFrameworks>
                </PropertyGroup>
            </Project>
            """);

        var appBuilder = DistributedApplication.CreateBuilder();
        var maui = appBuilder.AddMauiProject("mauiapp", tempFile);

        var platforms = new (IResourceBuilder<IResource> Builder, string ExpectedTfm)[]
        {
            (maui.AddWindowsDevice("win"), "net10.0-windows10.0.19041.0"),
            (maui.AddMacCatalystDevice("mac"), "net10.0-maccatalyst"),
            (maui.AddAndroidDevice("android-dev"), "net10.0-android"),
            (maui.AddAndroidEmulator("android-emu"), "net10.0-android"),
            (maui.AddiOSDevice("ios-dev"), "net10.0-ios"),
            (maui.AddiOSSimulator("ios-sim"), "net10.0-ios"),
        };

        foreach (var (builder, expectedTfm) in platforms)
        {
            var args = await ArgumentEvaluator.GetArgumentListAsync(builder.Resource);
            Assert.True(args.Count > 0, $"Expected args for {builder.Resource.Name}");
            Assert.Equal("run", args[0]);
            AssertTfm(args, expectedTfm);
        }

        static void AssertTfm(IReadOnlyList<string> args, string expectedTfm)
        {
            var i = args.ToList().IndexOf("-f");
            Assert.True(i >= 0 && i + 1 < args.Count, "expected -f <tfm>");
            Assert.Equal(expectedTfm, args[i + 1]);
        }
    }
}
