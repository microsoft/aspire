// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Provisioning.Expressions;
using Azure.Provisioning.Primitives;
using Azure.Provisioning;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Uses the Aspire v8.x naming scheme to provide default names for Azure resources.
/// </summary>
/// <remarks>
/// Can be used to keep consistent Azure resource names with Aspire 8.x.
/// </remarks>
public sealed class AspireV8ResourceNamePropertyResolver : DynamicResourceNamePropertyResolver
{
    /// <inheritdoc/>
    public override BicepValue<string>? ResolveName(ProvisioningBuildOptions options, ProvisionableResource resource, ResourceNameRequirements requirements)
    {
        var suffix = GetUniqueSuffix(options, resource);
        var prefix = GetNamePrefix(resource);

        return BicepFunction.ToLower(BicepFunction.Take(BicepFunction.Interpolate($"{prefix}{suffix}"), 24));
    }

    /// <summary>
    /// Use the 'aspire-resource-name' tag to get the prefix for the resource name, if available.
    /// </summary>
    /// <remarks>
    /// The BicepIdentifier has already had any dashes changed to underscores, which we don't want to use since Aspire 8.x used the dashes.
    /// </remarks>
    private static string GetNamePrefix(ProvisionableResource resource)
    {
        BicepValue<string>? aspireResourceName = null;
        if (resource.ProvisionableProperties.TryGetValue("Tags", out var tags) &&
            tags is BicepDictionary<string> tagDictionary)
        {
            tagDictionary.TryGetValue("aspire-resource-name", out aspireResourceName);
        }

        return aspireResourceName?.Value ?? resource.BicepIdentifier;
    }
}
