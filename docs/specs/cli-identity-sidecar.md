# Aspire CLI identity sidecar

> Pairs with `docs/specs/install-routes.md` (sidecar physical contract) and is part of the broader packaging-service rethink tracked by [#17580](https://github.com/microsoft/aspire/issues/17580). Lifts the version-on-context work tracked by [#17750](https://github.com/microsoft/aspire/issues/17750).

## Implementation status

Tracked in PR [#18087](https://github.com/microsoft/aspire/pull/18087). The resolver and the full call-site migration have **landed together** as the spec requires.

- **Resolver + `CliExecutionContext`.** `IIdentityResolver` (`IdentityResolver`) composes env var → sidecar → assembly fallback per field. `CliExecutionContext` exposes `IdentityChannel`, `IdentityVersion`, `IdentityCommit`, plus two derived members beyond the original shape below: `IdentitySdkVersion` (`IdentityVersion` with `+build` metadata stripped — absorbs the old `VersionHelper.GetDefaultSdkVersion()` logic) and `IdentityOverridden` (true when any field came from env or sidecar, used to drive the startup notice).
- **Call-site migration — complete.** Every identity-conditional decision now reads `CliExecutionContext` (`PackagingService` staging feed/version, `PackageChannel` template filter, `New`/`Add`/`Update` commands, `TemplateNuGetConfigService`, `InitCommand`/`ScaffoldingService` version stamping, `GuestAppHostProject` skew warning, `ExtensionRpcTarget`, SDK/skills generation). Physical-binary reads stay on the assembly and are annotated `// physical-binary-version-by-design (see docs/specs/cli-identity-sidecar.md)`.
- **Identity-conditional reads — documented convention.** Physical-binary reads are annotated `// physical-binary-version-by-design (see docs/specs/cli-identity-sidecar.md)` at each site, and `VersionHelper` documents that its `GetDefault*Version` helpers bypass identity. The invariant ("identity-sensitive version decisions read `CliExecutionContext`") is enforced by review rather than an automated guardrail. An earlier grep-based test that scanned `src/Aspire.Cli/` for stray physical reads was removed as too brittle (regex over source plus a file-level allow-list).
- **Telemetry split — done.** Binary `cli.version` / `cli.build_id` are kept; `identity.version` / `identity.channel` (and `identity.commit` when non-empty) are emitted alongside.
- **`--version` — overridden.** A custom version action (`Commands/IdentityVersionAction.cs`) prints `IdentityVersion` with the optional `IdentityCommit` appended as build metadata (e.g., `13.4.5+73114e86c64aeb9f3f3c7da8e37df1ae4281b27e`), so emulated runs report the emulated version and commit. The non-override output is byte-identical to the System.CommandLine default.
- **Startup override notice — added.** When `IdentityOverridden` and output is human-readable, a yellow notice on stderr makes a diagnostic run impossible to mistake for a real one.
- **`aspire doctor` — physical by design.** Doctor reports the *physical* install truth (like `--self`); emulation is surfaced by the startup notice rather than rewiring doctor's assembly-backed channel read (which `DoctorCommandTests` pin). `InstallationDiscovery` and `AspireVersionCheck.TryReadIdentityChannel` are annotated accordingly.
- **`ASPIRE_CLI_PACKAGES` packages-directory override — landed.** Resolves (env → sidecar → null) to `CliExecutionContext.IdentityPackagesDirectory`. When set, `PackagingService.GetChannelsAsync` registers a synthesized channel (named after the identity channel) that routes `Aspire*` to the directory and replaces any same-named channel; a fail-fast guardrail rejects a missing directory or duplicate `Aspire*` versions. Documented for repro workflows by the `cli-channel-debugging` skill.
  - **Resolution honors the override under any emulated channel name — fixed.** `aspire new` template resolution (`TemplateNuGetConfigService`) and `aspire add` package search/version-match (`IntegrationPackageSearchService`, `AddCommand`) previously only searched the synthesized local channel when its name looked like a local build (`local`/`pr-<N>`/`run-<N>` via `VersionHelper.IsLocalBuildChannel`). Emulating `stable`/`staging`/`daily` with `ASPIRE_CLI_PACKAGES` therefore fell back to nuget.org. Resolution now recognizes a channel as locally backed by its **mappings** (`PackageChannel.IsBackedByLocalPackageDirectory` — an `Aspire*` mapping pointing at an existing directory) and treats a set `IdentityPackagesDirectory` as a hive signal, so the override works under every emulated identity.
  - **Buildability caveat (all-local "future release").** A `stable` channel deliberately drops no per-project `NuGet.config`. When the emulated local version is not also on nuget.org, the apphost still cannot **build** until the directory is registered as an *ambient* NuGet source, because MSBuild resolves `Aspire.AppHost.Sdk` before restore and reads only `NuGet.config` sources (it ignores `RestoreAdditionalProjectSources`/`ASPIRE_CLI_PACKAGES`). This mirrors how a real `stable` build relies on nuget.org being ambient; see the `cli-channel-debugging` skill (Scenario 7c).
  - **All-local "future release" tooling + E2E coverage — landed.** `localhive.{sh,ps1}` gained a `--version <X.Y.Z>` (`-Version`) flag that packs a stable-shaped build (`/p:VersionPrefix=X.Y.Z /p:StabilizePackageVersion=true`) and stages only those exact packages into the hive. `EmulatedLocalReleaseBuildTests` (C# + TypeScript) consumes such an archive, emulates `stable` with `ASPIRE_CLI_PACKAGES` at the hive, and asserts `aspire new`/`aspire add` resolve the future-only version locally (registering the hive as an ambient NuGet source for buildability). The tests skip unless given a stable-shaped `LocalHive` archive, so default CI cost is zero.

## Goal

Stop encoding the CLI's **identity** — its channel, its version, and its commit — inside the executable, and resolve it at runtime from the install-route sidecar (`.aspire-install.json`) with environment-variable overrides. One binary, many identities.

## Why we are doing this

### What 13.4 actually cost us

The 13.4 release cycle surfaced an entire class of late-stage shipping bugs that all shared one shape: **the bits we tested in PR/daily were not the bits that ran in staging or stable, because the CLI's identity was baked in at build time and the identity changed how the CLI behaved.**

Three concrete examples from the cycle:

- **[#17527](https://github.com/microsoft/aspire/issues/17527)** — the staging build of the CLI baked `channel=stable` because the CI parameter wasn't threaded through. `aspire init` then dropped a `NuGet.config` that didn't include the staging feed, and projects resolved the wrong Aspire package versions. **The bug was undetectable before staging shipped, because no PR or daily binary ever runs with `channel=stable` baked in.**
- **[#17596](https://github.com/microsoft/aspire/issues/17596)** — `aspire new` on a daily-channel CLI resolved `Aspire.ProjectTemplates 13.3.5` from nuget.org instead of the daily 13.5 prerelease. Again driven by identity-keyed code paths that PR builds never exercise.
- **[#15511](https://github.com/microsoft/aspire/issues/15511)** — `SuppressFinalPackageVersion` caused E2E test failures on release branches because version-keyed behavior diverged between branches.

These weren't logic bugs in any individual code path. They were **identity-conditional code paths whose conditional was unreachable in any pre-release test environment**. The CLI shipped to customers with code that had never run on a developer's machine and had never run in CI under the identity it would actually adopt in production.

### What stamping identity into the executable forces on us

Today the CLI's channel is baked at build time as `[AssemblyMetadata("AspireCliChannel", "<value>")]` and read by `IdentityChannelReader`. Version and commit are baked into `AssemblyInformationalVersion`. This has three consequences we no longer want:

1. **Identity is welded to bytes.** A `daily` binary cannot, even for testing, behave like a `stable` binary, because changing identity requires a rebuild. Reproducing a user-reported issue against a shipped CLI version requires checking out that SHA and rebuilding with the right `/p:AspireCliChannel=`.
2. **Identity-conditional behavior cannot be exercised pre-release.** Any code path that branches on channel, version, or commit only executes under the identity that was baked in. The staging-only and stable-only branches stay dark in PR and daily testing.
3. **CI carries the burden of producing N binaries instead of 1.** Every release requires per-channel builds because the channel is part of the binary's identity. Per-PR binaries are the same bytes-modulo-a-string, but we still rebuild them.

### What hoisting identity into a sidecar gives us

A dev binary can be told, by environment variable or sidecar, "you are channel=stable, version=13.4.0, commit=abc123". The identity-conditional code paths then execute against that identity, on a developer's machine, before anything ships. The same binary, run with different identities, exercises different shipping behaviors — and the diff between those runs is the diff customers will see at GA.

This is the same shift the broader packaging rethink ([#17580](https://github.com/microsoft/aspire/issues/17580)) calls for: "**CLI identity moves to a co-located sidecar written at install time — same binary across channels, enables local 'fake stable / staging' debugging without a rebuild.**" This spec is the identity half of that shift. The packaging-feed half (per-build static feeds) is independent and lands later.

## Design

### Sidecar schema (extension to `.aspire-install.json`)

`docs/specs/install-routes.md` already defines `.aspire-install.json` as the route-identifying sidecar. This spec extends that file with five optional identity / emulation fields:

```json
{
  "source":  "script",                                  // existing, required — install route
  "channel": "stable",                                  // new, optional — packaging/quality identity
  "version": "13.4.0",                                  // new, optional — informational version
  "commit":  "abc123def…",                              // new, optional — source revision
  "nugetServiceIndexOverride": "http://127.0.0.1:5400/v3/index.json",  // new, optional — see below
  "packages": "/path/to/artifacts/packages/Release/Shipping"  // new, optional — see below
}
```

All five new fields are **optional**. When absent, the resolver falls back to the assembly-baked value (channel/version/commit) or to no-override (nugetServiceIndexOverride, packages) so existing installs continue to work unchanged during the migration window.

#### `nugetServiceIndexOverride`

The `nugetServiceIndexOverride` field replaces the `https://api.nuget.org/v3/index.json` URL that the CLI writes into newly-generated `NuGet.config` files (via `aspire init`, `aspire new`, and any other config-emitting path). When set, every emission point that would otherwise hard-code `api.nuget.org` instead writes the override URL. **The override only affects URLs the CLI *writes*; it never rewrites URLs the CLI *reads* from existing user configs.** That asymmetry is intentional — the override exists to let a Test Bench session (see #17824) point the CLI at a local NuGet proxy (see #17823) without touching the customer's `NuGet.config` that's actually under test.

The CLI exposes the override on demand via a small API on `CliExecutionContext`:

```csharp
/// <summary>
/// Returns the NuGet service-index URL the CLI should write into newly-generated NuGet.config files,
/// or null when no override is active (in which case callers use the canonical https://api.nuget.org/v3/index.json).
/// Never use this to rewrite URLs the CLI reads from existing user configs.
/// </summary>
public string? NuGetServiceIndexOverride { get; }
```

Call sites that today emit `api.nuget.org` directly (`NuGetConfigMerger`, `TemplateNuGetConfigService`, etc.) consult this property; when it returns non-null, the emitted config carries the override URL. When null, the existing canonical URL is used. This is the bytes-side half of the identity story tracked separately in #17823; the API and the sidecar/env plumbing land **now** so that #17823's proxy work has a stable consumer surface to slot into.

#### `packages`

The `packages` field points the CLI at a **flat directory of `.nupkg` files** (for example `artifacts/packages/Release/Shipping`). When set, the CLI synthesizes a package channel — named after the resolved identity channel — that maps `Aspire*` package globs to this directory (everything else resolves from nuget.org). This synthesized channel **replaces** any same-named built-in, discovered-hive, or PR-install channel, so the CLI resolves Aspire packages from the directory while still presenting the emulated channel/version/commit identity. It is exposed on `CliExecutionContext` as `IdentityPackagesDirectory` (a `DirectoryInfo?`, null when no override is active) — distinct from the existing `PackagesDirectory`, which is the restore cache.

This lets a locally built CLI test against packages it just built (or against a PR's CI-built packages staged into a directory) without copying them into a hive. It pairs with `ASPIRE_CLI_CHANNEL` for the "PR build identity + locally built packages" and "released repro with a CLI+hosting fix spanning packages" scenarios (see the `cli-channel-debugging` skill for the full matrix).

**Cleanliness guardrail (fail-fast).** A flat directory has no "latest stable vs latest prerelease" semantics: if the same `Aspire*` package id appears with more than one version, NuGet would silently resolve the highest and mask the intended version. The CLI therefore **fails fast** at channel-resolution time when the directory is missing, or when any `Aspire*` package id carries more than one distinct version, with a diagnostic listing the conflicting ids and versions (e.g. `Aspire.Hosting (13.4.1, 13.4.2)`). Only `Aspire*` packages are checked because the synthesized channel only routes those to the directory. This is intentional friction — `localhive.sh` accumulates auto-versioned builds in `Shipping`, so the directory must be cleaned (or freshly packed) before it can be used as an override.

### Resolution order

Each identity field is resolved **independently** so you can override one and inherit the rest. Resolution order, highest precedence first:

1. **Environment variable**: `ASPIRE_CLI_CHANNEL`, `ASPIRE_CLI_VERSION`, `ASPIRE_CLI_COMMIT`, `ASPIRE_CLI_NUGET_SERVICE_INDEX`, `ASPIRE_CLI_PACKAGES`.
2. **Sidecar field**: the matching field in `.aspire-install.json` next to the running binary.
3. **Terminal fallback**:
   - `channel` → `local`.
   - `version` and `commit` → the value read from `AssemblyInformationalVersion` (still stamped by Arcade for every assembly; this is not stamping we control or intend to remove).
   - `nugetServiceIndexOverride` → `null` (no override; callers use the canonical `https://api.nuget.org/v3/index.json`).
   - `packages` → `null` (no override; the CLI uses its normal channel/hive package sources).

The resolver distinguishes four outcomes per field, each surfaced in `aspire doctor --self`: `from-env`, `from-sidecar`, `from-assembly-fallback`, `defaulted-to-local`. This makes "is my override actually taking effect?" trivially debuggable and prevents the soft-fallback class of bug the critique flagged (a missing installer write silently looking healthy because the resolver fell back to a baked value).

### Env-var scope: process-local, not inherited

`ASPIRE_CLI_CHANNEL`, `ASPIRE_CLI_VERSION`, `ASPIRE_CLI_COMMIT`, `ASPIRE_CLI_NUGET_SERVICE_INDEX`, and `ASPIRE_CLI_PACKAGES` are **deliberately not propagated to child Aspire processes**. The CLI strips them from the environment of:

- Peer Aspire CLIs spawned by `aspire doctor` install discovery (`PeerInstallProbe`).
- AppHost child processes launched by `aspire run` / `aspire start` and friends, unless the override is also passed via a separate explicit opt-in (TBD; see open questions).
- Any other Aspire-CLI subprocess invocation.

This is essential because the overrides exist to make a single binary lie about its identity *for this process invocation only*. A peer probe under `ASPIRE_CLI_CHANNEL=staging` set by a user shell must still report each peer's real identity, not the parent's override. Without stripping, `aspire doctor` becomes useless under any developer's shell with the override set.

Stripping happens in the CLI's process-launch helpers (`ProcessExecutionFactory` / `DotNetCliRunner` / `PeerInstallProbe` / etc.) at the env-dictionary construction site, before the child process is started. Tests assert peer identity is preserved when the parent shell has overrides set.

### Why a separate file is rejected

We considered shipping a second sidecar (`aspire-cli.identity.json`) so route identity and CLI identity stayed in separate files. It was rejected because:

- Every install route already writes `.aspire-install.json`. Adding a second file doubles the per-route write contract and doubles the failure surface (one file written, one not).
- Route and identity are written together at install time. There is no scenario where one is known and the other is not.
- Discovery (`aspire doctor`) already reads one sidecar per candidate binary. Reading two doubles the I/O for no signal.

The `aspire.config.json` project file was also rejected: it describes what a *project* wants, not what the *CLI binary* is. Conflating the two is precisely the bug class that motivates this spec.

### `CliExecutionContext` shape, and the call-site migration

`CliExecutionContext` already exposes `IdentityChannel`. It gains two parallel properties resolved by the same pipeline:

```csharp
public string IdentityChannel { get; }   // existing
public string IdentityVersion { get; }   // new
public string IdentityCommit  { get; }   // new
```

**The validation patterns this spec exists to enable only work if every identity-sensitive call site reads from `CliExecutionContext`, not from the assembly.** A call site that reads `AssemblyInformationalVersion` directly bypasses the resolver and silently ignores `ASPIRE_CLI_VERSION`. An audit of the current code surfaces several such sites that must migrate in lockstep with the resolver landing:

- `PackagingService.GetStagingFeedUrl()` — reads `AssemblyInformationalVersionAttribute` directly to extract the commit for the `darc-pub-microsoft-aspire-<sha>` feed name. Migrate to `CliExecutionContext.IdentityCommit`.
- `PackagingService.GetStagingPinnedVersion()` — uses `VersionHelper.GetDefaultTemplateVersion()`. Migrate to `CliExecutionContext.IdentityVersion`.
- `TelemetryManager` and `AspireCliTelemetry.GetCliVersion()` / `GetCliBuildId()` — see the telemetry section below; these get **both** identities surfaced separately rather than a single migration.
- Multiple commands and template factories use `VersionHelper.GetDefaultTemplateVersion()` / `GetDefaultSdkVersion()`. Each call site is reviewed: identity-conditional reads migrate to `CliExecutionContext.IdentityVersion`; physical-binary reads (e.g., compatibility checks against bundled packages) stay on the assembly value.

To prevent regression, a unit test in `tests/Aspire.Cli.Tests/` greps the `src/Aspire.Cli/` tree for direct uses of `AssemblyInformationalVersionAttribute`, `[AssemblyMetadata(...)]` reads, and `VersionHelper.GetDefaultTemplateVersion()`, and fails on any unexpected hit. The allow-list is the resolver implementation itself plus a small number of bundled-package-compat call sites annotated with a comment explaining why they read the physical binary version. This test is part of the resolver PR — landing it forces the migration to be exhaustive rather than aspirational.

The existing `IIdentityChannelReader` interface widens to `IIdentityResolver` with one method per field; `IdentityChannelReader` becomes the default implementation backed by the sidecar reader + env var + terminal fallback. Tests continue to substitute a fake implementation.

### Validation

Identity overrides come from developer-controlled inputs — an `ASPIRE_CLI_*` env var or a hand-authored `.aspire-install.json`. The resolver validates the **shape** of each typed field at resolve time so a typo fails fast with a diagnostic naming the source, rather than silently producing a bogus staging-feed name or an unrestorable `NuGet.config` URL:

- **`version`** (`ASPIRE_CLI_VERSION` / sidecar) must parse as a strict SemVer 2.0 version (e.g. `13.4.3`, `13.5.0-preview.1.26311.9`, with optional `+build` metadata) — the same parser the rest of the CLI uses for package versions.
- **`commit`** (`ASPIRE_CLI_COMMIT` / sidecar) must be a hexadecimal SHA of 7–64 characters, since its only behavioral use is deriving the `darc-pub-microsoft-aspire-<sha8>` staging-feed name.
- **`nugetServiceIndexOverride`** (`ASPIRE_CLI_NUGET_SERVICE_INDEX` / sidecar) must be an absolute `http(s)` URL, because it is written verbatim into generated `NuGet.config` files as a v3 service index.

**`channel` is deliberately not validated** when it comes from env or sidecar. Developer overrides and tests routinely use bespoke labels (e.g. `pr-17580`) that are not in the built-in set, and rejecting them would defeat the override's purpose. Only the assembly-baked channel — the one input we control end-to-end — is validated against the accepted shape (`stable`, `staging`, `daily`, `local`, or `pr-<N>` where `<N>` is one or more ASCII digits) by `IdentityChannelReader`; a malformed stamp falls through to the terminal default (`local`) rather than crashing the resolver.

**`packages`** (`ASPIRE_CLI_PACKAGES` / sidecar) is validated where it is consumed: `PackagingService` fails fast if the directory is missing or if any `Aspire*` id appears with more than one version (see the cleanliness guardrail above).

The assembly-baked fallback values for `version` / `commit` are trusted (Arcade stamps them) and are never routed through these checks.

## Developer workflows: `dotnet run`, `startvs`, `localhive`

A core constraint: this change **must not break** `dotnet run --project src/Aspire.Cli -- <command>` or any of the in-tree dev loops that depend on it (VS debugger via `startvs.cmd`, `./build.sh` + run-from-artifacts, `localhive.{sh,ps1}`).

The matrix:

| Workflow | Today | After this change | Outcome |
|---|---|---|---|
| `dotnet run --project src/Aspire.Cli -- run` | csproj default bakes `AspireCliChannel=local`; reader returns `local` | No sidecar next to the built assembly; no env var set; resolver hits terminal fallback → `local` | Same channel, zero change to dev experience |
| `dotnet run` with channel override | Requires rebuild: `dotnet run -p:AspireCliChannel=staging --project src/Aspire.Cli -- run` | `ASPIRE_CLI_CHANNEL=staging dotnet run --project src/Aspire.Cli -- run` | **Simpler** — no rebuild needed to flip channels |
| `startvs.cmd` / VS debug | Same as `dotnet run` | Same as `dotnet run` | Unchanged |
| `./build.sh` then run from `artifacts/bin/...` | Assembly stamped `local`, no sidecar at artifacts path | No assembly stamp, no sidecar at artifacts path → terminal fallback `local` | Unchanged |
| `localhive.{sh,ps1}` install | Builds CLI, copies to hive prefix, writes `.aspire-install.json` with `{"source":"localhive"}` only | Same flow; sidecar additionally carries `"channel":"local"` | Explicit identity; no behavior change |
| Test that needs a specific channel | DI substitutes `FakeIdentityChannelReader` | DI substitutes `FakeIdentityResolver` | Same DI pattern, wider surface |

Three things to call out:

1. **The csproj `<AspireCliChannel>local</AspireCliChannel>` default can stay** as a no-op — it costs nothing and keeps `dotnet build` producing a binary whose assembly metadata reads sensibly when inspected. What changes is that CI stops *overriding* it for non-dotnet-tool publish paths.
2. **`dotnet run --project src/Aspire.Cli` continues to find no sidecar**, because the project's `bin/Debug/net10.0/` directory is not an install directory and nothing writes a sidecar there. This is correct: a `dotnet run` invocation is not an installed CLI and should resolve `local`.
3. **The validation patterns become available in the `dotnet run` loop too.** Want to reproduce a customer's stable-channel bug while iterating on `src/Aspire.Cli`?

   ```bash
   ASPIRE_CLI_CHANNEL=stable \
   ASPIRE_CLI_VERSION=13.4.0 \
   ASPIRE_CLI_COMMIT=abc123 \
     dotnet run --project src/Aspire.Cli -- init
   ```

   Same source tree, same build, behaves as the customer's binary did. No rebuild, no checkout dance.

### What this affects, and what it does not

The override changes every identity-conditional **decision** the CLI makes — channel-conditional branches, version-conditional branches, the contents of any `aspire.config.json` / `NuGet.config` the CLI writes, the staging-feed URL the CLI synthesizes, and the channel reported to telemetry. After the call-site migration (step 1), every such decision routes through `CliExecutionContext` and therefore through the resolver.

The override does **not** materialize package bytes that don't exist on the dev machine. Hives are rooted at `AspireHomeDirectory` and are populated by installer-side flows (a real `get-aspire-cli.{sh,ps1} --quality release` install creates `$HOME/.aspire/hives/stable/`, etc.). Forcing `ASPIRE_CLI_CHANNEL=stable` on a dev binary makes the CLI *try to use* the stable hive; if no stable hive is installed, hive-resolution diagnostics surface exactly as they would for a stale install — the override exposes the missing-hive case rather than masking it. For repros that need the real stable hive bytes (not just stable decision logic), the dev installs the stable CLI in parallel via `get-aspire-cli.{sh,ps1}` and the dev binary's `ASPIRE_CLI_CHANNEL=stable` run consumes that hive.

This separation is the point. Identity drives *decisions*; the install drives *bytes*. The 13.4 bug class was decisions and bytes being conflated into a single build-time stamp. Splitting them is what makes "use a PR binary to reproduce a customer's stable behavior" a one-line env-var prefix instead of a checkout-and-rebuild.

## Installer changes

Each install route already writes `.aspire-install.json`. Each route gains the responsibility of writing the matching identity:

| Route       | Channel written                        | Version / commit written           |
|-------------|----------------------------------------|------------------------------------|
| `script`    | mapped from `--quality` (dev→daily, staging→staging, release→stable) | the resolved download version |
| `pr`        | `pr-<PR_NUMBER>`                       | the resolved PR build version      |
| `localhive` | `local`                                | the locally-built version          |
| `winget`    | `stable` (winget ships GA only)        | the manifest version               |
| `brew`      | `stable` (brew ships GA only)          | the formula version                |
| `dotnet-tool` | written by the CLI at first run from the resolved package version (see below) | written at first run |

The release installer (`get-aspire-cli.{sh,ps1}`) already calls `map_quality_to_channel`; today that value is discarded after the download URL is constructed. This change persists it into the sidecar.

### Why dotnet-tool does not payload-embed identity

The obvious-but-wrong design is to ship a per-channel sidecar variant inside each dotnet-tool nupkg. **This is unsafe.** NuGet's global package cache is keyed by `(PackageId, PackageVersion)` — not by feed origin and not by channel. Aspire's release plumbing promotes the same nupkg identity from staging to stable: the bits a customer pulls from nuget.org are byte-identical to the bits a CLI promoted from the staging darc feed. If each channel's published nupkg carried a different sidecar in the payload, the user's cached copy would carry whichever sidecar's nupkg landed in their cache first, and the cache would never refresh because the package version would not have changed. We would have *exactly* the kind of identity-vs-bytes coupling this spec exists to remove.

The dotnet-tool route therefore writes its sidecar identity **at first run**, not at package install. The CLI already has a `WingetFirstRunProbe` for the winget route; the dotnet-tool route gets an analogous probe. The probe runs once per install, detects the dotnet-tool location (`~/.dotnet/tools/.store/aspire.cli/<version>/...`), and writes a sidecar with:

- `source: "dotnet-tool"`
- `version: <PackageVersion>` (read from the install path — already the source of truth NuGet uses)
- `channel`: derived from the assembly-stamped `AspireCliChannel` of the running binary at first run, **or** omitted if the assembly stamp is empty/`local`. When omitted, the resolver falls back to terminal `local` — acceptable because a dotnet-tool install with no channel signal is genuinely indistinguishable from a local-source install.
- `commit`: the parsed `+sha` from `AssemblyInformationalVersion`.

This keeps the nupkg payload route-and-channel-neutral. Cache hazards disappear. The cost is that **the dotnet-tool route is the one place where `AssemblyMetadata("AspireCliChannel", ...)` continues to be read**, as a one-shot at first-run sidecar materialization. That keeps a thin slice of the existing baking alive solely to seed the first-run sidecar — a deliberate exception, called out in code with a comment pointing at this section.

### Why winget / brew are simpler

Winget and brew always ship GA-only artifacts. Their channel is always `stable`. Their installers (or, in winget's case, the existing `WingetFirstRunProbe`) write `channel: "stable"` unconditionally with no per-version logic. There is no cache hazard because the installer scripts write the sidecar after extraction, not as part of the package payload.

## Build / CI cleanup

Once installers populate identity, the build-time stamping is removed *for channels that installers can populate from real install context*:

- `src/Aspire.Cli/Aspire.Cli.csproj`: drop the `<AspireCliChannel>` property and the `<AssemblyMetadata Include="AspireCliChannel" ... />` item **except** for the dotnet-tool publish — the dotnet-tool route's first-run sidecar materializer seeds `channel` from the assembly stamp (see the dotnet-tool section above), so the stamp remains *only on the bytes that ship through the dotnet-tool pack pipeline*. All other archive paths (script, pr, winget, brew, localhive) ship with the stamp set to `local` (the csproj default) and rely on the install-time-written sidecar for real channel identity.
- `.github/workflows/build-cli-native-archives.yml`: drop `/p:AspireCliChannel=…` from native-archive build invocations.
- `eng/pipelines/templates/build_sign_native.yml`: same.
- `eng/clipack/Common.projitems`: continues to pass `/p:AspireCliChannel=…` for the dotnet-tool publish path only.
- `eng/scripts/get-aspire-cli-pr.sh` / `.ps1`: drop any per-PR channel-baking parameters threaded through to the build; the PR number lives in the sidecar, written at install time.

The per-PR archives become **identical bytes regardless of PR number**. The PR number lives in the sidecar. The shared per-RID archives (consumed by script / pr / winget / brew / localhive) become channel-neutral by construction, which removes the cross-route smuggling failure mode #17527 was a symptom of.

The `AssemblyInformationalVersion` continues to be stamped by Arcade — we are not changing how versions are produced, only how the *running CLI* discovers what it should identify as.

### A non-dev shipped binary missing a sidecar

After this change, a shipped binary whose sidecar has been moved, deleted, or never written falls back to `channel=local` (and to `AssemblyInformationalVersion`-derived `version`/`commit`). This is a deliberate behavior change from today, where the assembly-stamped channel survives sidecar loss. The trade-off:

- **What is lost**: a customer who manually copies the `aspire` binary out of its install dir loses their channel identity. In practice this only affects power users on `staging` or `daily` — `stable` users default to nuget.org regardless of channel, so for the GA install base the behavior is indistinguishable.
- **What is gained**: every shipped CLI either has a correct sidecar or fails loudly enough to be diagnosable. The 13.4 bug shape — a binary silently mis-identifying because of a build-time threading mistake — is precluded by construction. CI gains a verification step (see below) that asserts every installer route produces a sidecar with a populated `channel` field, so a missing sidecar reaches no customer.
- **What `aspire doctor` does**: when the running binary is in a path shape consistent with a known install route (`script`, `pr`, `brew`, etc.) but has no sidecar identity, `doctor` emits a `warning: install-route detected but identity sidecar missing` diagnostic with a suggested re-install command.

This is the trade the spec accepts to honor the user's intent of removing build-time channel baking. The alternative — keeping the stamp permanently as a backstop — would re-introduce the 13.4 bug shape into the very mechanism intended to remove it.

## How this validates CLI changes earlier in the cycle

This is the core of why we are doing the work. Three concrete validation patterns become possible:

### 1. Reproduce a stable-only behavior on a dev binary

A user reports an issue against `aspire 13.4.0` (stable, commit `abc123`). Today, reproducing it requires either getting the user's exact binary or rebuilding from `release/13.4` at `abc123` with the right `/p:AspireCliChannel=stable`. With this change:

```bash
ASPIRE_CLI_CHANNEL=stable \
ASPIRE_CLI_VERSION=13.4.0 \
ASPIRE_CLI_COMMIT=abc123 \
  ./artifacts/bin/Aspire.Cli/Debug/net10.0/aspire init
```

The dev binary now runs every identity-conditional code path under the reported identity. Reproduction is a single command on the developer's machine, against the in-tree source code, with debugger attached.

### 2. Test staging behavior in PR CI

PR builds today bake `channel=pr-<N>` and never exercise the staging-only code paths. With this change, a CI job can run the PR binary with `ASPIRE_CLI_CHANNEL=staging` set, exercising the staging code paths against the PR's bits — *before* the bits are promoted to staging. Tests that depend on staging-feed resolution can run in PR.

### 3. Cross-channel diff testing

The same binary, run twice with `ASPIRE_CLI_CHANNEL=staging` vs `ASPIRE_CLI_CHANNEL=stable`, produces two execution traces. A CI job can diff those traces and fail when an identity-conditional code path appears that the test suite did not predict. New identity-conditional branches become visible as test failures rather than as 13.4-style shiproom escapes.

In all three patterns, the validation happens **on the bits that will ship**, **before they ship**, under **the identity they will adopt**. That is what 13.4 lacked.

## Migration

The change lands in three steps so each step is independently shippable:

1. **Resolver + call-site migration land first.** `IIdentityResolver` reads sidecar + env var + terminal fallback. **In the same PR**, every identified call site that today reads `AssemblyInformationalVersion` / `VersionHelper.GetDefaultTemplateVersion()` / `IIdentityChannelReader` for identity-sensitive purposes migrates to `CliExecutionContext`. The grep-based regression test (see "Call-site migration" above) lands here and forecloses backsliding. At this point no installer or build-system changes have happened: the resolver returns the same values it returned before, because the sidecar carries no identity fields and the assembly stamp is unchanged.

2. **Installers populate identity.** Each route's sidecar writer is updated to emit `channel` (and `version`/`commit` where cheaply available). Existing sidecars without identity fields continue to work via the terminal fallback. A CI verification job (`eng/scripts/verify-sidecar-identity.{sh,ps1}`) runs in the install-script integration tests and asserts every installed CLI has a sidecar with a populated `channel`. The dotnet-tool first-run probe lands in this step.

3. **Stamping is removed from non-dotnet-tool publish paths.** The csproj keeps the `AssemblyMetadata` item for the dotnet-tool publish only (consumed by the first-run sidecar materializer). All other CI build invocations stop passing `/p:AspireCliChannel=…`. Shared per-RID archives become channel-neutral bytes. The `aspire doctor` "install-route detected but identity sidecar missing" diagnostic lands in this step.

The three steps are observable from outside the codebase: step 1 changes no behavior; step 2 starts writing extended sidecars (visible in `aspire doctor --self`); step 3 changes the per-RID archive checksum to channel-neutral (visible as the same checksum across stable/staging/daily for the same source SHA, which is itself a CI-asserted invariant going forward).

## Security model

Identity overrides are local to the running process. They cannot:

- Modify what packages NuGet resolves (NuGet config is the source of truth for package resolution; the CLI's channel only influences *which* NuGet config it writes for *new* projects via `aspire init` / `aspire new`).
- Bypass code signing (`AssemblyInformationalVersion` is signed as part of the binary; the sidecar is plaintext and unsigned, exactly as `.aspire-install.json` is today).
- Escalate privileges.

The sidecar is read from `<binaryDir>/.aspire-install.json` — **never from the current working directory**. A planted sidecar in CWD cannot affect identity. The reader retains its existing 64 KB cap and AOT-safe JsonDocument parse from `InstallSidecarReader`.

**Env vars are ambient and inheritable** — any parent process, shell rc file, CI matrix entry, IDE launch config, or `env`-prefixed command can set `ASPIRE_CLI_CHANNEL`. They are explicitly **not a security boundary**. To make accidental override visible:

- The resolver tags each field with its source (`from-env` / `from-sidecar` / `from-assembly-fallback` / `defaulted-to-local`).
- `aspire doctor --self` shows the source per field.
- A non-fatal banner is emitted at CLI startup whenever any identity field resolves `from-env` and the resolved channel differs from the sidecar/fallback channel. This is a single line, suppressible via the standard CLI verbosity controls, and is the answer to "why is my CLI behaving like staging when I installed stable?".

The "is env-override gated for stable builds?" question is left as an open question (see below) — the spec leans against gating, but flags the call.

## Telemetry

Telemetry emits identity as **separate dimensions from the physical binary version**:

| Dimension                | Source                                                  |
|--------------------------|---------------------------------------------------------|
| `binary.version`         | `AssemblyInformationalVersion` (immutable per build)    |
| `binary.build_id`        | `AssemblyFileVersion` (immutable per build)             |
| `identity.channel`       | `CliExecutionContext.IdentityChannel`                   |
| `identity.version`       | `CliExecutionContext.IdentityVersion`                   |
| `identity.commit`        | `CliExecutionContext.IdentityCommit`                    |
| `identity.channel_source`| resolver source tag (`env` / `sidecar` / `fallback`)    |

This way a telemetry record can distinguish "real stable 13.4.0" from "dev binary impersonating stable 13.4.0", which is critical for not poisoning the telemetry corpus with override-driven noise. Aggregations that previously read `version` continue to read `binary.version`; identity-conditional analytics opt in to `identity.*` explicitly. The grep regression test from the call-site migration also enforces this split: telemetry callsites are allow-listed to read both surfaces.

Telemetry remains opt-out via `ASPIRE_CLI_TELEMETRY_OPTOUT` exactly as today; this spec does not change the opt model.

## Scope and non-goals

**In scope:**

- Resolver implementation with env var → sidecar → terminal fallback.
- Schema extension to `.aspire-install.json` (three optional fields).
- `CliExecutionContext` exposes `IdentityChannel`, `IdentityVersion`, `IdentityCommit`, and per-field source tags.
- Exhaustive migration of identity-sensitive call sites off direct assembly reads, enforced by a grep regression test.
- Env-var stripping at peer-process and AppHost-spawn boundaries.
- Installer scripts populate identity in sidecars (`script`, `pr`, `localhive`, `winget`, `brew`).
- Dotnet-tool first-run sidecar materializer (analogous to `WingetFirstRunProbe`).
- Removal of the `AspireCliChannel` build-time stamping from non-dotnet-tool publish paths.
- CI verification that every installer route produces a sidecar with `channel` populated.
- Telemetry split: `binary.*` vs `identity.*` dimensions.
- `aspire doctor --self` surfacing per-field resolution source and the "install-route detected but identity sidecar missing" diagnostic.
- Test coverage including the cross-route fallback matrix and peer-probe env-leak tests.

**Out of scope** (tracked separately):

- Per-build static NuGet feeds in blob storage — see [#17580](https://github.com/microsoft/aspire/issues/17580) and [#17578](https://github.com/microsoft/aspire/pull/17578).
- Removal of the `AssemblyInformationalVersion` stamping itself — Arcade emits this for all assemblies and we are not changing that.
- A signed identity sidecar — the threat model does not require it; the override is a developer affordance, not a security boundary.
- Removing the dotnet-tool route's `AspireCliChannel` stamp — kept deliberately as the seed for the first-run sidecar materializer. Revisiting depends on whether dotnet-tool can grow a non-payload-embedded identity-write mechanism without cache hazards.

## Open questions

1. **AppHost env propagation.** Should `ASPIRE_CLI_*` overrides propagate to AppHost child processes by default, only via an explicit opt-in, or never? Argument for propagation: AppHost decisions sometimes key on the parent CLI's identity (e.g., compatibility checks). Argument against: AppHost is not a peer Aspire CLI; conflating the two re-introduces the env-leak class. **Leaning: strip by default, add an `--inherit-cli-identity` opt-in on `aspire run` / `aspire start` if a real consumer surfaces.**
2. **Doctor surfacing.** Confirmed in this revision — `aspire doctor --self` shows the source per field.
3. **Env-override gating on stable builds.** Should `ASPIRE_CLI_*` be honored only when `ASPIRE_CLI_ALLOW_IDENTITY_OVERRIDE=1` is set on binaries whose installed channel is `stable`? Argument for: prevents a customer's shell config from accidentally driving their stable CLI into staging behavior. Argument against: the override is the spec's reason for existing; gating it adds friction to the validation patterns. **Leaning: do not gate, but emit the source-banner whenever identity comes from env, so accidental override is visible.**
4. **dotnet-tool stamp removal.** Is there a path to removing the `AspireCliChannel` assembly stamp for the dotnet-tool route too, without re-introducing the per-package-cache hazard? One option: ship a per-version, per-channel pre-install sidecar via a Roslyn-style "tools" file extracted by `dotnet tool install` outside the cache. Out of scope for now; tracked as a follow-up if dotnet-tool stamp removal becomes important.

## Test plan

The resolver and call-site migration land together; the test plan reflects that.

**Resolver-layer tests** (`IdentityResolverTests`, replacing `IdentityChannelReaderTests`):

- Matrix over (env var present/absent) × (sidecar present/absent) × (sidecar field present/absent) × (assembly stamp present/absent), for each of `channel` / `version` / `commit`.
- Per-field source-tag correctness (`from-env` / `from-sidecar` / `from-assembly-fallback` / `defaulted-to-local`).
- Bespoke channel labels from env/sidecar (e.g., `ASPIRE_CLI_CHANNEL=pr-17580`) are accepted as-is — they are legitimate overrides, not validation failures; only the assembly-baked channel is shape-validated.
- Invalid `version` / `commit` / `nugetServiceIndexOverride` from env or sidecar fails fast with a diagnostic naming the source (env var name or sidecar field) — not a silent fallback to the next layer.

**Sidecar-layer tests** (`InstallSidecarReaderTests`):

- Partial identity sidecars (`source` + `channel` only; `source` + `version` only; etc.).
- Oversize sidecars with identity fields — same 64 KB cap behavior as today.
- Malformed identity fields (non-string `channel`, missing closing brace, etc.) — invalidate the whole sidecar, not just the bad field, so a partial-trust read does not happen.
- Schema migration: an old sidecar with only `source` continues to parse and the new fields resolve via terminal fallback.

**Process-launch isolation tests**:

- `PeerInstallProbe` peer spawn under parent `ASPIRE_CLI_CHANNEL=staging` — assert peer's reported identity is its own (sidecar/fallback), not `staging`.
- AppHost spawn under parent override — assert env vars are stripped from the child unless the opt-in mechanism is set.
- A negative test: parent sets `ASPIRE_CLI_CHANNEL=staging`, runs `aspire doctor`, every peer row shows its own identity.

**Bootstrap and execution-context tests**:

- `CliBootstrapTests`: a `ASPIRE_CLI_CHANNEL=staging` run produces `CliExecutionContext.IdentityChannel == "staging"` and `IdentityChannelSource == "env"`.
- `CliExecutionContextTests`: per-field construction with overridden values and source tags.

**Packaging tests** (`PackagingServiceTests`):

- Staging-feed URL derives from `IdentityCommit`, not directly from the assembly — set `ASPIRE_CLI_COMMIT=abc123` and assert the resulting feed URL contains `abc123`.
- Staging pinned version derives from `IdentityVersion` — same shape as the commit test.
- Channel-conditional package source selection honors `IdentityChannel`.

**Installer tests**:

- `get-aspire-cli.{sh,ps1}` integration tests assert the post-install sidecar contains the channel matching `--quality`.
- `get-aspire-cli-pr.{sh,ps1}` integration tests assert the sidecar contains `pr-<N>`.
- `localhive.{sh,ps1}` integration tests assert the sidecar contains `local`.
- Winget first-run probe writes `channel: "stable"`.
- Brew cask postflight writes `channel: "stable"`.
- Dotnet-tool first-run probe writes `version` matching the install-path version segment.

**CI sidecar verification** (`eng/scripts/verify-sidecar-identity.{sh,ps1}`):

- For each install route exercised in CI, assert the post-install sidecar parses, has a known `source`, and (for step-3-migrated routes) has a populated `channel`.

**End-to-end** (`Aspire.Cli.EndToEnd.Tests/IdentityOverrideEndToEndTests.cs`):

- Run a built CLI under `ASPIRE_CLI_CHANNEL=stable` against a project and assert the resulting generated `aspire.config.json` carries `channel=stable`.
- Run the same binary under `ASPIRE_CLI_CHANNEL=staging` and assert the resulting `NuGet.config` includes the staging feed.
- Cross-channel diff smoke: same binary, two runs with different `ASPIRE_CLI_CHANNEL`, produce observably different `NuGet.config` outputs. This is the executable form of the spec's "earlier validation" claim — if it ever stops being true, an identity-conditional code path has slipped past the resolver.

**Emulated channel matrix E2E** (AppHost-language × channel-emulation; one test per language because C# and TypeScript scaffold through different code paths and have diverged before):

- `EmulatedReleasedBuildTests` (C# + TS): emulate the latest **shipped stable**; assert no `NuGet.config` is dropped (stable packages live on nuget.org) and the AppHost SDK is pinned stable-shaped.
- `EmulatedStagingBuildTests` (C# + TS): emulate the latest **staging** build (discovered darc commit); assert a `NuGet.config` mapping `Aspire*` to the SHA-specific darc feed is dropped and `aspire add` resolves the staging version.
- `EmulatedLocalReleaseBuildTests` (C# + TS): the **all-local "future release"** row. Build a stable-shaped archive with `localhive --version <X.Y.Z> --archive`, emulate `stable` with `ASPIRE_CLI_PACKAGES` pointed at the extracted hive, and assert `aspire new`/`aspire add` resolve the future-only version (e.g. `13.5.0`) **from the local hive** — proving the resolution fix (override honored under any emulated channel name). Registers the hive as an ambient NuGet source for buildability. Skips unless the CLI was installed from a stable-shaped `LocalHive` archive, so it adds zero default-CI cost (relying on `--ignore-exit-code 8` in `eng/Testing.props` for the all-skipped class job).
