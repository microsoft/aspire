# Nuxt hosting integration

Use this integration to model, configure, and orchestrate a Nuxt application in an Aspire solution.

`@aspire/nuxt` is a **TypeScript-authored preview**, not a NuGet integration. It contributes typed `addNuxtApp` and `withNuxtReference` methods to the generated AppHost SDK. The package is packable but has not been published to npm.

## Getting started

### Prerequisites

Use the Aspire CLI built from this PR, Node.js 22.12 or later, and a TypeScript AppHost. Docker is required to build and run published images. Nuxt 4 is covered by the real sample; other Nuxt versions are not yet validated.

### Build and add the integration

From this directory:

```bash
aspire restore --non-interactive
npm pack
```

The package includes its compiled implementation and generated core SDK. A consumer does not need the integration's source checkout or a separately restored SDK inside `node_modules`.

From your AppHost directory, install the resulting tarball:

```bash
npm install /path/to/aspire-nuxt-0.1.0-preview.1.tgz
```

Add these entries to `aspire.config.json`, keeping the AppHost's other packages:

```json
{
  "features": {
    "experimentalHostingIntegrations": true
  },
  "packages": {
    "Aspire.Hosting.JavaScript": "",
    "@aspire/nuxt": {
      "source": "npm",
      "path": "./node_modules/@aspire/nuxt/dist/src/host.js"
    }
  }
}
```

Run `aspire restore` to discover the integration and regenerate the consumer SDK. External hosting integrations are disabled by default; the feature setting above opts this AppHost in. Registry-name resolution through `aspire add` is not implemented for npm hosts yet; the explicit installed entry-point path is required.

## Usage example

In the TypeScript AppHost:

```typescript
const api = await builder.addJavaScriptApp('api', '../api', { runScriptName: 'start' })
    .withHttpEndpoint({ env: 'PORT' });

const web = await builder.addNuxtApp('web', '../web')
    .withNuxtReference('apiBase', api.getEndpoint('http'))
    .waitFor(api);
```

Declare the corresponding key in `nuxt.config.ts`:

```typescript
export default defineNuxtConfig({
    runtimeConfig: {
        apiBase: '',
    },
    nitro: {
        preset: 'node-server',
    },
});
```

Use `useRuntimeConfig(event).apiBase` in a Nuxt server handler. `withNuxtReference` injects `NUXT_API_BASE` as a deferred endpoint reference, so localhost development addresses become deployment-native addresses when publishing. Nested camelCase paths are supported: `public.apiBase` maps to `NUXT_PUBLIC_API_BASE`. Use `public` only for values safe to expose to the browser.

No C# wrapper is included. The APIs are discovered from the TypeScript host.

## Run and publish

`addNuxtApp` uses the application's `dev` script locally, allocates an HTTP target port through `PORT`, and binds to `0.0.0.0`. Aspire owns the dev-server workload; the AppHost server owns the separate integration host. Existing JavaScript APIs remain available, including package-manager selection, environment values, health checks, and browser debugging.

Publishing runs the application's `build` script and packages Nitro's `.output` directory. The production entry point is `.output/server/index.mjs`; the production image does not run `nuxt dev` or require the full source tree. Use the Node-server Nitro preset, not a static, edge, or provider-specific preset. The integration does not generate framework files or overwrite a user Dockerfile. Runtime config keys must already exist in the Nuxt config for overrides to take effect.

Custom script names can be supplied with `addNuxtApp(name, appDirectory, { runScriptName, buildScriptName })`. Add a health check matching your application rather than assuming every Nuxt app implements a particular health route.

The complete sample is in `playground/NuxtApp`. The CI script `.github/workflows/polyglot-validation/test-nuxt-integration.sh` packs and installs this package into a separate consumer, verifies a healthy real dev server and SSR backend response, then publishes, builds, and runs both production images with the published service reference.

## Additional documentation

- [Nuxt runtime configuration](https://nuxt.com/docs/4.x/guide/going-further/runtime-config)
- [Nuxt Node.js deployment](https://nuxt.com/docs/4.x/getting-started/deployment#nodejs-server)
- [TypeScript-authored integrations](../../docs/specs/polyglot-integrations.md)

## Feedback & contributing

https://github.com/microsoft/aspire
