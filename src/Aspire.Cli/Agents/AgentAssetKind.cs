// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents;

/// <summary>
/// Identifies agent asset kinds.
/// </summary>
[Flags]
internal enum AgentAssetKind
{
    /// <summary>
    /// Agent skills.
    /// </summary>
    Skill = 1,

    /// <summary>
    /// Agent extensions.
    /// </summary>
    Extension = 2,

    /// <summary>
    /// All supported agent asset kinds.
    /// </summary>
    All = Skill | Extension,
}
