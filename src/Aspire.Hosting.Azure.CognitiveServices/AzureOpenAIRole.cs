// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure.CognitiveServices;

/// <summary>
/// Represents ATS-compatible Azure OpenAI roles.
/// </summary>
internal enum AzureOpenAIRole
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
