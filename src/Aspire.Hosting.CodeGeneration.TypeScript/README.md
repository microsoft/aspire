# Aspire.Hosting.CodeGeneration.TypeScript

Provides TypeScript AppHost scaffolding and generates typed bindings for Aspire
hosting APIs. This is tooling for writing the AppHost in TypeScript, not an
integration for hosting a JavaScript or TypeScript application.

## Getting started

Install the Aspire CLI and a supported Node.js runtime and package manager.
From your application directory, initialize a TypeScript AppHost:

```bash
aspire init --language typescript
```

The CLI scaffolds `apphost.mts` and automatically restores this code-generation
package. Do not install it with `aspire add`. When the application already has a
root `package.json`, the AppHost is created in a nested `aspire-apphost/` package.

Edit the scaffolded `apphost.mts`, which imports the generated SDK:

```typescript
import { createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

await builder.build().run();
```

## Generated bindings

The SDK in `.aspire/modules/` is generated from Aspire Type System (ATS) metadata
in `Aspire.Hosting` and the hosting integrations configured in `aspire.config.json`.
It supplies the typed API used by your AppHost and calls the .NET AppHost server
through JSON-RPC.

Run `aspire restore` from the AppHost directory to regenerate bindings without
starting the AppHost. Use `aspire run` to run it. Do not edit `.aspire/modules/`;
change your AppHost or integration references instead.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://aspire.dev/app-host/typescript-apphost/
* https://aspire.dev/reference/cli/commands/aspire-restore/
* https://aspire.dev/extensibility/multi-language-integration-authoring/

## Feedback & contributing

https://github.com/microsoft/aspire
