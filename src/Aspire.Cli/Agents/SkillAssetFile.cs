// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents;

/// <summary>
/// Represents a text file that belongs to an installable skill.
/// </summary>
internal sealed record SkillAssetFile(string RelativePath, string Content);
