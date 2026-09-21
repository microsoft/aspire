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

$AspireMcpTools = @(
    'doctor',
    'execute_resource_command',
    'get_doc',
    'list_apphosts',
    'list_console_logs',
    'list_docs',
    'list_integrations',
    'list_resources',
    'list_structured_logs',
    'list_trace_structured_logs',
    'list_traces',
    'refresh_tools',
    'search_docs',
    'select_apphost'
)

$AspireReferenceFiles = @(
    'aspire-deployment/references/aws.md',
    'aspire-deployment/references/azure.md',
    'aspire-deployment/references/cicd.md',
    'aspire-deployment/references/docker-compose.md',
    'aspire-deployment/references/github-actions-azure-csharp.yml',
    'aspire-deployment/references/github-actions-azure-typescript.yml',
    'aspire-deployment/references/javascript.md',
    'aspire-deployment/references/kubernetes.md',
    'aspire-deployment/references/preflight.md',
    'aspire-init/references/init-workflow.md',
    'aspire-init/references/templates.md',
    'aspire-monitoring/references/diagnostics-bridge.md',
    'aspire-monitoring/references/monitoring.md',
    'aspire-monitoring/references/playwright-handoff.md',
    'aspire-orchestration/references/agent-workflows.md',
    'aspire-orchestration/references/app-commands.md',
    'aspire-orchestration/references/detection.md',
    'aspire-orchestration/references/resource-management.md',
    'aspire-orchestration/references/safety-guardrails.md',
    'aspire/references/aspire-13-3-breaking-changes.md',
    'aspire/references/aspire-13-5-breaking-changes.md',
    'aspireify/references/apphost-wiring.md',
    'aspireify/references/csharp-authoring.md',
    'aspireify/references/docker-compose.md',
    'aspireify/references/full-solution-apphosts.md',
    'aspireify/references/javascript-apps.md',
    'aspireify/references/opentelemetry.md',
    'aspireify/references/scan-and-propose.md',
    'aspireify/references/service-defaults.md',
    'aspireify/references/typescript-authoring.md',
    'aspireify/references/validation.md'
)

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

# Materialize at most 64 KiB plus one sentinel character. If the payload is larger, drain the
# remainder into a fixed buffer so the host can finish writing without retaining the event.
$payloadLimit = 65536
$payloadBuffer = [char[]]::new($payloadLimit + 1)
$payloadLength = 0
while ($payloadLength -lt $payloadBuffer.Length) {
    $read = [Console]::In.ReadBlock(
        $payloadBuffer,
        $payloadLength,
        $payloadBuffer.Length - $payloadLength)
    if ($read -eq 0) {
        break
    }

    $payloadLength += $read
}

$rawInput = [string]::new($payloadBuffer, 0, $payloadLength)
if ($payloadLength -gt $payloadLimit) {
    $discardBuffer = [char[]]::new(4096)
    while ([Console]::In.Read($discardBuffer, 0, $discardBuffer.Length) -gt 0) {
    }

    Write-Success
}

if ([string]::IsNullOrWhiteSpace($rawInput)) {
    Write-Success
}

# Fast path: most PostToolUse events are not Aspire-related. Everything we track carries
# "skill"/"aspire" in the payload (the skill tool name, an aspire-/mcp__aspire__ tool name, or a
# .../skills/<aspire-skill>/ path), so skip JSON parsing entirely when neither appears.
if ($rawInput -notmatch 'skill|aspire') {
    Write-Success
}

# A malformed payload is ignored rather than guessed from fragments that happen to look like fields.
try {
    $data = $rawInput | ConvertFrom-Json -ErrorAction Stop
}
catch {
    Write-Success
}

# Copilot CLI camelCase vs Claude/VS Code snake_case.
$toolName = $data.toolName
if (-not $toolName) { $toolName = $data.tool_name }

$sessionId = $data.sessionId
if (-not $sessionId) { $sessionId = $data.session_id }

$toolInput = $data.toolArgs
if (-not $toolInput) { $toolInput = $data.tool_input }
if ($toolInput -is [string]) {
    try {
        $toolInput = $toolInput | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        $toolInput = $null
    }
}

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

function Get-InputField([string] $name) {
    if ($toolInput -and $toolInput.PSObject -and $toolInput.PSObject.Properties.Name -contains $name) {
        return [string]$toolInput.$name
    }

    return $null
}

function Test-AspireSkill([string] $candidate) {
    return $AspireSkills -contains $candidate
}

function Test-SessionId([string] $candidate) {
    return $candidate.Length -eq 36 -and
        $candidate -match '^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$'
}

function Get-AspireSkillPath([string] $path) {
    $segments = $path -split '/'
    for ($i = $segments.Length - 3; $i -ge 0; $i--) {
        if ($segments[$i] -eq 'skills') {
            return ($segments[($i + 1)..($segments.Length - 1)] -join '/')
        }
    }

    return $null
}

$shouldTrack = $false
$eventType = $null
$skillName = $null
$mcpToolName = $null
$fileReference = $null

# --- skill_invocation via the skill/Skill tool ---
if ($toolName -eq 'skill' -or $toolName -eq 'Skill') {
    $candidate = [string](Get-InputField 'skill')
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
    $pathToCheck = Get-InputField 'path'
    if (-not $pathToCheck) { $pathToCheck = Get-InputField 'filePath' }
    if (-not $pathToCheck) { $pathToCheck = Get-InputField 'file_path' }
    if ($pathToCheck) {
        # Normalize separators and collapse duplicate slashes.
        $normalized = ($pathToCheck -replace '\\', '/') -replace '/+', '/'
        $skillPath = Get-AspireSkillPath $normalized
        if ($skillPath) {
            $skillSegment = $skillPath.Split('/')[0]
            if (-not (Test-AspireSkill $skillSegment)) {
                $skillPath = $null
            }
        }
        if ($skillPath) {
            if ($skillPath -imatch '(^|/)skill\.md$') {
                # A SKILL.md read is a skill invocation, not a reference-file read.
                if (-not $shouldTrack) {
                    $skillName = $skillSegment
                    $eventType = 'skill_invocation'
                    $shouldTrack = $true
                }
            } elseif (-not $shouldTrack -and $AspireReferenceFiles -contains $skillPath) {
                $fileReference = $skillPath
                $eventType = 'reference_file_read'
                $shouldTrack = $true
            }
        }
    }
}

# --- tool_invocation via an Aspire MCP tool prefix ---
# Conservative exact prefixes:
#   Copilot: aspire-<tool>   Claude: mcp__aspire__<tool>   VS Code: mcp_aspire_<tool>
if ($toolName.StartsWith('aspire-')) {
    $mcpTool = $toolName.Substring('aspire-'.Length)
} elseif ($toolName.StartsWith('mcp__aspire__')) {
    $mcpTool = $toolName.Substring('mcp__aspire__'.Length)
} elseif ($toolName.StartsWith('mcp_aspire_')) {
    $mcpTool = $toolName.Substring('mcp_aspire_'.Length)
} else {
    $mcpTool = $null
}

if ($mcpTool -and $AspireMcpTools -contains $mcpTool) {
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

$hookTimeoutSeconds = 10
if ($env:ASPIRE_HOOK_TIMEOUT_SECONDS -match '^(?:[1-9]|10)$') {
    $hookTimeoutSeconds = [int]$env:ASPIRE_HOOK_TIMEOUT_SECONDS
}

# Build the argument vector explicitly so untrusted hook values are passed as discrete args.
$cmdArgs = @('agent', 'telemetry', '--event-type', $eventType, '--client-name', $clientName, '--timestamp', $timestamp)
if ($sessionId -and (Test-SessionId ([string]$sessionId))) { $cmdArgs += @('--session-id', [string]$sessionId) }
if ($skillName) { $cmdArgs += @('--skill-name', $skillName) }
if ($mcpToolName) { $cmdArgs += @('--tool-name', $mcpToolName) }
if ($fileReference) { $cmdArgs += @('--file-reference', $fileReference) }

# Bound the call so a hung or slow CLI can't stall the agent's tool loop (mirrors the bash
# `timeout 10`). Run the CLI as a child process we can wait on and kill: an executable on PATH
# (the production `aspire`) is launched directly, while a .ps1 — used by the hook's tests via
# ASPIRE_CLI_COMMAND — is launched through pwsh. stdout/stderr are redirected and drained so a
# banner/log line can neither contaminate the hook's stdout nor deadlock on a full pipe buffer.
# Stream into a null sink rather than ReadToEndAsync, which retains every byte until the process
# exits and lets a noisy child consume unbounded memory during the timeout window.
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
$stdoutTask = $proc.StandardOutput.BaseStream.CopyToAsync([System.IO.Stream]::Null)
$stderrTask = $proc.StandardError.BaseStream.CopyToAsync([System.IO.Stream]::Null)
if (-not $proc.WaitForExit($hookTimeoutSeconds * 1000)) {
    try { $proc.Kill($true) } catch { }
    if (-not $proc.WaitForExit(1000)) {
        try { $proc.Kill($true) } catch { }
    }
}

# A child can exit while a descendant still holds an inherited pipe. Bound stream drainage too;
# closing our read ends prevents that descendant from keeping the hook alive indefinitely.
$drainTask = [System.Threading.Tasks.Task]::WhenAll($stdoutTask, $stderrTask)
if (-not $drainTask.Wait(1000)) {
    try { $proc.StandardOutput.Close() } catch { }
    try { $proc.StandardError.Close() } catch { }
}
try { $null = $drainTask.GetAwaiter().GetResult() } catch { }
$proc.Dispose()

Write-Success
