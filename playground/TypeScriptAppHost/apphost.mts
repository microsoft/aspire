// Aspire TypeScript AppHost - E2E Demo with PostgreSQL and Express
// This demonstrates compute, databases, and references working together.
// Run with: aspire run
// Publish with: aspire publish

import { join } from 'node:path';
import {
    createBuilder,
    refExpr,
    EnvironmentCallbackContext,
    ExecuteCommandContext,
    InputsDialogValidationContext,
    InputType
} from './.modules/aspire.mjs';

console.log("Aspire TypeScript AppHost starting...\n");

// Create the distributed application builder
const builder = await createBuilder();

const ec = await builder.executionContext();

const isPublishMode = await ec.isPublishMode();
console.log(`isRunMode: ${await ec.isRunMode()}`);
console.log(`isPublishMode: ${isPublishMode}`);

// Add Docker Compose environment for publishing
await builder.addDockerComposeEnvironment("compose");

const dir = await builder.appHostDirectory();
console.log(`AppHost directory: ${dir}`);
const processCommandScriptPath = join(dir, "process-command-scripts", "node-process-check.js");

// Add PostgreSQL server and database
const postgres = await builder.addPostgres("postgres");
const db = await postgres.addDatabase("db");

console.log("Added PostgreSQL server with database 'db'");

// Add Express API that connects to PostgreSQL (uses npm run dev with tsx)
const api = await builder
    .addNodeApp("api", "./express-api", "src/server.ts")
    .withRunScript("dev")
    .withHttpEndpoint({ env: "PORT" })
    .withReference(db)
    .waitFor(db);

console.log("Added Express API with reference to PostgreSQL database");

// Redis
const cache = await builder.addRedis("cache");
await cache.withPersistentLifetime();
await cache.withCommand(
    "set-prefix",
    "Set prefix",
    async (context: ExecuteCommandContext) => {
        const args = await context.arguments();
        const prefix = await args.requiredValue("prefix");
        const countValue = await args.requiredValue("count");
        const count = Number(countValue);

        return {
            success: true,
            message: `Validated prefix '${prefix}' with count ${count}.`
        };
    },
    {
        commandOptions: {
            description: "Validates ordered command arguments from the CLI and Dashboard.",
            arguments: [
                {
                    name: "prefix",
                    label: "Prefix",
                    inputType: InputType.Text,
                    required: true,
                    maxLength: 20
                },
                {
                    name: "count",
                    label: "Count",
                    inputType: InputType.Number,
                    required: true
                }
            ],
            validateArguments: async (context: InputsDialogValidationContext) => {
                const args = await context.inputs();
                const prefix = await args.requiredValue("prefix");
                const countValue = await args.requiredValue("count");
                const count = Number(countValue);

                if (prefix.toLowerCase() === "bad") {
                    await context.addValidationError("prefix", "Prefix cannot be 'bad'.");
                }

                if (!Number.isFinite(count) || count < 1) {
                    await context.addValidationError("count", "Count must be greater than or equal to 1.");
                }
            }
        }
    });
await cache.withProcessCommand(
    "node-process-check",
    "Node process check",
    {
        executablePath: "node",
        arguments: [
            processCommandScriptPath,
            "from-typescript-apphost"
        ],
        environmentVariables: {
            TS_PROCESS_COMMAND_SAMPLE: "from-process-command"
        },
        standardInputContent: "hello from TypeScript AppHost",
        maxOutputLineCount: 10,
        commandOptions: {
            description: "Runs a Node process command from the TypeScript AppHost.",
            iconName: "WindowConsole"
        }
    });
await cache.withProcessCommandFactory(
    "node-process-check-factory",
    "Node process check with arguments",
    async (context: ExecuteCommandContext) => {
        const args = await context.arguments();
        const message = await args.requiredValue("message");

        return {
            executablePath: "node",
            arguments: [
                processCommandScriptPath,
                message
            ],
            environmentVariables: {
                TS_PROCESS_COMMAND_SAMPLE: "from-process-command-factory"
            },
            standardInputContent: "hello from TypeScript AppHost factory"
        };
    },
    {
        commandOptions: {
            description: "Runs a Node process command with arguments from the TypeScript AppHost.",
            iconName: "WindowConsole",
            arguments: [
                {
                    name: "message",
                    label: "Message",
                    inputType: InputType.Text,
                    required: true
                }
            ]
        },
        maxOutputLineCount: 10
    });

console.log("Added Redis cache");

// Vite frontend
await builder
    .addViteApp("frontend", "./vite-frontend")
    .withReference(api)
    .waitFor(api)
    .withEnvironment("CUSTOM_ENV", "value")
    .withEnvironmentCallback(async (ctx: EnvironmentCallbackContext) => {
        // await needed here because getEndpoint returns a value we use
        const ep = await api.getEndpoint("http");
        await ctx.environment().set("API_ENDPOINT", refExpr`${ep}`);
    });

console.log("Added Vite frontend with reference to API");

// build() flushes all pending promises before running
await builder.build().run();
