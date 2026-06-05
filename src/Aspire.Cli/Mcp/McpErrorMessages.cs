// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Mcp;

/// <summary>
/// Provides common error messages used by MCP tools.
/// </summary>
internal static class McpErrorMessages
{
    /// <summary>
    /// Error message when no Aspire AppHost is currently running.
    /// </summary>
    public const string NoAppHostRunning =
        "No Aspire AppHost is currently running. " +
        "To use Aspire MCP tools, you must first start an Aspire application by running 'aspire start' in your AppHost project directory. " +
        "Once the application is running, the MCP tools will be able to connect to the dashboard and execute commands.";

    /// <summary>
    /// Error message when the dashboard is not available in the running AppHost.
    /// </summary>
    public const string DashboardNotAvailable =
        "The Aspire Dashboard is not available in the running AppHost. " +
        "The dashboard must be enabled to use MCP tools. " +
        "Ensure your AppHost is configured with the dashboard enabled (this is the default configuration).";
}
