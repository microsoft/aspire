// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Maui.Annotations;

namespace Aspire.Hosting.Tests;

/// <summary>
/// Tests for UnsupportedPlatformAnnotation behavior on MAUI platform resources.
/// </summary>
public class MauiUnsupportedPlatformTests
{
    [Fact]
    public void WindowsDevice_HasUnsupportedPlatformAnnotation_OnlyWhenNotOnWindows()
    {
        using var dir = new SharedTestTempDirectory();
        var tempFile = Path.Combine(dir.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent("net10.0-windows10.0.19041.0"));

        var appBuilder = DistributedApplication.CreateBuilder();
        var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
        var windows = maui.AddWindowsDevice();

        var annotation = windows.Resource.Annotations.OfType<UnsupportedPlatformAnnotation>().FirstOrDefault();
        if (OperatingSystem.IsWindows())
        {
            // Windows is supported on a Windows host, so no annotation should be added.
            Assert.Null(annotation);
        }
        else
        {
            Assert.NotNull(annotation);
            Assert.Contains("Windows", annotation.Reason);
        }
    }

    [Fact]
    public void MacCatalystDevice_HasUnsupportedPlatformAnnotation_OnlyWhenNotOnMac()
    {
        using var dir = new SharedTestTempDirectory();
        var tempFile = Path.Combine(dir.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent("net10.0-maccatalyst"));

        var appBuilder = DistributedApplication.CreateBuilder();
        var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
        var macCatalyst = maui.AddMacCatalystDevice();

        var annotation = macCatalyst.Resource.Annotations.OfType<UnsupportedPlatformAnnotation>().FirstOrDefault();
        if (OperatingSystem.IsMacOS())
        {
            // Mac Catalyst is supported on a macOS host, so no annotation should be added.
            Assert.Null(annotation);
        }
        else
        {
            Assert.NotNull(annotation);
            Assert.Contains("Mac Catalyst", annotation.Reason);
        }
    }

    [Fact]
    public void iOSDevice_HasUnsupportedPlatformAnnotation_OnlyWhenNotOnMac()
    {
        using var dir = new SharedTestTempDirectory();
        var tempFile = Path.Combine(dir.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent("net10.0-ios"));

        var appBuilder = DistributedApplication.CreateBuilder();
        var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
        var iosDevice = maui.AddiOSDevice();

        var annotation = iosDevice.Resource.Annotations.OfType<UnsupportedPlatformAnnotation>().FirstOrDefault();
        if (OperatingSystem.IsMacOS())
        {
            // iOS development is supported on a macOS host, so no annotation should be added.
            Assert.Null(annotation);
        }
        else
        {
            Assert.NotNull(annotation);
            Assert.Contains("iOS", annotation.Reason);
        }
    }

    [Fact]
    public void iOSSimulator_HasUnsupportedPlatformAnnotation_OnlyWhenNotOnMac()
    {
        using var dir = new SharedTestTempDirectory();
        var tempFile = Path.Combine(dir.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent("net10.0-ios"));

        var appBuilder = DistributedApplication.CreateBuilder();
        var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
        var iosSimulator = maui.AddiOSSimulator();

        var annotation = iosSimulator.Resource.Annotations.OfType<UnsupportedPlatformAnnotation>().FirstOrDefault();
        if (OperatingSystem.IsMacOS())
        {
            // iOS development is supported on a macOS host, so no annotation should be added.
            Assert.Null(annotation);
        }
        else
        {
            Assert.NotNull(annotation);
            Assert.Contains("iOS", annotation.Reason);
        }
    }

    [Fact]
    public void AndroidDevice_AlwaysSupported_NoUnsupportedAnnotation()
    {
        // Android is always allowed on all platforms (validation happens at dotnet run time)
        using var dir = new SharedTestTempDirectory();
        var tempFile = Path.Combine(dir.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent("net10.0-android"));

        var appBuilder = DistributedApplication.CreateBuilder();
        var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
        var androidDevice = maui.AddAndroidDevice();

        var annotation = androidDevice.Resource.Annotations.OfType<UnsupportedPlatformAnnotation>().FirstOrDefault();
        Assert.Null(annotation);
    }

    [Fact]
    public void AndroidEmulator_AlwaysSupported_NoUnsupportedAnnotation()
    {
        using var dir = new SharedTestTempDirectory();
        var tempFile = Path.Combine(dir.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent("net10.0-android"));

        var appBuilder = DistributedApplication.CreateBuilder();
        var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
        var androidEmulator = maui.AddAndroidEmulator();

        var annotation = androidEmulator.Resource.Annotations.OfType<UnsupportedPlatformAnnotation>().FirstOrDefault();
        Assert.Null(annotation);
    }

    [Fact]
    public void UnsupportedPlatformAnnotation_StoresReason()
    {
        var reason = "Test platform not available";
        var annotation = new UnsupportedPlatformAnnotation(reason);

        Assert.Equal(reason, annotation.Reason);
    }
}
