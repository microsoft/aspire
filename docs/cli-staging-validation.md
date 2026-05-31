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
