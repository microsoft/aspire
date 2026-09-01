# Validating Aspire CLI Native AOT Symbols

The Aspire CLI ships as a NativeAOT executable. Its `.pdb` (Windows), `.dbg` (Linux), and `.dwarf` (macOS) symbol files are published to MSDL by the internal pipeline so that `dotnet-symbol --symbols` against a shipped `aspire` binary downloads the symbol file a debugger needs to resolve stack frames during crash triage.

See [`docs/ci/native-cli-packaging.md`](native-cli-packaging.md) for the build/sign side. This doc covers the **symbol** side: how the symbol artifacts reach MSDL, and how to verify locally that what arrives is paired with the right binary and actually useful for symbolication.

## Pipeline architecture

How CLI NativeAOT debug symbols reach MSDL/SymWeb.

### Where ILC writes symbols

For every `PublishAot=true` build, ILC emits the symbol artifact next to the binary at `artifacts/bin/Aspire.Cli/<config>/<tfm>/<rid>/native/`:

| Platform | Artifact | How it's produced |
|---|---|---|
| Windows | `aspire.pdb` | ILC linker output |
| Linux | `aspire.dbg` sidecar | `StripSymbols=true` (the default) splits debug info via `objcopy --only-keep-debug` + `--add-gnu-debuglink` |
| macOS | `aspire.dSYM/` bundle | `dsymutil` against the Mach-O |

These paths are governed by `Microsoft.NETCore.Native.targets` in the `Microsoft.DotNet.ILCompiler` package (`NativeOutputPath`, `NativeSymbolExt`). If they move, the staging paths in `eng/clipack/Common.projitems` go stale.

### macOS: only the inner DWARF is shipped, not the `.dSYM` bundle

ILC produces a `.dSYM` directory bundle whose payload is a Mach-O file with DWARF debug sections at `<binary>.dSYM/Contents/Resources/DWARF/<binary>`. The pipeline extracts just that inner file and ships it as `<binary>.dwarf`, mirroring dotnet/runtime's `dsymutil --flat` output (the form `SymbolUploadHelper`'s extension allowlist accepts).

The `.dSYM` bundle directory itself isn't shipped: it has no extension to match the allowlist, and Apple-native automatic symbolication via Spotlight UUID indexing (which would benefit from the bundle form) is the open work tracked by [dotnet/runtime#88286](https://github.com/dotnet/runtime/issues/88286).

### Two arcade publishing routes (one for Windows, one for Linux+macOS)

Symbols flow through one of two distinct arcade publishing routes, depending on the target platform's symbol format:

* **Windows `.pdb`** — loose-file path via `FilesToPublishToSymbolServer` in `eng/Publishing.props`. Arcade's `PrepLoosePdbsForPublish` hard-filters to `.pdb`/`.dll` only, so this path is Windows-only.

* **Linux `.dbg` / macOS `.dwarf`** — packed into a NuGet symbol package (`.symbols.nupkg`) by `eng/clipack/Aspire.Cli.NativeSymbols.proj` (invoked from `eng/clipack/Common.projitems`'s `_PackNativeAotSymbols` target on the per-RID build agent) and routed via arcade's `_ExistingSymbolPackage` filter to the Symbols asset category. `SymbolUploadHelper` opens the `.symbols.nupkg` with raw `ZipFile.Open` (not NuGet OPC), filters entries by an extension allowlist that includes `.dbg`/`.dwarf`/`.so`/`.dylib`, then runs `symbol.exe adddirectory` on the extracted contents.

The split isn't aesthetic — arcade's loose-pdb path doesn't accept ELF/Mach-O sidecars, and the `.symbols.nupkg` path predates the loose-pdb path. The `.symbols.nupkg` itself is a real NuGet symbol package: NuGet's `PackTask`, driven by `eng/clipack/Aspire.Cli.NativeSymbols.proj`, uses the `TfmSpecificDebugSymbolsFile` hook + `AllowedOutputExtensionsInSymbolsPackageBuildOutputFolder` allowlist to route the platform-specific debug-info file into the `.symbols.nupkg` only (not the empty companion main `.nupkg`, which is discarded). See [dotnet/runtime's `runtime.native.System.IO.Ports`](https://github.com/dotnet/runtime/blob/main/src/libraries/System.IO.Ports/pkg/runtime.native.System.IO.Ports.props) for the prior-art pattern.

### Per-RID coverage gate

`eng/Publishing.props`'s `_PublishBlobItems` target enforces — alongside its existing checks for archives, RID-specific tool packages, and npm packages — that every RID with an `eng/clipack/Aspire.Cli.<rid>.csproj` has a matching symbol artifact: an `aspire.pdb` under `artifacts/native-symbols/<config>/native_symbols_<rid>/` for `win-*` RIDs, and an `Aspire.Cli.<rid>.<version>.symbols.nupkg` in `Shipping/` for `linux-*`/`osx-*` RIDs. The expected RID set comes from the same `_ExpectedCliRids` item the other checks use, so there is one source of truth and no parallel list to drift.

### Symbol-server keys

`symstore`'s SSQP keys are computed from the file's intrinsic build-id (ELF `.note.gnu.build-id` on Linux, Mach-O `LC_UUID` on macOS, CodeView GUID+Age in PE on Windows), *not* from the in-package path or filename:

| Platform | SSQP key form |
|---|---|
| Windows | `aspire.pdb/<GUID><Age>/aspire.pdb` |
| Linux | `_.debug/elf-buildid-sym-<id>/_.debug` |
| macOS | `_.dwarf/mach-uuid-sym-<uuid>/_.dwarf` |

`dotnet-symbol` computes the same key from the shipped binary at lookup time, which is how a customer's stack-trace symbolication finds the right symbol file. See [dotnet/symstore SSQP_Key_Conventions](https://github.com/dotnet/symstore/blob/main/docs/specs/SSQP_Key_Conventions.md) for the canonical spec.

### Upstream references

When debugging a "`dotnet-symbol` returns nothing" report, or planning an arcade SDK / .NET SDK / Xcode bump, these are the upstream sources that drive the contracts above:

* **dotnet/runtime** — uses this same `.dbg`/`.dwarf` symbol-package path for CoreCLR and libraries native symbols:
  - [`eng/liveBuilds.targets`](https://github.com/dotnet/runtime/blob/main/eng/liveBuilds.targets#L122-L141) — CoreCLR `RuntimeFiles` includes `*.pdb;*.dbg;*.dwarf`
  - [`eng/native/functions.cmake`](https://github.com/dotnet/runtime/blob/main/eng/native/functions.cmake#L362-L431) — defaults `dsymutil --flat` for macOS, producing flat `.dwarf` instead of `.dSYM` bundles
  - [`Microsoft.DotNet.ILCompiler.pkgproj`](https://github.com/dotnet/runtime/blob/main/src/installer/pkg/projects/Microsoft.DotNet.ILCompiler/Microsoft.DotNet.ILCompiler.pkgproj#L54-L63) — excludes `.dbg`/`.dwarf`/`.dSYM` from the ILC package because they flow through the symbol package
* **dotnet/arcade** — the `SymbolUploadHelper.cs` plumbing:
  - [extension allowlist](https://github.com/dotnet/arcade/blob/main/src/Microsoft.DotNet.Internal.SymbolHelper/SymbolUploadHelper.cs#L37) — `.dbg`/`.dwarf`/`.so`/`.dylib` entries kept
  - [`AddPackageToRequest`](https://github.com/dotnet/arcade/blob/main/src/Microsoft.DotNet.Internal.SymbolHelper/SymbolUploadHelper.cs#L273) — raw `ZipFile.Open` extraction
* **dotnet/symstore** — symbol-server lookup side:
  - [SSQP key conventions](https://github.com/dotnet/symstore/blob/main/docs/specs/SSQP_Key_Conventions.md)
  - `Microsoft.SymbolStore.KeyGenerators` (one per format: `PortableFileKeyGenerator`, `ELFFileKeyGenerator`, `MachOKeyGenerator`)

## Local validation

`eng/scripts/validate-cli-symbols.ps1` is the on-demand validation tool. It does not run in CI.

Pipeline success — even a green MSDL upload — does not prove that the right symbol file was paired with the right binary, that its bytes survived packaging intact, or that those bytes can actually resolve a stack frame. MSDL will happily accept mismatched, malformed, or unresolvable bytes; it just stores them. The first time anyone discovers the problem is the next crash triage attempt, which can be months after ship, and the symbols for already-shipped builds are unrecoverable. The script exists to catch all three failure modes locally before they ship.

For script usage / parameters / examples, see the comment-based help:

```pwsh
Get-Help eng/scripts/validate-cli-symbols.ps1 -Detailed
```

### What it verifies

The script reproduces the full symbol round-trip locally without uploading anything to MSDL. Three checks per RID, ordered loosest to strictest:

| Check | What it proves | If it fails, suspect |
|---|---|---|
| **A.** Identifier symmetry | Binary intrinsic ID (PDB GUID+Age / ELF BuildID / Mach-O LC_UUID) matches the symbol file's ID. | A pipeline that paired up the wrong binary with the wrong symbol file — e.g., a staging step copying from the wrong `bin/<rid>/native/` location. |
| **B.** `dotnet-symbol` round-trip | A real `dotnet-symbol` invocation against a local HTTP symstore (rooted at the SSQP-keyed directory) downloads a byte-identical copy of the symbol file. | SSQP key derivation drifting from what `dotnet-symbol` computes from the binary — most often because `Microsoft.SymbolStore`'s per-format key generator changed, or because the symbol file's intrinsic ID isn't what we think it is. |
| **C.** Resolver-readable content | Platform symbolicator (`atos` / `addr2line` / `llvm-symbolizer`) can actually resolve the binary's entry-point VA using the file Check B downloaded. | Symbol file bytes are well-formed at the container level (zip, ELF note, Mach-O LC) but the debug-info inside is malformed — e.g., macOS flat-DWARF extraction produced a Mach-O with a corrupt `__DWARF` segment, or our `aspire.dwarf` rename lost something Apple's tools care about. **Check C is the only thing in the whole stack that proves the bytes are actually useful for symbolication.** |

The `.symbols.nupkg` pack/extract round-trip is not separately checked: NuGet's `PackTask` owns the format and arcade reads what the SDK writes, so the script's contract stops at the symbol file inside.

### When to run

#### Required before merging

Run the script (typically on the host RID is enough; full matrix if the change is cross-platform) before merging any of:

* Changes to `eng/clipack/Common.projitems` (`_PackNativeAotSymbols` target) or `eng/clipack/Aspire.Cli.NativeSymbols.proj` that affect what gets staged from `artifacts/bin/Aspire.Cli/.../native/` or how the `.symbols.nupkg` is packed.
* Changes to `eng/pipelines/templates/build_sign_native.yml` that affect the `native_symbols_<rid>` artifact publish (publish path, artifact name).
* Changes to `eng/Publishing.props` that touch `FilesToPublishToSymbolServer`, the `_ExpectedCliRids` derivation, or the per-RID symbol-coverage gate (the `_Missing{Pdb,SymbolPackage}Rids` checks).
* Changes to the Windows `build` job in `azure-pipelines.yml` / `azure-pipelines-unofficial.yml` that affect the `**/aspire.pdb` or `**/Aspire.Cli.*.symbols.nupkg` download/staging steps.
* Changes to `src/Aspire.Cli/Aspire.Cli.csproj` involving `PublishAot`, `CopyOutputSymbolsToPublishDirectory`, or other symbol-affecting properties.

#### Recommended

* **Arcade SDK version bumps** — `_ExistingSymbolPackage` filter, `SymbolUploadHelper`, `PrepLoosePdbsForPublish`, and `GatherPublishItems` have all evolved across arcade versions. A bump can silently re-route symbol artifacts to a different asset category, or skip them entirely.
* **.NET SDK / runtime upgrades** — `Microsoft.NETCore.Native.targets` is the source of truth for where ILC emits symbols (`NativeOutputPath`, `NativeSymbolExt`). If those move, our staging paths in `eng/clipack/Common.projitems` go stale.
* **Xcode upgrades on the macOS native job** — `dsymutil`'s default output mode and the `aspire.dSYM/Contents/Resources/DWARF/<name>` layout have moved before. Our flat-DWARF extraction depends on both.
* **Adding a new RID** to the AOT build matrix. The script's per-RID detection paths and SSQP key derivation need to cover the new RID before the pipeline does.
* **Investigating a real-world "`dotnet-symbol` returns nothing" report against the Aspire CLI** — run the script against the same RID / configuration / build to localize the failure to A, B, or C before suspecting MSDL.

#### When *not* to run

* PR doesn't touch any of the files listed above and isn't a SDK / arcade / Xcode bump → skip. Running the script for unrelated changes wastes cycles and doesn't catch anything.

### How to run

Quick reference (full docs via `Get-Help`):

```pwsh
# Validate on the host RID, build first
pwsh eng/scripts/validate-cli-symbols.ps1

# Re-use an existing build (faster iteration during triage)
pwsh eng/scripts/validate-cli-symbols.ps1 -SkipBuild

# Cross-RID (must have publishable artifacts for the target RID)
pwsh eng/scripts/validate-cli-symbols.ps1 -Rid linux-arm64
```

Exit code is `0` if every executed check passed, `1` if any failed. **Skipped checks (missing tooling) do not fail the script** — the script never blocks on absent platform tools, only on actual mismatches.

### How to interpret results

Each check reports one of:

* **PASS** — check ran and the invariant held.
* **SKIP** — check did not run; a warning explains why (most commonly a missing platform tool). The skip message is the diagnostic; re-running with the missing tool installed will produce a real PASS/FAIL.
* **N/A** — check is not meaningful for this RID by design (e.g., Check A's PDB-side ID comparison is skipped on Windows when `llvm-pdbutil` isn't installed).
* **FAIL** — check ran and the invariant was violated. Triage using the *suspect* column in the table above.

For triage, the checks are layered: each one assumes the previous one passed. If Check A fails, B/C will likely also fail because they all depend on the binary↔symbol pairing being correct. Always fix the lowest-numbered failure first; later failures often resolve themselves once it's fixed.

A useful baseline to keep in mind for a clean run on each RID:

| RID | A | B | C | Notes |
|---|---|---|---|---|
| `osx-arm64` / `osx-x64` | PASS | PASS | PASS | Needs Xcode CLT for `atos` / `dwarfdump` / `otool` |
| `linux-x64` / `linux-arm64` / `linux-musl-x64` | PASS | PASS | PASS | Needs binutils for `addr2line` / `readelf` |
| `win-x64` / `win-arm64` | PASS *(SKIP w/o LLVM)* | PASS | PASS *(SKIP w/o LLVM)* | Without LLVM, A and C skip but B runs via `PEReader`; this is the common author-machine state. |

Anything outside this baseline — especially a FAIL anywhere, or a SKIP for a check that should have been PASS on a configured machine — should be triaged before merging.

### What the script covers vs. what it skips

The script mirrors the production data flow described in [Pipeline architecture](#pipeline-architecture) without uploading. Each production step maps to one of the checks:

| Production step | Script equivalent |
|---|---|
| `eng/clipack/Aspire.Cli.NativeSymbols.proj` packs `.symbols.nupkg` (Linux/macOS) | Not separately checked — NuGet `PackTask` owns the format; arcade reads the same format the SDK writes. |
| Arcade's `_ExistingSymbolPackage` filter routes to Symbols asset | Implicit — the script's symbol file is the same one the pipeline ships |
| `SymbolUploadHelper.AddPackageToRequest` + `symbol.exe adddirectory` index by SSQP key | Check B's local symstore directory + `dotnet-symbol` lookup |
| Customer / triage runs `dotnet-symbol --symbols aspire` against MSDL | Check B against the local symstore (same protocol, same `dotnet-symbol` invocation) |
| Customer / triage runs `atos` / `addr2line` / `llvm-symbolizer` against the downloaded file | Check C, against the same downloaded file |

The script does *not* exercise: arcade's BAR registration, MSDL's ingestion, or the network path. Those are covered by the AzDO build itself. The script covers everything between "build outputs exist" and "a customer can symbolicate" — which is the part with no other automated coverage.

### Maintaining the script

The script's per-platform implementations of ID extraction and SSQP key derivation are encoded against the upstream sources documented in [Pipeline architecture](#pipeline-architecture). When you change the script — or when an upstream arcade SDK / .NET SDK / Xcode bump touches symbol handling — cross-check those sources.

The script intentionally has no test coverage; the `Microsoft.SymbolStore` library is the reference implementation. If the script and `Microsoft.SymbolStore` disagree on an SSQP key, trust `Microsoft.SymbolStore` and fix the script.

### When to retire this script

The script is intentionally a one-shot tool, not a CI check. Running it on every PR would catch nothing for the ~95% of PRs that don't touch symbol-affecting files; the [When to run](#when-to-run) discipline is what makes that work.

Retire it when at least one of:

* Aspire CLI grows post-build test coverage that exercises stack-trace symbolication on the shipped native binary.
* The symbol-publishing path has shipped through 2+ stable releases without regression, *and* at least one real-world MSDL crash-triage per RID has succeeded.
* Arcade adds end-to-end symbol-resolve verification to its publish path.
