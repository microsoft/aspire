# Testing Loop: Aspire as a Test Orchestrator

> **Status:** Draft

This document explores using Aspire not just as a local development orchestrator but as
a **test orchestrator** for polyglot applications. It introduces a new `aspire test` CLI
command and a core `TestAnnotation` abstraction in the app model. The lifetime of an
`aspire test` invocation is bounded by the resources that participate in the test run:
once those resources reach a terminal state, results are harvested into an output
directory and the AppHost is shut down.

> **Note:** This is a design-exploration spec. The C# APIs, CLI options, backchannel
> methods, and result schemas shown here are **illustrative and not final** — they exist
> to communicate the shape of the design, not to lock in signatures.

## Table of Contents

1. [Overview](#overview)
2. [Motivation](#motivation)
3. [Concepts](#concepts)
4. [The `aspire test` command](#the-aspire-test-command)
5. [Post-completion lifetime and key switches](#post-completion-lifetime-and-key-switches)
6. [Architecture](#architecture)
7. [The `TestAnnotation` abstraction](#the-testannotation-abstraction)
8. [Operation mode](#operation-mode)
9. [Results model and output directory](#results-model-and-output-directory)
10. [Backchannel involvement](#backchannel-involvement)
11. [Composition with the app model](#composition-with-the-app-model)
12. [Extensibility](#extensibility)
13. [Higher-level layers](#higher-level-layers)
14. [Tooling and agent skills](#tooling-and-agent-skills)
15. [Comparison](#comparison)
16. [Open questions and future work](#open-questions-and-future-work)
17. [References](#references)

---

## Overview

Aspire already excels at standing up the infrastructure an application needs — databases,
caches, message brokers, cloud resources, and the services under development — and wiring
them together with connection strings, health checks, and dependency ordering. **Many**
tests need that same infrastructure, and for them the testing loop's biggest win is reusing
the exact environment the app runs in rather than a hand-maintained parallel one.

But infrastructure is not the only benefit, and not every test needs it. Even for tests
that require **no** infrastructure — pure unit tests, for example — running them through the
loop still adds value:

- **Useful injected context.** Aspire can inject the same ambient configuration it gives any
  resource — OTEL environment variables (so test telemetry flows to the dashboard like
  everything else), connection information, and other environment wiring — without the test
  author setting it up by hand.
- **A uniform, polyglot test surface.** The loop abstracts away the platform-specific test
  runners. In a polyglot solution, xUnit/MSTest, pytest, and Playwright suites are all driven
  and reported the same way (`aspire test`, one output directory), instead of every language
  bringing its own invocation and reporting conventions.

The **testing loop** reuses the app model as the description of a test environment and
adds a finite, result-producing command on top of it:

- `aspire test` runs the AppHost much like `aspire run`, but its lifetime is **bounded**
  by resources marked with a `TestAnnotation`.
- A `TestAnnotation` is, at its core, a **callback** that waits until "whatever
  constitutes a test run" for that resource has completed and then **collects results**
  through an Aspire API.
- When every test resource reaches a terminal state, the CLI harvests the aggregated
  results into an **output directory** (consumable by humans and agents) and shuts the
  AppHost down.

Because test resources are just resources, they compose with everything else in the app
model: `WaitFor`, health checks, references, and dependencies — including cloud resources
where that makes sense.

The system is intentionally open. The `TestAnnotation` primitive is language- and
framework-agnostic; higher-level layers build on it (for example, using
Microsoft.Testing.Platform to invoke xUnit/MSTest projects, or wrappers for pytest and
Playwright), and extensions can add entirely new testing mechanisms (for example, a
database upgrade-sequence tester that leverages Aspire's ability to provision database
resources).

---

## Motivation

### The problem

Integration and end-to-end tests routinely need real infrastructure. Today teams either:

- Hand-roll `docker compose` files and bash scripts that drift from how the app actually
  runs, or
- Use `Aspire.Hosting.Testing`, which is powerful but **.NET-only** (it invokes the
  AppHost entry point via reflection and hooks `DiagnosticListener`), or
- Use the **outer-loop** polyglot approach (see
  [Polyglot AppHost Testing](./polyglot-apphost-testing.md)) where an external test runner
  spawns the CLI (`aspire start` / `describe` / `stop`) to drive the AppHost as a black box.

All of these work, but none of them let the **app model itself** describe a test run and
own its lifetime. There is no first-class notion of "this resource represents a test, and
the run is finished when the tests finish."

`Aspire.Hosting.Testing` deserves special mention here because its
`TestDistributedApplicationBuilder` *does* directly expose the app model to a test — but it
does so as a private, per-test construction: each test builds and owns its own application.
The bigger leap this spec proposes is an **ambient, shared app model** in which the
**scheduling of test execution is part of the app model itself**. Tests are resources in a
single shared model — they can depend on shared infrastructure, be ordered relative to one
another, and be reasoned about as a graph — rather than each test spinning up an isolated
application behind the scenes.

### The solution

Make the app model the description of a test run:

- Mark the resources that represent tests with a `TestAnnotation`.
- Let the CLI run the AppHost and **wait for those resources to finish** instead of
  running indefinitely.
- Harvest each test resource's results (and any attached artifacts) into a single output
  directory.

This keeps tests on exactly the same orchestration substrate as `aspire run`: the same
resource wiring, the same dependency graph, the same dashboard for live observation, and
the same connection-string/endpoint plumbing.

### Inner-loop vs outer-loop

This spec is the **complement** of [Polyglot AppHost Testing](./polyglot-apphost-testing.md):

| | Outer-loop (existing spec) | Inner-loop (this spec) |
|---|---|---|
| **Who owns the test lifetime** | The external test runner (Jest, pytest, xUnit) | The app model / AppHost via `TestAnnotation` |
| **How tests start** | The runner spawns `aspire start` then drives resources | Test resources are part of the model and run with it |
| **How the run ends** | The runner calls `aspire stop` when its assertions finish | `aspire test` ends when test resources reach terminal state |
| **Where results live** | In the external runner's reporter | Aggregated by Aspire into an output directory |
| **Best when** | Tests are authored in a host language and treat Aspire as a black box | Tests are themselves resources composed into the model |

The two approaches are not mutually exclusive — an outer-loop runner could still use
`aspire describe`/`logs` against an `aspire test --keep-alive` session for debugging.

---

## Concepts

- **Test resource** — any resource in the app model annotated with a `TestAnnotation`. It
  may be a .NET test project, a pytest invocation, a Playwright run, a script, or a custom
  mechanism. It is an ordinary resource in every other respect.
- **`TestAnnotation`** — an `IResourceAnnotation` that marks a resource as a test resource
  and carries an **async callback** that drives the test run. The callback receives a
  `DistributedApplicationTestContext` and uses it to **record results as they occur or in
  batch**, **report live progress**, and **attach files**.
- **Terminal state** — the existing notion of a resource being done:
  `KnownResourceStates.TerminalStates` = `Finished`, `Exited`, `FailedToStart`.
- **Test run** — the whole `aspire test` invocation: all test resources plus the
  infrastructure they depend on.
- **Result** — a structured outcome for a test resource (passed/failed/skipped counts,
  duration, messages) plus optional **attached files** (logs, TRX, JUnit XML, screenshots,
  traces).
- **Output directory** — the on-disk location where the CLI writes the aggregated run
  manifest and per-resource artifacts.
- **Harvesting** — the callback records results and attachments through the test context as
  the run progresses; the CLI aggregates them into the output directory.

---

## The `aspire test` command

`aspire test` behaves like `aspire run` with three differences: a **bounded lifetime**,
**result harvesting**, and a set of **test-specific switches**.

```text
aspire test [<apphost>] [options]
```

Behavior:

1. Locate/build/start the AppHost exactly as `aspire run` does (same project location,
   same dashboard, same backchannel handshake, same run-mode resource behavior).
2. Discover which resources carry a `TestAnnotation` (reported over the backchannel).
3. Wait for all test resources to reach a terminal state (subject to `--timeout`).
4. Harvest results: drive each test resource's async callback, which records results and
   attachments through its test context as they occur, and write the **output directory**.
5. Determine the overall outcome and exit code from the aggregated results.
6. Tear the AppHost down — unless a keep-alive switch asks to leave it running (see below).

Illustrative options:

| Option | Purpose |
|---|---|
| `--output-dir <path>` | Where to write the run manifest and per-resource artifacts. Defaults to a conventional location (for example `./.aspire/test-results/<timestamp>`). |
| `--keep-alive` | After harvesting results, **detach and leave the AppHost running** for inspection instead of tearing it down (see below). |
| `--isolated` | Run with randomized ports and isolated user secrets so multiple instances (for example, different git worktrees) can run simultaneously. Reuses the existing shared option. |
| `--timeout <duration>` | Overall wall-clock budget for the test run (distinct from the AppHost **startup** timeout). |

Notes:

- `aspire test` runs in **run mode** semantics (see [Operation mode](#operation-mode)), so
  resources behave exactly as they do under `aspire run`. The only change is who decides
  when the run is over.
- Exit code reflects the aggregated result: `0` when all test resources pass, non-zero on
  any failure or on timeout. The precise code space is settled during implementation.
- `Ctrl+C` cancels the run, tears down (unless keep-alive), and harvests whatever partial
  results exist.

---

## Post-completion lifetime and key switches

By default, once all test resources reach a terminal state, the CLI harvests results and
tears the AppHost down — the command is finite, which is what CI wants.

### Keep-alive (detach)

A single opt-in switch keeps the infrastructure up after the tests finish so it can be
inspected — invaluable for debugging a failing test or for an agent that wants to poke at
the running system.

The decided behavior is **detach**, mirroring `aspire start`:

- Run the tests and harvest results as usual.
- Instead of tearing down, **detach and leave the AppHost running in the background**.
- The `aspire test` invocation **returns** (with the result and exit code), so a human or
  agent can immediately run other commands against the still-running system:
  `aspire ps`, `aspire logs`, `aspire describe`, resource commands, MCP, and finally
  `aspire stop` to tear it down.

This is the most agent-friendly shape precisely because the test invocation returns
control rather than blocking. The alternative — staying attached and blocking until
`Ctrl+C` — is rejected because it makes follow-up inspection awkward to automate.

> The flag name is settled during implementation. `--keep-alive` reads as the intent;
> reusing `--detach` is also a candidate but must be documented as differing from
> `aspire run --detach`, which detaches **immediately at startup** rather than **after the
> tests complete**.

This path reuses the existing detached-mode infrastructure (`aspire start` / `aspire stop`,
`RunCommand.ExecuteDetachedAsync`, `DetachOutputInfo`).

### `--isolated` and multiple worktrees

`--isolated` exists today as a shared option: "run in isolated mode with randomized ports
and isolated user secrets, allowing multiple instances to run simultaneously."

For the testing loop its primary purpose is **supporting multiple git worktrees**: a
developer or agent can run `aspire test` from several worktree directories at the same time
without the instances stopping or colliding with one another. (The current non-isolated
behavior stops an already-running instance of the same AppHost and warns: "To run multiple
isolated instances simultaneously, run from different directories such as git worktree
directories.") Secondary benefits are parallelizable test runs and not clobbering a
running `aspire run` dev session.

### `--timeout`

`--timeout` bounds how long the **test run** may take (waiting for all test resources to
reach a terminal state). This is **distinct** from the existing AppHost **startup** timeout
(`AppHostStartupTimeout`), which guards reaching a running state. Both can apply: startup
must succeed within the startup timeout, and then tests must finish within `--timeout`.

### Outcome × switch matrix

The interaction between `--timeout` and keep-alive is deliberate:

| Outcome | Default (no keep-alive) | `--keep-alive` |
|---|---|---|
| All test resources pass | Harvest results, tear down, exit `0` | Harvest results, **detach and stay alive**, exit `0` |
| A test resource fails | Harvest results, tear down, exit non-zero | Harvest results, **detach and stay alive**, exit non-zero |
| `--timeout` expires | Harvest partial results, **tear down**, exit non-zero (timeout) | Harvest partial results, **return non-zero but leave the AppHost alive** |
| `Ctrl+C` | Harvest partial results, tear down, exit cancelled | Harvest partial results, tear down, exit cancelled |

The key row: **`--timeout` + `--keep-alive` returns a non-zero exit code but leaves the
AppHost running**, so the (possibly hung) infrastructure can be inspected to understand
*why* the run timed out, rather than being torn down out from under the investigator.

---

## Architecture

```text
┌─────────────┐   spawn/build/run    ┌──────────────────────────────────────────┐
│  aspire CLI │ ───────────────────▶ │                AppHost                     │
│ (test cmd)  │                      │                                            │
│             │   backchannel        │  ┌──────────────┐   ┌───────────────────┐ │
│  • waits    │ ◀──────────────────▶ │  │ Test loop    │   │ Test resources    │ │
│  • harvests │   (JSON-RPC)         │  │ coordinator  │──▶│  [TestAnnotation] │ │
│  • writes   │                      │  │ (hosted svc) │   │  + dependencies   │ │
│    output   │                      │  └──────────────┘   └───────────────────┘ │
│  • teardown │                      │         │ invokes async callbacks          │
│    or detach│                      │         ▼                                  │
└─────────────┘                      │   streamed results + progress ─────────────┼──▶ over backchannel
                                     └──────────────────────────────────────────┘
```

Sequence:

1. **Start** — the CLI starts the AppHost in run mode (with a test-mode flag) and connects
   the backchannel, exactly like `aspire run`.
2. **Discover** — the CLI asks which resources are test resources (capability-gated
   backchannel call).
3. **Wait** — a host-side *test loop coordinator* (a hosted service) watches resource
   states via `ResourceNotificationService`. The dependencies the test resources `WaitFor`
   are brought up first by the normal orchestration.
4. **Run and record** — each test resource's async callback is invoked with a
   `DistributedApplicationTestContext`. It records results **as they occur** (or in a final
   batch), reports progress, and attaches files while the resource is working. The run for a
   given resource is bounded by it reaching a terminal state
   (`Finished`/`Exited`/`FailedToStart`).
5. **Report** — progress and results are streamed to the CLI over the backchannel (analogous
   to how `PublishingActivity` is streamed today), so the UX can show each test resource
   working and updating live.
6. **Harvest** — the CLI writes the run manifest and per-resource artifacts into the output
   directory.
7. **Finish** — the CLI computes the exit code and either tears the AppHost down or detaches
   and leaves it running (keep-alive), per the matrix above.

---

## The `TestAnnotation` abstraction

`TestAnnotation` is an `IResourceAnnotation` (the same pattern as `ResourceCommandAnnotation`)
that marks a resource as a test resource and carries the **async callback** that drives the
test run. Rather than returning a single result at the end, the callback receives a
`DistributedApplicationTestContext` and uses it to record results **as they occur or in
batch**, report progress, and attach files — so the dashboard and CLI can show a test
resource working and updating live.

Illustrative shape (not final):

```csharp
/// <summary>
/// Marks a resource as participating in an `aspire test` run and provides the async callback
/// that drives the test and records its results through a <see cref="DistributedApplicationTestContext"/>.
/// </summary>
public sealed class TestAnnotation : IResourceAnnotation
{
    public TestAnnotation(Func<DistributedApplicationTestContext, Task> runAsync)
    {
        RunAsync = runAsync;
    }

    /// <summary>
    /// Invoked to drive the test resource's run. Implementations observe the underlying test
    /// process (streaming output, native reports, exit code) and record results incrementally
    /// or in a final batch via the context, attaching any files to include in the output
    /// directory. The callback returns when the resource has reached a terminal state and all
    /// results have been recorded.
    /// </summary>
    public Func<DistributedApplicationTestContext, Task> RunAsync { get; }
}
```

```csharp
public sealed class DistributedApplicationTestContext
{
    public required IResource Resource { get; init; }
    public required IServiceProvider Services { get; init; }          // resolve app-model services
    public required CancellationToken CancellationToken { get; init; }

    // Live snapshot of the resource (state, exit code) for callbacks that wait for terminal.
    public CustomResourceSnapshot Snapshot { get; }

    // Await the resource reaching a terminal state (Finished/Exited/FailedToStart).
    public Task WaitForTerminalAsync(CancellationToken cancellationToken = default);

    // Record results as they occur. Each call flows to the dashboard/CLI as a progress update.
    public Task RecordResultAsync(TestResult result, CancellationToken cancellationToken = default);

    // Record many results at once (for runners that only emit a report at the end).
    public Task RecordResultsAsync(IEnumerable<TestResult> results, CancellationToken cancellationToken = default);

    // Free-form progress for the "test resource working" UX (e.g. "running 42/120").
    public Task ReportProgressAsync(string message, double? fraction = null, CancellationToken cancellationToken = default);

    // Attach a file (TRX, junit.xml, screenshot, trace) to this resource's output.
    public Task AttachFileAsync(string path, string? displayName = null, CancellationToken cancellationToken = default);
}

public sealed class TestResult
{
    public required string Name { get; init; }                        // e.g. fully-qualified test name / node id
    public required TestOutcome Outcome { get; init; }                // Passed | Failed | Skipped | TimedOut | Errored
    public TimeSpan Duration { get; init; }
    public string? Message { get; init; }                             // failure message / assertion detail
    public string? Output { get; init; }                              // captured stdout/stderr for this test
}
```

A builder extension marks any resource as a test and attaches the callback:

```csharp
var tests = builder.AddPythonApp("integration-tests", "../tests", "pytest")
                   .WaitFor(api)
                   .WithTestRun(async ctx =>
                   {
                       // Wait for pytest to finish, then parse junit.xml and record + attach it.
                       await ctx.WaitForTerminalAsync();
                       var report = await PytestReport.ReadAsync(ctx, "junit.xml");
                       await ctx.RecordResultsAsync(report.ToTestResults());
                       await ctx.AttachFileAsync("junit.xml");
                   });
```

Key points:

- The callback is **async** and runs **in the AppHost**, where it has access to the resource,
  its live snapshot (including exit code), and app-model services. It can record results
  **incrementally** as the underlying framework emits them (live progress), or **in batch**
  once the resource reaches a terminal state — both are first-class.
- Because results and progress flow through the context as they happen, the dashboard and CLI
  can show a test resource as **working** with live counts and per-test outcomes, and surface
  attached files as they are produced.
- The annotation is the **low-level primitive**. Higher-level helpers
  (`AddXunitProject(...)`, `AddPytest(...)`, `AddPlaywright(...)`) are thin layers that add
  the resource and an appropriate `TestAnnotation`.
- Failure to start (`FailedToStart`) is a terminal state too, so a test resource that can't
  even launch surfaces as a failed result rather than hanging the run.

---

## Operation mode

The user's framing is that "test mode is pretty much the same as `aspire run`." That should
hold literally in the app model: test resources, their dependencies, the dashboard, and all
run-mode behaviors should be identical to a normal run. The only additions are lifetime
control and result harvesting.

Today the AppHost is told its operation via `--operation` → `AppHost:Operation` config, and
`DistributedApplicationOperation` has just two values, `Run` and `Publish`. Many behaviors
across the codebase branch on `ExecutionContext.IsRunMode` / `IsPublishMode`.

Two options:

1. **Run sub-mode (recommended).** Keep `Operation == Run` so every `IsRunMode` code path
   behaves exactly as it does for `aspire run`, and signal "test" as an **additional flag**
   (for example `AppHost:TestMode=true` plus the output directory). The test loop
   coordinator is only enabled when this flag is present. This is the smallest, safest
   change and preserves run semantics by construction.
2. **New operation value (`DistributedApplicationOperation.Test`).** More explicit, but it
   forces an audit of every `IsRunMode` check in the codebase (and in third-party
   integrations) to decide whether "test" should behave like "run," risking subtle
   divergence from `aspire run`.

This spec recommends option 1, treating `aspire test` as a run with a test coordinator and a
bounded lifetime. The recommendation can be revisited if a behavior genuinely needs to
differ between run and test.

### Test resources under `aspire run`

Because test resources are just resources in a shared model, they are present under
`aspire run` too — testing is not a separate world. The proposed default is that **resources
marked as a test resource do not start automatically** under `aspire run` (they appear in the
model and the dashboard but sit idle), so a normal dev session isn't perturbed by test
executions firing on every launch. A developer can then start an individual test on demand —
via a resource command, or a dashboard affordance (see the Test tab idea under
[future work](#open-questions-and-future-work)) — to iterate on a single test against the
already-running infrastructure. Under `aspire test`, the test resources start as part of the
bounded run and drive its lifetime.

---

## Results model and output directory

The output directory is the deliverable of a test run — readable by humans (open the files)
and agents (parse the JSON). A proposed layout:

```text
<output-dir>/
  run.json                       # aggregated run manifest
  resources/
    integration-tests/
      result.json                # structured TestResult records for this resource
      junit.xml                  # attached, framework-native report
      stdout.log                 # attached console output
    web-e2e/
      result.json
      report.html                # attached Playwright report
      screenshots/...            # attached artifacts
  apphost.log                    # AppHost log for the whole run
```

`run.json` aggregates the per-resource results and the overall outcome:

```json
{
  "schemaVersion": 1,
  "startedUtc": "2026-06-23T06:54:00Z",
  "finishedUtc": "2026-06-23T06:55:12Z",
  "outcome": "Failed",
  "totals": { "passed": 41, "failed": 2, "skipped": 1 },
  "resources": [
    {
      "name": "integration-tests",
      "type": "pytest",
      "outcome": "Failed",
      "passed": 18, "failed": 2, "skipped": 0,
      "durationSeconds": 22.4,
      "attachments": ["resources/integration-tests/junit.xml",
                      "resources/integration-tests/stdout.log"]
    },
    {
      "name": "web-e2e",
      "type": "playwright",
      "outcome": "Passed",
      "passed": 23, "failed": 0, "skipped": 1,
      "durationSeconds": 47.9,
      "attachments": ["resources/web-e2e/report.html"]
    }
  ]
}
```

Design points:

- The manifest is the **stable contract**; framework-native reports (TRX, JUnit XML,
  Playwright HTML) are carried alongside as attachments so existing tooling still works.
- Where a resource already emits a standard format, the higher-level layer's async callback
  simply parses and attaches it; it does not reinvent it.
- The output directory is written even when a keep-alive session stays running, so results
  are available immediately while the infrastructure is still up for inspection.
- Agents can be pointed at `run.json` as a single, predictable entry point.

---

## Backchannel involvement

The testing loop adds a small number of **capability-gated** methods to the existing
backchannel, following the rules in [CLI Backchannel](./cli-backchannel.md) (never break
existing clients; advertise a new capability; the CLI degrades gracefully when the AppHost
doesn't advertise it). A new capability string — for example `test.v1` — would be returned
from `GetCapabilitiesAsync` by AppHosts that support the loop.

Illustrative additions (not final):

- `IAsyncEnumerable<TestResourceInfo> GetTestResourcesAsync(...)` — enumerate which
  resources carry a `TestAnnotation` so the CLI knows what it is waiting for.
- `IAsyncEnumerable<TestRunActivity> WatchTestRunAsync(...)` — stream progress and
  `TestResult` records as test resources record them. This mirrors the existing
  `GetPublishingActivitiesAsync` streaming pattern — a finite, result-producing flow — which
  is a good precedent for shape and serialization (AOT-friendly `JsonSerializerContext`).

The CLI already streams resource states via `GetResourceStatesAsync`
(`ResourceNotificationService.WatchAsync` on the AppHost side); the test methods are layered
on top of that same notification infrastructure rather than replacing it. Teardown reuses the
existing `RequestStopAsync`, and detach reuses the existing detached-mode plumbing.

---

## Composition with the app model

Because test resources are just resources, they compose with the rest of the model:

- **`WaitFor` / `WaitForCompletion`** — a test resource waits for the services and
  infrastructure it needs before it runs. The orchestrator brings dependencies up first; the
  test runs; then it reaches terminal and the loop notices.
- **Health checks** — a test can wait for a dependency to be *healthy*, not merely started,
  using the same `WaitForResourceHealthyAsync` semantics the rest of Aspire uses.
- **Connection strings / endpoints / references** — tests consume the same wiring as the
  app, so there is no separate "test configuration" to keep in sync.
- **Cloud resources** — when it makes sense, a test resource can depend on provisioned cloud
  resources (for example, an Azure resource brought up for the run), since those are modeled
  the same way.

```csharp
var pg     = builder.AddPostgres("pg");
var db     = pg.AddDatabase("appdb");
var api    = builder.AddProject<Projects.Api>("api").WithReference(db).WaitFor(db);

var tests  = builder.AddXunitProject<Projects.IntegrationTests>("integration-tests")
                    .WithReference(api)
                    .WaitFor(api);   // don't start tests until the API is up
```

Here `integration-tests` is a normal resource that happens to be a test (via
`AddXunitProject`, which attaches a `TestAnnotation`). `aspire test` waits for it to finish;
`aspire run` would simply run it like any other executable.

---

## Extensibility

The loop is intentionally open. Anyone can build a new testing mechanism by adding a resource
and a `TestAnnotation`. The annotation's only obligations are: reach a terminal state when
done, and record results (plus optional attachments) through the test context as the run
progresses.

**Example: a database upgrade-sequence tester.** Aspire is already good at provisioning
database resources, so a test mechanism can exercise a sequence of schema upgrades against a
freshly provisioned database:

```csharp
var sql = builder.AddSqlServer("sql");

var upgradeTests = builder.AddDatabaseUpgradeTest("db-upgrades", sql)
                          .WithScriptsFrom("../db/migrations")     // ordered upgrade scripts
                          .ExpectFinalSchema("../db/expected.sql")
                          .WaitFor(sql);
```

`AddDatabaseUpgradeTest` is an extension that:

1. Adds a resource that provisions a clean database, applies each upgrade script in order,
   and verifies the resulting schema.
2. Attaches a `TestAnnotation` whose callback records which steps passed/failed and attaches
   the per-step logs and any diff against the expected schema as the run progresses.

From `aspire test`'s perspective this is indistinguishable from any other test resource — it
participates in the same loop, the same output directory, and the same exit-code semantics.

---

## Higher-level layers

These are thin layers over the `TestAnnotation` primitive. Each adds a resource and an
appropriate async callback; none of them are special-cased by the CLI.

### .NET via Microsoft.Testing.Platform (xUnit / MSTest)

Reference a test project as an app and invoke it through MTP (the platform Aspire's own tests
already use). MTP can emit native reports (for example TRX) which the callback parses
and attaches.

```csharp
var tests = builder.AddXunitProject<Projects.IntegrationTests>("integration-tests")
                   .WithReference(api)
                   .WaitFor(api);
// AddXunitProject runs the project via MTP, points it at a results file, and attaches a
// TestAnnotation that parses the TRX/native report and records TestResult entries.
```

### Python via pytest

```csharp
var tests = builder.AddPytest("api-tests", projectDirectory: "../tests")
                   .WithReference(api)
                   .WaitFor(api);
// AddPytest runs `pytest --junitxml=...`, then parses junit.xml and records results.
```

### Browser/E2E via Playwright

```csharp
var e2e = builder.AddPlaywright("web-e2e", projectDirectory: "../e2e")
                 .WithReference(web)
                 .WaitFor(web);
// AddPlaywright runs the Playwright suite, attaches the HTML report and any
// screenshots/traces, and reports pass/fail counts.
```

In every case the resource reaches a terminal state when the underlying runner exits, the
async callback records the runner's native output as `TestResult` entries (live or in batch),
and the artifacts land in the output directory next to a uniform `run.json`.

---

## Tooling and agent skills

Aspire's user-facing workflow skills live in the separate **`microsoft/aspire-skills`** repo
under `skills/` (siblings include `aspire`, `aspire-init`, `aspire-orchestration`,
`aspire-deployment`, `aspire-monitoring`, and `aspireify`) — **not** in this repository's
`.agents/skills/` directory, which holds contributor-facing skills.

A proposed **`skills/aspire-testing/`** skill (authored in that repo as a separate follow-up)
would guide a user or agent to:

- Run `aspire test` and choose `--isolated` (for worktree parallelism), `--timeout`, and
  keep-alive(detach) appropriately.
- Read the **output directory** — start at `run.json`, then drill into per-resource
  `result.json` and attachments.
- When keep-alive is set, inspect the still-running (or hung, in the timeout case) AppHost via
  `aspire ps`, `aspire logs`, `aspire describe`, resource commands, and MCP, then `aspire stop`
  to tear down.

It would cross-reference the sibling skills (`aspire-orchestration`, `aspire-deployment`,
`aspire-monitoring`) rather than duplicating them. Contributor-facing skills in this repo's
`.agents/skills/` (for example `cli-e2e-testing`, `deployment-e2e-testing`, `fix-flaky-test`,
`test-management`, `pr-testing`) remain relevant to *implementing and testing* the feature
itself.

---

## Comparison

| | `Aspire.Hosting.Testing` | Outer-loop CLI testing | `aspire test` (this spec) |
|---|---|---|---|
| **Lifetime owner** | The test process | The external runner | The app model / AppHost |
| **Language** | .NET only | Any | Any (primitive is language-agnostic) |
| **Model access** | Full builder access in-process | Black box | Tests are part of the model |
| **Result aggregation** | The test framework | The external runner | Aspire output directory |
| **Best for** | .NET unit/integration tests that mutate the model | Treating Aspire as a black box from a host language | Composing tests as resources with shared infra |
| **Inspection after run** | In-process debugging | `aspire describe`/`logs` against a started AppHost | `--keep-alive` detached AppHost |

`aspire test` does not replace the others; it adds a first-class, app-model-driven option and
shares infrastructure with both (run-mode semantics from `Aspire.Hosting`, and the
detached/inspection commands from the outer-loop toolset).

---

## Open questions and future work

- **Flag naming** — settle `--keep-alive` vs reusing `--detach` (with clear docs on the
  timing difference from `aspire run --detach`).
- **Operation signaling** — confirm the "run sub-mode + test flag" recommendation over a new
  `DistributedApplicationOperation.Test` value.
- **Test resources under `aspire run`** — confirm that test resources are present but do
  **not** auto-start under a normal run, and define how a developer starts one on demand.
- **Granular (single-test) execution** — should the model **harvest the individually runnable
  tests** within a test resource (for example, each `[Fact]` / pytest node / Playwright spec)
  and support executing just one? This would enable per-test re-runs and a richer dashboard
  view, but discovering and addressing individual tests across frameworks may be **fairly
  expensive** (an enumeration/discovery pass per resource, plus a framework-specific way to
  target a single test). Open question: is the cost worth it, and is it opt-in per layer?
- **Dashboard Test tab** — a dedicated dashboard tab for viewing test results (live progress,
  per-resource and — if granular execution lands — per-test outcomes, and links to attached
  artifacts), and as a place to trigger an individual test under `aspire run`.
- **Exit-code space** — define the precise codes for pass/fail/timeout/cancel.
- **Result schema** — finalize `run.json`/`result.json`, the `TestOutcome` enum, and the
  mapping to TRX/JUnit.
- **Backchannel surface** — finalize the `test.v1` capability and method shapes.

Suggested phasing:

1. **Core primitive** — `TestAnnotation`, the host-side test loop coordinator, the `test.v1`
   backchannel methods, `aspire test` with `--output-dir`, `--isolated`, `--timeout`, and
   keep-alive(detach), and the output directory format.
2. **MTP layer** — `AddXunitProject` / MSTest support with TRX harvesting.
3. **pytest and Playwright layers** — `AddPytest` and `AddPlaywright` with native report
   harvesting.
4. **Dashboard Test tab** — surface results live and let a developer trigger an individual
   test resource under `aspire run`.
5. **Granular execution (investigation)** — prototype harvesting individually runnable tests
   and single-test execution, and measure the discovery cost before committing.
6. **Enhancements** — result diffing, flaky-test reporting, and the database upgrade-sequence
   tester as a worked extension example.
7. **`aspire-testing` skill** — authored in `microsoft/aspire-skills`.

---

## References

- [Polyglot AppHost Testing](./polyglot-apphost-testing.md) — the outer-loop, runner-driven
  complement to this spec.
- [Polyglot AppHost](./polyglot-apphost.md) — polyglot AppHost architecture.
- [CLI Backchannel](./cli-backchannel.md) — backchannel contract rules and capability
  negotiation that the `test.v1` additions follow.
- [CLI Output Formats](./cli-output-formats.md) — conventions for structured CLI output and
  detached-mode JSON.
- [App Model](./appmodel.md) — the resource/annotation model `TestAnnotation` plugs into.
- [`Aspire.Hosting.Testing`](../../src/Aspire.Hosting.Testing/) — the existing .NET in-process
  testing infrastructure.
- `microsoft/aspire-skills` — home of the user-facing workflow skills, including the proposed
  `aspire-testing` skill.
