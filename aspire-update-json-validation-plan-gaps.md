# Validation Plan Gap Analysis & Fix Plan: `aspire-update-json`

## Summary

The existing plan covers happy paths well but misses a critical runtime bug and several edge cases. The `ci` and `local` embedded channels are not recognized by the channel system, causing crashes in `update --self`, `new`, `add`, `init`, and other commands on CI-built and localhive binaries.

---

## Critical Bug: `ci`/`local` Embedded Channels Crash Multiple Commands

### The bug

Every CI archive build embeds `CliChannel=ci` (default in `Common.projitems` line 12 and `build-cli-native-archives.yml` line 19). Localhive builds embed `CliChannel=local`. Neither channel exists in `GetChannelsAsync()`.

**Affected code paths** (all do `GetEmbeddedChannel()` → channel lookup → crash):

| Command | File | Behavior with `ci` channel |
|---------|------|---------------------------|
| `update --self` | `UpdateCommand.cs:314-318` | `"ci"` ≠ `"pr"` → passes to downloader → `ArgumentException` |
| `new` | `NewCommand.cs:306,311` | Looks up `"ci"` → null → "No channel found matching 'ci'" |
| `add` | `AddCommand.cs:118` | → `ChannelNotFoundException` |
| `init` | `InitCommand.cs:743` | → same |
| template NuGet config | `TemplateNuGetConfigService.cs:67` | → `ChannelNotFoundException` |
| project server | `DotNetBasedAppHostServerProject.cs:333` | → same |
| prebuilt apphost | `PrebuiltAppHostServer.cs:347` | → same |

### Why `pr` works but `ci`/`local` don't

`UpdateCommand` line 316 explicitly excludes `"pr"` from auto-selection, falling through to user prompt. But `"ci"` and `"local"` pass through and hit the downloader/channel-lookup.

For non-update commands (`new`, `add`, `init`), there's NO exclusion for ANY non-standard channel — they all go straight to channel lookup.

### Why this hasn't been caught yet

- E2E tests run with `local` channel but test the "no explicit channel config" and "global config overrides" paths, not the crash path
- Unit tests mock `GetEmbeddedChannel()` away via `TestPackagingService`
- Localhive dogfood works because users set `channel` in config, which takes precedence over embedded channel

### The `pr` exclusion is dead code

No workflow currently passes `cliChannel: pr`. `tests.yml` calls `build-cli-native-archives.yml` 5 times without overriding `cliChannel`, all default to `ci`. The `pr` check at UpdateCommand line 316 never fires in practice.

---

## Fix Plan

### Fix 1: UpdateCommand — whitelist downloadable channels (not blacklist `pr`)

**File:** `src/Aspire.Cli/Commands/UpdateCommand.cs` lines 314-319

**Current:**
```csharp
var embeddedChannel = PackagingService.GetEmbeddedChannel();
if (!string.IsNullOrEmpty(embeddedChannel) &&
    !string.Equals(embeddedChannel, "pr", StringComparison.OrdinalIgnoreCase))
{
    channel = embeddedChannel;
}
```

**Fix:** Only auto-use if it's a known downloadable channel:
```csharp
var embeddedChannel = PackagingService.GetEmbeddedChannel();
if (!string.IsNullOrEmpty(embeddedChannel) &&
    IsDownloadableChannel(embeddedChannel))
{
    channel = embeddedChannel;
}
```

Where `IsDownloadableChannel` validates the channel exists in `GetChannelsAsync()` with a non-null `CliDownloadBaseUrl`, OR checks against the known set `{stable, daily, staging}`.

### Fix 2: Non-update commands — treat unknown embedded channel as "no channel configured"

**Files:** `NewCommand.cs`, `AddCommand.cs`, `InitCommand.cs`, `DotNetTemplateFactory.cs`, `TemplateNuGetConfigService.cs`, `PrebuiltAppHostServer.cs`, `DotNetBasedAppHostServerProject.cs`

All do:
```csharp
channelName = await configurationService.GetConfigurationAsync("channel", ct)
    ?? PackagingService.GetEmbeddedChannel();
```

Then look up `channelName` and crash if not found.

**Recommended fix — Option A (single fix point):** Add a `GetEmbeddedChannelIfKnown()` that returns null for non-distributable channels, or have `GetEmbeddedChannel()` itself filter. The raw value stays accessible for diagnostics (`aspire --info`).

**Alternative — Option B:** Add a validation wrapper at each call site.

**Alternative — Option C:** Register `ci`/`local` as channels in `GetChannelsAsync()` that alias to `default` with no download URL. Most robust but adds complexity.

### Fix 3: Decide on PR channel embedding

**Decision needed:** Should PR-triggered CI builds embed `cliChannel: pr`?
- If yes: update `tests.yml` to conditionally pass `cliChannel: pr` for PR events
- If no: remove the dead `pr` check from UpdateCommand and simplify to whitelist

---

## New Tests Needed

### Unit tests for UpdateCommand channel resolution

Add to `UpdateCommandTests.cs`:

| Test | Embedded channel | Expected behavior |
|------|-----------------|-------------------|
| `SelfUpdate_WithCiEmbeddedChannel_PromptsForChannel` | `"ci"` | Prompts user (not crash) |
| `SelfUpdate_WithLocalEmbeddedChannel_PromptsForChannel` | `"local"` | Prompts user |
| `SelfUpdate_WithPrEmbeddedChannel_PromptsForChannel` | `"pr"` | Prompts user (currently untested despite comment) |
| `SelfUpdate_WithStableEmbeddedChannel_AutoSelects` | `"stable"` | Auto-selects stable, no prompt |
| `SelfUpdate_WithDailyEmbeddedChannel_AutoSelects` | `"daily"` | Auto-selects daily, no prompt |
| `SelfUpdate_WithUnknownEmbeddedChannel_PromptsForChannel` | `"custom-foo"` | Prompts user (future-proof) |

### Unit tests for non-update paths

| Test | Command | Embedded channel | Expected |
|------|---------|-----------------|----------|
| `New_WithCiEmbeddedChannel_UsesDefaultChannel` | `aspire new` | `"ci"` | Falls back to default/implicit, no crash |
| `Add_WithLocalEmbeddedChannel_UsesDefaultChannel` | `aspire add` | `"local"` | Falls back to default, no crash |

### E2E tests for channel behavior

Add to `EmbeddedChannelTests.cs`:

| Test | Setup | Expected |
|------|-------|----------|
| `CiChannel_SelfUpdate_PromptsForChannel` | Archive with `CliChannel=ci` | `aspire update --self` shows channel prompt |
| `CiChannel_New_FallsBackToDefault` | Archive with `CliChannel=ci` | `aspire new` works normally |
| `PrDogfood_SelfUpdate_PromptsForChannel` | PR-built archive | `aspire update --self` prompts, user can select `stable`/`daily` |

### E2E tests from original plan (still needed)

| Test | File | Description |
|------|------|-------------|
| Homebrew symlink + sidecar | `HomebrewSimulationTests.cs` | Simulated Homebrew layout with brew instructions |
| Script install removes sidecar | `ScriptInstallOverrideTests.cs` | Sidecar cleanup on re-install |
| Sidecar + embedded channel interaction | `EmbeddedChannelTests.cs` | Sidecar blocks self-update regardless of channel |

---

## Medium Priority Gaps (from original analysis)

| # | Gap | Description |
|---|-----|-------------|
| M1 | Malformed sidecar JSON | Detector fails closed, no E2E coverage for user-visible behavior |
| M2 | `selfUpdateDisabled: false` + WinGet | Explicit false under WinGet path → WinGet detection takes over |
| M3 | Broken symlink fallback | `ResolveLinkTarget` failure → should fall back to original path |
| M4 | Script deletion edge cases | Read-only sidecar, already-absent sidecar |
| M5 | Channel forwarding verification | Post-project-update → self-update should receive exact selected channel |

## Low Priority Gaps

| # | Gap |
|---|-----|
| L1 | PowerShell script-over-WinGet contamination |
| L2 | Local `aspire.config.json` overriding embedded channel |
| L3 | Legacy settings migration with conflicting channel |

---

## Todos

### Must fix (blocking bugs)
- [ ] Fix UpdateCommand: whitelist downloadable channels instead of blacklisting `pr`
- [ ] Fix non-update commands: handle unknown embedded channel gracefully
- [ ] Decide on PR channel workflow embedding
- [ ] Add unit tests for ci/local/pr/stable/daily/unknown embedded channels in UpdateCommand
- [ ] Add unit tests for ci/local embedded channels in New/Add paths
- [ ] Add E2E tests for ci-channel binary and PR dogfood scenarios

### Should fix (from original plan)
- [ ] Run Tier 1 unit tests green
- [ ] Run Tier 2 E2E tests green
- [ ] Create HomebrewSimulationTests.cs
- [ ] Create ScriptInstallOverrideTests.cs
- [ ] Extend EmbeddedChannelTests.cs with sidecar interaction tests

### Nice to have
- [ ] Malformed sidecar E2E test
- [ ] Broken symlink unit test
- [ ] Script deletion edge case tests
- [ ] Channel forwarding verification test
