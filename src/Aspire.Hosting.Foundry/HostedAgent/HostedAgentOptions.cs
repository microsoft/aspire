// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.AI.Projects.Agents;

namespace Aspire.Hosting.Foundry;

// HostedAgentOptions exposes the subset of HostedAgentConfiguration that can be shared by .NET and
// polyglot app hosts. .NET callers can use the AsHostedAgent overload that takes
// Action<HostedAgentConfiguration> when they need the full Azure SDK-specific configuration surface.

/// <summary>
/// Options that control how a compute resource is deployed as a Microsoft Foundry hosted agent.
/// All properties are optional; unset properties fall back to the Foundry hosted agent defaults.
/// </summary>
[AspireDto]
internal sealed class HostedAgentOptions
{
    /// <summary>
    /// Human-readable description of the hosted agent surfaced in the Microsoft Foundry portal.
    /// When not set, the hosted agent default description is used.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// CPU allocation for each hosted agent instance, in vCPU cores. Must be between 0.5 and 3.5
    /// in increments of 0.25. When not set, the hosted agent default CPU allocation is used.
    /// </summary>
    public decimal? Cpu { get; set; }

    /// <summary>
    /// Memory allocation for each hosted agent instance, in GiB. Must be between 1 and 7 in
    /// increments of 0.5 and equal to twice the CPU value. When not set, the hosted agent
    /// default memory allocation is used.
    /// </summary>
    public decimal? Memory { get; set; }

    /// <summary>
    /// Additional metadata key/value pairs to attach to the hosted agent definition.
    /// Entries with the same key as an existing metadata entry overwrite it.
    /// </summary>
    public IDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Environment variables to set on the hosted agent container at runtime.
    /// Entries with the same key as an existing environment variable overwrite it.
    /// </summary>
    public IDictionary<string, string> EnvironmentVariables { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Protocol versions that the hosted agent container supports for ingress communication.
    /// When not set, the hosted agent default responses protocol is used.
    /// </summary>
    /// <remarks>
    /// In run mode, the first protocol entry selects the dashboard URL and HTTP command protocol.
    /// In publish mode, all entries are emitted to the Foundry hosted agent definition.
    /// </remarks>
    public IList<HostedAgentProtocolVersion> Protocols { get; init; } = [];

    internal void ApplyTo(HostedAgentConfiguration configuration)
    {
        if (Description is not null)
        {
            configuration.Description = Description;
        }

        // Cpu and Memory have a coupled invariant on HostedAgentConfiguration (Memory = Cpu * 2 with validation).
        // Apply Cpu first so a subsequent Memory assignment can still override the derived value.
        if (Cpu is { } cpu)
        {
            configuration.Cpu = cpu;
        }

        if (Memory is { } memory)
        {
            configuration.Memory = memory;
        }

        foreach (var kvp in Metadata)
        {
            configuration.Metadata[kvp.Key] = kvp.Value;
        }

        foreach (var kvp in EnvironmentVariables)
        {
            configuration.EnvironmentVariables[kvp.Key] = kvp.Value;
        }

        var protocols = ValidateProtocols();
        if (protocols.Count > 0)
        {
            var protocolVersionRecords = protocols.Select(ToProtocolVersionRecord).ToArray();

            configuration.ContainerProtocolVersions.Clear();
            foreach (var record in protocolVersionRecords)
            {
                configuration.ContainerProtocolVersions.Add(record);
            }
        }
    }

    private IList<HostedAgentProtocolVersion> ValidateProtocols()
    {
        if (Protocols is null)
        {
            throw new ArgumentNullException(nameof(Protocols), "Hosted agent protocols cannot be null.");
        }

        foreach (var protocol in Protocols)
        {
            ValidateProtocol(protocol);
        }

        return Protocols;
    }

    private static void ValidateProtocol(HostedAgentProtocolVersion protocolVersion)
    {
        if (protocolVersion is null)
        {
            throw new ArgumentNullException(nameof(protocolVersion), "Hosted agent protocols cannot contain null entries.");
        }

        if (string.IsNullOrWhiteSpace(protocolVersion.Protocol))
        {
            ThrowInvalidProtocolProperty(nameof(HostedAgentProtocolVersion.Protocol), "Hosted agent protocol cannot be null, empty, or whitespace.");
        }

        if (string.IsNullOrWhiteSpace(protocolVersion.Version))
        {
            ThrowInvalidProtocolProperty(nameof(HostedAgentProtocolVersion.Version), "Hosted agent protocol version cannot be null, empty, or whitespace.");
        }
    }

    private static void ThrowInvalidProtocolProperty(string propertyName, string message)
    {
        throw new ArgumentException(message, propertyName);
    }

    private static ProtocolVersionRecord ToProtocolVersionRecord(HostedAgentProtocolVersion protocolVersion)
    {
        return new ProtocolVersionRecord(new ProjectsAgentProtocol(protocolVersion.Protocol), protocolVersion.Version);
    }
}

/// <summary>
/// A protocol and version supported by a Microsoft Foundry hosted agent container.
/// </summary>
[AspireDto]
internal sealed class HostedAgentProtocolVersion
{
    /// <summary>
    /// The protocol name, such as <c>responses</c> or <c>invocations</c>.
    /// </summary>
    public required string Protocol { get; init; }

    /// <summary>
    /// The protocol version, such as <c>1.0.0</c>.
    /// </summary>
    public required string Version { get; init; }
}
