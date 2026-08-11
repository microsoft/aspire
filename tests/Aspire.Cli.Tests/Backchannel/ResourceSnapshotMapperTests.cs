// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Commands;
using Aspire.Shared.Model.Serialization;

namespace Aspire.Cli.Tests.Backchannel;

public class ResourceSnapshotMapperTests
{
    [Fact]
    public void ResourceSnapshotDeserialization_WithNumericPropertyValue_PreservesJsonNumber()
    {
        var json = """
            {
                "Name": "service",
                "ResourceType": "Executable",
                "Properties": {
                    "executable.pid": 12345
                }
            }
            """;

        var snapshot = JsonSerializer.Deserialize(json, BackchannelJsonSerializerContext.Default.ResourceSnapshot);

        Assert.NotNull(snapshot);
        var pid = Assert.IsAssignableFrom<JsonValue>(snapshot.Properties["executable.pid"]);
        Assert.Equal(12345, pid.GetValue<int>());
    }

    [Fact]
    public void MapToResourceJson_WithPopulatedProperties_MapsCorrectly()
    {
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
                            MaxLength = 128,
                            DynamicLoading = new ResourceSnapshotCommandArgumentDynamicLoading
                            {
                                AlwaysLoadOnStart = true,
                                DependsOnInputs = ["browser"]
                            }
                        }
                    ]
                },
                new ResourceSnapshotCommand { Name = "start", State = "Disabled", Description = "Start" },
                new ResourceSnapshotCommand { Name = "save", State = "Hidden", Description = "Save parameter" },
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

        var result = ResourceSnapshotMapper.MapToResourceJson(snapshot, allSnapshots, dashboardBaseUrl: "http://localhost:18080");

        Assert.Equal("frontend", result.Name);
        Assert.Single(result.Urls!);
        Assert.Equal("http://localhost:5000", result.Urls![0].Url);

        // Enabled commands with API visibility should be included by default.
        var command = Assert.Single(result.Commands!);
        Assert.Equal("stop", command.Key);

        var stopCommand = command.Value;
        Assert.Equal("Enabled", stopCommand.State);
        Assert.Equal(KnownCommandVisibility.Api, stopCommand.Visibility);
        var argumentInput = Assert.Single(stopCommand.ArgumentInputs!);
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
        Assert.NotNull(argumentInput.DynamicLoading);
        Assert.True(argumentInput.DynamicLoading.AlwaysLoadOnStart);
        Assert.Equal("browser", Assert.Single(argumentInput.DynamicLoading.DependsOnInputs!));

        // Only IsFromSpec environment variables should be included
        Assert.Single(result.Environment!);
        Assert.Equal("Development", result.Environment!["ASPNETCORE_ENVIRONMENT"]);

        // Dashboard URL should be generated
        Assert.NotNull(result.DashboardUrl);
        Assert.Contains("localhost:18080", result.DashboardUrl);
    }

    [Fact]
    public void MapToResourceJson_WithIncludeDisabledCommands_IncludesDisabledAndExcludesHidden()
    {
        var snapshot = new ResourceSnapshot
        {
            Name = "frontend",
            DisplayName = "frontend",
            ResourceType = "Project",
            State = "Running",
            Commands =
            [
                new ResourceSnapshotCommand { Name = "restart", State = KnownCommandState.Enabled, Description = "Restart" },
                new ResourceSnapshotCommand { Name = "start", State = KnownCommandState.Disabled, Description = "Start" },
                new ResourceSnapshotCommand { Name = "save", State = KnownCommandState.Hidden, Description = "Save parameter" }
            ]
        };

        var result = ResourceSnapshotMapper.MapToResourceJson(snapshot, [snapshot], includeDisabledCommands: true);

        Assert.Equal(["restart", "start"], result.Commands!.Keys);
        Assert.Equal(KnownCommandState.Enabled, result.Commands["restart"].State);
        Assert.Equal(KnownCommandState.Disabled, result.Commands["start"].State);
    }

    [Fact]
    public void MapToResourceJson_WithIncludeDisabledCommands_IncludesUiOnlyCommands()
    {
        var snapshot = new ResourceSnapshot
        {
            Name = "frontend",
            DisplayName = "frontend",
            ResourceType = "Project",
            State = "Running",
            Commands =
            [
                new ResourceSnapshotCommand { Name = "api-only", State = KnownCommandState.Enabled, Description = "API only", Visibility = KnownCommandVisibility.Api },
                new ResourceSnapshotCommand { Name = "ui-only", State = KnownCommandState.Enabled, Description = "UI only", Visibility = KnownCommandVisibility.UI },
                new ResourceSnapshotCommand { Name = "ui-disabled", State = KnownCommandState.Disabled, Description = "UI disabled", Visibility = KnownCommandVisibility.UI }
            ]
        };

        var result = ResourceSnapshotMapper.MapToResourceJson(snapshot, [snapshot], includeDisabledCommands: true);

        Assert.Equal(["api-only", "ui-disabled", "ui-only"], result.Commands!.Keys);
        Assert.Equal(KnownCommandVisibility.UI, result.Commands["ui-only"].Visibility);
        Assert.Equal(KnownCommandVisibility.UI, result.Commands["ui-disabled"].Visibility);
    }

    [Fact]
    public void MapToResourceJson_IncludeDisabledStream_StampsSortOrderOnCommands()
    {
        // Commands are provided in non-alphabetical order so SortOrder must reflect
        // registration (set-parameter before delete-parameter), not key order.
        var snapshot = new ResourceSnapshot
        {
            Name = "parameter",
            DisplayName = "parameter",
            ResourceType = "Parameter",
            State = "Running",
            Commands =
            [
                new ResourceSnapshotCommand { Name = "set-parameter", State = KnownCommandState.Enabled, Description = "Set", Visibility = KnownCommandVisibility.Api },
                new ResourceSnapshotCommand { Name = "custom-action", State = KnownCommandState.Enabled, Description = "Custom", Visibility = KnownCommandVisibility.Api },
                new ResourceSnapshotCommand { Name = "delete-parameter", State = KnownCommandState.Enabled, Description = "Delete", Visibility = KnownCommandVisibility.Api }
            ]
        };

        var result = ResourceSnapshotMapper.MapToResourceJson(snapshot, [snapshot], includeDisabledCommands: true);

        // SortOrder reflects the registration order the dashboard uses.
        // Keys are sorted alphabetically for a stable JSON shape.
        Assert.Equal(["custom-action", "delete-parameter", "set-parameter"], result.Commands!.Keys);
        Assert.Equal(0, result.Commands["set-parameter"].SortOrder);
        Assert.Equal(1, result.Commands["custom-action"].SortOrder);
        Assert.Equal(2, result.Commands["delete-parameter"].SortOrder);
    }

    [Fact]
    public void MapToResourceJson_DefaultStream_StampsSortOrder()
    {
        // The default/API stream stamps SortOrder just like the include-disabled stream.
        var snapshot = new ResourceSnapshot
        {
            Name = "parameter",
            DisplayName = "parameter",
            ResourceType = "Parameter",
            State = "Running",
            Commands =
            [
                new ResourceSnapshotCommand { Name = "set-parameter", State = KnownCommandState.Enabled, Description = "Set", Visibility = KnownCommandVisibility.Api },
                new ResourceSnapshotCommand { Name = "custom-action", State = KnownCommandState.Enabled, Description = "Custom", Visibility = KnownCommandVisibility.Api },
                new ResourceSnapshotCommand { Name = "delete-parameter", State = KnownCommandState.Enabled, Description = "Delete", Visibility = KnownCommandVisibility.Api }
            ]
        };

        var result = ResourceSnapshotMapper.MapToResourceJson(snapshot, [snapshot]);

        // SortOrder reflects the registration order even on the default stream.
        // Keys are sorted alphabetically for a stable JSON shape.
        Assert.Equal(["custom-action", "delete-parameter", "set-parameter"], result.Commands!.Keys);
        Assert.Equal(0, result.Commands["set-parameter"].SortOrder);
        Assert.Equal(1, result.Commands["custom-action"].SortOrder);
        Assert.Equal(2, result.Commands["delete-parameter"].SortOrder);
    }

    [Fact]
    public void MapToResourceJson_WithSecretCommandArgument_OmitsValue()
    {
        var snapshot = new ResourceSnapshot
        {
            Name = "frontend",
            DisplayName = "frontend",
            ResourceType = "Project",
            State = "Running",
            Commands =
            [
                new ResourceSnapshotCommand
                {
                    Name = "login",
                    State = KnownCommandState.Enabled,
                    Description = "Log in",
                    Visibility = KnownCommandVisibility.Api,
                    ArgumentInputs =
                    [
                        new ResourceSnapshotCommandArgument
                        {
                            Name = "password",
                            InputType = "SecretText",
                            Value = "super-secret"
                        },
                        new ResourceSnapshotCommandArgument
                        {
                            Name = "environment",
                            InputType = "Text",
                            Value = "Development"
                        }
                    ]
                }
            ]
        };

        var result = ResourceSnapshotMapper.MapToResourceJson(snapshot, [snapshot]);

        var argumentInputs = Assert.Single(result.Commands!).Value.ArgumentInputs!;
        Assert.Null(argumentInputs[0].Value);
        Assert.Equal("Development", argumentInputs[1].Value);
    }

    [Fact]
    public void MapToResourceJson_WithWhitespaceCommandDisplayName_MapsDisplayNameToNull()
    {
        var snapshot = new ResourceSnapshot
        {
            Name = "frontend",
            DisplayName = "frontend",
            ResourceType = "Project",
            State = "Running",
            Commands =
            [
                new ResourceSnapshotCommand
                {
                    Name = "custom-command",
                    State = "Enabled",
                    DisplayName = "   ",
                    Description = "Run custom command",
                    Visibility = KnownCommandVisibility.Api
                }
            ]
        };

        var result = ResourceSnapshotMapper.MapToResourceJson(snapshot, [snapshot]);

        var command = Assert.Single(result.Commands!);
        Assert.Null(command.Value.DisplayName);
        Assert.Equal("Run custom command", command.Value.Description);
    }

    [Fact]
    public void MapToResourceJson_ResolvesWaitingForDependencies()
    {
        var dependency = new ResourceSnapshot
        {
            Name = "messaging-abcxyz",
            DisplayName = "messaging",
            ResourceType = "Container",
            State = "Running"
        };

        var resource = new ResourceSnapshot
        {
            Name = "frontend",
            DisplayName = "frontend",
            ResourceType = "Project",
            State = "Waiting",
            WaitingFor = ["messaging-abcxyz"]
        };

        var result = ResourceSnapshotMapper.MapToResourceJson(resource, [resource, dependency]);

        Assert.NotNull(result.WaitingFor);
        Assert.Equal(["messaging"], result.WaitingFor);
    }

    [Fact]
    public void MapToResourceJson_MapsListPropertiesAsJsonArrays()
    {
        var resource = new ResourceSnapshot
        {
            Name = "frontend",
            DisplayName = "frontend",
            ResourceType = "Project",
            State = "Waiting",
            Properties = new Dictionary<string, JsonNode?>
            {
                ["custom.list"] = new JsonArray((JsonNode?)JsonValue.Create("one"), (JsonNode?)JsonValue.Create("two"))
            }
        };

        var result = ResourceSnapshotMapper.MapToResourceJson(resource, [resource]);

        Assert.NotNull(result.Properties);
        var listProperty = Assert.IsType<JsonArray>(result.Properties["custom.list"]);
        Assert.Collection(
            listProperty,
            value => Assert.Equal("one", value?.GetValue<string>()),
            value => Assert.Equal("two", value?.GetValue<string>()));

        var json = JsonSerializer.Serialize(result, ResourcesCommandJsonContext.RelaxedEscaping.ResourceJson);
        using var document = JsonDocument.Parse(json);
        var serializedProperty = document.RootElement.GetProperty("properties").GetProperty("custom.list");
        Assert.Equal(JsonValueKind.Array, serializedProperty.ValueKind);
        Assert.Equal("one", serializedProperty[0].GetString());
        Assert.Equal("two", serializedProperty[1].GetString());
    }

    [Fact]
    public void MapToResourceJson_UsesUniqueWaitingForNamesForReplicas()
    {
        var firstDependency = new ResourceSnapshot
        {
            Name = "messaging-abcxyz",
            DisplayName = "messaging",
            ResourceType = "Container",
            State = "Running"
        };
        var secondDependency = new ResourceSnapshot
        {
            Name = "messaging-defuvw",
            DisplayName = "messaging",
            ResourceType = "Container",
            State = "Running"
        };

        var resource = new ResourceSnapshot
        {
            Name = "frontend",
            DisplayName = "frontend",
            ResourceType = "Project",
            State = "Waiting",
            WaitingFor = ["messaging-abcxyz"]
        };

        var result = ResourceSnapshotMapper.MapToResourceJson(resource, [resource, firstDependency, secondDependency]);

        Assert.NotNull(result.WaitingFor);
        Assert.Equal(["messaging-abcxyz"], result.WaitingFor);
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
