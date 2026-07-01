// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp.Model;
using Aspire.Hosting.Utils;

#pragma warning disable ASPIREEXTENSION001 // WithDebugSupport is experimental

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding C# projects and file-based C# apps (by path) to an
/// <see cref="IDistributedApplicationBuilder"/>.
/// </summary>
public static class DotnetProjectHostingExtensions
{
    /// <summary>
    /// Adds a C# project or file-based app to the application model.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource. This name will be used for service discovery when referenced in a dependency.</param>
    /// <param name="path">The path to the file-based app file, project file, or project directory.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// This overload of the <see cref="AddDotnetProject(IDistributedApplicationBuilder, string, string)"/> method adds a C# project or file-based app to the application
    /// model using a path to the file-based app .cs file, project file (.csproj), or project directory.
    /// If the path is not an absolute path then it will be computed relative to the app host directory.
    /// </para>
    /// <example>
    /// Add a file-based app to the app model via a file path.
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// builder.AddDotnetProject("inventoryservice", @"..\InventoryService.cs");
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    /// </remarks>
    [Experimental("ASPIREDOTNETPROJECT001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExportIgnore(Reason = "Polyglot app hosts use the internal addDotnetProject dispatcher export.")]
    public static IResourceBuilder<DotnetProjectResource> AddDotnetProject(this IDistributedApplicationBuilder builder, [ResourceName] string name, string path)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(path);

        return builder.AddDotnetProject(name, path, _ => { });
    }

    /// <summary>
    /// Adds a C# application resource.
    /// </summary>
    [Experimental("ASPIREDOTNETPROJECT001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExport("addDotnetProject")]
    internal static IResourceBuilder<DotnetProjectResource> AddDotnetProjectForPolyglot(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        string path,
        ProjectResourceOptions? options = null)
    {
        return options is null
            ? builder.AddDotnetProject(name, path, _ => { })
            : builder.AddDotnetProject(name, path, configure => ApplyProjectResourceOptions(configure, options));
    }

    /// <summary>
    /// Adds a C# project or file-based app to the application model.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource. This name will be used for service discovery when referenced in a dependency.</param>
    /// <param name="path">The path to the file-based app file, project file, or project directory.</param>
    /// <param name="configure">An optional action to configure the C# app resource options.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// This overload of the <see cref="AddDotnetProject(IDistributedApplicationBuilder, string, string)"/> method adds a C# project or file-based app to the application
    /// model using a path to the file-based app .cs file, project file (.csproj), or project directory.
    /// If the path is not an absolute path then it will be computed relative to the app host directory.
    /// </para>
    /// <example>
    /// Add a file-based app to the app model via a file path.
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// builder.AddDotnetProject("inventoryservice", @"..\InventoryService.cs", o => o.LaunchProfileName = "https");
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    /// </remarks>
    [Experimental("ASPIREDOTNETPROJECT001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExportIgnore(Reason = "Polyglot app hosts use the internal addDotnetProject dispatcher export.")]
    public static IResourceBuilder<DotnetProjectResource> AddDotnetProject(this IDistributedApplicationBuilder builder, [ResourceName] string name, string path, Action<ProjectResourceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new ProjectResourceOptions();
        configure(options);

        path = PathNormalizer.NormalizePathForCurrentPlatform(Path.Combine(builder.AppHostDirectory, path));
        var projectMetadata = new ProjectMetadata(path);

        // ExecutableResource requires a working directory. Use the project/app directory so the process
        // launches from the same place a ProjectResource would (DCP used Path.GetDirectoryName(ProjectPath)).
        // Accessing ProjectPath also resolves a project directory to its single .csproj. Falling back to the
        // app host directory keeps construction valid for invalid paths, which are reported by the
        // OnBeforeResourceStarted validation below.
        var workingDirectory = Path.GetDirectoryName(projectMetadata.ProjectPath) ?? builder.AppHostDirectory;

        var app = new DotnetProjectResource(name, workingDirectory);

        // The app host's own build configuration (Debug/Release) is propagated to the child `dotnet run`
        // so the service matches the app host, mirroring DistributedApplicationOptions.Configuration.
        var configuration = builder.AppHostAssembly?.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration;

        var resource = builder.AddResource(app)
                              .WithAnnotation(projectMetadata)
                              .WithDebugSupport(mode => new ProjectLaunchConfiguration { ProjectPath = projectMetadata.ProjectPath, Mode = mode }, "project")
                              .WithProjectDefaults(options);

        // Build the `dotnet run` command line. This mirrors the ExecutionType.Process path in
        // Dcp/ExecutableCreator.PrepareProjectExecutables() so a non-debug launch of a DotnetProjectResource
        // (now an ExecutableResource, not a ProjectResource) matches how AddProject launches today:
        //   dotnet run --project <proj> [--no-build] [--configuration <cfg>] --no-launch-profile
        //   dotnet run --file <app.cs> --no-cache [--no-build] [--configuration <cfg>] --no-launch-profile
        resource.WithArgs(ctx =>
        {
            IProjectMetadata metadata = projectMetadata;

            ctx.Args.Add("run");
            ctx.Args.Add(metadata.IsFileBasedApp ? "--file" : "--project");
            ctx.Args.Add(metadata.ProjectPath);

            if (metadata.IsFileBasedApp)
            {
                ctx.Args.Add("--no-cache");
            }

            if (metadata.SuppressBuild)
            {
                ctx.Args.Add("--no-build");
            }

            if (!string.IsNullOrEmpty(configuration))
            {
                ctx.Args.Add("--configuration");
                ctx.Args.Add(configuration);
            }

            // Always suppress the normal launch profile handling: the profile's settings would otherwise
            // override the ambient environment, but those ambient settings come from the application model
            // and must take priority. WithProjectDefaults materializes the profile's environment manually.
            ctx.Args.Add("--no-launch-profile");

            // The launch profile's command line args are still applied here (run mode), after a `--`
            // separator so they're passed to the app, matching the ProjectResource launch behavior.
            if (builder.ExecutionContext.IsRunMode && !options.ExcludeLaunchProfile)
            {
                var launchProfile = ctx.Resource.GetEffectiveLaunchProfile()?.LaunchProfile;
                if (launchProfile is not null && !string.IsNullOrWhiteSpace(launchProfile.CommandLineArgs))
                {
                    var launchProfileArgs = CommandLineArgsParser.Parse(launchProfile.CommandLineArgs);
                    if (launchProfileArgs.Count > 0)
                    {
                        ctx.Args.Add("--");
                        foreach (var arg in launchProfileArgs)
                        {
                            ctx.Args.Add(arg);
                        }
                    }
                }
            }
        });

        resource.OnBeforeResourceStarted(async (r, e, ct) =>
        {
            var projectPath = projectMetadata.ProjectPath;

            // Validate project path
            if (!projectPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) && !projectPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                // Project path did not resolve to a .csproj or .cs file
                var message = Directory.Exists(projectPath)
                    ? $"Path to C# project could not be determined. The directory '{projectPath}' must contain a single .csproj file."
                    : $"The C# app path '{projectPath}' is invalid. The path must be to a .cs file, .csproj file, or directory containing a single .csproj file.";
                throw new DistributedApplicationException(message);
            }

            // Validate .NET version
            if (((IProjectMetadata)projectMetadata).IsFileBasedApp
                && await DotnetSdkUtils.TryGetVersionAsync(Path.GetDirectoryName(projectPath)).ConfigureAwait(false) is { } version
                && version.Major < 10)
            {
                // File-based apps are only supported on .NET 10 or later
                throw new DistributedApplicationException($"File-based apps are only supported on .NET 10 or later. The version active in '{Path.GetDirectoryName(projectPath)}' is {version}.");
            }
        });

        return resource;
    }

    private static void ApplyProjectResourceOptions(ProjectResourceOptions target, ProjectResourceOptions source)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);

        target.LaunchProfileName = source.LaunchProfileName;
        target.ExcludeLaunchProfile = source.ExcludeLaunchProfile;
        target.ExcludeKestrelEndpoints = source.ExcludeKestrelEndpoints;
    }
}
