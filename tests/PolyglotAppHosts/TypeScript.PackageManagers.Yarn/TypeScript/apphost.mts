import { createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

const container = await builder.addContainer("yarn-marker", "busybox");
await container.withArgs(["sh", "-c", "echo yarn"]);

await builder.build().run();
