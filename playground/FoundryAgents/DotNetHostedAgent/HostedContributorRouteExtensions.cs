// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Agents.AI.Foundry.Hosting;

/// <summary>
/// Routing helpers for contributor samples that host a Foundry-managed agent locally.
/// </summary>
public static class HostedContributorRouteExtensions
{
    /// <summary>
    /// In Development, maps the per-agent OpenAI route shape that live Foundry uses on top of the
    /// default <c>MapFoundryResponses()</c> so a local REPL client can reach the agent through
    /// <c>AIProjectClient.AsAIAgent(Uri agentEndpoint)</c>.
    /// </summary>
    /// <param name="app">The <see cref="WebApplication"/> to attach the routes to.</param>
    /// <returns>The same <see cref="WebApplication"/> for chaining.</returns>
    public static WebApplication MapDevTemporaryLocalAgentEndpoint(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (app.Environment.IsDevelopment())
        {
            app.MapFoundryResponses("api/projects/{project}/agents/{agentName}/endpoint/protocols/openai");
        }

        return app;
    }
}