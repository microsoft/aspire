// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Azure.AI.Projects.Agents;

namespace Aspire.Hosting.Foundry;

/// <summary>
/// A configuration helper for hosted agents.
/// </summary>
/// <remarks>
/// This type is used instead of <see cref="ProjectsAgentVersionCreationOptions"/> to provide a strongly typed
/// configuration surface for hosted agent definitions. When used from polyglot app hosts, only the
/// ATS-compatible properties are exported; Azure SDK-specific members remain .NET-only.
/// </remarks>
[AspireExport(ExposeProperties = true)]
public class HostedAgentConfiguration(string image)
{
    /// <summary>
    /// The description of the hosted agent.
    /// </summary>
    public string Description { get; set; } = "Hosted Agent";

    /// <summary>
    /// Additional metadata to associate with the hosted agent.
    /// </summary>
    public IDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>()
    {
        { "DeployedBy", "Aspire Hosting Framework" },
        { "DeployedOn", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) }
    };

    /// <summary>
    /// Configuration for Responsible AI (RAI) content filtering and safety features.
    /// </summary>
    [AspireExportIgnore(Reason = "Azure SDK-specific type not usable from polyglot hosts.")]
    public ContentFilterConfiguration? ContentFilterConfiguration { get; set; }

    /// <summary>
    /// Tools available to the hosted agent.
    /// </summary>
    [AspireExportIgnore(Reason = "Azure SDK-specific type not usable from polyglot hosts.")]
    public IList<ProjectsAgentTool> Tools { get; init; } = [];

    /// <summary>
    /// The protocols that the agent supports for ingress communication of the containers.
    /// </summary>
    [AspireExportIgnore(Reason = "Azure SDK-specific type not usable from polyglot hosts.")]
    public IList<ProtocolVersionRecord> ContainerProtocolVersions { get; init; } = [
        new ProtocolVersionRecord(ProjectsAgentProtocol.Responses, "v1")
    ];

    private decimal _cpu = 2.0m;

    /// <summary>
    /// CPU allocation for each hosted agent instance, in vCPU cores.
    /// </summary>
    public decimal Cpu
    {
        get => _cpu;
        set
        {
            if (value < 0.5m || value > 3.5m || value % 0.25m != 0)
            {
                throw new ArgumentException("CPU must be between 0.5 and 3.5 in increments of 0.25 vCPU.", nameof(Cpu));
            }
            _cpu = value;
        }
    }

    /// <summary>
    /// CPU allocation as a string.
    /// </summary>
    [AspireExportIgnore(Reason = "Derived formatting property used internally by Azure provisioning.")]
    public string CpuString { get => _cpu.ToString(CultureInfo.InvariantCulture); }

    /// <summary>
    /// Memory allocation for each hosted agent instance, in GiB.
    /// Must be 2x the CPU allocation.
    /// </summary>
    public decimal Memory
    {
        get => _cpu * 2;
        set
        {
            if (value < 1m || value > 7m || value % 0.5m != 0)
            {
                throw new ArgumentException("Memory must be between 1 and 7 in increments of 0.5 Gi.", nameof(Memory));
            }
            _cpu = value / 2;
        }
    }

    /// <summary>
    /// Memory allocation as a string.
    /// </summary>
    [AspireExportIgnore(Reason = "Derived formatting property used internally by Azure provisioning.")]
    public string MemoryString { get => Memory.ToString(CultureInfo.InvariantCulture) + "Gi"; }

    /// <summary>
    /// Environment variables to set in the hosted agent container.
    /// </summary>
    public IDictionary<string, string> EnvironmentVariables { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// The fully qualified container image name for the hosted agent.
    /// </summary>
    [AspireExportIgnore(Reason = "Image comes from the executable resource and is not configured from polyglot hosted agent callbacks.")]
    public string Image { get; set; } = image;

    /// <summary>
    /// Converts this configuration to an <see cref="ProjectsAgentVersionCreationOptions"/> instance.
    /// </summary>
    public ProjectsAgentVersionCreationOptions ToProjectsAgentVersionCreationOptions()
    {
        var def = new HostedAgentDefinition(
            ContainerProtocolVersions,
            cpu: CpuString,
            memory: MemoryString)
        {
            Image = Image
        };
        if (ContentFilterConfiguration is not null)
        {
            def.ContentFilterConfiguration = ContentFilterConfiguration;
        }
        foreach (var tool in Tools)
        {
            def.Tools.Add(tool);
        }
        foreach (var envVar in EnvironmentVariables)
        {
            def.EnvironmentVariables[envVar.Key] = envVar.Value;
        }
        var options = new ProjectsAgentVersionCreationOptions(def)
        {
            Description = Description,
        };
        foreach (var kvp in Metadata)
        {
            options.Metadata[kvp.Key] = kvp.Value;
        }
        return options;
    }
}
