// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using Aspire.Cli.Agents;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;

namespace Aspire.Cli.Commands;

/// <summary>
/// Hidden, machine-facing command invoked by the agent telemetry hook scripts
/// (<c>track-telemetry.sh</c> / <c>track-telemetry.ps1</c>) on each agent <c>PostToolUse</c>
/// event. It records a single reported activity describing the Aspire asset, MCP tool, or
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
    // high-cardinality values into the telemetry backend. Real values (asset names, tool names,
    // skills-relative reference paths) are well under this length.
    private const int MaxTagValueLength = 256;

    // The only event types the hook scripts emit. Anything else is dropped so a script bug or a
    // crafted argument cannot introduce arbitrary, high-cardinality event categories.
    private const string AssetInvocationEventType = "asset_invocation";
    private const string AssetInteractionEventType = "asset_interaction";
    private const string LegacySkillInvocationEventType = "skill_invocation";

    private static readonly string[] s_knownEventTypes = [AssetInvocationEventType, AssetInteractionEventType, "tool_invocation", "reference_file_read"];
    private static readonly string[] s_knownInteractionTypes = ["canvas_lifecycle", "canvas_action", "workflow"];
    private static readonly string[] s_knownInteractionOutcomes = ["success", "failure", "validation_failed", "timeout"];

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

    private readonly Option<string?> _assetKindOption = new("--asset-kind")
    {
        Description = AgentCommandStrings.AgentTelemetryCommand_AssetKindDescription
    };

    private readonly Option<string?> _assetNameOption = new("--asset-name")
    {
        Description = AgentCommandStrings.AgentTelemetryCommand_AssetNameDescription
    };

    private readonly Option<string?> _interactionTypeOption = new("--interaction-type");

    private readonly Option<string?> _interactionNameOption = new("--interaction-name");

    private readonly Option<string?> _interactionOutcomeOption = new("--outcome");

    private readonly Option<string?> _interactionDurationOption = new("--duration-ms");

    // Compatibility for hook scripts materialized by an older CLI and still registered in an
    // agent configuration after the CLI itself has been updated.
    private readonly Option<string?> _legacySkillNameOption = new("--skill-name");

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
        Options.Add(_assetKindOption);
        Options.Add(_assetNameOption);
        Options.Add(_interactionTypeOption);
        Options.Add(_interactionNameOption);
        Options.Add(_interactionOutcomeOption);
        Options.Add(_interactionDurationOption);
        Options.Add(_legacySkillNameOption);
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
            var tags = CollectValidTags(parseResult, out var interactionDuration);

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

                if (interactionDuration is not null)
                {
                    activity.SetTag(TelemetryConstants.Tags.AgentInteractionDurationMilliseconds, interactionDuration.Value);
                }
            }
        }
        catch
        {
            // Telemetry must never break the calling agent's hook. Swallow everything and exit 0.
        }

        return Task.FromResult(CommandResult.Success());
    }

    private List<(string Name, string Value)> CollectValidTags(ParseResult parseResult, out long? interactionDuration)
    {
        var tags = new List<(string Name, string Value)>();
        interactionDuration = null;

        var eventType = parseResult.GetValue(_eventTypeOption);
        var legacySkillName = parseResult.GetValue(_legacySkillNameOption);
        if (string.Equals(eventType, LegacySkillInvocationEventType, StringComparison.Ordinal))
        {
            eventType = AssetInvocationEventType;
        }

        AddIfValid(tags, TelemetryConstants.Tags.AgentEventType, eventType, static v => s_knownEventTypes.Contains(v, StringComparer.Ordinal));
        AddIfValid(tags, TelemetryConstants.Tags.AgentClientName, parseResult.GetValue(_clientNameOption), static v => IsSafeIdentifier(v, maxLength: 64));
        AddIfValid(tags, TelemetryConstants.Tags.AgentSessionId, parseResult.GetValue(_sessionIdOption), static v => IsSafeIdentifier(v, maxLength: 128));
        var assetKindValue = parseResult.GetValue(_assetKindOption) ?? (legacySkillName is not null ? nameof(AgentAssetKind.Skill) : null);
        if (Enum.TryParse<AgentAssetKind>(assetKindValue, ignoreCase: true, out var assetKind) &&
            assetKind is AgentAssetKind.Skill or AgentAssetKind.Extension)
        {
            tags.Add((TelemetryConstants.Tags.AgentAssetKind, assetKind.ToString().ToLowerInvariant()));
        }

        AddIfValid(tags, TelemetryConstants.Tags.AgentAssetName, parseResult.GetValue(_assetNameOption) ?? legacySkillName, static v => IsSafeIdentifier(v, maxLength: 128));
        AddIfValid(tags, TelemetryConstants.Tags.AgentInteractionType, parseResult.GetValue(_interactionTypeOption), static v => s_knownInteractionTypes.Contains(v, StringComparer.Ordinal));
        AddIfValid(tags, TelemetryConstants.Tags.AgentInteractionName, parseResult.GetValue(_interactionNameOption), static v => IsSafeIdentifier(v, maxLength: 128));
        AddIfValid(tags, TelemetryConstants.Tags.AgentInteractionOutcome, parseResult.GetValue(_interactionOutcomeOption), static v => s_knownInteractionOutcomes.Contains(v, StringComparer.Ordinal));
        if (long.TryParse(parseResult.GetValue(_interactionDurationOption), NumberStyles.None, CultureInfo.InvariantCulture, out var parsedInteractionDuration) &&
            parsedInteractionDuration >= 0)
        {
            interactionDuration = parsedInteractionDuration;
        }

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
