# Aspire Dogfooder

A Hex1b-based TUI test rig for exercising the **CLI identity sidecar** —
the runtime surface (`ASPIRE_CLI_*` env vars + `.aspire-install.json`
fields) that coerces the Aspire CLI into reporting an arbitrary channel,
version, commit, or NuGet service index.

Dogfooder lets you create named "dogfooding sessions" with a configured
identity, launches an embedded terminal pre-populated with the right env
vars, and (in later phases) records the session for sharing.

> **Status: Phase 1.** Skeleton + environment validation + session list +
> embedded terminal launch. See [the spec][spec] for the full roadmap.
> [spec]: ../../docs/specs/cli-identity-sidecar.md

## Running

```bash
dotnet run --project tools/Dogfooder
```

On startup Dogfooder runs three probes:

1. `dotnet --version` — verifies the .NET SDK is on `PATH`.
2. `gh auth status` — verifies you're signed into GitHub (needed for the
   future PR catalog).
3. `gh auth token` — caches the bearer token in-process.

If any probe fails you'll see remediation guidance. Once all three are
green you can press **Continue →** to enter the main screen.

The main screen is a split pane:
- **Left:** session list + "[+] Add" button.
- **Right:** depending on selection, either the session config form or
  the embedded terminal.

Configured sessions are persisted to
`~/.aspire/dogfooder/sessions.json` as a versioned envelope so future
schema extensions remain backward-compatible.

## Architecture

```
tools/Dogfooder/
  Program.cs                          # Host.CreateApplicationBuilder + RootCommand
  Commands/
    RunCommand.cs                     # default action — boots the TUI
    SelfTestCommand.cs                # Phase 2 stub
  State/                              # mutable state — zero Hex1b deps
    AppState.cs                       # top-level: phase, selected session, draft
    EnvironmentValidationState.cs     # probe outcomes + cached gh token
    DogfoodSessionConfig.cs           # immutable record (persisted)
    DogfoodSession.cs                 # config + runtime status
    DogfoodSessionStore.cs            # in-memory list + load/save
    ChangeNotifier.cs                 # event helper for re-render hooks
  Services/
    ISessionStoreFile / SessionStoreFile.cs
    IGitHubAuthProbe / GitHubAuthProbe.cs
    IDogfoodSessionPreparer / DogfoodSessionPreparer.cs
    IPrCatalog / StubPrCatalog.cs
  Screens/
    EnvironmentValidationScreen.cs    # builder: validation probes view
    MainScreen.cs                     # HSplitter(SessionList | Detail)
    Panels/
      SessionListPanel.cs
      SessionConfigPanel.cs
      SessionTerminalPanel.cs         # embedded Hex1bTerminal w/ env-var injection
```

### Layout rules

- **All mutable state lives in `State/*`.** Widget-builder methods take
  the relevant state object as a parameter and produce a pure widget
  tree. No fields on screens or panels.
- **Widget builders are static method invocations.** Each panel is a
  `static class` with a `Build(WidgetContext<…>, …)` method. This makes
  the future automator-driven `self-test` command able to assert state
  directly without needing to peek into the UI tree.
- **`ChangeNotifier` bridges state mutations to render.** Screens
  subscribe once in `RunCommand` and forward to `Hex1bApp.Invalidate()`.

### Identity env-var sharing

The `ASPIRE_CLI_*` env-var name constants are defined in
`src/Shared/AspireCliIdentityEnvVars.cs` and link-included into both
`Aspire.Cli.csproj` and this project. We deliberately avoid taking a
`<ProjectReference>` on the CLI because it's AOT-published and pulling
it in here would drag its dependency tree (and AOT trimming constraints)
into a tool that doesn't need them.

## Roadmap

- **Phase 1 (this drop):** Skeleton, env validation, session config,
  embedded terminal with env-var injection, JSON persistence.
- **Phase 2:** `DogfoodSessionPreparer` upgrade — drive the embedded
  terminal via `Hex1bTerminalAutomator` to run prep commands
  (e.g. `aspire --version`) and assert the identity actually took.
  Real `self-test` subcommand built on the same automator surface.
- **Phase 3:** Interactive scenarios — canned automator scripts
  (`aspire new starter`, `aspire add postgres`, …) triggerable from the
  session pane.
- **Phase 4:** Asciinema recording via Hex1b's `WithAsciinemaRecording`
  into `~/.aspire/dogfooder/recordings/<session>.cast`.
- **Phase 5:** Real GitHub PR catalog using the cached gh token —
  replaces `StubPrCatalog` with live PR build queries.
