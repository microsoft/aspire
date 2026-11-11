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
    IEnumerable<IAgentClient> clients,
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
        var local = LocalAspireSkills.Find(workingDirectory, workspaceRoot, clients, cancellationToken);
        var results = new List<EnvironmentCheckResult>();
        if (local.Errors.Count > 0)
        {
            results.Add(new EnvironmentCheckResult
            {
                Category = EnvironmentCheckCategories.AppHost,
                Name = $"{CheckName}-inspection",
                Status = EnvironmentCheckStatus.Warning,
                Message = DoctorCommandStrings.AspireSkillsInspectionFailedMessage,
                Details = string.Join(Environment.NewLine, local.Errors)
            });
        }

        if (local.Files.Count > 0)
        {
            var displayPaths = local.Files
                .Select(path => AgentPath.GetDisplayPath(path, workspaceRoot, executionContext))
                .Order(StringComparer.Ordinal)
                .ToArray();
            results.Add(new EnvironmentCheckResult
            {
                Category = EnvironmentCheckCategories.AppHost,
                Name = CheckName,
                Status = EnvironmentCheckStatus.Warning,
                Message = string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_LocalSkillsConflict, string.Empty).TrimEnd(),
                Fix = AgentCommandStrings.DeprecatedConfigFix,
                Files = displayPaths,
                Metadata = new JsonObject
                {
                    ["workspaceRoot"] = workspaceRoot.FullName
                }
            });
        }

        return results;
    }
}
