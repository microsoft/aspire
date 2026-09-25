import { createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

const container = await builder.addContainer("pnpm-marker", "busybox");
await container.withArgs(["sh", "-c", "echo pnpm"]);

await builder.build().run();
