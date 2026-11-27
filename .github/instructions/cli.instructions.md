---
applyTo: "src/Aspire.Cli/**/*.cs"
---

# CLI agent instructions

## Application code, not a library

- The CLI is an application, not a reusable library. Its public C# types and members are implementation details, not a supported public API.
- Do not flag breaking changes to those types or members, or require compatibility overloads or deprecation shims.
- XML documentation is optional for application types and members, regardless of accessibility. Do not flag missing XML documentation or require it when adding or changing code; this overrides the shared XML documentation requirements.
- Continue reviewing compatibility of user-visible behavior and external contracts, such as CLI commands, options, exit codes, and machine-readable output.

## Tables and machine-readable output

- Use `TableExtensions.AddBoldColumn` for Spectre table headers and escape user-provided cell text with `EscapeMarkup()`.
- Display `-` for empty table cells rather than leaving them blank.
- For commands supporting table and JSON output, use the existing `OutputFormat` convention. Emit JSON with `DisplayRawText` on stdout and let the shared command infrastructure route diagnostics to stderr.
- Preserve JSON value types and empty objects/arrays. Table display placeholders and Spectre markup must not appear in JSON output.

## Long-running `--follow` commands

- Streaming commands that a host process (the VS Code extension) can spawn must override `RunsUnderLauncherWatchdog(ParseResult)` on `BaseCommand` so the launcher watchdog is armed before `ExecuteAsync` runs.
- Do not call `ParentProcessWatchdog` from a command. `StartForCliParent` is internal, and arming the watchdog inside `ExecuteAsync` leaves earlier awaits — connection resolution, RPC handshake, initial state load — unwatched, so a force-killed launcher can strand the command there forever.
- Treat a launcher-triggered cancellation as a normal end of stream. `BaseCommand` already maps it to a successful exit; do not surface it as an error.
- See [Launcher watchdogs for long-running commands](../../docs/specs/process-management.md#launcher-watchdogs-for-long-running-commands).
