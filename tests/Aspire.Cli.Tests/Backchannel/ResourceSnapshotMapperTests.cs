// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Backchannel;

namespace Aspire.Cli.Tests.Backchannel;

public class ResourceSnapshotMapperTests
{
    [Fact]
    public void MapToResourceJson_WithPopulatedProperties_MapsCorrectly()
    {
        // Arrange
        var snapshot = new ResourceSnapshot
        {
            Name = "frontend",
            DisplayName = "frontend",
            ResourceType = "Project",
            State = "Running",
            Urls =
            [
                new ResourceSnapshotUrl { Name = "http", Url = "http://localhost:5000" }
            ],
            Commands =
            [
                new ResourceSnapshotCommand { Name = "stop", State = "Enabled", Description = "Stop" },
                new ResourceSnapshotCommand { Name = "start", State = "Disabled", Description = "Start" }
            ],
            EnvironmentVariables =
            [
                new ResourceSnapshotEnvironmentVariable { Name = "ASPNETCORE_ENVIRONMENT", Value = "Development", IsFromSpec = true },
                new ResourceSnapshotEnvironmentVariable { Name = "INTERNAL_VAR", Value = "hidden", IsFromSpec = false }
            ]
        };

        var allSnapshots = new List<ResourceSnapshot> { snapshot };

        // Act
        var result = ResourceSnapshotMapper.MapToResourceJson(snapshot, allSnapshots, dashboardBaseUrl: "http://localhost:18080");

        // Assert
        Assert.Equal("frontend", result.Name);
        Assert.Single(result.Urls!);
        Assert.Equal("http://localhost:5000", result.Urls![0].Url);

        // Only enabled commands should be included
        Assert.Single(result.Commands!);
        Assert.True(result.Commands!.ContainsKey("stop"));

        // Only IsFromSpec environment variables should be included
        Assert.Single(result.Environment!);
        Assert.Equal("Development", result.Environment!["ASPNETCORE_ENVIRONMENT"]);

        // Dashboard URL should be generated
        Assert.NotNull(result.DashboardUrl);
        Assert.Contains("localhost:18080", result.DashboardUrl);
    }

    [Fact]
    public void ResolveResources_ByExactName_ReturnsMatch()
    {
        var snapshots = new List<ResourceSnapshot>
        {
            new() { Name = "cache-zuyppzgw", DisplayName = "cache", ResourceType = "Container", State = "Running" },
            new() { Name = "frontend", DisplayName = "frontend", ResourceType = "Project", State = "Running" }
        };

        var result = ResourceSnapshotMapper.ResolveResources("cache-zuyppzgw", snapshots);

        Assert.Single(result);
        Assert.Equal("cache-zuyppzgw", result[0].Name);
    }

    [Fact]
    public void ResolveResources_ByDisplayName_WhenNoReplicas_ReturnsMatch()
    {
        var snapshots = new List<ResourceSnapshot>
        {
            new() { Name = "cache-zuyppzgw", DisplayName = "cache", ResourceType = "Container", State = "Running" },
            new() { Name = "frontend", DisplayName = "frontend", ResourceType = "Project", State = "Running" }
        };

        var result = ResourceSnapshotMapper.ResolveResources("cache", snapshots);

        Assert.Single(result);
        Assert.Equal("cache-zuyppzgw", result[0].Name);
    }

    [Fact]
    public void ResolveResources_ByDisplayName_WhenReplicas_ReturnsEmpty()
    {
        var snapshots = new List<ResourceSnapshot>
        {
            new() { Name = "cache-abc12345", DisplayName = "cache", ResourceType = "Container", State = "Running" },
            new() { Name = "cache-def67890", DisplayName = "cache", ResourceType = "Container", State = "Running" },
            new() { Name = "frontend", DisplayName = "frontend", ResourceType = "Project", State = "Running" }
        };

        var result = ResourceSnapshotMapper.ResolveResources("cache", snapshots);

        Assert.Empty(result);
    }

    [Fact]
    public void ResolveResources_ByExactName_WhenReplicas_ReturnsMatch()
    {
        var snapshots = new List<ResourceSnapshot>
        {
            new() { Name = "cache-abc12345", DisplayName = "cache", ResourceType = "Container", State = "Running" },
            new() { Name = "cache-def67890", DisplayName = "cache", ResourceType = "Container", State = "Running" },
            new() { Name = "frontend", DisplayName = "frontend", ResourceType = "Project", State = "Running" }
        };

        var result = ResourceSnapshotMapper.ResolveResources("cache-abc12345", snapshots);

        Assert.Single(result);
        Assert.Equal("cache-abc12345", result[0].Name);
    }

    [Fact]
    public void ResolveResources_NoMatch_ReturnsEmpty()
    {
        var snapshots = new List<ResourceSnapshot>
        {
            new() { Name = "cache-zuyppzgw", DisplayName = "cache", ResourceType = "Container", State = "Running" }
        };

        var result = ResourceSnapshotMapper.ResolveResources("nonexistent", snapshots);

        Assert.Empty(result);
    }

    [Fact]
    public void ResolveResources_IsCaseInsensitive()
    {
        var snapshots = new List<ResourceSnapshot>
        {
            new() { Name = "Cache-Zuyppzgw", DisplayName = "Cache", ResourceType = "Container", State = "Running" }
        };

        var resultByName = ResourceSnapshotMapper.ResolveResources("cache-zuyppzgw", snapshots);
        Assert.Single(resultByName);

        var resultByDisplayName = ResourceSnapshotMapper.ResolveResources("CACHE", snapshots);
        Assert.Single(resultByDisplayName);
    }

}
