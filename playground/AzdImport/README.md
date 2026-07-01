# AzdImport playground

This sample shows how an existing **Azure Developer CLI (azd)** project can be adopted by Aspire,
**without changing the azd assets**, using the experimental `builder.addAzdProject(...)` API from
`Aspire.Hosting.Azure.Azd`.

Everything here is **TypeScript** — including the Aspire app host (`apphost.mts`) and the adopted
service. It tells the "grow up from `azure.yaml` into Aspire, lose nothing" story end to end.

## Layout

```text
AzdImport/
├── apphost.mts                    # the Aspire app host (TypeScript) that adopts azd-app
├── aspire.config.json             # selects the packages exposed to the TypeScript app host
├── package.json / tsconfig.json
└── azd-app/                       # an existing azd repo, checked in
    ├── azure.yaml                 # services: store (ts); resources: cache (db.redis), orders (db.postgres)
    ├── infra/                     # existing Bicep (preserved by reference, not regenerated)
    │   ├── main.bicep
    │   └── main.parameters.json
    ├── .azure/                    # azd environment (reused, not recreated)
    │   ├── config.json            # defaultEnvironment: dev
    │   └── dev/.env               # AZURE_SUBSCRIPTION_ID / LOCATION / RESOURCE_GROUP / ...
    └── src/store/                 # the service source code (TypeScript + Express)
        ├── package.json
        └── src/server.ts
```

## What the app host does

```typescript
import { createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

// 1. Adopt the existing azd project as-is. Nothing in ./azd-app is rewritten.
const azd = await builder.addAzdProject('./azd-app');

// 2. Grow up: add a brand-new native Aspire resource and make it depend on an imported one.
const orders = await azd.getResource('orders');
const analytics = await builder.addRedis('analytics');
await analytics.waitFor(orders);

await builder.build().run();
```

`addAzdProject` reads `azure.yaml`, the selected `.azure` environment, and the `infra/` folder, then
projects the azd model onto native Aspire resources:

| azd asset | Imported Aspire resource |
| --- | --- |
| `services.store` (`host: containerapp`, `language: ts`) | a native Aspire **JavaScript app** for `src/store` (runs via npm locally, containerized on publish) |
| `resources.cache` (`db.redis`) | `AddAzureManagedRedis("cache")` (a Redis container locally) |
| `resources.orders` (`db.postgres`) | `AddAzurePostgresFlexibleServer("orders")` (a Postgres container locally) |
| `services.store.uses: [cache, orders]` | `WithReference(...)` wiring (`ConnectionStrings__cache` / `ConnectionStrings__orders`) |
| `infra/` | preserved by reference (an informational diagnostic is emitted) |

The imported model is not frozen: you keep composing it with the normal Aspire TypeScript API. The
loosely-typed `getResource` / `getService` accessors hand back resource handles that compose with
name-based operations such as `waitFor`, so brand-new native resources and imported azd resources
live in one application model.

## Run it locally

```bash
aspire run
```

Locally the Azure resources run as containers (`db.redis` and `db.postgres`), so a container runtime
is the only prerequisite — no Azure subscription or login is needed. Open the dashboard, then browse
the `store` endpoints:

- `/cache` — increments a counter in the imported Redis cache.
- `/catalog` — runs `SELECT version()` against the imported PostgreSQL database.

## Publish / deploy

Because the importer reuses the project's `.azure` environment and preserves the existing `infra/`
folder, the same model is what you would publish. The TypeScript `store` service is containerized
automatically on publish (a Dockerfile is generated if one is not present). This playground uses
placeholder Azure IDs in `.azure/dev/.env` so it can run locally; point it at a real azd environment
to deploy.
