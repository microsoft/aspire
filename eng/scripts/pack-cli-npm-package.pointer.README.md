# __PACKAGE_NAME__

The Aspire CLI for npm-based installs. Use it to create, run, publish, and deploy Aspire AppHosts from a terminal.

## What is Aspire?

Aspire is a code-first app model for distributed applications. Your AppHost describes projects, containers, databases, caches, and their connections in code. `aspire run` starts the app locally and opens the Aspire dashboard with logs, traces, metrics, resources, and health checks.

## Install

This package requires Node.js 20 or later.

Supported platforms: Windows x64/Arm64, macOS x64/Arm64, Linux x64/Arm64 with glibc, and Linux x64 with musl/Alpine.

```bash
npm install -g __PACKAGE_NAME__
```

Then verify the install:

```bash
aspire --version
aspire --help
```

The npm package installs a small JavaScript `aspire` launcher. The native platform packages are installed through npm optional dependencies. The launcher selects the package that matches your OS, CPU, and Linux libc. Do not install this package with optional dependencies disabled, or the launcher will not be able to find the native CLI binary.

## Quick start

Start from a repo with one or more app projects:

```bash
aspire init
aspire run
```

Starting from scratch? See [Build your first app](https://aspire.dev/get-started/first-app/).

## Useful commands

- `aspire new` creates a new Aspire app from a template.
- `aspire init` adds an AppHost to an existing repo.
- `aspire add <integration>` installs integrations such as PostgreSQL or Redis.
- `aspire run` starts the AppHost and opens the dashboard.
- `aspire publish` prepares deployment artifacts from the AppHost model.
- `aspire deploy` deploys an AppHost to its supported deployment targets.
- `aspire dashboard run` starts only the dashboard for apps that already export OpenTelemetry.
- `aspire --help` and `aspire <command> --help` show the current command options.

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

## Update or uninstall

Update to the latest npm release:

```bash
npm install -g __PACKAGE_NAME__@latest
```

If you run `aspire update --self` from an npm install, the CLI points you back to this npm update command.

Uninstall the npm package:

```bash
npm uninstall -g __PACKAGE_NAME__
```

## Troubleshooting

### Optional dependencies disabled

If the launcher says the native package was not installed, reinstall without `--omit=optional`, `--no-optional`, or the `npm_config_optional=false` environment variable:

```bash
npm install -g __PACKAGE_NAME__
```

### PATH cannot find `aspire`

Make sure your shell can see npm's global executable directory. `npm prefix -g` shows the global prefix; on macOS and Linux the `aspire` shim is usually under the prefix's `bin` directory. On Windows it is usually under the npm directory in your user profile.

### Supported platforms and architectures

The npm package currently ships native binaries for Windows x64/Arm64, macOS x64/Arm64, Linux x64/Arm64 with glibc, and Linux x64 with musl/Alpine. Other platforms are not supported by this package.

### Corrupted or mismatched install

If the launcher reports a corrupted package, mismatched native package version, or missing native binary, reinstall the package:

```bash
npm uninstall -g __PACKAGE_NAME__
npm install -g __PACKAGE_NAME__
```

## Learn more

- [Aspire documentation](https://aspire.dev/docs/)
- [Aspire CLI command reference](https://aspire.dev/reference/cli/commands/aspire/)
- [Build your first app](https://aspire.dev/get-started/first-app/)
- [Standalone dashboard](https://aspire.dev/dashboard/standalone/)
- [Aspire repository](https://github.com/microsoft/aspire)
- [Aspire samples repository](https://github.com/microsoft/aspire-samples)
- [Release notes](https://github.com/microsoft/aspire/releases)
- [Report an issue](https://github.com/microsoft/aspire/issues)
