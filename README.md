# Aspire

[![CI](https://github.com/microsoft/aspire/actions/workflows/ci.yml/badge.svg?branch=main&event=push)](https://github.com/microsoft/aspire/actions/workflows/ci.yml)
[![Tests](https://github.com/microsoft/aspire/actions/workflows/tests.yml/badge.svg?branch=main&event=push)](https://github.com/microsoft/aspire/actions/workflows/tests.yml)
[![Help Wanted](https://img.shields.io/github/issues/microsoft/aspire/help%20wanted?style=flat&color=%24EC820&label=help%20wanted)](https://github.com/microsoft/aspire/labels/help%20wanted)
[![Good First Issue](https://img.shields.io/github/issues/microsoft/aspire/good%20first%20issue?style=flat&color=%24EC820&label=good%20first%20issue)](https://github.com/microsoft/aspire/labels/good%20first%20issue)
[![Discord](https://img.shields.io/discord/1361488941836140614?style=flat&logo=discord&logoColor=white&label=Join%20our%20Discord&labelColor=512bd4&color=cyan)](https://discord.gg/raNPcaaSj8)

## What is Aspire?

Aspire is a multi-language, code-first toolchain for building, running, and deploying distributed applications.

Describe how services, frontends, containers, databases, caches, and connections fit together in code. The Aspire CLI runs the whole app locally, exposes OpenTelemetry-based observability, and carries the same definition into deployment.

## A simple app definition

The same application definition can be written in different languages.

**C#** (`apphost.cs`)

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

**TypeScript** (`apphost.ts`)

```typescript
import { createBuilder } from './.modules/aspire.js';

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

## Getting started

### Install the Aspire CLI

To install the latest released version of the Aspire CLI:

On Windows:

```powershell
irm https://aspire.dev/install.ps1 | iex
```

On Linux or macOS:

```sh
curl -sSL https://aspire.dev/install.sh | bash
```

> [!NOTE]
> If you want to use the latest daily builds instead of the released version, follow the instructions in [docs/using-latest-daily.md](docs/using-latest-daily.md).

## Useful links

- [Documentation](https://aspire.dev/docs/)
- [Build your first app](https://aspire.dev/get-started/first-app/)
- [Build status](https://github.com/microsoft/aspire/actions/workflows/ci.yml)
- [Aspire samples repository](https://github.com/microsoft/aspire-samples)
- [Developer Control Plane (DCP)](https://github.com/microsoft/dcp)
- [Dogfooding pull requests](docs/dogfooding-pull-requests.md)

## What is in this repo?

This repo contains the Aspire CLI, AppHost SDK, dashboard, service discovery infrastructure, project templates, integrations, and VS Code Extension.

## Contributing

Follow [docs/contributing.md](docs/contributing.md) for working in the repository.

## Reporting security issues and security bugs

Security issues and bugs should be reported privately, via email, to the Microsoft Security Response Center (MSRC) <secure@microsoft.com>. You should receive a response within 24 hours. If for some reason you do not, please follow up via email to ensure we received your original message. Further information, including the MSRC PGP key, can be found in the [Security TechCenter](https://www.microsoft.com/msrc/faqs-report-an-issue). You can also find these instructions in this repo's [Security doc](SECURITY.md).

### Note on containers used by Aspire resource and client integrations

The Aspire team cannot evaluate the underlying third-party containers for which it provides API support for suitability for specific customer requirements.

You should evaluate whichever containers you chose to compose and automate with Aspire to ensure they meet your, your employers or your government’s requirements on security and safety as well as cryptographic regulations and any other regulatory or corporate standards that may apply to your use of the container.

## License

The code in this repo is licensed under the [MIT](LICENSE.TXT) license.
