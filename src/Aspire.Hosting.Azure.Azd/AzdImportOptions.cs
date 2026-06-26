// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.Azure.Azd;

/// <summary>
/// Options that control how an azd project is imported into an Aspire application model.
/// </summary>
[Experimental("ASPIREAZUREAZD001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class AzdImportOptions
{
    /// <summary>
    /// Gets or sets the azd environment name to load from the project's <c>.azure</c> directory.
    /// </summary>
    /// <value>
    /// When <see langword="null"/> (the default), the default environment recorded in
    /// <c>.azure/config.json</c> is used.
    /// </value>
    public string? EnvironmentName { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the importer should create Azure compute environments
    /// (Azure Container Apps or Azure App Service) on demand for the services it imports.
    /// </summary>
    /// <value>
    /// Defaults to <see langword="true"/>. Set to <see langword="false"/> when the app host already
    /// declares its own compute environment that imported services should bind to.
    /// </value>
    public bool CreateComputeEnvironments { get; set; } = true;

    /// <summary>
    /// Gets or sets the name used for the Azure Container Apps environment created for
    /// <c>host: containerapp</c> services.
    /// </summary>
    public string ContainerAppEnvironmentName { get; set; } = "aca";

    /// <summary>
    /// Gets or sets the name used for the Azure App Service environment created for
    /// <c>host: appservice</c> services.
    /// </summary>
    public string AppServiceEnvironmentName { get; set; } = "appservice";

    /// <summary>
    /// Gets or sets a value indicating whether imported Azure resources that support a local
    /// development experience are switched to a container or emulator when the app host runs locally
    /// (<c>aspire run</c> / F5).
    /// </summary>
    /// <value>
    /// Defaults to <see langword="true"/>. When enabled, resources are run locally without touching
    /// Azure: Azure Cache for Redis and Azure Database for PostgreSQL run as containers, and Azure
    /// Storage, Cosmos DB, Service Bus, and Event Hubs run as emulators. This only affects run mode;
    /// publish/deploy always targets the real Azure resources. Resources without a local option (Key
    /// Vault, OpenAI, AI Search) continue to bind to Azure using the reused <c>.azure</c> environment.
    /// </value>
    public bool UseEmulatorsForLocalRun { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether azd resources marked <c>existing: true</c> are bound to
    /// the already-provisioned Azure resource instead of being provisioned again.
    /// </summary>
    /// <value>
    /// Defaults to <see langword="true"/>. When enabled, an imported resource flagged as existing in
    /// <c>azure.yaml</c> is annotated as existing (equivalent to calling <c>AsExisting</c>) using the
    /// azd resource name and the resource group from the reused <c>.azure</c> environment, so neither
    /// <c>aspire run</c> nor <c>aspire publish</c> recreates a resource the customer already owns.
    /// </value>
    public bool BindExistingResources { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the subscription, location, resource group, and tenant
    /// recorded in the project's <c>.azure</c> environment are reused as the Aspire provisioning target.
    /// </summary>
    /// <value>
    /// Defaults to <see langword="true"/>. When enabled, the importer seeds the <c>Azure:*</c>
    /// configuration section (consumed by <c>AddAzureProvisioning</c>) from the loaded environment so a
    /// migrated app provisions into the same subscription/region/resource group azd used. Values the app
    /// host already configured are never overwritten.
    /// </value>
    public bool ReuseAzureEnvironment { get; set; } = true;

    /// <summary>
    /// Gets the list of custom resource mappers consulted before the built-in mappers.
    /// </summary>
    /// <remarks>
    /// Add mappers here to support azd resource types the built-in mappers do not cover, or to
    /// override the default mapping for a given type. Mappers are consulted in order.
    /// </remarks>
    public IList<IAzdResourceMapper> ResourceMappers { get; } = [];
}
