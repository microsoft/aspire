import { createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

const container = await builder.addContainer("npm-marker", "busybox");
await container.withArgs(["sh", "-c", "echo npm"]);

await builder.build().run();
