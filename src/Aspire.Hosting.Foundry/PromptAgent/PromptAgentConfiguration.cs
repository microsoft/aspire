// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Azure.AI.Projects;
using Azure.AI.Projects.OpenAI;
using OpenAI.Responses;

namespace Aspire.Hosting.Foundry;

/// <summary>
/// A configuration helper for prompt agents.
/// </summary>
/// <remarks>
/// This type wraps the Foundry SDK's <see cref="AgentVersionCreationOptions"/> to provide a strongly typed
/// configuration surface for prompt agent definitions. Unlike <see cref="HostedAgentConfiguration"/> which
/// configures a containerized agent, this configures a model-based agent with instructions and tools.
/// </remarks>
[AspireExport(ExposeProperties = true)]
public class PromptAgentConfiguration(string model, string? instructions)
{
    /// <summary>
    /// Gets or sets the model deployment name used by this agent.
    /// </summary>
    public string Model { get; set; } = model;

    /// <summary>
    /// Gets or sets the system instructions for the agent.
    /// </summary>
    public string? Instructions { get; set; } = instructions;

    /// <summary>
    /// Gets or sets the description of the prompt agent.
    /// </summary>
    public string Description { get; set; } = "Prompt Agent";

    /// <summary>
    /// Gets the metadata to associate with the prompt agent.
    /// </summary>
    public IDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>()
    {
        { "DeployedBy", "Aspire Hosting Framework" },
        { "DeployedOn", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) }
    };

    /// <summary>
    /// Gets the tools available to the prompt agent.
    /// </summary>
    [AspireExportIgnore(Reason = "OpenAI SDK-specific type not usable from polyglot hosts.")]
    public IList<ResponseTool> Tools { get; init; } = [];

    /// <summary>
    /// Converts this configuration to an <see cref="AgentVersionCreationOptions"/> instance.
    /// </summary>
    public AgentVersionCreationOptions ToAgentVersionCreationOptions()
    {
        var def = new PromptAgentDefinition(Model)
        {
            Instructions = Instructions
        };

        foreach (var tool in Tools)
        {
            def.Tools.Add(tool);
        }

        var options = new AgentVersionCreationOptions(def)
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
