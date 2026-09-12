// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents;

/// <summary>
/// Identifies an agent asset kind.
/// </summary>
internal enum AgentAssetKind
{
    /// <summary>
    /// Agent skills.
    /// </summary>
    Skill,

    /// <summary>
    /// Agent extensions.
    /// </summary>
    Extension,
}
