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
    public void WindowsDevice_OnNonWindows_HasUnsupportedPlatformAnnotation()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("On Windows, the UnsupportedPlatformAnnotation should not be added");
            return;
        }

        var projectContent = MauiTestHelper.CreateProjectContent("net10.0-windows10.0.19041.0");
        var tempFile = MauiTestHelper.CreateTempProjectFile(projectContent);

        try
        {
            var appBuilder = DistributedApplication.CreateBuilder();
            var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
            var windows = maui.AddWindowsDevice();

            var annotation = windows.Resource.Annotations.OfType<UnsupportedPlatformAnnotation>().FirstOrDefault();
            Assert.NotNull(annotation);
            Assert.Contains("Windows", annotation.Reason);
        }
        finally
        {
            MauiTestHelper.CleanupTempFile(tempFile);
        }
    }

    [Fact]
    public void MacCatalystDevice_OnNonMac_HasUnsupportedPlatformAnnotation()
    {
        if (OperatingSystem.IsMacOS())
        {
            Assert.Skip("On macOS, the UnsupportedPlatformAnnotation should not be added");
            return;
        }

        var projectContent = MauiTestHelper.CreateProjectContent("net10.0-maccatalyst");
        var tempFile = MauiTestHelper.CreateTempProjectFile(projectContent);

        try
        {
            var appBuilder = DistributedApplication.CreateBuilder();
            var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
            var macCatalyst = maui.AddMacCatalystDevice();

            var annotation = macCatalyst.Resource.Annotations.OfType<UnsupportedPlatformAnnotation>().FirstOrDefault();
            Assert.NotNull(annotation);
            Assert.Contains("Mac Catalyst", annotation.Reason);
        }
        finally
        {
            MauiTestHelper.CleanupTempFile(tempFile);
        }
    }

    [Fact]
    public void iOSDevice_OnNonMac_HasUnsupportedPlatformAnnotation()
    {
        if (OperatingSystem.IsMacOS())
        {
            Assert.Skip("On macOS, the UnsupportedPlatformAnnotation should not be added");
            return;
        }

        var projectContent = MauiTestHelper.CreateProjectContent("net10.0-ios");
        var tempFile = MauiTestHelper.CreateTempProjectFile(projectContent);

        try
        {
            var appBuilder = DistributedApplication.CreateBuilder();
            var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
            var iosDevice = maui.AddiOSDevice();

            var annotation = iosDevice.Resource.Annotations.OfType<UnsupportedPlatformAnnotation>().FirstOrDefault();
            Assert.NotNull(annotation);
            Assert.Contains("iOS", annotation.Reason);
        }
        finally
        {
            MauiTestHelper.CleanupTempFile(tempFile);
        }
    }

    [Fact]
    public void iOSSimulator_OnNonMac_HasUnsupportedPlatformAnnotation()
    {
        if (OperatingSystem.IsMacOS())
        {
            Assert.Skip("On macOS, the UnsupportedPlatformAnnotation should not be added");
            return;
        }

        var projectContent = MauiTestHelper.CreateProjectContent("net10.0-ios");
        var tempFile = MauiTestHelper.CreateTempProjectFile(projectContent);

        try
        {
            var appBuilder = DistributedApplication.CreateBuilder();
            var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
            var iosSimulator = maui.AddiOSSimulator();

            var annotation = iosSimulator.Resource.Annotations.OfType<UnsupportedPlatformAnnotation>().FirstOrDefault();
            Assert.NotNull(annotation);
            Assert.Contains("iOS", annotation.Reason);
        }
        finally
        {
            MauiTestHelper.CleanupTempFile(tempFile);
        }
    }

    [Fact]
    public void AndroidDevice_AlwaysSupported_NoUnsupportedAnnotation()
    {
        // Android is always allowed on all platforms (validation happens at dotnet run time)
        var projectContent = MauiTestHelper.CreateProjectContent("net10.0-android");
        var tempFile = MauiTestHelper.CreateTempProjectFile(projectContent);

        try
        {
            var appBuilder = DistributedApplication.CreateBuilder();
            var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
            var androidDevice = maui.AddAndroidDevice();

            var annotation = androidDevice.Resource.Annotations.OfType<UnsupportedPlatformAnnotation>().FirstOrDefault();
            Assert.Null(annotation);
        }
        finally
        {
            MauiTestHelper.CleanupTempFile(tempFile);
        }
    }

    [Fact]
    public void AndroidEmulator_AlwaysSupported_NoUnsupportedAnnotation()
    {
        var projectContent = MauiTestHelper.CreateProjectContent("net10.0-android");
        var tempFile = MauiTestHelper.CreateTempProjectFile(projectContent);

        try
        {
            var appBuilder = DistributedApplication.CreateBuilder();
            var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
            var androidEmulator = maui.AddAndroidEmulator();

            var annotation = androidEmulator.Resource.Annotations.OfType<UnsupportedPlatformAnnotation>().FirstOrDefault();
            Assert.Null(annotation);
        }
        finally
        {
            MauiTestHelper.CleanupTempFile(tempFile);
        }
    }

    [Fact]
    public void UnsupportedPlatformAnnotation_StoresReason()
    {
        var reason = "Test platform not available";
        var annotation = new UnsupportedPlatformAnnotation(reason);

        Assert.Equal(reason, annotation.Reason);
    }
}
