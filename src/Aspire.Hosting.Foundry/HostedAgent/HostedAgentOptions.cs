// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Foundry;

// HostedAgentOptions exposes the subset of HostedAgentConfiguration that is meaningful to non-.NET
// app hosts. .NET callers should use the AsHostedAgent overload that takes Action<HostedAgentConfiguration>
// to access the full configuration surface (tools, content filters, container protocol versions, etc.).

/// <summary>
/// Options that control how a compute resource is deployed as a Microsoft Foundry hosted agent.
/// All properties are optional; unset properties fall back to the Foundry hosted agent defaults.
/// </summary>
[AspireExport(ExposeProperties = true)]
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
    }
}
