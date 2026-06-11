# Aspire CLI MCP Server

## Overview

This document describes the design and implementation of the Aspire CLI-based MCP (Model Context Protocol) server. This is a separate MCP server from the existing Dashboard-integrated MCP server, designed to provide a different set of capabilities and interaction model.

## Background

### Existing Dashboard MCP Server
The Aspire Dashboard includes an integrated MCP server that exposes tools and capabilities for interacting with running Aspire applications. This server communicates over HTTPS and is secured using the ASP.NET Core developer certificate.

**Limitations identified:**
1. **Certificate compatibility**: Node.js-based MCP clients (e.g., VS Code with older Node versions) cannot communicate securely with the dashboard's HTTPS endpoint due to certificate validation issues with ASP.NET Core developer certificates. This will be resolved when VS Code updates to newer Node.js versions, but creates friction in the current ecosystem.
2. **Runtime dependency**: The Dashboard MCP server requires the Aspire application host to be running and healthy, which creates a bootstrapping problem when trying to diagnose launch failures or locate app hosts.

### Motivation for CLI-Based MCP Server
The CLI-based MCP server addresses the limitations of the Dashboard approach by:

1. **Using stdio transport**: Eliminates certificate/TLS concerns entirely by communicating over standard input/output streams
2. **Independent lifecycle**: Can start immediately without requiring a running app host, enabling:
   - Helping locate and discover Aspire app hosts in multi-app-host workspaces
   - Supporting unattended coding agents (e.g., GitHub Copilot Code Agent) that require MCP servers to be available from workflow start
3. **Broader client compatibility**: stdio-based MCP servers work universally across all MCP client implementations

### MVP Implementation Strategy

The initial implementation takes a **proxy/forwarding approach**:
- CLI MCP server exposes the same static list of tools as the Dashboard MCP server
- Adds a `select-apphost` tool for multi-app-host workspace scenarios
- Tool invocations are **forwarded to the Dashboard** MCP server for execution
- No MCP prompts in MVP (deferred for future consideration)
- Focuses on solving the stdio transport and agent workflow compatibility issues

## Design Goals

### Primary Objectives

1. **Solve stdio transport compatibility**: Enable MCP clients (especially Node.js-based clients) to work with Aspire without HTTPS/certificate issues

2. **Support agent workflows**: Work with unattended coding agents (e.g., GitHub Copilot Code Agent) that require MCP servers available from workflow start

3. **Multi-app-host workspace support**: Enable users to select which app host to work with when multiple app hosts exist in the workspace

4. **Maintain feature parity with Dashboard MCP**: Expose the same tool capabilities as the Dashboard MCP server via forwarding mechanism

### Non-Goals (MVP)

- **Not replacing Dashboard MCP**: The CLI MCP server acts as a proxy/adapter, not a replacement
- **No independent tool implementation**: MVP forwards to Dashboard rather than re-implementing functionality
- **No MCP prompts**: Guidance via prompts deferred for future consideration
- **No direct app host bootstrapping**: Users still use CLI commands directly for `aspire init`, `aspire add`, etc.

### Relationship to Dashboard MCP Server

In the MVP implementation, the CLI MCP server acts as a **proxy/adapter** to the Dashboard MCP server:

- **CLI MCP Server**: 
  - stdio-based transport (solves certificate/compatibility issues)
  - Always available for agent workflows
  - Adds `select-apphost` capability for multi-app-host scenarios
  - Forwards tool invocations to Dashboard
  
- **Dashboard MCP Server**: 
  - Provides the actual tool implementations
  - HTTPS-based (works directly with clients that support it)
  - Remains the source of truth for runtime operations

Future iterations may add CLI-specific tools or independent implementations, but MVP maintains a simple forwarding architecture.

## Architecture

### Command Structure
The MCP server is exposed via the `aspire mcp` CLI command, implemented in `McpCommand.cs`.

### Transport
The CLI MCP server uses **stdio (standard input/output) transport**, which:
- Eliminates TLS/certificate concerns present with HTTPS
- Works universally across all MCP client implementations
- Is required for unattended agent workflows (e.g., GitHub Copilot Code Agent)
- Follows the standard MCP stdio transport pattern

### App Host Discovery and Connection

The MCP server uses a dual-mechanism approach for discovering and connecting to app hosts:

#### 1. Source Tree Scanning (Pre-launch Discovery)

**Purpose**: Locate candidate app hosts within the workspace before they are running.

**Implementation**:
- Leverages the existing `ProjectLocator` class (adapted for MCP context)
- Scans the working directory for `.AppHost.csproj` files
- Provides tools for the model to:
  - List available app hosts in the source tree
  - Use elicitation to ask the user which app host to "lock onto"
  - Launch the selected app host via CLI mechanisms

**Use cases**:
- Initial setup in a workspace with multiple potential app hosts
- Helping agents understand the structure of Aspire projects
- Bootstrapping new app hosts

#### 2. Dashboard Connection (Runtime Operations)

**Purpose**: Forward MCP tool invocations to the Dashboard MCP server for execution.

**MVP Implementation - Two-stage connection**:

1. **Stage 1: Connect to app host via new MCP backchannel socket**
   - CLI MCP server connects to a **new Unix socket** created by the app host
   - Socket location: `$HOME/.aspire/mcp/backchannels/mcp.[hash].socket` (where `[hash]` is app host hash)
   - This is a **new JSON-RPC backchannel**, separate from the existing CLI backchannel
   - Purpose: Stateless request/response operations to obtain Dashboard MCP connection information
   - Existing CLI backchannel remains untouched (it has different lifecycle orchestration requirements)

2. **Stage 2: Connect to Dashboard MCP server**
   - Dashboard already exposes an MCP server over **HTTPS**
   - Using endpoint URL and API token obtained from app host socket connection
   - CLI MCP server forwards tool invocations: stdio client → Dashboard MCP (HTTPS)
   - Returns Dashboard responses back to stdio client

**Discovery and authentication flow**:
1. User calls `select-apphost` with app host identifier
2. CLI MCP locates app host's MCP backchannel socket in `$HOME/.aspire/mcp/backchannels/`
3. CLI MCP connects to Unix socket (JSON-RPC)
4. CLI MCP queries socket for Dashboard MCP endpoint URL and API token
5. CLI MCP establishes HTTPS connection to Dashboard MCP endpoint using obtained credentials
6. Subsequent tool invocations are forwarded to Dashboard MCP over HTTPS

**App host implementation requirements**:
- New namespace: `Aspire.Hosting.Mcp` (mentioned earlier in original design)
- New service that launches with the app host
- Creates Unix socket at `$HOME/.aspire/mcp/backchannels/mcp.[hash].socket`
- Exposes JSON-RPC interface for:
  - Querying Dashboard MCP endpoint URL
  - Querying Dashboard MCP API token/credentials
  - [To be filled: Other operations needed via this backchannel?]
- Supports multiple concurrent connections (unlike existing CLI backchannel)

**Open questions**:
- [To be filled: Specific JSON-RPC method names for getting Dashboard MCP endpoint/token?]
- [To be filled: What tools does the Dashboard MCP server currently expose?]
- [To be filled: Does Dashboard MCP use standard MCP protocol over HTTPS/WebSocket?]

#### State Management

**Single app host selection**:
- The CLI MCP server works with **one selected app host at a time**
- App host selection managed via `select-apphost` tool
- No simultaneous multi-app-host support

**Tool availability**:
- **Static tool list**: CLI MCP server exposes the same tools as Dashboard MCP server
- Tools are always advertised, even if no app host is selected
- If tool is invoked without an app host selected (or app host not running):
  - Return error indicating app host needs to be selected/started

**Connection handling**:
- API token obtained from backchannel is valid for the lifetime of the app host launch
- Token will not expire while app host is running
- If Dashboard MCP endpoint becomes unavailable:
  - [To be filled: Return error to client? Attempt reconnection?]
- If app host restarts:
  - CLI MCP must re-query backchannel for new Dashboard MCP endpoint/token
  - [To be filled: Auto-detect restart and reconnect, or require manual re-selection?]
- If app host is running but doesn't support MCP backchannel (older version):
  - [To be filled: How to detect and handle gracefully?]

### Server Information
- **Name**: `aspire-mcp-server`
- **Version**: `1.0.0` [To be filled: Versioning strategy]

## Tools & Capabilities

### MVP Approach

**Static tool list matching Dashboard**: The CLI MCP server exposes the **same tools** as the Dashboard MCP server, forwarding invocations to the Dashboard for execution.

**No MCP prompts in MVP**: Guidance via MCP prompts is deferred for future consideration. LLMs use standard CLI commands (`aspire init`, `aspire add`, etc.) directly.

### MCP Tools

#### CLI-Specific Tools

**`select-apphost`**: Choose which app host to work with in multi-app-host workspaces
- **Input**: App host identifier (project path, name, or hash)
- **Behavior**:
  - Sets the selected app host for subsequent tool invocations
  - If app host is running: establishes connection to its Dashboard
  - If app host is not running: returns error suggesting to run `aspire run`
  - Selection persists for the lifetime of the MCP server session
- **Use case**: Disambiguate when workspace contains multiple `.AppHost.csproj` files

#### Dashboard-Forwarded Tools

All other tools match the Dashboard MCP server's tool list and are forwarded for execution:

**[To be filled: What tools does Dashboard MCP currently expose?]**
- Resource management (start, stop, restart, list, get state)
- Console log retrieval
- Trace/telemetry access
- Endpoint information
- [Others?]

**Forwarding behavior**:
- Tool schemas match Dashboard exactly
- CLI MCP validates selected app host is available before forwarding
- Responses from Dashboard are returned directly to stdio client
- Errors (connection issues, app host not running) are surfaced to client

## Protocol & Integration

### MCP Protocol Version
[To be filled: Which version of MCP protocol, any specific features we rely on]

### Client Integration
[To be filled: How will clients discover and connect to this server? Configuration needed?]

### Security Considerations
[To be filled: Authentication, authorization, data privacy concerns]

## Differences from Dashboard MCP Server

| Aspect | Dashboard MCP Server | CLI MCP Server |
|--------|---------------------|----------------|
| Transport | HTTPS | stdio (standard input/output) |
| Primary Use Case | Runtime monitoring and operations (direct access) | Agent workflows, certificate compatibility, multi-app-host scenarios |
| Lifecycle | Runs when app host is running | Always available (can start independently) |
| Tools/Capabilities | Provides actual tool implementations | Proxies/forwards to Dashboard MCP (MVP) |
| Client Compatibility | Requires HTTPS support, ASP.NET Core dev cert | Universal (all MCP clients) |
| Authentication | API token over HTTPS | Obtained via Unix socket backchannel |
| Connection Model | Direct HTTPS connection | Two-stage: Unix socket → Dashboard MCP |

## Implementation Phases

### Phase 1: Foundation (Current PR)
- [x] Basic command structure with feature flag
- [x] Stdio transport setup
- [x] Echo tool for validation
- [ ] Remove echo tool once real tools are added

### Phase 2: MVP - Dashboard Forwarding
- [ ] Implement `select-apphost` tool
  - [ ] Integrate `ProjectLocator` for discovering app hosts in workspace
  - [ ] Implement app host selection/tracking logic
- [ ] Implement Dashboard connection mechanism
  - [ ] Discovery: How to find Dashboard endpoint for selected app host
  - [ ] Authentication: How to authenticate with Dashboard MCP
  - [ ] Error handling: Connection failures, Dashboard unavailable
- [ ] Implement tool forwarding
  - [ ] Mirror Dashboard tool schemas in CLI MCP
  - [ ] Forward tool invocations to Dashboard
  - [ ] Handle responses and errors
- [ ] Connection state management
  - [ ] Detect when Dashboard becomes available/unavailable
  - [ ] Handle app host restarts
  - [ ] Error messages when operations fail due to connectivity

### Phase 3: Future Enhancements (Post-MVP)
- [ ] Consider MCP prompts for guidance
- [ ] Evaluate direct backchannel socket approach (bypassing Dashboard forwarding)
- [ ] Additional CLI-specific tools (if needed)
- [ ] Performance optimizations
- [ ] Support for multiple concurrent app host connections (if needed)

## Open Questions

### Dashboard MCP Server
1. What tools does the Dashboard MCP server currently expose? (Need to document the complete tool list)
2. Does Dashboard MCP use the standard Model Context Protocol over HTTPS/WebSocket?
3. Any specific MCP protocol version or features we need to be aware of?

### MCP Backchannel JSON-RPC Interface
1. What should the JSON-RPC method names be for querying Dashboard MCP connection info?
   - Suggestion: `GetMcpConnectionInfo` returning `{ endpoint: string, apiToken: string }`
2. Are there other operations needed via the MCP backchannel beyond connection info?

### Error Handling
1. How should CLI MCP handle Dashboard MCP endpoint becoming unavailable mid-operation?
2. If app host restarts, should CLI MCP auto-detect and reconnect, or require manual re-selection?
3. How to detect and gracefully handle app hosts that don't support MCP backchannel (older versions)?

### Client Integration
1. How will MCP clients discover and configure the CLI MCP server?
2. Any specific configuration needed for different agent environments (VS Code, GitHub CCA, etc.)?

### Versioning
1. What versioning strategy for the `aspire-mcp-server` version field?
2. How to handle version mismatches between CLI MCP and Dashboard MCP?

## Decision Log

### Architecture Decisions

**Decision: Proxy/forwarding approach for MVP**
- **Rationale**: Simplifies initial implementation by reusing existing Dashboard MCP tools
- **Alternative considered**: Implement tools directly in CLI MCP
- **Trade-offs**: MVP requires running app host + Dashboard, but avoids code duplication

**Decision: Two-stage connection (Unix socket → Dashboard MCP)**
- **Rationale**: Unix socket provides secure, local mechanism to obtain Dashboard credentials without environment variables or files
- **Alternative considered**: Direct environment variable or file-based credential sharing
- **Trade-offs**: Requires new MCP backchannel implementation in app host

**Decision: New MCP backchannel separate from existing CLI backchannel**
- **Rationale**: Existing CLI backchannel has specific lifecycle/orchestration requirements; new backchannel supports multiple concurrent connections and stateless request/response pattern
- **Alternative considered**: Extend existing CLI backchannel
- **Trade-offs**: More implementation work, but cleaner separation of concerns

**Decision: Single app host selection at a time**
- **Rationale**: Simplifies MVP implementation and covers primary use case
- **Alternative considered**: Support multiple simultaneous app host connections
- **Trade-offs**: Can be added in future if needed

**Decision: No MCP prompts in MVP**
- **Rationale**: Uncertain value; LLMs can already use CLI commands via terminal
- **Alternative considered**: Implement prompts for guidance
- **Trade-offs**: Can be reconsidered based on user feedback

**Decision: Static tool list always advertised**
- **Rationale**: Consistent tool discovery for MCP clients
- **Alternative considered**: Dynamic tool list based on connection state
- **Trade-offs**: Tools may fail if invoked without app host selected, but simpler client experience

**Decision: API token lifetime tied to app host launch**
- **Rationale**: No token expiration/refresh complexity needed
- **Alternative considered**: Time-based token expiration
- **Trade-offs**: Token only valid while app host is running, but this matches the use case

### Technical Decisions

**Decision: stdio transport for CLI MCP**
- **Rationale**: Solves certificate compatibility issues with Node.js-based clients; required for GitHub CCA and similar agents
- **Alternative considered**: HTTP/HTTPS server
- **Trade-offs**: Less flexible than HTTP, but perfect fit for agent workflows
 
## References

- [Model Context Protocol Specification](https://modelcontextprotocol.io/)
- Dashboard MCP Server: [To be filled: location/documentation]
