// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents.Hooks;

/// <summary>
/// Defines the command arguments and event types shared by the agent telemetry hook and command.
/// </summary>
internal static class AgentTelemetryProtocol
{
    internal const string AgentCommandName = "agent";
    internal const string TelemetryCommandName = "telemetry";

    internal const string HookOptionName = "--hook";
    internal const string DrainOptionName = "--drain";
    internal const string SourceOptionName = "--source";
    internal const string ScopeOptionName = "--scope";
    internal const string EventTypeOptionName = "--event-type";
    internal const string ClientNameOptionName = "--client-name";
    internal const string SessionIdOptionName = "--session-id";
    internal const string SkillNameOptionName = "--skill-name";
    internal const string ToolNameOptionName = "--tool-name";
    internal const string FileReferenceOptionName = "--file-reference";
    internal const string TimestampOptionName = "--timestamp";

    internal const string SkillInvocationEventType = "skill_invocation";
    internal const string ToolInvocationEventType = "tool_invocation";
    internal const string ReferenceFileReadEventType = "reference_file_read";

    // Values for --source: the hook configuration format that invoked the hook.
    internal const string CopilotSource = "copilot";
    internal const string ClaudeSource = "claude";

    // Values for --scope: where the hook configuration was installed.
    internal const string UserScope = "user";
    internal const string ProjectScope = "project";

    /// <summary>
    /// The Copilot hook file name used at both user scope (<c>~/.copilot/hooks</c>) and project scope (<c>.github/hooks</c>).
    /// </summary>
    internal const string CopilotHookFileName = "aspire-telemetry.json";
}
