// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.Backchannel;
using Aspire.TestUtilities;
using Microsoft.AspNetCore.InternalTesting;
using ModelContextProtocol.Protocol;
using Nerdbank.Streams;
using StreamJsonRpc;
using RequestId = StreamJsonRpc.RequestId;

namespace Aspire.Cli.Tests.Backchannel;

public class BackchannelJsonSerializerContextTests
{
    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(42L)]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    public void NumericRequestIdRoundTrips(long number)
    {
        var options = BackchannelJsonSerializerContext.CreateJsonSerializerOptions();
        var requestId = new RequestId(number);

        var json = JsonSerializer.SerializeToElement(requestId, options);

        Assert.Equal(JsonValueKind.Number, json.ValueKind);
        Assert.Equal(number, json.GetInt64());
        Assert.Equal(requestId, json.Deserialize<RequestId>(options));
    }

    [Theory]
    [InlineData("request-1")]
    [InlineData("")]
    [InlineData("request-\"quoted\"")]
    [InlineData(null)]
    public void StringAndNullRequestIdRoundTrip(string? text)
    {
        var options = BackchannelJsonSerializerContext.CreateJsonSerializerOptions();
        var requestId = new RequestId(text);

        var json = JsonSerializer.SerializeToElement(requestId, options);

        Assert.Equal(text is null ? JsonValueKind.Null : JsonValueKind.String, json.ValueKind);
        Assert.Equal(text, json.GetString());
        Assert.Equal(requestId, json.Deserialize<RequestId>(options));
    }

    [Theory]
    [InlineData("9223372036854775808")]
    [InlineData("-9223372036854775809")]
    [InlineData("1.5")]
    [InlineData("1e2")]
    public void RequestIdPreservesNumbersOutsideInt64AsStrings(string json)
    {
        var options = BackchannelJsonSerializerContext.CreateJsonSerializerOptions();

        var requestId = JsonSerializer.Deserialize<RequestId>(json, options);
        var serialized = JsonSerializer.SerializeToElement(requestId, options);

        Assert.Equal(new RequestId(json), requestId);
        Assert.Equal(JsonValueKind.String, serialized.ValueKind);
        Assert.Equal(json, serialized.GetString());
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void RequestIdRejectsInvalidTokens(string json)
    {
        var options = BackchannelJsonSerializerContext.CreateJsonSerializerOptions();

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RequestId>(json, options));
    }

    [Fact]
    public void UnspecifiedRequestIdSerializesAsNull()
    {
        var options = BackchannelJsonSerializerContext.CreateJsonSerializerOptions();

        var json = JsonSerializer.SerializeToElement(RequestId.NotSpecified, options);

        Assert.Equal(JsonValueKind.Null, json.ValueKind);
    }

    [Fact]
    [QuarantinedTest("https://github.com/microsoft/aspire/issues/20666")]
    public async Task RpcCancellationReachesServer()
    {
        var (clientStream, serverStream) = FullDuplexStream.CreatePair();
        using var client = new JsonRpc(new HeaderDelimitedMessageHandler(
            clientStream, clientStream, BackchannelJsonSerializerContext.CreateRpcMessageFormatter()));
        using var server = new JsonRpc(new HeaderDelimitedMessageHandler(
            serverStream, serverStream, BackchannelJsonSerializerContext.CreateRpcMessageFormatter()));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.AddLocalRpcMethod("wait", (Func<CancellationToken, Task>)(async cancellationToken =>
        {
            using var registration = cancellationToken.Register(() => cancelled.TrySetResult());
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }));
        server.AddLocalRpcMethod("ping", (Func<bool>)(() => true));
        server.StartListening();
        client.StartListening();

        using var cts = new CancellationTokenSource();
        var invocation = client.InvokeWithCancellationAsync("wait", arguments: null, cts.Token);
        await started.Task.DefaultTimeout();
        await cts.CancelAsync();

        await cancelled.Task.DefaultTimeout();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation.DefaultTimeout());
        Assert.True(await client.InvokeAsync<bool>("ping").DefaultTimeout());
    }

    [Fact]
    public void JsonSerializerOptionsSerializeInteractionMessageActionTargetsAsAUnion()
    {
        var options = BackchannelJsonSerializerContext.CreateJsonSerializerOptions();
        InteractionMessageAction[] actions =
        [
            InteractionMessageAction.ExecuteCommand("Retry", "aspire-vscode.retry"),
            InteractionMessageAction.OpenFile("Open CLI Log", @"C:\logs\aspire.cli.log")
        ];

        var json = JsonSerializer.Serialize(actions, options);

        Assert.Equal(
            """[{"displayName":"Retry","command":"aspire-vscode.retry"},{"displayName":"Open CLI Log","filePath":"C:\\logs\\aspire.cli.log"}]""",
            json);
    }

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
        // Back-compat: an older AppHost (pre-terminals.v1) that only knows about the original
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
    public async Task ListTerminalsResponse_RoundTripsThroughSerializer()
    {
        var options = BackchannelJsonSerializerContext.CreateJsonSerializerOptions();
        options.WriteIndented = true;
        var response = new ListTerminalsResponse
        {
            AppHostTerminals =
            [
                new AppHostTerminalSummary
                {
                    TerminalId = "terminal-1",
                    Title = "Shell",
                    Placement = "Dock"
                }
            ],
            ResourceTerminals =
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
                            ConsumerUdsPath = "/terminal/r0.sock",
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
        Assert.Single(roundTripped.ResourceTerminals);

        var terminal = roundTripped.ResourceTerminals[0];
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

        var appHostTerminal = Assert.Single(roundTripped.AppHostTerminals);
        Assert.Equal("terminal-1", appHostTerminal.TerminalId);
        Assert.Equal("Shell", appHostTerminal.Title);
        Assert.Equal("Dock", appHostTerminal.Placement);

        await Verify(json, "json");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"resourceTerminals":[]}""")]
    [InlineData("""{"appHostTerminals":[]}""")]
    [InlineData("""{"terminals":[],"appHostTerminals":[]}""")]
    public void ListTerminalsResponse_MissingRequiredCollections_Throws(string json)
    {
        var options = BackchannelJsonSerializerContext.CreateJsonSerializerOptions();

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ListTerminalsResponse>(json, options));
    }

    [Fact]
    public void ListTerminalsResponse_EmptyCollections_Deserializes()
    {
        var options = BackchannelJsonSerializerContext.CreateJsonSerializerOptions();
        var response = JsonSerializer.Deserialize<ListTerminalsResponse>(
            """{"resourceTerminals":[],"appHostTerminals":[]}""", options);

        Assert.NotNull(response);
        Assert.Empty(response.ResourceTerminals);
        Assert.Empty(response.AppHostTerminals);
    }
}
