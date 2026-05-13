// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;

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
    /// Throws <see cref="TerminalHostArgsException"/> with a human-readable message on any
    /// parse error so the host can write a friendly message to stderr.
    /// </summary>
    public static TerminalHostArgs Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? producer = null;
        string? consumer = null;
        string? control = null;
        int columns = 120;
        int rows = 30;
        string? shell = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--producer-uds":
                    if (producer is not null)
                    {
                        throw new TerminalHostArgsException(
                            "--producer-uds may only be specified once. Each terminal host serves exactly one replica.");
                    }
                    producer = ParseString(args, ref i, "--producer-uds");
                    break;
                case "--consumer-uds":
                    if (consumer is not null)
                    {
                        throw new TerminalHostArgsException(
                            "--consumer-uds may only be specified once. Each terminal host serves exactly one replica.");
                    }
                    consumer = ParseString(args, ref i, "--consumer-uds");
                    break;
                case "--control-uds":
                    control = ParseString(args, ref i, "--control-uds");
                    break;
                case "--columns":
                    columns = ParseInt(args, ref i, "--columns");
                    break;
                case "--rows":
                    rows = ParseInt(args, ref i, "--rows");
                    break;
                case "--shell":
                    shell = ParseString(args, ref i, "--shell");
                    break;
                default:
                    throw new TerminalHostArgsException($"Unknown argument: '{arg}'.");
            }
        }

        if (string.IsNullOrEmpty(producer))
        {
            throw new TerminalHostArgsException("Missing required argument: --producer-uds.");
        }

        if (string.IsNullOrEmpty(consumer))
        {
            throw new TerminalHostArgsException("Missing required argument: --consumer-uds.");
        }

        if (string.IsNullOrEmpty(control))
        {
            throw new TerminalHostArgsException("Missing required argument: --control-uds.");
        }

        if (columns < 1 || rows < 1)
        {
            throw new TerminalHostArgsException(
                $"--columns and --rows must be >= 1 (got {columns}x{rows}).");
        }

        return new TerminalHostArgs
        {
            ProducerUdsPath = producer,
            ConsumerUdsPath = consumer,
            ControlUdsPath = control,
            Columns = columns,
            Rows = rows,
            Shell = shell,
        };
    }

    private static string ParseString(string[] args, ref int i, string name)
    {
        if (i + 1 >= args.Length)
        {
            throw new TerminalHostArgsException($"Missing value for argument '{name}'.");
        }

        return args[++i];
    }

    private static int ParseInt(string[] args, ref int i, string name)
    {
        var raw = ParseString(args, ref i, name);
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            throw new TerminalHostArgsException($"Argument '{name}' expects an integer (got '{raw}').");
        }

        return value;
    }
}

/// <summary>
/// Thrown when the terminal host receives malformed command-line arguments.
/// </summary>
internal sealed class TerminalHostArgsException(string message) : Exception(message);
