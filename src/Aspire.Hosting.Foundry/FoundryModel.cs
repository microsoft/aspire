// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Foundry;

/// <summary>
/// Describes a model that can be deployed to Microsoft Foundry.
/// </summary>
[AspireDto]
public partial class FoundryModel
{
    /// <summary>
    /// The name of the model.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The version of the model.
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    /// The format or provider of the model (e.g., OpenAI, Microsoft, xAi, Deepseek).
    /// </summary>
    public required string Format { get; init; }
}
