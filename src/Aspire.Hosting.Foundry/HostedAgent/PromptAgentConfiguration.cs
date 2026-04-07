// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.AI.Projects;
using Azure.AI.Projects.OpenAI;

namespace Aspire.Hosting.Foundry;

/// <summary>
/// A configuration helper for Python hosted agents.
///
/// This is used instead of AzureAgentVersionCreationOptions to provide better static
/// typing of the agent definition.
/// </summary>
public class PromptAgentConfiguration(string model, string? instructions)
{
    /// <summary>
    /// The description of the prompt agent.
    /// </summary>
    public string Description { get; set; } = "Prompt Agent";

    /// <summary>
    /// The model of the prompt agent.
    /// </summary>
    public string Model { get; set; } = model;

    /// <summary>
    /// The "system prompt" of the prompt agent.
    /// </summary>
    public string? Instructions { get; set; } = instructions;

    /// <summary>
    /// Additional metadata to associate with the hosted agent.
    /// </summary>
    public IDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>()
    {
        { "DeployedBy", "Aspire Hosting Framework" },
        { "DeployedOn", DateTime.UtcNow.ToString("o") }
    };

    /// <summary>
    /// Converts this configuration to an <see cref="AgentVersionCreationOptions"/> instance.
    /// </summary>
    public AgentVersionCreationOptions ToAgentVersionCreationOptions()
    {
        var def = new PromptAgentDefinition(Model)
        {
            Instructions = Instructions ?? "",
        };
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
