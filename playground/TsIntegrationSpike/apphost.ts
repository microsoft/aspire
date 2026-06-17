import { createBuilder, ContainerLifetime } from './.modules/aspire.js';

console.log("=== TS Integration Spike AppHost ===\n");

const builder = await createBuilder();

// This comes from .NET (Aspire.Hosting.Redis) -- works today
const redis = await builder
    .addRedis("cache")
    .withLifetime(ContainerLifetime.Persistent);

console.log("Added Redis (from .NET integration)");

const kafka = await builder.addKafka("events", {
    configure: async (k) => {
        console.log(`[configure callback] running in guest, kafka handle: ${JSON.stringify(k)}`);
        return {
            SPIKE_CALLBACK_MARKER: "set-from-guest-callback",
            SPIKE_CALLBACK_AT: new Date().toISOString(),
        };
    },
});
console.log(`Added Kafka (from Node.js integration): ${JSON.stringify(kafka)}`);

const deno = await builder
    .addDenoApp("deno-api", "./deno-api", "main.ts")
    .withDenoPermissions(["net", "env"])
    .withDenoTask("serve")
    .withDenoBuildTask("check")
    .withDenoDockerfileBaseImage({ runtimeImage: "denoland/deno:alpine-2.5.6" })
    .publishAsDenoDockerFile({ port: 8000, cache: true })
    .withHttpEndpoint({ env: "PORT" });

console.log(`Added Deno API (from TypeScript integration): ${JSON.stringify(deno)}`);

const app = await builder.build();
console.log("\n=== Build succeeded! Starting distributed application... ===");

await app.run();
