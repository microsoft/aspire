// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRELAUNCHGROUPS001 // Internal use of the experimental launch-group annotation.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Orchestrator;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
    /// Selecting a group marks every resource without that membership for explicit start, including ungrouped resources.
    /// Dependencies and children must be assigned membership explicitly; selection does not change lifecycle commands.
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
