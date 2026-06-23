# AspireWithBun

A minimal Aspire **TypeScript AppHost** that hosts a [Bun](https://bun.com/) HTTP server two different ways:

| Resource    | API                                                        | Behavior                                          |
|-------------|------------------------------------------------------------|---------------------------------------------------|
| `bunapp`    | `addBunApp("bunapp", "./BunFrontend", "server.ts")`        | Runs `bun server.ts` directly.                    |
| `bunscript` | `addBunApp(...).withRunScript("start")`                    | Runs `bun run start` against the `start` script.  |

Both resources serve a simple `text/plain` greeting and use `Bun.serve`.

## Prerequisites

- [Aspire CLI](https://learn.microsoft.com/dotnet/aspire/) on PATH
- [Bun](https://bun.com/docs/installation) on PATH
- Node.js (used to host the TypeScript AppHost itself)

## Run

```bash
aspire run
```

The CLI generates the `.aspire/modules/` TypeScript bindings the first time it runs, then starts the AppHost. Open the URL listed for `bunapp` or `bunscript` in the dashboard. Each returns a different greeting so you can confirm which entry path served the response.
