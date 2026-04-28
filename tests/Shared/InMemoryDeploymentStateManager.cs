// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES002

using System.Text.Json.Nodes;
using Aspire.Hosting.Pipelines;

namespace Aspire.Hosting.Tests;

/// <summary>
/// In-memory deployment state manager for testing destroy scenarios.
/// </summary>
internal sealed class InMemoryDeploymentStateManager : IDeploymentStateManager
{
    private readonly Dictionary<string, JsonObject> _sections = new();
    private readonly Dictionary<string, long> _versions = new();

    public string? StateFilePath => null;

    public void SetSection(string name, JsonObject data)
    {
        _sections[name] = data;
        _versions[name] = 1;
    }

    public Task<DeploymentStateSection> AcquireSectionAsync(string sectionName, CancellationToken cancellationToken = default)
    {
        if (_sections.TryGetValue(sectionName, out var data))
        {
            var version = _versions.GetValueOrDefault(sectionName, 0);
            return Task.FromResult(new DeploymentStateSection(sectionName, data, version));
        }
        return Task.FromResult(new DeploymentStateSection(sectionName, [], 0));
    }

    public Task SaveSectionAsync(DeploymentStateSection section, CancellationToken cancellationToken = default)
    {
        _sections[section.SectionName] = section.Data;
        _versions[section.SectionName] = section.Version + 1;
        return Task.CompletedTask;
    }

    public Task DeleteSectionAsync(DeploymentStateSection section, CancellationToken cancellationToken = default)
    {
        _sections.Remove(section.SectionName);
        _versions.Remove(section.SectionName);
        return Task.CompletedTask;
    }

    public Task ClearAllStateAsync(CancellationToken cancellationToken = default)
    {
        _sections.Clear();
        _versions.Clear();
        return Task.CompletedTask;
    }
}
