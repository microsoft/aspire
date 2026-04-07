// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Maui;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding .NET MAUI projects to the application model.
/// </summary>
public static class MauiProjectExtensions
{
    /// <summary>
    /// Adds a .NET MAUI project to the application model. This resource can be used to create platform-specific resources.
    /// </summary>
    /// <param name="builder">The builder for the distributed application.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="projectPath">The path to the .NET MAUI project file (.csproj). This can be a relative or absolute path.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// This method creates a parent MAUI project resource that serves as a container for platform-specific
    /// resources such as Windows, Android, iOS, and macOS. The actual platform instances are added using
    /// extension methods like <c>AddWindowsDevice</c>.
    /// <para>
    /// The MAUI project is not built immediately when the AppHost starts. Instead, builds are deferred
    /// until a platform-specific resource is started, allowing faster AppHost startup and enabling
    /// incremental builds during development.
    /// </para>
    /// </remarks>
    /// <example>
    /// Add a MAUI project with Windows support:
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    /// 
    /// var weatherApi = builder.AddProject&lt;Projects.WeatherApi&gt;("api");
    /// 
    /// var maui = builder.AddMauiProject("mauiapp", "../MyMauiApp/MyMauiApp.csproj");
    /// var windowsDevice = maui.AddWindowsDevice()
    ///     .WithReference(weatherApi);
    /// 
    /// builder.Build().Run();
    /// </code>
    /// </example>
    [AspireExport(Description = "Adds a .NET MAUI project to the application model.")]
    public static IResourceBuilder<MauiProjectResource> AddMauiProject(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        string projectPath)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(projectPath);

        // Register MAUI-specific hosting services (lifecycle hooks, etc.)
        // This is safe to call multiple times - it only registers once
        builder.AddMauiHostingServices();

        // Create the MAUI project resource and configuration
        // Do not register the logical grouping resource with AddResource so it stays invisible in the dashboard
        // Only MAUI project targets added through their extension methods will show up
        var resource = new MauiProjectResource(name, projectPath);
        return builder.CreateResourceBuilder(resource);
    }
}
