// Aspire TypeScript AppHost — adopt an existing azd project, then grow it up natively.
//
// Run with:     aspire run
// Publish with: aspire publish
//
// The azd project in ./azd-app is checked in unchanged. addAzdProject reads its azure.yaml, the
// selected .azure environment, and the existing infra/ folder, and projects the azd services and
// resources onto native, fully mutable Aspire resources:
//   services.store  (language: ts, host: containerapp) -> a native Aspire JavaScript app (runs via npm
//                                                          locally, containerized automatically on publish)
//   resources.cache  (db.redis)                        -> Azure Managed Redis (azd's Azure Cache for Redis successor; a Redis container locally)
//   resources.orders (db.postgres)                     -> Azure Database for PostgreSQL (a Postgres container locally)
//   services.store.uses: [cache, orders]               -> WithReference wiring (ConnectionStrings__cache/__orders)
// Locally everything runs as containers, so `aspire run` needs only a container runtime — no Azure login.

import { createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

// 1. Adopt the existing azd project as-is. Nothing in ./azd-app is rewritten.
const azd = await builder.addAzdProject('./azd-app');

// 2. Grow up: keep building natively on top of the imported model. Here we add a brand-new Aspire
//    resource the azd project never had, and make it depend on an imported one. getResource returns a
//    base resource handle from the loosely-typed import that composes with name-based operations such as
//    waitFor, so brand-new native resources and imported azd resources live in one application model.
const orders = await azd.getResource('orders');
const analytics = await builder.addRedis('analytics');
await analytics.waitFor(orders);

// build() flushes all pending operations before running.
await builder.build().run();
