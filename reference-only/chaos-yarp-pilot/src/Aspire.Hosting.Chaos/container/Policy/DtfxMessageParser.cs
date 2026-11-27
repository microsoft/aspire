// <copyright file="DtfxMessageParser.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using System.Text;
using System.Text.Json;

namespace ChaosProxy.Container.Policy;

/// <summary>
/// Parses DurableTask Framework (DTFx) message envelopes from Azure Queue Storage
/// requests. The DTFx wire shape is: outer XML envelope <c>&lt;QueueMessage&gt;&lt;MessageText&gt;base64&lt;/MessageText&gt;&lt;/QueueMessage&gt;</c>,
/// inner base64-decoded payload is a JSON <c>TaskMessage</c> envelope containing
/// an <c>Event</c> object (one of the DurableTask.Core.History types) plus an
/// <c>OrchestrationInstance</c> with the workflow's instance id.
/// </summary>
/// <remarks>
/// <para>
/// We parse defensively — the DTFx serializer (Newtonsoft.Json with TypeNameHandling)
/// emits <c>$type</c> discriminators and a particular field-order/casing, but we
/// don't validate strict schema. We walk the JSON looking for the fields we need
/// and ignore everything else. Anything that doesn't look like a DTFx envelope
/// returns null and the caller treats it as "not a DTFx message" (chaos doesn't fire).
/// </para>
/// <para>
/// This parser is intentionally read-only and side-effect-free. The buffering
/// middleware calls it on each request body and stashes the result; the matcher
/// later consults the correlation store to decide whether to fire.
/// </para>
/// </remarks>
internal static class DtfxMessageParser
{
    /// <summary>The two event types we care about for activity replay races.</summary>
    public enum DtfxEventKind
    {
        Other,           // Anything we don't care to track (ExecutionStarted, TimerCreated, etc.)
        TaskScheduled,   // Orchestrator scheduling an activity. Carries activity Name + EventId.
        TaskCompleted,   // Activity reporting completion. Carries TaskScheduledId (refers back to the schedule event).
    }

    /// <summary>Outcome of parsing a single queue message.</summary>
    public sealed record DtfxMessage(
        DtfxEventKind Kind,
        string? InstanceId,
        string? ExecutionId,
        int? EventId,              // For TaskScheduled, this is the schedule's event id (= TaskCompleted.TaskScheduledId on the matching completion).
        int? TaskScheduledId,      // For TaskCompleted, this is the back-reference to TaskScheduled.EventId.
        string? ActivityName);     // For TaskScheduled, the activity name. Null on completion events.

    /// <summary>
    /// Parses the outer Azure Queue Storage XML envelope + inner DTFx JSON payload.
    /// Returns null on any of: not an XML envelope, no MessageText, not base64, not
    /// JSON, no Event property, or unrecognized event type.
    /// </summary>
    public static DtfxMessage? TryParse(string rawBody)
    {
        var decoded = TryDecodeQueueMessage(rawBody);
        if (decoded is null)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(decoded);
            return ExtractDtfxFields(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Public for tests: decodes the &lt;MessageText&gt;base64&lt;/MessageText&gt;
    /// payload to a UTF-8 string. Returns null if the body isn't a queue envelope
    /// or the base64 isn't valid.
    /// </summary>
    public static string? TryDecodeQueueMessage(string rawBody)
    {
        if (string.IsNullOrEmpty(rawBody))
        {
            return null;
        }

        const string openTag = "<MessageText>";
        const string closeTag = "</MessageText>";

        var openIdx = rawBody.IndexOf(openTag, StringComparison.OrdinalIgnoreCase);
        if (openIdx < 0)
        {
            return null;
        }
        var startIdx = openIdx + openTag.Length;
        var closeIdx = rawBody.IndexOf(closeTag, startIdx, StringComparison.OrdinalIgnoreCase);
        if (closeIdx < 0)
        {
            return null;
        }

        var base64 = rawBody.Substring(startIdx, closeIdx - startIdx).Trim();
        if (string.IsNullOrEmpty(base64))
        {
            return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(base64);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static DtfxMessage? ExtractDtfxFields(JsonElement root)
    {
        // The TaskMessage envelope can be the root, OR nested under "TaskMessage", OR
        // nested under "Message" (different storage providers use different wrappings).
        // Try a few common shapes.
        var taskMessage = root;
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("TaskMessage", out var inner)) { taskMessage = inner; }
            else if (root.TryGetProperty("Message", out var inner2)) { taskMessage = inner2; }
        }

        if (taskMessage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // Event object: TaskScheduledEvent, TaskCompletedEvent, etc.
        if (!taskMessage.TryGetProperty("Event", out var eventElement) || eventElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // EventType: either a string like "TaskScheduled" / "TaskCompleted", or a
        // numeric enum value (DurableTask.Core.History.EventType). The numeric values
        // are stable across versions:
        //   TaskCompleted = 5, TaskScheduled = 4 (per the EventType enum order)
        // We accept both. We could just check Name presence (only TaskScheduled has it)
        // but EventType is more reliable.
        var kind = ExtractEventKind(eventElement);

        // OrchestrationInstance: contains InstanceId + ExecutionId. Sometimes lives on
        // the TaskMessage envelope, sometimes on the Event itself depending on serializer.
        // Try both.
        string? instanceId = null;
        string? executionId = null;
        if (taskMessage.TryGetProperty("OrchestrationInstance", out var oi) && oi.ValueKind == JsonValueKind.Object)
        {
            instanceId = TryGetString(oi, "InstanceId");
            executionId = TryGetString(oi, "ExecutionId");
        }
        if (instanceId is null && eventElement.TryGetProperty("OrchestrationInstance", out var oi2) && oi2.ValueKind == JsonValueKind.Object)
        {
            instanceId = TryGetString(oi2, "InstanceId");
            executionId = TryGetString(oi2, "ExecutionId");
        }

        return kind switch
        {
            DtfxEventKind.TaskScheduled => new DtfxMessage(
                Kind: DtfxEventKind.TaskScheduled,
                InstanceId: instanceId,
                ExecutionId: executionId,
                EventId: TryGetInt(eventElement, "EventId"),
                TaskScheduledId: null,
                ActivityName: TryGetString(eventElement, "Name")),

            DtfxEventKind.TaskCompleted => new DtfxMessage(
                Kind: DtfxEventKind.TaskCompleted,
                InstanceId: instanceId,
                ExecutionId: executionId,
                EventId: TryGetInt(eventElement, "EventId"),
                TaskScheduledId: TryGetInt(eventElement, "TaskScheduledId"),
                ActivityName: null),

            _ => new DtfxMessage(
                Kind: DtfxEventKind.Other,
                InstanceId: instanceId,
                ExecutionId: executionId,
                EventId: null,
                TaskScheduledId: null,
                ActivityName: null),
        };
    }

    private static DtfxEventKind ExtractEventKind(JsonElement eventElement)
    {
        if (!eventElement.TryGetProperty("EventType", out var et))
        {
            // Fallback: presence of TaskScheduledId implies completion; presence of Name
            // (and EventId) implies scheduled. Neither = Other.
            if (eventElement.TryGetProperty("TaskScheduledId", out _)) { return DtfxEventKind.TaskCompleted; }
            if (eventElement.TryGetProperty("Name", out _)) { return DtfxEventKind.TaskScheduled; }
            return DtfxEventKind.Other;
        }

        if (et.ValueKind == JsonValueKind.String)
        {
            var s = et.GetString();
            return s switch
            {
                "TaskScheduled" => DtfxEventKind.TaskScheduled,
                "TaskCompleted" => DtfxEventKind.TaskCompleted,
                _ => DtfxEventKind.Other,
            };
        }

        if (et.ValueKind == JsonValueKind.Number && et.TryGetInt32(out var n))
        {
            // Per DurableTask.Core.History.EventType enum (values defined in source):
            //   TaskCompleted = 5
            //   TaskScheduled = 4
            return n switch
            {
                4 => DtfxEventKind.TaskScheduled,
                5 => DtfxEventKind.TaskCompleted,
                _ => DtfxEventKind.Other,
            };
        }

        return DtfxEventKind.Other;
    }

    private static string? TryGetString(JsonElement obj, string propertyName)
    {
        return obj.TryGetProperty(propertyName, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;
    }

    private static int? TryGetInt(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var p))
        {
            return null;
        }
        if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var n))
        {
            return n;
        }
        if (p.ValueKind == JsonValueKind.String && int.TryParse(p.GetString(), out var ns))
        {
            return ns;
        }
        return null;
    }
}
