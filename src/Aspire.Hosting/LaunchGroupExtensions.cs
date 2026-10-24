// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Orchestrator;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aspire.Hosting;

/// <summary>
/// Provides methods for configuring named subsets of resources for local application runs.
/// </summary>
public static class LaunchGroupExtensions
{
    /// <summary>
    /// Defines the valid launch group names.
    /// </summary>
    /// <param name="builder">The application builder.</param>
    /// <param name="launchGroupNames">One or more valid group names, combined with the <c>Aspire:LaunchGroups</c> configuration array.</param>
    /// <returns>The application builder.</returns>
    /// <remarks>
    /// <para>Select one group using the <c>Aspire:LaunchGroup</c> configuration setting, for example the
    /// <c>Aspire__LaunchGroup</c> environment variable in an AppHost launch profile or
    /// <c>dotnet run -- Aspire:LaunchGroup=catalog</c>.
    /// Names are case-insensitive. Calling this method is optional: defining groups in <c>Aspire:LaunchGroups</c>
    /// configuration or configuring resource membership with <c>WithLaunchGroup</c> or <c>WithLaunchGroups</c>
    /// also enables launch group support. Without explicit definitions in code or configuration,
    /// resource membership defines the valid names.</para>
    /// <para>Without a selection, the full application runs normally. With a selection, ungrouped resources,
    /// group members, and their dependencies start normally; other resources remain available for explicit
    /// starts. Existing explicit-start settings are preserved. Launch groups do not affect publishing.</para>
    /// </remarks>
    /// <example>
    /// <code lang="csharp">
    /// builder.DefineLaunchGroups("catalog", "basket");
    /// builder.AddContainer("catalog", "catalog-image").WithLaunchGroup("catalog");
    /// </code>
    /// </example>
    /// <exception cref="ArgumentNullException">The builder, group names array, or a group name is null.</exception>
    /// <exception cref="ArgumentException">No group names are provided, or a group name is empty, has surrounding whitespace, or contains a group separator.</exception>
    [AspireExport]
    public static IDistributedApplicationBuilder DefineLaunchGroups(this IDistributedApplicationBuilder builder, params string[] launchGroupNames)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(launchGroupNames);
        if (launchGroupNames.Length == 0)
        {
            throw new ArgumentException("At least one launch group name must be specified.", nameof(launchGroupNames));
        }

        foreach (var name in launchGroupNames)
        {
            LaunchGroupAnnotation.ValidateName(name, nameof(launchGroupNames));
        }

        EnsureSupport(builder);
        builder.Services.Configure<LaunchGroupsOptions>(options => options.LaunchGroups.UnionWith(launchGroupNames));

        return builder;
    }

    /// <summary>
    /// Adds a resource to a launch group.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="launchGroupName">The case-insensitive launch group name.</param>
    /// <returns>The resource builder.</returns>
    /// <remarks>
    /// Membership is additive. Select one group with the <c>Aspire:LaunchGroup</c> configuration setting.
    /// Dependencies of selected resources are included even if they belong to another group.
    /// Ungrouped children inherit their parent's membership.
    /// </remarks>
    /// <example>
    /// <code lang="csharp">
    /// builder.AddExecutable("worker", "worker", ".").WithLaunchGroup("background");
    /// </code>
    /// </example>
    /// <exception cref="ArgumentNullException">The builder or group name is null.</exception>
    /// <exception cref="ArgumentException">The group name is invalid.</exception>
    [AspireExport]
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
    public static IResourceBuilder<T> WithLaunchGroups<T>(this IResourceBuilder<T> builder, params string[] launchGroupNames) where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(launchGroupNames);
        foreach (var name in launchGroupNames)
        {
            LaunchGroupAnnotation.ValidateName(name, nameof(launchGroupNames));
        }

        var annotations = launchGroupNames.Select(name => new LaunchGroupAnnotation(name)).ToArray();
        EnsureSupport(builder.ApplicationBuilder);
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
    /// Registers services and the pre-start step for launch groups.
    /// </summary>
    internal static void EnsureSupport(IDistributedApplicationBuilder builder)
    {
        if (builder.Services.Any(descriptor => descriptor.ServiceType == typeof(LaunchGroupService)))
        {
            return;
        }

        builder.Services.AddOptions<LaunchGroupsOptions>();
        builder.Services.TryAddSingleton<LaunchGroupService>();
        builder.Pipeline.AddStep(new PipelineStep
        {
            Name = "configure-launch-groups",
            Description = "Selects the resources that start in the active launch group.",
            RequiredBySteps = [WellKnownPipelineSteps.BeforeStart],
            Action = context =>
            {
                context.Services.GetRequiredService<LaunchGroupService>().Configure(context.Model);
                return Task.CompletedTask;
            }
        });
    }
}

internal sealed class LaunchGroupsOptions
{
    internal const string ConfigurationSectionName = "Aspire:LaunchGroups";

    public HashSet<string> LaunchGroups { get; } = new(StringComparer.OrdinalIgnoreCase);
}
