import { createBuilder, ContainerLifetime } from './.modules/aspire.js';
import { addKafka } from '../packages/aspire-kafka/src/index.js';

console.log("=== TS Kafka Lib Model A Spike AppHost ===\n");

const builder = await createBuilder();

// Same Redis as the Model B spike, for parity.
const redis = await builder
    .addRedis("cache")
    .withLifetime(ContainerLifetime.Persistent);
console.log("Added Redis (from .NET integration)");

// Kafka from the in-process reusable TS integration library.
// The configure callback is a plain in-process JS closure — no external capability,
// no integration host process, no JSON-RPC callback relay. It runs in this Node
// process and the env vars it returns are applied via builder methods on the
// same client connection.
const kafka = await addKafka(builder, "events", {
    configure: async (k) => {
        console.log(`[configure callback] in-process, kafka handle: ${JSON.stringify(k)}`);
        return {
            MODEL_A_CALLBACK_MARKER: "set-from-in-process-callback",
            MODEL_A_CALLBACK_AT: new Date().toISOString(),
        };
    },
});
console.log(`Added Kafka (from in-process TS integration library): ${JSON.stringify(kafka)}`);

const app = await builder.build();
console.log("\n=== Build succeeded! Starting distributed application... ===");

await app.run();
