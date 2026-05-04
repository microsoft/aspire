// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;

namespace Aspire.TerminalHost;

/// <summary>
/// Parsed command-line arguments for the Aspire terminal host.
/// </summary>
internal sealed class TerminalHostArgs
{
    public required int ReplicaCount { get; init; }
    public required string[] ProducerUdsPaths { get; init; }
    public required string[] ConsumerUdsPaths { get; init; }
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
    ///   <item><c>--replica-count N</c> (required, &gt;= 1)</item>
    ///   <item><c>--producer-uds PATH</c> (repeated N times)</item>
    ///   <item><c>--consumer-uds PATH</c> (repeated N times)</item>
    ///   <item><c>--control-uds PATH</c> (required)</item>
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

        int? replicaCount = null;
        var producers = new List<string>();
        var consumers = new List<string>();
        string? control = null;
        int columns = 120;
        int rows = 30;
        string? shell = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--replica-count":
                    replicaCount = ParseInt(args, ref i, "--replica-count");
                    break;
                case "--producer-uds":
                    producers.Add(ParseString(args, ref i, "--producer-uds"));
                    break;
                case "--consumer-uds":
                    consumers.Add(ParseString(args, ref i, "--consumer-uds"));
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

        if (replicaCount is null)
        {
            throw new TerminalHostArgsException("Missing required argument: --replica-count.");
        }

        if (replicaCount.Value < 1)
        {
            throw new TerminalHostArgsException(
                $"--replica-count must be >= 1 (got {replicaCount.Value}).");
        }

        if (producers.Count != replicaCount.Value)
        {
            throw new TerminalHostArgsException(
                $"Expected {replicaCount.Value} --producer-uds argument(s), got {producers.Count}.");
        }

        if (consumers.Count != replicaCount.Value)
        {
            throw new TerminalHostArgsException(
                $"Expected {replicaCount.Value} --consumer-uds argument(s), got {consumers.Count}.");
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
            ReplicaCount = replicaCount.Value,
            ProducerUdsPaths = [.. producers],
            ConsumerUdsPaths = [.. consumers],
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
