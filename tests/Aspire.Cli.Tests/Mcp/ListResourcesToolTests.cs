// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Mcp.Tools;
using Aspire.Cli.Tests.TestServices;
using Aspire.Dashboard.Model;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Aspire.Cli.Tests.Mcp;

public class ListResourcesToolTests(ITestOutputHelper outputHelper)
{
    private const string AppHostPath = "/repo/TestAppHost/TestAppHost.csproj";

    [Fact]
    public async Task ListResourcesTool_ThrowsException_WhenNoAppHostRunning()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var tool = new ListResourcesTool(monitor, NullLogger<ListResourcesTool>.Instance);

        var exception = await Assert.ThrowsAsync<McpProtocolException>(
            () => tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).AsTask()).DefaultTimeout();

        Assert.Contains("No Aspire AppHost", exception.Message);
        Assert.Contains("aspire start", exception.Message);
    }

    [Fact]
    public async Task ListResourcesTool_ReportsOutOfScopeAppHostsWithoutExposingTheirIdentity()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = CreateConnection("/other/Private/Private.AppHost.csproj");
        connection.IsInScope = false;
        monitor.AddConnection("hash1", "socket.hash1", connection);
        var tool = new ListResourcesTool(monitor, NullLogger<ListResourcesTool>.Instance);

        var exception = await Assert.ThrowsAsync<McpProtocolException>(
            () => tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).AsTask()).DefaultTimeout();

        Assert.Equal(
            "Running Aspire AppHosts were found outside the MCP server's working directory scope. " +
            "Use 'list_apphosts' to discover available AppHosts, then 'select_apphost' to choose one.",
            exception.Message);
    }

    [Fact]
    public async Task ListResourcesTool_ReturnsExplicitEmptyResult_WhenSnapshotsAreEmpty()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("hash1", "socket.hash1", CreateConnection());

        var tool = new ListResourcesTool(monitor, NullLogger<ListResourcesTool>.Instance);
        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).DefaultTimeout();

        Assert.True(result.IsError is null or false);
        using var json = GetResourceData(result);

        Assert.Equal(JsonValueKind.Array, json.RootElement.ValueKind);
        Assert.Empty(json.RootElement.EnumerateArray());
    }

    [Fact]
    public async Task ListResourcesTool_SerializesEmptyCollectionsAsArrays()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection(
            "hash1",
            "socket.hash1",
            CreateConnection(new ResourceSnapshot
            {
                Name = "api-service",
                ResourceType = "Project",
                State = "Running"
            }));
        var tool = new ListResourcesTool(monitor, NullLogger<ListResourcesTool>.Instance);

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).DefaultTimeout();

        using var json = GetResourceData(result);
        var resource = json.RootElement[0];
        Assert.Equal("[]", resource.GetProperty("waiting_for").GetRawText());
        Assert.Equal("[]", resource.GetProperty("urls").GetRawText());
        Assert.Equal("[]", resource.GetProperty("relationships").GetRawText());
        Assert.Equal("{}", resource.GetProperty("commands").GetRawText());
    }

    [Fact]
    public async Task ListResourcesTool_FansOutAndDeduplicatesRelationshipsToReplicaRuntimeNames()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection(
            "hash1",
            "socket.hash1",
            CreateConnection(
                new ResourceSnapshot
                {
                    Name = "api",
                    DisplayName = "API",
                    ResourceType = "Project",
                    State = "Running",
                    Relationships =
                    [
                        new ResourceSnapshotRelationship
                        {
                            ResourceName = "Redis",
                            Type = "Reference"
                        },
                        new ResourceSnapshotRelationship
                        {
                            ResourceName = "Redis",
                            Type = "Reference"
                        }
                    ]
                },
                new ResourceSnapshot
                {
                    Name = "redis-instance-1",
                    DisplayName = "Redis",
                    ResourceType = "Container",
                    State = "Running"
                },
                new ResourceSnapshot
                {
                    Name = "redis-instance-2",
                    DisplayName = "Redis",
                    ResourceType = "Container",
                    State = "Running"
                },
                new ResourceSnapshot
                {
                    Name = "redis-instance-3",
                    DisplayName = "Redis",
                    ResourceType = "Container",
                    State = "Running"
                }));
        var tool = new ListResourcesTool(monitor, NullLogger<ListResourcesTool>.Instance);

        var result = await tool.CallToolAsync(
            CallToolContextTestHelper.Create(),
            CancellationToken.None).DefaultTimeout();

        using var json = GetResourceData(result);
        var relationships = json.RootElement
            [0]
            .GetProperty("relationships")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(3, relationships.Length);
        Assert.All(
            relationships,
            relationship => Assert.Equal("Reference", relationship.GetProperty("type").GetString()));
        Assert.Equal(
            ["redis-instance-1", "redis-instance-2", "redis-instance-3"],
            relationships.Select(relationship => relationship.GetProperty("resource_name").GetString()));
    }

    [Fact]
    public async Task ListResourcesTool_RelationshipFanOutExcludesHiddenDuplicateRuntimeName()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection(
            "hash1",
            "socket.hash1",
            CreateConnection(
                new ResourceSnapshot
                {
                    Name = "api",
                    DisplayName = "API",
                    ResourceType = "Project",
                    State = "Running",
                    Relationships =
                    [
                        new ResourceSnapshotRelationship
                        {
                            ResourceName = "Redis",
                            Type = "Reference"
                        }
                    ]
                },
                new ResourceSnapshot
                {
                    Name = "redis-visible",
                    DisplayName = "Redis",
                    ResourceType = "Container",
                    State = "Running"
                },
                new ResourceSnapshot
                {
                    Name = "redis-hidden",
                    DisplayName = "Redis",
                    ResourceType = "Container",
                    State = "Hidden"
                }));
        var tool = new ListResourcesTool(monitor, NullLogger<ListResourcesTool>.Instance);

        var result = await tool.CallToolAsync(
            CallToolContextTestHelper.Create(),
            CancellationToken.None).DefaultTimeout();

        using var json = GetResourceData(result);
        Assert.Equal(
            ["redis-visible"],
            json.RootElement[0]
                .GetProperty("relationships")
                .EnumerateArray()
                .Select(relationship => relationship.GetProperty("resource_name").GetString()));
        Assert.Equal(
            ["api", "redis-visible"],
            json.RootElement
                .EnumerateArray()
                .Select(resource => resource.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task ListResourcesTool_WaitingForPreservesHiddenDuplicateIdentity()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection(
            "hash1",
            "socket.hash1",
            CreateConnection(
                new ResourceSnapshot
                {
                    Name = "api",
                    DisplayName = "API",
                    ResourceType = "Project",
                    State = "Running",
                    WaitingFor = ["redis-visible", "redis-hidden"]
                },
                new ResourceSnapshot
                {
                    Name = "redis-visible",
                    DisplayName = "Redis",
                    ResourceType = "Container",
                    State = "Running"
                },
                new ResourceSnapshot
                {
                    Name = "redis-hidden",
                    DisplayName = "Redis",
                    ResourceType = "Container",
                    State = "Hidden"
                }));
        var tool = new ListResourcesTool(monitor, NullLogger<ListResourcesTool>.Instance);

        var result = await tool.CallToolAsync(
            CallToolContextTestHelper.Create(),
            CancellationToken.None).DefaultTimeout();

        using var json = GetResourceData(result);
        Assert.Equal(
            ["redis-visible", "redis-hidden"],
            json.RootElement[0]
                .GetProperty("waiting_for")
                .EnumerateArray()
                .Select(value => value.GetString()));
        Assert.Equal(
            ["api", "redis-visible"],
            json.RootElement
                .EnumerateArray()
                .Select(resource => resource.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task ListResourcesTool_DoesNotExposeMatchingExplicitSymlinkedAppHostPath()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var realDirectory = workspace.WorkspaceRoot.CreateSubdirectory("real");
        var realAppHostPath = Path.Combine(realDirectory.FullName, "Symlinked.AppHost.csproj");
        File.WriteAllText(realAppHostPath, "<Project />");
        var symlinkDirectory = Path.Combine(workspace.WorkspaceRoot.FullName, "link");

        try
        {
            Directory.CreateSymbolicLink(symlinkDirectory, realDirectory.FullName);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Skip($"Cannot create a directory symlink in this environment: {ex.Message}");
        }

        var symlinkedAppHostPath = Path.Combine(symlinkDirectory, "Symlinked.AppHost.csproj");
        var monitor = new TestAuxiliaryBackchannelMonitor
        {
            SelectedAppHostPath = symlinkedAppHostPath
        };
        var connection = CreateConnection(realAppHostPath);
        monitor.AddConnection("hash1", "socket.hash1", connection);
        var tool = new ListResourcesTool(monitor, NullLogger<ListResourcesTool>.Instance);

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).DefaultTimeout();

        using var json = GetResourceData(result);
        Assert.Equal(JsonValueKind.Array, json.RootElement.ValueKind);
    }

    [Fact]
    public async Task ListResourcesTool_ReturnsMultipleResources()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = CreateConnection(
            new ResourceSnapshot
            {
                Name = "api-service",
                DisplayName = "API Service",
                ResourceType = "Project",
                State = "Running"
            },
            new ResourceSnapshot
            {
                Name = "redis",
                DisplayName = "Redis",
                ResourceType = "Container",
                State = "Running"
            },
            new ResourceSnapshot
            {
                Name = "postgres",
                DisplayName = "PostgreSQL",
                ResourceType = "Container",
                State = "Starting"
            });
        monitor.AddConnection("hash1", "socket.hash1", connection);

        var tool = new ListResourcesTool(monitor, NullLogger<ListResourcesTool>.Instance);
        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).DefaultTimeout();

        using var json = GetResourceData(result);
        var resources = json.RootElement;

        Assert.Equal(["api-service", "postgres", "redis"], resources.EnumerateArray().Select(r => r.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task ListResourcesTool_DescribesPaginationInputs()
    {
        var tool = new ListResourcesTool(new TestAuxiliaryBackchannelMonitor(), NullLogger<ListResourcesTool>.Instance);

        await Verify(tool.GetInputSchema().GetRawText(), "json");
    }

    [Theory]
    [InlineData("""{"offset":-1}""", "offset")]
    [InlineData("""{"offset":2147483648}""", "offset")]
    [InlineData("""{"offset":1.5}""", "offset")]
    [InlineData("""{"offset":"64"}""", "offset")]
    [InlineData("""{"offset":null}""", "offset")]
    [InlineData("""{"offset":true}""", "offset")]
    [InlineData("""{"limit":0}""", "limit")]
    [InlineData("""{"limit":65}""", "limit")]
    [InlineData("""{"limit":2147483648}""", "limit")]
    [InlineData("""{"limit":1.5}""", "limit")]
    [InlineData("""{"limit":"1"}""", "limit")]
    [InlineData("""{"limit":null}""", "limit")]
    [InlineData("""{"limit":[]}""", "limit")]
    [InlineData("""{"unexpected":"value"}""", "unexpected")]
    public async Task ListResourcesTool_RejectsInvalidPaginationBeforeReadingResources(string json, string invalidArgument)
    {
        using var arguments = JsonDocument.Parse(json);
        var tool = new ListResourcesTool(new TestAuxiliaryBackchannelMonitor(), NullLogger<ListResourcesTool>.Instance);
        var context = CallToolContextTestHelper.Create(
            arguments.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => property.Value));

        var exception = await Assert.ThrowsAsync<McpProtocolException>(
            () => tool.CallToolAsync(context, CancellationToken.None).AsTask()).DefaultTimeout();

        Assert.Equal(McpErrorCode.InvalidParams, exception.ErrorCode);
        Assert.Equal(invalidArgument switch
        {
            "offset" => "Argument 'offset' must be an integer from 0 through 2147483647.",
            "limit" => "Argument 'limit' must be an integer from 1 through 64.",
            _ => "Arguments may contain only 'offset' and 'limit'."
        }, exception.Message);
    }

    [Fact]
    public async Task ListResourcesTool_PagesThroughEveryVisibleResourceInStableOrder()
    {
        var names = Enumerable.Range(0, 140).Select(index => $"resource-{index:D3}").ToArray();
        var connection = CreateConnection(names.Reverse().Select(name => new ResourceSnapshot
        {
            Name = name,
            State = "Running"
        }).ToArray());
        connection.ResourceSnapshots.Insert(0, new ResourceSnapshot { Name = "hidden", IsHidden = true });
        connection.ResourceSnapshots.Insert(1, new ResourceSnapshot { Name = "hidden-state", State = "Hidden" });
        connection.ResourceSnapshots.Insert(2, new ResourceSnapshot
        {
            Name = "excluded",
            Properties = new Dictionary<string, JsonNode?>
            {
                [KnownProperties.Resource.ExcludeFromMcp] = JsonValue.Create(true)
            }
        });
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("hash1", "socket.hash1", connection);
        var tool = new ListResourcesTool(monitor, NullLogger<ListResourcesTool>.Instance);
        var returnedNames = new List<string?>();

        foreach (var offset in new[] { 0, 64, 128 })
        {
            connection.ResourceSnapshots.Reverse();
            var context = CallToolContextTestHelper.Create(new Dictionary<string, JsonElement>
            {
                ["offset"] = JsonSerializer.SerializeToElement(offset)
            });
            var result = await tool.CallToolAsync(context, CancellationToken.None).DefaultTimeout();

            using var resources = GetResourceData(result);
            using var pagination = GetPagination(result);
            var pageNames = resources.RootElement.EnumerateArray().Select(resource => resource.GetProperty("name").GetString()).ToArray();
            Assert.Equal(names.Skip(offset).Take(64), pageNames);
            returnedNames.AddRange(pageNames);
            Assert.Equal(offset, pagination.RootElement.GetProperty("offset").GetInt32());
            Assert.Equal(64, pagination.RootElement.GetProperty("limit").GetInt32());
            Assert.Equal(names.Length, pagination.RootElement.GetProperty("total").GetInt32());
            Assert.Equal(offset < 128, pagination.RootElement.TryGetProperty("next_offset", out var nextOffset));
            if (offset < 128)
            {
                Assert.Equal(offset + 64, nextOffset.GetInt32());
            }
        }

        Assert.Equal(names, returnedNames);
    }

    [Theory]
    [InlineData(0, 1, 1, 1)]
    [InlineData(1, 1, 1, 2)]
    [InlineData(1, 64, 2, null)]
    [InlineData(3, 2, 0, null)]
    [InlineData(int.MaxValue, 64, 0, null)]
    public async Task ListResourcesTool_HonorsPageSizeAndHandlesOffsetsPastTheEnd(
        int offset, int limit, int expectedCount, int? expectedNextOffset)
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("hash1", "socket.hash1", CreateConnection(
            new ResourceSnapshot { Name = "first" },
            new ResourceSnapshot { Name = "second" },
            new ResourceSnapshot { Name = "third" }));
        var tool = new ListResourcesTool(monitor, NullLogger<ListResourcesTool>.Instance);
        var context = CallToolContextTestHelper.Create(new Dictionary<string, JsonElement>
        {
            ["offset"] = JsonSerializer.SerializeToElement(offset),
            ["limit"] = JsonSerializer.SerializeToElement(limit)
        });

        var result = await tool.CallToolAsync(context, CancellationToken.None).DefaultTimeout();

        using var resources = GetResourceData(result);
        using var pagination = GetPagination(result);
        Assert.Equal(expectedCount, resources.RootElement.GetArrayLength());
        Assert.Equal(
            new[] { "first", "second", "third" }.Skip(offset).Take(limit),
            resources.RootElement.EnumerateArray().Select(resource => resource.GetProperty("name").GetString()));
        Assert.Equal(3, pagination.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(expectedNextOffset is not null, pagination.RootElement.TryGetProperty("next_offset", out var nextOffset));
        if (expectedNextOffset is not null)
        {
            Assert.Equal(expectedNextOffset.Value, nextOffset.GetInt32());
        }
    }

    [Fact]
    public async Task ListResourcesTool_PreservesCrossPageRelationshipsAndHiddenReplicaIdentity()
    {
        var snapshots = Enumerable.Range(0, 300).Select(index => new ResourceSnapshot
        {
            Name = $"resource-{index:D3}"
        }).ToList();
        snapshots.Insert(0, new ResourceSnapshot
        {
            Name = "api",
            WaitingFor = ["redis-visible", "redis-hidden"],
            Relationships =
            [
                new ResourceSnapshotRelationship { ResourceName = "Redis", Type = "Reference" }
            ]
        });
        snapshots.Add(new ResourceSnapshot { Name = "redis-visible", DisplayName = "Redis" });
        snapshots.Add(new ResourceSnapshot { Name = "redis-hidden", DisplayName = "Redis", IsHidden = true });
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("hash1", "socket.hash1", CreateConnection([.. snapshots]));
        var tool = new ListResourcesTool(monitor, NullLogger<ListResourcesTool>.Instance);
        var context = CallToolContextTestHelper.Create(new Dictionary<string, JsonElement>
        {
            ["limit"] = JsonSerializer.SerializeToElement(1)
        });

        var result = await tool.CallToolAsync(context, CancellationToken.None).DefaultTimeout();

        await Verify(GetResultText(result), "txt");
    }

    [Fact]
    public async Task ListResourcesTool_ListsCommandMetadataWithoutCurrentOrDefaultInputValues()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("hash1", "socket.hash1", CreateConnection(new ResourceSnapshot
        {
            Name = "api",
            Commands =
            [
                new ResourceSnapshotCommand
                {
                    Name = "reload",
                    State = "Enabled",
                    Description = "Reload configuration.",
                    ArgumentInputs =
                    [
                        new ResourceSnapshotCommandArgument
                        {
                            Name = "configuration",
                            InputType = "Text",
                            Description = "Configuration name.",
                            Required = true,
                            MaxLength = 100,
                            Value = "private-default-configuration",
                            Placeholder = "private-placeholder"
                        },
                        new ResourceSnapshotCommandArgument
                        {
                            Name = "password",
                            InputType = "SecretText",
                            Required = true,
                            Value = "secret-password",
                            Options = new Dictionary<string, string?> { ["secret-option"] = "secret-label" }
                        },
                        new ResourceSnapshotCommandArgument
                        {
                            Name = "environment",
                            InputType = "Choice",
                            Required = true,
                            AllowCustomChoice = true,
                            Options = new Dictionary<string, string?> { ["staging"] = "Staging", ["production"] = "Production" },
                            Value = "private-current-environment",
                            DynamicLoading = new ResourceSnapshotCommandArgumentDynamicLoading
                            {
                                AlwaysLoadOnStart = true,
                                DependsOnInputs = ["configuration"]
                            }
                        },
                        new ResourceSnapshotCommandArgument { Name = "force", InputType = "Boolean", Value = "true" },
                        new ResourceSnapshotCommandArgument { Name = "attempts", InputType = "Number", Value = "5", Disabled = true }
                    ]
                },
                new ResourceSnapshotCommand { Name = "disabled", State = "disabled", Visibility = "api" },
                new ResourceSnapshotCommand { Name = "hidden", State = "Hidden" },
                new ResourceSnapshotCommand { Name = "ui-only", State = "Enabled", Visibility = "UI" },
                new ResourceSnapshotCommand { Name = "none", State = "Enabled", Visibility = "None" }
            ]
        }));
        var tool = new ListResourcesTool(monitor, NullLogger<ListResourcesTool>.Instance);

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).DefaultTimeout();

        using var json = GetResourceData(result);
        await Verify(json.RootElement[0].GetProperty("commands").GetRawText(), "json");
    }

    [Fact]
    public async Task ListResourcesTool_BoundsCommandDescriptionsWithoutTruncatingInvocationMetadata()
    {
        var description = new string('x', 255) + "\U0001F680\ntrailing text";
        var commands = Enumerable.Range(0, 40).Select(index => new ResourceSnapshotCommand
        {
            Name = $"command-{index:D3}",
            State = "Enabled",
            Description = description,
            ArgumentInputs = index == 39
                ? Enumerable.Range(0, 40).Select(argumentIndex => new ResourceSnapshotCommandArgument
                {
                    Name = $"argument-{argumentIndex:D3}",
                    InputType = "Choice",
                    Description = description,
                    Options = argumentIndex == 39
                        ? Enumerable.Range(0, 40).ToDictionary(optionIndex => $"option-{optionIndex:D3}", _ => (string?)description)
                        : null
                }).ToArray()
                : []
        }).ToArray();
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("hash1", "socket.hash1", CreateConnection(new ResourceSnapshot { Name = "api", Commands = commands }));
        var tool = new ListResourcesTool(monitor, NullLogger<ListResourcesTool>.Instance);
        var context = CallToolContextTestHelper.Create(new Dictionary<string, JsonElement>
        {
            ["limit"] = JsonSerializer.SerializeToElement(1)
        });

        var result = await tool.CallToolAsync(context, CancellationToken.None).DefaultTimeout();

        using var json = GetResourceData(result);
        var metadata = json.RootElement[0].GetProperty("commands");
        Assert.Equal(commands.Select(command => command.Name), metadata.EnumerateObject().Select(command => command.Name));
        var command = metadata.GetProperty("command-039");
        Assert.Equal(new string('x', 255) + "\U0001F680", command.GetProperty("description").GetString());
        var arguments = command.GetProperty("argument_inputs");
        Assert.Equal(commands[39].ArgumentInputs.Select(input => input.Name),
            arguments.EnumerateArray().Select(input => input.GetProperty("name").GetString()));
        var argument = arguments[39];
        Assert.Equal(new string('x', 255) + "\U0001F680", argument.GetProperty("description").GetString());
        var options = argument.GetProperty("options");
        Assert.Equal(commands[39].ArgumentInputs[39].Options!.Keys, options.EnumerateObject().Select(option => option.Name));
        Assert.All(options.EnumerateObject(), option => Assert.Equal(new string('x', 255) + "\U0001F680", option.Value.GetString()));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("Active", "Active")]
    [InlineData("Building", "Building")]
    [InlineData("Exited", "Exited")]
    [InlineData("FailedToStart", "FailedToStart")]
    [InlineData("Finished", "Finished")]
    [InlineData("NotStarted", "NotStarted")]
    [InlineData("Running", "Running")]
    [InlineData("RuntimeUnhealthy", "RuntimeUnhealthy")]
    [InlineData("Starting", "Starting")]
    [InlineData("Stopping", "Stopping")]
    [InlineData("ValueMissing", "ValueMissing")]
    [InlineData("Waiting", "Waiting")]
    [InlineData("Unknown", "unknown")]
    [InlineData("Downloading model ghcr.io/private/repository:latest", "unknown")]
    [InlineData("running", "unknown")]
    public async Task ListResourcesTool_ProjectsOnlySupportedResourceStates(string? state, string? expectedState)
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection(
            "hash1",
            "socket.hash1",
            CreateConnection(new ResourceSnapshot
            {
                Name = "model",
                ResourceType = "Custom",
                State = state
            }));
        var tool = new ListResourcesTool(monitor, NullLogger<ListResourcesTool>.Instance);

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).DefaultTimeout();

        using var json = GetResourceData(result);
        var resource = json.RootElement[0];
        if (expectedState is null)
        {
            Assert.False(resource.TryGetProperty("state", out _));
        }
        else
        {
            Assert.Equal(expectedState, resource.GetProperty("state").GetString());
        }
    }

    [Fact]
    public async Task ListResourcesTool_BoundsModelFacingCollectionsAndText()
    {
        var resourceNames = Enumerable.Range(0, 70).Select(index => $"resource-{index}").ToArray();
        var longText = new string('x', 300) + "\ncontrol";
        var snapshots = resourceNames.Select((name, index) => new ResourceSnapshot
        {
            Name = name,
            DisplayName = index == 0 ? longText : name,
            ResourceType = index == 0 ? longText : "Custom",
            State = index == 0 ? longText : "Running",
            StateStyle = index == 0 ? longText : null,
            HealthStatus = index == 0 ? longText : null,
            WaitingFor = index == 0 ? resourceNames[1..41] : [],
            Urls = index == 0
                ? Enumerable.Range(0, 20)
                    .Select(urlIndex => new ResourceSnapshotUrl
                    {
                        Name = longText,
                        Url = $"https://example.com/{urlIndex}/{longText}"
                    })
                    .ToArray()
                : [],
            Relationships = index == 0
                ? resourceNames[1..41]
                    .Select(target => new ResourceSnapshotRelationship
                    {
                        Type = longText,
                        ResourceName = target
                    })
                    .ToArray()
                : []
        }).ToArray();
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("hash1", "socket.hash1", CreateConnection(snapshots));
        var tool = new ListResourcesTool(monitor, NullLogger<ListResourcesTool>.Instance);

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).DefaultTimeout();

        using var json = GetResourceData(result);
        Assert.Equal(64, json.RootElement.GetArrayLength());
        using var pagination = GetPagination(result);
        Assert.Equal(70, pagination.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(64, pagination.RootElement.GetProperty("next_offset").GetInt32());
        var firstResource = json.RootElement[0];
        foreach (var propertyName in new[] { "display_name", "resource_type", "state_style", "health_status" })
        {
            var value = Assert.IsType<string>(firstResource.GetProperty(propertyName).GetString());
            Assert.Equal(256, value.Length);
            Assert.All(value, character => Assert.False(char.IsControl(character)));
        }
        Assert.Equal("unknown", firstResource.GetProperty("state").GetString());
        Assert.Equal(32, firstResource.GetProperty("waiting_for").GetArrayLength());
        Assert.Equal(16, firstResource.GetProperty("urls").GetArrayLength());
        Assert.Equal(32, firstResource.GetProperty("relationships").GetArrayLength());
    }

    [Fact]
    public async Task ListResourcesTool_UsesCrossPlatformBasenamesForSources()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection(
            "hash1",
            "socket.hash1",
            CreateConnection(
                new ResourceSnapshot
                {
                    Name = "api-service",
                    ResourceType = "Project",
                    State = "Running",
                    Properties = new Dictionary<string, JsonNode?>
                    {
                        [KnownProperties.Project.Path] = JsonValue.Create(@"C:\repo\Api\Api.csproj")
                    }
                },
                new ResourceSnapshot
                {
                    Name = "worker",
                    ResourceType = "Executable",
                    State = "Running",
                    Properties = new Dictionary<string, JsonNode?>
                    {
                        [KnownProperties.Executable.Path] = JsonValue.Create(@"C:\repo\bin\worker.exe")
                    }
                }));
        var tool = new ListResourcesTool(monitor, NullLogger<ListResourcesTool>.Instance);

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).DefaultTimeout();

        using var json = GetResourceData(result);
        var resources = json.RootElement;
        Assert.Equal("Api.csproj", resources[0].GetProperty("source").GetString());
        Assert.Equal("worker.exe", resources[1].GetProperty("source").GetString());
    }

    [Fact]
    public async Task ListResourcesTool_BoundsSourceByUnicodeScalarsAndSanitizesControls()
    {
        const string AstralRune = "\U0001F680";
        var sourceValue = new string('x', 254) + "\n" + AstralRune + "-truncated";
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection(
            "hash1",
            "socket.hash1",
            CreateConnection(new ResourceSnapshot
            {
                Name = "unicode-source",
                ResourceType = "Container",
                State = "Running",
                Properties = new Dictionary<string, JsonNode?>
                {
                    [KnownProperties.Container.Image] = JsonValue.Create(sourceValue)
                }
            }));
        var tool = new ListResourcesTool(monitor, NullLogger<ListResourcesTool>.Instance);

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).DefaultTimeout();

        using var json = GetResourceData(result);
        var source = Assert.IsType<string>(json.RootElement[0].GetProperty("source").GetString());
        Assert.Equal(new string('x', 254) + " " + AstralRune, source);
        Assert.Equal(256, source.EnumerateRunes().Count());
    }

    [Fact]
    public async Task ListResourcesTool_AppliesResourceEndpointUrlPolicy()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection(
            "hash1",
            "socket.hash1",
            CreateConnection(new ResourceSnapshot
            {
                Name = "endpoints",
                ResourceType = "Custom",
                State = "Running",
                Urls =
                [
                    new ResourceSnapshotUrl { Name = "tcp", Url = "tcp://cache.example.com:6379" },
                    new ResourceSnapshotUrl { Name = "udp", Url = "udp://dns.example.com:53" },
                    new ResourceSnapshotUrl { Name = "ws", Url = "ws://events.example.com/socket" },
                    new ResourceSnapshotUrl { Name = "wss", Url = "wss://events.example.com/socket" },
                    new ResourceSnapshotUrl { Name = "postgres", Url = "postgresql://db.example.com:5432/catalog" },
                    new ResourceSnapshotUrl { Name = "file", Url = "file:///repo/private.txt" },
                    new ResourceSnapshotUrl { Name = "windows", Url = @"C:\repo\private.txt" }
                ]
            }));
        var tool = new ListResourcesTool(monitor, NullLogger<ListResourcesTool>.Instance);

        var result = await tool.CallToolAsync(
            CallToolContextTestHelper.Create(),
            CancellationToken.None).DefaultTimeout();

        using var json = GetResourceData(result);
        var urls = json.RootElement[0].GetProperty("urls");
        Assert.Equal(
            [
                "tcp://cache.example.com:6379",
                "udp://dns.example.com:53",
                "ws://events.example.com/socket",
                "wss://events.example.com/socket",
                "postgresql://db.example.com:5432/catalog"
            ],
            urls.EnumerateArray()
                .Where(url => url.TryGetProperty("url", out _))
                .Select(url => url.GetProperty("url").GetString()));
        Assert.False(urls[5].TryGetProperty("url", out _));
        Assert.False(urls[6].TryGetProperty("url", out _));
    }

    [Fact]
    public async Task ListResourcesTool_ReturnsOnlyBoundedResourceDataAndSanitizedUrls()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = CreateConnection(
            new ResourceSnapshot
            {
                Name = "api-service",
                DisplayName = "API Service",
                ResourceType = "Project",
                State = "Running",
                WaitingFor = ["Redis"],
                Properties = new Dictionary<string, JsonNode?>
                {
                    [KnownProperties.Project.Path] = JsonValue.Create("/repo/Api/Api.csproj"),
                    ["secret.property"] = JsonValue.Create("property-secret")
                },
                EnvironmentVariables =
                [
                    new ResourceSnapshotEnvironmentVariable
                    {
                        Name = "API_PASSWORD",
                        Value = "environment-secret",
                        IsFromSpec = true
                    }
                ],
                Urls =
                [
                    new ResourceSnapshotUrl
                    {
                        Name = "https",
                        Url = "https://endpoint-user:endpoint-password@localhost:5001/api?view=summary&TOKEN=endpoint-secret#access_token=fragment-secret",
                        IsInternal = true,
                        DisplayProperties = new ResourceSnapshotUrlDisplayProperties
                        {
                            DisplayName = "HTTPS"
                        }
                    }
                ],
                Relationships =
                [
                    new ResourceSnapshotRelationship
                    {
                        ResourceName = "Redis",
                        Type = "Reference"
                    }
                ],
                Volumes =
                [
                    new ResourceSnapshotVolume
                    {
                        Source = "/repo/private",
                        Target = "/app/private",
                        MountType = "bind"
                    }
                ],
                HealthReports =
                [
                    new ResourceSnapshotHealthReport
                    {
                        Name = "ready",
                        Status = "Healthy",
                        Description = "health-secret",
                        ExceptionText = "exception-secret"
                    }
                ],
                Commands =
                [
                    new ResourceSnapshotCommand
                    {
                        Name = "connect",
                        State = "Enabled",
                        ArgumentInputs =
                        [
                            new ResourceSnapshotCommandArgument
                            {
                                Name = "password",
                                InputType = "SecretText",
                                Value = "command-secret"
                            }
                        ]
                    }
                ]
            },
            new ResourceSnapshot
            {
                Name = "redis-instance",
                DisplayName = "Redis",
                ResourceType = "Container",
                State = "Running",
                Properties = new Dictionary<string, JsonNode?>
                {
                    [KnownProperties.Container.Image] = JsonValue.Create("redis:8"),
                    [KnownProperties.Executable.Path] = JsonValue.Create("/repo/bin/container-secret")
                }
            },
            new ResourceSnapshot
            {
                Name = "worker",
                ResourceType = "Executable",
                State = "Running",
                Properties = new Dictionary<string, JsonNode?>
                {
                    [KnownProperties.Executable.Path] = JsonValue.Create("/repo/bin/worker")
                }
            },
            new ResourceSnapshot
            {
                Name = "custom",
                ResourceType = "Custom",
                State = "Running",
                Properties = new Dictionary<string, JsonNode?>
                {
                    [KnownProperties.Resource.Source] = JsonValue.Create("generic-source-secret")
                }
            },
            new ResourceSnapshot
            {
                Name = "case-mismatched-project",
                ResourceType = "project",
                State = "Running",
                Properties = new Dictionary<string, JsonNode?>
                {
                    [KnownProperties.Project.Path] = JsonValue.Create("/repo/CaseMismatched/CaseMismatched.csproj"),
                    [KnownProperties.Resource.Source] = JsonValue.Create("case-mismatch-secret")
                }
            });
        connection.DashboardUrlsState = new DashboardUrlsState
        {
            BaseUrlWithLoginToken = "https://dashboard-user:dashboard-password@dashboard.localhost:18888/login?t=dashboard-secret&view=resources#access_token=fragment-secret"
        };
        monitor.AddConnection("hash1", "socket.hash1", connection);

        var tool = new ListResourcesTool(monitor, NullLogger<ListResourcesTool>.Instance);
        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).DefaultTimeout();

        using var json = GetResourceData(result);
        var resources = json.RootElement;
        var resource = resources[0];

        Assert.Equal(
            ["name", "display_name", "resource_type", "state", "waiting_for", "source", "dashboard_url", "urls", "relationships", "commands"],
            resource.EnumerateObject().Select(p => p.Name));
        Assert.Equal(["Redis"], resource.GetProperty("waiting_for").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal("Api.csproj", resource.GetProperty("source").GetString());
        Assert.Equal(
            "https://localhost:5001/api?view=summary",
            resource.GetProperty("urls")[0].GetProperty("url").GetString());
        Assert.Equal(
            ["name", "display_name", "url", "is_internal"],
            resource.GetProperty("urls")[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal(
            ["type", "resource_name"],
            resource.GetProperty("relationships")[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal("redis-instance", resource.GetProperty("relationships")[0].GetProperty("resource_name").GetString());
        Assert.Equal(
            "https://dashboard.localhost:18888?view=resources&resource=api-service",
            resource.GetProperty("dashboard_url").GetString());
        Assert.False(resources[1].TryGetProperty("source", out _));
        Assert.False(resources[2].TryGetProperty("source", out _));
        Assert.Equal("redis:8", resources[3].GetProperty("source").GetString());
        Assert.Equal("worker", resources[4].GetProperty("source").GetString());
        await Verify(resource.GetProperty("commands").GetRawText(), "json");
    }

    [Fact]
    public async Task ListResourcesTool_DoesNotMaterializeUnrelatedProperties()
    {
        var cyclicValue = new List<object?>();
        cyclicValue.Add(cyclicValue);
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection(
            "hash1",
            "socket.hash1",
            CreateConnection(new ResourceSnapshot
            {
                Name = "custom",
                ResourceType = "Custom",
                State = "Running",
                Properties = new Dictionary<string, JsonNode?>
                {
                    ["unrelated.full.property"] = JsonValue.Create<object>(cyclicValue)
                }
            }));
        var tool = new ListResourcesTool(monitor, NullLogger<ListResourcesTool>.Instance);

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).DefaultTimeout();

        using var json = GetResourceData(result);
        Assert.Equal(
            ["name", "resource_type", "state", "waiting_for", "dashboard_url", "urls", "relationships", "commands"],
            json.RootElement[0].EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public async Task ListResourcesTool_ThrowsMcpErrorWithoutSensitiveDetails_WhenSnapshotRetrievalFails()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = CreateConnection();
        connection.GetResourceSnapshotsHandler = _ => throw new InvalidOperationException(
            "secret-value /other/Unrelated.AppHost.csproj PID 9876");
        monitor.AddConnection("hash1", "socket.hash1", connection);
        var sink = new TestSink();
        var logger = new TestLogger<ListResourcesTool>(
            new TestLoggerFactory(sink, enabled: true));
        var tool = new ListResourcesTool(monitor, logger);

        var exception = await Assert.ThrowsAsync<McpProtocolException>(
            () => tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).AsTask()).DefaultTimeout();

        Assert.Equal(McpErrorCode.InternalError, exception.ErrorCode);
        Assert.Equal("Unable to retrieve resources from the selected AppHost.", exception.Message);
        Assert.DoesNotContain(AppHostPath, exception.Message, StringComparison.Ordinal);
        Assert.Collection(
            sink.Writes,
            write =>
            {
                Assert.Equal($"Using single in-scope AppHost: {AppHostPath}", write.Message);
                Assert.Null(write.Exception);
            },
            write =>
            {
                Assert.Equal(
                    $"Error retrieving resources for AppHost {AppHostPath}: InvalidOperationException",
                    write.Message);
                Assert.Null(write.Exception);
            });
    }

    [Fact]
    public async Task ListResourcesTool_PropagatesRequestCancellationUnchanged()
    {
        using var cancellationSource = new CancellationTokenSource();
        var expectedException = new OperationCanceledException(cancellationSource.Token);
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = CreateConnection();
        connection.GetResourceSnapshotsHandler = cancellationToken =>
        {
            Assert.Equal(cancellationSource.Token, cancellationToken);
            throw expectedException;
        };
        monitor.AddConnection("hash1", "socket.hash1", connection);
        var tool = new ListResourcesTool(monitor, NullLogger<ListResourcesTool>.Instance);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => tool.CallToolAsync(CallToolContextTestHelper.Create(), cancellationSource.Token).AsTask()).DefaultTimeout();

        Assert.Same(expectedException, exception);
    }

    [Fact]
    public async Task ListResourcesTool_DoesNotExposeCandidateAppHostPaths_WhenMultipleAppHostsAreAvailable()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("hash1", "socket.hash1", CreateConnection("/repo/First/First.AppHost.csproj"));
        monitor.AddConnection("hash2", "socket.hash2", CreateConnection("/repo/Second/Second.AppHost.csproj"));

        var tool = new ListResourcesTool(monitor, NullLogger<ListResourcesTool>.Instance);

        var exception = await Assert.ThrowsAsync<McpProtocolException>(
            () => tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).AsTask()).DefaultTimeout();

        Assert.Equal(McpErrorCode.InternalError, exception.ErrorCode);
        Assert.Equal(
            "Multiple Aspire AppHosts are running in the MCP server's working directory scope. " +
            "Use 'select_apphost' to choose the AppHost for this request.",
            exception.Message);
    }

    [Fact]
    public async Task ListResourcesTool_DoesNotFallBack_WhenPinnedAppHostIsUnavailable()
    {
        const string pinnedAppHostPath = "/repo/Pinned/Pinned.AppHost.csproj";
        var monitor = new TestAuxiliaryBackchannelMonitor
        {
            SelectedAppHostPath = pinnedAppHostPath
        };
        monitor.AddConnection("hash1", "socket.hash1", CreateConnection("/repo/Other/Other.AppHost.csproj"));

        var tool = new ListResourcesTool(monitor, NullLogger<ListResourcesTool>.Instance);

        var exception = await Assert.ThrowsAsync<McpProtocolException>(
            () => tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).AsTask()).DefaultTimeout();

        Assert.Equal(McpErrorCode.InternalError, exception.ErrorCode);
        Assert.Equal(
            "The selected AppHost is not available. Start that AppHost and retry.",
            exception.Message);
        Assert.DoesNotContain(pinnedAppHostPath, exception.Message, StringComparison.Ordinal);
    }

    private static TestAppHostAuxiliaryBackchannel CreateConnection(params ResourceSnapshot[] snapshots)
        => CreateConnection(AppHostPath, snapshots);

    private static TestAppHostAuxiliaryBackchannel CreateConnection(string appHostPath, params ResourceSnapshot[] snapshots)
    {
        return new TestAppHostAuxiliaryBackchannel
        {
            AppHostInfo = new AppHostInformation
            {
                AppHostPath = appHostPath,
                ProcessId = 4242
            },
            ResourceSnapshots = [.. snapshots],
            DashboardUrlsState = new DashboardUrlsState
            {
                BaseUrlWithLoginToken = "http://localhost:18888/login?t=dashboard-secret"
            }
        };
    }

    private static JsonDocument GetResourceData(CallToolResult result)
    {
        Assert.NotNull(result.Content);
        var textContent = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        const string marker = "# RESOURCE DATA";
        var markerIndex = textContent.Text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(markerIndex >= 0, "Response should contain the resource data marker.");
        var jsonText = textContent.Text[(markerIndex + marker.Length)..].Trim();
        Assert.StartsWith("[", jsonText, StringComparison.Ordinal);

        return JsonDocument.Parse(jsonText);
    }

    private static JsonDocument GetPagination(CallToolResult result)
    {
        // Responses contain "# PAGINATION", its JSON object, then "# RESOURCE DATA" and its array.
        var text = GetResultText(result);
        const string marker = "# PAGINATION";
        var start = text.IndexOf(marker, StringComparison.Ordinal);
        var end = text.IndexOf("# RESOURCE DATA", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "Response should contain pagination before resource data.");

        return JsonDocument.Parse(text[(start + marker.Length)..end].Trim());
    }

    private static string GetResultText(CallToolResult result)
    {
        Assert.NotNull(result.Content);
        return Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
    }
}
