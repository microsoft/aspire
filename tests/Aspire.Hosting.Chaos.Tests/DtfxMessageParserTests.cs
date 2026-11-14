// <copyright file="DtfxMessageParserTests.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using System.Text;
using ChaosProxy.Container.Policy;

namespace Aspire.Hosting.Chaos.UnitTests;

public class DtfxMessageParserTests
{
    private static string Envelope(string innerJson)
    {
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(innerJson));
        return $"<QueueMessage><MessageText>{base64}</MessageText></QueueMessage>";
    }

    [Fact]
    public void TryParse_NonEnvelope_ReturnsNull()
    {
        Assert.Null(DtfxMessageParser.TryParse("just some text"));
        Assert.Null(DtfxMessageParser.TryParse(""));
        Assert.Null(DtfxMessageParser.TryParse("<NotMessage>data</NotMessage>"));
    }

    [Fact]
    public void TryParse_NotBase64_ReturnsNull()
    {
        var raw = "<QueueMessage><MessageText>!!!not-base64!!!</MessageText></QueueMessage>";
        Assert.Null(DtfxMessageParser.TryParse(raw));
    }

    [Fact]
    public void TryParse_Base64ButNotJson_ReturnsNull()
    {
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("not json"));
        var raw = $"<QueueMessage><MessageText>{base64}</MessageText></QueueMessage>";
        Assert.Null(DtfxMessageParser.TryParse(raw));
    }

    [Fact]
    public void TryParse_TaskScheduled_StringEventType_ExtractsActivityName()
    {
        var inner = """
        {
          "Event": {
            "$type": "DurableTask.Core.History.TaskScheduledEvent, DurableTask.Core",
            "EventType": "TaskScheduled",
            "EventId": 3,
            "Name": "TriggerScenarioEvaluation",
            "Version": "1.0",
            "Input": "{\"workspaceId\":\"abc\"}"
          },
          "OrchestrationInstance": {
            "InstanceId": "instance-xyz",
            "ExecutionId": "exec-001"
          }
        }
        """;
        var msg = DtfxMessageParser.TryParse(Envelope(inner));

        Assert.NotNull(msg);
        Assert.Equal(DtfxMessageParser.DtfxEventKind.TaskScheduled, msg!.Kind);
        Assert.Equal("instance-xyz", msg.InstanceId);
        Assert.Equal("exec-001", msg.ExecutionId);
        Assert.Equal(3, msg.EventId);
        Assert.Equal("TriggerScenarioEvaluation", msg.ActivityName);
        Assert.Null(msg.TaskScheduledId);
    }

    [Fact]
    public void TryParse_TaskScheduled_NumericEventType_StillRecognized()
    {
        // DurableTask.Core.History.EventType.TaskScheduled = 4
        var inner = """
        {
          "Event": {
            "EventType": 4,
            "EventId": 7,
            "Name": "UpdateManagedIdentity"
          },
          "OrchestrationInstance": { "InstanceId": "i", "ExecutionId": "e" }
        }
        """;
        var msg = DtfxMessageParser.TryParse(Envelope(inner));

        Assert.NotNull(msg);
        Assert.Equal(DtfxMessageParser.DtfxEventKind.TaskScheduled, msg!.Kind);
        Assert.Equal("UpdateManagedIdentity", msg.ActivityName);
        Assert.Equal(7, msg.EventId);
    }

    [Fact]
    public void TryParse_TaskCompleted_StringEventType_ExtractsTaskScheduledId()
    {
        var inner = """
        {
          "Event": {
            "$type": "DurableTask.Core.History.TaskCompletedEvent, DurableTask.Core",
            "EventType": "TaskCompleted",
            "EventId": -1,
            "TaskScheduledId": 3,
            "Result": "\"operation-id-abc\""
          },
          "OrchestrationInstance": {
            "InstanceId": "instance-xyz",
            "ExecutionId": "exec-001"
          }
        }
        """;
        var msg = DtfxMessageParser.TryParse(Envelope(inner));

        Assert.NotNull(msg);
        Assert.Equal(DtfxMessageParser.DtfxEventKind.TaskCompleted, msg!.Kind);
        Assert.Equal("instance-xyz", msg.InstanceId);
        Assert.Equal(3, msg.TaskScheduledId);
        Assert.Null(msg.ActivityName);
    }

    [Fact]
    public void TryParse_TaskCompleted_NumericEventType_StillRecognized()
    {
        // DurableTask.Core.History.EventType.TaskCompleted = 5
        var inner = """
        {
          "Event": {
            "EventType": 5,
            "TaskScheduledId": 42
          },
          "OrchestrationInstance": { "InstanceId": "i", "ExecutionId": "e" }
        }
        """;
        var msg = DtfxMessageParser.TryParse(Envelope(inner));

        Assert.NotNull(msg);
        Assert.Equal(DtfxMessageParser.DtfxEventKind.TaskCompleted, msg!.Kind);
        Assert.Equal(42, msg.TaskScheduledId);
    }

    [Fact]
    public void TryParse_OtherEventType_ReturnsOther()
    {
        var inner = """
        {
          "Event": {
            "EventType": "ExecutionStarted",
            "EventId": -1,
            "Name": "MyOrchestrator"
          },
          "OrchestrationInstance": { "InstanceId": "i" }
        }
        """;
        var msg = DtfxMessageParser.TryParse(Envelope(inner));

        Assert.NotNull(msg);
        Assert.Equal(DtfxMessageParser.DtfxEventKind.Other, msg!.Kind);
    }

    [Fact]
    public void TryParse_WrappedInTaskMessage_StillExtracts()
    {
        // Some storage providers wrap the envelope under TaskMessage.
        var inner = """
        {
          "TaskMessage": {
            "Event": {
              "EventType": "TaskScheduled",
              "EventId": 1,
              "Name": "DoStuff"
            },
            "OrchestrationInstance": { "InstanceId": "instance-1", "ExecutionId": "exec-1" }
          },
          "ActivityId": "act-1"
        }
        """;
        var msg = DtfxMessageParser.TryParse(Envelope(inner));

        Assert.NotNull(msg);
        Assert.Equal("DoStuff", msg!.ActivityName);
        Assert.Equal("instance-1", msg.InstanceId);
    }

    [Fact]
    public void TryParse_OrchestrationInstanceOnEvent_AlsoWorks()
    {
        // Fallback: some serializers put OrchestrationInstance on the Event itself.
        var inner = """
        {
          "Event": {
            "EventType": "TaskScheduled",
            "EventId": 1,
            "Name": "DoStuff",
            "OrchestrationInstance": { "InstanceId": "inner-i", "ExecutionId": "inner-e" }
          }
        }
        """;
        var msg = DtfxMessageParser.TryParse(Envelope(inner));

        Assert.NotNull(msg);
        Assert.Equal("DoStuff", msg!.ActivityName);
        Assert.Equal("inner-i", msg.InstanceId);
    }

    [Fact]
    public void TryParse_FallbackOnEventTypeMissing_TaskScheduledViaNamePresence()
    {
        // No EventType but Name + EventId present → infer TaskScheduled.
        var inner = """
        {
          "Event": { "EventId": 5, "Name": "ImpliedScheduled" },
          "OrchestrationInstance": { "InstanceId": "i" }
        }
        """;
        var msg = DtfxMessageParser.TryParse(Envelope(inner));

        Assert.NotNull(msg);
        Assert.Equal(DtfxMessageParser.DtfxEventKind.TaskScheduled, msg!.Kind);
        Assert.Equal("ImpliedScheduled", msg.ActivityName);
    }

    [Fact]
    public void TryParse_FallbackOnEventTypeMissing_TaskCompletedViaTaskScheduledIdPresence()
    {
        var inner = """
        {
          "Event": { "TaskScheduledId": 5, "Result": "\"x\"" },
          "OrchestrationInstance": { "InstanceId": "i" }
        }
        """;
        var msg = DtfxMessageParser.TryParse(Envelope(inner));

        Assert.NotNull(msg);
        Assert.Equal(DtfxMessageParser.DtfxEventKind.TaskCompleted, msg!.Kind);
        Assert.Equal(5, msg.TaskScheduledId);
    }
}
