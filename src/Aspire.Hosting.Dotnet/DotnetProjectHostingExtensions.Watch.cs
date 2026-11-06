// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dotnet;

namespace Aspire.Hosting;

public static partial class DotnetProjectHostingExtensions
{
    /// <summary>
    /// Configures whether the .NET project is hot reloaded when the AppHost is started with <c>aspire run --watch</c>.
    /// </summary>
    /// <param name="builder">The .NET project resource builder.</param>
    /// <param name="enabled">
    /// <see langword="true"/> to require hot reload for the project; <see langword="false"/> to run it without hot reload.
    /// </param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining additional configuration.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// This setting only applies to <c>aspire run --watch</c>; other runs never watch the project. By default every
    /// .NET project is watched, except projects that run to completion (another resource waits for them with
    /// <c>WaitForCompletion</c>), which run without hot reload so that they can complete.
    /// </para>
    /// <para>
    /// File-based C# apps are watched like projects. A project that is watched by default but can't be (a persistent
    /// lifetime or a custom launch tool) fails the watch run with an error that names it; call <c>WithWatch(false)</c> on
    /// it to run it without hot reload. Passing <see langword="true"/> makes the requirement explicit: a project that runs
    /// to completion then fails the run as well instead of being excluded.
    /// </para>
    /// <para>
    /// The last call wins. When the AppHost runs under an IDE debug session, the IDE owns change tracking and this
    /// setting has no effect.
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

        return builder.WithAnnotation(new DotnetProjectWatchAnnotation(enabled), ResourceAnnotationMutationBehavior.Replace);
    }
}
