# __PACKAGE_NAME__

[![CI](https://github.com/microsoft/aspire/actions/workflows/ci.yml/badge.svg?branch=main&event=push)](https://github.com/microsoft/aspire/actions/workflows/ci.yml)
[![Tests](https://github.com/microsoft/aspire/actions/workflows/tests.yml/badge.svg?branch=main&event=push)](https://github.com/microsoft/aspire/actions/workflows/tests.yml)

The Aspire CLI, published for npm-based installs.

## What is Aspire?

Your stack, streamlined. Aspire is a multi-language, code-first orchestration and observability layer for building, running, and deploying distributed applications.

Use an AppHost to describe how services, frontends, containers, databases, caches, and connections fit together in code. The Aspire CLI runs the whole app locally, opens the OpenTelemetry dashboard for logs, traces, metrics, and health checks, and carries the same app model into deployment.

## Standalone dashboard

The Aspire dashboard shows logs, traces, and metrics for any app that exports OpenTelemetry, even without an AppHost. Start one in a single command:

```bash
aspire dashboard run
```

This launches the dashboard with an OTLP endpoint your apps can point at via `OTEL_EXPORTER_OTLP_ENDPOINT`. Use `aspire dashboard run --help` to see options such as `--frontend-url`, `--otlp-grpc-url`, and `--allow-anonymous`. See [Run the Aspire dashboard standalone](https://aspire.dev/dashboard/standalone/) for setup details.

## A simple app definition

You describe your app in a TypeScript AppHost (`apphost.mts`). The example below runs an Express API and a Vite frontend, exposes the API over HTTP, and wires the frontend to it:

```typescript
import { createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

// Run the Express API and expose its HTTP endpoint externally.
const app = await builder
  .addNodeApp("app", "./api", "src/index.ts")
  .withHttpEndpoint({ env: "PORT" })
  .withExternalHttpEndpoints();

// Run the Vite frontend after the API and inject the API URL for local proxying.
const frontend = await builder
  .addViteApp("frontend", "./frontend")
  .withReference(app)
  .waitFor(app);

// Bundle the frontend build output into the API container for publish/deploy.
await app.publishWithContainerFiles(frontend, "./static");

await builder.build().run();
```

Each builder call returns a promise, so `await` is used to get the resource before referencing it from another resource. `aspire run` builds and launches everything, then opens the dashboard.

### Add backing services

Resources like databases and caches are added the same way and connected with `withReference`. `waitFor` holds dependents until the resource is ready:

```typescript
import { createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

const postgres = await builder.addPostgres("postgres");
const db = await postgres.addDatabase("db");

const cache = await builder.addRedis("cache");

await builder
  .addNodeApp("api", "./api", "src/index.ts")
  .withHttpEndpoint({ env: "PORT" })
  .withExternalHttpEndpoints()
  .withReference(db)
  .withReference(cache)
  .waitFor(db)
  .waitFor(cache);

await builder.build().run();
```

`addPostgres` and `addRedis` become available once the matching integrations are installed with `aspire add postgresql` and `aspire add redis`.

## Install

This package requires Node.js 20 or later.

```bash
npm install -g __PACKAGE_NAME__
```

Then verify the install:

```bash
aspire --version
aspire --help
```

Start from a repo with one or more app projects:

```bash
aspire init
aspire run
```

The native platform packages are installed through npm optional dependencies. Do not install this package with optional dependencies disabled, or the `aspire` launcher will not be able to find the native CLI binary.

## Update

```bash
npm install -g __PACKAGE_NAME__@latest
```

If you run `aspire update --self` from an npm install, the CLI points you back to this npm update command.

## Learn more

- [Documentation](https://aspire.dev/docs/)
- [Build your first app](https://aspire.dev/get-started/first-app/)
- [Aspire repository](https://github.com/microsoft/aspire)
- [Aspire samples repository](https://github.com/microsoft/aspire-samples)
