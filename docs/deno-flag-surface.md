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
   write, run, env, sys, ffi; allow before deny)
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

`deno serve` has its own `--port`/`--host` flags, but Aspire allocates the port and communicates it
via the injected `PORT` environment variable and endpoint annotations. Passing `--port` (through
`WithDenoRuntimeArgs`) overrides Aspire's allocation and desyncs the endpoint the dashboard/service
discovery advertise from the port the process actually binds. Bind to the injected `PORT` env var
instead; `WithDenoServe()` deliberately exposes no port method.

### 4. `--inspect*` versus Aspire/VS Code debugging

`AddDenoApp` already wires a `deno` debug launch configuration (`SupportsDebuggingAnnotation`) that the
Aspire IDE extension uses to attach an inspector. `WithDenoInspect*` is provided for callers who launch
Deno's inspector manually (e.g. an external Chrome DevTools session), but the two paths can contend for
the same inspector port. Do not combine a manual `WithDenoInspectBrk()` with IDE-driven debugging on
the same resource.

### 5. `--watch` / `--watch-hmr` in published containers

Watch/HMR are run-mode developer conveniences. The value is honored for the run-mode command line and
is also reflected in the generated Dockerfile entrypoint for fidelity, but file-watching has no useful
meaning in an immutable published container image and should be treated as run-mode only.

### 6. `deno task` permission flags

For `deno task`, permissions are defined by the task's own command inside `deno.json`, not on the
`deno task` invocation. `WithDenoTask(...)` therefore intentionally does **not** emit permission flags
(`--allow-*`/`--deny-*`); configure them in the task definition. Resolution flags (`--config`,
`--import-map`, `--lock`, `--node-modules-dir`) and `--unstable-*` are still emitted because they are
valid `deno task` options.

### 7. Interactive / TTY-oriented flags

Flags that assume an interactive terminal (for example `deno run` prompting for permissions when a
grant is missing) do not apply: Aspire runs the process non-interactively with stdout/stderr captured
for the dashboard. Always grant the permissions the workload needs explicitly (or use `-A`) rather than
relying on interactive permission prompts.

## Published container image

`AddDenoApp` generates a multi-stage Dockerfile (when the app directory has no hand-written
`Dockerfile`) tuned for Deno's execution model:

- **Dependency pre-caching.** The build stage runs `deno cache <entrypoint>` (or
  `deno cache --frozen <entrypoint>` when a `deno.lock` is present) to resolve the entrypoint's full
  module graph — remote URLs and `npm:`/`jsr:` specifiers — into `DENO_DIR`. `DENO_DIR` is pinned to
  `/deno-dir` in both stages and copied `--from=build` into the runtime stage, so the image starts
  offline / air-gapped and avoids a cold-start dependency fetch. There is no `node_modules` stage:
  Deno caches under `DENO_DIR` rather than a project-local folder.
- **`NODE_ENV=production`.** Set in the runtime stage (and in run mode via the resource defaults) so
  Deno's Node-compatibility mode — `npm:` resolution and package.json `exports` conditions — behaves
  like the Node/Bun variants.
- **Native OpenTelemetry.** `OTEL_DENO=true` is exported by default and flows to the OTLP endpoint
  configured by `WithOtlpExporter`. Native OTel is **stable** on the Deno versions the pinned
  `denoland/deno:2` tag resolves to (verified on Deno 2.9.0), so **no `--unstable-otel` flag is
  emitted** — the env var alone activates trace/metric/log export. The run-mode command and the
  published container entrypoint stay consistent.

## Escape hatch

Any Deno flag without a dedicated method (for example `--v8-flags=...`, `--seed`, `--cached-only`,
`--reload`, `--env-file`) can be injected verbatim before the entrypoint with `WithDenoRuntimeArgs(...)`,
giving full parity with `AddExecutable("name", "deno", workdir, args...)`. This is unvalidated by
design; the conflicts documented above still apply when the injected flag overlaps Aspire-managed
concerns.
