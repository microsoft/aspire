// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Maui;
using Aspire.Hosting.Maui.Utilities;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding Android platform resources to MAUI projects.
/// </summary>
public static class MauiAndroidExtensions
{
    /// <summary>
    /// Adds an Android physical device resource to run the MAUI application on an Android device.
    /// </summary>
    /// <param name="builder">The MAUI project resource builder.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// This method creates a new Android device platform resource that will run the MAUI application
    /// targeting the Android platform using <c>dotnet run</c>. The resource does not auto-start 
    /// and must be explicitly started from the dashboard by clicking the start button.
    /// <para>
    /// The resource name will default to "{projectName}-android-device".
    /// </para>
    /// <para>
    /// This will run the application on a physical Android device connected via USB/WiFi debugging.
    /// If only one device is attached, it will automatically use that device. If multiple devices
    /// are attached, use the overload with deviceId parameter to specify which device to use.
    /// Make sure an Android device is connected and visible via <c>adb devices</c>.
    /// </para>
    /// <para>
    /// This overload is not available in polyglot app hosts. Use <see cref="AddAndroidDevice(IResourceBuilder{MauiProjectResource}, string, string)"/> instead.
    /// </para>
    /// </remarks>
    /// <example>
    /// Add an Android device to a MAUI project:
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    /// 
    /// var maui = builder.AddMauiProject("mauiapp", "../MyMauiApp/MyMauiApp.csproj");
    /// var androidDevice = maui.AddAndroidDevice();
    /// 
    /// builder.Build().Run();
    /// </code>
    /// </example>
    [AspireExportIgnore(Reason = "Convenience overload. Use the overload with name and optional device ID instead.")]
    public static IResourceBuilder<MauiAndroidDeviceResource> AddAndroidDevice(
        this IResourceBuilder<MauiProjectResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var name = $"{builder.Resource.Name}-android-device";
        return builder.AddAndroidDevice(name, deviceId: null);
    }

    /// <summary>
    /// Adds an Android physical device resource to run the MAUI application on an Android device with a specific name.
    /// </summary>
    /// <param name="builder">The MAUI project resource builder.</param>
    /// <param name="name">The name of the Android device resource.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// This method creates a new Android device platform resource that will run the MAUI application
    /// targeting the Android platform using <c>dotnet run</c>. The resource does not auto-start 
    /// and must be explicitly started from the dashboard by clicking the start button.
    /// <para>
    /// Multiple Android device resources can be added to the same MAUI project if needed, each with
    /// a unique name.
    /// </para>
    /// <para>
    /// This will run the application on a physical Android device connected via USB/WiFi debugging.
    /// If only one device is attached, it will automatically use that device. If multiple devices
    /// are attached, use the overload with deviceId parameter to specify which device to use.
    /// Make sure an Android device is connected and visible via <c>adb devices</c>.
    /// </para>
    /// <para>
    /// This overload is not available in polyglot app hosts. Use <see cref="AddAndroidDevice(IResourceBuilder{MauiProjectResource}, string, string)"/> instead.
    /// </para>
    /// </remarks>
    /// <example>
    /// Add multiple Android devices to a MAUI project:
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    /// 
    /// var maui = builder.AddMauiProject("mauiapp", "../MyMauiApp/MyMauiApp.csproj");
    /// var device1 = maui.AddAndroidDevice("android-device-1");
    /// var device2 = maui.AddAndroidDevice("android-device-2");
    /// 
    /// builder.Build().Run();
    /// </code>
    /// </example>
    [AspireExportIgnore(Reason = "Convenience overload. Use the overload with the optional device ID parameter instead.")]
    public static IResourceBuilder<MauiAndroidDeviceResource> AddAndroidDevice(
        this IResourceBuilder<MauiProjectResource> builder,
        [ResourceName] string name)
    {
        return builder.AddAndroidDevice(name, deviceId: null);
    }

    /// <summary>
    /// Adds an Android physical device resource to run the MAUI application on an Android device with a specific name and device ID.
    /// </summary>
    /// <param name="builder">The MAUI project resource builder.</param>
    /// <param name="name">The name of the Android device resource.</param>
    /// <param name="deviceId">Optional device ID to target a specific Android device. If not specified, uses the only attached device (requires exactly one device to be connected).</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// This method creates a new Android device platform resource that will run the MAUI application
    /// targeting the Android platform using <c>dotnet run</c>. The resource does not auto-start 
    /// and must be explicitly started from the dashboard by clicking the start button.
    /// <para>
    /// Multiple Android device resources can be added to the same MAUI project if needed, each with
    /// a unique name.
    /// </para>
    /// <para>
    /// This will run the application on a physical Android device connected via USB/WiFi debugging.
    /// Make sure an Android device is connected and visible via <c>adb devices</c>.
    /// </para>
    /// <para>
    /// To target a specific device when multiple are attached, provide the device ID (e.g., "abc12345" or "192.168.1.100:5555" for WiFi debugging).
    /// Use <c>adb devices</c> to list available device IDs.
    /// </para>
    /// </remarks>
    /// <example>
    /// Add multiple Android devices to a MAUI project:
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    /// 
    /// var maui = builder.AddMauiProject("mauiapp", "../MyMauiApp/MyMauiApp.csproj");
    /// 
    /// // Default device (only one attached)
    /// var device1 = maui.AddAndroidDevice("android-device-default");
    /// 
    /// // Specific device by serial number
    /// var device2 = maui.AddAndroidDevice("android-device-pixel", "abc12345");
    /// 
    /// // WiFi debugging device
    /// var device3 = maui.AddAndroidDevice("android-device-wifi", "192.168.1.100:5555");
    /// 
    /// builder.Build().Run();
    /// </code>
    /// </example>
    [AspireExport("addAndroidDevice", Description = "Adds an Android device resource for a .NET MAUI project.")]
    public static IResourceBuilder<MauiAndroidDeviceResource> AddAndroidDevice(
        this IResourceBuilder<MauiProjectResource> builder,
        [ResourceName] string name,
        string? deviceId = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // Get the absolute project path and working directory
        var (projectPath, workingDirectory) = MauiPlatformHelper.GetProjectPaths(builder);

        var androidDeviceResource = new MauiAndroidDeviceResource(name, builder.Resource);

        var resourceBuilder = builder.ApplicationBuilder.AddResource(androidDeviceResource)
            .WithAnnotation(new MauiProjectMetadata(projectPath))
            .WithAnnotation(new MauiAndroidEnvironmentAnnotation()) // Enable environment variable support via targets file
            .WithAnnotation(new ExecutableAnnotation
            {
                Command = "dotnet",
                WorkingDirectory = workingDirectory
            });

        // Build additional arguments for device ID if specified
        // For Android devices, we need to use the MSBuild property AdbTarget to specify which device to target
        // See: https://learn.microsoft.com/dotnet/maui/whats-new/dotnet-10#dotnet-run-support
        // Valid formats:
        //   -p:AdbTarget=-d               (run on only attached device)
        //   -p:AdbTarget=-s abc12345      (run on specific device by serial)
        var additionalArgs = new List<string>();
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            // Specific device - use -s prefix (no quotes around the value)
            additionalArgs.Add($"-p:AdbTarget=-s {deviceId}");
        }
        else
        {
            // No specific device ID - use -d to target the only attached device
            additionalArgs.Add("-p:AdbTarget=-d");
        }

        // Configure the platform resource with common settings
        // Android runs on Windows, macOS, and Linux - check for Android SDK/tooling availability is complex
        // For now, allow on all platforms and let dotnet run fail gracefully if Android SDK is not available
        MauiPlatformHelper.ConfigurePlatformResource(
            resourceBuilder,
            projectPath,
            "android",
            "Android",
            "net10.0-android",
            () => true, // Allow on all platforms, validation happens at dotnet run time
            "PhoneTablet",
            additionalArgs.ToArray());

        return resourceBuilder;
    }

    /// <summary>
    /// Adds an Android emulator resource to run the MAUI application on an Android emulator.
    /// </summary>
    /// <param name="builder">The MAUI project resource builder.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// This method creates a new Android emulator platform resource that will run the MAUI application
    /// targeting the Android platform using <c>dotnet run</c>. The resource does not auto-start 
    /// and must be explicitly started from the dashboard by clicking the start button.
    /// <para>
    /// The resource name will default to "{projectName}-android-emulator".
    /// </para>
    /// <para>
    /// This will run the application on an Android emulator. Make sure you have created an Android
    /// Virtual Device (AVD) using Android Studio or <c>avdmanager</c>. The emulator should be running
    /// and visible via <c>adb devices</c>.
    /// </para>
    /// <para>
    /// To target a specific emulator, use the overload that accepts an emulatorId parameter.
    /// </para>
    /// <para>
    /// This overload is not available in polyglot app hosts. Use <see cref="AddAndroidEmulator(IResourceBuilder{MauiProjectResource}, string, string)"/> instead.
    /// </para>
    /// </remarks>
    /// <example>
    /// Add an Android emulator to a MAUI project:
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    /// 
    /// var maui = builder.AddMauiProject("mauiapp", "../MyMauiApp/MyMauiApp.csproj");
    /// 
    /// // Uses default/running emulator
    /// var defaultEmulator = maui.AddAndroidEmulator();
    /// 
    /// builder.Build().Run();
    /// </code>
    /// </example>
    [AspireExportIgnore(Reason = "Convenience overload. Use the overload with name and optional emulator ID instead.")]
    public static IResourceBuilder<MauiAndroidEmulatorResource> AddAndroidEmulator(
        this IResourceBuilder<MauiProjectResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var name = $"{builder.Resource.Name}-android-emulator";
        return builder.AddAndroidEmulator(name, emulatorId: null);
    }

    /// <summary>
    /// Adds an Android emulator resource to run the MAUI application on an Android emulator with a specific name.
    /// </summary>
    /// <param name="builder">The MAUI project resource builder.</param>
    /// <param name="name">The name of the Android emulator resource.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// This method creates a new Android emulator platform resource that will run the MAUI application
    /// targeting the Android platform using <c>dotnet run</c>. The resource does not auto-start 
    /// and must be explicitly started from the dashboard by clicking the start button.
    /// <para>
    /// Multiple Android emulator resources can be added to the same MAUI project if needed, each with
    /// a unique name.
    /// </para>
    /// <para>
    /// This will run the application on an Android emulator. Make sure you have created an Android
    /// Virtual Device (AVD) using Android Studio or <c>avdmanager</c>. The emulator should be running
    /// and visible via <c>adb devices</c>.
    /// </para>
    /// <para>
    /// To target a specific emulator, use the overload that accepts an emulatorId parameter.
    /// </para>
    /// <para>
    /// This overload is not available in polyglot app hosts. Use <see cref="AddAndroidEmulator(IResourceBuilder{MauiProjectResource}, string, string)"/> instead.
    /// </para>
    /// </remarks>
    /// <example>
    /// Add multiple Android emulators to a MAUI project:
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    /// 
    /// var maui = builder.AddMauiProject("mauiapp", "../MyMauiApp/MyMauiApp.csproj");
    /// var emulator1 = maui.AddAndroidEmulator("android-emulator-1");
    /// var emulator2 = maui.AddAndroidEmulator("android-emulator-2");
    /// 
    /// builder.Build().Run();
    /// </code>
    /// </example>
    [AspireExportIgnore(Reason = "Convenience overload. Use the overload with the optional emulator ID parameter instead.")]
    public static IResourceBuilder<MauiAndroidEmulatorResource> AddAndroidEmulator(
        this IResourceBuilder<MauiProjectResource> builder,
        [ResourceName] string name)
    {
        return builder.AddAndroidEmulator(name, emulatorId: null);
    }

    /// <summary>
    /// Adds an Android emulator resource to run the MAUI application on an Android emulator with a specific name.
    /// </summary>
    /// <param name="builder">The MAUI project resource builder.</param>
    /// <param name="name">The name of the Android emulator resource.</param>
    /// <param name="emulatorId">Optional emulator ID to target a specific Android emulator. If not specified, uses the currently running emulator or starts the default emulator.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// This method creates a new Android emulator platform resource that will run the MAUI application
    /// targeting the Android platform using <c>dotnet run</c>. The resource does not auto-start 
    /// and must be explicitly started from the dashboard by clicking the start button.
    /// <para>
    /// Multiple Android emulator resources can be added to the same MAUI project if needed, each with
    /// a unique name.
    /// </para>
    /// <para>
    /// This will run the application on an Android emulator. Make sure you have created an Android
    /// Virtual Device (AVD) using Android Studio or <c>avdmanager</c>. The emulator should be running
    /// and visible via <c>adb devices</c>.
    /// </para>
    /// <para>
    /// To target a specific emulator, provide the emulator ID (e.g., "Pixel_5_API_33" or "emulator-5554").
    /// Use <c>adb devices</c> to list available emulator IDs.
    /// </para>
    /// </remarks>
    /// <example>
    /// Add multiple Android emulators to a MAUI project:
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    /// 
    /// var maui = builder.AddMauiProject("mauiapp", "../MyMauiApp/MyMauiApp.csproj");
    /// 
    /// // Default emulator
    /// var emulator1 = maui.AddAndroidEmulator("android-emulator-default");
    /// 
    /// // Specific Pixel 5 emulator
    /// var emulator2 = maui.AddAndroidEmulator("android-emulator-pixel5", "Pixel_5_API_33");
    /// 
    /// // Specific emulator by serial
    /// var emulator3 = maui.AddAndroidEmulator("android-emulator-5554", "emulator-5554");
    /// 
    /// builder.Build().Run();
    /// </code>
    /// </example>
    [AspireExport("addAndroidEmulator", Description = "Adds an Android emulator resource for a .NET MAUI project.")]
    public static IResourceBuilder<MauiAndroidEmulatorResource> AddAndroidEmulator(
        this IResourceBuilder<MauiProjectResource> builder,
        [ResourceName] string name,
        string? emulatorId = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // Get the absolute project path and working directory
        var (projectPath, workingDirectory) = MauiPlatformHelper.GetProjectPaths(builder);

        var androidEmulatorResource = new MauiAndroidEmulatorResource(name, builder.Resource);

        var resourceBuilder = builder.ApplicationBuilder.AddResource(androidEmulatorResource)
            .WithAnnotation(new MauiProjectMetadata(projectPath))
            .WithAnnotation(new MauiAndroidEnvironmentAnnotation()) // Enable environment variable support via targets file
            .WithAnnotation(new ExecutableAnnotation
            {
                Command = "dotnet",
                WorkingDirectory = workingDirectory
            });

        // Build additional arguments for emulator ID if specified
        // For Android, we need to use the MSBuild property AdbTarget to specify which device/emulator to target
        // See: https://learn.microsoft.com/dotnet/maui/whats-new/dotnet-10#dotnet-run-support
        // Valid formats:
        //   -p:AdbTarget=-e               (run on only running emulator)
        //   -p:AdbTarget=-s emulator-5554 (run on specific emulator/device by serial)
        var additionalArgs = new List<string>();
        if (!string.IsNullOrWhiteSpace(emulatorId))
        {
            // Specific emulator - use -s prefix (no quotes around the value)
            additionalArgs.Add($"-p:AdbTarget=-s {emulatorId}");
        }
        else
        {
            // No specific emulator ID - use -e to target the only running emulator
            additionalArgs.Add("-p:AdbTarget=-e");
        }

        // Configure the platform resource with common settings
        // Android runs on Windows, macOS, and Linux - check for Android SDK/tooling availability is complex
        // For now, allow on all platforms and let dotnet run fail gracefully if Android SDK is not available
        MauiPlatformHelper.ConfigurePlatformResource(
            resourceBuilder,
            projectPath,
            "android",
            "Android",
            "net10.0-android",
            () => true, // Allow on all platforms, validation happens at dotnet run time
            "PhoneTablet",
            additionalArgs.ToArray());

        return resourceBuilder;
    }
}
