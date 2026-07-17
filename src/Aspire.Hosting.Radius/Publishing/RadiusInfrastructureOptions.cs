// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRERADIUS004 // Experimental: ConfigureRadiusInfrastructure escape-hatch construct types are consumed internally by the publisher.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.Radius.Publishing.Constructs;
using Azure.Provisioning;

namespace Aspire.Hosting.Radius.Publishing;

/// <summary>
/// Exposes strongly-typed mutable collections of Radius construct classes for
/// AST customization via the <c>ConfigureRadiusInfrastructure</c> callback.
/// </summary>
/// <remarks>
/// <b>Preview surface.</b> The construct shapes exposed here mirror the still-
/// evolving Radius preview schemas and are expected to change as those schemas
/// stabilize. Treat <c>ConfigureRadiusInfrastructure</c> and the construct types
/// referenced by this options bag as an advanced escape hatch that may shift
/// shape across releases of <c>Aspire.Hosting.Radius</c>.
/// </remarks>
[Experimental("ASPIRERADIUS004", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class RadiusInfrastructureOptions
{
    /// <summary>
    /// Gets the list of <c>Radius.Core/environments</c> constructs.
    /// </summary>
    public List<RadiusEnvironmentConstruct> Environments { get; } = [];

    /// <summary>
    /// Gets the list of <c>Radius.Core/applications</c> constructs.
    /// </summary>
    public List<RadiusApplicationConstruct> Applications { get; } = [];

    /// <summary>
    /// Gets the list of <c>Radius.Core/recipePacks</c> constructs.
    /// </summary>
    public List<RadiusRecipePackConstruct> RecipePacks { get; } = [];

    /// <summary>
    /// Gets the list of resource type instance constructs
    /// (e.g., <c>Radius.Data/redisCaches</c>, <c>Radius.Messaging/rabbitMQQueues</c>).
    /// </summary>
    public List<RadiusResourceTypeConstruct> ResourceTypeInstances { get; } = [];

    /// <summary>
    /// Gets the list of <c>Radius.Compute/containers</c> workload constructs.
    /// </summary>
    public List<RadiusContainerConstruct> Containers { get; } = [];

    /// <summary>
    /// Gets the list of legacy <c>Applications.Core/environments</c> constructs
    /// emitted when one or more targeted resources fall back to a legacy
    /// <c>Applications.*</c> type (e.g., Redis, Mongo, RabbitMQ, Dapr).
    /// </summary>
    public List<LegacyApplicationEnvironmentConstruct> LegacyEnvironments { get; } = [];

    /// <summary>
    /// Gets the list of legacy <c>Applications.Core/applications</c> constructs
    /// paired with <see cref="LegacyEnvironments"/>.
    /// </summary>
    public List<LegacyApplicationConstruct> LegacyApplications { get; } = [];

    /// <summary>
    /// Gets the top-level Bicep parameters emitted for secret/parameter values referenced by
    /// container environment variables. Secret sources are declared <c>@secure()</c> so their
    /// values are supplied at deploy time via <c>rad deploy --parameters</c> rather than inlined.
    /// </summary>
    public List<ProvisioningParameter> Parameters { get; } = [];
}
