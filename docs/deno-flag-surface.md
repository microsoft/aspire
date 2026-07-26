# Deno flag surface for `AddDenoApp`

`AddDenoApp` maps the Deno CLI onto Aspire's resource model. The fluent `WithDeno*` methods on
`IResourceBuilder<DenoAppResource>` let a caller express the full Deno runtime flag surface, so a Deno
workload no longer has to fall back to a raw `AddExecutable("name", "deno", workdir, args...)`.

## Emitted command shape

```text
deno <run|task|serve> [runtime-flags] <entrypoint|task> [script-args]
```

Runtime flags are emitted in a fixed, valid-CLI order regardless of the order the fluent methods are
called:

1. Permissions — `-A`/`--allow-all` or granular `--allow-*`/`--deny-*` (category order: net, read,
   write, run, env, import, sys, ffi; allow before deny)
2. Resolution — `--config`, `--import-map`, `--lock`/`--no-lock`, `--node-modules-dir[=mode]`
3. `--unstable-*`
4. `--watch` / `--watch-hmr`
5. `--inspect` / `--inspect-brk` / `--inspect-wait` (optional `host:port`)
6. Raw runtime args (`WithDenoRuntimeArgs`, escape hatch)

Then the entrypoint (or task name), then script args (`WithDenoScriptArgs`).

## Backward compatibility

A bare `AddDenoApp(name, workdir, entrypoint)` with no `WithDeno*` calls still emits
`deno run -A <entrypoint>`. The blanket `-A` grant is emitted by default (tri-state `AllowAll`): it is
kept unless the caller either opts out with `WithDenoAllowAll(false)` or configures at least one
granular `--allow-*` flag, in which case least-privilege is assumed and `-A` is dropped.

## Working directory and environment variables

These are **not** Deno flags in Aspire's model:

- **Working directory** is the `appDirectory` passed to `AddDenoApp` (the resource's
  `WorkingDirectory`). Deno inherits the process cwd from Aspire, so there is no `--cwd` method — a
  separate `--cwd` would desync from the resource working directory. For `deno task`, use standard
  Aspire working-directory configuration rather than injecting `--cwd` via `WithDenoRuntimeArgs`.
- **Environment variables** are set with the standard `WithEnvironment(...)`. Do not use Deno's
  `--env-file`: Aspire owns env injection (service discovery, `OTEL_*`, `PORT`, cert paths), and a
  Deno-side dotenv load runs outside Aspire's ordering and can silently shadow injected values.

---

## Capabilities Aspire's resource model cannot (safely) express

The following are genuine limitations or conflicts with how Aspire injects endpoints, environment
variables, service discovery, and debugging. They are surfaced here rather than as dedicated methods.

### 1. Least-privilege `--allow-net` with a fixed host list

Aspire allocates endpoint ports dynamically in run mode and injects service-discovery hosts/ports as
environment variables at launch. A hard-coded `WithDenoAllowNet("host:port", ...)` cannot enumerate
those dynamic targets ahead of time, so outbound calls to Aspire-discovered services (or inbound
binding to a randomly-allocated endpoint port) may be blocked with `NotCapable`. Use `-A` (default),
or `WithDenoAllowNet()` with **no** value list (allow all hosts), when the app relies on Aspire
service discovery. A scoped allow-list is only safe for endpoints the caller controls end-to-end.

### 2. Least-privilege `--allow-env` with a fixed variable list

Aspire injects a broad, partly non-deterministic set of environment variables (`OTEL_*`,
`OTEL_EXPORTER_OTLP_*`, `DENO_CERT`/`DENO_TLS_CA_STORE`, `PORT`, service-discovery keys). Restricting
`--allow-env` to an application-authored subset makes the runtime and injected integrations throw when
they read an un-granted variable. A scoped `WithDenoAllowEnv(...)` must include every Aspire-injected
key the app or runtime reads, which the caller generally cannot know statically. Prefer `-A` or an
unscoped `WithDenoAllowEnv()`.

### 3. `deno serve --port` / `--host`

`WithDenoServe()` allocates an endpoint and emits the matching `--host`/`--port` arguments itself, because
a `deno serve` handler cannot choose its own address — it exports a `fetch` handler and Deno owns the
listener. Verified on Deno 2.9.0: `PORT=9911 deno serve -A s.ts` still binds `0.0.0.0:8000`, so a serve
entrypoint does not read `PORT` and must be told the port on the command line.

Passing `--port`/`--host` again through `WithDenoRuntimeArgs` does not merely duplicate the flag — Deno rejects
it outright (`error: the argument '--port <port>' cannot be used multiple times`, verified on 2.9.0), so the
resource fails to start. This is one instance of the general rule in
[Managed flags and `WithDenoRuntimeArgs`](#managed-flags-and-withdenoruntimeargs) below. To change the port,
configure the endpoint (for example `WithHttpEndpoint(port: 5005)`) and let `WithDenoServe()` project it;
`WithDenoServe()` deliberately exposes no port method of its own.

The injected `PORT` environment variable remains relevant for `deno run` entrypoints, which create their
own listener (`Deno.serve({ port: Number(Deno.env.get("PORT")) }, ...)`).

### 4. `--inspect*` versus Aspire/VS Code debugging

`AddDenoApp` already wires a `deno` debug launch configuration (`SupportsDebuggingAnnotation`) that the
Aspire IDE extension uses to attach an inspector. `WithDenoInspect*` is provided for callers who launch
Deno's inspector manually (e.g. an external Chrome DevTools session), but the two paths can contend for
the same inspector port. Do not combine a manual `WithDenoInspectBrk()` with IDE-driven debugging on
the same resource.

IDE debugging only covers direct entrypoints (`deno run` / `deno serve`). Deno rejects runtime inspector
flags on the `deno task` sub-command, so a task entrypoint (`WithDenoTask(...)`, or `WithRunScript(...)`
with the default Deno package manager) cannot be launched under the debugger and the extension reports
that debugging is unsupported for that resource. To debug a task, run its underlying script through
`AddDenoApp(...)` with the entrypoint directly, or start the inspector inside the task definition in
`deno.json` and attach manually.

### 5. `--watch` / `--watch-hmr` in published containers

Watch/HMR are run-mode developer conveniences. The value is honored for the run-mode command line and
is intentionally omitted from the generated Dockerfile entrypoint because file-watching has no useful
meaning in an immutable published container image.

### 6. `deno task` permission flags and publish cache inference

For `deno task`, permissions are defined by the task's own command inside `deno.json`, not on the
`deno task` invocation. `WithDenoTask(...)` therefore intentionally does **not** emit permission flags
(`--allow-*`/`--deny-*`); configure them in the task definition. Resolution flags (`--config`,
`--lock`, `--node-modules-dir`) and `--unstable-*` are still emitted because they are valid
`deno task` options; `--import-map` is intentionally omitted because Deno rejects it on `deno task`.

Published task entrypoints are opaque to Aspire because a task can run any shell command, including
another Deno command, a package-manager command, or multiple chained commands. The generated
Dockerfile therefore does not try to pre-cache an inferred task module graph. If a task-published
image needs offline startup, define the task so it uses dependencies that are already present in the
image or author a custom Dockerfile.

### 7. Interactive / TTY-oriented flags

Flags that assume an interactive terminal (for example `deno run` prompting for permissions when a
grant is missing) do not apply: Aspire runs the process non-interactively with stdout/stderr captured
for the dashboard. Always grant the permissions the workload needs explicitly (or use `-A`) rather than
relying on interactive permission prompts.

## Published container image

`AddDenoApp` generates a multi-stage Dockerfile (when the app directory has no hand-written
`Dockerfile`) tuned for Deno's execution model:

- **Dependency pre-caching for direct entrypoints.** The build stage runs `deno cache <entrypoint>`
  (or `deno cache --frozen <entrypoint>` when a `deno.lock` is present) for direct `run`/`serve`
  entrypoints to resolve the entrypoint's module graph — remote URLs and `npm:`/`jsr:` specifiers —
  into `DENO_DIR`. `DENO_DIR` is pinned to `/deno-dir` in both stages and copied `--from=build` into
  the runtime stage. Direct `run`/`serve` published entrypoints add `--cached-only`, so missing
  dependencies fail fast instead of fetching from the network at container start — unless the caller
  selects their own cache policy (see
  [Managed flags and `WithDenoRuntimeArgs`](#managed-flags-and-withdenoruntimeargs)). `deno task`
  entrypoints skip this pre-cache because Aspire cannot infer the task's actual module graph.
- **Build context hygiene.** The generated per-Dockerfile `.dockerignore` excludes local
  `node_modules` folders. Deno can materialize `node_modules` during the build for npm compatibility,
  but host-local dependency folders should not leak into the container build context.
- **`NODE_ENV=production`.** Set in the runtime stage (and in run mode via the resource defaults) so
  Deno's Node-compatibility mode — `npm:` resolution and package.json `exports` conditions — behaves
  like the Node/Bun variants.
- **Native OpenTelemetry.** `OTEL_DENO=true` is exported by default and flows to the OTLP endpoint
  configured by `WithOtlpExporter`. Native OTel is **stable** on the pinned Deno 2.9.0 image, so
  **no `--unstable-otel` flag is emitted** — the env var alone activates trace/metric/log export.
  The run-mode command and the published container entrypoint stay consistent.

## Escape hatch

Any Deno flag without a dedicated method (for example `--v8-flags=...`, `--seed`, `--cached-only`,
`--reload`, `--env-file`) can be injected verbatim before the entrypoint with `WithDenoRuntimeArgs(...)`,
giving full parity with `AddExecutable("name", "deno", workdir, args...)`. This is unvalidated by
design, except where the injected flag overlaps an Aspire-managed concern.

## Managed flags and `WithDenoRuntimeArgs`

Deno treats most of the flags Aspire manages as single-occurrence, so supplying one again through
`WithDenoRuntimeArgs` is a hard parse error rather than an override. All of the following were verified
on Deno 2.9.0:

| Managed flag | Emitted by | Conflict on 2.9.0 |
| --- | --- | --- |
| `--host`, `--port` | `WithDenoServe()` | `cannot be used multiple times` |
| `--config` | `WithDenoConfig(...)` | `cannot be used multiple times` |
| `--import-map` | `WithDenoImportMap(...)` | `cannot be used multiple times` |
| `--lock`, `--no-lock` | `WithDenoLock(...)` / `WithDenoNoLock()` | `cannot be used multiple times` / `cannot be used with` |
| `--node-modules-dir` | `WithDenoNodeModulesDir(...)` | `cannot be used multiple times` |
| `--watch`, `--watch-hmr` | `WithDenoWatch(...)` | `cannot be used multiple times` / `cannot be used with` |
| `--inspect`, `--inspect-brk`, `--inspect-wait` | `WithDenoInspect*()` | `cannot be used multiple times` |

Aspire detects these ahead of time and throws with actionable guidance naming the managed flag, the
API that emits it, and the supported way to configure it — instead of letting the raw Deno parser error
surface at container start. Both `--flag value` and `--flag=value` spellings are detected.

The check is scoped to what each mode actually emits. `deno task` entrypoints emit config, lock, and
node-modules-dir flags but **not** `--import-map` or the debugging flags, so those remain available
through `WithDenoRuntimeArgs` in task mode.

Repeatable flags are deliberately **not** treated as conflicts, because Deno accepts them: `-A` alongside
`--allow-all` is fine, and `--allow-read=/tmp --allow-read=/var` merges rather than erroring.

### Cache policy is suppressed, not rejected

`--cached-only` is the one managed flag that does not error when duplicated — but it silently wins.
Verified against a real `jsr:` import on 2.9.0, `--cached-only --reload` is accepted and `--reload`
becomes a no-op; with a cold cache the run fails identically with or without it:

```text
error: Specifier not found in cache: "https://jsr.io/@std/fmt/1.0.10/colors.ts", --cached-only is specified.
```

Because `--cached-only` is an Aspire default (hermetic builds) rather than a caller requirement, Aspire
**omits** it when `WithDenoRuntimeArgs` contains `--reload`, `-r`, or an explicit `--cached-only`, so the
caller's cache policy takes effect instead of being silently discarded. `--no-remote` is not treated as a
cache policy — it is orthogonal to caching.

