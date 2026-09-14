<#
.SYNOPSIS
  Validates that the Aspire CLI's NativeAOT symbol artifacts produce a
  working symbol-server round-trip, without uploading anything to MSDL.

.DESCRIPTION
  Three checks, ordered loosest-to-strictest:

    A. Identifier symmetry   — the binary's intrinsic ID (PDB GUID/age,
       ELF BuildID, Mach-O LC_UUID) matches the symbol file's. If this
       holds, symstore's per-format key generator will compute the same
       SSQP key from either side, which is the only thing the symbol
       server's lookup depends on.

    B. dotnet-symbol round-trip — set up a local HTTP server rooted at a
       symstore directory laid out per the SSQP convention, place the
       packaged symbol at the computed path, and run dotnet-symbol
       against the binary. This exercises the exact lookup code path
       that customers hit against MSDL.

    C. Symbol resolution from downloaded file — resolve the binary's
       entry-point virtual address against the symbol file Check B just
       downloaded. Proves the file contains usable debug info, not just
       bytes that happen to pass an SSQP round-trip. (Check B alone
       would be satisfied by, e.g., a same-sized file of zeros that
       carried the correct build-id.)

  Prerequisites:
    - pwsh 7+ (ships Start-ThreadJob, used to host a local HTTP symbol
      server in-process for Check B)
    - dotnet SDK 10 (the script will use the repo-local SDK via
      dotnet.cmd / dotnet.sh when present)
    - Platform-specific ID extractors (for Check A):
        macOS:   dwarfdump (Xcode CLT)
        Linux:   readelf (binutils)
        Windows: System.Reflection.PortableExecutable.PEReader (from the
                 .NET runtime — always available) extracts the binary's
                 CodeView GUID+Age. The PDB-side ID uses llvm-pdbutil
                 when present; if absent, Check A's symmetry comparison
                 skips but Check B still runs end-to-end against the
                 binary-side ID (which is what dotnet-symbol queries).
    - Platform-specific symbolicators (for Check C):
        macOS:   atos + otool (Xcode CLT)
        Linux:   addr2line (binutils)
        Windows: llvm-symbolizer (LLVM). Entry-point VA extraction uses
                 System.Reflection.PortableExecutable.PEReader (from the
                 .NET runtime — always available); llvm-readobj is only a
                 secondary fallback. Check C skips with a warning if its
                 resolver is missing; the script never fails just because
                 platform tooling is absent.
    - dotnet-symbol global tool (the script installs it if absent)

.PARAMETER RepoRoot
  Repository root. Defaults to two levels above the script's directory
  (i.e., the standard eng/scripts/ → repo root resolution).

.PARAMETER Configuration
  Build configuration. Default: Release.

.PARAMETER Rid
  Target runtime identifier. Defaults to the host's win-x64/win-arm64/
  linux-x64/linux-arm64/osx-x64/osx-arm64.

.PARAMETER TargetFramework
  Target framework moniker for the Aspire CLI build. Default: net10.0.
  Used to compute the bin path under artifacts/bin/Aspire.Cli/<Configuration>/
  <TargetFramework>/<Rid>/native/.

.PARAMETER SkipBuild
  Reuse an existing build under artifacts/bin/Aspire.Cli/<Configuration>/
  <TargetFramework>/<Rid>/native/ instead of running dotnet publish.

.EXAMPLE
  # Validate on the host RID, build first
  pwsh eng/scripts/validate-cli-symbols.ps1

.EXAMPLE
  # Validate using a build that already exists
  pwsh eng/scripts/validate-cli-symbols.ps1 -SkipBuild

.EXAMPLE
  # Validate a non-host RID (e.g. linux-arm64 from x64)
  pwsh eng/scripts/validate-cli-symbols.ps1 -Rid linux-arm64

.NOTES
  Exit code: 0 if every executed check passes, 1 if any check fails.
  Skipped checks (missing tooling) don't fail the script.

  This script is intentionally a one-shot local-validation tool, not a
  CI test. CI exercises the same pipeline shape via the AzDO internal
  build (build_sign_native → native_symbols_<rid> artifacts → Windows
  build job staging → arcade publish to MSDL).

  For when to run, how to triage a failed check, and the relationship
  to the production pipeline, see docs/ci/cli-native-symbols.md.

.LINK
  docs/ci/cli-native-symbols.md
#>

[CmdletBinding()]
param(
  [string]$RepoRoot = "$PSScriptRoot/../..",
  [string]$Configuration = 'Release',
  [string]$Rid,
  [string]$TargetFramework = 'net10.0',
  [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

# Resolve absolute repo root
$RepoRoot = (Resolve-Path $RepoRoot -ErrorAction SilentlyContinue)?.Path
if (-not $RepoRoot -or -not (Test-Path "$RepoRoot/global.json")) {
  $RepoRoot = $PWD.Path
  if (-not (Test-Path "$RepoRoot/global.json")) {
    Write-Host "##[error]Run from repo root or pass -RepoRoot. Could not find global.json at $RepoRoot" -ForegroundColor Red
    exit 1
  }
}

# Detect host RID
if (-not $Rid) {
  $arch = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'arm64' } else { 'x64' }
  if ($IsWindows) { $Rid = "win-$arch" }
  elseif ($IsMacOS) { $Rid = "osx-$arch" }
  elseif ($IsLinux) { $Rid = "linux-$arch" }
  else { throw 'Unsupported host OS' }
}

$results = [ordered]@{}

Write-Host "=== Aspire CLI symbol validation ===" -ForegroundColor Cyan
Write-Host "RepoRoot:        $RepoRoot"
Write-Host "Configuration:   $Configuration"
Write-Host "TargetFramework: $TargetFramework"
Write-Host "Rid:             $Rid"
Write-Host ""

# 1. Build (or assume built)
$binDir = Join-Path $RepoRoot "artifacts/bin/Aspire.Cli/$Configuration/$TargetFramework/$Rid/native"
$binary = if ($Rid.StartsWith('win-')) { Join-Path $binDir 'aspire.exe' } else { Join-Path $binDir 'aspire' }

if ($SkipBuild) {
  if (-not (Test-Path $binary)) {
    Write-Host "##[error]-SkipBuild specified but binary not found at $binary" -ForegroundColor Red
    exit 1
  }
} else {
  Write-Host "--- Building Aspire.Cli (-r $Rid -p:PublishAot=true) ---" -ForegroundColor Yellow
  $dotnetCmd = if ($IsWindows) { "$RepoRoot/dotnet.cmd" } else { "$RepoRoot/dotnet.sh" }
  if (-not (Test-Path $dotnetCmd)) {
    # Fall back to system dotnet if the repo wrapper isn't there yet
    $dotnetCmd = 'dotnet'
  }
  & $dotnetCmd publish "$RepoRoot/src/Aspire.Cli/Aspire.Cli.csproj" -c $Configuration -r $Rid --self-contained -p:PublishAot=true -p:ContinuousIntegrationBuild=false 2>&1 | Tee-Object -Variable buildLog | Out-Null
  if ($LASTEXITCODE -ne 0) {
    Write-Host "##[error]Build failed:" -ForegroundColor Red
    $buildLog | Select-Object -Last 30 | ForEach-Object { Write-Host $_ }
    exit 1
  }
  Write-Host "Build OK"
  Write-Host ""
}

if (-not (Test-Path $binary)) {
  throw "Binary missing after build at $binary"
}

# 2. Locate the symbol-file artifact ILC produced.
$stagingDir = Join-Path $RepoRoot "artifacts/validate-symbols/$Rid"
if (Test-Path $stagingDir) { Remove-Item -Recurse -Force $stagingDir }
New-Item -ItemType Directory -Force -Path $stagingDir | Out-Null

if ($Rid.StartsWith('win-')) {
  # Windows: loose .pdb path (no .symbols.nupkg, since arcade ships these via FilesToPublishToSymbolServer)
  $srcSymbol = Join-Path $binDir 'aspire.pdb'
  $stagedSymbol = Join-Path $stagingDir 'aspire.pdb'
  if (-not (Test-Path $srcSymbol)) { throw "Expected $srcSymbol" }
  Copy-Item $srcSymbol $stagedSymbol -Force
  Write-Host "Windows PDB: $stagedSymbol"
}
elseif ($Rid.StartsWith('linux-')) {
  $srcSymbol = Join-Path $binDir 'aspire.dbg'
  if (-not (Test-Path $srcSymbol)) { throw "Expected $srcSymbol" }
  $stagedSymbol = Join-Path $stagingDir 'aspire.dbg'
  Copy-Item $srcSymbol $stagedSymbol -Force
  Write-Host "Linux .dbg: $stagedSymbol"
}
elseif ($Rid.StartsWith('osx-')) {
  $srcSymbol = Join-Path $binDir 'aspire.dSYM/Contents/Resources/DWARF/aspire'
  if (-not (Test-Path $srcSymbol)) { throw "Expected $srcSymbol" }
  $stagedSymbol = Join-Path $stagingDir 'aspire.dwarf'
  Copy-Item $srcSymbol $stagedSymbol -Force
  Write-Host "macOS .dwarf (extracted from .dSYM/Contents/Resources/DWARF/): $stagedSymbol"
}
else { throw "Unsupported RID: $Rid" }
Write-Host ""

# .symbols.nupkg pack/extract round-trip used to be Check B here; it was
# retired when eng/clipack/Aspire.Cli.NativeSymbols.proj took over packing
# from the YAML PowerShell heredoc. NuGet's PackTask now owns the format,
# so symmetry against arcade's _ExistingSymbolPackage / SymbolUploadHelper
# is no longer the script's contract to prove.

# === Check A: identifier symmetry ===
Write-Host "--- Check A: identifier symmetry ---" -ForegroundColor Yellow
$idBinary = $null
$idSymbol = $null
$idKind = ''
# Tracks "tool was found and invoked but failed or produced no match" so the
# SKIP-vs-FAIL gate at the end can distinguish missing optional tooling
# (legitimate SKIP) from a genuine extractor failure (FAIL). The script's
# "fail loudly when our own infra breaks" intent depends on this — silently
# downgrading an unexpected tool failure to SKIP would let a real
# binary↔symbol regression exit 0 unnoticed (the failure mode cycle-1 C3
# already fixed for the dotnet-symbol install path).
$extractorIssues = @()

if ($Rid.StartsWith('osx-')) {
  $idKind = 'Mach-O UUID'
  $dwarfdump = Get-Command dwarfdump -ErrorAction SilentlyContinue
  if ($dwarfdump) {
    foreach ($pair in @(@{ File = $binary; Var = 'idBinary'; Label = 'binary' },
                        @{ File = $stagedSymbol; Var = 'idSymbol'; Label = 'symbol' })) {
      $cmdOut = & dwarfdump --uuid $pair.File 2>&1
      $exit = $LASTEXITCODE
      $first = ($cmdOut | Select-Object -First 1) -as [string]
      if ($exit -ne 0) {
        $extractorIssues += "dwarfdump --uuid failed on $($pair.Label) (exit $exit): $($cmdOut -join '; ')"
      } elseif (-not ($first -match '^UUID: ')) {
        $extractorIssues += "dwarfdump --uuid produced no 'UUID: ' line for $($pair.Label) (output: $first)"
      } else {
        $id = $first -replace '^UUID: ', '' -replace ' .*$', '' -replace '-', ''
        Set-Variable -Name $pair.Var -Value $id.ToLowerInvariant()
      }
    }
  } else {
    Write-Host "  (dwarfdump not available — Check A's ID extraction skipped; install Xcode Command Line Tools)"
  }
}
elseif ($Rid.StartsWith('linux-')) {
  $idKind = 'ELF BuildID'
  $readelf = Get-Command readelf -ErrorAction SilentlyContinue
  if ($readelf) {
    foreach ($pair in @(@{ File = $binary; Var = 'idBinary'; Label = 'binary' },
                        @{ File = $stagedSymbol; Var = 'idSymbol'; Label = 'symbol' })) {
      $cmdOut = & readelf -n $pair.File 2>&1
      $exit = $LASTEXITCODE
      $m = $cmdOut | Select-String -Pattern 'Build ID:\s+([0-9a-f]+)' | Select-Object -First 1
      if ($exit -ne 0) {
        $extractorIssues += "readelf -n failed on $($pair.Label) (exit $exit): $($cmdOut -join '; ')"
      } elseif (-not $m) {
        $extractorIssues += "readelf -n produced no 'Build ID' line for $($pair.Label) — note section may be missing"
      } else {
        Set-Variable -Name $pair.Var -Value $m.Matches.Groups[1].Value
      }
    }
  } else {
    Write-Host "  (readelf not available — Check A's ID extraction skipped; install binutils)"
  }
}
elseif ($Rid.StartsWith('win-')) {
  $idKind = 'PDB GUID+Age'

  # Binary-side: use System.Reflection.PortableExecutable.PEReader, which
  # ships with .NET 10 runtime / pwsh 7+. The CodeView debug directory
  # entry in the PE carries the GUID+Age that the symbol server uses to
  # key the .pdb — and crucially, this is the ID dotnet-symbol computes
  # from the binary to drive its lookup. Doing this without an external
  # tool is important because Check B below depends on having $idBinary
  # even when LLVM isn't installed. We still try llvm-readobj as a
  # secondary fallback in case PEReader can't read the CodeView record.
  $peReaderError = $null
  try {
    $stream = [System.IO.File]::OpenRead($binary)
    try {
      $peReader = [System.Reflection.PortableExecutable.PEReader]::new($stream)
      try {
        foreach ($entry in $peReader.ReadDebugDirectory()) {
          if ($entry.Type -eq [System.Reflection.PortableExecutable.DebugDirectoryEntryType]::CodeView) {
            $cv = $peReader.ReadCodeViewDebugDirectoryData($entry)
            $guid = $cv.Guid.ToString('N').ToLowerInvariant()
            $age  = [Convert]::ToString($cv.Age, 16).ToLowerInvariant()
            $idBinary = "$guid$age"
            break
          }
        }
      } finally { $peReader.Dispose() }
    } finally { $stream.Dispose() }
  } catch {
    # PEReader is always available, so a hard exception here is unexpected.
    # Defer marking as FAIL until we see whether the llvm-readobj fallback
    # recovers — if it does, we got the ID and the exception was harmless.
    $peReaderError = $_.Exception.Message
    Write-Host "  (PEReader CodeView extraction failed: $peReaderError — will try llvm-readobj fallback)"
  }

  if (-not $idBinary) {
    # PEReader didn't yield a CodeView entry — fall back to llvm-readobj.
    $llvmReadobj = Get-Command llvm-readobj -ErrorAction SilentlyContinue
    if ($llvmReadobj) {
      $cv = & llvm-readobj --codeview $binary 2>&1
      $cvExit = $LASTEXITCODE
      $cvGuid = ($cv | Select-String -Pattern 'PDB70Signature:\s+\{?([0-9A-Fa-f-]+)\}?' | Select-Object -First 1)
      $cvAge  = ($cv | Select-String -Pattern 'Age:\s+(\d+)' | Select-Object -First 1)
      if ($cvExit -ne 0) {
        $extractorIssues += "llvm-readobj --codeview failed on binary (exit $cvExit): $($cv -join '; ')"
      } elseif (-not ($cvGuid -and $cvAge)) {
        $extractorIssues += "llvm-readobj --codeview ran cleanly on binary but did not produce PDB70Signature+Age"
      } else {
        $g = $cvGuid.Matches.Groups[1].Value -replace '-', ''
        $a = [int]$cvAge.Matches.Groups[1].Value
        $idBinary = "$($g.ToLowerInvariant())$([Convert]::ToString($a,16).ToLowerInvariant())"
      }
    } elseif ($peReaderError) {
      # No fallback installed AND PEReader threw — the binary-side ID is
      # genuinely unavailable due to extractor failure (not missing tooling).
      $extractorIssues += "PEReader CodeView extraction failed ($peReaderError) and llvm-readobj fallback is not installed"
    } else {
      # PEReader ran cleanly (no exception) but produced no CodeView entry,
      # and there is no fallback installed to second-source the result.
      # For a NativeAOT-built Windows aspire.exe this is a real artifact
      # failure — the toolchain always emits a CodeView debug directory
      # pointing at the .pdb sibling — and silently falling through to
      # SKIP would let a regression that produces a binary without an
      # SSQP key exit 0 unnoticed. Same shape, same fix as the
      # readelf/dwarfdump "ran cleanly but did not produce expected
      # output" issues above.
      $extractorIssues += "PEReader found no CodeView debug directory in binary — a NativeAOT-built Windows aspire.exe is expected to have one; the symbol-server lookup key cannot be derived from the binary without it"
    }
  }

  # PDB-side: requires llvm-pdbutil. If absent, Check A's symmetry
  # comparison can't run, but Check B will still proceed using $idBinary
  # (which is what dotnet-symbol computes from the binary to key its
  # request).
  $llvmPdbutil = Get-Command llvm-pdbutil -ErrorAction SilentlyContinue
  if ($llvmPdbutil) {
    $pdbSummary = & llvm-pdbutil dump --summary $stagedSymbol 2>&1
    $pdbExit = $LASTEXITCODE
    $guidLine = $pdbSummary | Select-String -Pattern 'GUID:\s+\{([0-9A-F-]+)\}' | Select-Object -First 1
    $ageLine  = $pdbSummary | Select-String -Pattern 'Age:\s+(\d+)' | Select-Object -First 1
    if ($pdbExit -ne 0) {
      $extractorIssues += "llvm-pdbutil dump --summary failed on symbol (exit $pdbExit): $($pdbSummary -join '; ')"
    } elseif (-not ($guidLine -and $ageLine)) {
      $extractorIssues += "llvm-pdbutil dump --summary ran cleanly on symbol but did not produce GUID+Age"
    } else {
      $guid = $guidLine.Matches.Groups[1].Value -replace '-', ''
      $age  = [int]$ageLine.Matches.Groups[1].Value
      $idSymbol = "$($guid.ToLowerInvariant())$([Convert]::ToString($age,16).ToLowerInvariant())"
    }
  } else {
    if ($idBinary) {
      Write-Host "  (llvm-pdbutil not available — PDB-side ID skipped; Check A's symmetry comparison will skip, but Check B will use the binary's CodeView ID: $idBinary)"
    } else {
      Write-Host "  (llvm-pdbutil not available and PEReader did not yield CodeView — both Check A and Check B will skip)"
    }
  }
}

if ($idBinary -and $idSymbol) {
  Write-Host "  $idKind from binary: $idBinary"
  Write-Host "  $idKind from symbol: $idSymbol"
  if ($idBinary -eq $idSymbol) {
    Write-Host "  ✅ A PASS: identifiers match" -ForegroundColor Green
    $results['A'] = $true
  } else {
    Write-Host "  ❌ A FAIL: identifier mismatch — symbol won't be findable from binary" -ForegroundColor Red
    $results['A'] = $false
  }
} elseif ($extractorIssues.Count -gt 0) {
  Write-Host "  ❌ A FAIL: extractor was present but did not produce a usable ID:" -ForegroundColor Red
  foreach ($issue in $extractorIssues) { Write-Host "    - $issue" -ForegroundColor Red }
  $results['A'] = $false
} else {
  Write-Host "  ⏭ A SKIP: missing tooling for ID extraction"
  $results['A'] = $null
}
Write-Host ""

# === Check B: dotnet-symbol round-trip via local symstore ===
# Served over a loopback HttpListener, not a file:// URI — dotnet-symbol's
# server-path parser only accepts http(s) (see HttpListener block below).
Write-Host "--- Check B: dotnet-symbol round-trip via local symstore ---" -ForegroundColor Yellow

# Ensure dotnet-symbol is installed. We attempt install when the tool is
# absent, but distinguish "install attempted and failed" (Check B FAIL — the
# script can't validate what it set out to validate) from "tool genuinely
# absent and install was not attempted" (Check B SKIP — consistent with the
# rest of the script's missing-prereq policy). Without that distinction, an
# install failure (feed outage, auth, SDK mismatch) silently looks like a
# clean SKIP and the script exits 0.
#
# Append ~/.dotnet/tools to PATH BEFORE the first Get-Command so an
# already-installed tool whose install dir isn't on PATH is discovered
# without going through `dotnet tool install -g`. That install would exit
# 1 with "Tool 'dotnet-symbol' is already installed", which the
# installFailed gate below would otherwise classify as a real install
# failure even though the tool is fully usable.
$env:PATH = "$env:PATH" + [System.IO.Path]::PathSeparator + (Join-Path $HOME '.dotnet/tools')
$dotnetSymbol = Get-Command dotnet-symbol -ErrorAction SilentlyContinue
$installFailed = $false
$installLog = $null
if (-not $dotnetSymbol) {
  Write-Host "  Installing dotnet-symbol globally..."
  $installLog = & dotnet tool install -g dotnet-symbol 2>&1
  $installExit = $LASTEXITCODE
  $dotnetSymbol = Get-Command dotnet-symbol -ErrorAction SilentlyContinue
  if ($installExit -ne 0 -or -not $dotnetSymbol) {
    $installFailed = $true
  }
}

if ($installFailed) {
  if ($installExit -ne 0) {
    Write-Host "  ❌ B FAIL: dotnet tool install -g dotnet-symbol failed (exit $installExit). Output:" -ForegroundColor Red
  } else {
    Write-Host "  ❌ B FAIL: dotnet tool install -g dotnet-symbol reported success but the tool is still not on PATH after install. Output:" -ForegroundColor Red
  }
  $installLog | ForEach-Object { Write-Host "    $_" }
  $results['B'] = $false
} elseif (-not $dotnetSymbol) {
  Write-Host "  ⏭ B SKIP: dotnet-symbol not available" -ForegroundColor Yellow
  $results['B'] = $null
} else {
  $store = Join-Path $stagingDir 'local-symstore'
  # Compute SSQP key + place symbol file at expected path. The key is computed
  # from the binary's intrinsic ID — that's what dotnet-symbol extracts at
  # lookup time. Prefer the symbol-side ID when available (covers the Check A
  # symmetry case end-to-end), fall back to the binary-side ID otherwise so
  # Check B still exercises the full round-trip even when only one side of
  # Check A could be extracted (e.g. Windows without llvm-pdbutil).
  $keyId = if ($idSymbol) { $idSymbol } elseif ($idBinary) { $idBinary } else { $null }
  $keyPath = $null
  if ($Rid.StartsWith('osx-') -and $keyId) {
    $keyPath = "_.dwarf/mach-uuid-sym-$keyId/_.dwarf"
  } elseif ($Rid.StartsWith('linux-') -and $keyId) {
    $keyPath = "_.debug/elf-buildid-sym-$keyId/_.debug"
  } elseif ($Rid.StartsWith('win-') -and $keyId) {
    $keyPath = "aspire.pdb/$keyId/aspire.pdb"
  }

  if ($keyPath) {
    $target = Join-Path $store $keyPath
    New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
    # Place the staged symbol at the SSQP-derived path so dotnet-symbol's
    # lookup finds it. On Windows we use the loose .pdb staged earlier; on
    # Linux/macOS we use the staged .dbg/.dwarf. $stagedSymbol covers both.
    Copy-Item $stagedSymbol $target -Force
    Write-Host "  Placed symbol at: $keyPath"

    $out = Join-Path $stagingDir 'dotnet-symbol-out'
    if (Test-Path $out) { Remove-Item -Recurse -Force $out }
    New-Item -ItemType Directory -Force -Path $out | Out-Null

    # dotnet-symbol only accepts http(s) server paths — not file:// — so spin
    # up a tiny HTTP server rooted at our local symstore. We use
    # System.Net.HttpListener (cross-platform, built into .NET) in a
    # Start-ThreadJob rather than shelling out to `python -m http.server`
    # for two reasons:
    #
    #   1. No external dependency. Earlier versions used python3 which is
    #      missing on stock Windows; `python.exe` resolves to the Microsoft
    #      Store stub on a clean Windows 10/11 box, which exits silently
    #      when invoked non-interactively. That left the script with a
    #      "server" that never bound the port and Check B failing with
    #      ConnectionRefused.
    #
    #   2. Actual readiness check. The old code just slept 2s and hoped;
    #      we now poll the listener until it serves a request (or time out).
    #
    # The HttpListener is constructed and Start()ed in the main thread so
    # we own its lifetime; the thread-job only consumes contexts off it.
    # Calling $listener.Close() from the main thread is the *only* way to
    # unblock the job's HttpListenerContext.GetContext() call (it's a
    # blocking unmanaged call that Stop-Job/cancellation tokens can't
    # interrupt).
    $port = 18080 + (Get-Random -Maximum 999)
    $listener = [System.Net.HttpListener]::new()
    $listener.Prefixes.Add("http://127.0.0.1:$port/")
    $listener.Start()
    $serverJob = Start-ThreadJob -ScriptBlock {
      param($listener, $root)
      while ($listener.IsListening) {
        try {
          $ctx = $listener.GetContext()
          $relPath = $ctx.Request.Url.AbsolutePath.TrimStart('/').Replace('/', [System.IO.Path]::DirectorySeparatorChar)
          $filePath = Join-Path $root $relPath
          if (Test-Path -LiteralPath $filePath -PathType Leaf) {
            $bytes = [System.IO.File]::ReadAllBytes($filePath)
            $ctx.Response.ContentLength64 = $bytes.Length
            $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
          } else {
            $ctx.Response.StatusCode = 404
          }
          $ctx.Response.Close()
        } catch {
          # listener closed from the main thread — exit the loop
          break
        }
      }
    } -ArgumentList $listener, $store

    # Poll until the listener answers (any HTTP response — including the
    # expected 404 for /__ping__ — proves it's bound and serving).
    $serverReady = $false
    for ($i = 0; $i -lt 30; $i++) {
      try {
        Invoke-WebRequest -Uri "http://127.0.0.1:$port/__ping__" -TimeoutSec 1 -UseBasicParsing -ErrorAction Stop | Out-Null
        $serverReady = $true; break
      } catch {
        if ($_.Exception.Response -and $_.Exception.Response.StatusCode -eq [System.Net.HttpStatusCode]::NotFound) {
          $serverReady = $true; break
        }
        Start-Sleep -Milliseconds 200
      }
    }

    if (-not $serverReady) {
      # The HttpListener.Start() above succeeded, so this is the validator's
      # own in-process server failing to serve — not a missing prereq. Fail
      # the check loudly (results['B']=$false) instead of skipping; a SKIP
      # here would mask a real validator infrastructure failure as a clean
      # run.
      Write-Host "  ❌ B FAIL: local HTTP symstore did not become ready within 6s" -ForegroundColor Red
      $results['B'] = $false
      try { $listener.Close() } catch { }
      Stop-Job $serverJob -ErrorAction SilentlyContinue | Out-Null
      Remove-Job $serverJob -Force -ErrorAction SilentlyContinue | Out-Null
    } else {
      try {
        $serverUrl = "http://127.0.0.1:$port"
        Write-Host "  Local HTTP symstore: $serverUrl  (Job $($serverJob.Id))"
        Write-Host "  Running: dotnet-symbol --symbols $binary --server-path $serverUrl -o $out"
        & dotnet-symbol --symbols $binary --server-path $serverUrl -o $out -d 2>&1 | Tee-Object -Variable dsLog | Out-Null
        $dsExit = $LASTEXITCODE
        if ($dsExit -ne 0) {
          Write-Host "  ❌ B FAIL: dotnet-symbol exited $dsExit" -ForegroundColor Red
          $dsLog | ForEach-Object { Write-Host "    $_" }
          $results['B'] = $false
        } else {
          $downloaded = @(Get-ChildItem -Path $out -Recurse -File)
          if ($downloaded.Count -eq 0) {
            Write-Host "  ❌ B FAIL: dotnet-symbol succeeded but no file was downloaded" -ForegroundColor Red
            $dsLog | ForEach-Object { Write-Host "    $_" }
            $results['B'] = $false
          } else {
            $downloadedHash = (Get-FileHash $downloaded[0].FullName -Algorithm SHA256).Hash
            $sourceHash = (Get-FileHash $stagedSymbol -Algorithm SHA256).Hash
            Write-Host "  Downloaded:    $($downloaded[0].FullName) ($($downloaded[0].Length) bytes)"
            Write-Host "  Source SHA-256:     $sourceHash"
            Write-Host "  Downloaded SHA-256: $downloadedHash"
            if ($downloadedHash -eq $sourceHash) {
              Write-Host "  ✅ B PASS: dotnet-symbol retrieved byte-identical symbol via local HTTP symstore" -ForegroundColor Green
              $results['B'] = $true
            } else {
              Write-Host "  ❌ B FAIL: downloaded file differs from source" -ForegroundColor Red
              $results['B'] = $false
            }
          }
        }
      } finally {
        try { $listener.Close() } catch { }
        Stop-Job $serverJob -ErrorAction SilentlyContinue | Out-Null
        Remove-Job $serverJob -Force -ErrorAction SilentlyContinue | Out-Null
      }
    }
  } else {
    Write-Host "  ⏭ B SKIP: could not compute SSQP key (no identifier from Check A)" -ForegroundColor Yellow
    $results['B'] = $null
  }
}
Write-Host ""

# === Check C: symbol resolution from the downloaded file ===
# Check B only proves "the symbol-server protocol delivers byte-identical
# bytes for the right key". A same-sized file of zeros carrying the right
# build-id would pass it. Check C points the platform's native symbolicator
# at the file Check B just downloaded and asks it to resolve the binary's
# entry-point virtual address — proving the bytes are actually a usable
# debug-info container.
Write-Host "--- Check C: symbol resolution from downloaded file ---" -ForegroundColor Yellow

if ($results['B'] -ne $true) {
  Write-Host "  ⏭ C SKIP: Check B did not pass; nothing to resolve against" -ForegroundColor Yellow
  $results['C'] = $null
} else {
  $downloadedSymbol = (Get-ChildItem -Path $out -Recurse -File | Select-Object -First 1).FullName

  # 1. Extract the binary's entry-point VA per format.
  $entryVa = $null
  $textBaseHex = $null  # Mach-O only — atos needs the segment load address
  # Tracks "extractor was present but did not yield a usable VA" so the
  # SKIP-vs-FAIL gate below distinguishes missing optional tooling from a
  # real extraction failure. Mirrors the Check A pattern above.
  $entryExtractorIssues = @()

  if ($Rid.StartsWith('osx-')) {
    # Mach-O LC_MAIN.entryoff is a __TEXT-segment-relative offset; the VA is
    # (__TEXT vmaddr) + entryoff. Example output we parse:
    #   Load command N
    #         cmd LC_SEGMENT_64
    #     ...
    #         segname __TEXT
    #      vmaddr 0x0000000100000000
    #   Load command M
    #         cmd LC_MAIN
    #     entryoff 364504
    $otoolCmd = Get-Command otool -ErrorAction SilentlyContinue
    if ($otoolCmd) {
      $otool = & otool -l $binary 2>&1
      $otoolExit = $LASTEXITCODE
      if ($otoolExit -ne 0) {
        $entryExtractorIssues += "otool -l failed on binary (exit $otoolExit): $($otool -join '; ')"
      } else {
        $inText = $false
        $textBase = 0
        foreach ($line in $otool) {
          if ($line -match 'segname __TEXT')  { $inText = $true; continue }
          if ($inText -and $line -match 'vmaddr (0x[0-9a-fA-F]+)') {
            $textBase = [Convert]::ToInt64($matches[1], 16)
            break
          }
        }
        $entryMatch = $otool | Select-String -Pattern 'entryoff (\d+)' | Select-Object -First 1
        if ($textBase -and $entryMatch) {
          $entryOff = [int]$entryMatch.Matches.Groups[1].Value
          $entryVa = $textBase + $entryOff
          $textBaseHex = '0x{0:x}' -f $textBase
        } else {
          $entryExtractorIssues += "otool -l ran cleanly on binary but did not produce both __TEXT vmaddr and LC_MAIN entryoff"
        }
      }
    } else {
      Write-Host "  (otool not available — Check C's entry-point VA extraction skipped; install Xcode Command Line Tools)"
    }
  }
  elseif ($Rid.StartsWith('linux-')) {
    # ELF entry point lives in the file header — readelf -h emits one line:
    #   Entry point address:               0x4e300
    $readelfCmd = Get-Command readelf -ErrorAction SilentlyContinue
    if ($readelfCmd) {
      $hdr = & readelf -h $binary 2>&1
      $hdrExit = $LASTEXITCODE
      $m = $hdr | Select-String -Pattern 'Entry point address:\s+(0x[0-9a-fA-F]+)' | Select-Object -First 1
      if ($hdrExit -ne 0) {
        $entryExtractorIssues += "readelf -h failed on binary (exit $hdrExit): $($hdr -join '; ')"
      } elseif (-not $m) {
        $entryExtractorIssues += "readelf -h ran cleanly on binary but did not produce an 'Entry point address' line"
      } else {
        $entryVa = [Convert]::ToInt64($m.Matches.Groups[1].Value, 16)
      }
    } else {
      Write-Host "  (readelf not available — Check C's entry-point VA extraction skipped; install binutils)"
    }
  }
  elseif ($Rid.StartsWith('win-')) {
    # PE entry-point VA = ImageBase + AddressOfEntryPoint, both fields of
    # the PE optional header. Use System.Reflection.PortableExecutable.PEReader
    # (ships with the .NET runtime, no LLVM required) for parity with the
    # binary-side ID extraction in Check A. Fall back to llvm-readobj if
    # PEReader can't read the headers for some reason.
    $peReaderError = $null
    try {
      $stream = [System.IO.File]::OpenRead($binary)
      try {
        $peReader = [System.Reflection.PortableExecutable.PEReader]::new($stream)
        try {
          $peHdr = $peReader.PEHeaders.PEHeader
          if ($peHdr) {
            $entryVa = [int64]$peHdr.ImageBase + [int64]$peHdr.AddressOfEntryPoint
          }
        } finally { $peReader.Dispose() }
      } finally { $stream.Dispose() }
    } catch {
      # Defer marking FAIL until we see whether the llvm-readobj fallback
      # recovers (same pattern as Check A).
      $peReaderError = $_.Exception.Message
      Write-Host "  (PEReader entry-point extraction failed: $peReaderError — will try llvm-readobj fallback)"
    }

    if (-not $entryVa) {
      $llvmReadobj = Get-Command llvm-readobj -ErrorAction SilentlyContinue
      if ($llvmReadobj) {
        # llvm-readobj --file-headers output shape:
        #   ImageBase:           0x140000000
        #   AddressOfEntryPoint: 0x12345
        $hdrs = & llvm-readobj --file-headers $binary 2>&1
        $hdrsExit = $LASTEXITCODE
        $aepM = $hdrs | Select-String -Pattern 'AddressOfEntryPoint:\s+(0x[0-9a-fA-F]+)' | Select-Object -First 1
        $ibM  = $hdrs | Select-String -Pattern 'ImageBase:\s+(0x[0-9a-fA-F]+)' | Select-Object -First 1
        if ($hdrsExit -ne 0) {
          $entryExtractorIssues += "llvm-readobj --file-headers failed on binary (exit $hdrsExit): $($hdrs -join '; ')"
        } elseif (-not ($aepM -and $ibM)) {
          $entryExtractorIssues += "llvm-readobj --file-headers ran cleanly on binary but did not produce ImageBase+AddressOfEntryPoint"
        } else {
          $aep = [Convert]::ToInt64($aepM.Matches.Groups[1].Value, 16)
          $ib  = [Convert]::ToInt64($ibM.Matches.Groups[1].Value, 16)
          $entryVa = $aep + $ib
        }
      } elseif ($peReaderError) {
        $entryExtractorIssues += "PEReader entry-point extraction failed ($peReaderError) and llvm-readobj fallback is not installed"
      } else {
        # PEReader ran cleanly (no exception) but produced no entry-point
        # VA, and there is no fallback installed. For any valid PE binary
        # this means PEHeaders.PEHeader was null — i.e. the file isn't a
        # parseable PE, which is a real artifact failure for a Windows
        # aspire.exe. Same reasoning as the Check A PEReader-no-CodeView
        # case above.
        $entryExtractorIssues += "PEReader read no PE optional header from binary — the file is not a parseable PE, so entry-point VA cannot be derived"
      }
    }
  }

  if (-not $entryVa) {
    if ($entryExtractorIssues.Count -gt 0) {
      Write-Host "  ❌ C FAIL: entry-point extractor was present but did not produce a usable VA:" -ForegroundColor Red
      foreach ($issue in $entryExtractorIssues) { Write-Host "    - $issue" -ForegroundColor Red }
      $results['C'] = $false
    } else {
      Write-Host "  ⏭ C SKIP: could not determine binary entry-point VA" -ForegroundColor Yellow
      $results['C'] = $null
    }
  } else {
    $entryVaHex = '0x{0:x}' -f $entryVa
    Write-Host "  Binary entry-point VA: $entryVaHex"

    # 2. Resolve the address against the downloaded symbol file.
    $symResult = $null
    $resolverTool = $null
    if ($Rid.StartsWith('osx-')) {
      $atos = Get-Command atos -ErrorAction SilentlyContinue
      if ($atos) {
        $resolverTool = 'atos'
        # atos resolves an address against a Mach-O symbol container; -l
        # supplies the load address (here the __TEXT vmaddr, since the file
        # isn't actually mapped). Output shape: "main (in aspire.dwarf) (main.cpp:228)"
        $symResult = (& atos -o $downloadedSymbol -l $textBaseHex $entryVaHex 2>$null | Select-Object -First 1)
      }
    }
    elseif ($Rid.StartsWith('linux-')) {
      $a2l = Get-Command addr2line -ErrorAction SilentlyContinue
      if ($a2l) {
        $resolverTool = 'addr2line'
        # addr2line -f prints the function name on line 1 and file:line on
        # line 2; -C demangles. We only assert on the function name.
        # Output shape: "_start\n??:?" or "main\nmain.c:42"
        $symResult = (& addr2line -e $downloadedSymbol -f -C $entryVaHex 2>$null | Select-Object -First 1)
      }
    }
    elseif ($Rid.StartsWith('win-')) {
      $llvm = Get-Command llvm-symbolizer -ErrorAction SilentlyContinue
      if ($llvm) {
        $resolverTool = 'llvm-symbolizer'
        # llvm-symbolizer reads the PE's CodeView record to locate the PDB,
        # which defaults to the .pdb path embedded at link time. Stage the
        # downloaded .pdb adjacent to a copy of the binary so resolution
        # uses the file dotnet-symbol actually delivered (not whatever PDB
        # might be sitting next to the original build output).
        $resolveDir = Join-Path $stagingDir 'resolve-test'
        New-Item -ItemType Directory -Force -Path $resolveDir | Out-Null
        $binCopy = Join-Path $resolveDir 'aspire.exe'
        $pdbCopy = Join-Path $resolveDir 'aspire.pdb'
        Copy-Item $binary $binCopy -Force
        Copy-Item $downloadedSymbol $pdbCopy -Force
        # Output shape: "wmain\nC:\path\to\main.cpp:228:0"
        $symResult = (& llvm-symbolizer --obj=$binCopy $entryVaHex 2>$null | Select-Object -First 1)
      }
    }

    if (-not $resolverTool) {
      Write-Host "  ⏭ C SKIP: no symbolicator available for $Rid" -ForegroundColor Yellow
      $results['C'] = $null
    }
    elseif (-not $symResult `
        -or $symResult -match '^\s*\?\?\s*$' `
        -or $symResult -match '^\s*0x[0-9a-fA-F]+\s*$') {
      # Empty, "??" (addr2line "unknown"), or a hex address echoed back
      # (llvm-symbolizer's "no debug info" output) all indicate the symbol
      # file failed to resolve — meaning it doesn't actually contain usable
      # debug info for this address.
      Write-Host "  ❌ C FAIL: $resolverTool could not resolve $entryVaHex; got: '$symResult'" -ForegroundColor Red
      $results['C'] = $false
    } else {
      Write-Host "  Resolved via ${resolverTool}: $entryVaHex → $symResult"
      Write-Host "  ✅ C PASS: downloaded symbol contains usable debug info" -ForegroundColor Green
      $results['C'] = $true
    }
  }
}
Write-Host ""

# Summary
Write-Host "=== Summary for $Rid ===" -ForegroundColor Cyan
$exitCode = 0
foreach ($k in $results.Keys) {
  $v = $results[$k]
  $glyph = if ($v -eq $true) { '✅' } elseif ($v -eq $false) { '❌' } else { '⏭' }
  Write-Host "  $glyph Check $k"
  if ($v -eq $false) { $exitCode = 1 }
}
exit $exitCode
