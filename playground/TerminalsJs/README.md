# TerminalsJs playground

TypeScript/Node.js AppHost demonstrating `withTerminal()` on a polyglot resource.

## What's here

- `apphost.mts` — TS AppHost that adds a single Node.js child app and attaches an interactive terminal session via `withTerminal()`.
- `guessing-game/` — Small dependency-free Node guessing game (1–100). Reads from stdin via `readline`, writes colored ANSI output. Designed to be driven from the dashboard's xterm.js terminal panel.
- `aspire.config.json` — Aspire CLI config wiring `Aspire.Hosting.JavaScript`.

## Run

```bash
aspire run
```

Then open the dashboard, expand the `guessing-game` resource, and click the terminal tab to attach. Type a number and press enter.

Commands inside the game: `help`, `new`, `cheat`, `quit`.

## Notes for in-repo runs

This sample exercises the polyglot AppHost path (`DotNetBasedAppHostServerProject`). When running from inside the repo with `dotnet run --project src/Aspire.Cli -- run --project playground/TerminalsJs`, the CLI injects `ASPIRE_TERMINAL_HOST_PATH` pointing at `artifacts/bin/Aspire.Managed/Debug/net10.0/aspire-managed` so DCP can resolve the terminal host binary without a per-RID NuGet stamp.
