// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents;

/// <summary>
/// Represents a text file contained by an <see cref="AgentFileAssetDefinition"/>.
/// </summary>
internal sealed record AgentAssetFile(string RelativePath, string Content);
