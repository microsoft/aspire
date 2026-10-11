// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.RegularExpressions;

namespace AnalyzeCiFailure.Common;

/// <summary>
/// Formats output as GitHub Actions workflow commands.
/// See https://docs.github.com/actions/reference/workflow-commands-for-github-actions.
/// </summary>
internal static partial class WorkflowCommands
{
    public static string Error(string message) => $"::error::{message}";

    /// <summary>
    /// Renders an agent-controlled value for an error message. GitHub Actions treats every output
    /// line that starts with <c>::</c> as a command, so a raw value containing a newline could
    /// start a second command such as <c>::add-mask::</c>. Plain identifiers are shown as-is;
    /// anything else is JSON-quoted, which escapes control characters onto one physical line.
    /// </summary>
    public static string Display(string value)
        => PlainValue().IsMatch(value) ? value : JsonSerializer.Serialize(value);

    [GeneratedRegex("^[A-Za-z0-9._/:@+=,-]+$")]
    private static partial Regex PlainValue();
}
