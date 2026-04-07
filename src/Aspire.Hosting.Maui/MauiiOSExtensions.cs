// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Maui;
using Aspire.Hosting.Maui.Utilities;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding iOS platform resources to MAUI projects.
/// </summary>
public static class MauiiOSExtensions
{
    /// <summary>
    /// Adds an iOS physical device resource to run the MAUI application on an iOS device.
    /// </summary>
    /// <param name="builder">The MAUI project resource builder.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// This method creates a new iOS device platform resource that will run the MAUI application
    /// targeting the iOS platform using <c>dotnet run</c>. The resource does not auto-start 
    /// and must be explicitly started from the dashboard by clicking the start button.
    /// <para>
    /// The resource name will default to "{projectName}-ios-device".
    /// </para>
    /// <para>
    /// This will run the application on a physical iOS device connected via USB.
    /// The device must be provisioned before deployment. For more information, see 
    /// https://learn.microsoft.com/dotnet/maui/ios/device-provisioning
    /// </para>
    /// <para>
    /// If only one device is attached, it will automatically use that device. If multiple devices
    /// are connected, use the overload with deviceId parameter to specify which device to use by UDID.
    /// You can find the device UDID in Xcode under Window > Devices and Simulators > Devices tab.
    /// </para>
    /// <para>
    /// This overload is not available in polyglot app hosts. Use <see cref="AddiOSDevice(IResourceBuilder{MauiProjectResource}, string, string)"/> instead.
    /// </para>
    /// </remarks>
    /// <example>
    /// Add an iOS device to a MAUI project:
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    /// 
    /// var maui = builder.AddMauiProject("mauiapp", "../MyMauiApp/MyMauiApp.csproj");
    /// var iOSDevice = maui.AddiOSDevice();
    /// 
    /// builder.Build().Run();
    /// </code>
    /// </example>
    [AspireExportIgnore(Reason = "Convenience overload. Use the overload with name and optional device UDID instead.")]
    public static IResourceBuilder<MauiiOSDeviceResource> AddiOSDevice(
        this IResourceBuilder<MauiProjectResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var name = $"{builder.Resource.Name}-ios-device";
        return builder.AddiOSDevice(name, deviceId: null);
    }

    /// <summary>
    /// Adds an iOS physical device resource to run the MAUI application on an iOS device with a specific name.
    /// </summary>
    /// <param name="builder">The MAUI project resource builder.</param>
    /// <param name="name">The name of the iOS device resource.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// This method creates a new iOS device platform resource that will run the MAUI application
    /// targeting the iOS platform using <c>dotnet run</c>. The resource does not auto-start 
    /// and must be explicitly started from the dashboard by clicking the start button.
    /// <para>
    /// Multiple iOS device resources can be added to the same MAUI project if needed, each with
    /// a unique name.
    /// </para>
    /// <para>
    /// This will run the application on a physical iOS device connected via USB.
    /// The device must be provisioned before deployment. For more information, see 
    /// https://learn.microsoft.com/dotnet/maui/ios/device-provisioning
    /// </para>
    /// <para>
    /// If only one device is attached, it will automatically use that device. If multiple devices
    /// are connected, use the overload with deviceId parameter to specify which device to use by UDID.
    /// You can find the device UDID in Xcode under Window > Devices and Simulators > Devices tab.
    /// </para>
    /// <para>
    /// This overload is not available in polyglot app hosts. Use <see cref="AddiOSDevice(IResourceBuilder{MauiProjectResource}, string, string)"/> instead.
    /// </para>
    /// </remarks>
    /// <example>
    /// Add multiple iOS devices to a MAUI project:
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    /// 
    /// var maui = builder.AddMauiProject("mauiapp", "../MyMauiApp/MyMauiApp.csproj");
    /// var device1 = maui.AddiOSDevice("ios-device-1");
    /// var device2 = maui.AddiOSDevice("ios-device-2");
    /// 
    /// builder.Build().Run();
    /// </code>
    /// </example>
    [AspireExportIgnore(Reason = "Convenience overload. Use the overload with the optional device UDID parameter instead.")]
    public static IResourceBuilder<MauiiOSDeviceResource> AddiOSDevice(
        this IResourceBuilder<MauiProjectResource> builder,
        [ResourceName] string name)
    {
        return builder.AddiOSDevice(name, deviceId: null);
    }

    /// <summary>
    /// Adds an iOS physical device resource to run the MAUI application on an iOS device with a specific name and device UDID.
    /// </summary>
    /// <param name="builder">The MAUI project resource builder.</param>
    /// <param name="name">The name of the iOS device resource.</param>
    /// <param name="deviceId">Optional device UDID to target a specific iOS device. If not specified, uses the only attached device (requires exactly one device to be connected).</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// This method creates a new iOS device platform resource that will run the MAUI application
    /// targeting the iOS platform using <c>dotnet run</c>. The resource does not auto-start 
    /// and must be explicitly started from the dashboard by clicking the start button.
    /// <para>
    /// Multiple iOS device resources can be added to the same MAUI project if needed, each with
    /// a unique name.
    /// </para>
    /// <para>
    /// This will run the application on a physical iOS device connected via USB.
    /// The device must be provisioned before deployment. For more information, see 
    /// https://learn.microsoft.com/dotnet/maui/ios/device-provisioning
    /// </para>
    /// <para>
    /// To target a specific device when multiple are connected, provide the device UDID.
    /// You can find the device UDID in Xcode under Window > Devices and Simulators > Devices tab,
    /// or right-click on the device and select "Copy Identifier".
    /// </para>
    /// </remarks>
    /// <example>
    /// Add multiple iOS devices to a MAUI project:
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    /// 
    /// var maui = builder.AddMauiProject("mauiapp", "../MyMauiApp/MyMauiApp.csproj");
    /// 
    /// // Default device (only one attached)
    /// var device1 = maui.AddiOSDevice("ios-device-default");
    /// 
    /// // Specific device by UDID
    /// var device2 = maui.AddiOSDevice("ios-device-iphone13", "00008030-001234567890123A");
    /// 
    /// builder.Build().Run();
    /// </code>
    /// </example>
    [AspireExport(Description = "Adds an iOS device resource for a .NET MAUI project.")]
    public static IResourceBuilder<MauiiOSDeviceResource> AddiOSDevice(
        this IResourceBuilder<MauiProjectResource> builder,
        [ResourceName] string name,
        string? deviceId = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // Get the absolute project path and working directory
        var (projectPath, workingDirectory) = MauiPlatformHelper.GetProjectPaths(builder);

        var iOSDeviceResource = new MauiiOSDeviceResource(name, builder.Resource);

        var resourceBuilder = builder.ApplicationBuilder.AddResource(iOSDeviceResource)
            .WithAnnotation(new MauiProjectMetadata(projectPath))
            .WithAnnotation(new MauiiOSEnvironmentAnnotation()) // Enable environment variable support via targets file
            .WithAnnotation(new ExecutableAnnotation
            {
                Command = "dotnet",
                WorkingDirectory = workingDirectory
            });

        // Build additional arguments for device UDID if specified
        // For iOS devices, we need to use the MSBuild property _DeviceName to specify which device to target
        // and RuntimeIdentifier must be ios-arm64 for physical devices
        // See: https://learn.microsoft.com/dotnet/maui/ios/cli#launch-the-app-on-a-device
        // Format: -p:_DeviceName=<UDID> -p:RuntimeIdentifier=ios-arm64
        var additionalArgs = new List<string>();
        
        // iOS devices always need RuntimeIdentifier=ios-arm64
        additionalArgs.Add("-p:RuntimeIdentifier=ios-arm64");
        
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            // Specific device - use the UDID directly (no :v2:udid= prefix for devices)
            additionalArgs.Add($"-p:_DeviceName={deviceId}");
        }
        // If no device ID specified, dotnet run will use the only attached device

        // Configure the platform resource with common settings
        // iOS runs only on macOS - check for macOS platform
        MauiPlatformHelper.ConfigurePlatformResource(
            resourceBuilder,
            projectPath,
            "ios",
            "iOS",
            "net10.0-ios",
            OperatingSystem.IsMacOS, // iOS development requires macOS
            "PhoneTablet",
            additionalArgs.ToArray());

        // Validate device ID format before starting the resource
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            resourceBuilder.OnBeforeResourceStarted((resource, eventing, ct) =>
            {
                // Validate that the device ID doesn't look like a simulator ID (which has GUID format)
                if (IsLikelySimulatorId(deviceId))
                {
                    throw new DistributedApplicationException(
                        $"Device ID '{deviceId}' for iOS device resource '{name}' appears to be an iOS Simulator UDID (GUID format). " +
                        $"iOS physical devices typically use a different UDID format (e.g., 00008030-001234567890123A). " +
                        $"If you intended to target an iOS Simulator, use AddiOSSimulator(\"{name}\", \"{deviceId}\") instead.");
                }

                return Task.CompletedTask;
            });
        }

        return resourceBuilder;
    }

    /// <summary>
    /// Adds an iOS simulator resource to run the MAUI application on an iOS simulator.
    /// </summary>
    /// <param name="builder">The MAUI project resource builder.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// This method creates a new iOS simulator platform resource that will run the MAUI application
    /// targeting the iOS platform using <c>dotnet run</c>. The resource does not auto-start 
    /// and must be explicitly started from the dashboard by clicking the start button.
    /// <para>
    /// The resource name will default to "{projectName}-ios-simulator".
    /// </para>
    /// <para>
    /// This will run the application on the default iOS simulator. If no simulator is currently running,
    /// Xcode will launch the default simulator. To target a specific simulator, use the overload with
    /// simulatorId parameter.
    /// </para>
    /// <para>
    /// This overload is not available in polyglot app hosts. Use <see cref="AddiOSSimulator(IResourceBuilder{MauiProjectResource}, string, string)"/> instead.
    /// </para>
    /// </remarks>
    /// <example>
    /// Add an iOS simulator to a MAUI project:
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    /// 
    /// var maui = builder.AddMauiProject("mauiapp", "../MyMauiApp/MyMauiApp.csproj");
    /// var iOSSimulator = maui.AddiOSSimulator();
    /// 
    /// builder.Build().Run();
    /// </code>
    /// </example>
    [AspireExportIgnore(Reason = "Convenience overload. Use the overload with name and optional simulator UDID instead.")]
    public static IResourceBuilder<MauiiOSSimulatorResource> AddiOSSimulator(
        this IResourceBuilder<MauiProjectResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var name = $"{builder.Resource.Name}-ios-simulator";
        return builder.AddiOSSimulator(name, simulatorId: null);
    }

    /// <summary>
    /// Adds an iOS simulator resource to run the MAUI application on an iOS simulator with a specific name.
    /// </summary>
    /// <param name="builder">The MAUI project resource builder.</param>
    /// <param name="name">The name of the iOS simulator resource.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// This method creates a new iOS simulator platform resource that will run the MAUI application
    /// targeting the iOS platform using <c>dotnet run</c>. The resource does not auto-start 
    /// and must be explicitly started from the dashboard by clicking the start button.
    /// <para>
    /// Multiple iOS simulator resources can be added to the same MAUI project if needed, each with
    /// a unique name.
    /// </para>
    /// <para>
    /// This will run the application on the default iOS simulator. If no simulator is currently running,
    /// Xcode will launch the default simulator. To target a specific simulator, use the overload with
    /// simulatorId parameter.
    /// </para>
    /// <para>
    /// This overload is not available in polyglot app hosts. Use <see cref="AddiOSSimulator(IResourceBuilder{MauiProjectResource}, string, string)"/> instead.
    /// </para>
    /// </remarks>
    /// <example>
    /// Add multiple iOS simulators to a MAUI project:
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    /// 
    /// var maui = builder.AddMauiProject("mauiapp", "../MyMauiApp/MyMauiApp.csproj");
    /// var simulator1 = maui.AddiOSSimulator("ios-simulator-1");
    /// var simulator2 = maui.AddiOSSimulator("ios-simulator-2");
    /// 
    /// builder.Build().Run();
    /// </code>
    /// </example>
    [AspireExportIgnore(Reason = "Convenience overload. Use the overload with the optional simulator UDID parameter instead.")]
    public static IResourceBuilder<MauiiOSSimulatorResource> AddiOSSimulator(
        this IResourceBuilder<MauiProjectResource> builder,
        [ResourceName] string name)
    {
        return builder.AddiOSSimulator(name, simulatorId: null);
    }

    /// <summary>
    /// Adds an iOS simulator resource to run the MAUI application on an iOS simulator with a specific name and simulator UDID.
    /// </summary>
    /// <param name="builder">The MAUI project resource builder.</param>
    /// <param name="name">The name of the iOS simulator resource.</param>
    /// <param name="simulatorId">Optional simulator UDID to target a specific iOS simulator. If not specified, uses the default simulator.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// This method creates a new iOS simulator platform resource that will run the MAUI application
    /// targeting the iOS platform using <c>dotnet run</c>. The resource does not auto-start 
    /// and must be explicitly started from the dashboard by clicking the start button.
    /// <para>
    /// Multiple iOS simulator resources can be added to the same MAUI project if needed, each with
    /// a unique name.
    /// </para>
    /// <para>
    /// To target a specific simulator, provide the simulator UDID. You can find simulator UDIDs in Xcode
    /// under Window > Devices and Simulators > Simulators tab, right-click on a simulator and select
    /// "Copy Identifier", or use the command: /Applications/Xcode.app/Contents/Developer/usr/bin/simctl list
    /// </para>
    /// </remarks>
    /// <example>
    /// Add multiple iOS simulators to a MAUI project:
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    /// 
    /// var maui = builder.AddMauiProject("mauiapp", "../MyMauiApp/MyMauiApp.csproj");
    /// 
    /// // Default simulator
    /// var simulator1 = maui.AddiOSSimulator("ios-simulator-default");
    /// 
    /// // Specific simulator by UDID
    /// var simulator2 = maui.AddiOSSimulator("ios-simulator-iphone15", "E25BBE37-69BA-4720-B6FD-D54C97791E79");
    /// 
    /// builder.Build().Run();
    /// </code>
    /// </example>
    [AspireExport(Description = "Adds an iOS simulator resource for a .NET MAUI project.")]
    public static IResourceBuilder<MauiiOSSimulatorResource> AddiOSSimulator(
        this IResourceBuilder<MauiProjectResource> builder,
        [ResourceName] string name,
        string? simulatorId = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // Get the absolute project path and working directory
        var (projectPath, workingDirectory) = MauiPlatformHelper.GetProjectPaths(builder);

        var iOSSimulatorResource = new MauiiOSSimulatorResource(name, builder.Resource);

        var resourceBuilder = builder.ApplicationBuilder.AddResource(iOSSimulatorResource)
            .WithAnnotation(new MauiProjectMetadata(projectPath))
            .WithAnnotation(new MauiiOSEnvironmentAnnotation()) // Enable environment variable support via targets file
            .WithAnnotation(new ExecutableAnnotation
            {
                Command = "dotnet",
                WorkingDirectory = workingDirectory
            });

        // Build additional arguments for simulator UDID if specified
        // For iOS simulators, we need to use the MSBuild property _DeviceName with the :v2:udid= prefix
        // See: https://learn.microsoft.com/dotnet/maui/ios/cli#launch-the-app-on-a-specific-simulator
        // Format: -p:_DeviceName=:v2:udid=<UDID>
        var additionalArgs = new List<string>();
        
        if (!string.IsNullOrWhiteSpace(simulatorId))
        {
            // Specific simulator - use :v2:udid= prefix (note: no quotes around the value to avoid Android issue)
            additionalArgs.Add($"-p:_DeviceName=:v2:udid={simulatorId}");
        }
        // If no simulator ID specified, dotnet run will use the default simulator

        // Configure the platform resource with common settings
        // iOS runs only on macOS - check for macOS platform
        MauiPlatformHelper.ConfigurePlatformResource(
            resourceBuilder,
            projectPath,
            "ios",
            "iOS",
            "net10.0-ios",
            OperatingSystem.IsMacOS, // iOS development requires macOS
            "PhoneTablet",
            additionalArgs.ToArray());

        // Validate simulator ID format before starting the resource
        if (!string.IsNullOrWhiteSpace(simulatorId))
        {
            resourceBuilder.OnBeforeResourceStarted((resource, eventing, ct) =>
            {
                // Validate that the simulator ID looks like a GUID (expected format for iOS Simulator UDIDs)
                if (!IsLikelySimulatorId(simulatorId))
                {
                    throw new DistributedApplicationException(
                        $"Simulator ID '{simulatorId}' for iOS simulator resource '{name}' does not appear to be an iOS Simulator UDID (GUID format). " +
                        "iOS Simulator UDIDs are typically GUIDs (e.g., E25BBE37-69BA-4720-B6FD-D54C97791E79). " +
                        $"If you intended to target a physical iOS device, use AddiOSDevice(\"{name}\", \"{simulatorId}\") instead.");
                }

                return Task.CompletedTask;
            });
        }

        return resourceBuilder;
    }

    /// <summary>
    /// Checks if a device ID appears to be an iOS Simulator UDID.
    /// iOS Simulator UDIDs are standard GUIDs (8-4-4-4-12 format).
    /// </summary>
    private static bool IsLikelySimulatorId(string deviceId)
    {
        // iOS Simulator UDIDs are standard GUIDs (8-4-4-4-12 format)
        // Example: E25BBE37-69BA-4720-B6FD-D54C97791E79
        return Guid.TryParse(deviceId, out _);
    }
}
