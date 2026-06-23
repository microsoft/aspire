// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Text;

namespace Aspire.TerminalHost;

/// <summary>
/// Parsed command-line arguments for the Aspire terminal host.
/// </summary>
/// <remarks>
/// <para>
/// Each <c>aspire.terminalhost</c> process serves exactly <strong>one</strong> replica's
/// terminal session. The "which replica is this?" question is intentionally opaque to the
/// host: the AppHost encodes the replica identity in the UDS paths it passes in (typically
/// as a per-replica directory like <c>{base}/{i}/dcp.sock</c>) and the host just listens
/// on whatever paths it's told. If a target resource has <c>N</c> replicas, the AppHost
/// spawns <c>N</c> independent terminal host processes, each with its own
/// producer/consumer/control UDS triple.
/// </para>
/// <para>
/// Connection direction note: on the producer side the terminal host <strong>listens</strong>
/// and DCP <strong>dials</strong>. On the consumer side the terminal host <strong>listens</strong>
/// and viewers (Dashboard, CLI) <strong>dial</strong>. Same shape on both ends.
/// </para>
/// </remarks>
internal sealed class TerminalHostArgs
{
    public required string ProducerUdsPath { get; init; }
    public required string ConsumerUdsPath { get; init; }
    public required string ControlUdsPath { get; init; }
    public int Columns { get; init; } = 120;
    public int Rows { get; init; } = 30;

    /// <summary>
    /// Optional shell name. Informational only (the host does not spawn a PTY itself —
    /// that is DCP's responsibility); included so the host can log it on startup.
    /// </summary>
    public string? Shell { get; init; }

    /// <summary>
    /// Parses command-line arguments. The argument shape is:
    /// <list type="bullet">
    ///   <item><c>--producer-uds PATH</c> (required) — path the host LISTENS on; DCP dials.</item>
    ///   <item><c>--consumer-uds PATH</c> (required) — path the host LISTENS on; viewers dial.</item>
    ///   <item><c>--control-uds PATH</c> (required) — path the host LISTENS on; AppHost dials for status/shutdown RPC.</item>
    ///   <item><c>--columns N</c> (optional, default 120)</item>
    ///   <item><c>--rows N</c> (optional, default 30)</item>
    ///   <item><c>--shell NAME</c> (optional, informational)</item>
    /// </list>
    /// Every option is single-valued and may only be specified once; duplicates throw
    /// <see cref="TerminalHostArgsException"/>. We use <c>System.CommandLine</c> so the
    /// host inherits the same parsing model as the rest of the Aspire CLI (consistent
    /// error messages, --help output, etc.).
    /// </summary>
    public static TerminalHostArgs Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var producerOption = SingleValueOption<string>("--producer-uds", required: true,
            "Path the terminal host LISTENS on for the DCP-driven PTY producer stream.");
        var consumerOption = SingleValueOption<string>("--consumer-uds", required: true,
            "Path the terminal host LISTENS on for viewer (Dashboard / CLI) consumer connections.");
        var controlOption = SingleValueOption<string>("--control-uds", required: true,
            "Path the terminal host LISTENS on for the AppHost control RPC channel.");

        var columnsOption = SingleValueOption<int>("--columns", required: false,
            "Initial PTY width in columns (default 120).", defaultValue: 120);
        var rowsOption = SingleValueOption<int>("--rows", required: false,
            "Initial PTY height in rows (default 30).", defaultValue: 30);
        var shellOption = SingleValueOption<string?>("--shell", required: false,
            "Informational shell name, logged on startup.");

        // Cols / rows must be positive. System.CommandLine has no built-in range validator
        // for ints, so attach one explicitly. Skip when the value couldn't be converted at
        // all (e.g. --columns abc) - that error is already surfaced through
        // ParseResult.Errors and calling GetValueOrDefault would rethrow it.
        columnsOption.Validators.Add(r =>
        {
            if (r.Tokens.Count > 0
                && int.TryParse(r.Tokens[0].Value, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var value)
                && value < 1)
            {
                r.AddError("--columns must be >= 1.");
            }
        });
        rowsOption.Validators.Add(r =>
        {
            if (r.Tokens.Count > 0
                && int.TryParse(r.Tokens[0].Value, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var value)
                && value < 1)
            {
                r.AddError("--rows must be >= 1.");
            }
        });

        // Treat this as a leaf command - no subcommands. The terminal host doesn't ship
        // help text to end users (it is spawned by DCP, not invoked directly), but using
        // RootCommand keeps the parsing model consistent with the rest of the CLI.
        var command = new RootCommand("Aspire terminal host (per-replica HMP v1 broker).")
        {
            producerOption,
            consumerOption,
            controlOption,
            columnsOption,
            rowsOption,
            shellOption,
        };
        // The terminal host argv comes from DCP only; treat unknown flags as a hard error
        // so we don't silently accept garbage and start with the wrong configuration.
        command.TreatUnmatchedTokensAsErrors = true;

        var parseResult = command.Parse(args);
        if (parseResult.Errors.Count > 0)
        {
            var message = new StringBuilder();
            foreach (var error in parseResult.Errors)
            {
                if (message.Length > 0)
                {
                    message.Append("; ");
                }
                message.Append(error.Message);
            }
            throw new TerminalHostArgsException(message.ToString());
        }

        return new TerminalHostArgs
        {
            ProducerUdsPath = parseResult.GetValue(producerOption)!,
            ConsumerUdsPath = parseResult.GetValue(consumerOption)!,
            ControlUdsPath = parseResult.GetValue(controlOption)!,
            Columns = parseResult.GetValue(columnsOption),
            Rows = parseResult.GetValue(rowsOption),
            Shell = parseResult.GetValue(shellOption),
        };
    }

    private static Option<T> SingleValueOption<T>(
        string name,
        bool required,
        string description,
        T? defaultValue = default)
    {
        var option = new Option<T>(name)
        {
            Description = description,
            Required = required,
            Arity = ArgumentArity.ExactlyOne,
            AllowMultipleArgumentsPerToken = false,
        };

        if (!required && defaultValue is not null)
        {
            option.DefaultValueFactory = _ => defaultValue;
        }

        // System.CommandLine accepts repeated occurrences of single-valued options and
        // silently keeps the last value (last-write-wins). For this host every flag
        // identifies a per-replica resource (UDS path, dimensions, shell), so a duplicate
        // is unambiguously a misuse by the caller - reject it uniformly across all flags.
        option.Validators.Add(r =>
        {
            if (r.IdentifierTokenCount > 1)
            {
                r.AddError($"{name} may only be specified once.");
            }
        });

        return option;
    }
}

/// <summary>
/// Thrown when the terminal host receives malformed command-line arguments.
/// </summary>
internal sealed class TerminalHostArgsException(string message) : Exception(message);

