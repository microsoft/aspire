# AspireWithDeno

A minimal Aspire **TypeScript AppHost** that hosts a [Deno](https://deno.com/) HTTP server two different ways:

| Resource     | API                                                          | Behavior                                          |
|--------------|--------------------------------------------------------------|---------------------------------------------------|
| `denoapp`    | `addDenoApp("denoapp", "./DenoFrontend", "main.ts")`         | Runs `deno run -A main.ts` directly.              |
| `denoscript` | `addDenoApp(...).withRunScript("start")`                     | Runs `deno task start` against the `start` task.  |

Both resources serve a simple `text/plain` greeting and use `Deno.serve`.

Deno's built-in OpenTelemetry integration is enabled automatically (via the `OTEL_DENO`
environment variable), so traces, metrics, and logs flow to the Aspire dashboard with no
application-level SDK wiring.

> **Why `-A`?** Deno runs under a deny-by-default permission model. Aspire injects configuration
> through environment variables (`PORT`, OTLP endpoints, certificate paths) that the app reads with
> `Deno.env` and serves over sockets with `Deno.serve`, both of which require explicit grants.
> `addDenoApp` passes `-A` (allow-all) to keep the developer experience on par with Node and Bun.

## Prerequisites

- [Aspire CLI](https://learn.microsoft.com/dotnet/aspire/) on PATH
- [Deno](https://docs.deno.com/runtime/getting_started/installation/) on PATH
- Node.js (used to host the TypeScript AppHost itself)

## Run

```bash
aspire run
```

The CLI generates the `.aspire/modules/` TypeScript bindings the first time it runs, then starts the AppHost. Open the URL listed for `denoapp` or `denoscript` in the dashboard. Each returns a different greeting so you can confirm which entry path served the response.
