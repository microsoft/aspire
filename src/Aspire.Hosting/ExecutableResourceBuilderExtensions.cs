// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding executable resources to the <see cref="IDistributedApplicationBuilder"/> application model.
/// </summary>
public static class ExecutableResourceBuilderExtensions
{
    /// <summary>
    /// Adds an executable resource to the application model.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="command">The executable path. This can be a fully qualified path or a executable to run from the shell/command line.</param>
    /// <param name="workingDirectory">The working directory of the executable.</param>
    /// <param name="args">The arguments to the executable.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    /// <remarks>
    /// You can run any executable command using its full path.
    /// As a security feature, Aspire doesn't run executable unless the command is located in a path listed in the PATH environment variable.
    /// <para/>
    /// To run an executable file that's in the current directory, specify the full path or use the relative path <c>./</c> to represent the current directory.
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<ExecutableResource> AddExecutable(this IDistributedApplicationBuilder builder, [ResourceName] string name, string command, string workingDirectory, params string[]? args)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(workingDirectory);

        return AddExecutable(builder, name, command, workingDirectory, (object[]?)args);
    }

    /// <summary>
    /// Adds an executable resource to the application model.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="command">The executable path. This can be a fully qualified path or a executable to run from the shell/command line.</param>
    /// <param name="workingDirectory">The working directory of the executable.</param>
    /// <param name="args">The arguments to the executable.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>This method is not available in polyglot app hosts. Use the string[] overload instead.</remarks>
    [AspireExportIgnore(Reason = "Uses object[] parameter which is not ATS-compatible. String[] overload is exported.")]
    public static IResourceBuilder<ExecutableResource> AddExecutable(this IDistributedApplicationBuilder builder, [ResourceName] string name, string command, string workingDirectory, params object[]? args)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(workingDirectory);

        workingDirectory = PathNormalizer.NormalizePathForCurrentPlatform(Path.Combine(builder.AppHostDirectory, workingDirectory));

        var executable = new ExecutableResource(name, command, workingDirectory);
        return builder.AddResource(executable)
                      .WithArgs(context =>
                      {
                          if (args is not null)
                          {
                              context.Args.AddRange(args);
                          }
                      });
    }

    /// <summary>
    /// Adds annotation to <see cref="ExecutableResource" /> to support containerization during deployment.
    /// </summary>
    /// <typeparam name="T">Type of executable resource</typeparam>
    /// <param name="builder">Resource builder</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the overload with the optional configure callback.")]
    public static IResourceBuilder<T> PublishAsDockerFile<T>(this IResourceBuilder<T> builder) where T : ExecutableResource
    {
        return builder.PublishAsDockerFile(c => { });
    }

    /// <summary>
    /// Adds annotation to <see cref="ExecutableResource" /> to support containerization during deployment.
    /// The resulting container image is built, and when the optional <paramref name="buildArgs"/> are provided
    /// they're used with <c>docker build --build-arg</c>.
    /// </summary>
    /// <typeparam name="T">Type of executable resource</typeparam>
    /// <param name="builder">Resource builder</param>
    /// <param name="buildArgs">The optional build arguments, used with <c>docker build --build-args</c>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [Obsolete("Use builder.PublishAsDockerFile(c => c.WithBuildArg(name, value)) instead.")]
    public static IResourceBuilder<T> PublishAsDockerFile<T>(this IResourceBuilder<T> builder, IEnumerable<DockerBuildArg>? buildArgs) where T : ExecutableResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.PublishAsDockerFile(c =>
        {
            foreach (var arg in buildArgs ?? [])
            {
                c.WithBuildArg(arg.Name, arg.Value);
            }
        });
    }

    /// <summary>
    /// Adds support for containerizing this <see cref="ExecutableResource"/> during deployment.
    /// The resulting container image is built, and when the optional <paramref name="configure"/> action is provided,
    /// it is used to configure the container resource.
    /// </summary>
    /// <ats-summary>Publishes an executable as a Docker file</ats-summary>
    /// <remarks>
    /// When the executable resource is projected as a container resource, the arguments to the executable
    /// are not used. This is because arguments to the executable often contain physical paths that are not valid
    /// in the container. The container can be set up with the correct arguments using the <paramref name="configure"/> action.
    /// </remarks>
    /// <typeparam name="T">Type of executable resource</typeparam>
    /// <param name="builder">Resource builder</param>
    /// <param name="configure">Optional action to configure the container resource</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport(RunSyncOnBackgroundThread = true)]
    public static IResourceBuilder<T> PublishAsDockerFile<T>(this IResourceBuilder<T> builder, Action<IResourceBuilder<ContainerResource>>? configure)
        where T : ExecutableResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        var hasProjection = builder.Resource.TrySelectProjection(
            builder.ApplicationBuilder.ExecutionContext,
            out _);

        builder.WithContainerProjection(
            DistributedApplicationOperation.Publish,
            container =>
            {
                if (!hasProjection)
                {
                    container.WithImage(builder.Resource.Name);
                    container.WithDockerfile(contextPath: builder.Resource.WorkingDirectory);
                }
            });

        if (!builder.Resource.TrySelectProjection(builder.ApplicationBuilder.ExecutionContext, out var projection) ||
            projection is not ContainerResource containerProjection)
        {
            return builder;
        }

        builder.WithContainerProjection(
            DistributedApplicationOperation.Publish,
            container =>
            {
                // The image entrypoint replaces the host executable, so arguments configured before
                // this conversion often contain host-only paths and must not reach the container.
                container.WithArgs(context => context.Args.Clear());
                configure?.Invoke(container);
            });

        return builder.WithManifestPublishingCallback(
            context => context.WriteContainerAsync(containerProjection));
    }

    /// <summary>
    /// Sets the command for the executable resource.
    /// </summary>
    /// <typeparam name="T">Type of executable resource.</typeparam>
    /// <param name="builder">Builder for the executable resource.</param>
    /// <param name="command">Command.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport("withExecutableCommand")]
    public static IResourceBuilder<T> WithCommand<T>(this IResourceBuilder<T> builder, string command) where T : ExecutableResource
    {
        ArgumentException.ThrowIfNullOrEmpty(command);

        var executableAnnotation = builder.Resource.Annotations.OfType<ExecutableAnnotation>().LastOrDefault();
        if (executableAnnotation is { })
        {
            executableAnnotation.Command = command;
        }
        else
        {
            executableAnnotation = new ExecutableAnnotation
            {
                Command = command,
                WorkingDirectory = string.Empty
            };
            builder.Resource.Annotations.Add(executableAnnotation);
        }

        return builder;
    }

    /// <summary>
    /// Sets the working directory for the executable resource.
    /// </summary>
    /// <typeparam name="T">Type of executable resource.</typeparam>
    /// <param name="builder">Builder for the executable resource.</param>
    /// <param name="workingDirectory">Working directory.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<T> WithWorkingDirectory<T>(this IResourceBuilder<T> builder, string workingDirectory) where T : ExecutableResource
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);

        if (builder.Resource.Annotations.OfType<ExecutableAnnotation>().LastOrDefault() is { } executableAnnotation)
        {
            workingDirectory = PathNormalizer.NormalizePathForCurrentPlatform(Path.Combine(builder.ApplicationBuilder.AppHostDirectory, workingDirectory));
            executableAnnotation.WorkingDirectory = workingDirectory;
            return builder;
        }

        throw new InvalidOperationException($"The resource '{builder.Resource.Name}' is missing the ExecutableAnnotation");
    }
}
