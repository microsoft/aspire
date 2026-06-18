# __PACKAGE_NAME__

[![CI](https://github.com/microsoft/aspire/actions/workflows/ci.yml/badge.svg?branch=main&event=push)](https://github.com/microsoft/aspire/actions/workflows/ci.yml)
[![Tests](https://github.com/microsoft/aspire/actions/workflows/tests.yml/badge.svg?branch=main&event=push)](https://github.com/microsoft/aspire/actions/workflows/tests.yml)

The Aspire CLI, published for npm-based installs.

## What is Aspire?

Your stack, streamlined. Aspire is a multi-language, code-first orchestration and observability layer for building, running, and deploying distributed applications.

Use an AppHost to describe how services, frontends, containers, databases, caches, and connections fit together in code. The Aspire CLI runs the whole app locally, opens the OpenTelemetry dashboard for logs, traces, metrics, and health checks, and carries the same app model into deployment.

## A simple app definition

The same application definition can be written in different languages.

__C#__ (`apphost.cs`)

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var cache = builder.AddRedis("cache");

var api = builder.AddNodeApp("api", "./api", "src/index.ts")
  .WithReference(cache)
  .WaitFor(cache)
  .WithHttpEndpoint(env: "PORT")
  .WithExternalHttpEndpoints();

builder.AddViteApp("frontend", "./frontend")
  .WithReference(api)
  .WaitFor(api);

builder.Build().Run();
```

__TypeScript__ (`apphost.mts`)

```typescript
import { createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

const cache = await builder.addRedis("cache");

const api = await builder
  .addNodeApp("api", "./api", "src/index.ts")
  .withReference(cache)
  .waitFor(cache)
  .withHttpEndpoint({ env: "PORT" })
  .withExternalHttpEndpoints();

await builder
  .addViteApp("frontend", "./frontend")
  .withReference(api)
  .waitFor(api);

await builder.build().run();
```

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
