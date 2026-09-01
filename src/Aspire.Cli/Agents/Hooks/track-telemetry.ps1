# Telemetry tracking hook for Aspire Skills.
#
# Runs on every agent PostToolUse event. Reads the hook JSON from stdin, detects when an
# Aspire skill, Aspire MCP tool, or Aspire skill reference file was used, and forwards a
# low-cardinality usage event to `aspire agent telemetry`. The Aspire CLI command owns the
# actual opt-out + publishing logic; this script only classifies the event and shells out.
#
# Hook contract: a PostToolUse hook MUST always print a single JSON object to stdout and exit
# 0, otherwise it can break the agent session. Intentional exits call Write-Success; a top-level
# trap is the last-resort net that satisfies the contract on any unhandled terminating error.
#
# Runs with PowerShell 7+ (pwsh): the hook installer always registers it as `pwsh -File ...`, which
# the GitHub Copilot CLI hooks reference also makes a hard Windows prerequisite. See
# track-telemetry.sh for the full client-format / event-type / privacy notes (the logic here mirrors
# that script).

$ErrorActionPreference = "SilentlyContinue"

# Last-resort net for the hook contract: any unhandled terminating error still prints exactly one
# {"continue":true} and exits 0 instead of breaking the agent's tool loop.
trap {
    Write-Output '{"continue":true}'
    exit 0
}

# Allowlist of Aspire-owned skill names (keep in sync with github.com/microsoft/aspire-skills).
# A shared .agents/skills directory can also contain third-party skills, so a path/name is only
# treated as Aspire when its skill segment is one of these.
$AspireSkills = @('aspire', 'aspire-init', 'aspireify', 'aspire-orchestration', 'aspire-deployment', 'aspire-monitoring')

function Write-Success {
    Write-Output '{"continue":true}'
    exit 0
}

function Test-OptOut([string] $value) {
    # PowerShell -eq / -ieq are case-insensitive, so this accepts 1 and any-case true,
    # matching the lower-cased check in track-telemetry.sh.
    return $value -eq '1' -or $value -ieq 'true'
}

# Opt out when the Aspire CLI telemetry switch is set. This is the single opt-out that also
# gates the `aspire agent telemetry` command path, so honoring it here avoids spawning the CLI
# at all for opted-out users.
if (Test-OptOut $env:ASPIRE_CLI_TELEMETRY_OPTOUT) {
    Write-Success
}

# Read the entire payload from stdin (one complete JSON object per hook invocation). A read
# failure is exotic and falls through to the top-level trap, which still returns success.
$rawInput = [Console]::In.ReadToEnd()

if ([string]::IsNullOrWhiteSpace($rawInput)) {
    Write-Success
}

# Fast path: most PostToolUse events are not Aspire-related. Everything we track carries
# "skill"/"aspire" in the payload (the skill tool name, an aspire-/mcp__aspire__ tool name, or a
# .../skills/<aspire-skill>/ path), so skip JSON parsing entirely when neither appears.
if ($rawInput -notmatch 'skill|aspire') {
    Write-Success
}

# A malformed payload yields $null here (or throws into the trap); either way we never guess.
$data = $rawInput | ConvertFrom-Json
if (-not $data) {
    Write-Success
}

# Copilot CLI camelCase vs Claude/VS Code snake_case.
$toolName = $data.toolName
if (-not $toolName) { $toolName = $data.tool_name }

$sessionId = $data.sessionId
if (-not $sessionId) { $sessionId = $data.session_id }

# Copilot encodes toolArgs as a JSON string with escaped quotes (\"field\":\"value\"); unescaping
# them turns it — and the nested-object form Claude/VS Code send (tool_input:{...}) — into the same
# flat "field":"value" pairs. The classification below reads nested fields straight out of this text
# by name, best-effort, rather than assuming the payload's shape or which client produced it.
$normalizedInput = $rawInput -replace '\\"', '"'

$timestamp = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")

# Detect the client (used only for a low-cardinality client-name tag).
$propertyNames = @()
if ($data.PSObject -and $data.PSObject.Properties) { $propertyNames = $data.PSObject.Properties.Name }
$hasHookEventName = $propertyNames -contains 'hook_event_name'
$hasToolArgs = $propertyNames -contains 'toolArgs'

if ($env:COPILOT_CLI -eq '1') {
    $clientName = 'copilot-cli'
} elseif ($hasHookEventName) {
    $toolUseId = [string]$data.tool_use_id
    $transcriptPath = ([string]$data.transcript_path) -replace '\\', '/'
    if ($toolUseId -match '__vscode' -or $transcriptPath -match '/Code( - Insiders)?/') {
        $clientName = 'vscode'
    } else {
        $clientName = 'claude-code'
    }
} elseif ($hasToolArgs) {
    $clientName = 'copilot-cli'
} else {
    $clientName = 'unknown'
}

if (-not $toolName) {
    Write-Success
}

# Read a string field's value from anywhere in the normalized payload by name (first match wins).
# Used for the nested tool-input values whose container shape differs across clients.
function Get-PayloadField([string] $name) {
    $match = [regex]::Match($normalizedInput, '"' + [regex]::Escape($name) + '"\s*:\s*"([^"]*)"')
    if ($match.Success) { return $match.Groups[1].Value }
    return $null
}

function Test-AspireSkill([string] $candidate) {
    return $AspireSkills -contains $candidate
}

$shouldTrack = $false
$eventType = $null
$skillName = $null
$mcpToolName = $null
$fileReference = $null

# --- skill_invocation via the skill/Skill tool ---
if ($toolName -eq 'skill' -or $toolName -eq 'Skill') {
    $candidate = [string](Get-PayloadField 'skill')
    # Claude prefixes plugin skill names, e.g. "aspire:aspire-deployment".
    if ($candidate.StartsWith('aspire:')) { $candidate = $candidate.Substring(7) }
    if (Test-AspireSkill $candidate) {
        $skillName = $candidate
        $eventType = 'skill_invocation'
        $shouldTrack = $true
    }
}

# --- skill_invocation / reference_file_read via a file read tool ---
if ($toolName -eq 'view' -or $toolName -eq 'Read' -or $toolName -eq 'read_file') {
    $pathToCheck = Get-PayloadField 'path'
    if (-not $pathToCheck) { $pathToCheck = Get-PayloadField 'filePath' }
    if (-not $pathToCheck) { $pathToCheck = Get-PayloadField 'file_path' }
    if ($pathToCheck) {
        # Normalize separators and collapse duplicate slashes.
        $normalized = ($pathToCheck -replace '\\', '/') -replace '/+', '/'
        # Capture the skill segment after skills/ and the remainder.
        $skillSegment = $null
        $remainder = $null
        if ($normalized -match '(?:^|/)skills/([^/]+)/(.+)$') {
            $skillSegment = $Matches[1]
            $remainder = $Matches[2]
        }
        if ($skillSegment -and (Test-AspireSkill $skillSegment)) {
            if ($remainder -imatch '(^|/)skill\.md$') {
                # A SKILL.md read is a skill invocation, not a reference-file read.
                if (-not $shouldTrack) {
                    $skillName = $skillSegment
                    $eventType = 'skill_invocation'
                    $shouldTrack = $true
                }
            } elseif (-not $shouldTrack -and $remainder) {
                # Forward only the relative path after skills/ (e.g. aspire/references/deploy.md).
                $fileReference = "$skillSegment/$remainder"
                $eventType = 'reference_file_read'
                $shouldTrack = $true
            }
        }
    }
}

# --- tool_invocation via an Aspire MCP tool prefix ---
# Conservative exact prefixes:
#   Copilot: aspire-<tool>   Claude: mcp__aspire__<tool>   VS Code: mcp_aspire_<tool>
if ($toolName.StartsWith('aspire-') -or $toolName.StartsWith('mcp__aspire__') -or $toolName.StartsWith('mcp_aspire_')) {
    $mcpToolName = $toolName
    $eventType = 'tool_invocation'
    $shouldTrack = $true
}

if (-not $shouldTrack) {
    Write-Success
}

# Resolve the Aspire CLI. ASPIRE_CLI_COMMAND lets tests substitute a recording stub.
$aspireCmd = $env:ASPIRE_CLI_COMMAND
if (-not $aspireCmd) { $aspireCmd = 'aspire' }

# Build the argument vector explicitly so untrusted hook values are passed as discrete args.
$cmdArgs = @('agent', 'telemetry', '--event-type', $eventType, '--client-name', $clientName, '--timestamp', $timestamp)
if ($sessionId) { $cmdArgs += @('--session-id', [string]$sessionId) }
if ($skillName) { $cmdArgs += @('--skill-name', $skillName) }
if ($mcpToolName) { $cmdArgs += @('--tool-name', $mcpToolName) }
if ($fileReference) { $cmdArgs += @('--file-reference', $fileReference) }

# Bound the call so a hung or slow CLI can't stall the agent's tool loop (mirrors the bash
# `timeout 10`). Run the CLI as a child process we can wait on and kill: an executable on PATH
# (the production `aspire`) is launched directly, while a .ps1 — used by the hook's tests via
# ASPIRE_CLI_COMMAND — is launched through pwsh. stdout/stderr are redirected and drained so a
# banner/log line can neither contaminate the hook's stdout nor deadlock on a full pipe buffer.
# Any failure here is swallowed by the top-level trap.
$psi = [System.Diagnostics.ProcessStartInfo]::new()
if ($aspireCmd -like '*.ps1') {
    $psi.FileName = 'pwsh'
    foreach ($a in (@('-NoProfile', '-File', $aspireCmd) + $cmdArgs)) { $psi.ArgumentList.Add([string]$a) }
}
else {
    $psi.FileName = $aspireCmd
    foreach ($a in $cmdArgs) { $psi.ArgumentList.Add([string]$a) }
}
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$proc = [System.Diagnostics.Process]::Start($psi)
$null = $proc.StandardOutput.ReadToEndAsync()
$null = $proc.StandardError.ReadToEndAsync()
if (-not $proc.WaitForExit(10000)) {
    try { $proc.Kill() } catch { }
}

Write-Success
