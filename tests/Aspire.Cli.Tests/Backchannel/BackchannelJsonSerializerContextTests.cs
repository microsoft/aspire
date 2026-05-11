// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.Backchannel;
using ModelContextProtocol.Protocol;

namespace Aspire.Cli.Tests.Backchannel;

public class BackchannelJsonSerializerContextTests
{
    [Fact]
    public void JsonSerializerOptionsCanSerializeAndDeserializeResourceSnapshotMcpServers()
    {
        var options = BackchannelJsonSerializerContext.CreateJsonSerializerOptions();

        var servers = new Aspire.Cli.Backchannel.ResourceSnapshotMcpServer[]
        {
            new()
            {
                EndpointUrl = "http://localhost:8000",
                Tools =
                [
                    new Tool
                    {
                        Name = "query",
                        Description = "Runs a SQL query",
                        InputSchema = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"sql\":{\"type\":\"string\"}}}").RootElement
                    }
                ]
            }
        };

        var json = JsonSerializer.Serialize(servers, options);
        var roundTripped = JsonSerializer.Deserialize<Aspire.Cli.Backchannel.ResourceSnapshotMcpServer[]>(json, options);

        Assert.NotNull(roundTripped);
        Assert.Single(roundTripped);
        Assert.Equal("http://localhost:8000", roundTripped[0].EndpointUrl);
        Assert.Single(roundTripped[0].Tools);
        Assert.Equal("query", roundTripped[0].Tools[0].Name);
    }

    [Fact]
    public void JsonSerializerOptionsCanSerializeAndDeserializeDictionaryStringJsonElement()
    {
        var options = BackchannelJsonSerializerContext.CreateJsonSerializerOptions();

        var payload = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["sql"] = JsonDocument.Parse("\"select 1\"").RootElement,
            ["limit"] = JsonDocument.Parse("1").RootElement
        };

        var json = JsonSerializer.Serialize(payload, options);
        var roundTripped = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, options);

        Assert.NotNull(roundTripped);
        Assert.Equal("select 1", roundTripped["sql"].GetString());
        Assert.Equal(1, roundTripped["limit"].GetInt32());
    }

    [Fact]
    public void JsonSerializerOptionsCanDeserializePublishingActivityWithoutHierarchyMetadata()
    {
        var options = BackchannelJsonSerializerContext.CreateJsonSerializerOptions();
        var json =
            """
            {
              "Type": "step",
              "Data": {
                "Id": "step-1",
                "StatusText": "Prepare",
                "CompletionState": "InProgress"
              }
            }
            """;

        var activity = JsonSerializer.Deserialize<PublishingActivity>(json, options);

        Assert.NotNull(activity);
        Assert.Equal(PublishingActivityTypes.Step, activity.Type);
        Assert.Equal("step-1", activity.Data.Id);
        Assert.Equal("Prepare", activity.Data.StatusText);
        Assert.Null(activity.Data.ParentStepId);
        Assert.Null(activity.Data.HierarchyLevel);
        Assert.Null(activity.Data.CompletionMessage);
        Assert.Equal(CompletionStates.InProgress, activity.Data.CompletionState);
    }

    [Fact]
    public void TerminalReplicaInfo_OldPayloadWithoutNewFields_DeserializesWithNulls()
    {
        // Back-compat: an older AppHost (pre-terminals.ps.v1) that only knows about the original
        // TerminalReplicaInfo shape will not emit CurrentColumns/CurrentRows/AttachedPeerCount/Peers.
        // The CLI must accept that payload and treat the new fields as null. See
        // docs/specs/cli-backchannel.md §3 for the per-feature capability strategy.
        var options = BackchannelJsonSerializerContext.CreateJsonSerializerOptions();
        var json =
            """
            {
              "ReplicaIndex": 0,
              "Label": "myresource-0",
              "ConsumerUdsPath": "/tmp/r0.sock",
              "IsAlive": true,
              "ProducerConnected": true,
              "RestartCount": 0
            }
            """;

        var replica = JsonSerializer.Deserialize<TerminalReplicaInfo>(json, options);

        Assert.NotNull(replica);
        Assert.Equal(0, replica.ReplicaIndex);
        Assert.Equal("myresource-0", replica.Label);
        Assert.True(replica.IsAlive);
        Assert.Null(replica.CurrentColumns);
        Assert.Null(replica.CurrentRows);
        Assert.Null(replica.AttachedPeerCount);
        Assert.Null(replica.Peers);
    }

    [Fact]
    public void ListTerminalsResponse_RoundTripsThroughSerializer()
    {
        var options = BackchannelJsonSerializerContext.CreateJsonSerializerOptions();
        var response = new ListTerminalsResponse
        {
            Terminals =
            [
                new TerminalSummary
                {
                    ResourceName = "myresource",
                    DisplayName = "myresource",
                    ConfiguredColumns = 120,
                    ConfiguredRows = 30,
                    IsHostReachable = true,
                    Replicas =
                    [
                        new TerminalReplicaInfo
                        {
                            ReplicaIndex = 0,
                            Label = "myresource-0",
                            ConsumerUdsPath = "/tmp/r0.sock",
                            IsAlive = true,
                            CurrentColumns = 130,
                            CurrentRows = 32,
                            AttachedPeerCount = 1,
                            Peers =
                            [
                                new TerminalPeerInfo { PeerId = "peer-1", DisplayName = "viewer-1" }
                            ]
                        }
                    ]
                }
            ]
        };

        var json = JsonSerializer.Serialize(response, options);
        var roundTripped = JsonSerializer.Deserialize<ListTerminalsResponse>(json, options);

        Assert.NotNull(roundTripped);
        Assert.Single(roundTripped.Terminals);

        var terminal = roundTripped.Terminals[0];
        Assert.Equal("myresource", terminal.ResourceName);
        Assert.True(terminal.IsHostReachable);
        Assert.NotNull(terminal.Replicas);
        Assert.Single(terminal.Replicas);

        var replica = terminal.Replicas[0];
        Assert.Equal(130, replica.CurrentColumns);
        Assert.Equal(32, replica.CurrentRows);
        Assert.Equal(1, replica.AttachedPeerCount);
        Assert.NotNull(replica.Peers);
        Assert.Single(replica.Peers);
        Assert.Equal("peer-1", replica.Peers[0].PeerId);
        Assert.Equal("viewer-1", replica.Peers[0].DisplayName);
    }
}
