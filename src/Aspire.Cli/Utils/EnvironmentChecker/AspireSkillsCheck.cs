// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json.Nodes;
using Aspire.Cli.Agents;
using Aspire.Cli.Agents.AspireSkills;
using Aspire.Cli.Git;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Utils.EnvironmentChecker;

/// <summary>
/// Warns about static Aspire skills that can shadow configured skill sources.
/// </summary>
internal sealed class AspireSkillsCheck(
    IGitRepository gitRepository,
    CliExecutionContext executionContext) : IEnvironmentCheck
{
    internal const string CheckName = "aspire-skill-sources";

    public int Order => 103;

    public async Task<IReadOnlyList<EnvironmentCheckResult>> CheckAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var workingDirectory = executionContext.WorkingDirectory;
        var workspaceRoot = await gitRepository.GetRootAsync(cancellationToken) ?? workingDirectory;
        var skillCopies = LocalAspireSkills.Find(workingDirectory, workspaceRoot, executionContext.HomeDirectory, cancellationToken);
        var results = new List<EnvironmentCheckResult>();
        if (skillCopies.Count > 0)
        {
            // Doctor reports each location once, even when it is found under both scopes.
            var copies = skillCopies
                .DistinctBy(copy => copy.Directory, StringComparers.FileSystemPath)
                .Select(copy => (DisplayPath: AgentPath.GetDisplayPath(copy.Directory, workspaceRoot, executionContext), copy.Owners))
                .OrderBy(copy => copy.DisplayPath, StringComparer.Ordinal)
                .ToArray();

            var copyLines = copies.Select(copy => string.Format(CultureInfo.CurrentCulture,
                AgentCommandStrings.InitCommand_LocationWithAgents, copy.DisplayPath, string.Join(", ", copy.Owners)));

            results.Add(new EnvironmentCheckResult
            {
                Category = EnvironmentCheckCategories.Environment,
                Name = CheckName,
                Status = EnvironmentCheckStatus.Warning,
                Message = DoctorCommandStrings.AspireSkillsMessage,
                Details = string.Join('\n', copyLines),
                Fix = DoctorCommandStrings.AspireSkillsFix,
                Files = copies.Select(copy => copy.DisplayPath).ToArray(),
                Metadata = new JsonObject
                {
                    ["workspaceRoot"] = workspaceRoot.FullName
                }
            });
        }

        return results;
    }
}
