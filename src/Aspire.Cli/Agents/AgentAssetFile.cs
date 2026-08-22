// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents;

/// <summary>
/// Represents a validated file contained in an agent asset.
/// </summary>
internal sealed record AgentAssetFile(string RelativePath, string Content);
