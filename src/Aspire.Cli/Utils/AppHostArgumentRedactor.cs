// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Utils;

/// <summary>
/// Redacts the arguments a user forwards to an AppHost so they never reach CLI logs or
/// profiling traces.
/// </summary>
/// <remarks>
/// Everything after the first <c>--</c> separator is application input the CLI never
/// interprets, so it routinely carries connection strings, API keys, and other secrets:
/// <code>
/// aspire run -- --ConnectionStrings:db "Server=...;Password=hunter2"
/// dotnet run --project AppHost.csproj -- --ApiKey sk-live-...
/// </code>
/// The VS Code extension already redacts the same values before logging a debug
/// configuration (see <c>extension/src/debugger/AspireDebugSession.ts</c>); this keeps the
/// CLI consistent with it. One placeholder is emitted per token so log readers can still
/// diagnose argument-count and boundary problems.
/// <para>
/// This type only handles command lines the CLI itself projected or built, where a literal
/// <c>--</c> always precedes AppHost input. Raw user arguments carry no such guarantee — the
/// <c>run</c> and <c>start</c> commands also forward unmatched tokens — so those must be
/// redacted with <c>ParseResultHelper.GetLoggableArguments</c> instead.
/// </para>
/// </remarks>
internal static class AppHostArgumentRedactor
{
    /// <summary>
    /// Placeholder substituted for each argument that follows the separator.
    /// </summary>
    internal const string RedactedToken = "<redacted>";

    private const string ArgumentSeparator = "--";

    /// <summary>
    /// Returns a copy of <paramref name="args"/> that is safe to log: tokens up to and
    /// including the first <c>--</c> separator are preserved verbatim and every later token is
    /// replaced with <see cref="RedactedToken"/>. When there is no separator the arguments are
    /// entirely CLI-owned and are returned unchanged.
    /// </summary>
    internal static IReadOnlyList<string> Redact(IReadOnlyList<string> args)
    {
        var separatorIndex = -1;
        for (var i = 0; i < args.Count; i++)
        {
            if (string.Equals(args[i], ArgumentSeparator, StringComparison.Ordinal))
            {
                separatorIndex = i;
                break;
            }
        }

        if (separatorIndex < 0)
        {
            return args;
        }

        // Every token past the separator is redacted, including a literal "--" that appears after
        // the first one: only the first separator is a boundary, later ones are AppHost input.
        return RedactFrom(args, separatorIndex + 1);
    }

    /// <summary>
    /// Returns a copy of <paramref name="args"/> in which every token from
    /// <paramref name="firstRedactedIndex"/> onward is replaced with <see cref="RedactedToken"/>.
    /// </summary>
    /// <remarks>
    /// Some invocations carry AppHost input without a separator to key off. Launching a built
    /// AppHost directly is one of them, because the executable is invoked without the
    /// <c>dotnet run</c> boundary:
    /// <code>
    /// bin/Debug/net10.0/MyApp.AppHost --ApiKey sk-live-...
    /// </code>
    /// Those callers know where CLI-owned arguments stop and pass that index explicitly.
    /// </remarks>
    internal static IReadOnlyList<string> RedactFrom(IReadOnlyList<string> args, int firstRedactedIndex)
    {
        if (firstRedactedIndex >= args.Count)
        {
            return args;
        }

        var redacted = new List<string>(args.Count);
        for (var i = 0; i < args.Count; i++)
        {
            redacted.Add(i < firstRedactedIndex ? args[i] : RedactedToken);
        }

        return redacted;
    }

    /// <summary>
    /// Returns the space-joined, redacted form of <paramref name="args"/> for message templates
    /// that log a whole command line.
    /// </summary>
    internal static string RedactToString(IReadOnlyList<string> args) => string.Join(' ', Redact(args));

    /// <summary>
    /// Returns the space-joined form of <paramref name="args"/> with every token from
    /// <paramref name="firstRedactedIndex"/> onward replaced by <see cref="RedactedToken"/>.
    /// </summary>
    internal static string RedactFromToString(IReadOnlyList<string> args, int firstRedactedIndex)
        => string.Join(' ', RedactFrom(args, firstRedactedIndex));
}
