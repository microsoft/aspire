// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Backchannel;
using System.Text.Json;

namespace Aspire.Cli.Tests.Backchannel;

public class ResourceSnapshotMapperTests
{
    private static readonly JsonSerializerOptions s_options = BackchannelJsonSerializerContext.CreateJsonSerializerOptions();

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
                new ResourceSnapshotCommand
                {
                    Name = "stop",
                    State = "Enabled",
                    Description = "Stop",
                    Visibility = KnownCommandVisibility.Api,
                    ArgumentInputs =
                    [
                        new ResourceSnapshotCommandArgument
                        {
                            Name = "selector",
                            Label = "Selector",
                            Description = "CSS selector to click.",
                            EnableDescriptionMarkdown = true,
                            InputType = "Text",
                            Required = true,
                            Placeholder = "#submit",
                            Options = new Dictionary<string, string?> { ["primary"] = "Primary" },
                            AllowCustomChoice = true,
                            Disabled = true,
                            MaxLength = 128
                        }
                    ]
                },
                new ResourceSnapshotCommand { Name = "start", State = "Disabled", Description = "Start" },
                new ResourceSnapshotCommand { Name = "dashboard-only", State = "Enabled", Description = "UI only", Visibility = KnownCommandVisibility.UI },
                new ResourceSnapshotCommand { Name = "missing-visibility", State = "Enabled", Description = "Missing visibility", Visibility = null! }
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

        // Null visibility simulates old wire data. It normalizes to Default, which includes API visibility.
        Assert.Equal(["missing-visibility", "stop"], result.Commands!.Keys.Order(StringComparer.Ordinal));
        Assert.Null(result.Commands["missing-visibility"].Visibility);

        var command = result.Commands["stop"];
        Assert.Equal(KnownCommandVisibility.Api, command.Visibility);
        var argumentInput = Assert.Single(command.ArgumentInputs!);
        Assert.Equal("selector", argumentInput.Name);
        Assert.Equal("Selector", argumentInput.Label);
        Assert.Equal("CSS selector to click.", argumentInput.Description);
        Assert.True(argumentInput.EnableDescriptionMarkdown);
        Assert.Equal("Text", argumentInput.InputType);
        Assert.True(argumentInput.Required);
        Assert.Equal("#submit", argumentInput.Placeholder);
        Assert.Equal("Primary", argumentInput.Options!["primary"]);
        Assert.True(argumentInput.AllowCustomChoice);
        Assert.True(argumentInput.Disabled);
        Assert.Equal(128, argumentInput.MaxLength);

        // Only IsFromSpec environment variables should be included
        Assert.Single(result.Environment!);
        Assert.Equal("Development", result.Environment!["ASPNETCORE_ENVIRONMENT"]);

        // Dashboard URL should be generated
        Assert.NotNull(result.DashboardUrl);
        Assert.Contains("localhost:18080", result.DashboardUrl);
    }

    [Fact]
    public void MapToResourceJson_SkipsMalformedWireChildItems()
    {
        var snapshot = JsonSerializer.Deserialize<ResourceSnapshot>(
            """
            {
              "Name": "frontend",
              "DisplayName": "frontend",
              "ResourceType": "Project",
              "Urls": [
                null,
                { "Name": null, "Url": "http://localhost:5001" },
                { "Name": "http", "Url": "http://localhost:5000" }
              ],
              "Volumes": [
                null,
                { "Target": null, "MountType": "volume" },
                { "Target": "/data", "MountType": "volume" }
              ],
              "HealthReports": [
                null,
                { "Status": "Healthy" },
                { "Name": "live", "Status": "Healthy" }
              ],
              "EnvironmentVariables": [
                null,
                { "Value": "ignored", "IsFromSpec": true },
                { "Name": "ASPNETCORE_ENVIRONMENT", "Value": "Development", "IsFromSpec": true }
              ],
              "Relationships": [
                null,
                { "ResourceName": null, "Type": "Reference" },
                { "ResourceName": "database", "Type": "Reference" }
              ],
              "Commands": [
                null,
                { "Name": null, "State": "Enabled" },
                {
                  "Name": "click",
                  "State": "Enabled",
                  "Visibility": "Api",
                  "ArgumentInputs": [
                    null,
                    { "Name": null, "InputType": "Text" },
                    { "Name": "selector", "InputType": "Text" }
                  ]
                },
                { "Name": "bad-state", "State": null },
                { "Name": "stop", "State": "Enabled", "Visibility": "Api" }
              ]
            }
            """,
            s_options);
        Assert.NotNull(snapshot);

        var database = new ResourceSnapshot { Name = "database", DisplayName = "database", ResourceType = "Postgres" };
        var result = ResourceSnapshotMapper.MapToResourceJson(snapshot, [snapshot, database]);

        var url = Assert.Single(result.Urls!);
        Assert.Equal("http", url.Name);

        var volume = Assert.Single(result.Volumes!);
        Assert.Equal("/data", volume.Target);

        var healthReport = Assert.Single(result.HealthReports!);
        Assert.Equal("live", healthReport.Key);

        var environment = Assert.Single(result.Environment!);
        Assert.Equal("ASPNETCORE_ENVIRONMENT", environment.Key);

        var relationship = Assert.Single(result.Relationships!);
        Assert.Equal("database", relationship.ResourceName);

        Assert.Equal(["click", "stop"], result.Commands!.Keys.Order(StringComparer.Ordinal));
        var argumentInput = Assert.Single(result.Commands["click"].ArgumentInputs!);
        Assert.Equal("selector", argumentInput.Name);
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
