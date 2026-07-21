// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents;

/// <summary>
/// Represents a kind of agent asset that can be installed.
/// </summary>
internal enum AgentAssetKind
{
    /// <summary>
    /// A skill asset.
    /// </summary>
    Skill,

    /// <summary>
    /// An extension asset.
    /// </summary>
    Extension
}
