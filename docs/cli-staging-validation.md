# Validating staging feed routing with a local CLI build

This document describes how to make a locally built Aspire CLI resolve `Aspire.*`
packages exactly the way an official **staging** (or **stable**) build would, so the
staging feed-routing behavior can be validated end-to-end without an official build.

## Background

A staging-identity CLI is an official release-branch build whose own commit always has
a SHA-specific `darc-pub-microsoft-aspire-<commit>` feed carrying its matching packages
(prerelease-shaped `13.4.0-preview.*` and stable-shaped `13.4.0` alike). Feed
**provenance** is decided by the CLI's baked build **identity** (`AspireCliChannel`),
while version **filtering** (the channel quality) is decided by the CLI's **version
shape**. See `PackagingService.ShouldUseSharedStagingFeed`.

A locally built CLI bakes a `local` identity and an unstamped informational version, so
it never synthesizes a staging channel and never derives a darc feed. The two diagnostic
overrides below let you simulate the staging path locally.

## The two diagnostic overrides

Both are read by `PackagingService` only (their blast radius is limited to staging
feed-routing decisions — they do **not** change the global identity used for hive or
package-directory lookups):

| Config key | Purpose |
| --- | --- |
| `overrideCliIdentityChannel` | Forces the identity used for staging-feed routing decisions. Must be a valid channel (`stable`, `staging`, `daily`, `local`, or `pr-<N>`); invalid values are ignored and the real identity is used. |
| `overrideCliInformationalVersion` | Forces the informational version that both the SHA-derivation provider and the version-shape (quality) predicate read. The part after `+` (truncated to 8 chars) builds the darc URL; the version part determines stable-vs-prerelease shape. |

**Both overrides are required** to reach the darc path from a local build:

- Identity override alone → the SHA is still unstamped, so the darc URL can't be derived.
- Version override alone → the identity stays `local`, so routing never selects the darc feed.

When either override is set, the CLI emits a one-time warning so an overridden
identity/feed can't silently resolve packages on a normal invocation.

## Recipe

1. Build the CLI locally:

   ```bash
   ./build.sh --build /p:SkipNativeBuild=true
   ```

2. In the apphost directory, set `channel: staging` in `aspire.config.json` (this is what
   `aspire add` filters the synthesized channels to):

   ```json
   {
     "channel": "staging"
   }
   ```

3. Set the two overrides (environment variables are the simplest; they are read
   case-insensitively with no prefix):

   ```bash
   export overrideCliIdentityChannel=staging
   export overrideCliInformationalVersion=13.4.0-preview.1.26280.6+<full-commit-hash>
   ```

   Use a real release-branch build commit hash so the derived feed actually exists if you
   intend to restore; any 8+ char hex suffix works for inspecting the resolved feed URL.

4. Run `aspire add` with debug logging and confirm the resolved darc feed:

   ```bash
   aspire add foundry --debug
   ```

   The logs should show the staging channel resolving `Aspire*` to
   `.../darc-pub-microsoft-aspire-<first8-of-commit>/...` rather than the shared
   `dnceng/.../dotnet9` daily feed.

To simulate a **stable**-shaped staging build, use a stable-shaped version override
(e.g. `13.4.0+<full-commit-hash>`); the channel quality becomes `Stable` while the feed
stays the darc feed.

## Helper scripts

`eng/scripts/debug-staging.{sh,ps1}` and `eng/scripts/debug-stable.{sh,ps1}` wrap the
recipe above. Both target identity `staging` and expect the **same** darc feed; they
differ only in version shape/quality:

| Script | Version shape | Expected quality | Scenario |
| --- | --- | --- | --- |
| `debug-staging` | prerelease (`13.4.0-preview.*`) | `Both` | [#17744](https://github.com/microsoft/aspire/issues/17744) — the bug this PR fixes |
| `debug-stable` | stable (`13.4.0`) | `Stable` | [#17527](https://github.com/microsoft/aspire/issues/17527) — stable-shaped release build |

Each script computes the expected `darc-pub-microsoft-aspire-<sha8>` feed and supports
three modes:

- **Validate (default):** runs `aspire add <pkg> --debug` in a throwaway directory and
  asserts the darc feed appears in the resolution log. Exits non-zero if it doesn't.
- **`--print-env` / `-PrintEnv`:** emits `export`/`$env:` lines you apply to your current
  shell. Every subsequent `aspire` command then behaves like the simulated build.
- **`--shell` / `-Shell`:** opens an interactive subshell with the overrides applied and
  the target CLI first on `PATH`. It also points `NUGET_PACKAGES` at an isolated, per-sha
  cache so restores from the simulated staging feed never contaminate your real global
  package cache. Exiting the subshell restores normal behavior.

Common flags: `--sha <commit>` (required, 8–40 hex), `--cli <path>` (CLI to drive),
`--pr <N>` (install that PR's full-bundle build first, then target it), `--version <ver>`.

### Interactive validation against an installed PR build

You don't need a local source build — the easiest carrier is an installed **PR build**,
which is a real full-bundle `~/.aspire` install. Install it, then make it behave like a
staging build for a full `aspire new` / `aspire add` / run flow:

```bash
# 1. Install the PR's full-bundle build.
./eng/scripts/get-aspire-cli-pr.sh 17743

# 2a. Apply staging overrides to the CURRENT shell (every aspire command is staging-flavored):
eval "$(./eng/scripts/debug-stable.sh --sha <commit> --print-env)"
aspire new       # behaves like the simulated staging build
aspire add foundry
# revert when done:
unset channel overrideCliIdentityChannel overrideCliInformationalVersion

# 2b. ...or get a throwaway subshell instead (overrides vanish on 'exit'):
./eng/scripts/debug-stable.sh --pr 17743 --sha <commit> --shell
```

PowerShell is identical with the `.ps1` siblings:

```powershell
./eng/scripts/get-aspire-cli-pr.ps1 17743
./eng/scripts/debug-stable.ps1 -Sha <commit> -PrintEnv | Invoke-Expression
# ...or:
./eng/scripts/debug-stable.ps1 -Pr 17743 -Sha <commit> -Shell
```

The overrides are scoped to `PackagingService` feed routing and only ever live in the
shell/subshell environment, so nothing is written to global or per-project config.

## Validation matrix

| Identity | Version shape | Expected feed | Expected quality |
| --- | --- | --- | --- |
| `staging` | prerelease | `darc-pub-microsoft-aspire-<sha8>` | `Both` |
| `staging` | stable | `darc-pub-microsoft-aspire-<sha8>` | `Stable` |
| `daily` | any | shared `dnceng/.../dotnet9` daily feed | `Both` |
| `local` / `pr-<N>` | any | local/PR hive + implicit (no staging synthesis) | n/a |
| `stable` | stable | nuget.org | `Stable` |
