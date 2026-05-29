// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Foundry;

/// <summary>
/// Polyglot-friendly options for configuring a hosted agent.
/// </summary>
/// <remarks>
/// This DTO exposes the subset of <see cref="HostedAgentConfiguration"/> that is meaningful
/// to non-.NET app hosts. .NET callers can use the
/// <see cref="HostedAgentResourceBuilderExtensions.AsHostedAgent{T}(ApplicationModel.IResourceBuilder{T}, ApplicationModel.IResourceBuilder{AzureCognitiveServicesProjectResource}, System.Action{HostedAgentConfiguration})"/>
/// overload to access the full configuration surface (tools, content filters, container protocol versions, etc.).
/// </remarks>
[AspireExport(ExposeProperties = true)]
internal sealed class HostedAgentOptions
{
    /// <summary>
    /// The description of the hosted agent. When not set, the default from <see cref="HostedAgentConfiguration"/> is used.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// CPU allocation for each hosted agent instance, in vCPU cores. Must be between 0.5 and 3.5 in increments of 0.25.
    /// When not set, the default from <see cref="HostedAgentConfiguration"/> is used.
    /// </summary>
    public decimal? Cpu { get; set; }

    /// <summary>
    /// Memory allocation for each hosted agent instance, in GiB. Must be between 1 and 7 in increments of 0.5
    /// and equal to twice the CPU value. When not set, the default from <see cref="HostedAgentConfiguration"/> is used.
    /// </summary>
    public decimal? Memory { get; set; }

    /// <summary>
    /// Additional metadata to merge into the hosted agent definition. Existing entries with the same key are overwritten.
    /// </summary>
    public IDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Environment variables to set on the hosted agent container. Existing entries with the same key are overwritten.
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
