// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents;

/// <summary>
/// Provides factory methods for creating common agent applicators that are shared across different agent environments.
/// </summary>
internal static class CommonAgentApplicators
{
    /// <summary>
    /// Creates an applicator for agent instructions file if one hasn't been added yet.
    /// </summary>
    /// <param name="context">The scan context.</param>
    /// <param name="workspaceRoot">The workspace root directory.</param>
    /// <returns>True if the applicator was added, false if it was already added.</returns>
    public static bool TryAddAgentInstructionsApplicator(AgentEnvironmentScanContext context, DirectoryInfo workspaceRoot)
    {
        if (context.AgentInstructionsApplicatorAdded)
        {
            return false;
        }

        var agentsFilePath = Path.Combine(workspaceRoot.FullName, "AGENTS.md");

        // Don't add applicator if AGENTS.md already exists
        if (File.Exists(agentsFilePath))
        {
            context.AgentInstructionsApplicatorAdded = true;
            return false;
        }

        // AGENTS.md doesn't exist, add applicator to create it
        context.AgentInstructionsApplicatorAdded = true;
        context.AddApplicator(new AgentEnvironmentApplicator(
            "Create agent instructions file (AGENTS.md)",
            ct => CreateAgentInstructionsAsync(workspaceRoot, ct),
            promptGroup: McpInitPromptGroup.AdditionalOptions,
            priority: 0));
        
        return true;
    }

    /// <summary>
    /// Tracks a detected environment and adds a single Playwright applicator if not already added.
    /// This should be called by each scanner that detects an environment supporting Playwright.
    /// </summary>
    /// <param name="context">The scan context.</param>
    /// <param name="configurationCallback">The callback to configure Playwright for this specific environment.</param>
    public static void AddPlaywrightConfigurationCallback(
        AgentEnvironmentScanContext context,
        Func<CancellationToken, Task> configurationCallback)
    {
        // Add this environment's Playwright configuration callback
        context.AddPlaywrightConfigurationCallback(configurationCallback);

        // Only add the Playwright applicator prompt once across all environments
        if (context.PlaywrightApplicatorAdded)
        {
            return;
        }

        context.PlaywrightApplicatorAdded = true;
        context.AddApplicator(new AgentEnvironmentApplicator(
            "Configure Playwright MCP server",
            async ct =>
            {
                // Execute all registered Playwright configuration callbacks
                foreach (var callback in context.PlaywrightConfigurationCallbacks)
                {
                    await callback(ct);
                }
            },
            promptGroup: McpInitPromptGroup.AdditionalOptions,
            priority: 1));
    }

    /// <summary>
    /// Creates agent instruction file (AGENTS.md).
    /// </summary>
    private static async Task CreateAgentInstructionsAsync(DirectoryInfo workspaceRoot, CancellationToken cancellationToken)
    {
        var agentsFilePath = Path.Combine(workspaceRoot.FullName, "AGENTS.md");

        // Only create the file if it doesn't already exist
        if (!File.Exists(agentsFilePath))
        {
            await File.WriteAllTextAsync(agentsFilePath, AgentsMdContent, cancellationToken);
        }
    }

    private const string AgentsMdContent =
        """
        # Copilot instructions

        This repository is set up to use Aspire. Aspire is an orchestrator for the entire application and will take care of configuring dependencies, building, and running the application. The resources that make up the application are defined in `apphost.cs` including application code and external dependencies.

        ## General recommendations for working with Aspire
        1. Before making any changes always run the apphost using `aspire run` and inspect the state of resources to make sure you are building from a known state.
        1. Changes to the _apphost.cs_ file will require a restart of the application to take effect.
        2. Make changes incrementally and run the aspire application using the `aspire run` command to validate changes.
        3. Use the Aspire MCP tools to check the status of resources and debug issues.

        ## Running the application
        To run the application run the following command:

        ```
        aspire run
        ```

        If there is already an instance of the application running it will prompt to stop the existing instance. You only need to restart the application if code in `apphost.cs` is changed, but if you experience problems it can be useful to reset everything to the starting state.

        ## Checking resources
        To check the status of resources defined in the app model use the _list resources_ tool. This will show you the current state of each resource and if there are any issues. If a resource is not running as expected you can use the _execute resource command_ tool to restart it or perform other actions.

        ## Listing integrations
        IMPORTANT! When a user asks you to add a resource to the app model you should first use the _list integrations_ tool to get a list of the current versions of all the available integrations. You should try to use the version of the integration which aligns with the version of the Aspire.AppHost.Sdk. Some integration versions may have a preview suffix. Once you have identified the correct integration you should always use the _get integration docs_ tool to fetch the latest documentation for the integration and follow the links to get additional guidance.

        ## Debugging issues
        IMPORTANT! Aspire is designed to capture rich logs and telemetry for all resources defined in the app model. Use the following diagnostic tools when debugging issues with the application before making changes to make sure you are focusing on the right things.

        1. _list structured logs_; use this tool to get details about structured logs.
        2. _list console logs_; use this tool to get details about console logs.
        3. _list traces_; use this tool to get details about traces.
        4. _list trace structured logs_; use this tool to get logs related to a trace

        ## Other Aspire MCP tools

        1. _select apphost_; use this tool if working with multiple app hosts within a workspace.
        2. _list apphosts_; use this tool to get details about active app hosts.

        ## Playwright MCP server

        The playwright MCP server has also been configured in this repository and you should use it to perform functional investigations of the resources defined in the app model as you work on the codebase. To get endpoints that can be used for navigation using the playwright MCP server use the list resources tool.

        ## Updating the app host
        The user may request that you update the Aspire apphost. You can do this using the `aspire update` command. This will update the apphost to the latest version and some of the Aspire specific packages in referenced projects, however you may need to manually update other packages in the solution to ensure compatibility. You can consider using the `dotnet-outdated` with the users consent. To install the `dotnet-outdated` tool use the following command:

        ```
        dotnet tool install --global dotnet-outdated-tool
        ```

        ## Persistent containers
        IMPORTANT! Consider avoiding persistent containers early during development to avoid creating state management issues when restarting the app.

        ## Aspire workload
        IMPORTANT! The aspire workload is obsolete. You should never attempt to install or use the Aspire workload.

        ## Official documentation
        IMPORTANT! Always prefer official documentation when available. The following sites contain the official documentation for Aspire and related components

        1. https://aspire.dev
        2. https://learn.microsoft.com/dotnet/aspire
        3. https://nuget.org (for specific integration package details)
        """;
}
