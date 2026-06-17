import { createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

const container = await builder.addContainer("bun-marker", "busybox");
await container.withArgs(["sh", "-c", "echo bun"]);

await builder.build().run();
