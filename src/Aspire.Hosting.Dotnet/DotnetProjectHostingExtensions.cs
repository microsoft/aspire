// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dotnet;
using Aspire.Hosting.Utils;

#pragma warning disable ASPIREEXTENSION001 // WithDebugSupport is experimental
#pragma warning disable ASPIREPROJECTS001 // WithProjectDefaults is experimental

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding C# projects and file-based C# apps (by path) to an
/// <see cref="IDistributedApplicationBuilder"/>.
/// </summary>
public static class DotnetProjectHostingExtensions
{
    /// <summary>
    /// Adds an environment variable to the build process for a .NET project.
    /// </summary>
    /// <param name="builder">The .NET project resource builder.</param>
    /// <param name="name">The name of the environment variable.</param>
    /// <param name="value">The value of the environment variable.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining additional configuration.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="builder"/>, <paramref name="name"/>, or <paramref name="value"/> is null.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The variable is available while Aspire builds the project but is not added to the environment of the
    /// launched project. Use <c>WithEnvironment</c> separately when the same variable is also needed at runtime.
    /// </para>
    /// <para>
    /// Configuring a build environment causes Aspire to build this project separately from traversal groups.
    /// </para>
    /// <para>
    /// Do not use this API for secrets. Aspire must carry the value in IDE launch metadata and process environments.
    /// Protected temporary MSBuild response files preserve global-property semantics without exposing values in process
    /// command lines, but they are not a general-purpose secret transport.
    /// </para>
    /// </remarks>
    /// <example>
    /// Configure an environment variable that selects a custom build output:
    /// <code lang="csharp">
    /// builder.AddDotnetProject("worker", "../Worker/Worker.csproj")
    ///     .WithBuildEnvironment("BUILD_FLAVOR", "custom");
    /// </code>
    /// </example>
    [Experimental("ASPIREDOTNETPROJECT001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExport]
    public static IResourceBuilder<DotnetProjectResource> WithBuildEnvironment(
        this IResourceBuilder<DotnetProjectResource> builder,
        string name,
        string value)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);

        return builder.WithBuildEnvironment(context => context.EnvironmentVariables[name] = value);
    }

    /// <summary>
    /// Adds a callback that configures build-only environment variables for a .NET project.
    /// </summary>
    /// <param name="builder">The .NET project resource builder.</param>
    /// <param name="callback">The callback that configures the build environment.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining additional configuration.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="builder"/> or <paramref name="callback"/> is null.
    /// </exception>
    /// <remarks>
    /// Values configured by this callback are not added to the environment of the launched project. Do not use this API
    /// for secrets because Aspire carries the values in IDE launch metadata, process environments, and protected
    /// temporary MSBuild response files.
    /// </remarks>
    [Experimental("ASPIREDOTNETPROJECT001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExportIgnore(Reason = "Raw Action delegate callbacks are not ATS-compatible.")]
    public static IResourceBuilder<DotnetProjectResource> WithBuildEnvironment(
        this IResourceBuilder<DotnetProjectResource> builder,
        Action<EnvironmentCallbackContext> callback)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(callback);

        return builder.WithBuildEnvironment(context =>
        {
            callback(context);
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Adds an asynchronous callback that configures build-only environment variables for a .NET project.
    /// </summary>
    /// <param name="builder">The .NET project resource builder.</param>
    /// <param name="callback">The callback that configures the build environment.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining additional configuration.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="builder"/> or <paramref name="callback"/> is null.
    /// </exception>
    /// <remarks>
    /// Values configured by this callback are not added to the environment of the launched project. Do not use this API
    /// for secrets because Aspire carries the values in IDE launch metadata, process environments, and protected
    /// temporary MSBuild response files.
    /// </remarks>
    [Experimental("ASPIREDOTNETPROJECT001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExportIgnore(Reason = "Raw Func delegate callbacks are not ATS-compatible.")]
    public static IResourceBuilder<DotnetProjectResource> WithBuildEnvironment(
        this IResourceBuilder<DotnetProjectResource> builder,
        Func<EnvironmentCallbackContext, Task> callback)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(callback);

        return builder.WithAnnotation(new DotnetProjectBuildEnvironmentCallbackAnnotation(callback));
    }

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
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the internal addDotnetProject dispatcher export.")]
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
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the internal addDotnetProject dispatcher export.")]
    public static IResourceBuilder<DotnetProjectResource> AddDotnetProject(this IDistributedApplicationBuilder builder, [ResourceName] string name, string path, Action<ProjectResourceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new ProjectResourceOptions();
        configure(options);

        path = PathNormalizer.NormalizePathForCurrentPlatform(Path.Combine(builder.AppHostDirectory, path));

        // The app host's own build configuration (Debug/Release) is propagated to every child launch
        // so process and IDE launchers resolve the output produced by the coordinated build.
        var configuration = builder.AppHostAssembly?.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration;
        var projectMetadata = new DotnetProjectMetadata(path, configuration);
        var buildCoordinator = DotnetProjectBuildCoordinator.Prepare(builder, projectMetadata);

        // ExecutableResource requires a working directory. Use the project/app directory so the process
        // launches from the same place a ProjectResource would (DCP used Path.GetDirectoryName(ProjectPath)).
        // Accessing ProjectPath also resolves a project directory to its single .csproj. Falling back to the
        // app host directory keeps construction valid for invalid paths, which are reported by the
        // OnBeforeResourceStarted validation below.
        var workingDirectory = Path.GetDirectoryName(projectMetadata.ProjectPath) ?? builder.AppHostDirectory;

        var app = new DotnetProjectResource(name, workingDirectory);

        var resource = builder.AddResource(app)
                              .WithAnnotation(projectMetadata)
                              .WithIconName("CodeCsRectangle")
                              .WithProjectDefaults(options);
        var projectLaunchConfigurationType = resource.Resource.Annotations
            .OfType<SupportsDebuggingAnnotation>()
            .LastOrDefault()
            ?.LaunchConfigurationType
            ?? KnownLaunchConfigurationTypes.Project;

        DotnetProjectBuildCoordinator.Configure(resource, buildCoordinator);
        string? defaultRunWorkingDirectory = null;

        // Declare the SDK-selected tool invocation separately from the program arguments so a later
        // WithLaunchToolArgs call replaces it instead of being prepended to it.
        resource.WithLaunchToolArgs(
            async ctx =>
            {
                if (ctx.Resource.SupportsDebugging(builder.Configuration, out var debugAnnotation)
                    && debugAnnotation.LaunchConfigurationType == projectLaunchConfigurationType)
                {
                    return;
                }

                IProjectMetadata metadata = projectMetadata;
                if (!metadata.IsFileBasedApp &&
                    metadata.SuppressBuild &&
                    metadata.BuildWorkingDirectory is { } buildWorkingDirectory)
                {
                    var runProperties = await DotnetProjectRunPropertiesResolver.ResolveAsync(
                        metadata.ProjectPath,
                        projectMetadata.BuildConfiguration,
                        projectMetadata.BuildEnvironment,
                        buildWorkingDirectory,
                        ctx.Logger,
                        ctx.CancellationToken).ConfigureAwait(false);
                    var executableAnnotation = ctx.Resource.Annotations.OfType<ExecutableAnnotation>().Last();
                    defaultRunWorkingDirectory ??= executableAnnotation.WorkingDirectory;
                    executableAnnotation.Command = runProperties.Command;
                    executableAnnotation.WorkingDirectory = string.IsNullOrEmpty(runProperties.WorkingDirectory)
                        ? defaultRunWorkingDirectory
                        : runProperties.WorkingDirectory;

                    foreach (var argument in CommandLineArgsParser.Parse(runProperties.Arguments))
                    {
                        ctx.Args.Add(argument);
                    }

                    return;
                }

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

                if (!string.IsNullOrEmpty(projectMetadata.BuildConfiguration))
                {
                    ctx.Args.Add("--configuration");
                    ctx.Args.Add(projectMetadata.BuildConfiguration);
                }

                // Always suppress the normal launch profile handling: the profile's settings would otherwise
                // override the ambient environment, but those ambient settings come from the application model
                // and must take priority. WithProjectDefaults materializes the profile's environment manually.
                ctx.Args.Add("--no-launch-profile");

                if (GetLaunchProfileArguments(ctx.Resource).Count > 0)
                {
                    ctx.Args.Add("--");
                }
            },
            ownedByLaunchConfigurationType: projectLaunchConfigurationType,
            showInCommandLine: true);

        // Launch-profile command-line arguments belong to the program, not the replaceable tool invocation.
        // Keeping them in the ordinary segment preserves them when a caller supplies a custom launch tool.
        resource.WithArgs(ctx =>
        {
            foreach (var arg in GetLaunchProfileArguments(ctx.Resource))
            {
                ctx.Args.Add(arg);
            }
        });

        List<string> GetLaunchProfileArguments(IResource resource)
        {
            // Project launch configurations carry the selected launch profile, so the IDE applies its command-line arguments.
            if (!builder.ExecutionContext.IsRunMode
                || options.ExcludeLaunchProfile
                || (resource.SupportsDebugging(builder.Configuration, out var debugAnnotation)
                    && debugAnnotation.LaunchConfigurationType == projectLaunchConfigurationType))
            {
                return [];
            }

            var launchProfile = resource.GetEffectiveLaunchProfile()?.LaunchProfile;
            return launchProfile is not null && !string.IsNullOrWhiteSpace(launchProfile.CommandLineArgs)
                ? CommandLineArgsParser.Parse(launchProfile.CommandLineArgs)
                : [];
        }

        resource.OnBeforeResourceStarted((r, e, ct) =>
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

            // The minimum-SDK check for file-based apps is applied by WithProjectDefaults.
            return Task.CompletedTask;
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
