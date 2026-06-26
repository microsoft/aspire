// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure.Azd;

/// <summary>
/// Represents a single azd environment, as stored under the project's <c>.azure</c> directory.
/// </summary>
/// <remarks>
/// azd persists per-environment configuration in <c>.azure/&lt;name&gt;/.env</c>. These values capture
/// the deployment target (subscription, location, resource group) and the outputs of the most recent
/// provisioning, and are surfaced here so an importer can reuse them instead of re-prompting the user.
/// </remarks>
public sealed class AzdEnvironment
{
    internal AzdEnvironment(string name, IReadOnlyDictionary<string, string> values)
    {
        Name = name;
        Values = values;
    }

    /// <summary>
    /// Gets the environment name (the directory name under <c>.azure</c>).
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the raw key/value pairs read from the environment's <c>.env</c> file.
    /// </summary>
    public IReadOnlyDictionary<string, string> Values { get; }

    /// <summary>
    /// Gets the Azure subscription id (<c>AZURE_SUBSCRIPTION_ID</c>), if present.
    /// </summary>
    public string? SubscriptionId => GetValueOrDefault("AZURE_SUBSCRIPTION_ID");

    /// <summary>
    /// Gets the Azure location/region (<c>AZURE_LOCATION</c>), if present.
    /// </summary>
    public string? Location => GetValueOrDefault("AZURE_LOCATION");

    /// <summary>
    /// Gets the target resource group (<c>AZURE_RESOURCE_GROUP</c>), if present.
    /// </summary>
    public string? ResourceGroup => GetValueOrDefault("AZURE_RESOURCE_GROUP");

    /// <summary>
    /// Gets the deploying principal's object id (<c>AZURE_PRINCIPAL_ID</c>), if present.
    /// </summary>
    public string? PrincipalId => GetValueOrDefault("AZURE_PRINCIPAL_ID");

    /// <summary>
    /// Gets the Azure tenant id (<c>AZURE_TENANT_ID</c>), if present.
    /// </summary>
    public string? TenantId => GetValueOrDefault("AZURE_TENANT_ID");

    /// <summary>
    /// Gets the deploying principal's type (<c>AZURE_PRINCIPAL_TYPE</c>, for example <c>User</c> or
    /// <c>ServicePrincipal</c>), if present.
    /// </summary>
    public string? PrincipalType => GetValueOrDefault("AZURE_PRINCIPAL_TYPE");

    /// <summary>
    /// Gets the value for the specified key, or <see langword="null"/> when it is not present.
    /// </summary>
    /// <param name="key">The environment variable name.</param>
    /// <returns>The value, or <see langword="null"/>.</returns>
    public string? GetValueOrDefault(string key)
        => Values.TryGetValue(key, out var value) ? value : null;
}
