// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRELAUNCHGROUPS001 // Internal use of the experimental launch-group annotation.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// Provides methods for configuring named subsets of resources for local application runs.
/// </summary>
[Experimental("ASPIRELAUNCHGROUPS001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public static class LaunchGroupExtensions
{
    /// <summary>
    /// Adds a resource to a launch group.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="launchGroupName">The case-insensitive launch group name.</param>
    /// <returns>The resource builder.</returns>
    /// <remarks>
    /// Membership is additive. Select one group with the <c>Aspire:LaunchGroup</c> configuration setting.
    /// Selecting a group marks resources without that membership for explicit start, including ungrouped resources,
    /// unless they have an <see cref="ExcludeFromLaunchGroupSelectionAnnotation"/>.
    /// Structural children without their own memberships inherit their parent's memberships. Their own memberships
    /// override that default. Display relationships and references do not inherit membership or select dependencies.
    /// Selection does not change lifecycle commands.
    /// Valid group names are inferred from membership unless explicitly defined in <c>Aspire:LaunchGroups</c>.
    /// Use the <c>Aspire__LaunchGroup</c> environment variable or <c>dotnet run -- Aspire:LaunchGroup=catalog</c>
    /// to select a group. No selection preserves normal startup, and publishing is unaffected.
    /// </remarks>
    /// <example>
    /// <code lang="csharp">
    /// builder.AddExecutable("worker", "worker", ".").WithLaunchGroup("background");
    /// </code>
    /// </example>
    /// <exception cref="ArgumentNullException">The builder or group name is null.</exception>
    /// <exception cref="ArgumentException">The group name is invalid.</exception>
    [AspireExport]
    [Experimental("ASPIRELAUNCHGROUPS001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<T> WithLaunchGroup<T>(this IResourceBuilder<T> builder, string launchGroupName) where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        LaunchGroupAnnotation.ValidateName(launchGroupName, nameof(launchGroupName));

        return builder.WithLaunchGroups(launchGroupName);
    }

    /// <summary>
    /// Adds a resource to multiple launch groups.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="launchGroupNames">The case-insensitive launch group names.</param>
    /// <returns>The resource builder.</returns>
    /// <remarks>
    /// Repeated calls add membership rather than replacing it. No selection preserves normal startup.
    /// Unselected resources can be started later from the dashboard or resource commands.
    /// </remarks>
    /// <example>
    /// <code lang="csharp">
    /// builder.AddContainer("cache", "redis").WithLaunchGroups("catalog", "basket");
    /// </code>
    /// </example>
    /// <exception cref="ArgumentNullException">The builder, group names array, or a group name is null.</exception>
    /// <exception cref="ArgumentException">A group name is invalid.</exception>
    [AspireExport]
    [Experimental("ASPIRELAUNCHGROUPS001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<T> WithLaunchGroups<T>(this IResourceBuilder<T> builder, params string[] launchGroupNames) where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(launchGroupNames);
        foreach (var name in launchGroupNames)
        {
            LaunchGroupAnnotation.ValidateName(name, nameof(launchGroupNames));
        }

        var annotations = launchGroupNames.Select(name => new LaunchGroupAnnotation(name)).ToArray();
        foreach (var annotation in annotations)
        {
            if (!builder.Resource.Annotations.OfType<LaunchGroupAnnotation>()
                .Any(existing => StringComparer.OrdinalIgnoreCase.Equals(existing.LaunchGroupName, annotation.LaunchGroupName)))
            {
                builder.WithAnnotation(annotation, ResourceAnnotationMutationBehavior.Append);
            }
        }

        return builder;
    }

    /// <summary>
    /// Declares a local resource dependency that is optional during initial launch-group selection.
    /// </summary>
    /// <typeparam name="T">The dependent resource type.</typeparam>
    /// <typeparam name="TDependency">The dependency resource type.</typeparam>
    /// <param name="builder">The dependent resource builder.</param>
    /// <param name="dependency">The optional dependency resource builder.</param>
    /// <returns>The resource builder.</returns>
    /// <remarks>
    /// This declares a reference relationship for launch-group validation. It does not add a readiness wait or
    /// change behavior when no launch group is selected. Use it only when the dependent resource can run without
    /// the dependency during initial startup.
    /// </remarks>
    /// <example>
    /// <code lang="csharp">
    /// var frontend = builder.AddProject("frontend", "../Frontend/Frontend.csproj")
    ///     .WithLaunchGroup("frontend")
    ///     .WithOptionalLaunchGroupDependency(metrics);
    /// </code>
    /// </example>
    [AspireExportIgnore(Reason = "The experimental launch-group helper is C#-only.")]
    [Experimental("ASPIRELAUNCHGROUPS001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<T> WithOptionalLaunchGroupDependency<T, TDependency>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<TDependency> dependency)
        where T : IResource
        where TDependency : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(dependency);

        builder.WithReferenceRelationship(dependency.Resource);
        return builder.WithAnnotation(new OptionalLaunchGroupDependencyAnnotation(dependency.Resource));
    }
}
