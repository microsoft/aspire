# FoundryAgentToolboxTs

End-to-end sample of an Aspire **TypeScript AppHost** wiring a Foundry Agent + **Foundry Toolbox** to a **custom MCP server** that requires bearer-token authentication.

## What it shows

- TypeScript AppHost (`apphost.mts`) using `Aspire.Hosting.Foundry` and `Aspire.Hosting.JavaScript`.
- A custom MCP server (Node + Express + `@modelcontextprotocol/sdk`) implementing the streamable HTTP transport and validating an `Authorization: Bearer <token>` header.
- A `mcp-bearer-token` parameter wired into **both** sides of the call:
  - Into the MCP server as the `MCP_BEARER_TOKEN` env var (for validation).
  - Into the Toolbox `withMcpTool(...)` definition as the outbound `authorizationToken` (forwarded by the Foundry data plane as the `Authorization` header).
- An extra `x-app-source` custom header on the MCP tool to demonstrate the headers feature.

## Topology

```
mcp-bearer-token (parameter, secret)
   |
   +--> mcp-server (Node)
   |        validates Authorization: Bearer <token>
   |
   +--> foundry.project.field-tools toolbox
            .withWebSearchTool()
            .withMcpTool('directory', mcpServer http endpoint,
                         { authorizationToken: refExpr`${bearerToken}`,
                           headers: { 'x-app-source': refExpr`foundry-toolbox-ts-sample` } })
```

## Run locally

> **Pre-release wiring.** This playground depends on the polyglot `authorizationToken` / `headers`
> options added in `Aspire.Hosting.Foundry` by PR #17742, which is not yet in a shipped package.
> Until that PR ships, the local `NuGet.config` here points at `../../artifacts/packages/Debug/Shipping`
> and `aspire.config.json` pins both Foundry / JavaScript hosting packages to `13.5.0-dev`. Before
> running the sample for the first time, pack the in-repo packages:
>
> ```bash
> # from the repo root
> dotnet pack src/Aspire.Hosting.Foundry/Aspire.Hosting.Foundry.csproj    -c Debug --no-restore
> dotnet pack src/Aspire.Hosting.JavaScript/Aspire.Hosting.JavaScript.csproj -c Debug --no-restore
> ```
>
> Once #17742 ships in a release, delete `NuGet.config` here and set both packages back to `""` in
> `aspire.config.json` (matching `playground/TypeScriptAppHost/aspire.config.json`).

```bash
cd playground/FoundryAgentToolboxTs/mcp-server && npm install
cd .. && aspire start
```

The first time you run it, the CLI prompts for the `mcp-bearer-token` value. Use any non-empty string; the same value flows to the MCP server and to the toolbox tool definition.

You can manually verify the auth behavior:

```bash
TOKEN=<the value you entered>
ENDPOINT=$(curl -s http://localhost:5252/health) # just to confirm the server is up
curl -i http://localhost:5252/mcp                     # 401 (no bearer)
curl -i -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
    -d '{"jsonrpc":"2.0","method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"curl","version":"0"}},"id":1}' \
    http://localhost:5252/mcp                          # initializes a session
```

The actual MCP endpoint port is assigned by Aspire, look it up in the dashboard for the `mcp-server` resource.

## Important caveat

The **Foundry data plane** is what invokes the MCP server when an agent calls the `directory` tool. The data plane runs in Azure, so it cannot reach `http://localhost:*` MCP endpoints. In `aspire start` mode you can:

- Verify all resources are healthy.
- Verify the MCP server rejects unauthenticated requests and accepts authenticated ones.
- Inspect the generated Foundry toolbox manifest (via `aspire publish`).

For a real end-to-end tool invocation from a Foundry agent, run `aspire publish` (or `aspire deploy`) so the MCP server runs on Azure Container Apps with public ingress.

## Tools exposed by the MCP server

- `lookup_employee(name)`: returns a fake directory record for one of a handful of names.
- `record_note(subject, body)`: records an in-memory note and returns the assigned id.
