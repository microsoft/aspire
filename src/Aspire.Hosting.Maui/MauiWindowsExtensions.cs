// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Maui;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding Windows platform resources to MAUI projects.
/// </summary>
public static class MauiWindowsExtensions
{
    /// <summary>
    /// Adds a Windows device resource to run the MAUI application on the Windows platform.
    /// </summary>
    /// <param name="builder">The MAUI project resource builder.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// This method creates a new Windows platform resource that will run the MAUI application
    /// targeting the Windows platform using <c>dotnet run</c>. The resource does not auto-start 
    /// and must be explicitly started from the dashboard by clicking the start button.
    /// <para>
    /// The resource name will default to "{projectName}-windows".
    /// </para>
    /// <para>
    /// This overload is not available in polyglot app hosts. Use <see cref="AddWindowsDevice(IResourceBuilder{MauiProjectResource}, string)"/> instead.
    /// </para>
    /// </remarks>
    /// <example>
    /// Add a Windows device to a MAUI project:
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    /// 
    /// var maui = builder.AddMauiProject("mauiapp", "../MyMauiApp/MyMauiApp.csproj");
    /// var windowsDevice = maui.AddWindowsDevice();
    /// 
    /// builder.Build().Run();
    /// </code>
    /// </example>
    [AspireExportIgnore(Reason = "Convenience overload. Use the named overload instead.")]
    public static IResourceBuilder<MauiWindowsPlatformResource> AddWindowsDevice(
        this IResourceBuilder<MauiProjectResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var name = $"{builder.Resource.Name}-windows";
        return builder.AddWindowsDevice(name);
    }

    /// <summary>
    /// Adds a Windows device resource to run the MAUI application on the Windows platform with a specific name.
    /// </summary>
    /// <param name="builder">The MAUI project resource builder.</param>
    /// <param name="name">The name of the Windows device resource.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// This method creates a new Windows platform resource that will run the MAUI application
    /// targeting the Windows platform using <c>dotnet run</c>. The resource does not auto-start 
    /// and must be explicitly started from the dashboard by clicking the start button.
    /// <para>
    /// You can add multiple Windows device resources to a MAUI project by calling this method multiple times with different names.
    /// </para>
    /// </remarks>
    /// <example>
    /// Add multiple Windows devices to a MAUI project:
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    /// 
    /// var maui = builder.AddMauiProject("mauiapp", "../MyMauiApp/MyMauiApp.csproj");
    /// var windowsDevice1 = maui.AddWindowsDevice("windows-device-1");
    /// var windowsDevice2 = maui.AddWindowsDevice("windows-device-2");
    /// 
    /// builder.Build().Run();
    /// </code>
    /// </example>
    [AspireExport("addWindowsDevice", Description = "Adds a Windows platform resource for a .NET MAUI project.")]
    public static IResourceBuilder<MauiWindowsPlatformResource> AddWindowsDevice(
        this IResourceBuilder<MauiProjectResource> builder,
        [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // Get the absolute project path and working directory
        var (projectPath, workingDirectory) = MauiPlatformHelper.GetProjectPaths(builder);

        var windowsResource = new MauiWindowsPlatformResource(name, builder.Resource);

        var resourceBuilder = builder.ApplicationBuilder.AddResource(windowsResource)
            .WithAnnotation(new MauiProjectMetadata(projectPath))
            .WithAnnotation(new ExecutableAnnotation
            {
                Command = "dotnet",
                WorkingDirectory = workingDirectory
            });

        // Configure the platform resource with common settings
        MauiPlatformHelper.ConfigurePlatformResource(
            resourceBuilder,
            projectPath,
            "windows",
            "Windows",
            "net10.0-windows10.0.19041.0",
            OperatingSystem.IsWindows,
            "Desktop");

        return resourceBuilder;
    }
}
