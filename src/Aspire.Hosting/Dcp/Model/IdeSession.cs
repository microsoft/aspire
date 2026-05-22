// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;

namespace Aspire.Hosting.Dcp.Model;

/// <summary>
/// Represents the desired and observed state of an on-demand IDE debug session.
/// Created by Aspire at app startup in "Initial" state; transitions to "Starting"
/// when the user requests a debug session. DCP reconciles by calling the IDE endpoint
/// and updating the status to "Running" or "Failed".
/// </summary>
internal sealed class IdeSession : CustomResource<IdeSessionSpec, IdeSessionStatus>, IKubernetesStaticMetadata
{
    [JsonConstructor]
    public IdeSession(IdeSessionSpec spec) : base(spec) { }

    public static IdeSession Create(string name, IdeSessionSpec spec)
    {
        var session = new IdeSession(spec);
        session.Kind = Dcp.IdeSessionKind;
        session.ApiVersion = Dcp.GroupVersion.ToString();
        session.Metadata.Name = name;
        session.Metadata.NamespaceProperty = string.Empty;
        return session;
    }

    public static string ObjectKind => Dcp.IdeSessionKind;
}

internal sealed class IdeSessionSpec
{
    /// <summary>
    /// Launch configurations for the debug session. Typically contains a single
    /// <c>browser-debug</c> launch configuration with the client project path and app URL.
    /// </summary>
    [JsonPropertyName("launch_configurations")]
    public List<ExecutableLaunchConfiguration> LaunchConfigurations { get; set; } = [];

    /// <summary>
    /// Desired session state. Aspire sets this to <see cref="IdeSessionState.Running"/>
    /// to request DCP to start the session via the IDE protocol.
    /// </summary>
    [JsonPropertyName("desired_state")]
    public string DesiredState { get; set; } = IdeSessionState.Initial;
}

internal sealed class IdeSessionStatus
{
    /// <summary>
    /// The current observed state of the IDE session as reported by DCP.
    /// </summary>
    [JsonPropertyName("state")]
    public string? State { get; set; }

    /// <summary>
    /// Human-readable message providing additional context about the session state
    /// (e.g., error details when state is "Failed").
    /// </summary>
    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

/// <summary>
/// Well-known states for IdeSession resources.
/// </summary>
internal static class IdeSessionState
{
    /// <summary>Session created but not yet requested to start.</summary>
    public const string Initial = "Initial";

    /// <summary>Start requested; DCP is contacting the IDE.</summary>
    public const string Starting = "Starting";

    /// <summary>IDE confirmed the debug session is active.</summary>
    public const string Running = "Running";

    /// <summary>Session was stopped (by user or IDE disconnect).</summary>
    public const string Stopped = "Stopped";

    /// <summary>Session failed to start (IDE rejected or timeout).</summary>
    public const string Failed = "Failed";
}
