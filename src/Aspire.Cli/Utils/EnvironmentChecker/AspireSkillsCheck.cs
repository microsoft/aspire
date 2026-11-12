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
            var displayPaths = skillCopies
                .Select(copy => copy.Directory)
                .Distinct(StringComparers.FileSystemPath)
                .Select(directory => AgentPath.GetDisplayPath(directory, workspaceRoot, executionContext))
                .Order(StringComparer.Ordinal)
                .ToArray();

            // Init removes only copies at its scope that a selected agent reads, so each fix line
            // names the agents for one scope.
            var fix = string.Join('\n', skillCopies
                .GroupBy(copy => copy.Scope)
                .OrderBy(group => group.Key)
                .Select(group =>
                {
                    var agents = string.Join(',', group.SelectMany(copy => copy.Owners).Distinct().Order(StringComparer.Ordinal));
                    var command = $"aspire agent init --agents {agents} --scope {group.Key.ToString().ToLowerInvariant()}";
                    return string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.AspireSkillsFixFormat, command);
                }));
            results.Add(new EnvironmentCheckResult
            {
                Category = EnvironmentCheckCategories.Environment,
                Name = CheckName,
                Status = EnvironmentCheckStatus.Warning,
                Message = DoctorCommandStrings.AspireSkillsMessage,
                Fix = fix,
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
