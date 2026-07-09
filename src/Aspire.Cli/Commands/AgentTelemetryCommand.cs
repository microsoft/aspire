// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;

namespace Aspire.Cli.Commands;

/// <summary>
/// Hidden, machine-facing command invoked by the agent telemetry hook scripts
/// (<c>track-telemetry.sh</c> / <c>track-telemetry.ps1</c>) on each agent <c>PostToolUse</c>
/// event. It records a single reported activity describing the Aspire skill, MCP tool, or
/// reference-file usage that the hook detected.
/// </summary>
/// <remarks>
/// Hook-safety contract: this command must never throw and must always exit 0. A hook that fails
/// or writes unexpected output can break the host agent's tool loop, so every operation is wrapped
/// so that any failure degrades to a successful no-op. All options are optional and unvalidated,
/// and unmatched tokens are ignored, so option binding can never fail before the handler runs and a
/// newer hook script passing an unknown flag cannot break an older CLI.
///
/// The opt-out (<c>ASPIRE_CLI_TELEMETRY_OPTOUT</c>) and the suppression of the generic
/// <c>aspire/cli/main</c> span for this command path are handled in
/// <see cref="TelemetryManager"/> and <c>Program</c> before the host is built. When telemetry is
/// opted out no reported provider is created, so <see cref="AspireCliTelemetry.StartReportedActivity(string, System.Diagnostics.ActivityKind)"/>
/// returns <see langword="null"/> here and the command is a no-op.
/// </remarks>
internal sealed class AgentTelemetryCommand : BaseCommand
{
    // Defensive cap so a malformed or hostile hook payload cannot push oversized or
    // high-cardinality values into the telemetry backend. Real values (skill names, tool names,
    // skills-relative reference paths) are well under this length.
    private const int MaxTagValueLength = 256;

    // The only event types the hook scripts emit. Anything else is dropped so a script bug or a
    // crafted argument cannot introduce arbitrary, high-cardinality event categories.
    private static readonly string[] s_knownEventTypes = ["skill_invocation", "tool_invocation", "reference_file_read"];

    private readonly Option<string?> _eventTypeOption = new("--event-type")
    {
        Description = AgentCommandStrings.AgentTelemetryCommand_EventTypeDescription
    };

    private readonly Option<string?> _clientNameOption = new("--client-name")
    {
        Description = AgentCommandStrings.AgentTelemetryCommand_ClientNameDescription
    };

    private readonly Option<string?> _sessionIdOption = new("--session-id")
    {
        Description = AgentCommandStrings.AgentTelemetryCommand_SessionIdDescription
    };

    private readonly Option<string?> _skillNameOption = new("--skill-name")
    {
        Description = AgentCommandStrings.AgentTelemetryCommand_SkillNameDescription
    };

    private readonly Option<string?> _toolNameOption = new("--tool-name")
    {
        Description = AgentCommandStrings.AgentTelemetryCommand_ToolNameDescription
    };

    private readonly Option<string?> _fileReferenceOption = new("--file-reference")
    {
        Description = AgentCommandStrings.AgentTelemetryCommand_FileReferenceDescription
    };

    private readonly Option<string?> _timestampOption = new("--timestamp")
    {
        Description = AgentCommandStrings.AgentTelemetryCommand_TimestampDescription
    };

    public AgentTelemetryCommand(CommonCommandServices services)
        : base("telemetry", AgentCommandStrings.AgentTelemetryCommand_Description, services)
    {
        // This command is an implementation detail of the agent hook scripts, not a user-facing
        // command, so keep it out of help output.
        Hidden = true;

        // Never fail the hook because a newer script passes a flag this CLI version does not know.
        TreatUnmatchedTokensAsErrors = false;

        Options.Add(_eventTypeOption);
        Options.Add(_clientNameOption);
        Options.Add(_sessionIdOption);
        Options.Add(_skillNameOption);
        Options.Add(_toolNameOption);
        Options.Add(_fileReferenceOption);
        Options.Add(_timestampOption);
    }

    protected override Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        try
        {
            // Validate every value up front. Invalid or oversized values are dropped (never recorded),
            // so a parser bug in a hook script cannot leak an absolute path, user name, or other
            // sensitive/high-cardinality data into telemetry.
            var tags = CollectValidTags(parseResult);

            // Nothing valid survived validation (for example a newer hook script paired with an older
            // CLI dropped every field): emit no span rather than a tagless one.
            if (tags.Count is 0)
            {
                return Task.FromResult(CommandResult.Success());
            }

            // Activity is null when telemetry is opted out (no reported provider) or no listener is
            // attached; in that case this is a no-op, which is the desired behavior.
            using var activity = Telemetry.StartReportedActivity(TelemetryConstants.Activities.AgentTelemetry);
            if (activity is not null)
            {
                foreach (var (name, value) in tags)
                {
                    activity.SetTag(name, value);
                }
            }
        }
        catch
        {
            // Telemetry must never break the calling agent's hook. Swallow everything and exit 0.
        }

        return Task.FromResult(CommandResult.Success());
    }

    private List<(string Name, string Value)> CollectValidTags(ParseResult parseResult)
    {
        var tags = new List<(string Name, string Value)>();

        AddIfValid(tags, TelemetryConstants.Tags.AgentEventType, parseResult.GetValue(_eventTypeOption), static v => s_knownEventTypes.Contains(v, StringComparer.Ordinal));
        AddIfValid(tags, TelemetryConstants.Tags.AgentClientName, parseResult.GetValue(_clientNameOption), static v => IsSafeIdentifier(v, maxLength: 64));
        AddIfValid(tags, TelemetryConstants.Tags.AgentSessionId, parseResult.GetValue(_sessionIdOption), static v => IsSafeIdentifier(v, maxLength: 128));
        AddIfValid(tags, TelemetryConstants.Tags.AgentSkillName, parseResult.GetValue(_skillNameOption), static v => IsSafeIdentifier(v, maxLength: 128));
        AddIfValid(tags, TelemetryConstants.Tags.AgentToolName, parseResult.GetValue(_toolNameOption), static v => IsSafeIdentifier(v, maxLength: 128));
        AddIfValid(tags, TelemetryConstants.Tags.AgentFileReference, parseResult.GetValue(_fileReferenceOption), IsSafeReference);
        AddIfValid(tags, TelemetryConstants.Tags.AgentEventTimestamp, parseResult.GetValue(_timestampOption), IsValidTimestamp);

        return tags;
    }

    private static void AddIfValid(List<(string Name, string Value)> tags, string name, string? value, Func<string, bool> isValid)
    {
        if (!string.IsNullOrWhiteSpace(value) && isValid(value))
        {
            tags.Add((name, value));
        }
    }

    /// <summary>
    /// Validates an opaque identifier/name value: a bounded length and a conservative ASCII charset
    /// (letters, digits, '-', '_', '.'). This rejects whitespace, path separators, and other
    /// characters that would indicate the value is not an Aspire-owned identifier.
    /// </summary>
    private static bool IsSafeIdentifier(string value, int maxLength)
    {
        if (value.Length > maxLength)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Validates a skills-relative reference path. Only forward-slash relative paths within the
    /// Aspire skills tree are recorded; absolute paths, drive letters, UNC paths, parent traversal,
    /// home (<c>~</c>) references, and backslashes are rejected so no machine-specific or
    /// user-identifying path can be captured.
    /// </summary>
    private static bool IsSafeReference(string value)
    {
        if (value.Length > MaxTagValueLength ||
            value.StartsWith('/') ||
            value.StartsWith('~') ||
            value.Contains('\\') ||
            value.Contains("..", StringComparison.Ordinal) ||
            Path.IsPathRooted(value))
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.' or '/'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Validates that the timestamp value parses as a round-trippable date/time so a free-form string
    /// cannot be recorded under the timestamp tag.
    /// </summary>
    private static bool IsValidTimestamp(string value)
        => value.Length <= MaxTagValueLength &&
           DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _);
}
