---
name: cli-channel-debugging
description: Emulates any Aspire CLI build identity (channel, version, commit, package source) from a locally built CLI using ASPIRE_CLI_* environment variables and the install sidecar, so a reported bug can be reproduced and fixed locally without going through the install/PR loop. Use this when asked to reproduce channel/version/quality-specific CLI behavior, simulate a daily/staging/stable/PR build locally, or decide which override knobs to set for a given scenario.
---

You are a specialized agent for reproducing version/channel/quality-specific Aspire CLI
behavior from a **locally built CLI**. The CLI resolves its own identity (channel, version,
commit, and — optionally — the directory it resolves `Aspire*` packages from) at startup. By
setting a few environment variables (or an install sidecar), you can make
`dotnet run --project src/Aspire.Cli -- <cmd>` behave as if it were any released, staging,
daily, or PR build. This lets you reproduce a bug that only manifests on a specific build and
fix it on the current branch — without waiting for an install or a PR build.

Read `docs/specs/cli-identity-sidecar.md` for the full design. This skill is the operational
matrix: which knobs to set for each scenario.

## ⛔ MANDATORY: State the scenario and exact knobs in chat before running

Before you run **any** emulated CLI command, you MUST write a short line in chat declaring:

1. **Which scenario row** (1–7b below) you are emulating.
2. **The exact environment variables and values** you are setting (channel, version, commit,
   packages directory, etc.).
3. For `ASPIRE_CLI_PACKAGES`: that you **built/populated the directory yourself** and verified
   it is clean (exactly one version of each `Aspire*` package). Do **not** hand-wave "you
   populate the directory" — actually run the build/pack, show the command, and list what
   landed there.

Example:

> Using **Scenario 2** (PR build identity + locally built packages):
> `ASPIRE_CLI_CHANNEL=pr-18087`, `ASPIRE_CLI_PACKAGES=<repo>/artifacts/packages/Release/Shipping`.
> Packed locally with `./build.sh --pack -c Release`; verified Shipping has one version of each
> `Aspire*` package.

If you cannot determine the right knobs, ask — don't guess.

## The override knobs

### Identity environment variables (process-local; the source of truth)

These are read by `IdentityResolver` and flow into `CliExecutionContext`. They change the
CLI's **global identity**, so they affect hive discovery, package-channel selection, staging
feed derivation, `--version`, telemetry identity tags, and the SDK-skew warning. They are
**stripped before the CLI spawns child Aspire processes**, so they never leak into peer probes
or `aspire doctor`'s child invocations — treat them as process-local test affordances.

| Env var | Effect |
|---|---|
| `ASPIRE_CLI_CHANNEL` | Identity channel: `stable`, `staging`, `daily`, `local`, or `pr-<N>`. Drives hive selection and staging-feed provenance. |
| `ASPIRE_CLI_VERSION` | Informational version (e.g. `13.5.0-preview.1.26310.9`). Drives version-shape (quality) decisions, version pins, the skew warning, and `--version` output. |
| `ASPIRE_CLI_COMMIT` | Source commit SHA. Drives the staging darc feed name (`darc-pub-microsoft-aspire-<sha8>`). |
| `ASPIRE_CLI_PACKAGES` | A flat directory of `.nupkg` files (e.g. `artifacts/packages/<Config>/Shipping`). The CLI synthesizes a package channel **named after `ASPIRE_CLI_CHANNEL`** that maps `Aspire*` to this directory (everything else → nuget.org), **replacing** any same-named built-in/discovered/PR channel. See the cleanliness caveat below. |
| `ASPIRE_CLI_NUGET_SERVICE_INDEX` | Replaces the canonical `https://api.nuget.org/v3/index.json` URL the CLI writes into **newly generated** `NuGet.config` files. Never rewrites URLs read from existing configs. |

When any of these is set, the CLI prints a yellow banner on **stderr**:
`Aspire CLI is emulating identity '<channel>' version '<version>'...`. Seeing that banner
confirms `IdentityOverridden` is true.

### Install sidecar (`.aspire-install.json`)

The same values can come from the `channel`, `version`, `commit`, `nugetServiceIndexOverride`,
and `packages` fields of an `.aspire-install.json` sidecar next to the CLI binary. Resolution
order per field is **env var → sidecar field → assembly-baked stamp**. Use the sidecar when
you want a persistent emulated identity for an installed binary; use env vars for one-off runs.

### Legacy PackagingService config keys (staging-feed routing only)

These predate the global env vars and are **narrower**: they only influence staging-feed
routing decisions inside `PackagingService`; they do **not** change the global identity, hive
lookups, or `--version`. Set with `aspire config set -g <key> <value>`.

| Config key | Effect |
|---|---|
| `overrideCliIdentityChannel` | Identity used for **staging-feed decisions only** (validated against the known channel set). |
| `overrideCliInformationalVersion` | Version that staging SHA-derivation and version-shape checks read. |
| `overrideStagingFeed` | Forces staging to be available and points it at an explicit feed URL. |
| `stagingPinToCliVersion` | When `true` (and using the shared feed), pins resolution to the CLI version. |

**Prefer the `ASPIRE_CLI_*` env vars for whole-identity emulation.** Use the config keys only
when you specifically want to exercise staging-feed routing from an otherwise-plain local build
(the `eng/scripts/debug-staging.sh` / `debug-stable.sh` recipes do exactly this — see
`docs/cli-staging-validation.md`).

## Running the local CLI under an emulated identity

```bash
# From the repo root. Build once, then run with the knobs set:
ASPIRE_CLI_CHANNEL=daily \
ASPIRE_CLI_VERSION=13.5.0-preview.1.26310.9 \
ASPIRE_CLI_COMMIT=95f0d2968... \
  dotnet run --project src/Aspire.Cli -- <command>
```

You can also run the already-built binary directly to skip the rebuild:
`artifacts/bin/Aspire.Cli/Debug/net10.0/aspire.dll` via `dotnet <dll> -- <command>`.

Confirm the emulation took effect: `aspire --version` prints the emulated version, and the
emulation banner appears on stderr. (Note: `aspire doctor` reports some fields from the
physical binary by design — see the spec.)

## The scenario matrix

| # | Scenario | Identity | `Aspire*` package source | How |
|---|---|---|---|---|
| 1 | Local build + locally built packages (local hive) | `local` | `~/.aspire/hives/local/packages` **or** local `Shipping` dir | `./localhive.sh` (no env vars) — or `ASPIRE_CLI_PACKAGES=<Shipping>` |
| 2 | PR build identity + locally built packages | `pr-<N>` | local `Shipping` dir | `ASPIRE_CLI_CHANNEL=pr-<N>` + `ASPIRE_CLI_PACKAGES=<Shipping>` |
| 3 | PR build identity + CI-built packages, local CLI | `pr-<N>` | `~/.aspire/hives/pr-<N>/packages` | `get-aspire-cli-pr.sh --pr <N>`, then `ASPIRE_CLI_CHANNEL=pr-<N>` |
| 4 | Latest daily, local CLI | `daily` | dnceng/dotnet9 daily feed | `ASPIRE_CLI_CHANNEL=daily` + `ASPIRE_CLI_VERSION` (+ `ASPIRE_CLI_COMMIT`) |
| 5 | Staging, unstable version | `staging` | `darc-pub-microsoft-aspire-<sha8>` feed (quality Both) | `ASPIRE_CLI_CHANNEL=staging` + prerelease `ASPIRE_CLI_VERSION` + `ASPIRE_CLI_COMMIT` |
| 6 | Staging, stable version | `staging` | `darc-pub-microsoft-aspire-<sha8>` feed (quality Stable) | `ASPIRE_CLI_CHANNEL=staging` + stable-shaped `ASPIRE_CLI_VERSION` + `ASPIRE_CLI_COMMIT` |
| 7 | Released build repro | `stable` | nuget.org | `ASPIRE_CLI_CHANNEL=stable` + released `ASPIRE_CLI_VERSION` |
| 7b | Released repro + CLI+hosting fix spanning packages | `stable` | locally rebuilt `Shipping` dir | as 7 + `ASPIRE_CLI_PACKAGES=<clean-rebuilt-dir>` |

### Scenario 1 — Local build + locally built packages

`./localhive.sh [-c Release|Debug] [-n <hive>]` packs NuGet packages into
`artifacts/packages/<Config>/Shipping`, creates `~/.aspire/hives/<hive>/packages`, and installs
the CLI to `~/.aspire/bin`. The installed CLI bakes a `local` identity and discovers the hive —
no env vars needed.

To skip the hive copy and point a dev-tree CLI straight at the packed output:

```bash
./build.sh --pack -c Release        # -> artifacts/packages/Release/Shipping
ASPIRE_CLI_PACKAGES="$PWD/artifacts/packages/Release/Shipping" \
  dotnet run --project src/Aspire.Cli -- <cmd>
```

### Scenario 2 — PR build identity + locally built packages (agent builds the packages)

You (the agent) build the packages, then run the local CLI as the PR build:

```bash
./build.sh --pack -c Release        # produces artifacts/packages/Release/Shipping
# Verify the Shipping dir is clean (one version per Aspire* package) before running.
ASPIRE_CLI_CHANNEL=pr-<N> \
ASPIRE_CLI_PACKAGES="$PWD/artifacts/packages/Release/Shipping" \
  dotnet run --project src/Aspire.Cli -- <cmd>
```

`ASPIRE_CLI_PACKAGES` wins over a same-named hive or PR-install channel, so the CLI resolves
`Aspire*` from your freshly built packages while presenting a `pr-<N>` identity.

### Scenario 3 — PR build identity + CI-built packages, local CLI

Use this to test something locally against a PR's **CI-built** packages without rebuilding them:

```bash
eng/scripts/get-aspire-cli-pr.sh --pr <N>     # populates ~/.aspire/hives/pr-<N>/packages
ASPIRE_CLI_CHANNEL=pr-<N> dotnet run --project src/Aspire.Cli -- <cmd>
```

No `ASPIRE_CLI_PACKAGES` — the local CLI discovers `~/.aspire/hives/pr-<N>` by name because its
identity channel matches. (See `docs/dogfooding-pull-requests.md` and `eng/scripts/README.md`.)

### Scenario 4 — Latest daily, local CLI

```bash
ASPIRE_CLI_CHANNEL=daily \
ASPIRE_CLI_VERSION=<daily-version> \
ASPIRE_CLI_COMMIT=<daily-sha> \
  dotnet run --project src/Aspire.Cli -- <cmd>
```

The `daily` channel maps `Aspire*` to the dnceng `dotnet9` feed. Use the **real** daily version
and commit of the build you're emulating so version pins and the skew warning match what users
saw.

### Scenarios 5 & 6 — Staging (unstable vs stable version)

```bash
# 5 — unstable (prerelease-shaped) version -> quality Both
ASPIRE_CLI_CHANNEL=staging \
ASPIRE_CLI_VERSION=13.4.0-preview.1.26280.6 \
ASPIRE_CLI_COMMIT=<full-commit-sha> \
  dotnet run --project src/Aspire.Cli -- <cmd>

# 6 — stable-shaped version -> quality Stable (the #17527 stabilizing-build scenario)
ASPIRE_CLI_CHANNEL=staging ASPIRE_CLI_VERSION=13.4.0 ASPIRE_CLI_COMMIT=<full-commit-sha> \
  dotnet run --project src/Aspire.Cli -- <cmd>
```

Staging feed provenance is derived from the commit: `darc-pub-microsoft-aspire-<sha8>`. The
version **shape** controls only which versions on that feed are eligible (Stable vs Both).
Alternatively, `eng/scripts/debug-staging.sh` / `debug-stable.sh` exercise the same routing via
the legacy config keys — see `docs/cli-staging-validation.md`.

### Scenario 7 — Released build repro

```bash
ASPIRE_CLI_CHANNEL=stable ASPIRE_CLI_VERSION=13.4.2 dotnet run --project src/Aspire.Cli -- <cmd>
```

The `stable` channel maps `Aspire*` to nuget.org. Use the exact released version the bug was
reported against.

### Scenario 7b — Released repro with a fix spanning CLI **and** hosting packages

When the fix touches packages too (e.g. `Aspire.Hosting.*`), rebuild those packages from the
current branch at the released version into a **clean** directory and layer them in:

```bash
./build.sh --pack -c Release                  # rebuild packages from this branch's source
# Stage exactly the packages you changed into a clean dir (or clean Shipping first).
ASPIRE_CLI_CHANNEL=stable \
ASPIRE_CLI_VERSION=13.4.2 \
ASPIRE_CLI_PACKAGES="$PWD/artifacts/packages/Release/Shipping" \
  dotnet run --project src/Aspire.Cli -- <cmd>
```

Identity stays `stable`/`13.4.2` while `Aspire*` resolves from your locally rebuilt packages, so
you can validate a CLI+hosting fix together before publishing anything.

## ⚠️ `ASPIRE_CLI_PACKAGES` cleanliness caveat (fail-fast)

A flat directory feed has no "latest stable vs latest prerelease" semantics: if the same
`Aspire*` package id appears with **more than one version**, NuGet would silently resolve the
highest and mask the version you meant to test. The CLI therefore **fails fast** with:

> The Aspire CLI packages override directory '<dir>' contains more than one version of the same
> Aspire package... Clean the directory so each `Aspire*` package appears exactly once.
> Conflicts: Aspire.Hosting (13.4.1, 13.4.2).

This commonly bites because **`./localhive.sh` auto-generates a fresh `local.YYYYMMDD.tHHmmss`
version each run and accumulates them in `artifacts/packages/<Config>/Shipping`**. Before
pointing `ASPIRE_CLI_PACKAGES` at `Shipping`, clean stale `.nupkg` files (or repack fresh) so
each `Aspire*` id has exactly one version. The directory must also exist (missing → fail-fast).

## Reproduce → fix → validate loop

1. Identify the reported build (channel + version + commit) and which scenario row it maps to.
2. **State the scenario and exact knobs in chat** (mandatory section above).
3. Reproduce the bug with the local CLI under emulation.
4. Fix on the current branch.
5. Re-run the same emulated command and confirm the fix; keep the env vars identical so the
   before/after is apples-to-apples.

## References (don't duplicate — link to these)

- `docs/specs/cli-identity-sidecar.md` — identity resolution design, sidecar schema, the full
  list of `ASPIRE_CLI_*` overrides including `ASPIRE_CLI_PACKAGES`.
- `docs/cli-staging-validation.md` — staging/stable feed routing and the `debug-staging.sh` /
  `debug-stable.sh` recipes.
- `docs/dogfooding-pull-requests.md` + `eng/scripts/README.md` — installing PR builds with
  `get-aspire-cli-pr.sh`.
- `localhive.sh` (repo root) — build packages + CLI + hive locally.
