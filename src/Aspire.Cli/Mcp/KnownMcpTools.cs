// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Mcp;

/// <summary>
/// Defines the names of MCP tools exposed by the Aspire CLI.
/// </summary>
// Tool names like "doctor" are also used as CLI subcommand names elsewhere; scope
// this catalog to MCP code so the analyzer doesn't suggest cross-domain matches.
[InternalKnownConstants(Namespaces = new[] { "Aspire.Cli.Mcp" })]
internal static class KnownMcpTools
{
    internal const string ListResources = "list_resources";
    internal const string ListConsoleLogs = "list_console_logs";
    internal const string ExecuteResourceCommand = "execute_resource_command";
    internal const string ListStructuredLogs = "list_structured_logs";
    internal const string ListTraces = "list_traces";
    internal const string ListTraceStructuredLogs = "list_trace_structured_logs";
    internal const string SelectAppHost = "select_apphost";
    internal const string ListAppHosts = "list_apphosts";
    internal const string ListIntegrations = "list_integrations";
    internal const string Doctor = "doctor";
    internal const string RefreshTools = "refresh_tools";
    internal const string ListDocs = "list_docs";
    internal const string SearchDocs = "search_docs";
    internal const string GetDoc = "get_doc";

    /// <summary>
    /// Gets all known MCP tool names.
    /// </summary>
    public static IReadOnlyList<string> All { get; } =
    [
        ListResources,
        ListConsoleLogs,
        ExecuteResourceCommand,
        ListStructuredLogs,
        ListTraces,
        ListTraceStructuredLogs,
        SelectAppHost,
        ListAppHosts,
        ListIntegrations,
        Doctor,
        RefreshTools,
        ListDocs,
        SearchDocs,
        GetDoc
    ];

}
