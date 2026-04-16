# Validation Plan: `aspire-update-json` Branch

## Problem Statement

The `aspire-update-json` branch introduces three interrelated features:
1. **`.aspire-update.json` sidecar file** — opt-out mechanism for self-update (placed by package managers, removed by install scripts)
2. **Embedded channel (`CliChannel`)** — binary-stamped distribution channel (ci, stable, daily, local) via `AssemblyMetadata`
3. **`InstallationDetector`** — runtime detection of install method (WinGet path heuristics, dotnet tool, sidecar file, symlink resolution for Homebrew)

These features change how `aspire update --self`, update notifications, and channel resolution behave depending on how the CLI was installed. We need to validate all paths end-to-end, including simulated package manager installs.

## Approach

Layered validation: existing unit tests → new integration/E2E tests → scripted scenario tests simulating real install methods. Automate everything that can run in CI; document manual-only gaps.

---

## Tier 1: Existing Unit Tests (verify green)

Already exist in `tests/Aspire.Cli.Tests/`. Just need to build and run:

- `InstallationDetectorTests` (22 cases) — all detection paths, priorities, error handling
- `UpdateCommandTests` — self-update behavior per install method, channel resolution
- `CliUpdateNotificationServiceTests` — notification suppression, per-method messaging
- `AspireConfigFileTests` — config migration, embedded channel reading, ExtensionData
- `PackagingServiceTests` — `GetEmbeddedChannel()` reading

## Tier 2: Existing E2E & Acquisition Tests (verify green)

- `SelfUpdateDetectionTests` — 3 Hex1b Docker tests for `.aspire-update.json` behavior
- `EmbeddedChannelTests` — 2 Hex1b Docker tests for embedded channel (LocalHive-only)
- `ReleaseScriptFunctionTests.InstallArchive_RemovesAspireUpdateJson` — bash script removal
- `ReleaseScriptPSFunctionTests.ExpandAspireCliArchive_RemovesAspireUpdateJson` — PowerShell removal

---

## Tier 3: New Scenario Tests (the work)

### Where to add them

**New tests go in `tests/Aspire.Cli.EndToEnd.Tests/`** as Hex1b Docker tests. This is the established pattern for CLI integration tests that need a real binary running in a realistic environment.

The existing `SelfUpdateDetectionTests.cs` already demonstrates the exact pattern we need — install CLI in Docker, manipulate files next to the binary, run `aspire update --self`, assert on terminal output. We extend this approach with new test classes for each install method simulation.

### Design: How each install method is simulated

All scenarios start from a **LocalHive archive** (`ASPIRE_E2E_ARCHIVE` env var → `localhive.sh --archive`). The archive extracts to `~/.aspire/` with `bin/aspire`, `managed/`, `dcp/`, and hive packages. Tests then manipulate the filesystem to simulate different install methods.

#### Scenario A: Simulated Homebrew Install

**File**: `tests/Aspire.Cli.EndToEnd.Tests/HomebrewSimulationTests.cs`

**What it does**: Mimics what Homebrew's `postflight` block in `aspire.rb.template` does — writes `.aspire-update.json` with `selfUpdateDisabled:true` and `updateInstructions:"brew upgrade --cask aspire"`. Also creates a symlink to simulate Homebrew's bin linking behavior (the `InstallationDetector` resolves symlinks to find the sidecar file).

```
Docker container setup:
  1. Extract localhive archive → ~/.aspire/
  2. Create Cellar-like layout:
     mkdir -p /opt/homebrew/Caskroom/aspire/13.2.0
     cp -r ~/.aspire/bin/* /opt/homebrew/Caskroom/aspire/13.2.0/
     echo '{"selfUpdateDisabled":true,"updateInstructions":"brew upgrade --cask aspire"}' \
       > /opt/homebrew/Caskroom/aspire/13.2.0/.aspire-update.json
  3. Create symlink (Homebrew pattern):
     ln -sf /opt/homebrew/Caskroom/aspire/13.2.0/aspire /usr/local/bin/aspire
  4. Put symlink first in PATH:
     export PATH=/usr/local/bin:$PATH
```

**Test cases**:

| Test | Command | Expected Output |
|------|---------|-----------------|
| `Homebrew_UpdateSelf_ShowsBrewInstructions` | `aspire update --self` | Contains `SelfUpdateDisabledMessage` AND `brew upgrade --cask aspire` |
| `Homebrew_SymlinkResolution_FindsSidecar` | `aspire update --self` | Same as above — verifies symlink is properly resolved |
| `Homebrew_UpdateNotification_Suppressed` | `aspire --version` (with newer version available) | Does NOT show "update available" banner |

#### Scenario B: Simulated Script Install (over Homebrew)

**File**: Add to existing `SelfUpdateDetectionTests.cs` or new `ScriptInstallOverrideTests.cs`

**What it does**: Simulates a user who installed via Homebrew first, then ran `get-aspire-cli.sh` which should remove the `.aspire-update.json` and allow self-update.

```
Docker container setup:
  1. Extract localhive archive → ~/.aspire/
  2. Write .aspire-update.json (simulating pre-existing Homebrew install):
     echo '{"selfUpdateDisabled":true}' > ~/.aspire/bin/.aspire-update.json
  3. Run the install script extraction logic:
     # The install script extracts and then removes .aspire-update.json
     # We can test this by sourcing the script functions and calling install_archive
```

**Test cases**:

| Test | Command | Expected |
|------|---------|----------|
| `ScriptInstall_RemovesSidecar_AllowsSelfUpdate` | `aspire update --self --channel stable` | Does NOT contain `SelfUpdateDisabledMessage` |
| `ScriptInstall_OverHomebrew_CleansUpSidecar` | Check `~/.aspire/bin/.aspire-update.json` existence | File does not exist after script install |

#### Scenario C: Simulated Direct Download

**File**: This is essentially what `SelfUpdateDetectionTests.UpdateSelf_WithoutAspireUpdateJson_DoesNotShowDisabledMessage` already tests. No new test needed — the default archive extraction has no `.aspire-update.json`.

**Verify**: Archive contents have no `.aspire-update.json` (already tested in Acquisition tests).

#### Scenario D: Embedded Channel Resolution (extended)

**File**: Extend `EmbeddedChannelTests.cs`

**Existing tests**: `LocalHive_WorksWithoutExplicitChannelConfig` and `GlobalConfig_OverridesEmbeddedChannel` — both `LocalHive`-only.

**New test cases**:

| Test | Setup | Expected |
|------|-------|----------|
| `EmbeddedChannel_SelfUpdate_UsesEmbeddedWhenNoFlag` | LocalHive archive (CliChannel=local) | `aspire update --self` uses "local" channel without `--channel` flag |
| `EmbeddedChannel_ExplicitFlag_OverridesEmbedded` | LocalHive + `--channel stable` | `aspire update --self --channel stable` uses stable, not local |
| `EmbeddedChannel_WithSidecarDisabled_ShowsInstructions` | LocalHive + `.aspire-update.json` | Sidecar takes priority; self-update blocked regardless of embedded channel |

#### Scenario E: Update Notification Per Install Method

**File**: `tests/Aspire.Cli.EndToEnd.Tests/UpdateNotificationTests.cs`

**What it does**: Verifies the update notification banner adapts to the install method. This is harder to test in E2E because it requires a version mismatch. The unit tests (`CliUpdateNotificationServiceTests`) already cover the logic thoroughly. We may skip E2E for this and rely on unit tests.

**Decision**: Unit tests sufficient. No new E2E test.

#### Scenario F: WinGet Detection (Windows-only)

**Limitation**: E2E tests run in Docker on Linux only. WinGet path detection is Windows-specific and cannot be tested in the Docker-based E2E framework.

**Coverage**: Unit tests (`InstallationDetectorTests`) already cover WinGet path detection with mocked process paths on all platforms. This is sufficient for the path-heuristic logic.

**Future**: If we add Windows E2E infrastructure, add a test that places the binary under a fake `%LOCALAPPDATA%\Microsoft\WinGet\Packages\` path.

---

## Tier 3 Implementation Details

### New file: `HomebrewSimulationTests.cs`

```csharp
// Pattern: follows SelfUpdateDetectionTests.cs exactly
public sealed class HomebrewSimulationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Homebrew_UpdateSelf_ShowsBrewInstructions()
    {
        // 1. Setup: Docker terminal with localhive archive
        // 2. Install CLI: extract archive to ~/.aspire/
        // 3. Simulate Homebrew:
        //    - Copy binary to fake Cellar path
        //    - Write .aspire-update.json with brew instructions
        //    - Create symlink from /usr/local/bin/aspire → Cellar binary
        //    - Set PATH with symlink dir first
        // 4. Run: aspire update --self
        // 5. Assert: output contains SelfUpdateDisabledMessage
        // 6. Assert: output contains "brew upgrade --cask aspire"
    }

    [Fact]
    public async Task Homebrew_SymlinkResolved_FindsSidecarNextToRealBinary()
    {
        // Same as above but verify the symlink resolution specifically:
        // - .aspire-update.json is next to the REAL binary in Cellar, not next to symlink
        // - Running via symlink still detects the sidecar
    }
}
```

### New file: `ScriptInstallOverrideTests.cs`

```csharp
public sealed class ScriptInstallOverrideTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ScriptInstall_OverExistingSidecar_RemovesItAndAllowsSelfUpdate()
    {
        // 1. Install CLI normally
        // 2. Write .aspire-update.json (simulating prior Homebrew install)
        // 3. Re-extract archive over the install (simulating get-aspire-cli.sh behavior)
        //    - Or directly remove .aspire-update.json to test the script's cleanup
        // 4. Run: aspire update --self --channel stable
        // 5. Assert: SelfUpdateDisabledMessage does NOT appear
    }
}
```

### Extending `EmbeddedChannelTests.cs`

Add 2-3 new tests to the existing file:
- `EmbeddedChannel_WithSidecarDisabled_SidecarWins` — sidecar blocks self-update even with embedded channel
- `EmbeddedChannel_SelfUpdate_UsesEmbeddedChannel` — `aspire update --self` without `--channel` uses embedded

### Acquisition Tests: Verify Archive Contents

Add to `ReleaseScriptFunctionTests.cs`:

```csharp
[Fact]
public async Task BuiltArchive_DoesNotContainAspireUpdateJson()
{
    // Build a real archive using the bundling infrastructure
    // Inspect tar.gz contents
    // Assert .aspire-update.json is NOT present
    // (This verifies the design decision that archives don't ship the sidecar)
}
```

**Note**: This may already be implicitly tested by the `CreateFakeArchiveAsync` helper (which doesn't include the file). But testing against a real build output would be more valuable — possibly as a CI-only test.

---

## Tier 4: Manual/CI-Only Tests (cannot automate locally)

| Scenario | Why manual | Mitigation |
|----------|-----------|------------|
| Actual Homebrew `brew install --cask aspire` | Needs macOS + real Homebrew | Homebrew sim in Docker covers logic |
| Actual WinGet `winget install aspire` | Needs Windows + WinGet | Unit tests + future Windows CI |
| WinGet → script coexistence (W4,W5 from gist) | Needs Windows + both installed | Unit tests for detection priority |
| Cross-install config contamination (W34,W35) | Needs multiple installs on same machine | Partially mitigated by embedded channel |
| PATH precedence in new terminal window | Requires spawning new shell | Manual verification |
| `aspire update --self` actually downloading + replacing binary | Needs real download infrastructure | Covered by existing self-update E2E tests |

---

## Summary of New Files to Create

| File | Type | Tests | Description |
|------|------|-------|-------------|
| `HomebrewSimulationTests.cs` | E2E (Hex1b) | 2-3 | Simulated Homebrew layout with symlink + sidecar |
| `ScriptInstallOverrideTests.cs` | E2E (Hex1b) | 1-2 | Script install overrides existing sidecar |
| Extended `EmbeddedChannelTests.cs` | E2E (Hex1b) | 2-3 | Embedded channel + sidecar interaction, self-update channel |

**Total new tests**: ~7-8 E2E test cases
**Existing tests verified**: ~30+ unit tests + 5 E2E + 2 acquisition script tests

---

---

## Forward-Compatibility: PR #15951 and #15955

### PR #15951 — Self-contained dogfood installs under `~/.aspire/dogfood/`

**Key changes that affect us:**
- `localhive.sh` now installs to `~/.aspire/dogfood/{name}/` instead of `~/.aspire/bin/`
- `GetInstallRootDirectory()` resolves install root from process path: `~/.aspire/bin/aspire` → root is `~/.aspire/`, flat `~/.aspire/dogfood/pr-1234/aspire` → root is `~/.aspire/dogfood/pr-1234/`
- `CliE2EAutomatorHelpers.InstallAspireCliInDockerAsync` updates PATH to include dogfood dir
- `AspireNewAsync` gains handling for "Select a template version" prompt in dogfood installs

**Impact on our tests:**
- Our Homebrew simulation places the binary in a custom Cellar path, so the localhive path change doesn't affect it directly
- BUT: after PR #15951, the E2E `InstallAspireCliAsync` may extract to a dogfood path. Our tests that subsequently write `.aspire-update.json` need to find the binary dynamically, not assume `~/.aspire/bin/`

**Recommendation: Add a helper to resolve CLI binary path dynamically**

```csharp
// Helper: find where aspire was installed (works for both stable and dogfood layouts)
internal static async Task<string> GetInstalledCliBinaryDirAsync(
    this Hex1bTerminalAutomator auto, SequenceCounter counter)
{
    await auto.TypeAsync("dirname $(which aspire)");
    await auto.EnterAsync();
    // Parse output to get the directory
    await auto.WaitForSuccessPromptAsync(counter);
    // ... return the captured path
}
```

This helper would make our sidecar placement tests layout-agnostic:
```csharp
// Instead of hardcoded:
await auto.TypeAsync("echo '{...}' > ~/.aspire/bin/.aspire-update.json");
// Use dynamic resolution:
var binDir = await auto.GetInstalledCliBinaryDirAsync(counter);
await auto.TypeAsync($"echo '{{...}}' > {binDir}/.aspire-update.json");
```

**Practical decision**: The existing `SelfUpdateDetectionTests` already hardcodes `~/.aspire/bin/` — PR #15951 will need to update those too. We should write our new tests with the same pattern as existing tests for now (hardcoded `~/.aspire/bin/`), since we'll do a bulk update when #15951 lands. Adding the helper now would conflict with the current install layout.

### PR #15955 — NativeAOT dotnet tool packaging + E2E

**Key changes that affect us:**
- Adds `DotnetToolSmokeTests.cs` using `additionalVolumes` parameter on `CreateDockerTestTerminal`
- Uses `DetectDockerInstallMode` (legacy API) instead of `CliInstallStrategy.Detect()` — may indicate API churn

**Impact on our tests:**
- The `additionalVolumes` API could be useful for mounting test fixtures, but our tests just `echo` JSON directly in the container — no need
- `DotnetToolSmokeTests` validates `dotnet tool install` → `aspire new` → `aspire run` but does NOT verify installation detection behavior

**Recommendation: Plan a follow-up test** (after #15955 merges)
- Add a test to `DotnetToolSmokeTests` or a new class that verifies:
  - `aspire update --self` when installed via dotnet tool shows `dotnet tool update -g Aspire.Cli`
  - `.aspire-update.json` is NOT present after dotnet tool install
  - This would be the E2E equivalent of our unit test `DotNetTool_DetectedByProcessName`

### Summary: What to change NOW

1. **No infra changes needed now** — write tests matching current layout (`~/.aspire/bin/`)
2. **Keep tests simple** — use same `echo '...' > ~/.aspire/bin/.aspire-update.json` pattern as existing `SelfUpdateDetectionTests`
3. **Note in test comments** that paths may change with PR #15951
4. **Plan follow-up** for dotnet tool detection E2E after #15955 lands

---

## Todos

- [ ] Run Tier 1 unit tests (Aspire.Cli.Tests)
- [ ] Run Tier 2 E2E tests (Aspire.Cli.EndToEnd.Tests) — requires localhive archive
- [ ] Run Tier 2 acquisition tests (Aspire.Acquisition.Tests)
- [ ] Create `HomebrewSimulationTests.cs` with Homebrew symlink + sidecar tests
- [ ] Create `ScriptInstallOverrideTests.cs` with sidecar cleanup test
- [ ] Extend `EmbeddedChannelTests.cs` with sidecar interaction tests
- [ ] Run all new tests locally with localhive archive
- [ ] Document manual test gaps (Tier 4)
