// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;

namespace Aspire.Dashboard.Telemetry;

// Types from MS.VS.VisualStudio that we are using in lieu of referencing the entire package

public class TelemetryEventCorrelation
{
    [JsonPropertyName("id")]
    public required Guid Id { get; set; }

    [JsonPropertyName("eventType")]
    [JsonConverter(typeof(JsonStringEnumConverter<DataModelEventType>))]
    public DataModelEventType EventType { get; set; }
}

public enum TelemetryResult
{
    /// <summary>Used for unknown or unavailable result.</summary>
    None,
    /// <summary>A result without any failure from product or user.</summary>
    Success,
    /// <summary>
    /// A result to indicate the action/operation failed because of product issue (not user faults)
    /// Consider using FaultEvent to provide more details about the failure.
    /// </summary>
    Failure,
    /// <summary>
    /// A result to indicate the action/operation failed because of user fault (e.g., invalid input).
    /// Consider using FaultEvent to provide more details.
    /// </summary>
    UserFault,
    /// <summary>
    /// A result to indicate the action/operation is cancelled by user.
    /// </summary>
    UserCancel,
}

public enum DataModelEventType
{
    /// <summary>User task event</summary>
    UserTask,
    /// <summary>Trace event</summary>
    Trace,
    /// <summary>Operation event</summary>
    Operation,
    /// <summary>Fault event</summary>
    Fault,
    /// <summary>Asset event</summary>
    Asset,
}

public enum TelemetrySeverity
{
    /// <summary>indicates telemetry event with verbose information.</summary>
    Low = -10, // 0xFFFFFFF6
    /// <summary>indicates a regular telemetry event.</summary>
    Normal = 0,
    /// <summary>
    /// indicates telemetry event with high value or require attention (e.g., fault).
    /// </summary>
    High = 10, // 0x0000000A
}

public enum FaultSeverity
{
    /// <summary>
    /// Uncategorized faults have no severity assigned by the developer. Developers should NOT use this severity in any new instrumentation.
    /// The majority of uncategorized faults are being assigned the uncategorized value by default in legacy code.
    /// Teams with high volumes of uncategorized fault data may be asked to make changes to add real severity to their faults.
    /// </summary>
    Uncategorized,
    /// <summary>
    /// Diagnostics faults represent faults which are likely informational in nature. The fault may have no clear tangible impact, it may
    /// be considered "by design" but still undesirable, or the fault only matters in relation to other faults. The fault information is
    /// nonetheless useful to investigate or root-cause an issue, or to inform future investments or changes to design, but the fault
    /// is not itself an indicator of an issue warranting attention.
    /// </summary>
    Diagnostic,
    /// <summary>
    /// General faults are the most common type of fault - the impact or significance of the fault may not be known during instrumentation.
    /// Further investigation may be required to understand the nature of the fault and, if possible, assign a more useful severity.
    /// </summary>
    General,
    /// <summary>
    /// Critical faults are faults which represent likely bugs or notable user impact. If this kind of fault is seen, there is a high
    /// likelihood that there is some kind of bug ultimately causing the issue.
    /// </summary>
    Critical,
    /// <summary>
    /// Crash faults are faults which definitively represent a bug or notable user impact because they represent a fatal crash. While
    /// Watson or other systems may collect a crash dump, crash faults are likely to include other contextual diagnostic information.
    /// </summary>
    Crash,
}
