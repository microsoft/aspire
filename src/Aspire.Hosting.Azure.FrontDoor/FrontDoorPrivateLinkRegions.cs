// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Frozen;
using Azure.Provisioning.Expressions;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Chooses the region of the Front Door managed private endpoint for an origin in a given region.
/// </summary>
/// <remarks>
/// <para>
/// Front Door Private Link is only available in some regions, and the private endpoint can be in a different region
/// than the origin, at the cost of an extra network hop:
/// https://learn.microsoft.com/azure/frontdoor/private-link#region-availability
/// </para>
/// <para>
/// This table only moves an origin to a supported region in the same Azure geography
/// (https://azure.microsoft.com/explore/global-infrastructure/geographies/), so a remap never changes the data
/// residency boundary of the connection. A region in a geography that has no supported region isn't listed: the
/// deployment fails in Front Door, and the origin's <c>SharedPrivateLinkResource.PrivateLinkLocation</c> can be set in
/// <c>ConfigureInfrastructure</c> to pick a region in another geography on purpose.
/// </para>
/// <para>
/// MAINTENANCE: update this table when Front Door adds or removes Private Link regions or when Azure adds regions. Moving
/// an entry changes the private endpoint of existing deployments, which creates a new connection request that must be
/// approved. Last reviewed against the documentation above on 2026-10-22.
/// </para>
/// </remarks>
internal static class FrontDoorPrivateLinkRegions
{
    public const string BicepVariableName = "privateLinkRegions";

    // Keys are the normalized region names (lowercase, no spaces) that the environment outputs. Regions that Front Door
    // Private Link supports aren't listed, because they are used as they are.
    public static FrozenDictionary<string, string> Remapped { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // United States
        ["westus"] = "westus2",
        ["northcentralus"] = "centralus",
        ["westcentralus"] = "centralus",
        // Early Updates Access Program (EUAP) canary regions and their STG counterparts stay in the US geography.
        ["eastus2euap"] = "eastus2",
        ["centraluseuap"] = "centralus",
        ["eastusstg"] = "eastus",
        ["southcentralusstg"] = "southcentralus",
        // Canada
        ["canadaeast"] = "canadacentral",
        // Brazil
        ["brazilsoutheast"] = "brazilsouth",
        // United Kingdom
        ["ukwest"] = "uksouth",
        // France
        ["francesouth"] = "francecentral",
        // Germany
        ["germanynorth"] = "germanywestcentral",
        // Norway
        ["norwaywest"] = "norwayeast",
        // Sweden
        ["swedensouth"] = "swedencentral",
        // South Africa
        ["southafricawest"] = "southafricanorth",
        // United Arab Emirates
        ["uaecentral"] = "uaenorth",
        // Australia
        ["australiasoutheast"] = "australiaeast",
        ["australiacentral"] = "australiaeast",
        ["australiacentral2"] = "australiaeast",
        // India
        ["southindia"] = "centralindia",
        ["westindia"] = "centralindia",
        ["jioindiacentral"] = "centralindia",
        ["jioindiawest"] = "centralindia",
        // Japan
        ["japanwest"] = "japaneast",
        // Korea
        ["koreasouth"] = "koreacentral",
        // China
        ["chinaeast"] = "chinaeast3",
        ["chinaeast2"] = "chinaeast3",
        ["chinanorth"] = "chinanorth3",
        ["chinanorth2"] = "chinanorth3",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    // The Bicep object that maps a region to the region of its private endpoint:
    //   var privateLinkRegions = {
    //     westus: 'westus2'
    //     ...
    //   }
    public static ObjectExpression CreateBicepObject()
    {
        return new ObjectExpression(
            [.. Remapped.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new PropertyExpression(pair.Key, new StringLiteralExpression(pair.Value)))]);
    }

    // Looks up the region in the object above and keeps the region when it has no entry: privateLinkRegions[?region] ?? region.
    public static BinaryExpression CreateLookup(string variableName, string regionIdentifier)
    {
        return new BinaryExpression(
            new SafeIndexExpression(new IdentifierExpression(variableName), new IdentifierExpression(regionIdentifier)),
            BinaryBicepOperator.Coalesce,
            new IdentifierExpression(regionIdentifier));
    }
}
