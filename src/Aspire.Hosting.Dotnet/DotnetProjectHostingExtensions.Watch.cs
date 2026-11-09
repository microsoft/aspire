// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dotnet;

namespace Aspire.Hosting;

public static partial class DotnetProjectHostingExtensions
{
    /// <summary>
    /// Overrides the AppHost's resource-watch default for this .NET project during a non-IDE run.
    /// </summary>
    /// <param name="builder">The .NET project resource builder.</param>
    /// <param name="enabled">
    /// <see langword="true"/> to require hot reload for the project; <see langword="false"/> to run it without hot reload.
    /// </param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining additional configuration.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// Compatible C# resources added with <c>AddDotnetProject</c> are watched by default in Run mode, independently
    /// of the CLI's <c>--watch</c> option (which controls AppHost edits only). An explicit resource choice wins over
    /// <see cref="DistributedApplicationOptions.DefaultResourceWatchEnabled"/> and <c>AppHost:Run:WatchEnabled</c>.
    /// Legacy <c>AddProject</c> resources are not affected.
    /// </para>
    /// <para>
    /// File-based C# apps are watched like projects. Implicitly incompatible projects retain ordinary build and launch:
    /// this includes completion-sensitive resources, persistent lifetimes, custom launch tools, and unsupported
    /// SDK/runtime/build contexts. Passing <see langword="true"/> instead requires watch and fails the run if that
    /// requirement cannot be satisfied, including when the launcher specifies <c>--no-build</c>.
    /// </para>
    /// <para>
    /// The last call wins. When the AppHost runs under an IDE debug session, the IDE owns change tracking and this
    /// setting has no effect. Publish, inspect, discovery, and native-server contexts do not activate resource watching.
    /// </para>
    /// </remarks>
    /// <example>
    /// Run a database migrator once, without hot reload, while the API is watched:
    /// <code lang="csharp">
    /// var migrator = builder.AddDotnetProject("migrator", "../Migrator/Migrator.csproj")
    ///     .WithWatch(false);
    ///
    /// builder.AddDotnetProject("api", "../Api/Api.csproj")
    ///     .WaitForCompletion(migrator);
    /// </code>
    /// </example>
    [AspireExport]
    public static IResourceBuilder<DotnetProjectResource> WithWatch(
        this IResourceBuilder<DotnetProjectResource> builder,
        bool enabled = true)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.WithAnnotation(new DotnetProjectWatchAnnotation(enabled), ResourceAnnotationMutationBehavior.Replace);
        if (enabled)
        {
            Dotnet.Watch.DotnetWatchCoordinator.Enable(builder);
        }

        return builder;
    }
}
