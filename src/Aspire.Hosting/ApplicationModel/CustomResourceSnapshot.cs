// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Diagnostics;
using Aspire.Dashboard.Model;
using Aspire.Hosting.Dcp.Model;
using HealthStatus = Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// An immutable snapshot of the state of a resource.
/// </summary>
[DebuggerDisplay("ResourceType = {ResourceType,nq}, State = {State?.Text,nq}, HealthStatus = {HealthStatus?.ToString(),nq}, Properties = {Properties.Length}")]
public sealed record CustomResourceSnapshot
{
    private readonly ImmutableArray<HealthReportSnapshot> _healthReports = [];
    private readonly ResourceStateSnapshot? _state;

    /// <summary>
    /// Monotonically increasing version number for the snapshot.
    /// </summary>
    internal long Version { get; init; }

    /// <summary>
    /// The type of the resource.
    /// </summary>
    public required string ResourceType { get; init; }

    /// <summary>
    /// The properties that should show up in the dashboard for this resource.
    /// </summary>
    public required ImmutableArray<ResourcePropertySnapshot> Properties { get; init; }

    /// <summary>
    /// The creation timestamp of the resource.
    /// </summary>
    public DateTime? CreationTimeStamp { get; init; }

    /// <summary>
    /// The start timestamp of the resource.
    /// </summary>
    public DateTime? StartTimeStamp { get; init; }

    /// <summary>
    /// The stop timestamp of the resource.
    /// </summary>
    public DateTime? StopTimeStamp { get; init; }

    /// <summary>
    /// Represents the state of the resource.
    /// </summary>
    public ResourceStateSnapshot? State
    {
        get => _state;
        init
        {
            _state = value;
            HealthStatus = ComputeHealthStatus(_healthReports, value?.Text);
        }
    }

    /// <summary>
    /// The exit code of the resource.
    /// </summary>
    public int? ExitCode { get; init; }

    /// <summary>
    /// A snapshot of the event that indicates the resource is ready.
    /// </summary>
    internal EventSnapshot? ResourceReadyEvent { get; init; }

    /// <summary>
    /// Gets the health status of the resource.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This value is derived from <see cref="HealthReports"/>.
    /// </para>
    /// </remarks>
    public HealthStatus? HealthStatus { get; private set; }

    /// <summary>
    /// The health reports for this resource.
    /// </summary>
    /// <remarks>
    /// May be zero or more. If there are no health reports, the resource is considered healthy
    /// so long as no heath checks are registered for the resource.
    /// </remarks>
    public ImmutableArray<HealthReportSnapshot> HealthReports
    {
        get => _healthReports;
        internal init
        {
            _healthReports = value;
            HealthStatus = ComputeHealthStatus(value, State?.Text);
        }
    }

    /// <summary>
    /// The environment variables that should show up in the dashboard for this resource.
    /// </summary>
    public ImmutableArray<EnvironmentVariableSnapshot> EnvironmentVariables { get; init; } = [];

    /// <summary>
    /// The URLs that should show up in the dashboard for this resource.
    /// </summary>
    public ImmutableArray<UrlSnapshot> Urls { get; init; } = [];

    /// <summary>
    /// The volumes that should show up in the dashboard for this resource.
    /// </summary>
    public ImmutableArray<VolumeSnapshot> Volumes { get; init; } = [];

    /// <summary>
    /// The commands available in the dashboard for this resource.
    /// </summary>
    public ImmutableArray<ResourceCommandSnapshot> Commands { get; init; } = [];

    /// <summary>
    /// The relationships to other resources.
    /// </summary>
    public ImmutableArray<RelationshipSnapshot> Relationships { get; init; } = [];

    /// <summary>
    /// Whether this resource should be hidden in UI.
    /// </summary>
    public bool IsHidden { get; init; }

    /// <summary>
    /// Whether this resource is a built-in resource and supports usage telemetry.
    /// </summary>
    internal bool SupportsDetailedTelemetry { get; init; }

    /// <summary>
    /// The custom icon name for the resource. This should be a valid FluentUI icon name.
    /// If not specified, the dashboard will use default icons based on the resource type.
    /// </summary>
    public string? IconName { get; init; }

    /// <summary>
    /// The custom icon variant for the resource.
    /// </summary>
    public IconVariant? IconVariant { get; init; }

    internal static HealthStatus? ComputeHealthStatus(ImmutableArray<HealthReportSnapshot> healthReports, string? state)
    {
        if (state != KnownResourceStates.Running)
        {
            return null;
        }

        return healthReports.Length == 0
            // If there are no health reports and the resource is running, assume it's healthy.
            ? Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy
            // If there are health reports, the health status is the minimum of the health status of the reports.
            // If any of the reports is null (first health check has not returned), the health status is unhealthy.
            : healthReports.MinBy(r => r.Status)?.Status
                ?? Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy;
    }
}

/// <summary>
/// A snapshot of an event.
/// </summary>
/// <param name="EventTask">The task the represents the result of executing the event.</param>
internal record EventSnapshot(Task EventTask);

/// <summary>
/// A snapshot of the resource state
/// </summary>
/// <param name="Text">The text for the state update. See <see cref="KnownResourceStates"/> for expected values.</param>
/// <param name="Style">The style for the state update. Use <seealso cref="KnownResourceStateStyles"/> for the supported styles.</param>
[DebuggerDisplay("{Text}")]
public sealed record ResourceStateSnapshot(string Text, string? Style)
{
    /// <summary>
    /// Convert text to state snapshot. The style will be null by default
    /// </summary>
    /// <param name="s"></param>
    public static implicit operator ResourceStateSnapshot?(string? s) =>
        s is null ? null : new(Text: s, Style: null);
}

/// <summary>
/// A snapshot of an environment variable.
/// </summary>
/// <param name="Name">The name of the environment variable.</param>
/// <param name="Value">The value of the environment variable.</param>
/// <param name="IsFromSpec">Determines if this environment variable was defined in the resource explicitly or computed (for e.g. inherited from the process hierarchy).</param>
[DebuggerDisplay("{Value}", Name = "{Name}")]
public sealed record EnvironmentVariableSnapshot(string Name, string? Value, bool IsFromSpec);

/// <summary>
/// A snapshot of the URL.
/// </summary>
/// <param name="Name">Name of the endpoint associated with the URL.</param>
/// <param name="Url">The full URL.</param>
/// <param name="IsInternal">Determines if this URL is internal. Internal URLs are only shown in the details grid for a resource.</param>
[DebuggerDisplay("{Url}", Name = "{Name}")]
public sealed record UrlSnapshot(string? Name, string Url, bool IsInternal)
{
    /// <summary>
    /// The UI display properties for the url.
    /// </summary>
    public UrlDisplayPropertiesSnapshot DisplayProperties { get; init; } = new();

    /// <summary>
    /// Whether this URL is inactive or not.
    /// </summary>
    /// <remarks>
    /// Inactive URLs are not displayed in UI.
    /// </remarks>
    public bool IsInactive { get; init; }

    internal void Deconstruct(out string? name, out string url, out bool isInternal, out bool isInactive)
    {
        name = Name;
        url = Url;
        isInternal = IsInternal;
        isInactive = IsInactive;
    }
}

/// <summary>
/// A snapshot of the display properties for a url.
/// </summary>
/// <param name="DisplayName">The display name of the url.</param>
/// <param name="SortOrder">The order of the url in UI. Higher numbers are displayed first in the UI.</param>
public sealed record UrlDisplayPropertiesSnapshot(string DisplayName = "", int SortOrder = 0);

/// <summary>
/// A snapshot of a volume, mounted to a container.
/// </summary>
/// <param name="Source">The name of the volume. Can be <see langword="null"/> if the mount is an anonymous volume.</param>
/// <param name="Target">The target of the mount.</param>
/// <param name="MountType">Gets the mount type, such as <see cref="VolumeMountType.Bind"/> or <see cref="VolumeMountType.Volume"/></param>
/// <param name="IsReadOnly">Whether the volume mount is read-only or not.</param>
[DebuggerDisplay("{Source}", Name = "{Target}")]
public sealed record VolumeSnapshot(string? Source, string Target, string MountType, bool IsReadOnly);

/// <summary>
/// A snapshot of a relationship.
/// </summary>
/// <param name="ResourceName">The name of the resource the relationship is to.</param>
/// <param name="Type">The relationship type.</param>
public sealed record RelationshipSnapshot(string ResourceName, string Type);

/// <summary>
/// A snapshot of the resource property.
/// </summary>
/// <param name="Name">The name of the property.</param>
/// <param name="Value">The value of the property.</param>
[DebuggerDisplay("{Value}", Name = "{Name}")]
public sealed record ResourcePropertySnapshot(string Name, object? Value)
{
    /// <summary>
    /// Whether this property is considered sensitive or not.
    /// </summary>
    /// <remarks>
    /// Sensitive properties are masked when displayed in UI and require an explicit user action to reveal.
    /// </remarks>
    public bool IsSensitive { get; init; }

    internal void Deconstruct(out string name, out object? value, out bool isSensitive)
    {
        name = Name;
        value = Value;
        isSensitive = IsSensitive;
    }
}

/// <summary>
/// A snapshot of a resource command.
/// </summary>
/// <param name="Name">The name of command. The name uniquely identifies the command.</param>
/// <param name="State">The state of the command.</param>
/// <param name="DisplayName">The display name visible in UI for the command.</param>
/// <param name="DisplayDescription">
/// Optional description of the command, to be shown in the UI.
/// Could be used as a tooltip. May be localized.
/// </param>
/// <param name="Parameter">
/// Obsolete optional parameter that configures the command in some way.
/// Clients must return any value provided by the server when invoking the command.
/// </param>
/// <param name="ConfirmationMessage">
/// When a confirmation message is specified, the UI will prompt with an OK/Cancel dialog
/// and the confirmation message before starting the command.
/// </param>
/// <param name="IconName">The icon name for the command. The name should be a valid FluentUI icon name. https://aka.ms/fluentui-system-icons</param>
/// <param name="IconVariant">The icon variant.</param>
/// <param name="IsHighlighted">A flag indicating whether the command is highlighted in the UI.</param>
[DebuggerDisplay(null, Name = "{Name}")]
public sealed record ResourceCommandSnapshot(string Name, ResourceCommandState State, string DisplayName, string? DisplayDescription, [property: Obsolete("Use ArgumentInputs to describe invocation arguments.")] object? Parameter, string? ConfirmationMessage, string? IconName, IconVariant? IconVariant, bool IsHighlighted)
{
#pragma warning disable ASPIREINTERACTION001 // InteractionInput is used to describe dashboard command arguments.
    /// <summary>
    /// Gets the inputs used to describe the invocation arguments accepted by the command.
    /// </summary>
    public IReadOnlyList<InteractionInput>? ArgumentInputs { get; init; }
#pragma warning restore ASPIREINTERACTION001
}

/// <summary>
/// A report produced by a health check about a resource.
/// </summary>
/// <param name="Name">The name of the health check that produced this report.</param>
/// <param name="Status">The state of the resource, according to the report, or <see langword="null"/> if a health report has not yet been received for this health check.</param>
/// <param name="Description">An optional description of the report, for display.</param>
/// <param name="ExceptionText">An optional string containing exception details.</param>
[DebuggerDisplay("{Status}", Name = "{Name}")]
public sealed record HealthReportSnapshot(string Name, HealthStatus? Status, string? Description, string? ExceptionText)
{
    /// <summary>
    /// The timestamp when this health check was last executed, or <see langword="null"/> if not available.
    /// </summary>
    public DateTime? LastRunAt { get; init; }
}

/// <summary>
/// The state of a resource command.
/// </summary>
public enum ResourceCommandState
{
    /// <summary>
    /// Command is visible and enabled for use.
    /// </summary>
    Enabled,
    /// <summary>
    /// Command is visible and disabled for use.
    /// </summary>
    Disabled,
    /// <summary>
    /// Command is hidden.
    /// </summary>
    Hidden
}

/// <summary>
/// The set of well known resource states.
/// </summary>
public static class KnownResourceStateStyles
{
    /// <summary>
    /// The success state
    /// </summary>
    public static readonly string Success = "success";

    /// <summary>
    /// The error state. Useful for error messages.
    /// </summary>
    public static readonly string Error = "error";

    /// <summary>
    /// The info state. Useful for informational messages.
    /// </summary>
    public static readonly string Info = "info";

    /// <summary>
    /// The warning state. Useful for showing warnings.
    /// </summary>
    public static readonly string Warn = "warning";
}

/// <summary>
/// The set of well known resource states.
/// </summary>
public static class KnownResourceStates
{
    /// <summary>
    /// The hidden state. Useful for hiding the resource.
    /// </summary>
    [Obsolete("Use CustomResourceSnapshot.IsHidden instead.")]
    public static readonly string Hidden = nameof(Hidden);

    /// <summary>
    /// The starting state. Useful for showing the resource is starting.
    /// </summary>
    public static readonly string Starting = nameof(Starting);

    /// <summary>
    /// The running state. Useful for showing the resource is running.
    /// </summary>
    public static readonly string Running = nameof(Running);

    /// <summary>
    /// The failed to start state. Useful for showing the resource has failed to start successfully.
    /// </summary>
    public static readonly string FailedToStart = nameof(FailedToStart);

    /// <summary>
    /// The runtime unhealthy state. Indicates that a resource could not be started because the runtime is not in a healthy state.
    /// </summary>
    public static readonly string RuntimeUnhealthy = nameof(RuntimeUnhealthy);

    /// <summary>
    /// The stopping state. Useful for showing the resource is stopping.
    /// </summary>
    public static readonly string Stopping = nameof(Stopping);

    /// <summary>
    /// The exited state. Useful for showing the resource has exited.
    /// </summary>
    public static readonly string Exited = nameof(Exited);

    /// <summary>
    /// The finished state. Useful for showing the resource has finished.
    /// </summary>
    public static readonly string Finished = nameof(Finished);

    /// <summary>
    /// The waiting state. Useful for showing the resource is waiting for a dependency.
    /// </summary>
    public static readonly string Waiting = nameof(Waiting);

    /// <summary>
    /// The not started state. Useful for showing the resource was created without being started.
    /// </summary>
    public static readonly string NotStarted = nameof(NotStarted);

    /// <summary>
    /// The building state. Useful for showing the resource is being rebuilt.
    /// </summary>
    public static readonly string Building = nameof(Building);

    /// <summary>
    /// The value missing state. Useful for showing a parameter resource is waiting for a value.
    /// </summary>
    public static readonly string ValueMissing = nameof(ValueMissing);

    /// <summary>
    /// The not active state. Useful for resources without a lifetime.
    /// </summary>
    public static readonly string Active = nameof(Active);

    /// <summary>
    /// List of terminal states.
    /// </summary>
    public static readonly IReadOnlyList<string> TerminalStates = [Finished, FailedToStart, Exited];

    /// <summary>
    /// List of states in which a resource can be rebuilt.
    /// </summary>
    public static readonly IReadOnlyList<string> BuildableStates = [Running, Waiting, Finished, FailedToStart, Exited];
}

internal static class ResourceSnapshotBuilder
{
    public static ImmutableArray<RelationshipSnapshot> BuildRelationships(IResource resource)
    {
        var relationships = ImmutableArray.CreateBuilder<RelationshipSnapshot>();

        if (resource is IResourceWithParent resourceWithParent)
        {
            relationships.Add(new(resourceWithParent.Parent.Name, KnownRelationshipTypes.Parent));
        }

        foreach (var annotation in resource.Annotations.OfType<ResourceRelationshipAnnotation>())
        {
            relationships.Add(new(annotation.Resource.Name, annotation.Type));
        }

        return relationships.ToImmutable();
    }
}
