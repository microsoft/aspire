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
/// Provides extension methods for adding SDK-style .NET projects and file-based C# apps (by path) to an
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
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="name"/> is empty.
    /// </exception>
    /// <exception cref="DistributedApplicationException">
    /// Thrown when <paramref name="builder"/> represents a file-based C# app.
    /// </exception>
    /// <remarks>
    /// <para>
    /// This method supports SDK-style project files (<c>.csproj</c>, <c>.fsproj</c>, and <c>.vbproj</c>) only.
    /// File-based C# apps (<c>.cs</c>) do not support build-only environment variables.
    /// </para>
    /// <para>
    /// The variable is available while Aspire builds the project but is not added to the environment of the
    /// launched project. Use <c>WithEnvironment</c> separately when the same variable is also needed at runtime.
    /// </para>
    /// <para>
    /// Configuring a build environment causes Aspire to build this project separately from traversal groups.
    /// </para>
    /// <para>
    /// When publishing a container, do not use this API to set MSBuild properties that control the output artifact's
    /// identity, destination, format, or target platform. Aspire rejects those properties because downstream
    /// publishing steps use the values configured with <c>WithContainerBuildOptions</c>.
    /// </para>
    /// <para>
    /// Do not use this API for secrets. Aspire must carry the value in IDE launch metadata and process environments,
    /// and the value can appear in build diagnostics. Protected temporary MSBuild response files preserve
    /// global-property semantics without exposing values in process command lines, but they are not a general-purpose
    /// secret transport.
    /// </para>
    /// </remarks>
    /// <example>
    /// Configure an environment variable that selects a custom build output:
    /// <code lang="csharp">
    /// builder.AddDotnetProject("worker", "../Worker/Worker.csproj")
    ///     .WithBuildEnvironment("BUILD_FLAVOR", "custom");
    /// </code>
    /// </example>
    [AspireExport]
    public static IResourceBuilder<DotnetProjectResource> WithBuildEnvironment(
        this IResourceBuilder<DotnetProjectResource> builder,
        string name,
        string value)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
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
    /// <exception cref="DistributedApplicationException">
    /// Thrown when <paramref name="builder"/> represents a file-based C# app.
    /// </exception>
    /// <remarks>
    /// <para>
    /// This method supports SDK-style project files (<c>.csproj</c>, <c>.fsproj</c>, and <c>.vbproj</c>) only.
    /// File-based C# apps (<c>.cs</c>) do not support build-only environment variables.
    /// </para>
    /// <para>
    /// When publishing a container, do not use this API to set MSBuild properties that control the output artifact's
    /// identity, destination, format, or target platform. Aspire rejects those properties because downstream
    /// publishing steps use the values configured with <c>WithContainerBuildOptions</c>.
    /// </para>
    /// <para>
    /// Values configured by this callback are not added to the environment of the launched project. Do not use this API
    /// for secrets because Aspire carries the values in IDE launch metadata, process environments, and protected
    /// temporary MSBuild response files, and the values can appear in build diagnostics.
    /// </para>
    /// </remarks>
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
    /// <exception cref="DistributedApplicationException">
    /// Thrown when <paramref name="builder"/> represents a file-based C# app.
    /// </exception>
    /// <remarks>
    /// <para>
    /// This method supports SDK-style project files (<c>.csproj</c>, <c>.fsproj</c>, and <c>.vbproj</c>) only.
    /// File-based C# apps (<c>.cs</c>) do not support build-only environment variables.
    /// </para>
    /// <para>
    /// When publishing a container, do not use this API to set MSBuild properties that control the output artifact's
    /// identity, destination, format, or target platform. Aspire rejects those properties because downstream
    /// publishing steps use the values configured with <c>WithContainerBuildOptions</c>.
    /// </para>
    /// <para>
    /// Values configured by this callback are not added to the environment of the launched project. Do not use this API
    /// for secrets because Aspire carries the values in IDE launch metadata, process environments, and protected
    /// temporary MSBuild response files, and the values can appear in build diagnostics.
    /// </para>
    /// </remarks>
    [AspireExportIgnore(Reason = "Raw Func delegate callbacks are not ATS-compatible.")]
    public static IResourceBuilder<DotnetProjectResource> WithBuildEnvironment(
        this IResourceBuilder<DotnetProjectResource> builder,
        Func<EnvironmentCallbackContext, Task> callback)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(callback);

        if (builder.Resource.Annotations.OfType<DotnetProjectMetadata>().SingleOrDefault() is { } metadata)
        {
            ValidateBuildEnvironmentSupport(builder.Resource, metadata);
        }

        builder.WithAnnotation(new DotnetProjectBuildEnvironmentCallbackAnnotation(callback));
        return builder.WithDotnetProgramBuildEnvironment(callback);
    }

    internal static void ValidateBuildEnvironmentSupport(IResource resource, IProjectMetadata metadata)
    {
        if (metadata.IsFileBasedApp)
        {
            throw new DistributedApplicationException(
                $"The .NET resource '{resource.Name}' uses WithBuildEnvironment, which is supported only for project files.");
        }
    }

    /// <summary>
    /// Configures the number of replicas of the .NET project.
    /// </summary>
    /// <param name="builder">The .NET project resource builder.</param>
    /// <param name="replicas">The number of replicas.</param>
    /// <returns>The same builder for chaining additional configuration.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> is null.</exception>
    [AspireExport("withDotnetProjectReplicas", MethodName = "withReplicas")]
    public static IResourceBuilder<DotnetProjectResource> WithReplicas(
        this IResourceBuilder<DotnetProjectResource> builder,
        int replicas)
    {
        return DotnetProgramResourceBuilderExtensions.WithReplicas(builder, replicas);
    }

    /// <summary>
    /// Disables automatic forwarded-header configuration when publishing the .NET project.
    /// </summary>
    /// <param name="builder">The .NET project resource builder.</param>
    /// <returns>The same builder for chaining additional configuration.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> is null.</exception>
    [AspireExport("disableDotnetProjectForwardedHeaders", MethodName = "disableForwardedHeaders")]
    public static IResourceBuilder<DotnetProjectResource> DisableForwardedHeaders(
        this IResourceBuilder<DotnetProjectResource> builder)
    {
        return DotnetProgramResourceBuilderExtensions.DisableForwardedHeaders(builder);
    }

    /// <summary>
    /// Configures which endpoints contribute environment variables for the .NET project.
    /// </summary>
    /// <param name="builder">The .NET project resource builder.</param>
    /// <param name="filter">A filter that returns true for endpoints whose environment variables should be injected.</param>
    /// <returns>The same builder for chaining additional configuration.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> or <paramref name="filter"/> is null.</exception>
    /// <remarks>
    /// Filters compose: an endpoint is included only when every configured filter accepts it.
    /// </remarks>
    [AspireExportIgnore(Reason = "Uses Func<EndpointAnnotation, bool>; polyglot AppHosts use the endpoint-name dispatcher.")]
    public static IResourceBuilder<DotnetProjectResource> WithEndpointsInEnvironment(
        this IResourceBuilder<DotnetProjectResource> builder,
        Func<EndpointAnnotation, bool> filter)
    {
        return DotnetProgramResourceBuilderExtensions.WithEndpointsInEnvironment(builder, filter);
    }

    /// <summary>
    /// Gets the project metadata for the .NET project resource.
    /// </summary>
    /// <param name="projectResource">The .NET project resource.</param>
    /// <returns>The project metadata.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="projectResource"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The resource does not carry exactly one stable project metadata annotation.</exception>
    /// <remarks>
    /// Project metadata cannot be replaced after project defaults have been applied because launch settings,
    /// endpoints, and rebuild behavior are derived from that annotation.
    /// </remarks>
    [AspireExportIgnore(Reason = "Project metadata is a .NET-specific contract and is not part of the ATS surface.")]
    public static IProjectMetadata GetProjectMetadata(this DotnetProjectResource projectResource)
    {
        ArgumentNullException.ThrowIfNull(projectResource);

        return ProjectResourceExtensions.GetProjectMetadata((IDotnetProgramResource)projectResource);
    }

    /// <summary>
    /// Configures endpoint environment-variable injection for a .NET project in polyglot AppHosts.
    /// </summary>
    [AspireExport("withDotnetProjectEndpointsInEnvironment", MethodName = "withEndpointsInEnvironment")]
    internal static IResourceBuilder<DotnetProjectResource> WithEndpointsInEnvironmentForPolyglot(
        this IResourceBuilder<DotnetProjectResource> builder,
        string[] endpointNames)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(endpointNames);

        var includedEndpointNames = endpointNames.ToHashSet(StringComparer.OrdinalIgnoreCase);

        return builder.WithEndpointsInEnvironment(endpoint => includedEndpointNames.Contains(endpoint.Name));
    }

    /// <summary>
    /// Adds an SDK-style .NET project or file-based C# app to the application model.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource. This name will be used for service discovery when referenced in a dependency.</param>
    /// <param name="path">The path to the file-based app file, project file, or project directory.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// This overload adds a path to a file-based app (<c>.cs</c>), an SDK-style C#, F#, or Visual Basic project
    /// (<c>.csproj</c>, <c>.fsproj</c>, or <c>.vbproj</c>), or a directory containing exactly one supported project.
    /// Directories containing multiple supported projects require an explicit project filename.
    /// If the path is not an absolute path then it will be computed relative to the app host directory.
    /// </para>
    /// <para>
    /// Visual Basic projects prefer <c>My Project/launchSettings.json</c> and fall back to
    /// <c>Properties/launchSettings.json</c>. F# scripts and non-SDK project systems are not supported.
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
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the internal addDotnetProject dispatcher export.")]
    public static IResourceBuilder<DotnetProjectResource> AddDotnetProject(this IDistributedApplicationBuilder builder, [ResourceName] string name, string path)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(path);

        return builder.AddDotnetProject(name, path, _ => { });
    }

    /// <summary>
    /// Adds a .NET application resource.
    /// </summary>
    [AspireExport("addDotnetProject")]
    internal static IResourceBuilder<DotnetProjectResource> AddDotnetProjectForPolyglot(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        string path,
        DotnetProjectOptions? options = null)
    {
        return options is null
            ? builder.AddDotnetProject(name, path, _ => { })
            : builder.AddDotnetProject(name, path, configure => ApplyProjectResourceOptions(configure, options));
    }

    /// <summary>
    /// Adds an SDK-style .NET project or file-based C# app to the application model.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource. This name will be used for service discovery when referenced in a dependency.</param>
    /// <param name="path">The path to the file-based app file, project file, or project directory.</param>
    /// <param name="configure">An action to configure the .NET project resource options.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// This overload adds a path to a file-based app (<c>.cs</c>), an SDK-style C#, F#, or Visual Basic project
    /// (<c>.csproj</c>, <c>.fsproj</c>, or <c>.vbproj</c>), or a directory containing exactly one supported project.
    /// Directories containing multiple supported projects require an explicit project filename.
    /// If the path is not an absolute path then it will be computed relative to the app host directory.
    /// </para>
    /// <para>
    /// Visual Basic projects prefer <c>My Project/launchSettings.json</c> and fall back to
    /// <c>Properties/launchSettings.json</c>. F# scripts and non-SDK project systems are not supported.
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
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the internal addDotnetProject dispatcher export.")]
    public static IResourceBuilder<DotnetProjectResource> AddDotnetProject(this IDistributedApplicationBuilder builder, [ResourceName] string name, string path, Action<ProjectResourceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new ProjectResourceOptions();
        configure(options);

        return builder.AddDotnetProject(
            name,
            path,
            static (resourceName, directory) => new DotnetProjectResource(resourceName, directory),
            KnownLaunchConfigurationTypes.ProjectWithExternalBuild,
            resource => resource.WithProjectDefaults(options));
    }

    /// <summary>
    /// Adds a specialized .NET project resource using the shared build, launch, and publishing infrastructure.
    /// </summary>
    /// <typeparam name="TResource">The specialized .NET project resource type.</typeparam>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The resource name passed to <paramref name="resourceFactory"/>.</param>
    /// <param name="path">The project file, supported project directory, or file-based app path, relative to the AppHost directory or absolute.</param>
    /// <param name="resourceFactory">
    /// Creates an unregistered resource. Its arguments are the resource name and the resolved default working directory.
    /// The returned resource must retain the requested name and must not already belong to the application model.
    /// </param>
    /// <param name="externalBuildCapability">
    /// The integration-specific IDE launch capability that promises to honor externally built output.
    /// Ordinary .NET projects use <c>project-with-external-build.v1</c>; specialized launchers must negotiate their own contract.
    /// </param>
    /// <param name="configureResource">
    /// Applies framework defaults and debug support after managed metadata is attached, but before final launch wiring.
    /// The callback must not remove, replace, or add project metadata, or register the resource again.
    /// </param>
    /// <returns>The specialized resource builder for further configuration.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> or <paramref name="externalBuildCapability"/> is empty.</exception>
    /// <exception cref="InvalidOperationException">The factory returns an invalid resource or the callback changes project metadata.</exception>
    /// <remarks>
    /// <para>
    /// This method is for integration authors. Ordinary applications should use the non-generic
    /// <c>AddDotnetProject</c> overloads. The same resource object and managed metadata remain in use for
    /// defaults, coordinated builds, SDK invocation, IDE metadata, Rebuild, and container publishing.
    /// </para>
    /// <list type="table">
    /// <listheader><term>Shared factory</term><description>Integration callback</description></listheader>
    /// <item><term>Resolves paths and attaches managed metadata.</term><description>Chooses base or full framework defaults.</description></item>
    /// <item><term>Constructs and registers one resource.</term><description>Configures framework endpoints, storage, and additional telemetry.</description></item>
    /// <item><term>Coordinates builds using the supplied capability.</term><description>Selects compatible IDE debug support and produces its launch payload.</description></item>
    /// <item><term>Resolves the SDK process invocation and owns tool arguments.</term><description>Custom IDE launchers consume the supplied program arguments, including launch-profile arguments.</description></item>
    /// <item><term>Enables SDK container publishing.</term><description>Adds framework-specific deployment annotations.</description></item>
    /// </list>
    /// <para>
    /// CLI and process-fallback runs coordinate initial builds. For an IDE-launched resource, Aspire coordinates
    /// its build only when the IDE advertises <paramref name="externalBuildCapability"/>. Specialized IDE-built
    /// fallback resources are not build inputs, but wait for an existing coordinated initial-build batch before
    /// launching. An unrelated failed build can therefore block that fallback. Build-only environment values
    /// are not automatically runtime settings.
    /// </para>
    /// <para>
    /// The callback normally applies <c>WithDotnetBaseDefaults</c> or <c>WithProjectDefaults</c> exactly once,
    /// then supplies specialized debug support. Known project launchers apply their own launch-profile
    /// arguments; custom launchers receive those arguments from Aspire and must not apply them a second time.
    /// Build resources are run-only and do not appear in publish output.
    /// </para>
    /// <example>
    /// Configure an integration's resource before final launch wiring:
    /// <code lang="csharp">
    /// builder.AddDotnetProject(
    ///     "worker", "../Worker/Worker.csproj",
    ///     static (name, directory) => new CustomDotnetResource(name, directory),
    ///     "custom-dotnet-with-external-build.v1",
    ///     resource => ConfigureCustomFramework(resource));
    /// </code>
    /// </example>
    /// </remarks>
    [Experimental("ASPIREPROJECTS001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExportIgnore(Reason = "Integration-authoring API with resource-factory and configuration delegates; not an AppHost SDK export.")]
    public static IResourceBuilder<TResource> AddDotnetProject<TResource>(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        string path,
        Func<string, string, TResource> resourceFactory,
        string externalBuildCapability,
        Action<IResourceBuilder<TResource>> configureResource)
        where TResource : DotnetProjectResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(resourceFactory);
        ArgumentException.ThrowIfNullOrEmpty(externalBuildCapability);
        ArgumentNullException.ThrowIfNull(configureResource);

        path = PathNormalizer.NormalizePathForCurrentPlatform(Path.Combine(builder.AppHostDirectory, path));

        // The app host's own build configuration (Debug/Release) is propagated to every child launch
        // so process and IDE launchers resolve the output produced by the coordinated build.
        var configuration = builder.AppHostAssembly?.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration;
        var projectMetadata = new DotnetProjectMetadata(path, configuration, name);
        var buildCoordinator = DotnetProjectBuildCoordinator.Prepare(builder, projectMetadata, externalBuildCapability);

        // ExecutableResource requires a working directory. Use the project/app directory so the process
        // launches from the same place a ProjectResource would (DCP used Path.GetDirectoryName(ProjectPath)).
        // Accessing ProjectPath also resolves directory shorthand. Unresolved paths are validated
        // during launch-settings discovery or resource startup, depending on launch-profile options.
        var workingDirectory = Path.GetDirectoryName(projectMetadata.ProjectPath) ?? builder.AppHostDirectory;
        var iconName = Path.GetExtension(projectMetadata.ProjectPath).ToLowerInvariant() switch
        {
            ".fsproj" => "CodeFsRectangle",
            ".vbproj" => "CodeVbRectangle",
            _ => "CodeCsRectangle"
        };

        var app = resourceFactory(name, workingDirectory);
        if (app is null || !string.Equals(app.Name, name, StringComparison.Ordinal) || builder.Resources.Contains(app))
        {
            throw new InvalidOperationException("The .NET resource factory must return an unregistered resource with the requested name.");
        }

        var resource = builder.AddResource(app)
                              .WithAnnotation(projectMetadata)
                              .WithIconName(iconName);
        configureResource(resource);
        ValidateMetadata();
        resource.WithDotnetProgramPublishing();
        var projectLaunchConfigurationType = resource.Resource.Annotations
            .OfType<SupportsDebuggingAnnotation>()
            .LastOrDefault()
            ?.LaunchConfigurationType
            ?? KnownLaunchConfigurationTypes.Project;

        DotnetProjectBuildCoordinator.Configure(resource, buildCoordinator);
        var defaultRunWorkingDirectory = resource.Resource.WorkingDirectory;

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
                    var coordinator = buildCoordinator ?? throw new InvalidOperationException(
                        "A coordinated .NET project build must have a build coordinator.");
                    // Persistent explicit-start resources create their DCP object before BeforeResourceStarted is raised.
                    // Keep the run-property query behind the build barrier even on that eager configuration path.
                    var runProperties = await ResolveRunPropertiesAfterBuildAsync(
                        coordinator,
                        resource.Resource,
                        ctx.ExecutionContext.Services,
                        cancellationToken => projectMetadata.RunPropertiesResolver(
                            metadata.ProjectPath,
                            projectMetadata.BuildConfiguration,
                            projectMetadata.BuildEnvironment,
                            buildWorkingDirectory,
                            ctx.Logger,
                            cancellationToken),
                        ctx.CancellationToken).ConfigureAwait(false);
                    projectMetadata.SetBuildTarget(runProperties.TargetPath, runProperties.UseAppHost);
                    var executableAnnotation = ctx.Resource.Annotations.OfType<ExecutableAnnotation>().Last();
                    executableAnnotation.Command = runProperties.Command;
                    if (!executableAnnotation.WorkingDirectoryExplicitlySet)
                    {
                        executableAnnotation.WorkingDirectory = string.IsNullOrEmpty(runProperties.WorkingDirectory)
                            ? defaultRunWorkingDirectory
                            : runProperties.WorkingDirectory;
                    }

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
                    ctx.Args.Add(metadata.SuppressBuild ? "--no-build" : "--no-cache");
                }
                else if (metadata.SuppressBuild)
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
                || resource.HasAnnotationOfType<ExcludeLaunchProfileAnnotation>()
                || (resource.SupportsDebugging(builder.Configuration, out var debugAnnotation)
                    && debugAnnotation.LaunchConfigurationType is KnownLaunchConfigurationTypes.Project or KnownLaunchConfigurationTypes.ProjectWithExternalBuild))
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
            ValidateMetadata();
            projectMetadata.ValidateProjectPath();

            // The minimum-SDK check for file-based apps is applied by WithProjectDefaults.
            return Task.CompletedTask;
        });

        builder.OnBeforeStart((_, _) =>
        {
            ValidateMetadata();
            return Task.CompletedTask;
        });

        return resource;

        void ValidateMetadata()
        {
            if (!ReferenceEquals(app.GetProjectMetadata(), projectMetadata))
            {
                throw new InvalidOperationException(
                    $"Project metadata for resource '{name}' must remain the managed metadata supplied by {nameof(AddDotnetProject)}.");
            }
        }
    }

    internal static async Task<DotnetProjectRunProperties> ResolveRunPropertiesAfterBuildAsync(
        DotnetProjectBuildCoordinator.CoordinatorState coordinator,
        DotnetProjectResource resource,
        IServiceProvider services,
        Func<CancellationToken, Task<DotnetProjectRunProperties>> resolver,
        CancellationToken cancellationToken)
    {
        await coordinator.WaitForBuildCompletionAsync(
            resource,
            services,
            cancellationToken).ConfigureAwait(false);

        return await resolver(cancellationToken).ConfigureAwait(false);
    }

    private static void ApplyProjectResourceOptions(ProjectResourceOptions target, DotnetProjectOptions source)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);

        target.LaunchProfileName = source.LaunchProfileName;
        target.ExcludeLaunchProfile = source.ExcludeLaunchProfile;
        target.ExcludeKestrelEndpoints = source.ExcludeKestrelEndpoints;
    }
}
