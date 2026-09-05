// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Foundry;
using Azure.Provisioning.CognitiveServices;

namespace Aspire.Hosting;

/// <summary>
/// Represents ATS-compatible Microsoft Foundry roles.
/// </summary>
internal enum FoundryRole
{
    /// <summary>
    /// Allows building and testing with models and agents in Microsoft Foundry.
    /// </summary>
    FoundryUser,

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

/// <summary>
/// Converts ATS-compatible Microsoft Foundry roles to Azure Provisioning role values.
/// </summary>
internal static class FoundryRoleExtensions
{
    /// <summary>
    /// Converts a Microsoft Foundry role to its Azure Provisioning equivalent.
    /// </summary>
    internal static CognitiveServicesBuiltInRole ToBuiltInRole(this FoundryRole role)
    {
        return role switch
        {
            FoundryRole.FoundryUser => (CognitiveServicesBuiltInRole)AzureHostedAgentResource.FoundryUserRoleDefinitionId,
            FoundryRole.CognitiveServicesOpenAIContributor => CognitiveServicesBuiltInRole.CognitiveServicesOpenAIContributor,
            FoundryRole.CognitiveServicesOpenAIUser => CognitiveServicesBuiltInRole.CognitiveServicesOpenAIUser,
            FoundryRole.CognitiveServicesUser => CognitiveServicesBuiltInRole.CognitiveServicesUser,
            _ => throw new ArgumentException($"'{role}' is not a valid {nameof(FoundryRole)} value.", nameof(role))
        };
    }
}
