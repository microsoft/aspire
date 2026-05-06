// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Azure;
using Azure.Provisioning.CognitiveServices;

namespace Aspire.Hosting;

/// <summary>
/// Represents ATS-compatible Microsoft Foundry roles.
/// </summary>
internal enum FoundryRole
{
    /// <summary>
    /// Allows full management of Azure OpenAI resources.
    /// </summary>
    CognitiveServicesOpenAIContributor,

    /// <summary>
    /// Allows using Azure OpenAI models for inference.
    /// </summary>
    CognitiveServicesOpenAIUser,

    /// <summary>
    /// Allows access to Azure Cognitive Services resources.
    /// </summary>
    CognitiveServicesUser,
}

internal static class FoundryRoleHelpers
{
    internal static CognitiveServicesBuiltInRole[] ToCognitiveServicesBuiltInRoles(IReadOnlyList<FoundryRole>? roles)
    {
        if (roles is null || roles.Count == 0)
        {
            return [];
        }

        var builtInRoles = new CognitiveServicesBuiltInRole[roles.Count];
        for (var i = 0; i < roles.Count; i++)
        {
            builtInRoles[i] = roles[i] switch
            {
                FoundryRole.CognitiveServicesOpenAIContributor => CognitiveServicesBuiltInRole.CognitiveServicesOpenAIContributor,
                FoundryRole.CognitiveServicesOpenAIUser => CognitiveServicesBuiltInRole.CognitiveServicesOpenAIUser,
                FoundryRole.CognitiveServicesUser => CognitiveServicesBuiltInRole.CognitiveServicesUser,
                _ => throw new ArgumentException($"'{roles[i]}' is not a valid {nameof(FoundryRole)} value.", nameof(roles))
            };
        }

        return builtInRoles;
    }
}

internal static class FoundryProjectRoleHelpers
{
    private const string AzureAIUserRoleId = "53ca6127-db72-4b80-b1b0-d745d6d5456d";
    private const string AzureAIUserRoleName = "Azure AI User";

    internal static HashSet<RoleDefinition> CreateDefaultRoleDefinitions() =>
    [
        new RoleDefinition(AzureAIUserRoleId, AzureAIUserRoleName)
    ];
}
