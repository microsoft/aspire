#!/bin/bash

# Telemetry tracking hook for Aspire Skills.
#
# Runs on every agent PostToolUse event. Reads the hook JSON from stdin, detects when an
# Aspire skill, Aspire MCP tool, or Aspire skill reference file was used, and forwards a
# low-cardinality usage event to `aspire agent telemetry`. The Aspire CLI command owns the
# actual opt-out + publishing logic; this script only classifies the event and shells out.
#
# Hook contract: a PostToolUse hook MUST always print a single JSON object to stdout and exit
# 0, otherwise it can break the agent session. Every code path here ends in return_success.
#
# === Client format reference ===
#
# Copilot CLI:
#   - Field names: camelCase (toolName, sessionId, toolArgs) when the hook event is configured
#     in camelCase (postToolUse); snake_case (tool_name, ...) when configured in PascalCase
#     (PostToolUse, "VS Code compatible" payload). We handle both.
#   - Tool names: lowercase (skill, view)
#   - Aspire MCP prefix: aspire-<tool>            (e.g. aspire-list_resources)
#   - Detection: COPILOT_CLI=1, or a "toolArgs" field present
#
# Claude Code:
#   - Field names: snake_case (tool_name, session_id, tool_input, hook_event_name)
#   - Tool names: PascalCase (Skill, Read, Edit)
#   - Aspire MCP prefix: mcp__aspire__<tool>      (server named "aspire" in .mcp.json)
#   - Skill prefix: aspire:<skill-name> (plugin install) — stripped before allowlist match
#   - Detection: has "hook_event_name", tool_use_id does NOT contain "__vscode"
#
# VS Code:
#   - Field names: snake_case (tool_name, session_id, tool_input, hook_event_name)
#   - Tool names: snake_case (read_file)
#   - Aspire MCP prefix: mcp_aspire_<tool>
#   - Detection: has "hook_event_name", tool_use_id contains "__vscode" or transcript_path has /Code/
#
# === Event types emitted ===
#
# 1. skill_invocation     - the skill/Skill tool ran with an Aspire skill name, OR a SKILL.md
#                           under .../skills/<aspire-skill>/SKILL.md was read.   (--skill-name)
# 2. tool_invocation      - a tool matching an Aspire MCP prefix ran.            (--tool-name)
# 3. reference_file_read  - a non-SKILL.md file under .../skills/<aspire-skill>/ was read.
#                                                                                (--file-reference)
#
# Privacy: only Aspire-owned identifiers are forwarded. Skill/tool names are matched against an
# allowlist of the skills shipped by github.com/microsoft/aspire-skills, and reference files are
# only forwarded as the repo-relative path *after* skills/<skill>/ — never absolute paths, repo
# names, or user names. The Aspire CLI command independently re-validates and drops anything else.

# Never abort the agent: failures must be silent and we must still emit {"continue":true}.
set +e

# Allowlist of Aspire-owned skill names (must stay in sync with the skills shipped by
# github.com/microsoft/aspire-skills). A shared .agents/skills directory can also contain
# third-party skills (dotnet-inspect, playwright, ...), so a path/name is only treated as
# Aspire when its skill segment is one of these.
ASPIRE_SKILLS="aspire aspire-init aspireify aspire-orchestration aspire-deployment aspire-monitoring"

return_success() {
    echo '{"continue":true}'
    exit 0
}

# Opt out when either the global Aspire CLI telemetry switch or the AI-specific switch is set.
case "${ASPIRE_CLI_TELEMETRY_OPTOUT}" in
    1|true|TRUE|True) return_success ;;
esac
case "${ASPIRE_CLI_AGENT_TELEMETRY_OPTOUT}" in
    1|true|TRUE|True) return_success ;;
esac

# Extract a top-level string field, e.g. "toolName": "view"  ->  view
# Uses sed (portable; no jq/grep -P dependency).
extract_json_field() {
    printf '%s' "$1" | sed -n "s/.*\"$2\"[[:space:]]*:[[:space:]]*\"\([^\"]*\)\".*/\1/p" | head -n 1
}

# Extract a nested string field from toolArgs (Copilot) or tool_input (Claude/VS Code).
extract_nested_field() {
    local json="$1" field="$2" value=""
    value=$(printf '%s' "$json" | sed -n "s/.*\"toolArgs\"[[:space:]]*:[[:space:]]*{[^}]*\"$field\"[[:space:]]*:[[:space:]]*\"\([^\"]*\)\".*/\1/p" | head -n 1)
    if [ -z "$value" ]; then
        value=$(printf '%s' "$json" | sed -n "s/.*\"tool_input\"[[:space:]]*:[[:space:]]*{[^}]*\"$field\"[[:space:]]*:[[:space:]]*\"\([^\"]*\)\".*/\1/p" | head -n 1)
    fi
    printf '%s' "$value"
}

# Extract a file path from tool input, trying the documented field names in order.
extract_nested_path() {
    local json="$1" value=""
    for field in path filePath file_path; do
        value=$(extract_nested_field "$json" "$field")
        if [ -n "$value" ]; then
            break
        fi
    done
    printf '%s' "$value"
}

# Return 0 when $1 is an allowlisted Aspire skill name.
is_aspire_skill() {
    local candidate="$1" name
    for name in $ASPIRE_SKILLS; do
        if [ "$candidate" = "$name" ]; then
            return 0
        fi
    done
    return 1
}

# No stdin (interactive) means nothing to track.
if [ -t 0 ]; then
    return_success
fi

rawInput=$(cat)
if [ -z "$rawInput" ]; then
    return_success
fi

toolName=$(extract_json_field "$rawInput" "toolName")
[ -z "$toolName" ] && toolName=$(extract_json_field "$rawInput" "tool_name")

sessionId=$(extract_json_field "$rawInput" "sessionId")
[ -z "$sessionId" ] && sessionId=$(extract_json_field "$rawInput" "session_id")

timestamp=$(date -u +"%Y-%m-%dT%H:%M:%SZ")

# Detect the client (used only for a low-cardinality client-name tag).
if [ "$COPILOT_CLI" = "1" ]; then
    clientName="copilot-cli"
elif printf '%s' "$rawInput" | grep -q '"hook_event_name"'; then
    toolUseId=$(extract_json_field "$rawInput" "tool_use_id")
    transcriptPath=$(extract_json_field "$rawInput" "transcript_path")
    transcriptPathNorm=$(printf '%s' "$transcriptPath" | tr '\\' '/')
    case "$toolUseId$transcriptPathNorm" in
        *__vscode*|*/Code/*|*/Code\ -\ Insiders/*) clientName="vscode" ;;
        *) clientName="claude-code" ;;
    esac
elif printf '%s' "$rawInput" | grep -q '"toolArgs"'; then
    clientName="copilot-cli"
else
    clientName="unknown"
fi

# Nothing to classify without a tool name.
if [ -z "$toolName" ]; then
    return_success
fi

shouldTrack=false
eventType=""
skillName=""
mcpToolName=""
fileReference=""

# --- skill_invocation via the skill/Skill tool ---
if [ "$toolName" = "skill" ] || [ "$toolName" = "Skill" ]; then
    candidate=$(extract_nested_field "$rawInput" "skill")
    # Claude prefixes plugin skill names, e.g. "aspire:aspire-deployment".
    candidate="${candidate#aspire:}"
    if is_aspire_skill "$candidate"; then
        skillName="$candidate"
        eventType="skill_invocation"
        shouldTrack=true
    fi
fi

# --- skill_invocation / reference_file_read via a file read tool ---
# Copilot CLI: view, Claude Code: Read, VS Code: read_file.
if [ "$toolName" = "view" ] || [ "$toolName" = "Read" ] || [ "$toolName" = "read_file" ]; then
    pathToCheck=$(extract_nested_path "$rawInput")
    if [ -n "$pathToCheck" ]; then
        # Normalize separators and collapse duplicate slashes. Example inputs:
        #   .agents/skills/aspire/SKILL.md
        #   /home/me/proj/.github/skills/aspire-deployment/references/deploy.md
        #   C:\src\.claude\skills\aspireify\SKILL.md
        normalized=$(printf '%s' "$pathToCheck" | tr '\\' '/' | sed 's|//*|/|g')
        # Capture the skill segment after skills/. We only honor allowlisted Aspire skills.
        skillSegment=$(printf '%s' "$normalized" | sed -n 's|.*/skills/\([^/]*\)/.*|\1|p')
        if [ -z "$skillSegment" ]; then
            # Handles a leading "skills/<skill>/..." with no parent directory.
            skillSegment=$(printf '%s' "$normalized" | sed -n 's|^skills/\([^/]*\)/.*|\1|p')
        fi
        if [ -n "$skillSegment" ] && is_aspire_skill "$skillSegment"; then
            remainder=$(printf '%s' "$normalized" | sed -n 's|.*/skills/||p')
            [ -z "$remainder" ] && remainder=$(printf '%s' "$normalized" | sed -n 's|^skills/||p')
            case "$remainder" in
                */SKILL.md|SKILL.md|*/skill.md|skill.md)
                    # A SKILL.md read is a skill invocation, not a reference-file read.
                    if [ "$shouldTrack" = false ]; then
                        skillName="$skillSegment"
                        eventType="skill_invocation"
                        shouldTrack=true
                    fi
                    ;;
                *)
                    if [ "$shouldTrack" = false ] && [ -n "$remainder" ]; then
                        # Forward only the relative path after skills/ (e.g. aspire/references/deploy.md).
                        fileReference="$remainder"
                        eventType="reference_file_read"
                        shouldTrack=true
                    fi
                    ;;
            esac
        fi
    fi
fi

# --- tool_invocation via an Aspire MCP tool prefix ---
# Conservative exact prefixes (avoid matching arbitrary "*aspire*" tools):
#   Copilot: aspire-<tool>   Claude: mcp__aspire__<tool>   VS Code: mcp_aspire_<tool>
case "$toolName" in
    aspire-*|mcp__aspire__*|mcp_aspire_*)
        mcpToolName="$toolName"
        eventType="tool_invocation"
        shouldTrack=true
        ;;
esac

if [ "$shouldTrack" != true ]; then
    return_success
fi

# Resolve the Aspire CLI. ASPIRE_CLI_COMMAND lets tests substitute a recording stub.
aspireCmd="${ASPIRE_CLI_COMMAND:-aspire}"

# Build the argument vector explicitly so untrusted hook values are passed as discrete args
# (never concatenated into a shell string).
args=(agent telemetry --event-type "$eventType" --client-name "$clientName" --timestamp "$timestamp")
[ -n "$sessionId" ] && args+=(--session-id "$sessionId")
[ -n "$skillName" ] && args+=(--skill-name "$skillName")
[ -n "$mcpToolName" ] && args+=(--tool-name "$mcpToolName")
[ -n "$fileReference" ] && args+=(--file-reference "$fileReference")

# Redirect all child output to null so a banner/log line can never contaminate hook stdout.
# Bound the call so a hung CLI can't stall the agent; swallow every failure.
if command -v timeout >/dev/null 2>&1; then
    timeout 10 "$aspireCmd" "${args[@]}" >/dev/null 2>&1
else
    "$aspireCmd" "${args[@]}" >/dev/null 2>&1
fi

return_success
