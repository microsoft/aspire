# Telemetry tracking hook for Aspire Skills.
#
# Runs on every agent PostToolUse event. Reads the hook JSON from stdin, detects when an
# Aspire skill, Aspire MCP tool, or Aspire skill reference file was used, and forwards a
# low-cardinality usage event to `aspire agent telemetry`. The Aspire CLI command owns the
# actual opt-out + publishing logic; this script only classifies the event and shells out.
#
# Hook contract: a PostToolUse hook MUST always print a single JSON object to stdout and exit
# 0, otherwise it can break the agent session. Every code path here ends in Write-Success.
#
# Compatible with Windows PowerShell 5.1 and PowerShell 7+. See track-telemetry.sh for the
# full client-format / event-type / privacy notes (the logic here mirrors that script).

$ErrorActionPreference = "SilentlyContinue"

# Allowlist of Aspire-owned skill names (keep in sync with github.com/microsoft/aspire-skills).
# A shared .agents/skills directory can also contain third-party skills, so a path/name is only
# treated as Aspire when its skill segment is one of these.
$AspireSkills = @('aspire', 'aspire-init', 'aspireify', 'aspire-orchestration', 'aspire-deployment', 'aspire-monitoring')

function Write-Success {
    Write-Output '{"continue":true}'
    exit 0
}

function Test-OptOut([string] $value) {
    return $value -eq '1' -or $value -ieq 'true'
}

# Opt out when the Aspire CLI telemetry switch is set. This is the single opt-out that also
# gates the `aspire agent telemetry` command path, so honoring it here avoids spawning the CLI
# at all for opted-out users.
if (Test-OptOut $env:ASPIRE_CLI_TELEMETRY_OPTOUT) {
    Write-Success
}

# Read the entire payload from stdin (one complete JSON object per hook invocation).
try {
    $rawInput = [Console]::In.ReadToEnd()
} catch {
    Write-Success
}

if ([string]::IsNullOrWhiteSpace($rawInput)) {
    Write-Success
}

# Parse-fail -> skip telemetry, never guess.
try {
    $data = $rawInput | ConvertFrom-Json -ErrorAction Stop
} catch {
    Write-Success
}

# Copilot CLI camelCase vs Claude/VS Code snake_case.
$toolName = $data.toolName
if (-not $toolName) { $toolName = $data.tool_name }

$sessionId = $data.sessionId
if (-not $sessionId) { $sessionId = $data.session_id }

$toolInput = $data.toolArgs
if (-not $toolInput) { $toolInput = $data.tool_input }

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

function Get-ToolInputPath($inputObject) {
    if (-not $inputObject) { return $null }
    if ($inputObject.path) { return [string]$inputObject.path }
    if ($inputObject.filePath) { return [string]$inputObject.filePath }
    if ($inputObject.file_path) { return [string]$inputObject.file_path }
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
    $candidate = [string]$toolInput.skill
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
    $pathToCheck = Get-ToolInputPath $toolInput
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

# Redirect all child output to null so a banner/log line can never contaminate hook stdout;
# swallow every failure so the hook still returns success.
try {
    & $aspireCmd @cmdArgs *> $null
} catch { }

Write-Success
